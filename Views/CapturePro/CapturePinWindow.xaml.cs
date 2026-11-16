using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图窗口：把截下来的图钉在屏幕上，可拖动 / 缩放 / 调不透明度 / 置顶 / 锁定。
/// </summary>
/// <remarks>
/// <para>
/// **不抢焦点**：<c>ShowActivated=False</c> + 扩展样式 <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>。
/// 点它、滚它都不该让前台变成自己——否则紧接着注入的键鼠全打在贴图上。
/// 代价是收不到键盘事件（<c>Esc</c> 关不掉），所以出口做成鼠标可达：双击、右上角 ✕、右键菜单。
/// </para>
/// <para>
/// 坐标仍是「窗口 DIP」：截图区域是物理像素，出场时按屏幕缩放换成 DIP（<see cref="PinMath.PlaceAt"/>），
/// 100% 缩放时正好盖住刚才截的那块。滚轮缩放读 <see cref="PinMath"/>，光标下的内容不动。
/// </para>
/// </remarks>
public partial class CapturePinWindow : Window
{
    private readonly BitmapSource _image;
    private readonly double _scale;
    private readonly double _baseWidth;      // 100% 时的 DIP 宽 / 高
    private readonly double _baseHeight;

    private IntPtr _hwnd;
    private double _zoom = 1;
    private bool _locked;

    private bool _dragging;
    private Point _dragStart;
    private double _dragLeft, _dragTop;

    private ContextMenu? _menu;

    public CapturePinWindow(BitmapSource image, Int32Rect area, double scale)
    {
        InitializeComponent();

        _image = image;
        _scale = scale > 0 ? scale : 1;
        Shot.Source = image;

        var rect = PinMath.PlaceAt(new Rect(area.X, area.Y, area.Width, area.Height), _scale);
        _baseWidth = rect.Width;
        _baseHeight = rect.Height;

        Width = _baseWidth;
        Height = _baseHeight;
        Left = rect.X;
        Top = rect.Y;

        Area = area;
        SourceInitialized += (_, _) => MakeNonActivating();
    }

    /// <summary>这张贴图是哪块屏幕区域截下来的（物理像素）。</summary>
    public Int32Rect Area { get; }

    /// <summary>贴的图（导出 / 复制用）。</summary>
    public BitmapSource Image => _image;

    /// <summary>相对 100% 的缩放倍率。</summary>
    public double Zoom => _zoom;

    /// <summary>锁定后不能拖动、不能滚轮缩放 / 改不透明度。</summary>
    public bool Locked => _locked;

    // ============ 出场 ============

