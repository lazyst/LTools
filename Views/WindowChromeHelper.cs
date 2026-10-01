using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LTools.Views;

/// <summary>WindowChrome 辅助：修正最大化时窗口内容溢出屏幕（WM_GETMINMAXINFO 钳制到当前显示器工作区）。</summary>
internal static class WindowChromeHelper
{
    public static void FixMaximize(Window window)
    {
        // SourceInitialized 时窗口 HWND 已创建，此时挂钩最稳妥
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            HookNow(window);
        else
            window.SourceInitialized += (_, _) => HookNow(window);
    }

    private static void HookNow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var src = HwndSource.FromHwnd(hwnd);
        src?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref info))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.ptMaxPosition = new POINT { X = info.rcWork.Left - info.rcMonitor.Left, Y = info.rcWork.Top - info.rcMonitor.Top };
                mmi.ptMaxSize = new POINT
                {
                    X = info.rcWork.Right - info.rcWork.Left,
                    Y = info.rcWork.Bottom - info.rcWork.Top,
                };
                mmi.ptMaxTrackSize = mmi.ptMaxSize;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        return IntPtr.Zero;
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
}
