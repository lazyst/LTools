using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 冻结屏幕的取点 / 框选窗口（由 <see cref="ScreenPicker"/> 创建）。
/// </summary>
/// <remarks>
/// <para>
/// 画面是**调用时拍下的静态截图**，不是实时屏幕：用户框选时目标不会动，
/// 截下来的模板也没有 LTools 自己的窗口，匹配结果与框选内容天然一致。
/// </para>
/// <para>
/// 框选松手后**不直接确定**：选区带八个把手，可拖边 / 拖角 / 整体移动、方向键逐像素微调，
/// 再按「使用这块区域」/ Enter / 双击确认。像截图软件一样可以来回调，直到刚好框住目标。
/// </para>
/// <para>
/// 探测与吸附（Snipaste / PixPin 同款交互）：鼠标移动时高亮光标下的窗口 / 控件，
/// **单击**即选中它（拖动了就是自由框选）；滚轮在「控件 → 父容器 → 窗口」之间换层级；
/// 拖动边角时自动吸附到附近元素的边，按住 Ctrl 临时关闭。
/// </para>
/// <para>
/// 窗口盖住光标所在的那台显示器；坐标换算为「显示器左上角 + 图像像素」，
/// 与 <see cref="ScreenCapture"/> 的物理像素坐标系一致（多显示器 / 混合 DPI 都不会偏）。
/// </para>
/// </remarks>
public partial class ScreenPickerWindow : Window
{
    private const double MinSize = 4;     // 选区最小边长（DIP）
    private const double GripSize = 9;    // 把手方块尺寸（DIP）
    private const double GripHit = 8;     // 把手命中半径（DIP）
    private const double SnapDistance = 8;  // 边 / 角吸附距离（DIP）
    private const double PressSlop = 3;     // 按下后位移小于此值算「单击」而不是拖动

    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _monitor;
    private readonly bool _pointMode;
    private readonly bool _wantColor;
    private readonly List<Rectangle> _grips = new();

    // —— 窗口 / 控件探测（框选模式）——
    private readonly List<SnapBox> _windowTargets = new();   // Win32 窗口树，打开时枚举一次
    private readonly Dictionary<IntPtr, List<SnapBox>> _uiaTrees = new();   // 顶层窗口 → 整棵控件子树
    private List<SnapBox> _uiaTargets = new();               // 光标所在窗口的控件子树
    private readonly List<SnapBox> _hoverCandidates = new();
    private int _hoverIndex;
    private SnapBox? _hoverTarget;
    private Point _hoverAt = new(-9999, -9999);
    private IntPtr _uiaPending;      // 正在后台枚举的窗口
    private string _notice = "";     // 「已复制坐标」这类一次性提示
    private DispatcherTimer? _noticeTimer;

    private List<double> _snapXs = new();
    private List<double> _snapYs = new();

    private Point _pressAt;
    private bool _pressMoved;
    private SnapBox? _pressTarget;

    private Rect? _sel;          // 当前选区（窗口 DIP）
    private DragMode _drag;
    private Handle _handle;
    private Point _origin;       // 本次拖动起点
    private Rect _originRect;    // 本次拖动开始时的选区

    /// <summary>框选的屏幕矩形（物理像素，含显示器偏移）。</summary>
    public Int32Rect Area { get; private set; }

    /// <summary>框选出来的模板图（从冻结画面裁剪，未包含任何 LTools 窗口）。</summary>
    public BitmapSource? Crop { get; private set; }

    /// <summary>取点结果（物理屏幕像素）。</summary>
    public int PointX { get; private set; }
    public int PointY { get; private set; }

    /// <summary>取点处的颜色 <c>#RRGGBB</c>。</summary>
    public string Hex { get; private set; } = "";

    /// <summary>十字准线已经落到画面上（取点 / 取色都可以确认）。</summary>
    private bool _hasPoint;

    /// <summary>用户是否确认（关闭 / Esc / 右键取消时为 false）。</summary>
    public bool Confirmed { get; private set; }

    private enum DragMode { None, New, Move, Resize }

    private enum Handle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public ScreenPickerWindow(BitmapSource shot, System.Drawing.Rectangle monitor, bool pointMode, bool wantColor)
    {
        InitializeComponent();
        _shot = shot;
        _monitor = monitor;
        _pointMode = pointMode;
        _wantColor = wantColor;

        Shot.Source = shot;
        MagImage.Source = shot;
        MagImage.Width = shot.PixelWidth;
        MagImage.Height = shot.PixelHeight;

        // 按目标显示器的 DPI 把物理矩形换算成 DIP：窗口正好盖住它。
        (double scaleX, double scaleY) = MonitorScale(monitor);
        Left = monitor.Left / scaleX;
        Top = monitor.Top / scaleY;
        Width = Math.Max(1, monitor.Width / scaleX);
        Height = Math.Max(1, monitor.Height / scaleY);

        if (pointMode)
        {
            HintText.Text = wantColor ? "单击取色 · 右键 / Esc 取消" : "单击取点 · 右键 / Esc 取消";
            // 放大镜只在取色时显示：取点只要十字准线 + 坐标，132×132 的黑框会把要点的目标挡住。
            MagBox.Visibility = ShowMagnifier(pointMode, wantColor) ? Visibility.Visible : Visibility.Collapsed;
            DimPath.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintText.Text = "移动选窗口 / 控件 · 拖动自由框选 · 滚轮换层级 · 右键 / Esc 取消";
            ActionHint.Text = "拖动边角微调，方向键逐像素";
            BuildGrips();
        }

