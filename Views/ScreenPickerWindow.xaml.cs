using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// 窗口盖住光标所在的那台显示器；坐标换算为「显示器左上角 + 图像像素」，
/// 与 <see cref="ScreenCapture"/> 的物理像素坐标系一致（多显示器 / 混合 DPI 都不会偏）。
/// </para>
/// </remarks>
public partial class ScreenPickerWindow : Window
{
    private readonly BitmapSource _shot;
    private readonly System.Drawing.Rectangle _monitor;
    private readonly bool _pointMode;
    private readonly bool _wantColor;

    private bool _dragging;
    private Point _startDip;
    private Point _currentDip;
    private int _currentImageX;
    private int _currentImageY;

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

        // 显示器 DPI 与窗口当前 DPI 不一致时（把窗口挪到别的显示器），WPF 会按建议矩形重排一次，
        // 再钉一次保证仍是 1:1 覆盖。
        DpiChanged += (_, _) => PinToMonitor();

        HintText.Text = pointMode
            ? (wantColor ? "单击取色 · 右键 / Esc 取消" : "单击取点 · 右键 / Esc 取消")
            : "拖动框选范围 · 松手确定 · 右键 / Esc 取消";
        MagBox.Visibility = pointMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ 生命周期 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PinToMonitor();
        Activate();
        Focus();

        UpdateLayout();
        PlaceHint();
        UpdateDim(null);

        if (_pointMode)
        {
            // 打开时先把十字线放到用户按下按钮前的鼠标处（静态画面里它就在那儿）。
            if (Win32.GetCursorPos(out var pt) && TryToDip(pt, out var dip))
                UpdateCrosshair(Clamp(dip));
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Cancel(); return; }
        if (e.Key == Key.Enter && _pointMode && Hex.Length > 0) ConfirmPoint();
    }

    private void Window_Cancel(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void Cancel()
    {
        Confirmed = false;
        Close();
    }

    // ============ 鼠标 ============

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (_pointMode)
        {
            UpdateCrosshair(Clamp(e.GetPosition(Root)));
            ConfirmPoint();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _startDip = Clamp(e.GetPosition(Root));
        _currentDip = _startDip;
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point pos = Clamp(e.GetPosition(Root));

        if (_pointMode) { UpdateCrosshair(pos); return; }
        if (!_dragging) return;

        _currentDip = pos;
        var rect = RectFrom(_startDip, _currentDip);
        UpdateDim(rect);
        UpdateSelection(rect);
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;

        _currentDip = Clamp(e.GetPosition(Root));
        var rect = RectFrom(_startDip, _currentDip);
        if (rect.Width < 4 || rect.Height < 4)
        {
            // 误点：清掉选区重新等一次，不关闭窗口。
            UpdateDim(null);
            Selection.Visibility = Visibility.Collapsed;
            InfoBox.Visibility = Visibility.Collapsed;
            return;
        }

        ConfirmRegion(rect);
    }

    // ============ 取点 ============

    private void UpdateCrosshair(Point dip)
    {
        _currentDip = dip;
        var (ix, iy) = ToImage(dip);
        _currentImageX = ix;
        _currentImageY = iy;

        DimPath.Visibility = Visibility.Collapsed;

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

    private void UpdateDim(Rect? selection)
    {
        var full = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        if (selection is not Rect sel || sel.Width < 0.5 || sel.Height < 0.5)
        {
            DimPath.Data = new RectangleGeometry(full);
            DimPath.Visibility = Visibility.Visible;
            return;
        }

        var geo = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geo.Children.Add(new RectangleGeometry(full));
        geo.Children.Add(new RectangleGeometry(sel));
        DimPath.Data = geo;
        DimPath.Visibility = Visibility.Visible;
    }

    private void UpdateSelection(Rect rect)
    {
        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, rect.X);
        Canvas.SetTop(Selection, rect.Y);
        Selection.Width = rect.Width;
        Selection.Height = rect.Height;

        InfoText.Text = $"{Math.Round(rect.Width)} × {Math.Round(rect.Height)}";
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = InfoBox.DesiredSize.Width;
        double h = InfoBox.DesiredSize.Height;
        double left = Math.Clamp(rect.X, 8, Math.Max(8, ActualWidth - w - 8));
        double top = rect.Y - h - 8 >= 8 ? rect.Y - h - 8 : rect.Y + 8;
        Canvas.SetLeft(InfoBox, left);
        Canvas.SetTop(InfoBox, top);
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
