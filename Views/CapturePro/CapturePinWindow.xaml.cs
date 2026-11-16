using System;
using System.Linq;
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
/// 贴图窗口：把截下来的图钉在屏幕上，可拖动 / 缩放 / 调不透明度 / 置顶 / 锁定 / 局部显示 / 缩略图 / 标注。
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
/// <para>
/// **缩略图**（<see cref="SetThumbnail"/>）：缩成 <see cref="PinMath.ThumbnailSide"/> 的小方块让开路，
/// 与局部显示互斥；方块里双击 = 还原（不是关闭）。**标注**（<see cref="AnnotateContent"/>）复用截图覆盖层
/// （<c>editOnly</c>），标完把结果换回这张贴图。
/// </para>
/// </remarks>
public partial class CapturePinWindow : Window
{
    private BitmapSource _content;
    private readonly double _scale;
    private double _baseWidth;               // 100% 时的 DIP 宽 / 高
    private double _baseHeight;

    private IntPtr _hwnd;
    private double _zoom = 1;
    private bool _locked;
    private bool _viewport;
    private bool _thumb;                     // 缩略图模式（固定边长的小方块）
    private bool _hovering;
    private Vector _pan;                     // 局部显示里内容相对取景框的偏移（DIP）

    private bool _dragging;
    private bool _panning;
    private Point _dragStart;                // 平移：按下的**窗口内**位置（窗口不动，够用）
    private Point _moveStart;                // 移动窗口：按下的**屏幕**位置（DIP）
    private Vector _panStart;

    private bool _maybeSelect;                // Ctrl 按下但还没挪动：松手算「多选」，挪了算「拖」
    private Point _selectAt;                  // 上面那一下按下的屏幕位置（DIP）

    private string _group = PinGroups.Default;
    private bool _selected;

    private ContextMenu? _menu;

