using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏空闲期的摄像头画中画预览（照 Snow Shot 的 <c>RecordingWebcamControl</c>）：
/// 一块能看到实时画面的方块 / 圆形，能拖动改位置、拖右上角圆钮改大小，
/// 正上方还挂一条四个按钮的方向工具条（顺 / 逆时针转 90°、左右翻、上下翻）。
/// </summary>
/// <remarks>
/// <para>
/// **一个分层窗口装三样东西**：画中画本体（按圆形 / 方形裁剪的摄像头画面）、右上角的缩放圆钮
/// （32 DIP，半透明黑 + 白色对角箭头）、方向工具条（白底 + 1px 描边 + 圆角 4，
/// 4 个 24 DIP 图标按钮）。上游是「控件本体（2 alpha 的交互面）+ 独立浮层工具条」两个 HWND；
/// 这里合成一个分层窗口——全透明的像素本来就不吃鼠标，点上去等于点在桌面上，效果一致，
/// 少一个窗口要同步。
/// </para>
/// <para>
/// 交互全部照上游 <c>recordingwebcamcontrol.cpp</c>：按下即开始（没有拖动阈值）、
/// 位移用 <c>GetCursorPos</c> 的**物理像素**（跨屏拖动不会因为 DPI 变化跳一下）、
/// 缩放用 <c>size = clamp(初始边长 + (dx − dy) / 2, 50, 500)</c>、
/// 边距从「夹过的起点」算所以不会越拖越远、松手才提交、鼠标按键中途松开 / 窗口藏起来就回初始布局。
/// </para>
/// <para>
/// **不抢焦点**（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
/// 摆画中画的时候用户还在别的程序里操作。代价是拿不到键盘焦点，所以拖动中按 Esc 取消靠轮询系统键态
/// （这一次 Esc 会漏给下层程序，上游会吞掉——见日志里的平台差异）。出口是「松手提交 / Esc 取消 /
/// 指针 / 捕获丢失」。
/// 自己**不入镜**：<c>WDA_EXCLUDEFROMCAPTURE</c>（录制一开始本窗就藏起来了，留着只是保险）。
/// </para>
/// </remarks>
internal sealed class RecordWebcamWindow : Window
{
    private static readonly Brush SurfaceBackdrop = Frozen(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush SurfaceBorder = Frozen(Color.FromArgb(60, 0xFF, 0xFF, 0xFF));
    private static readonly Brush HandleBrush = Frozen(Color.FromArgb(150, 0, 0, 0));
    private static readonly Brush HandleHoverBrush = Frozen(Color.FromArgb(180, 0, 0, 0));
    private static readonly Brush HandlePressedBrush = Frozen(Color.FromArgb(210, 0, 0, 0));
    private static readonly Brush ToolbarSurface = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush ToolbarBorder = Frozen(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private static readonly Brush ToolbarHover = Frozen(Color.FromArgb(0x0F, 0, 0, 0));
    private static readonly Brush ToolbarPressed = Frozen(Color.FromArgb(0x1E, 0, 0, 0));
    private static readonly Brush ToolbarIcon = Frozen(Color.FromArgb(0xE6, 0, 0, 0));

    private readonly Canvas _root;
    private readonly PreviewSurface _surface;
    private readonly ResizeGrip _grip;
    private readonly Border _toolbar;
    private readonly Button[] _orientationButtons = new Button[RecordWebcamLayout.OrientationButtons];

    private Int32Rect _area;
    private Int32Rect _screen;
    private Int32Rect _preview;
    private Int32Rect _toolbarRect;
    private double _scale = 1;
    private RecordCameraShape _shape = RecordCameraShape.square;
    private DispatcherTimer? _escapeWatch;

    private const int VkEscape = 0x1B;

    private int _size = RecordOptions.DefaultCameraSize;
    private int _offsetLeft = RecordOptions.DefaultCameraOffset;
    private int _offsetBottom = RecordOptions.DefaultCameraOffset;

    private bool _interacting;
    private bool _resizing;
    private bool _changed;
    private int _originX;
    private int _originY;
    private int _initialSize;
    private int _initialLeft;
    private int _initialBottom;
    private int _initialSide;
    private int _initialFittedLeft;
    private int _initialFittedBottom;

    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    private IntPtr _hwnd;

    public RecordWebcamWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsTabStop = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;
        Cursor = Cursors.SizeAll;

        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            // 程序集限定的 pack URI：无头冒烟在另一个 exe 里反射造这个窗口，相对 URI 会找错程序集
            Source = new Uri("pack://application:,,,/LTools;component/Views/CapturePro/CaptureIcons.xaml"),
        });