        // 显示器 DPI 与窗口当前 DPI 不一致时（把窗口挪到别的显示器），WPF 会按建议矩形重排一次，
        // 再钉一次保证仍是 1:1 覆盖。
        DpiChanged += (_, _) => PinToMonitor();
    }

    /// <summary>选区上的八个把手（框选模式下创建）。</summary>
    private void BuildGrips()
    {
        foreach (Handle h in new[]
                 {
                     Handle.TopLeft, Handle.TopRight, Handle.BottomLeft, Handle.BottomRight,
                     Handle.Top, Handle.Bottom, Handle.Left, Handle.Right,
                 })
        {
            var grip = new Rectangle
            {
                Width = GripSize,
                Height = GripSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
                Tag = h,
            };
            _grips.Add(grip);
            Layer.Children.Add(grip);
        }
    }

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PinToMonitor();
        Activate();
        Focus();

        UpdateLayout();
        PlaceHint();

        if (_pointMode)
        {
            // 打开时先把十字线放到用户按下按钮前的鼠标处（静态画面里它就在那儿）。
            if (Win32.GetCursorPos(out var pt) && TryToDip(pt, out var dip))
                UpdateCrosshair(Clamp(dip));
        }
        else
        {
            // 窗口树枚举很快（几百个句柄），打开时一次即可；UIA 的那一层在悬停时按需补。
            try
            {
                _windowTargets.AddRange(ScreenSnapTargets.FromWindows(_monitor));
                RebuildSnapLines();
            }
            catch { /* 探测失败不影响手动框选 */ }

            UpdateChrome();

            // 打开时鼠标本来就停在某个窗口上：先算一次悬停，省得用户要动一下才看见高亮。
            if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
                UpdateHover(Clamp(dip));
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 拖动中按 Esc 只放弃这一次拖动（和 Snipaste / Quicker 一致），再按一次才退出框选。
            if (_drag != DragMode.None) CancelDrag();
            else Cancel();
            e.Handled = true;
            return;
        }

        if (_pointMode) { if (e.Key == Key.Enter && _hasPoint) { ConfirmPoint(); e.Handled = true; } return; }