    public CapturePinWindow(BitmapSource image, Int32Rect area, double scale)
    {
        InitializeComponent();

        _content = image;
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

    /// <summary>贴的图（导出 / 复制用；标注之后就是标完的那张）。</summary>
    public BitmapSource Image => _content;

    /// <summary>相对 100% 的缩放倍率。</summary>
    public double Zoom => _zoom;

    /// <summary>锁定后不能拖动、不能滚轮缩放 / 改不透明度。</summary>
    public bool Locked => _locked;

    /// <summary>局部显示开着没有（窗口是固定大小的取景框）。</summary>
    public bool Viewport => _viewport;

    /// <summary>缩略图模式开着没有（窗口是 83 DIP 的小方块）。</summary>
    public bool Thumbnail => _thumb;

    /// <summary>局部显示里内容的偏移（DIP，负 = 往左 / 上拖过）。</summary>
    public Vector Pan => _pan;

    /// <summary>这张贴图在哪个分组（只在内存，见 <see cref="CapturePinManager"/>）。</summary>
    public string Group => _group;

    /// <summary>在多选里没有（<c>Ctrl</c>+单击切换）。</summary>
    public bool Selected => _selected;

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
        if (on && _thumb) SetThumbnail(false);        // 两种特殊形态互斥
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
        RefreshHoverChrome();
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

    /// <summary>把内容尺寸与偏移写到界面（缩放 / 平移 / 进出取景框 / 缩略图都只调它）。</summary>
    private void ApplyContent()
    {
        if (_thumb)
        {
            // 缩略图：整张图等比塞进方块（按 100% 铺的话只能看见左上角一块）
            Shot.Width = Math.Max(1, Width);
            Shot.Height = Math.Max(1, Height);
            PanTransform.X = 0;
            PanTransform.Y = 0;
            return;
        }

        var content = ContentSize;
        Shot.Width = content.Width;
        Shot.Height = content.Height;
        PanTransform.X = _pan.X;
        PanTransform.Y = _pan.Y;
    }

    /// <summary>关掉局部显示后窗口还该多大：整张图的尺寸（滚轮 / 菜单换缩放时用）。</summary>
    private void FitWindowToContent()
    {
        if (_viewport || _thumb) return;
        var content = ContentSize;
        Width = content.Width;
        Height = content.Height;
    }

    // ============ 缩略图模式 ============

    /// <summary>
    /// 缩略图模式：缩成一个 <see cref="PinMath.ThumbnailSide"/> 的小方块（图等比放进去），再点一次还原。
    /// 与局部显示互斥（一个是「只露一块」、一个是「整张缩小」）；开关放在右键菜单里，双击也能还原。
    /// </summary>
    public void SetThumbnail(bool on)
    {
        if (_thumb == on) return;

        if (on)
        {
            if (_viewport) SetViewport(false);        // 两种特殊形态互斥
            _thumb = true;
            Shot.Stretch = Stretch.Uniform;
            ResizeCentered(new Size(PinMath.ThumbnailSide, PinMath.ThumbnailSide));
            _pan = default;
        }
        else
        {
            _thumb = false;
            Shot.Stretch = Stretch.Fill;
            ResizeCentered(ContentSize);              // 回到整张图（缩放与位置都不变）
        }

        ApplyContent();
        ApplyFrame();
        ApplyCursor();
        RefreshHoverChrome();
        RefreshBadges();                              // 方块里不挂徽标
    }

    // ============ 标注 ============

    /// <summary>
    /// 标注这张贴图：覆盖层开在贴图上面（冻结画面就是它自己），标完的结果换回这张贴图。
    /// 热键 / 鼠标钩子的让路在 <see cref="CaptureSession.TryEdit"/> 里，别在这里另写一份。
    /// </summary>
    private void AnnotateContent()
    {
        // 覆盖层只能把图按「像素 1:1」铺开，所以标注前先回到整张图的 100%：
        // 缩略图 / 取景框里都只看得见一部分，缩放 200% 时覆盖层与贴图本身对不上。
        if (_thumb) SetThumbnail(false);
        if (_viewport) SetViewport(false);
        if (Math.Abs(_zoom - 1) > 0.001) SetZoom(1);

        var result = CaptureSession.TryEdit(_content, PhysicalArea());
        if (result == null) return;                   // 取消 = 什么都不变

        ReplaceContent(result);
    }

    /// <summary>窗口现在占着的屏幕物理像素矩形（被拖到别的显示器时按当前 DPI 算）。</summary>
    private Int32Rect PhysicalArea()
    {
        double sx = _scale, sy = _scale;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var ttd = target.TransformToDevice;
            if (ttd.M11 > 0) sx = ttd.M11;
            if (ttd.M22 > 0) sy = ttd.M22;
        }

        return new Int32Rect(
            (int)Math.Round(Left * sx), (int)Math.Round(Top * sy),
            Math.Max(2, (int)Math.Round(Width * sx)), Math.Max(2, (int)Math.Round(Height * sy)));
    }

    /// <summary>换掉贴的内容（标注之后）：回到 100% 并按新图尺寸贴合窗口。</summary>
    private void ReplaceContent(BitmapSource image)
    {
        _content = image;
        Shot.Source = image;
        _zoom = 1;
        _baseWidth = Math.Max(1, image.PixelWidth / _scale);
        _baseHeight = Math.Max(1, image.PixelHeight / _scale);

        if (!_viewport && !_thumb) ResizeCentered(ContentSize);
        ApplyContent();
        ApplyCursor();
        RefreshHoverChrome();
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

        // 双击：缩略图里 = 还原成整张图（不是关掉，83px 的小方块太容易误关）；
        // 普通贴图 = 关闭（比右键菜单快一步；锁定也允许，它不会改位置）
        if (e.ClickCount == 2)
        {
            if (_thumb) SetThumbnail(false);
            else Close();
            e.Handled = true;
            return;
        }

        // Ctrl+单击 = 多选（贴图不抢焦点、收不到键盘，这是唯一的选择入口）。
        // 先不动手：取景框里 Ctrl 拖动是「移动窗口」，得看松手前有没有挪过再定。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _maybeSelect = true;
            _selectAt = CursorScreenDip();
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_locked) return;

