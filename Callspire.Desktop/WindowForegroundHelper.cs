#if WINDOWS
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace Softphone
{
    /// <summary>
    /// Brings a WPF window to the foreground. Browsers that hand off to <c>callspire://</c> often
    /// only flash the taskbar; Windows may not treat <see cref="Window.Activate"/> as enough.
    /// </summary>
    internal static class WindowForegroundHelper
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SwRestore = 9;

        public static void RequestUserAttention(Window? window)
        {
            if (window == null) return;
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(() => RequestUserAttention(window)));
                return;
            }

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Show();
            var wasTop = window.Topmost;
            window.Topmost = true;
            window.Activate();
            try { window.Focus(); } catch { }

            // Best-effort: take focus on the actual HWND (helps when the taskbar only "blinks")
            try
            {
                var helper = new WindowInteropHelper(window);
                if (helper.Handle != IntPtr.Zero)
                {
                    ShowWindow(helper.Handle, SwRestore);
                    SetForegroundWindow(helper.Handle);
                }
            }
            catch { }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    await window.Dispatcher.InvokeAsync(() =>
                    {
                        try { window.Topmost = wasTop; } catch { }
                    });
                }
                catch { }
            });
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
