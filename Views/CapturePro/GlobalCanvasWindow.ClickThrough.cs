using System;
using System.Windows;
using System.Windows.Input;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的「点击穿透」（照上游 <c>toggle()</c> / <c>updateToolbarOpacity</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游给整窗设 <c>WA_TransparentForMouseEvents</c>，工具条是**另一个窗口**所以还能点。
/// 本项目的工具条与画布同窗，于是穿透靠窗口自己的 <c>WS_EX_TRANSPARENT</c>（分层窗口上
/// 这一位才是「鼠标全透过去」），**指针停在工具条上时把这一位摘掉**——那样工具条照常可点。
/// </para>
/// <para>
/// 别改回 <c>WM_NCHITTEST</c> 返回 <c>HTTRANSPARENT</c>：那只在**同一线程**的窗口之间放行，
/// 落到用户真正要点的那个程序（另一个进程）上一点都不透，表现就是「鼠标穿透不可用」。
/// </para>
/// <para>
/// 穿透期间补 <c>WS_EX_NOACTIVATE</c>（点画布不会把前台抢回本程序），并把工具条压到
/// 0.32 不透明度；指针回到工具条上恢复 1.0。指针位置按 <see cref="GlobalCanvasLayout.ClickThroughPollMs"/>
/// 轮询——穿透期间它是「这一拍要不要吃鼠标」的唯一依据。激光笔不受影响，穿透时照样看得见。
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
        // 穿透时画布不再接手势；光标恢复成箭头
        Surface.Cursor = Cursors.Arrow;
        UpdateCanvasCursor();

        Toolbar.SetClickThroughState(_clickThrough);
        SyncPollInterval();
        PollClickThroughState();

        if (!_clickThrough)
        {
            // 切回来要能立刻用键盘（工具字母键 / Ctrl+Z）
            Activate();
            Surface.Focus();
        }
    }

    /// <summary>
    /// 一拍轮询：工具条不透明度 + 窗口要不要吃鼠标。穿透期间每
    /// <see cref="GlobalCanvasLayout.ClickThroughPollMs"/> 一次，其余时候只为不透明度（50ms）。
    /// </summary>
    private void PollClickThroughState()
    {
        // 不在穿透时不必问指针位置：不透明度恒为 1
        bool onToolbar = _clickThrough && PointerOnToolbar();

        double target = GlobalCanvasLayout.ToolbarOpacity(_clickThrough, onToolbar);
        if (Math.Abs(Toolbar.Opacity - target) > 0.01) Toolbar.Opacity = target;

        ApplyMouseTransparency(GlobalCanvasLayout.MouseTransparent(_clickThrough, onToolbar));
    }

    /// <summary>按需要摘掉 / 挂上 <c>WS_EX_TRANSPARENT</c>（穿透期间还带着 <c>WS_EX_NOACTIVATE</c>）。</summary>
    private void ApplyMouseTransparency(bool transparent)
    {
        if (_hwnd == IntPtr.Zero) return;

        // 正拖着工具条时不摘：鼠标已在本窗口捕获，中途来回摘挂会让拖动发飘
        if (Toolbar.IsMouseCaptureWithin) transparent = false;

        long style = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
        long wanted = _clickThrough
            ? style | Win32.WsExNoactivate
            : style & ~Win32.WsExNoactivate;
        wanted = transparent
            ? wanted | Win32.WsExTransparent | Win32.WsExLayered
            : wanted & ~Win32.WsExTransparent;

        if (wanted != style) Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, new IntPtr(wanted));
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
}