        BeginGesture(e.GetPosition(this));
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>按下后真正开始的那一下手势：取景框里平移内容，其余情况移动窗口（含成组 / 多选）。</summary>
    private void BeginGesture(Point windowPos)
    {
        // 局部显示里拖动是「平移内容」：窗口不动，用窗口内坐标；
        // 移动窗口必须用**屏幕**坐标——窗口跟着走时窗口内坐标会互相抵消，
        // 每帧只落下最后一个增量，看着就是拖影 / 追不上手（见 DragTo）。
        if (IsPanGesture())
        {
            _panning = true;
            _dragStart = windowPos;
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
        // Ctrl 按下还没挪：挪过 4 DIP 就作废「多选」，改判成拖动 / 平移
        if (_maybeSelect)
        {
            Point now = CursorScreenDip();
            if (Math.Abs(now.X - _selectAt.X) < 4 && Math.Abs(now.Y - _selectAt.Y) < 4)
            {
                e.Handled = true;
                return;
            }

            _maybeSelect = false;
            if (_locked)                  // 锁着的贴图不跟着走，这一下什么都不做
            {
                try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
                return;
            }
            BeginGesture(e.GetPosition(this));
        }

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
        if (_maybeSelect)
        {
            _maybeSelect = false;
            CapturePinManager.ToggleSelect(this);
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
            e.Handled = true;
            return;
        }

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

        if (_thumb) return;      // 缩略图里滚轮不改缩放（要改先双击还原）

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
        if (_thumb) SetThumbnail(false);      // 缩略图里改缩放 = 先还原（方块里缩放没有意义）
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
        _hovering = true;
        RefreshHoverChrome();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        _hovering = false;
        RefreshHoverChrome();
    }

    /// <summary>
    /// 悬停界面（右上角 ✕、左下角状态提示）的显隐。
    /// 小窗口与缩略图里按钮会盖住内容，<see cref="PinMath.ShowsHoverButtons"/> 直接不给
    /// （出口仍在：双击、右键菜单）；提示只在局部显示时出现，退回去就收起。
    /// </summary>
    /// <remarks>
    /// 判「悬停」要写成 <c>_hovering || IsMouseOver</c>：无头冒烟里 <c>MouseEnter</c> 是反射调的，
    /// <c>IsMouseOver</c> 恒为 false，只看它的话冒烟永远看不到按钮。
    /// </remarks>
    private void RefreshHoverChrome()
    {
        bool hover = _hovering || IsMouseOver;

        CloseBtn.Visibility = hover && PinMath.ShowsHoverButtons(Width, Height, _thumb)
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (hover && _viewport && !_thumb)
        {
            ViewHintText.Text = "局部显示 · 拖动看别处 · Ctrl 拖动移窗口";
            ViewHint.Visibility = Visibility.Visible;
        }
        else
        {
            ViewHint.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 空闲光标：取景框里用四向箭头暗示「拖 = 平移」，其它情况是普通箭头
    /// （拖动中的 <c>SizeAll</c> 在按下时临时设，松手回到这里）。
    /// </summary>
    private void ApplyCursor() => Cursor = !_locked && _viewport ? Cursors.ScrollAll : Cursors.Arrow;

    /// <summary>边框颜色同时表达三种状态：多选（2px 亮蓝）优先，其次锁定（亮蓝），再次局部显示（淡蓝）。</summary>
    private void ApplyFrame()
    {
        if (_selected)
        {
            // 2px 描边是「压在图上」画的（见 XAML 里 Frame 的注释），加粗不会挤动内容
            Frame.BorderThickness = new Thickness(2);
            Frame.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            return;
        }

        Frame.BorderThickness = new Thickness(1);
        Frame.BorderBrush = new SolidColorBrush(_locked
            ? Color.FromRgb(0x3B, 0x82, 0xF6)
            : _viewport
                ? Color.FromArgb(0x66, 0x3B, 0x82, 0xF6)
                : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

    // ============ 分组 / 多选 ============

    /// <summary>换分组（由 <see cref="CapturePinManager.MoveToGroup"/> 调；只管显示，藏 / 显在那边）。</summary>
    internal void SetGroup(string group)
    {
        _group = string.IsNullOrWhiteSpace(group) ? PinGroups.Default : group;
        RefreshBadges();
    }

    /// <summary>多选状态（由 <see cref="CapturePinManager"/> 调）。</summary>
    internal void SetSelected(bool on)
    {
        if (_selected == on) return;
        _selected = on;
        ApplyFrame();
        RefreshBadges();
    }

    /// <summary>
    /// 左上角两枚徽标：已选 + 分组名（默认组不显示，免得每张都挂一行）。
    /// 缩略图方块里一律收起——83 DIP 的方块会被盖掉一半。
    /// </summary>
    private void RefreshBadges()
    {
        SelectionBadge.Visibility = _selected && !_thumb ? Visibility.Visible : Visibility.Collapsed;

        bool named = !_thumb && !PinGroups.Same(_group, PinGroups.Default);
        GroupBadgeText.Text = _group;
        GroupBadge.Visibility = named ? Visibility.Visible : Visibility.Collapsed;
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
        save.Click += (_, _) => CaptureSave.TrySave(null, _content);
        menu.Items.Add(save);

        // 标注这张贴图：覆盖层开在它上面，标完换回内容（菜单必须先收起，ShowDialog 挂在开着
        // 的 Popup 上会让菜单看不见也点不动）
        var annotate = new MenuItem { Header = "标注…" };
        annotate.Click += (_, _) =>
        {
            _menu?.IsOpen = false;
            Dispatcher.BeginInvoke(new Action(AnnotateContent),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        menu.Items.Add(annotate);

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

        AddGroupMenu(menu);
        AddBatchMenu(menu);

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

        // 缩略图模式：缩成一个小方块让开路（与局部显示互斥，双击方块可还原）
        var thumb = new MenuItem { Header = "缩略图模式", IsCheckable = true, IsChecked = _thumb };
        thumb.Click += (_, _) => SetThumbnail(!_thumb);
        menu.Items.Add(thumb);

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

    /// <summary>
    /// 「分组」子菜单：先「移到」（勾的是这张图现在的组），再「只看」（勾的是当前的过滤），
    /// 中间夹「新建分组并移入…」（建完直接挪过去，省一步）。
    /// </summary>
    private void AddGroupMenu(ContextMenu menu)
    {
        var root = MenuFx.Submenu(menu, "分组");

        foreach (var name in CapturePinManager.Groups)
        {
            string target = name;
            var item = new MenuItem
            {
                Header = $"移到「{name}」",
                IsCheckable = true,
                IsChecked = PinGroups.Same(target, _group),
            };
            item.Click += (_, _) => MoveToGroup(target);
            root.Items.Add(item);
        }

        var create = new MenuItem { Header = "新建分组并移入…" };
        create.Click += (_, _) =>
        {
            _menu?.IsOpen = false;      // 模态输入框不能挂在开着的 popup 上
            Dispatcher.BeginInvoke(new Action(NewGroupAndMove),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        root.Items.Add(create);

        root.Items.Add(new Separator());

        var all = new MenuItem
        {
            Header = "显示全部分组",
            IsCheckable = true,
            IsChecked = CapturePinManager.ActiveGroup == null,
        };
        all.Click += (_, _) => CapturePinManager.SetActiveGroup(null);
        root.Items.Add(all);

        foreach (var name in CapturePinManager.Groups)
        {
            string target = name;
            var item = new MenuItem
            {
                Header = $"只看「{name}」",
                IsCheckable = true,
                IsChecked = PinGroups.Same(target, CapturePinManager.ActiveGroup),
            };
            item.Click += (_, _) => CapturePinManager.SetActiveGroup(target);
            root.Items.Add(item);
        }
    }

    /// <summary>挪到某个分组；挪出当前正看着的组会当场藏起来，给一句 toast 免得像「贴图丢了」。</summary>
    private void MoveToGroup(string group)
    {
        CapturePinManager.MoveToGroup(this, group);
        if (!IsVisible && CapturePinManager.ActiveGroup != null)
            ToastService.Show($"已移到「{_group}」，当前只看「{CapturePinManager.ActiveGroup}」",
                "贴图分组", 2600, ToastKind.info, key: "pin-group");
    }

    /// <summary>弹输入框建一个新分组并把这张图挪进去（贴图是 Topmost 且不抢焦点，框必须自己置顶）。</summary>
    private void NewGroupAndMove()
    {
        var (ok, value) = InputDialog.Show(null, "新建分组",
            $"分组名（最多 {PinGroups.MaxName} 字）", "", topmost: true);
        if (!ok) return;

        string? name = CapturePinManager.CreateGroup(value);
        if (name == null)
        {
            ToastService.Show("分组名不能为空", "贴图分组", 2400, ToastKind.warning);
            return;
        }

        MoveToGroup(name);
    }

    /// <summary>
    /// 「批量」子菜单：全选 / 取消 / 关闭 / 另存 / 锁定 / 缩略图，全部作用在多选上。
    /// 一张没选时后四项置灰；贴图不抢焦点、收不到键盘，所以底部留一句「Ctrl+单击」的提示。
    /// </summary>
    private void AddBatchMenu(ContextMenu menu)
    {
        int count = CapturePinManager.Selected.Count;
        var root = MenuFx.Submenu(menu, count > 0 ? $"批量（已选 {count} 张）" : "批量");

        var all = new MenuItem { Header = "全选", IsEnabled = CapturePinManager.Visible.Count > 0 };
        all.Click += (_, _) => CapturePinManager.SelectAll();
        root.Items.Add(all);

        var none = new MenuItem { Header = "取消选择", IsEnabled = count > 0 };
        none.Click += (_, _) => CapturePinManager.ClearSelection();
        root.Items.Add(none);

        root.Items.Add(new Separator());

        var close = new MenuItem
        {
            Header = count > 0 ? $"关闭所选（{count} 张）" : "关闭所选",
            IsEnabled = count > 0,
        };
        close.Click += (_, _) => CapturePinManager.CloseSelected();
        root.Items.Add(close);

        // 另存要弹文件夹选择，先收起菜单（模态框挂在开着的 popup 上会点不动）
        var save = new MenuItem { Header = "另存所选…", IsEnabled = count > 0 };
        save.Click += (_, _) =>
        {
            var images = CapturePinManager.Selected.Select(p => p.Image).ToArray();
            _menu?.IsOpen = false;
            Dispatcher.BeginInvoke(new Action(() => CaptureSave.TrySaveMany(null, images)),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        root.Items.Add(save);

        bool allLocked = count > 0 && CapturePinManager.Selected.All(p => p.Locked);
        var locked = new MenuItem
        {
            Header = allLocked ? "解锁所选" : "锁定所选",
            IsEnabled = count > 0,
        };
        locked.Click += (_, _) => CapturePinManager.SetSelectedLocked(!allLocked);
        root.Items.Add(locked);

        bool allThumb = count > 0 && CapturePinManager.Selected.All(p => p.Thumbnail);
        var thumb = new MenuItem
        {
            Header = allThumb ? "还原所选" : "缩略图所选",
            IsEnabled = count > 0,
        };
        thumb.Click += (_, _) => CapturePinManager.SetSelectedThumbnail(!allThumb);
        root.Items.Add(thumb);

        root.Items.Add(new Separator());
        root.Items.Add(new MenuItem { Header = "按住 Ctrl 再单击可多选", IsEnabled = false });
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
        if (locked && (_dragging || _panning || _maybeSelect))
        {
            _dragging = false;
            _panning = false;
            _maybeSelect = false;
            CapturePinManager.EndMove();
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        }
        ApplyCursor();
    }

    private void CopyImage()
    {
        if (NativeClipboard.SetImage(_content)) ToastService.Show("贴图已复制", "贴图", 1600);
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
