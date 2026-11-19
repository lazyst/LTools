using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的阴影：一层独立的分层窗口，垫在贴图窗口**正下方**。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>pinnedShadowMargin</c>：阴影画在内容外面 <b>16 物理像素</b>宽的一圈里
/// （逻辑像素模式 12），峰值不透明度 <c>0.36</c>（<c>screenshotshadowprofile.h</c>），
/// 向外按 <c>(1 - d / w)^4</c> 衰减。这里用「圆角矩形 + 高斯模糊」逼近同一条曲线：
/// 窗口比贴图大一圈，中间那块被贴图自己盖住，只有外圈露出来。
/// </para>
/// <para>
/// **为什么不做进贴图窗口**：贴图窗口的宽高就是内容宽高（拖动、缩放、吸附、导出全按它算），
/// 往里塞内边距会把这套几何全部推翻。独立窗口只跟着 <c>Left/Top/Width/Height</c> 走，
/// 与点击穿透控制栏（<see cref="PinClickThroughBar"/>）同一个做法。
/// </para>
/// <para>
/// 不抢焦点、不吃鼠标（<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT</c>），
/// 每次贴图抬头 / 挪动都把阴影重新插到它下面（<c>SetWindowPos(hwnd, 贴图句柄)</c>）。
/// </para>
/// </remarks>
internal sealed class PinShadowWindow : Window
{
    /// <summary>阴影带宽（物理像素，照上游 <c>pinnedShadowMargin</c>）。</summary>
    public const double MarginPhysical = 16;

    /// <summary>峰值不透明度（照上游 <c>peakAlphaScale</c>）。</summary>
    public const double PeakAlpha = 0.36;

    /// <summary>带宽换 DIP；屏幕缩放未知时按 1 算，并且不小于 6（太小看不出阴影）。</summary>
    public static double MarginDip(double scale) => Math.Max(6, MarginPhysical / (scale > 0 ? scale : 1));

    private readonly Border _blur;
    private IntPtr _hwnd;

    public PinShadowWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsTabStop = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        IsHitTestVisible = false;
        UseLayoutRounding = false;

        _blur = new Border
        {
            IsHitTestVisible = false,
            SnapsToDevicePixels = false,
        };
        Content = _blur;

        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: false);
            if (_hwnd == IntPtr.Zero) return;

            long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
            Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle, (IntPtr)(ex | Win32.WsExTransparent));
        };
    }

    /// <summary>摆到贴图矩形外面一圈，并按当前状态色重画。</summary>
    /// <param name="content">贴图窗口的屏幕矩形（DIP）。</param>
    /// <param name="cornerRadius">贴图内容当前的圆角（DIP）。</param>
    /// <param name="color">阴影色（含 alpha）。</param>
    /// <param name="scale">贴图所在显示器的缩放（物理像素 → DIP）。</param>
    /// <param name="topmost">跟着贴图的置顶状态。</param>
    /// <param name="above">贴图窗口句柄：阴影插到它下面；<see cref="IntPtr.Zero"/> 时只置顶。</param>
    public void Follow(Rect content, double cornerRadius, Color color, double scale,
        bool topmost, IntPtr above)
    {
        double margin = MarginDip(scale);

        Left = content.X - margin;
        Top = content.Y - margin;
        Width = Math.Max(1, content.Width + margin * 2);
        Height = Math.Max(1, content.Height + margin * 2);

        _blur.Margin = new Thickness(margin);
        _blur.CornerRadius = new CornerRadius(Math.Max(0, cornerRadius) + margin * 0.5);

        byte alpha = (byte)Math.Round(color.A * PeakAlpha);
        _blur.Background = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));

        // 模糊半径取带宽的一半：可见扩散范围约 2r ≈ 带宽，正好被窗口留出的边距装下
        _blur.Effect = new BlurEffect
        {
            Radius = Math.Max(2, margin * 0.5),
            KernelType = KernelType.Gaussian,
            RenderingBias = RenderingBias.Quality,
        };

        if (Topmost != topmost) Topmost = topmost;
        Place(above);
    }

    /// <summary>插到贴图下面（贴图抬头 / 挪动之后调；不抢焦点）。</summary>
    public void Place(IntPtr above)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        Win32.SetWindowPos(_hwnd, above == IntPtr.Zero ? Win32.HwndTopmost : above,
            0, 0, 0, 0, Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }
}
