using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
/// 窗口盖住光标所在的那台显示器；坐标换算为「显示器左上角 + 图像像素」，
/// 与 <see cref="ScreenCapture"/> 的物理像素坐标系一致（多显示器 / 混合 DPI 都不会偏）。
/// </para>
/// </remarks>
public partial class ScreenPickerWindow : Window
{
    private const double MinSize = 4;     // 选区最小边长（DIP）
    private const double GripSize = 9;    // 把手方块尺寸（DIP）
    private const double GripHit = 8;     // 把手命中半径（DIP）

    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _monitor;
    private readonly bool _pointMode;
    private readonly bool _wantColor;
    private readonly List<Rectangle> _grips = new();

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
            MagBox.Visibility = Visibility.Visible;
            DimPath.Visibility = Visibility.Collapsed;
            ActionBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintText.Text = "拖动框选范围 · 右键 / Esc 取消";
            ActionHint.Text = "拖动框选要匹配的区域";
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
            UpdateChrome();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); return; }

        if (e.Key == Key.Enter)
        {
            if (_pointMode)
            {
                if (Hex.Length > 0) ConfirmPoint();
            }
            else if (IsValid(_sel))
            {
                ConfirmRegion(_sel!.Value);
            }
            e.Handled = true;
            return;
        }

        if (_pointMode || _sel is not Rect sel || _drag != DragMode.None) return;

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
        if (hit != Handle.None)
        {
            _drag = hit == Handle.Move ? DragMode.Move : DragMode.Resize;
            _handle = hit;
            _originRect = _sel ?? default;
        }
        else
        {
            // 空白处按下：重新拖一块。
            _drag = DragMode.New;
            _handle = Handle.None;
            _sel = new Rect(p, p);
        }

        _origin = p;
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
            return;
        }

        switch (_drag)
        {
            case DragMode.New:
                _sel = RectFrom(_origin, p);
                break;
            case DragMode.Move:
                _sel = MoveTo(_originRect, p - _origin);
                break;
            case DragMode.Resize:
                _sel = ResizeFrom(_originRect, _handle, p - _origin);
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

        if (!IsValid(_sel))
        {
            // 误点 / 太小：清掉重来，不关闭窗口。
            _sel = null;
        }

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

        InfoText.Text = _wantColor
            ? $"{Hex}   {PointX}, {PointY}"
            : $"{PointX}, {PointY}   {Hex}";
        PlaceInfo(dip);
        PlaceMagnifier(dip, ix, iy);
    }

    private void ConfirmPoint()
    {
        if (Hex.Length == 0) return;
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

    /// <summary>按当前选区重画压暗层、选区框、把手、尺寸提示与底部确认栏。</summary>
    private void UpdateChrome()
    {
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        if (_sel is not Rect s || s.Width < 0.5 || s.Height < 0.5)
        {
            DimPath.Data = new RectangleGeometry(full);
            DimPath.Visibility = Visibility.Visible;
            Selection.Visibility = Visibility.Collapsed;
            foreach (var grip in _grips) grip.Visibility = Visibility.Collapsed;
            InfoBox.Visibility = Visibility.Collapsed;
            ConfirmBtn.IsEnabled = false;
            ActionBar.Visibility = Visibility.Collapsed;   // 没选区时不挡任何拖动（提示在顶部不拦鼠标）
            ActionHint.Text = "拖动框选要匹配的区域";
            HintText.Text = "拖动框选范围 · 右键 / Esc 取消";
            return;
        }

        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        geo.Children.Add(new RectangleGeometry(s));
        DimPath.Data = geo;
        DimPath.Visibility = Visibility.Visible;

        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, s.X);
        Canvas.SetTop(Selection, s.Y);
        Selection.Width = s.Width;
        Selection.Height = s.Height;

        bool valid = IsValid(s);
        bool showBar = valid && _drag == DragMode.None;   // 拖动过程中先别跳出来，松手后再给按钮
        PositionGrips(s, valid);
        ConfirmBtn.IsEnabled = showBar;
        ActionBar.Visibility = showBar ? Visibility.Visible : Visibility.Collapsed;
        ActionHint.Text = valid ? "可拖边 / 角微调，方向键逐像素" : "继续拖大一点";

        // 选区靠下时把确认栏挪到顶部，别挡住正在调的边。
        bool nearBottom = s.Bottom > ActualHeight * 0.72 && ActualHeight > 220;
        ActionBar.VerticalAlignment = nearBottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ActionBar.Margin = nearBottom ? new Thickness(0, 78, 0, 0) : new Thickness(0, 0, 0, 38);

        ShowSizeInfo(s);
        HintText.Text = "拖边框 / 角微调 · 方向键 1px（Shift 10px）· 双击 / Enter 确定";
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

    /// <summary>在选区角上角显示尺寸（物理像素，就是最终会存进配置的数字）。</summary>
    private void ShowSizeInfo(Rect s)
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
        double top = s.Bottom + 8;
        if (top + ih > ActualHeight - 8) top = s.Y - ih - 8;
        if (top < 8) top = s.Y + 8;
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, Math.Clamp(top, 8, Math.Max(8, ActualHeight - ih - 8)));
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
    private static (double X, double Y) MonitorScale(System.Drawing.Rectangle monitor)
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
