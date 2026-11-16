using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
/// 贴图窗口：把截下来的图钉在屏幕上，可拖动 / 缩放 / 调不透明度 / 置顶 / 锁定 / 局部显示。
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
/// <para>
/// **局部显示**（<see cref="SetViewport"/>）：窗口变成固定大小的取景框，图片在框里平移、超出部分裁掉。
/// 长截图钉在屏幕上时它比屏幕还大，整张缩到能看又太小；切到局部显示就能在 100% 下拖着一块块看。
/// 此时拖动 = 平移内容，按住 <c>Ctrl</c> 拖才是移动窗口（取景框也得能挪地方）。
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
    private bool _viewport;
    private Vector _pan;                     // 局部显示里内容相对取景框的偏移（DIP）

    private bool _dragging;
    private bool _panning;
    private Point _dragStart;                // 平移：按下的**窗口内**位置（窗口不动，够用）
    private Point _moveStart;                // 移动窗口：按下的**屏幕**位置（DIP）
    private Vector _panStart;

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
        ApplyContent();

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

    /// <summary>局部显示开着没有（窗口是固定大小的取景框）。</summary>
    public bool Viewport => _viewport;

    /// <summary>局部显示里内容的偏移（DIP，负 = 往左 / 上拖过）。</summary>
    public Vector Pan => _pan;

    /// <summary>内容当前的实际尺寸（100% 尺寸 × 缩放）。</summary>
    private Size ContentSize => new(Math.Max(1, _baseWidth * _zoom), Math.Max(1, _baseHeight * _zoom));

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

    // ============ 局部显示 ============

    /// <summary>
    /// 开 / 关局部显示。开：窗口缩成取景框（不超过内容，也不超过工作区），内容居中；
    /// 关：窗口回到整张图的尺寸，偏移归零。两种切换都以窗口中心为锚，图不会突然跳走。
    /// </summary>
    public void SetViewport(bool on)
    {
        if (_viewport == on) return;
        _viewport = on;

        var content = ContentSize;

        if (on)
        {
            var view = PinMath.ViewportSize(content, WindowSizing.WorkAreaOf(this));
            ResizeCentered(view);
            _pan = PinMath.CenterPan(content, new Size(Width, Height));
        }
        else
        {
            ResizeCentered(content);
            _pan = default;
        }

        ApplyContent();
        ApplyFrame();
        ApplyCursor();
        ViewHint.Visibility = _viewport && IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>改窗口尺寸但守住中心（进入 / 退出取景框、缩小到工作区里都用它）。</summary>
    private void ResizeCentered(Size size)
    {
        double cx = Left + Width / 2;
        double cy = Top + Height / 2;

        Width = Math.Max(1, size.Width);
        Height = Math.Max(1, size.Height);

        var box = new Rect(cx - Width / 2, cy - Height / 2, Width, Height);
        Vector delta = PinMath.ConstrainGroupMove(new Vector(0, 0), new[] { box },
            PinMath.VirtualDesktop, PinMath.KeepVisible);

        Left = box.Left + delta.X;
        Top = box.Top + delta.Y;
    }

    /// <summary>把内容尺寸与偏移写到界面（缩放 / 平移 / 进出取景框都只调它）。</summary>
    private void ApplyContent()
    {
        var content = ContentSize;
        Shot.Width = content.Width;
        Shot.Height = content.Height;
        PanTransform.X = _pan.X;
        PanTransform.Y = _pan.Y;
    }

    /// <summary>关掉局部显示后窗口还该多大：整张图的尺寸（滚轮 / 菜单换缩放时用）。</summary>
    private void FitWindowToContent()
    {
        if (_viewport) return;
        var content = ContentSize;
        Width = content.Width;
        Height = content.Height;
    }

    // ============ 鼠标 ============

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        RaiseToTop();

        // 点在自己的按钮上（右上角 ✕）就交给按钮：这个处理器在隧道阶段先到，
        // 要是照常走「按下 = 开始拖动」并 CaptureMouse()，按钮永远收不到 MouseUp、
        // Click 不会触发——用户得连点好几次、凑巧双击才关得掉（实测）。
        if (IsOnButton(e.OriginalSource as DependencyObject)) return;

        // 双击 = 关闭（比右键菜单快一步；锁定也允许，它不会改位置）
        if (e.ClickCount == 2)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (_locked) return;

        // 局部显示里拖动是「平移内容」：窗口不动，用窗口内坐标；
        // 移动窗口必须用**屏幕**坐标——窗口跟着走时窗口内坐标会互相抵消，
        // 每帧只落下最后一个增量，看着就是拖影 / 追不上手（见 DragTo）。
        if (IsPanGesture())
        {
            _panning = true;
            _dragStart = e.GetPosition(this);
            _panStart = _pan;
            Cursor = Cursors.ScrollAll;
        }
        else
        {
            _dragging = true;
            _moveStart = CursorScreenDip();
            CapturePinManager.BeginMove(this);
            Cursor = Cursors.SizeAll;
        }

        CaptureMouse();
        e.Handled = true;
    }

    private bool IsPanGesture() => _viewport && (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    /// <summary>
    /// 拖到光标的这个**屏幕**位置（DIP）。位移一律是「现在 − 按下时」，
    /// 与 <see cref="Window.Left"/> / <see cref="Window.Top"/> 同一坐标系，
    /// 所以窗口正好停在光标带走的那段距离上，不会一顿一顿地回弹。
    /// </summary>
    internal void DragTo(Point screenDip)
    {
        CapturePinManager.MoveBy(this, screenDip.X - _moveStart.X, screenDip.Y - _moveStart.Y);
    }

    /// <summary>
    /// 光标在「屏幕 DIP」里的位置：<c>GetCursorPos</c> 给物理像素，
    /// 按窗口自己的 <c>TransformFromDevice</c> 换成 DIP（与 <c>Left/Top</c> 同一套换算）。
    /// </summary>
    private Point CursorScreenDip()
    {
        if (!Win32.GetCursorPos(out var pt)) return new Point(Left, Top);

        double sx = 1, sy = 1;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            if (tfd.M11 > 0) sx = tfd.M11;
            if (tfd.M22 > 0) sy = tfd.M22;
        }
        return new Point(pt.X * sx, pt.Y * sy);
    }

    /// <summary>事件源是不是某个按钮（贴图里的 ✕）：按钮的点击不能当成拖动。</summary>
    private static bool IsOnButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging && !_panning) return;

        if (_panning)
        {
            Point at = e.GetPosition(this);
            _pan = PinMath.ClampPan(_panStart + (at - _dragStart), ContentSize, new Size(Width, Height));
            ApplyContent();
            e.Handled = true;
            return;
        }

        // 移动窗口一律交给登记处：单张时它只夹自己，成组移动时按「整组一个位移」夹
        DragTo(CursorScreenDip());
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging && !_panning) return;

        _dragging = false;
        _panning = false;
        CapturePinManager.EndMove();
        ApplyCursor();
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

        if (_viewport)
        {
            // 取景框不跟图走：只换内容缩放，再用锚点算出该看哪一块
            Vector pan = PinMath.ZoomAnchoredPan(_pan, at, _zoom, next);
            ApplyZoom(next);
            _pan = PinMath.ClampPan(pan, ContentSize, new Size(Width, Height));
            ApplyContent();
            return;
        }

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
        double target = PinMath.ClampZoom(zoom);

        if (_viewport)
        {
            var center = new Point(Width / 2, Height / 2);
            Vector pan = PinMath.ZoomAnchoredPan(_pan, center, _zoom, target);
            ApplyZoom(target);
            _pan = PinMath.ClampPan(pan, ContentSize, new Size(Width, Height));
            ApplyContent();
            return;
        }

        var anchor = new Point(ActualWidth / 2, ActualHeight / 2);
        var origin = new Point(Left, Top);
        var oldSize = new Size(Width, Height);

        ApplyZoom(target);

        Point moved = PinMath.AnchoredOrigin(origin, oldSize, new Size(Width, Height), anchor);
        Left = moved.X;
        Top = moved.Y;
    }

    private void ApplyZoom(double zoom)
    {
        _zoom = PinMath.ClampZoom(zoom);
        FitWindowToContent();      // 局部显示里是空操作：取景框大小不跟图走
        ApplyContent();
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        CloseBtn.Visibility = Visibility.Visible;
        if (_viewport)
        {
            ViewHintText.Text = "局部显示 · 拖动看别处 · Ctrl 拖动移窗口";
            ViewHint.Visibility = Visibility.Visible;
        }
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseBtn.Visibility = Visibility.Collapsed;
        ViewHint.Visibility = Visibility.Collapsed;
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 空闲光标：取景框里用四向箭头暗示「拖 = 平移」，其它情况是普通箭头
    /// （拖动中的 <c>SizeAll</c> 在按下时临时设，松手回到这里）。
    /// </summary>
    private void ApplyCursor() => Cursor = !_locked && _viewport ? Cursors.ScrollAll : Cursors.Arrow;

    /// <summary>边框颜色同时表达两种状态：锁定（亮蓝）优先，其次局部显示（淡蓝）。</summary>
    private void ApplyFrame()
    {
        Frame.BorderBrush = new SolidColorBrush(_locked
            ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : _viewport
                ? Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)
                : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

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

        // 局部显示：长截图钉在屏幕上比屏幕还大时用——取景框固定，拖着看
        var viewport = new MenuItem { Header = "局部显示", IsCheckable = true, IsChecked = _viewport };
        viewport.Click += (_, _) => SetViewport(!_viewport);
        menu.Items.Add(viewport);

        // 成组移动：只有一张时没有「组」，置灰（开关本身记着，等第二张贴上来才生效）
        var group = new MenuItem
        {
            Header = "成组移动",
            IsCheckable = true,
            IsChecked = CapturePinManager.GroupMove,
            IsEnabled = CapturePinManager.Count > 1,
        };
        group.Click += (_, _) => CapturePinManager.GroupMove = !CapturePinManager.GroupMove;
        menu.Items.Add(group);

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
        ApplyFrame();
        if (locked && (_dragging || _panning))
        {
            _dragging = false;
            _panning = false;
            CapturePinManager.EndMove();
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        }
        ApplyCursor();
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
