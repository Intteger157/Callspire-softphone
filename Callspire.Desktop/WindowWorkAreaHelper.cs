#if WINDOWS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Softphone
{
    /// <summary>
    /// Keeps WPF windows inside the monitor work area (taskbar-aware) on high-DPI displays.
    /// </summary>
    internal static class WindowWorkAreaHelper
    {
        private static readonly HashSet<Window> _attached = new();
        private static readonly HashSet<Window> _clamping = new();
        private static readonly Dictionary<Window, DispatcherTimer> _timers = new();
        private static readonly Dictionary<Window, bool> _blockMaximize = new();
        private static bool _displayHandlerRegistered;

        public static void Attach(Window window, bool blockMaximize = false)
        {
            if (window == null || !_attached.Add(window)) return;

            _blockMaximize[window] = blockMaximize;
            window.SourceInitialized += OnSourceInitialized;
            window.LocationChanged += OnLocationChanged;
            window.Closed += OnClosed;

            RegisterDisplayHandler();
        }

        public static Rect GetWorkArea(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

                const int monitorDefaultToNearest = 0x00000002;
                IntPtr monitor = MonitorFromWindow(hwnd, monitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return SystemParameters.WorkArea;

                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(monitor, ref mi)) return SystemParameters.WorkArea;

                return ConvertScreenPxRectToDip(
                    window,
                    mi.rcWork.left,
                    mi.rcWork.top,
                    mi.rcWork.right - mi.rcWork.left,
                    mi.rcWork.bottom - mi.rcWork.top);
            }
            catch
            {
                return SystemParameters.WorkArea;
            }
        }

        public static void EnsureWithinWorkArea(Window window, bool centerOnFirstFit = false)
        {
            if (window == null || _clamping.Contains(window)) return;

            try
            {
                _clamping.Add(window);

                var work = GetWorkArea(window);
                if (work.Width <= 0 || work.Height <= 0) return;

                RelaxMinimumSizeIfNeeded(window, work);

                double width = ResolveDimension(window.Width, window.ActualWidth, window.MinWidth);
                double height = ResolveDimension(window.Height, window.ActualHeight, window.MinHeight);

                if (width > work.Width) width = work.Width;
                if (height > work.Height) height = work.Height;

                width = Math.Max(window.MinWidth, width);
                height = Math.Max(window.MinHeight, height);

                window.Width = width;
                window.Height = height;

                double left = centerOnFirstFit
                    ? work.Left + (work.Width - width) / 2
                    : window.Left;
                double top = centerOnFirstFit
                    ? work.Top + (work.Height - height) / 2
                    : window.Top;

                left = Math.Max(work.Left, Math.Min(left, work.Right - width));
                top = Math.Max(work.Top, Math.Min(top, work.Bottom - height));

                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = left;
                window.Top = top;
            }
            catch { }
            finally
            {
                _clamping.Remove(window);
            }
        }

        /// <summary>
        /// Centers a child over a host window and clamps to the host monitor work area.
        /// </summary>
        public static void CenterOverHost(Window child, Window host)
        {
            try
            {
                if (child == null || host == null) return;

                child.WindowStartupLocation = WindowStartupLocation.Manual;

                var work = GetWorkArea(host);
                RelaxMinimumSizeIfNeeded(child, work);

                double width = ResolveDimension(child.Width, child.ActualWidth, child.MinWidth);
                double height = ResolveDimension(child.Height, child.ActualHeight, child.MinHeight);

                if (width > work.Width) width = work.Width;
                if (height > work.Height) height = work.Height;

                var hostBounds = host.WindowState == WindowState.Maximized
                    ? host.RestoreBounds
                    : new Rect(host.Left, host.Top, host.ActualWidth, host.ActualHeight);
                if (hostBounds.Width <= 0 || hostBounds.Height <= 0)
                    hostBounds = host.RestoreBounds;

                double left = hostBounds.Left + (hostBounds.Width - width) / 2;
                double top = hostBounds.Top + (hostBounds.Height - height) / 2;

                if (work.Width > 0 && work.Height > 0)
                {
                    left = Math.Max(work.Left, Math.Min(left, work.Right - width));
                    top = Math.Max(work.Top, Math.Min(top, work.Bottom - height));
                }

                child.Width = width;
                child.Height = height;
                child.Left = left;
                child.Top = top;
            }
            catch { }
        }

        private static void RelaxMinimumSizeIfNeeded(Window window, Rect work)
        {
            if (work.Width > 0 && window.MinWidth > work.Width)
                window.MinWidth = work.Width;
            if (work.Height > 0 && window.MinHeight > work.Height)
                window.MinHeight = work.Height;
        }

        private static double ResolveDimension(double value, double actual, double fallback)
        {
            if (!double.IsNaN(value) && value > 0) return value;
            if (actual > 0) return actual;
            return fallback;
        }

        private static void OnSourceInitialized(object? sender, EventArgs e)
        {
            if (sender is not Window window) return;

            try
            {
                bool centerOnFirstFit = window.WindowStartupLocation == WindowStartupLocation.CenterScreen;
                EnsureWithinWorkArea(window, centerOnFirstFit: centerOnFirstFit);

                var hwnd = new WindowInteropHelper(window).Handle;
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
            catch { }
        }

        private static void OnLocationChanged(object? sender, EventArgs e)
        {
            if (sender is Window window)
                ScheduleClamp(window);
        }

        private static void OnClosed(object? sender, EventArgs e)
        {
            if (sender is not Window window) return;

            _attached.Remove(window);
            _blockMaximize.Remove(window);
            _clamping.Remove(window);

            if (_timers.TryGetValue(window, out var timer))
            {
                timer.Stop();
                _timers.Remove(window);
            }
        }

        private static void RegisterDisplayHandler()
        {
            if (_displayHandlerRegistered) return;
            _displayHandlerRegistered = true;

            try
            {
                SystemEvents.DisplaySettingsChanged += (_, __) =>
                {
                    foreach (var window in _attached.ToArray())
                    {
                        try
                        {
                            window.Dispatcher.BeginInvoke(
                                () => EnsureWithinWorkArea(window, centerOnFirstFit: false),
                                DispatcherPriority.ApplicationIdle);
                        }
                        catch { }
                    }
                };
            }
            catch { }
        }

        private static void ScheduleClamp(Window window)
        {
            try
            {
                if (_clamping.Contains(window)) return;

                if (!_timers.TryGetValue(window, out var timer))
                {
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                    timer.Tick += (_, __) =>
                    {
                        timer.Stop();
                        EnsureWithinWorkArea(window, centerOnFirstFit: false);
                    };
                    _timers[window] = timer;
                }

                timer.Stop();
                timer.Start();
            }
            catch { }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (HwndSource.FromHwnd(hwnd)?.RootVisual is not Window window)
                return IntPtr.Zero;

            const int wmSysCommand = 0x0112;
            const int scMaximize = 0xF030;
            const int wmGetMinMaxInfo = 0x0024;
            const int wmDpiChanged = 0x02E0;
            const int wmDisplayChange = 0x007E;

            if (msg == wmSysCommand && _blockMaximize.TryGetValue(window, out bool block) && block)
            {
                try
                {
                    int cmd = (int)(wParam.ToInt64() & 0xFFF0);
                    if (cmd == scMaximize)
                    {
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
                catch { }
            }

            if (msg == wmGetMinMaxInfo)
            {
                try
                {
                    WmGetMinMaxInfo(window, hwnd, lParam);
                    handled = true;
                }
                catch { }
            }

            if (msg == wmDpiChanged || msg == wmDisplayChange)
            {
                try
                {
                    window.Dispatcher.BeginInvoke(
                        () => EnsureWithinWorkArea(window, centerOnFirstFit: false),
                        DispatcherPriority.ApplicationIdle);
                }
                catch { }
            }

            return IntPtr.Zero;
        }

        private static Rect ConvertScreenPxRectToDip(Window window, int leftPx, int topPx, int widthPx, int heightPx)
        {
            try
            {
                var src = PresentationSource.FromVisual(window);
                if (src?.CompositionTarget != null)
                {
                    var m = src.CompositionTarget.TransformFromDevice;
                    var tl = m.Transform(new Point(leftPx, topPx));
                    var br = m.Transform(new Point(leftPx + widthPx, topPx + heightPx));
                    return new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
                }
            }
            catch { }

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 96 && dpi <= 480)
                    {
                        double scale = 96.0 / dpi;
                        return new Rect(leftPx * scale, topPx * scale, widthPx * scale, heightPx * scale);
                    }
                }
            }
            catch { }

            return new Rect(leftPx, topPx, widthPx, heightPx);
        }

        private static void WmGetMinMaxInfo(Window window, IntPtr hwnd, IntPtr lParam)
        {
            const int monitorDefaultToNearest = 0x00000002;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, monitorDefaultToNearest);

            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    RECT workArea = monitorInfo.rcWork;
                    RECT monitorArea = monitorInfo.rcMonitor;

                    mmi.ptMaxPosition.X = workArea.left - monitorArea.left;
                    mmi.ptMaxPosition.Y = workArea.top - monitorArea.top;
                    mmi.ptMaxSize.X = workArea.right - workArea.left;
                    mmi.ptMaxSize.Y = workArea.bottom - workArea.top;
                    mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                    mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                }
            }

            try
            {
                uint dpi = 96;
                try
                {
                    var d = GetDpiForWindow(hwnd);
                    if (d >= 96 && d <= 480) dpi = d;
                }
                catch { }

                double scale = dpi / 96.0;
                int minWpx = (int)Math.Ceiling(window.MinWidth * scale);
                int minHpx = (int)Math.Ceiling(window.MinHeight * scale);

                mmi.ptMinTrackSize.X = Math.Max(mmi.ptMinTrackSize.X, minWpx);
                mmi.ptMinTrackSize.Y = Math.Max(mmi.ptMinTrackSize.Y, minHpx);
            }
            catch { }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr handle, int flags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
#endif
