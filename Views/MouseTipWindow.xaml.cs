using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using LTools.Native;

namespace LTools.Views;

/// <summary>跟随鼠标右下角的轻量提示文本框（替代系统托盘气球）。
/// 无边框置顶 + 半透明圆角深色背景；按光标位置自动避开屏幕边缘。</summary>
public partial class MouseTipWindow : Window
{
    public MouseTipWindow()
    {
        InitializeComponent();
        ShowActivated = false;
    }

    /// <summary>设置文本（支持 \n 换行）。</summary>
    public void SetText(string text) => TipText.Text = text;

    /// <summary>定位到光标右下角，避开屏幕右下边缘。
    /// 注意 DPI：GetCursorPos 返回物理像素，而 Window.Left/Top 是 DIP（逻辑像素），
    /// 需用 TransformFromDevice 换算，否则在缩放 >100% 的显示器上会偏右偏下。</summary>
    public void PlaceNearCursor()
    {
        if (!Win32.GetCursorPos(out var pt)) return;

        // 物理→DIP 换算因子（TransformFromDevice.M11 = 1/scaleX）
        double m11 = 1.0, m22 = 1.0;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            m11 = tfd.M11; m22 = tfd.M22;
        }

        double cx = pt.X * m11; // 光标 DIP
        double cy = pt.Y * m22;
        double w = ActualWidth > 0 ? ActualWidth : 200; // DIP
        double h = ActualHeight > 0 ? ActualHeight : 32;

        const double gap = 16; // DIP 间距
        double x = cx + gap;
        double y = cy + gap;

        // 屏幕工作区避让（多显示器：取光标所在显示器，rcWork 是物理像素→转 DIP）
        var screen = GetScreenBounds(pt.X, pt.Y);
        double sLeft = screen.Left * m11, sTop = screen.Top * m22;
        double sRight = screen.Right * m11, sBottom = screen.Bottom * m22;
        if (x + w > sRight - 8) x = cx - w - gap; // 右溢出→放左侧
        if (x < sLeft + 8) x = sLeft + 8;
        if (y + h > sBottom - 8) y = cy - h - gap; // 下溢出→放上方
        if (y < sTop + 8) y = sTop + 8;

        Left = x;
        Top = y;
    }

    private static (double Left, double Top, double Right, double Bottom) GetScreenBounds(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { x = x, y = y }, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi))
            return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        // 回退主屏幕
        var wa = SystemParameters.WorkArea;
        return (wa.Left, wa.Top, wa.Right, wa.Bottom);
    }

    private struct POINT { public int x; public int y; }
    private struct RECT { public int left; public int top; public int right; public int bottom; }
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
