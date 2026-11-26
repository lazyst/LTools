using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏区域边框：告诉你正在录哪一块（照 Snow Shot 的做法）。
/// </summary>
/// <remarks>
/// <para>
/// 三种颜色与上游一致：录制中红 <c>#F5222D</c>、暂停中琥珀 <c>#FAAD14</c>、待机蓝 <c>#4096FF</c>。
/// 边框画在选区**外面**（2 物理像素 + 1 像素透明间隔）：编码器补齐奇数尺寸时多出来的那 1 像素
/// 落在间隔里，不会把边框撕进成品。
/// </para>
/// <para>
/// 三条必须守：
/// ① **点击穿透 + 不抢焦点**（<c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
///    录制时用户还要正常操作被录的程序，边框挡住鼠标就等于把那一圈变砖；
/// ② **自己不入镜**（<see cref="Win32.WdaExcludeFromCapture"/>），否则成品四周会挂一圈红边；
/// ③ 中间必须是**真的空**：窗口区域用 <c>SetWindowRgn</c> 抠成一个「回」字，
///    这样不用分层窗口也能透明（分层窗口的排除捕获在个别系统上不生效）。
/// </para>
/// </remarks>
internal sealed class RecordBorderWindow : Window
{
    /// <summary>上游 kPhysicalBorderWidth。</summary>
    public const int BorderPhysical = 2;

    /// <summary>上游 kPhysicalBorderPadding：边框与选区之间留 1 像素透明缝隙。</summary>
    public const int GapPhysical = 1;

    private const int WsExTransparent = 0x00000020;
    private const int GwlExstyle = -20;

    private const int RgnDiff = 4;

    public static readonly Color RecordingColor = Color.FromRgb(0xF5, 0x22, 0x2D);
    public static readonly Color PausedColor = Color.FromRgb(0xFA, 0xAD, 0x14);
    public static readonly Color IdleColor = Color.FromRgb(0x40, 0x96, 0xFF);

    private IntPtr _hwnd;
    private Int32Rect _area;
    private double _scale = 1;
    private bool _paused;

    public RecordBorderWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = ThemeBrush.Frozen(RecordingColor);
        UseLayoutRounding = true;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        Loaded += (_, _) => ApplyRegion();
        SizeChanged += (_, _) => ApplyRegion();
    }

    /// <summary>录制区域（屏幕物理像素）。</summary>
    internal Int32Rect Area => _area;

    /// <summary>摆到这块区域外面并显示（已经显示就只挪位置）。</summary>
    public void ShowFor(Int32Rect area)
    {
        _area = area;
        Place();

        if (!IsVisible) Show();
        ApplyRegion();
    }

    /// <summary>跟着选区走（跟随鼠标时每帧都会调）。窗口已经关掉就什么都不做。</summary>
    public void MoveTo(Int32Rect area)
    {
        if (area == _area || !IsVisible) return;
        _area = area;
        Place();
        ApplyRegion();
    }

    /// <summary>暂停 / 恢复：琥珀 ↔ 红。</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused || !IsVisible) return;
        _paused = paused;
        Background = ThemeBrush.Frozen(paused ? PausedColor : RecordingColor);
    }

    // ============ 落点与区域 ============

    private void Place()
    {
        var screen = CaptureRecordWindow.ScreenOf(_area);
        _scale = screen is { Scale: > 0 } ? screen.Scale : 1;

        double inset = (BorderPhysical + GapPhysical) / _scale;
        Left = _area.X / _scale - inset;
        Top = _area.Y / _scale - inset;
        Width = Math.Max(1, _area.Width) / _scale + inset * 2;
        Height = Math.Max(1, _area.Height) / _scale + inset * 2;
    }

    /// <summary>把窗口区域抠成「回」字：只留边框，中间那块根本不属于窗口。</summary>
    private void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero || !IsVisible) return;

        UpdateLayout();

        double scale = _scale > 0 ? _scale : 1;
        int width = Math.Max(4, (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * scale));
        int height = Math.Max(4, (int)Math.Round((ActualHeight > 0 ? ActualHeight : Height) * scale));

        // 区域是**设备像素**：边框就是上游的 2 物理像素，别再按 DPI 放大一次。
        // 窗口比选区四周各大 (2 + 1)，所以「边框 2 像素、留 1 像素透明缝」正好落在窗口里。
        int thickness = BorderPhysical;

        IntPtr outer = CreateRectRgn(0, 0, width, height);
        IntPtr inner = CreateRectRgn(thickness, thickness,
            Math.Max(thickness + 1, width - thickness), Math.Max(thickness + 1, height - thickness));

        if (outer == IntPtr.Zero || inner == IntPtr.Zero)
        {
            if (outer != IntPtr.Zero) DeleteObject(outer);
            if (inner != IntPtr.Zero) DeleteObject(inner);
            return;
        }

        CombineRgn(outer, outer, inner, RgnDiff);
        DeleteObject(inner);

        // 成功时系统接管 outer 的所有权，不要再 DeleteObject
        if (SetWindowRgn(_hwnd, outer, true) == 0) DeleteObject(outer);
    }

    // ============ 窗口样式 ============

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
        if (_hwnd == IntPtr.Zero) return;

        long ex = Win32.GetWindowLongPtr(_hwnd, GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, GwlExstyle, (IntPtr)(ex | WsExTransparent));

        ApplyRegion();
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