    private void MakeNonActivating()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GwlExstyle).ToInt64();
        Win32.SetWindowLongPtr(_hwnd, Win32.GwlExstyle,
            (IntPtr)(ex | Win32.WsExNoactivate | Win32.WsExToolwindow));

        // 物理像素落位：混合 DPI 下 WPF 的 DIP 换算可能差几个像素，
        // 贴图要正好盖住刚才截的那块，所以再用 Win32 钉一次（与覆盖层同一套）。
        int w = Math.Max(1, (int)Math.Round(_baseWidth * _scale));
        int h = Math.Max(1, (int)Math.Round(_baseHeight * _scale));
        Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, Area.X, Area.Y, w, h, Win32.SwpNoactivate);
        RaiseToTop();
    }

    /// <summary>顶回最上面（点它 / 面板显示之后调；不抢焦点）。</summary>
    public void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Topmost ? Win32.HwndTopmost : IntPtr.Zero,   // IntPtr.Zero = HWND_TOP
            0, 0, 0, 0, Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
    }

    // ============ 鼠标 ============

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        RaiseToTop();

        // 双击 = 关闭（比右键菜单快一步；锁定也允许，它不会改位置）
        if (e.ClickCount == 2)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (_locked) return;

        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragLeft = Left;
        _dragTop = Top;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point at = e.GetPosition(this);
        double left = _dragLeft + (at.X - _dragStart.X);
        double top = _dragTop + (at.Y - _dragStart.Y);

        // 至少留 40 DIP 在虚拟桌面里，拖出去就找不回来了
        double minLeft = SystemParameters.VirtualScreenLeft - Width + 40;
        double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40;
        double minTop = SystemParameters.VirtualScreenTop - Height + 40;
        double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;

        Left = Math.Clamp(left, minLeft, maxLeft);
        Top = Math.Clamp(top, minTop, maxTop);
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Cursor = Cursors.Arrow;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        e.Handled = true;
    }

    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_locked) return;

        // Ctrl + 滚轮 = 不透明度；单独滚 = 缩放（缩放锚在光标下，见 PinMath.AnchoredOrigin）
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            Opacity = PinMath.NextOpacity(Opacity, e.Delta);
            return;
        }

        double next = PinMath.NextZoom(_zoom, e.Delta);
        if (Math.Abs(next - _zoom) < 0.0001) return;

        Point at = e.GetPosition(this);
        var origin = new Point(Left, Top);
        var oldSize = new Size(Width, Height);

        ApplyZoom(next);

        Point moved = PinMath.AnchoredOrigin(origin, oldSize, new Size(Width, Height), at);
        Left = moved.X;
        Top = moved.Y;
    }

    /// <summary>换缩放（滚轮与右键菜单共用；菜单点完按窗口中心锚定）。</summary>
    public void SetZoom(double zoom)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var origin = new Point(Left, Top);
        var oldSize = new Size(Width, Height);

        ApplyZoom(zoom);

        Point moved = PinMath.AnchoredOrigin(origin, oldSize, new Size(Width, Height), center);
        Left = moved.X;
        Top = moved.Y;
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = PinMath.ClampZoom(zoom);
        Width = Math.Max(1, _baseWidth * _zoom);
        Height = Math.Max(1, _baseHeight * _zoom);
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e) =>
        CloseBtn.Visibility = Visibility.Visible;

    private void Window_MouseLeave(object sender, MouseEventArgs e) =>
        CloseBtn.Visibility = Visibility.Collapsed;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    // ============ 右键菜单 ============

    /// <summary>
    /// 菜单在**按下时**装好：WPF 的菜单服务在 <c>ContextMenuOpening</c> 触发时已经取过菜单，
    /// 那里现赋的话第一次右键只会存下菜单、不弹出（与步骤列表同一个坑）。
    /// </summary>
    private void Window_RightDown(object sender, MouseButtonEventArgs e)
    {
        RaiseToTop();
        ContextMenu = BuildMenu();
    }

    private ContextMenu BuildMenu()
    {
        _menu?.IsOpen = false;

        var menu = new ContextMenu { PlacementTarget = this };

        var copy = new MenuItem { Header = "复制图片" };
        copy.Click += (_, _) => CopyImage();
        menu.Items.Add(copy);

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => CaptureSave.TrySave(null, _image);
        menu.Items.Add(save);

        var zoom = MenuFx.Submenu(menu, "缩放");
        AddZoom(zoom, "100%", 1);
        AddZoom(zoom, "50%", 0.5);
        AddZoom(zoom, "200%", 2);
        AddZoom(zoom, "400%", 4);

        var opacity = MenuFx.Submenu(menu, "不透明度");
        AddOpacity(opacity, "100%", 1);
        AddOpacity(opacity, "80%", 0.8);
        AddOpacity(opacity, "60%", 0.6);
        AddOpacity(opacity, "40%", 0.4);

        menu.Items.Add(new Separator());

        var pin = new MenuItem { Header = "置顶", IsCheckable = true, IsChecked = Topmost };
        pin.Click += (_, _) =>
        {
            Topmost = !Topmost;
            RaiseToTop();
        };
        menu.Items.Add(pin);

        var locked = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = _locked };
        locked.Click += (_, _) => SetLocked(!_locked);
        menu.Items.Add(locked);

        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "关闭" };
        close.Click += (_, _) => Close();
        menu.Items.Add(close);

        var closeAll = new MenuItem { Header = "关闭全部贴图" };
        closeAll.Click += (_, _) => CapturePinManager.CloseAll();
        menu.Items.Add(closeAll);

        _menu = menu;
        return menu;
    }

    private void AddZoom(ItemsControl parent, string header, double zoom)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => SetZoom(zoom);
        parent.Items.Add(item);
    }

    private void AddOpacity(ItemsControl parent, string header, double opacity)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Opacity = PinMath.ClampOpacity(opacity);
        parent.Items.Add(item);
    }

    /// <summary>锁定：不拖动、不缩放，边框亮起来告诉用户「这是锁住的」。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        Frame.BorderBrush = new SolidColorBrush(locked
            ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        if (locked && _dragging)
        {
            _dragging = false;
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        }
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void CopyImage()
    {
        if (NativeClipboard.SetImage(_image)) ToastService.Show("贴图已复制", "贴图", 1600);
        else ToastService.Show("复制贴图失败", "贴图", 0, ToastKind.error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _menu?.IsOpen = false;
        _menu = null;
        base.OnClosed(e);
        CapturePinManager.Forget(this);
    }
}
