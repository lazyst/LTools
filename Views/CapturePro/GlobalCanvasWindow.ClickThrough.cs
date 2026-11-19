using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的「点击穿透」（照上游 <c>toggle()</c> / <c>updateToolbarOpacity</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游给整窗设 <c>WA_TransparentForMouseEvents</c>，工具条是**另一个窗口**所以还能点。
/// 本项目工具条与画布同窗，所以改用 <c>WM_NCHITTEST</c>：穿透期间工具条以外一律返回
/// <c>HTTRANSPARENT</c>，鼠标事件落到下面的程序；工具条自身照常可点。
/// </para>
/// <para>
/// 穿透期间补 <c>WS_EX_NOACTIVATE</c>（点画布不会把前台抢回本程序），并把工具条压到
/// 0.32 不透明度；指针回到工具条上恢复 1.0（50ms 轮询，指针在别的程序里也能感知）。
/// 激光笔不受影响，穿透时照样看得见。
/// </para>
/// </remarks>
public partial class GlobalCanvasWindow
{
    private bool _clickThrough;

    /// <summary>切成 / 切回点击穿透（热键、工具条按钮、会话入口都走这里）。</summary>
    internal void ToggleClickThrough()
    {
        if (_closed) return;

        CommitText();
        CancelGesture();
        EndPicker();

        _clickThrough = !_clickThrough;
        ApplyClickThrough();
    }

    private void ApplyClickThrough()
    {
        if (_hwnd != IntPtr.Zero)
        {
            long style = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
            style = _clickThrough ? style | Win32.WsExNoactivate : style & ~Win32.WsExNoactivate;
            Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, new IntPtr(style));
        }

        // 穿透时画布不再接手势；光标恢复成箭头
        Surface.Cursor = Cursors.Arrow;
        UpdateCanvasCursor();

        Toolbar.SetClickThroughState(_clickThrough);
        UpdateToolbarOpacity();

        if (!_clickThrough)
        {
            // 切回来要能立刻用键盘（工具字母键 / Ctrl+Z）
            Activate();
            Surface.Focus();
        }
    }

    /// <summary>穿透时工具条的不透明度：指针停在工具条上 1.0，否则 0.32。</summary>
    private void UpdateToolbarOpacity()
    {
        double target = GlobalCanvasLayout.ToolbarOpacity(_clickThrough, PointerOnToolbar());
        if (Math.Abs(Toolbar.Opacity - target) > 0.01) Toolbar.Opacity = target;
    }

    /// <summary>指针（物理像素）是否落在工具条矩形里。</summary>
    private bool PointerOnToolbar()
    {
        if (Toolbar.Visibility != Visibility.Visible) return false;
        if (!Win32.GetCursorPos(out var pt)) return false;

        var (x0, y0, x1, y1) = ToolbarScreenBounds();
        return pt.X >= x0 && pt.X <= x1 && pt.Y >= y0 && pt.Y <= y1;
    }

    /// <summary>工具条在屏幕物理像素里的矩形（窗口贴在这块显示器左上角，没有额外偏移）。</summary>
    private (double X0, double Y0, double X1, double Y1) ToolbarScreenBounds()
    {
        double left = System.Windows.Controls.Canvas.GetLeft(Toolbar);
        double top = System.Windows.Controls.Canvas.GetTop(Toolbar);
        if (double.IsNaN(left)) left = 0;
        if (double.IsNaN(top)) top = 0;

        double x0 = _monitor.Left + left * _sx;
        double y0 = _monitor.Top + top * _sy;
        double x1 = x0 + Math.Max(0, Toolbar.ActualWidth) * _sx;
        double y1 = y0 + Math.Max(0, Toolbar.ActualHeight) * _sy;
        return (x0, y0, x1, y1);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 穿透期间：工具条以外全部放行（HTTRANSPARENT 让系统把消息发给下面的窗口）
        if (msg == WmNchittest && _clickThrough)
        {
            long packed = lParam.ToInt64();
            int x = (short)(packed & 0xFFFF);
            int y = (short)((packed >> 16) & 0xFFFF);

            var (x0, y0, x1, y1) = ToolbarScreenBounds();
            bool onToolbar = x >= x0 && x <= x1 && y >= y0 && y <= y1;
            if (!onToolbar)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }
        }

        return IntPtr.Zero;
    }
}
