using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图收进顶部之后留在工作区顶边的那条小手柄（照上游
/// <c>screenshotpinnedhidetotopcontroller.cpp</c> 里的 <c>Handle</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 窗口本身 = **命中区**（比可见手柄左右各宽 2 DIP、下方高 2 DIP），底色透明但仍能命中；
/// 可见手柄是里面那一块 30×6 DIP 的彩色条，左 / 下 / 右三面各 2 物理像素深色描边，顶边不画
/// （它贴在工作区顶边上）。
/// </para>
/// <para>
/// 和贴图一样**不抢焦点**（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
/// 鼠标移到手柄上只是把贴图展开，不该让前台程序变成自己的某个窗口。
/// </para>
/// <para>
/// 交互照上游 <c>eventFilter</c>：右键 = 弹出贴图右键菜单并退出收顶部；左键双击 / 中键 =
/// 按设置执行贴图的指针动作；进入 / 离开只是把「指针在不在」告诉宿主（延迟收起由宿主统一算）。
/// </para>
/// </remarks>
internal sealed class PinHideHandleWindow : Window
{
    /// <summary>描边色（主题 <c>colorText</c> 的浅色主题值；贴在工作区顶边，用深灰才看得清）。</summary>
    private static readonly Brush HandleBorderBrush = ThemeBrush.Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F));

    private readonly Border _body;
    private IntPtr _hwnd;

    /// <summary>
    /// 1 个 alpha 步的底色：分层窗口里全透明的像素**点不到**（上游同样用 alpha=1 铺满，
    /// 才让放大后的命中区能收到鼠标）。可见部分被彩色手柄盖住，看不出来。
    /// </summary>
    private static readonly Brush HitWash = ThemeBrush.Frozen(Color.FromArgb(1, 0, 0, 0));

    /// <summary>鼠标进入手柄。</summary>
    public event Action? PointerEntered;

    /// <summary>鼠标离开手柄。</summary>
    public event Action? PointerLeft;

    /// <summary>左键双击。</summary>
    public event Action? LeftDoubleClicked;

    /// <summary>中键。</summary>
    public event Action? MiddleClicked;

    /// <summary>右键（宿主负责先退出收顶部再弹菜单）。</summary>
    public event Action? RightClicked;

    public PinHideHandleWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = HitWash;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsTabStop = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ToolTip = "收到屏幕顶部";

        _body = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            IsHitTestVisible = false,
        };
        Content = new Grid { Background = HitWash, Children = { _body } };

        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount < 2) return;
            e.Handled = true;
            LeftDoubleClicked?.Invoke();
        };

        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            MiddleClicked?.Invoke();
        };

        PreviewMouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            RightClicked?.Invoke();
        };

        MouseEnter += (_, _) => PointerEntered?.Invoke();
        MouseLeave += (_, _) => PointerLeft?.Invoke();

        SourceInitialized += (_, _) => _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: false);
    }

    /// <summary>
    /// 按物理像素摆好：<paramref name="hit"/> 是命中区、<paramref name="visual"/> 是可见手柄，
    /// <paramref name="dpi"/> 是这块屏的缩放。
    /// </summary>
    public void Place(Int32Rect hit, Int32Rect visual, double dpi, Color accent)
    {
        if (dpi <= 0) dpi = 1;

        Width = Math.Max(1, hit.Width / dpi);
        Height = Math.Max(1, hit.Height / dpi);
        Left = hit.X / dpi;
        Top = hit.Y / dpi;

        _body.Width = Math.Max(1, visual.Width / dpi);
        _body.Height = Math.Max(1, visual.Height / dpi);
        _body.Margin = new Thickness(
            Math.Max(0, (visual.X - hit.X) / dpi),
            Math.Max(0, (visual.Y - hit.Y) / dpi),
            0, 0);
        _body.Background = ThemeBrush.Frozen(accent);

        // 2 物理像素的描边：左 / 下 / 右各一面，顶边留空（上游 fillRect 的写法）
        double border = PinHideToTop.HandleBorderDevicePixels / dpi;
        _body.BorderBrush = HandleBorderBrush;
        _body.BorderThickness = new Thickness(border, 0, border, border);

        RaiseToTop();
    }

    /// <summary>提到同层最前、不移动不抢焦点。</summary>
    public void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
            Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }

}