        _root = new Canvas { Background = null };
        Content = _root;

        _surface = new PreviewSurface { Cursor = Cursors.SizeAll };
        _surface.MouseLeftButtonDown += (_, e) => BeginInteraction(false, e);
        _root.Children.Add(_surface);

        _grip = new ResizeGrip { Cursor = Cursors.SizeNESW };
        _grip.MouseLeftButtonDown += (_, e) => BeginInteraction(true, e);
        _root.Children.Add(_grip);

        _toolbar = BuildToolbar();
        _root.Children.Add(_toolbar);

        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) =>
        {
            if (_interacting) FinishInteraction(commit: false);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && _interacting) FinishInteraction(commit: false);
        };

        SourceInitialized += (_, _) =>
        {
            _hwnd = OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
        };
    }

    /// <summary>拖 / 缩放过程中的预览（每次移动都发；调用方据此让面板 / 合成器看到最新布局）。</summary>
    public event Action<int, int, int>? Previewed;

    /// <summary>松手时提交（<c>size</c> / <c>offsetLeft</c> / <c>offsetBottom</c>）。</summary>
    public event Action<int, int, int>? Committed;

    /// <summary>点了方向工具条上的按钮。</summary>
    public event Action<RecordWebcamAction>? OrientationRequested;

    /// <summary>正在拖 / 正在缩放（期间工具条置灰、调用方不要重摆几何）。</summary>
    internal bool IsInteracting => _interacting;

    /// <summary>画中画本体的物理矩形（冒烟用来对账）。</summary>
    internal Int32Rect PreviewPhysicalBounds => _preview;

    /// <summary>方向工具条的物理矩形。</summary>
    internal Int32Rect ToolbarPhysicalBounds => _toolbarRect;

    /// <summary>当前形状。</summary>
    internal RecordCameraShape Shape => _shape;

    /// <summary>方向按钮（冒烟用来发真实鼠标点击；顺序照 <see cref="RecordWebcamAction"/>）。</summary>
    internal Button? OrientationButton(RecordWebcamAction action) =>
        _orientationButtons[(int)action];

    /// <summary>
    /// 按「录屏区域 + 画中画参数」摆好并显示（<paramref name="screen"/> = 所在显示器的工作区，
    /// 物理像素）。参数没变就直接返回，不打断用户的拖动。
    /// </summary>
    internal void Present(Int32Rect area, int size, int offsetLeft, int offsetBottom,
        RecordCameraShape shape, Int32Rect screen, double scale)
    {
        if (RecordWebcamLayout.IsEmpty(area) || RecordWebcamLayout.IsEmpty(screen)) return;

        bool same = IsVisible
            && area == _area && size == _size
            && offsetLeft == _offsetLeft && offsetBottom == _offsetBottom
            && shape == _shape && screen == _screen && Math.Abs(scale - _scale) < 0.001;
        if (same) return;

        _area = area;
        _size = size;
        _offsetLeft = offsetLeft;
        _offsetBottom = offsetBottom;
        _shape = shape;
        _screen = screen;
        _scale = scale > 0 ? scale : 1;
        _surface.Shape = shape;

        ApplyLayout();

        if (!IsVisible) Show();
        RaiseToTop();
    }

    /// <summary>把最新一帧摄像头画面贴上去（没有新帧就保持上一帧，与上游「掉帧重复上一帧」一致）。</summary>
    internal void PushFrame(RecordCamera camera)
    {
        int side = camera.Side;
        if (side <= 0) return;

        if (_bitmap == null || _buffer == null || _bitmap.PixelWidth != side)
        {
            _buffer = new byte[side * side * 4];
            _bitmap = new WriteableBitmap(side, side, 96, 96, PixelFormats.Bgra32, null);
            _surface.Source = _bitmap;
        }

        if (!camera.TryCopyFrame(_buffer)) return;

        try
        {
            _bitmap.WritePixels(new Int32Rect(0, 0, side, side), _buffer, side * 4, 0);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "刷新画中画预览失败：" + ex.Message);
        }
    }

    /// <summary>还没拿到画面时显示的话（摄像头打不开 / 正在连）。</summary>
    internal void SetPlaceholder(string text)
    {
        if (string.Equals(_surface.Placeholder, text, StringComparison.Ordinal)) return;
        _surface.Placeholder = text;
    }

    /// <summary>藏起来（开录 / 取消 / 收尾）：顺手把进行中的拖动退回初始布局。</summary>
    internal void HidePreview()
    {
        if (_interacting) FinishInteraction(commit: false);

        _surface.Source = null;
        _bitmap = null;
        _buffer = null;
        if (IsVisible) Hide();
    }

    /// <summary>把拖动 / 缩放退回去（别处改了区域、窗口被挡住时调）。</summary>
    internal void CancelInteraction()
    {
        if (_interacting) FinishInteraction(commit: false);
    }

    /// <summary>顶回同层最前面（区域框 / 面板显示之后再顶一次，不抢焦点）。</summary>
    internal void RaiseToTop()
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "置顶画中画预览失败：" + ex.Message);
        }
    }

    // ============ 布局 ============

    /// <summary>
    /// 窗口 = 画中画 ∪ 方向工具条（工具条可能翻到下方、也可能横向被夹回屏幕里）。
    /// 子元素按这个并集左上角换算成相对坐标。
    /// </summary>
    private void ApplyLayout()
    {
        if (RecordWebcamLayout.IsEmpty(_area)) return;

        _preview = RecordWebcamLayout.PreviewRect(_area, _size, _offsetLeft, _offsetBottom);
        _toolbarRect = RecordWebcamLayout.ToolbarRect(_preview, _screen, _scale);

        var union = Union(_preview, _toolbarRect);
        if (RecordWebcamLayout.IsEmpty(union)) return;

        double scale = _scale;
        Left = union.X / scale;
        Top = union.Y / scale;
        Width = union.Width / scale;
        Height = union.Height / scale;

        Place(_surface, _preview, union);
        Place(_grip, RecordWebcamLayout.HandleRect(_preview, scale), union);
        Place(_toolbar, _toolbarRect, union);
    }

    private void Place(FrameworkElement element, Int32Rect rect, Int32Rect origin)
    {
        Canvas.SetLeft(element, (rect.X - origin.X) / _scale);
        Canvas.SetTop(element, (rect.Y - origin.Y) / _scale);
        element.Width = Math.Max(1, rect.Width / _scale);
        element.Height = Math.Max(1, rect.Height / _scale);
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (RecordWebcamLayout.IsEmpty(a)) return b;
        if (RecordWebcamLayout.IsEmpty(b)) return a;

        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    // ============ 拖动 / 缩放 ============

    private void BeginInteraction(bool resize, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _interacting || !IsVisible) return;
        e.Handled = true;

        _interacting = true;
        _resizing = resize;
        _changed = false;

        _initialSize = _size;
        _initialLeft = _offsetLeft;
        _initialBottom = _offsetBottom;
        _initialSide = RecordWebcamLayout.ResizeAnchor(_area, _size);

        var fitted = RecordWebcamLayout.FittedOffset(_area, _size, _offsetLeft, _offsetBottom);
        _initialFittedLeft = fitted.Left;
        _initialFittedBottom = fitted.Bottom;

        var (px, py) = CursorPosition();
        _originX = px;
        _originY = py;

        _grip.SetDown(resize);
        _toolbar.IsEnabled = false;
        CaptureMouse();
        StartEscapeWatch();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_interacting) return;

        // 上游：左键中途松开（在窗口外抬起）就当取消，退回初始布局
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishInteraction(commit: false);
            return;
        }

        UpdateInteraction();
        e.Handled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_interacting) return;

        UpdateInteraction();
        e.Handled = true;
        FinishInteraction(commit: true);
    }

    /// <summary>照上游 <c>updateInteraction</c>：位移 → 边距；缩放 → 边长；没变化就回到初始。</summary>
    private void UpdateInteraction()
    {
        var (px, py) = CursorPosition();
        int dx = px - _originX;
        int dy = py - _originY;
        double resizeDelta = (dx - dy) / 2.0;

        if ((dx == 0 && dy == 0) || (_resizing && Math.Abs(resizeDelta) < 0.5))
        {
            if (!_changed) return;

            _size = _initialSize;
            _offsetLeft = _initialLeft;
            _offsetBottom = _initialBottom;
        }
        else if (_resizing)
        {
            _size = RecordWebcamLayout.SizeForResize(_initialSide, dx, dy);
            _offsetLeft = _initialFittedLeft;
            _offsetBottom = _initialFittedBottom;
        }
        else
        {
            var (left, bottom) = RecordWebcamLayout.OffsetForMove(
                _area, _initialSide, _initialFittedLeft, _initialFittedBottom, dx, dy);
            _offsetLeft = left;
            _offsetBottom = bottom;
        }

        _changed = true;
        ApplyLayout();
        Previewed?.Invoke(_size, _offsetLeft, _offsetBottom);
    }

    private void FinishInteraction(bool commit)
    {
        if (!_interacting) return;

        _interacting = false;
        _resizing = false;
        StopEscapeWatch();
        _grip.SetDown(false);
        _toolbar.IsEnabled = true;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (!commit)
        {
            if (_changed)
            {
                _size = _initialSize;
                _offsetLeft = _initialLeft;
                _offsetBottom = _initialBottom;
                _changed = false;
                ApplyLayout();
                Previewed?.Invoke(_size, _offsetLeft, _offsetBottom);
            }

            return;
        }

        bool changed = _changed;
        _changed = false;
        if (!changed) return;

        Committed?.Invoke(_size, _offsetLeft, _offsetBottom);
        RaiseToTop();
    }

    private static (int X, int Y) CursorPosition() =>
        Win32.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    // ============ Esc 取消 ============

    /// <summary>
    /// 上游在拖动中按 Esc 会取消（退回初始布局）。这个窗口是 <c>WS_EX_NOACTIVATE</c>、拿不到键盘焦点，
    /// 所以拖动期间直接问系统键态；代价是这一次 Esc 会漏给下层程序（上游会吞掉），见文档里的平台差异。
    /// </summary>
    private void StartEscapeWatch()
    {
        if (_escapeWatch == null)
        {
            _escapeWatch = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _escapeWatch.Tick += (_, _) =>
            {
                if (!_interacting) return;
                if ((Win32.GetAsyncKeyState(VkEscape) & 0x8000) == 0) return;

                FinishInteraction(commit: false);
            };
        }

        _escapeWatch.Start();
    }

    private void StopEscapeWatch() => _escapeWatch?.Stop();

    // ============ 方向工具条 ============

    private Border BuildToolbar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(RecordWebcamLayout.ToolbarPadX, RecordWebcamLayout.ToolbarPadY,
                RecordWebcamLayout.ToolbarPadX, RecordWebcamLayout.ToolbarPadY),
        };

        for (int i = 0; i < _orientationButtons.Length; i++)
        {
            var action = (RecordWebcamAction)i;
            var button = new Button
            {
                Width = RecordWebcamLayout.ToolbarButton,
                Height = RecordWebcamLayout.ToolbarButton,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                IsTabStop = false,
                ToolTip = RecordWebcamLayout.ActionLabel(action),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Template = ToolbarButtonTemplate(),
                Content = MakeIcon(RecordWebcamLayout.ActionIcon(action)),
            };

            button.Click += (_, _) => OrientationRequested?.Invoke(action);

            _orientationButtons[i] = button;
            row.Children.Add(button);
        }

        var border = new Border
        {
            Background = ToolbarSurface,
            BorderBrush = ToolbarBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            SnapsToDevicePixels = true,
            Child = row,
        };

        return border;
    }

    /// <summary>24×24 的图标按钮：悬停 6% 黑、按下 12% 黑、圆角 4（antd Text 按钮的 Small 规格）。</summary>
    private static ControlTemplate ToolbarButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, ToolbarHover, "Bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, ToolbarPressed, "Bd"));
        template.Triggers.Add(pressed);

        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.45, "Bd"));
        template.Triggers.Add(disabled);

        template.Seal();
        return template;
    }

    /// <summary>图标：1024 viewBox 等比装进 14 DIP 的格子（24 的按钮里留一圈余量）。</summary>
    private FrameworkElement MakeIcon(string key)
    {
        var path = new ShapePath
        {
            Data = (Geometry)FindResource(key),
            Stretch = Stretch.None,
            Width = 1024,
            Height = 1024,
            Fill = ToolbarIcon,
            IsHitTestVisible = false,
        };

        return new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.Both,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = path,
        };
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ============ 画中画本体 ============

    /// <summary>
    /// 画中画本体：摄像头画面按方形 / 圆形裁剪（圆形照上游 <c>QRegion::Ellipse</c>），
    /// 没有画面时铺一层深灰并写一行提示——**没有摄像头也能摆位置**（上游的交互层同样一直在）。
    /// </summary>
    private sealed class PreviewSurface : FrameworkElement
    {
        private ImageSource? _source;
        private string _placeholder = "摄像头未连接";

        public ImageSource? Source
        {
            get => _source;
            set
            {
                _source = value;
                InvalidateVisual();
            }
        }

        public string Placeholder
        {
            get => _placeholder;
            set
            {
                _placeholder = value ?? "";
                InvalidateVisual();
            }
        }

        public RecordCameraShape Shape { get; set; } = RecordCameraShape.square;

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            var rect = new Rect(0, 0, width, height);
            Geometry clip = Shape == RecordCameraShape.circle
                ? new EllipseGeometry(rect)
                : new RectangleGeometry(rect);
            clip.Freeze();

            dc.PushClip(clip);
            dc.DrawRectangle(SurfaceBackdrop, null, rect);

            if (_source != null)
            {
                dc.DrawImage(_source, rect);
            }
            else if (_placeholder.Length > 0)
            {
                var text = new FormattedText(_placeholder, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 12,
                    Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                    VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = Math.Max(1, width - 8),
                    TextAlignment = TextAlignment.Center,
                };

                dc.DrawText(text, new Point(4, Math.Max(0, (height - text.Height) / 2)));
            }

            dc.Pop();

            // 摄像头画面本身没有边框，加一圈极淡的白描边，深色背景下也看得出边界
            var pen = new Pen(SurfaceBorder, 1);
            pen.Freeze();
            dc.DrawGeometry(null, pen, clip);
        }
    }

    /// <summary>
    /// 右上角的缩放圆钮（照上游 <c>ResizeButton::paintEvent</c>）：半透明黑椭圆 +
    /// 白色对角双箭头，悬停 / 按下时更深；尺寸按「32 的基准 × 实际边长」缩放。
    /// </summary>
    private sealed class ResizeGrip : FrameworkElement
    {
        private bool _hover;
        private bool _down;

        public ResizeGrip()
        {
            MouseEnter += (_, _) => SetHover(true);
            MouseLeave += (_, _) => SetHover(false);
        }

        public void SetDown(bool value)
        {
            if (_down == value) return;
            _down = value;
            InvalidateVisual();
        }

        private void SetHover(bool value)
        {
            if (_hover == value) return;
            _hover = value;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0) return;

            var fill = _down ? HandlePressedBrush : _hover ? HandleHoverBrush : HandleBrush;
            var ellipse = new EllipseGeometry(new Rect(0, 0, width, height));
            ellipse.Freeze();
            dc.DrawGeometry(fill, null, ellipse);

            double scale = Math.Min(width, height) / RecordWebcamLayout.HandleSize;
            if (scale <= 0) return;

            double cx = width / 2;
            double cy = height / 2;
            double u = 6 * scale;

            var pen = new Pen(Brushes.White, Math.Max(1, 1.8 * scale))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();

            dc.DrawLine(pen, new Point(cx - u, cy + u), new Point(cx + u, cy - u));

            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(new Point(cx, cy - u), false, false);
                ctx.LineTo(new Point(cx + u, cy - u), true, false);
                ctx.LineTo(new Point(cx + u, cy), true, false);

                ctx.BeginFigure(new Point(cx - u, cy), false, false);
                ctx.LineTo(new Point(cx - u, cy + u), true, false);
                ctx.LineTo(new Point(cx, cy + u), true, false);
            }

            head.Freeze();
            dc.DrawGeometry(null, pen, head);
        }
    }
}