        // Ctrl+A：整屏（当前显示器）。很多人用惯了截图工具的「截全屏」。
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            _drag = DragMode.None;
            _sel = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
            UpdateChrome();
            e.Handled = true;
            return;
        }

        // P：把当前选区（没选区就光标位置）复制成 x,y,w,h，方便填到别处。
        if (e.Key == Key.P && (Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            CopySelection();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (_sel is Rect enterSel && IsValid(enterSel))
            {
                ConfirmRegion(enterSel);
            }
            else if (_hoverTarget is SnapBox hover)
            {
                // 还没框选时按 Enter = 选中当前高亮的窗口 / 控件，再按一次才确定
                _sel = ToDipRect(hover.Bounds);
                UpdateChrome();
            }
            e.Handled = true;
            return;
        }

        if (_sel is not Rect sel || _drag != DragMode.None) return;

        // 方向键逐像素微调；按住 Shift 每次 10px。
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default,
        };
        if (delta == default) return;

        _sel = MoveTo(sel, delta);
        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>放弃当前拖动：正在拖新选区就清掉，拖把手 / 移动就还原。</summary>
    private void CancelDrag()
    {
        _drag = DragMode.None;
        _handle = Handle.None;
        _pressTarget = null;
        _sel = _sel is Rect s && IsValid(s) && _originRect != default ? _originRect : null;
        if (_sel is Rect back && (back.Width < MinSize || back.Height < MinSize)) _sel = null;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        Cursor = Cursors.Arrow;
        UpdateChrome();
    }

    /// <summary>复制选区（<c>x,y,w,h</c>）或光标位置（<c>x,y</c>）到剪贴板。</summary>
    private void CopySelection()
    {
        string text;
        if (_sel is Rect s && IsValid(s))
        {
            var (x1, y1) = ToImage(s.TopLeft);
            var (x2, y2) = ToImage(s.BottomRight);
            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{_monitor.X + x},{_monitor.Y + y},{Math.Abs(x2 - x1) + 1},{Math.Abs(y2 - y1) + 1}");
        }
        else if (Win32.GetCursorPos(out var cursor))
        {
            text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{cursor.X},{cursor.Y}");
        }
        else return;

        try { Clipboard.SetText(text); }
        catch { return; }   // 剪贴板被别的程序占着：不打断框选

        ShowNotice($"已复制 {text}");
    }

    /// <summary>顶部提示临时换成一句话，2 秒后恢复。</summary>
    private void ShowNotice(string text)
    {
        _notice = text;
        UpdateChrome();

        _noticeTimer ??= new DispatcherTimer();
        _noticeTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(2);
        _noticeTimer.Tick -= NoticeElapsed;
        _noticeTimer.Tick += NoticeElapsed;
        _noticeTimer.Start();
    }

    private void NoticeElapsed(object? sender, EventArgs e)
    {
        _noticeTimer?.Stop();
        _notice = "";
        UpdateChrome();
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancel();

    private void Cancel()
    {
        Confirmed = false;
        Close();
    }

    // ============ 鼠标 ============
    //
    // 全部用窗口级 Preview 事件：鼠标按下后 WPF 会把捕获挂到窗口上（Mouse.Captured == 本窗口），
    // 之后的 MouseMove 只路由到窗口，挂在 Canvas 上的 MouseMove 收不到——框选会「按下就卡住」。

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 底部确认栏是普通按钮：点它不能当成开始框选。
        if (IsOnActionBar(e.OriginalSource as DependencyObject)) return;

        Point p = Clamp(e.GetPosition(Layer));

        if (_pointMode)
        {
            UpdateCrosshair(p);
            ConfirmPoint();
            e.Handled = true;
            return;
        }

        // 双击选区内部 = 确定（和大多数截图工具一致）。
        if (e.ClickCount == 2 && _sel is Rect dbl && dbl.Contains(p))
        {
            ConfirmRegion(dbl);
            e.Handled = true;
            return;
        }

        Handle hit = HitTest(p);
        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;

        if (hit != Handle.None)
        {
            _drag = hit == Handle.Move ? DragMode.Move : DragMode.Resize;
            _handle = hit;
            _originRect = _sel ?? default;
        }
        else
        {
            // 空白处按下：先按自由拖动开始；松手时若几乎没动、且光标下有窗口 / 控件，就选中它。
            _pressTarget = _hoverTarget;
            _drag = DragMode.New;
            _handle = Handle.None;
            _originRect = _sel ?? default;   // Esc 放弃这次拖动时还原成原来的选区
            _sel = new Rect(p, p);
            SetHover(null);
        }

        _origin = p;

        // 吸附线只取「按下时最上层那个窗口」的边：后台窗口的边不该把框吸走。
        // 拖动期间不再重算，保证手感稳定（Ctrl 可以临时关掉吸附）。
        RebuildSnapLines();

        Cursor = _drag == DragMode.New ? Cursors.Cross : CursorFor(_handle);
        CaptureMouse();
        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point p = Clamp(e.GetPosition(Layer));

        if (_pointMode) { UpdateCrosshair(p); return; }

        if (_drag == DragMode.None)
        {
            Cursor = CursorFor(HitTest(p));
            UpdateHover(p);
            return;
        }

        if (!_pressMoved &&
            (Math.Abs(p.X - _pressAt.X) > PressSlop || Math.Abs(p.Y - _pressAt.Y) > PressSlop))
            _pressMoved = true;

        switch (_drag)
        {
            case DragMode.New:
            {
                // 拖出来的边角吸附到附近窗口 / 控件的边（按住 Ctrl 临时关闭）；按住 Shift 锁正方形。
                Point end = SnapPoint(p);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) end = SquareOf(_origin, end);
                _sel = RectFrom(_origin, end);
                break;
            }
            case DragMode.Move:
                _sel = SnapMove(MoveTo(_originRect, p - _origin));
                break;
            case DragMode.Resize:
                _sel = SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle);
                break;
        }

        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        _drag = DragMode.None;
        _handle = Handle.None;
        ReleaseMouseCapture();

        // 几乎没移动 = 单击：选中光标下的窗口 / 控件（和 Snipaste / PixPin 的「点一下即选中」一致）。
        // 拖动了就是自由框选，不抢。
        if (!_pressMoved && _pressTarget is SnapBox target)
        {
            _sel = ToDipRect(target.Bounds);
        }
        else if (!IsValid(_sel))
        {
            // 误点 / 太小：清掉重来，不关闭窗口。
            _sel = null;
        }

        _pressTarget = null;
        Cursor = Cursors.Arrow;
        UpdateChrome();
        e.Handled = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (IsValid(_sel)) ConfirmRegion(_sel!.Value);
    }

    /// <summary>事件源是否来自底部确认栏（按钮 / 它的父 Border）。</summary>
    private bool IsOnActionBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ActionBar)) return true;
            if (source is Button) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ============ 取点 ============

    /// <summary>
    /// 放大镜只在「屏幕取色」时显示：取点只要十字准线 + 坐标，
    /// 那个 132×132 的黑框会压住要点的目标（纯判定，冒烟可直接断言）。
    /// </summary>
    internal static bool ShowMagnifier(bool pointMode, bool wantColor) => pointMode && wantColor;

    /// <summary>信息行：取点只报坐标；取色把色值放在前面（纯函数，冒烟可直接断言）。</summary>
    internal static string PointInfoText(bool wantColor, string hex, int x, int y) =>
        wantColor ? $"{hex}   {x}, {y}" : $"{x}, {y}";

    private void UpdateCrosshair(Point dip)
    {
        var (ix, iy) = ToImage(dip);

        CrossH.Visibility = Visibility.Visible;
        CrossV.Visibility = Visibility.Visible;
        CrossH.X1 = 0; CrossH.X2 = ActualWidth;
        CrossH.Y1 = CrossH.Y2 = dip.Y;
        CrossV.Y1 = 0; CrossV.Y2 = ActualHeight;
        CrossV.X1 = CrossV.X2 = dip.X;

        Hex = SampleHex(ix, iy);
        PointX = _monitor.X + ix;
        PointY = _monitor.Y + iy;
        _hasPoint = true;

        InfoText.Text = PointInfoText(_wantColor, Hex, PointX, PointY);
        PlaceInfo(dip);
        if (ShowMagnifier(_pointMode, _wantColor)) PlaceMagnifier(dip, ix, iy);
    }

    private void ConfirmPoint()
    {
        if (!_hasPoint) return;
        Confirmed = true;
        Close();
    }

    // ============ 框选 ============

    private void ConfirmRegion(Rect dipRect)
    {
        var (x1, y1) = ToImage(dipRect.TopLeft);
        var (x2, y2) = ToImage(dipRect.BottomRight);
        int x = Math.Min(x1, x2);
        int y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1;
        int h = Math.Abs(y2 - y1) + 1;

        // 至少 2×2：OpenCV 模板再小就没意义了。
        w = Math.Min(w, _shot.PixelWidth - x);
        h = Math.Min(h, _shot.PixelHeight - y);
        if (w < 2 || h < 2) return;

        var crop = new CroppedBitmap(_shot, new Int32Rect(x, y, w, h));
        crop.Freeze();

        Crop = crop;
        Area = new Int32Rect(_monitor.X + x, _monitor.Y + y, w, h);
        Confirmed = true;
        Close();
    }

    /// <summary>按当前选区重画压暗层、选区框、悬停高亮、把手、尺寸提示与确认栏。</summary>
    private void UpdateChrome()
    {
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        bool hasSelection = _sel is Rect ss && ss.Width >= 0.5 && ss.Height >= 0.5;
        Rect sel = hasSelection ? _sel!.Value : default;

        // 悬停高亮只在「还没选区」时出现；有选区时用户在看边角，不需要再跳候选。
        Rect? hover = null;
        if (!hasSelection && _hoverTarget is SnapBox hb)
        {
            var hr = ToDipRect(hb.Bounds);
            if (hr.Width >= 2 && hr.Height >= 2 && full.IntersectsWith(hr)) hover = hr;
        }

        // 压暗层用 EvenOdd 挖洞：选区（或悬停命中的窗口 / 控件）保持原亮度。
        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        if (hasSelection) geo.Children.Add(new RectangleGeometry(sel));
        else if (hover is Rect hh) geo.Children.Add(new RectangleGeometry(hh));
        DimPath.Data = geo;
        DimPath.Visibility = Visibility.Visible;

        // 选区
        Selection.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        SelectionShadow.Visibility = Selection.Visibility;
        if (hasSelection)
        {
            Canvas.SetLeft(Selection, sel.X);
            Canvas.SetTop(Selection, sel.Y);
            Selection.Width = sel.Width;
            Selection.Height = sel.Height;
            Canvas.SetLeft(SelectionShadow, sel.X);
            Canvas.SetTop(SelectionShadow, sel.Y);
            SelectionShadow.Width = sel.Width;
            SelectionShadow.Height = sel.Height;
        }

        // 悬停候选
        if (hover is Rect hr2) DrawDetect(hr2, _hoverTarget!.Value.Label);
        else
        {
            DetectRect.Visibility = Visibility.Collapsed;
            DetectLabel.Visibility = Visibility.Collapsed;
        }

        if (!hasSelection)
        {
            foreach (var grip in _grips) grip.Visibility = Visibility.Collapsed;
            ConfirmBtn.IsEnabled = false;
            ActionBar.Visibility = Visibility.Collapsed;   // 没选区时不挡任何拖动（提示在顶部不拦鼠标）
            InfoBox.Visibility = Visibility.Collapsed;
            ActionHint.Text = "拖动边角微调，方向键逐像素";
            RefreshHint();
            return;
        }

        bool valid = IsValid(sel);
        bool showBar = valid && _drag == DragMode.None;   // 拖动过程中先别跳出来，松手后再给按钮
        PositionGrips(sel, valid);
        ConfirmBtn.IsEnabled = showBar;
        ActionBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = valid ? "拖动边角微调，方向键逐像素" : "继续拖大一点";

        bool barAbove = showBar && PositionActionBar(sel);
        ShowSizeInfo(sel, barAbove);
        RefreshHint();
    }

    /// <summary>
    /// 顶部提示行：把「当前能做什么」和「控件识别到了没有」放在一句话里，
    /// 识别中 / 识别不到都要说出来——静默降级会让人以为这程序不支持吸附。
    /// </summary>
    private void RefreshHint()
    {
        if (_pointMode)
        {
            HintText.Text = _wantColor ? "单击取色 · 右键 / Esc 取消" : "单击取点 · 右键 / Esc 取消";
            return;
        }

        if (_notice.Length > 0)
        {
            HintText.Text = _notice;
            return;
        }

        if (_sel is Rect s && IsValid(s))
        {
            HintText.Text = "拖动边角微调 · 方向键 1px（Shift 10px）· Ctrl+A 整屏 · P 复制坐标 · 双击 / Enter 确定";
            return;
        }

        string text = _windowTargets.Count > 0
            ? "移动选窗口 / 控件 · 拖动自由框选 · 滚轮换层级 · 右键 / Esc 取消"
            : "拖动框选范围 · 右键 / Esc 取消";

        if (_uiaPending != IntPtr.Zero)
            text += " · 识别控件中…";
        else if (_uiaTargets.Count == 0 && _windowTargets.Count > 0)
            text += " · 未检测到控件，可直接拖拽框选";

        HintText.Text = text;
    }

    /// <summary>
    /// 把确认栏贴到选区下方、与选区右边缘对齐（主流截图工具的摆法：Snipaste / PixPin / QQ 都是
    /// 「选框外下方、靠右」）。下方放不下就翻到选区上方；返回 true 表示放在了上方。
    /// </summary>
    private bool PositionActionBar(Rect sel)
    {
        // 确认栏在 Canvas 里，按内容尺寸测量；量不到就先不动（下一帧再排）。
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = ActionBar.DesiredSize.Width;
        double h = ActionBar.DesiredSize.Height;
        if (w <= 1 || h <= 1) return false;

        double left = Math.Clamp(sel.Right - w, 8, Math.Max(8, ActualWidth - w - 8));
        double below = sel.Bottom + 8;
        bool above = below + h > ActualHeight - 8;
        double top = above ? sel.Top - h - 8 : below;

        // 上下都放不下（选区几乎占满整屏）：贴着选区内部下方，至少不跑到屏幕外。
        if (top < 8) top = Math.Max(8, sel.Bottom - h - 8);

        Canvas.SetLeft(ActionBar, left);
        Canvas.SetTop(ActionBar, Math.Clamp(top, 8, Math.Max(8, ActualHeight - h - 8)));
        return above;
    }

    /// <summary>悬停高亮：琥珀色框 + 左上角标签（控件类型 · 名称 · 尺寸）。</summary>
    private void DrawDetect(Rect r, string label)
    {
        DetectRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(DetectRect, r.X);
        Canvas.SetTop(DetectRect, r.Y);
        DetectRect.Width = r.Width;
        DetectRect.Height = r.Height;

        var (x1, y1) = ToImage(r.TopLeft);
        var (x2, y2) = ToImage(r.BottomRight);
        int w = Math.Abs(x2 - x1) + 1;
        int h = Math.Abs(y2 - y1) + 1;
        DetectText.Text = $"{label}   {w} × {h}";

        DetectLabel.Visibility = Visibility.Visible;
        DetectLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double lw = DetectLabel.DesiredSize.Width;
        double lh = DetectLabel.DesiredSize.Height;

        double left = Math.Clamp(r.X, 8, Math.Max(8, ActualWidth - lw - 8));
        double top = r.Y - lh - 6;
        if (top < 8) top = r.Y + 6;                    // 上方贴边就放进框内侧
        if (top + lh > ActualHeight - 8) top = r.Bottom - lh - 6;
        Canvas.SetLeft(DetectLabel, left);
        Canvas.SetTop(DetectLabel, Math.Clamp(top, 8, Math.Max(8, ActualHeight - lh - 8)));
    }

    private void PositionGrips(Rect s, bool visible)
    {
        if (_grips.Count == 0) return;

        foreach (var grip in _grips)
        {
            if (!visible || grip.Tag is not Handle h)
            {
                grip.Visibility = Visibility.Collapsed;
                continue;
            }

            Point at = GripPoint(s, h);
            Canvas.SetLeft(grip, at.X - GripSize / 2);
            Canvas.SetTop(grip, at.Y - GripSize / 2);
            grip.Visibility = Visibility.Visible;
        }
    }

    /// <summary>尺寸提示（物理像素，就是最终会存进配置的数字）；主流放在选区左上角外侧。</summary>
    private void ShowSizeInfo(Rect s, bool barAbove)
    {
        var (x1, y1) = ToImage(new Point(s.X, s.Y));
        var (x2, y2) = ToImage(new Point(s.X + s.Width, s.Y + s.Height));
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Abs(x2 - x1) + 1, h = Math.Abs(y2 - y1) + 1;

        InfoText.Text = $"{w} × {h} px   {_monitor.X + x}, {_monitor.Y + y}";
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double iw = InfoBox.DesiredSize.Width;
        double ih = InfoBox.DesiredSize.Height;

        double left = Math.Clamp(s.X, 8, Math.Max(8, ActualWidth - iw - 8));
        double top = s.Y - ih - 6;                 // 选区上方
        if (top < 8 || barAbove) top = s.Y + 6;    // 上方没空间 / 被确认栏占了 → 放进选区左上角
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, Math.Clamp(top, 8, Math.Max(8, ActualHeight - ih - 8)));
    }

    // ============ 窗口 / 控件探测与吸附 ============

    /// <summary>滚轮在候选层级之间切换：向上滚 = 父级（更大范围），向下滚 = 更细的控件。</summary>
    private void Window_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (_pointMode || IsValid(_sel) || _hoverCandidates.Count < 2) return;

        int step = e.Delta > 0 ? 1 : -1;
        _hoverIndex = (_hoverIndex + step + _hoverCandidates.Count) % _hoverCandidates.Count;
        SetHover(_hoverCandidates[_hoverIndex]);
        e.Handled = true;
    }

    /// <summary>光标移动时更新悬停候选（窗口树即时，控件子树按窗口缓存、异步取一次）。</summary>
    private void UpdateHover(Point p)
    {
        if (IsValid(_sel)) { SetHover(null); return; }

        var screen = ToScreen(p);
        IntPtr top = ScreenSnapTargets.TopWindowAtPoint(
            new WindowInteropHelper(this).Handle, screen);

        // 第一次进这个窗口：后台把它的控件子树取回来（之后移动鼠标纯本地判断）。
        EnsureUiaTree(top);

        _hoverCandidates.Clear();
        var seen = new HashSet<(int X, int Y, int W, int H)>();

        // 只保留最上层那个窗口及其子控件：窗口树里也包含被遮住的后台窗口，
        // 不过滤的话光标处可能挑中最上层看不见的小控件。
        void Collect(List<SnapBox> boxes)
        {
            foreach (var box in boxes)
            {
                if (!box.Bounds.Contains(screen) || !OwnedBy(box, top)) continue;
                // Win32 窗口与 UIA 根元素常常是同一个矩形：只留一个，免得滚轮白转一圈。
                if (!seen.Add((box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height))) continue;
                _hoverCandidates.Add(box);
            }
        }

        Collect(_windowTargets);
        Collect(_uiaTargets);

        if (_hoverCandidates.Count == 0)
        {
            SetHover(null);
            return;
        }

        _hoverCandidates.Sort((a, b) => a.Area.CompareTo(b.Area));

        // 光标在同一处停留时不重置层级（滚轮选好的那一级要留住）
        if (Math.Abs(p.X - _hoverAt.X) > PressSlop || Math.Abs(p.Y - _hoverAt.Y) > PressSlop)
        {
            _hoverIndex = 0;
            _hoverAt = p;
        }
        if (_hoverIndex >= _hoverCandidates.Count) _hoverIndex = 0;
        SetHover(_hoverCandidates[_hoverIndex]);
    }

    private void SetHover(SnapBox? box)
    {
        if (Nullable.Equals(_hoverTarget, box)) return;
        _hoverTarget = box;
        UpdateChrome();
    }

    /// <summary>候选是否属于光标下最上层的那个窗口（拿不到顶层窗口时不过滤）。</summary>
    private static bool OwnedBy(SnapBox box, IntPtr top) =>
        top == IntPtr.Zero || box.Owner == IntPtr.Zero || box.Owner == top;

    /// <summary>
    /// 按窗口缓存控件子树：UIA 是跨进程调用，慢应用一次能花几十到几百毫秒，
    /// 所以整棵树只在**第一次进入某个窗口**时后台取一次，之后鼠标移动只是本地包含判断。
    /// </summary>
    private void EnsureUiaTree(IntPtr top)
    {
        if (top == IntPtr.Zero) return;

        if (_uiaTrees.TryGetValue(top, out var cached))
        {
            if (!ReferenceEquals(_uiaTargets, cached))
            {
                _uiaTargets = cached;
                RebuildSnapLines();
            }
            return;
        }

        if (_uiaPending == top) return;   // 已经有人在查这个窗口了
        _uiaPending = top;

        var monitor = _monitor;
        Task.Run(() => ScreenSnapTargets.FromWindowTree(top, monitor))
            .ContinueWith(t =>
            {
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_uiaPending != top) return;
                    _uiaPending = IntPtr.Zero;

                    if (t.Status != TaskStatus.RanToCompletion)
                    {
                        UpdateChrome();
                        return;
                    }

                    // 空结果也缓存：说明这个程序不暴露 UIA，别再反复问它。
                    if (_uiaTrees.Count > 40) _uiaTrees.Clear();
                    _uiaTrees[top] = t.Result;

                    if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
                    {
                        var at = Clamp(dip);
                        if (ScreenSnapTargets.TopWindowAtPoint(
                                new WindowInteropHelper(this).Handle, ToScreen(at)) == top)
                        {
                            _uiaTargets = t.Result;
                            RebuildSnapLines();
                            if (!IsValid(_sel)) UpdateHover(at);
                        }
                    }

                    UpdateChrome();
                }));
            });
    }

    /// <summary>收集吸附值（DIP）：只取光标下最上层窗口及子控件的边 + 屏幕四边。</summary>
    private void RebuildSnapLines()
    {
        var xs = new List<double>(_windowTargets.Count * 2 + 16);
        var ys = new List<double>(_windowTargets.Count * 2 + 16);

        IntPtr top = IntPtr.Zero;
        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out _))
            top = ScreenSnapTargets.TopWindowAtPoint(
                new WindowInteropHelper(this).Handle, new System.Drawing.Point(cursor.X, cursor.Y));

        void Add(System.Drawing.Rectangle b)
        {
            var r = ToDipRect(b);
            if (r.Width < 1 || r.Height < 1) return;
            xs.Add(r.Left); xs.Add(r.Right);
            ys.Add(r.Top); ys.Add(r.Bottom);
        }

        // 后台窗口的边也吸的话，框会被「看不见的窗口」拽住——Quicker / Snipaste 都只吸看得见的那个。
        foreach (var box in _windowTargets)
            if (OwnedBy(box, top)) Add(box.Bounds);
        foreach (var box in _uiaTargets)
            if (OwnedBy(box, top)) Add(box.Bounds);
        xs.Add(0); xs.Add(ActualWidth);
        ys.Add(0); ys.Add(ActualHeight);

        _snapXs = Dedup(xs);
        _snapYs = Dedup(ys);
    }

    private static List<double> Dedup(List<double> values)
    {
        values.Sort();
        var result = new List<double>(values.Count);
        foreach (double v in values)
            if (result.Count == 0 || Math.Abs(v - result[^1]) > 0.5) result.Add(v);
        return result;
    }

    /// <summary>是否启用吸附：有候选边，且用户没有按住 Ctrl 临时关闭。</summary>
    private bool SnapEnabled =>
        _snapXs.Count > 0 && (Keyboard.Modifiers & ModifierKeys.Control) == 0;

    private Point SnapPoint(Point p) => SnapEnabled
        ? new Point(SnapValue(p.X, _snapXs), SnapValue(p.Y, _snapYs))
        : p;

    private Rect SnapMove(Rect r)
    {
        if (!SnapEnabled) return r;
        double dx = NearestDelta(new[] { r.Left, r.Right }, _snapXs);
        double dy = NearestDelta(new[] { r.Top, r.Bottom }, _snapYs);
        return new Rect(r.X + dx, r.Y + dy, r.Width, r.Height);
    }

    private Rect SnapResize(Rect r, Handle handle)
    {
        if (!SnapEnabled) return r;

        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        if (handle is Handle.Left or Handle.TopLeft or Handle.BottomLeft) left = SnapValue(left, _snapXs);
        if (handle is Handle.Right or Handle.TopRight or Handle.BottomRight) right = SnapValue(right, _snapXs);
        if (handle is Handle.Top or Handle.TopLeft or Handle.TopRight) top = SnapValue(top, _snapYs);
        if (handle is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight) bottom = SnapValue(bottom, _snapYs);

        if (right - left < MinSize || bottom - top < MinSize) return r;
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double SnapValue(double v, List<double> lines)
    {
        double best = v, bestDelta = SnapDistance;
        foreach (double line in lines)
        {
            double d = Math.Abs(line - v);
            if (d < bestDelta) { bestDelta = d; best = line; }
        }
        return best;
    }

    private static double NearestDelta(double[] values, List<double> lines)
    {
        double bestDelta = 0, best = SnapDistance;
        foreach (double v in values)
            foreach (double line in lines)
            {
                double d = Math.Abs(line - v);
                if (d < best) { best = d; bestDelta = line - v; }
            }
        return bestDelta;
    }

    // ============ 坐标 ============

    /// <summary>窗口 DIP → 屏幕物理像素（吸附目标用物理像素记录）。</summary>
    private System.Drawing.Point ToScreen(Point dip)
    {
        var (ix, iy) = ToImage(dip);
        return new System.Drawing.Point(_monitor.X + ix, _monitor.Y + iy);
    }

    /// <summary>屏幕物理像素矩形 → 窗口 DIP。</summary>
    private Rect ToDipRect(System.Drawing.Rectangle b)
    {
        double sx = ActualWidth > 0 ? ActualWidth / _shot.PixelWidth : 1;
        double sy = ActualHeight > 0 ? ActualHeight / _shot.PixelHeight : 1;
        return new Rect(
            (b.X - _monitor.X) * sx,
            (b.Y - _monitor.Y) * sy,
            b.Width * sx,
            b.Height * sy);
    }

    // ============ 选区计算 ============

    private bool IsValid(Rect? r) => r is Rect s && s.Width >= MinSize && s.Height >= MinSize;

    private Handle HitTest(Point p)
    {
        if (_sel is not Rect s) return Handle.None;

        foreach (var h in new[]
                 {
                     Handle.TopLeft, Handle.TopRight, Handle.BottomLeft, Handle.BottomRight,
                     Handle.Top, Handle.Bottom, Handle.Left, Handle.Right,
                 })
        {
            Point at = GripPoint(s, h);
            if (Math.Abs(p.X - at.X) <= GripHit && Math.Abs(p.Y - at.Y) <= GripHit) return h;
        }

        return s.Contains(p) ? Handle.Move : Handle.None;
    }

    private static Point GripPoint(Rect r, Handle h)
    {
        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;
        return h switch
        {
            Handle.TopLeft => new Point(r.Left, r.Top),
            Handle.TopRight => new Point(r.Right, r.Top),
            Handle.BottomLeft => new Point(r.Left, r.Bottom),
            Handle.BottomRight => new Point(r.Right, r.Bottom),
            Handle.Top => new Point(cx, r.Top),
            Handle.Bottom => new Point(cx, r.Bottom),
            Handle.Left => new Point(r.Left, cy),
            Handle.Right => new Point(r.Right, cy),
            _ => new Point(cx, cy),
        };
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        Handle.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private Rect MoveTo(Rect r, Vector delta)
    {
        double x = Math.Clamp(r.X + delta.X, 0, Math.Max(0, ActualWidth - r.Width));
        double y = Math.Clamp(r.Y + delta.Y, 0, Math.Max(0, ActualHeight - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    private Rect ResizeFrom(Rect r, Handle h, Vector d)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

        if (h is Handle.Left or Handle.TopLeft or Handle.BottomLeft) left += d.X;
        if (h is Handle.Right or Handle.TopRight or Handle.BottomRight) right += d.X;
        if (h is Handle.Top or Handle.TopLeft or Handle.TopRight) top += d.Y;
        if (h is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight) bottom += d.Y;

        left = Math.Clamp(left, 0, ActualWidth);
        right = Math.Clamp(right, 0, ActualWidth);
        top = Math.Clamp(top, 0, ActualHeight);
        bottom = Math.Clamp(bottom, 0, ActualHeight);

        // 小于最小尺寸时保持上一次的选区，避免框被拖成 0。
        if (right - left < MinSize || bottom - top < MinSize) return r;

        return new Rect(left, top, right - left, bottom - top);
    }

    // ============ 布局 / 换算 ============

    /// <summary>
    /// 按物理像素把窗口钉死在显示器矩形上。
    /// 混合 DPI 下 WPF 的 DIP→像素换算可能差几个像素（主屏 100%、副屏 150% 时尤其明显），
    /// 这里用 Win32 再确认一次，保证冻结画面与屏幕 1:1 对齐、取点不偏移。
    /// </summary>
    private void PinToMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(hwnd, Win32.HwndTopmost,
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, Win32.SwpNoactivate);
    }

    private void PlaceHint()
    {
        HintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = HintBox.DesiredSize.Width;
        Canvas.SetLeft(HintBox, Math.Max(12, (ActualWidth - w) / 2));
        Canvas.SetTop(HintBox, 26);
    }

    private void PlaceInfo(Point dip)
    {
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = InfoBox.DesiredSize.Width;
        double h = InfoBox.DesiredSize.Height;
        double x = dip.X + 18;
        double y = dip.Y + 20;
        if (x + w > ActualWidth - 8) x = dip.X - w - 18;
        if (y + h > ActualHeight - 8) y = dip.Y - h - 20;
        Canvas.SetLeft(InfoBox, Math.Max(8, x));
        Canvas.SetTop(InfoBox, Math.Max(8, y));
    }

    private void PlaceMagnifier(Point dip, int imageX, int imageY)
    {
        const double zoom = 8;
        const double box = 132;
        const double inner = 64;    // 放大镜中心（132 - 2px 边框 → 128，中心 64）

        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(zoom, zoom));
        transform.Children.Add(new TranslateTransform(inner - imageX * zoom, inner - imageY * zoom));
        MagImage.RenderTransform = transform;

        double x = dip.X + 22;
        double y = dip.Y + 22;
        if (x + box > ActualWidth - 8) x = dip.X - box - 22;
        if (y + box > ActualHeight - 8) y = dip.Y - box - 22;
        Canvas.SetLeft(MagBox, Math.Max(8, x));
        Canvas.SetTop(MagBox, Math.Max(8, y));
        Canvas.SetZIndex(MagBox, 10);
        MagBox.Visibility = Visibility.Visible;
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, ActualWidth - 1)),
        Math.Clamp(p.Y, 0, Math.Max(0, ActualHeight - 1)));

    private static Rect RectFrom(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>按住 Shift 时把拖拽终点修正成正方形（保持拖拽方向）。</summary>
    private static Point SquareOf(Point origin, Point end)
    {
        double dx = end.X - origin.X;
        double dy = end.Y - origin.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (side < 1) return end;
        return new Point(origin.X + (dx < 0 ? -side : side), origin.Y + (dy < 0 ? -side : side));
    }

    /// <summary>DIP → 冻结画面像素（画面就是显示器的 1:1 物理像素）。</summary>
    private (int X, int Y) ToImage(Point dip)
    {
        double sx = ActualWidth > 0 ? _shot.PixelWidth / ActualWidth : 1;
        double sy = ActualHeight > 0 ? _shot.PixelHeight / ActualHeight : 1;
        int x = (int)Math.Round(dip.X * sx);
        int y = (int)Math.Round(dip.Y * sy);
        return (Math.Clamp(x, 0, _shot.PixelWidth - 1), Math.Clamp(y, 0, _shot.PixelHeight - 1));
    }

    /// <summary>物理屏幕坐标 → 窗口 DIP。</summary>
    private bool TryToDip(Win32.Point screen, out Point dip)
    {
        dip = default;
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        double sx = (double)ActualWidth / _shot.PixelWidth;
        double sy = (double)ActualHeight / _shot.PixelHeight;
        dip = new Point((screen.X - _monitor.X) * sx, (screen.Y - _monitor.Y) * sy);
        return true;
    }

    private string SampleHex(int x, int y)
    {
        try
        {
            var one = new CroppedBitmap(_shot, new Int32Rect(x, y, 1, 1));
            var px = new byte[4];
            one.CopyPixels(px, 4, 0);
            return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}";   // Bgra32
        }
        catch
        {
            return "#000000";
        }
    }

    /// <summary>取显示器有效 DPI（换算成 WPF DIP 的缩放比）；取不到时按 100%。</summary>
    internal static (double X, double Y) MonitorScale(System.Drawing.Rectangle monitor)
    {
        try
        {
            var center = new Win32.Point
            {
                X = monitor.X + monitor.Width / 2,
                Y = monitor.Y + monitor.Height / 2,
            };
            var hmon = Win32.MonitorFromPoint(center, Win32.MonitorDefaultToNearest);
            if (hmon != IntPtr.Zero &&
                Win32.GetDpiForMonitor(hmon, Win32.MdtEffectiveDpi, out uint dx, out uint dy) == 0 &&
                dx > 0 && dy > 0)
                return (dx / 96.0, dy / 96.0);
        }
        catch { /* shcore 不可用（理论上 Win10+ 都有） */ }
        return (1.0, 1.0);
    }
}
