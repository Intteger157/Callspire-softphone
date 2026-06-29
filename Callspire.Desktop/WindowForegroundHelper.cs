#if WINDOWS

using System;

using System.Collections.Generic;

using System.Linq;

using System.Runtime.InteropServices;

using System.Threading;

using System.Threading.Tasks;

using System.Windows;

using System.Windows.Interop;



namespace Softphone

{

    /// <summary>

    /// Brings WPF windows to the foreground. Browsers that hand off to <c>callspire://</c> often

    /// keep focus; Windows foreground lock blocks a plain <see cref="Window.Activate"/>.

    /// </summary>

    internal static class WindowForegroundHelper

    {

        [DllImport("user32.dll")]

        private static extern bool SetForegroundWindow(IntPtr hWnd);



        [DllImport("user32.dll")]

        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);



        [DllImport("user32.dll")]

        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);



        [DllImport("user32.dll")]

        private static extern IntPtr GetForegroundWindow();



        [DllImport("user32.dll")]

        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);



        [DllImport("user32.dll")]

        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);



        [DllImport("kernel32.dll")]

        private static extern uint GetCurrentThreadId();



        [DllImport("user32.dll")]

        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);



        private static readonly IntPtr HwndTopmost = new(-1);

        private static readonly IntPtr HwndNotTopmost = new(-2);



        private const int SwRestore = 9;

        private const int SwShow = 5;

        private const uint SwpNomove = 0x0002;

        private const uint SwpNosize = 0x0001;

        private const uint SwpShowwindow = 0x0040;

        private const uint FlashwAll = 0x00000003;

        private const uint FlashwTimerNoFg = 0x0000000C;



        private static int _elevatedSessionDepth;



        private sealed class ElevatedSession

        {

            public Window Window { get; init; } = null!;

            public bool WasTopmost { get; init; }

        }



        private static readonly Stack<ElevatedSession> _elevatedSessions = new();



        public static bool IsElevatedForegroundActive => Volatile.Read(ref _elevatedSessionDepth) > 0;



        [StructLayout(LayoutKind.Sequential)]

        private struct FLASHWINFO

        {

            public uint cbSize;

            public IntPtr hwnd;

            public uint dwFlags;

            public uint uCount;

            public uint dwTimeout;

        }



        /// <summary>One-shot attention (taskbar flash + brief topmost). Used when forwarding protocol URLs.</summary>

        public static void RequestUserAttention(Window? window)

        {

            if (window == null) return;

            if (!window.Dispatcher.CheckAccess())

            {

                window.Dispatcher.BeginInvoke(new Action(() => RequestUserAttention(window)));

                return;

            }



            if (IsElevatedForegroundActive)

            {

                PrepareVisible(window, show: true);

                ForceBringToFront(window);

                return;

            }



            PrepareVisible(window, show: true);

            var wasTop = window.Topmost;

            window.Topmost = true;

            ForceBringToFront(window);

            try { window.Activate(); } catch { }

            try { window.Focus(); } catch { }



            _ = Task.Run(async () =>

            {

                try

                {

                    await Task.Delay(500).ConfigureAwait(false);

                    if (IsElevatedForegroundActive)

                        return;



                    await window.Dispatcher.InvokeAsync(() =>

                    {

                        try

                        {

                            if (!IsElevatedForegroundActive)

                            {

                                window.Topmost = wasTop;

                                ApplyNativeTopmost(window, wasTop);

                            }

                        }

                        catch { }

                    });

                }

                catch { }

            });

        }



        /// <summary>

        /// Keeps the anchor window topmost for the browser click-to-call flow

        /// (connection picker and call window opening).

        /// </summary>

        public static void BeginElevatedForegroundSession(Window? anchor)

        {

            Interlocked.Increment(ref _elevatedSessionDepth);

            if (anchor == null) return;



            if (!anchor.Dispatcher.CheckAccess())

            {

                anchor.Dispatcher.Invoke(() => BeginElevatedForegroundSession(anchor));

                return;

            }



            _elevatedSessions.Push(new ElevatedSession

            {

                Window = anchor,

                WasTopmost = anchor.Topmost

            });



            PrepareVisible(anchor, show: true);

            anchor.Topmost = true;

            ApplyNativeTopmost(anchor, topmost: true);

            ForceBringToFront(anchor);

        }



        public static void EndElevatedForegroundSession(Window? anchor)

            => CompleteElevatedForegroundSession(anchor);



        /// <summary>Ends the elevated session once; safe to call multiple times for the same anchor.</summary>

        public static void CompleteElevatedForegroundSession(Window? anchor)

        {

            if (Volatile.Read(ref _elevatedSessionDepth) <= 0)

                return;



            if (anchor != null && _elevatedSessions.Count > 0 && !ReferenceEquals(_elevatedSessions.Peek().Window, anchor))

                return;



            var depth = Interlocked.Decrement(ref _elevatedSessionDepth);

            if (depth < 0)

                Interlocked.Exchange(ref _elevatedSessionDepth, 0);



            if (anchor == null) return;



            if (!anchor.Dispatcher.CheckAccess())

            {

                anchor.Dispatcher.Invoke(() => CompleteElevatedForegroundSession(anchor));

                return;

            }



            bool wasTopmost = false;

            if (_elevatedSessions.Count > 0 && ReferenceEquals(_elevatedSessions.Peek().Window, anchor))

                wasTopmost = _elevatedSessions.Pop().WasTopmost;

            else if (_elevatedSessions.Count > 0)

                wasTopmost = _elevatedSessions.Peek().WasTopmost;



            if (Volatile.Read(ref _elevatedSessionDepth) <= 0)

                RestoreWindowTopmost(anchor, wasTopmost);

        }



        /// <summary>Hard reset if Topmost got stuck after an error during click-to-call.</summary>

        public static void ForceReleaseElevatedForeground(Window? anchor)

        {

            Interlocked.Exchange(ref _elevatedSessionDepth, 0);

            _elevatedSessions.Clear();



            if (anchor == null) return;



            if (!anchor.Dispatcher.CheckAccess())

            {

                anchor.Dispatcher.Invoke(() => ForceReleaseElevatedForeground(anchor));

                return;

            }



            RestoreWindowTopmost(anchor, wasTopmost: false);

        }



        /// <summary>

        /// Shows a modal dialog above other apps (browser during AmoCRM click-to-call).

        /// </summary>

        public static bool? ShowDialogAboveAll(Window dialog, Window? owner = null)

        {

            if (dialog == null) throw new ArgumentNullException(nameof(dialog));

            if (!dialog.Dispatcher.CheckAccess())

                return dialog.Dispatcher.Invoke(() => ShowDialogAboveAll(dialog, owner));



            if (owner != null)

            {

                try

                {

                    if (owner.IsLoaded)

                        dialog.Owner = owner;

                }

                catch { }

            }



            // ShowDialog() requires a hidden window — never call Show() first.

            PrepareForModalDialog(dialog);

            dialog.Topmost = true;



            void OnDialogSourceInitialized(object? s, EventArgs e)

            {

                dialog.SourceInitialized -= OnDialogSourceInitialized;

                ApplyNativeTopmost(dialog, topmost: true);

                ForceBringToFront(dialog);

            }



            void OnDialogLoaded(object? s, RoutedEventArgs e)

            {

                dialog.Loaded -= OnDialogLoaded;

                ForceBringToFront(dialog);

                ToggleTopmost(dialog);

            }



            void OnDialogContentRendered(object? s, EventArgs e)

            {

                dialog.ContentRendered -= OnDialogContentRendered;

                ForceBringToFront(dialog);

            }



            void OnDialogActivated(object? s, EventArgs e) => ForceBringToFront(dialog);



            dialog.SourceInitialized += OnDialogSourceInitialized;

            dialog.Loaded += OnDialogLoaded;

            dialog.ContentRendered += OnDialogContentRendered;

            dialog.Activated += OnDialogActivated;



            try

            {

                return dialog.ShowDialog();

            }

            finally

            {

                dialog.SourceInitialized -= OnDialogSourceInitialized;

                dialog.Loaded -= OnDialogLoaded;

                dialog.ContentRendered -= OnDialogContentRendered;

                dialog.Activated -= OnDialogActivated;



                dialog.Topmost = false;

                ApplyNativeTopmost(dialog, topmost: false);

            }

        }



        /// <summary>Shows a non-modal window above other apps.</summary>

        public static void ShowAboveAll(Window window, Window? owner = null)

        {

            if (window == null) throw new ArgumentNullException(nameof(window));

            if (!window.Dispatcher.CheckAccess())

            {

                window.Dispatcher.Invoke(() => ShowAboveAll(window, owner));

                return;

            }



            if (owner != null)

            {

                try

                {

                    if (owner.IsLoaded)

                        window.Owner = owner;

                }

                catch { }

            }



            PrepareVisible(window, show: true);

            window.Topmost = true;

            ApplyNativeTopmost(window, topmost: true);



            void OnLoaded(object? s, RoutedEventArgs e)

            {

                window.Loaded -= OnLoaded;

                ForceBringToFront(window);

                ToggleTopmost(window);

            }



            window.Loaded += OnLoaded;

            window.Show();

            ForceBringToFront(window);

        }



        private static void PrepareVisible(Window window, bool show)

        {

            if (window.WindowState == WindowState.Minimized)

                window.WindowState = WindowState.Normal;



            window.ShowInTaskbar = true;

            window.ShowActivated = true;

            if (show && !window.IsVisible)

                window.Show();

        }



        private static void PrepareForModalDialog(Window dialog)

        {

            if (dialog.WindowState == WindowState.Minimized)

                dialog.WindowState = WindowState.Normal;



            dialog.ShowInTaskbar = true;

            dialog.ShowActivated = true;

        }



        private static void RestoreWindowTopmost(Window window, bool wasTopmost)

        {

            try

            {

                window.Topmost = wasTopmost;

                ApplyNativeTopmost(window, wasTopmost);

            }

            catch { }

        }



        private static void ToggleTopmost(Window window)

        {

            try

            {

                window.Topmost = false;

                window.Topmost = true;

                ApplyNativeTopmost(window, topmost: true);

            }

            catch { }

        }



        private static void ApplyNativeTopmost(Window window, bool topmost)

        {

            try

            {

                var helper = new WindowInteropHelper(window);

                if (helper.Handle == IntPtr.Zero)

                    return;



                SetWindowPos(

                    helper.Handle,

                    topmost ? HwndTopmost : HwndNotTopmost,

                    0, 0, 0, 0,

                    SwpNomove | SwpNosize | SwpShowwindow);

            }

            catch { }

        }



        private static void ForceBringToFront(Window window)

        {

            try

            {

                var helper = new WindowInteropHelper(window);

                if (helper.Handle == IntPtr.Zero)

                    return;



                var hwnd = helper.Handle;

                ShowWindow(hwnd, window.WindowState == WindowState.Minimized ? SwRestore : SwShow);



                uint targetThread = GetWindowThreadProcessId(hwnd, IntPtr.Zero);

                uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);

                uint currentThread = GetCurrentThreadId();

                bool attachedToForeground = false;

                bool attachedToTarget = false;



                try

                {

                    if (foregroundThread != 0 && foregroundThread != currentThread)

                        attachedToForeground = AttachThreadInput(currentThread, foregroundThread, true);

                    if (targetThread != 0 && targetThread != currentThread)

                        attachedToTarget = AttachThreadInput(currentThread, targetThread, true);



                    SetForegroundWindow(hwnd);

                }

                finally

                {

                    if (attachedToTarget)

                        AttachThreadInput(currentThread, targetThread, false);

                    if (attachedToForeground)

                        AttachThreadInput(currentThread, foregroundThread, false);

                }



                ApplyNativeTopmost(window, window.Topmost);



                try { window.Activate(); } catch { }

                try { window.Focus(); } catch { }



                FlashTaskbar(hwnd);

            }

            catch { }

        }



        private static void FlashTaskbar(IntPtr hwnd)

        {

            try

            {

                var info = new FLASHWINFO

                {

                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),

                    hwnd = hwnd,

                    dwFlags = FlashwAll | FlashwTimerNoFg,

                    uCount = 3,

                    dwTimeout = 0

                };

                FlashWindowEx(ref info);

            }

            catch { }

        }



        /// <summary>Settings (if open) is what the user was using to start auth; otherwise the main window.</summary>

        public static void RequestForCdrAuthCallback()

        {

            if (Application.Current == null) return;

            if (!Application.Current.Dispatcher.CheckAccess())

            {

                Application.Current.Dispatcher.BeginInvoke(new Action(RequestForCdrAuthCallback));

                return;

            }



            var sw = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();

            if (sw != null)

            {

                RequestUserAttention(sw);

                return;

            }



            if (Application.Current.MainWindow is { } main)

                RequestUserAttention(main);

        }

    }

}



#endif // WINDOWS

