using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「原文」预览的主体：一条紧凑标题条 + 可缩放 / 平移的原图。
/// </summary>
/// <remarks>
/// <para>
/// 同一份实现服务两个宿主：识别窗里**内嵌的一栏**（默认，不遮挡任何东西）和点「弹出」之后的
/// **独立小窗**（<see cref="CaptureOriginalPreviewWindow"/>）——缩放 / 平移 / 状态文案只写一遍。
/// </para>
/// <para>
/// 交互照上游 <c>screenshotoriginalimagepreviewwindow</c>：滚轮**在指针位置**缩放
/// （100% = 1 源像素 1 物理像素）、左键拖动平移、双击在「适应窗口 / 100%」之间切；
/// 放到 100% 以上时改用最近邻采样，看像素对照才清楚。窗口不抢焦点（由宿主负责），
/// 所以识别窗里还能接着打字。
/// </para>
/// </remarks>
internal sealed class OriginalPreviewView : UserControl
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;

    private readonly Image _picture = new();
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _status = new();
    private readonly Button _popBtn;
    private readonly Border _header;

    private BitmapSource? _image;
    private double _scale = 1;
    private double _zoom = 1;
    private bool _fitted = true;
    private bool _dragging;
    private Point _dragStart;
    private double _dragOffsetX, _dragOffsetY;

    public OriginalPreviewView()
    {
        _picture.Stretch = Stretch.Fill;
        _picture.SnapsToDevicePixels = true;
        _picture.HorizontalAlignment = HorizontalAlignment.Left;
        _picture.VerticalAlignment = VerticalAlignment.Top;

        _scroller = new ScrollViewer
        {
            Content = _picture,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
            Focusable = false,
        };
        _scroller.PreviewMouseWheel += OnWheel;
        _scroller.PreviewMouseLeftButtonDown += OnMouseDown;
        _scroller.PreviewMouseLeftButtonUp += OnMouseUp;
        _scroller.PreviewMouseMove += OnMouseMove;

        _popBtn = Mini("弹出", "弹出为独立小窗（想放到别处对照时用）", null, 34);
        _popBtn.Click += (_, _) => PopOutRequested?.Invoke();

        _header = new Border
        {
            Height = 28,
            Background = Brush("SurfaceAltBrush", Color.FromRgb(0xF4, 0xF4, 0xF5)),
            BorderBrush = Brush("BorderBrush", Color.FromRgb(0xE4, 0xE4, 0xE7)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 0, 4, 0),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "原文",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextBrush", Color.FromRgb(0x18, 0x18, 0x1B)),
        };

        _status.FontSize = 11;
        _status.Margin = new Thickness(8, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = Brush("HintTextBrush", Color.FromRgb(0xA1, 0xA1, 0xAA));

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(title);
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        // 状态放 `*` 列：栏窄的时候让它省略，别把右边的按钮挤出栏外
        Grid.SetColumn(_status, 1);
        row.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(FitButton());
        buttons.Children.Add(Mini("1:1", "100%：1 源像素 = 1 屏幕像素（双击图片也能切）", ActualSize, 26));
        buttons.Children.Add(_popBtn);
        buttons.Children.Add(Mini("✕", "关掉原文预览", () => CloseRequested?.Invoke()));
        Grid.SetColumn(buttons, 2);
        row.Children.Add(buttons);

        _header.Child = row;

        var body = new Border
        {
            Background = Brush("SurfaceBrush", Colors.White),
            ClipToBounds = true,
            Child = _scroller,
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_header, 0);
        root.Children.Add(_header);
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        Content = root;

        SizeChanged += (_, _) => { if (_fitted) FitToViewport(); };
    }

    /// <summary>点了「弹出」：宿主把这一栏换成独立小窗。</summary>
    public event Action? PopOutRequested;

    /// <summary>点了「✕」：宿主负责把「原文预览」勾选关掉。</summary>
    public event Action? CloseRequested;

    /// <summary>能不能弹出（独立小窗里自己就不用再弹了）。</summary>
    public bool CanPopOut
    {
        get => _popBtn.Visibility == Visibility.Visible;
        set => _popBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>标题条（独立小窗拿它当拖动把手）。</summary>
    internal FrameworkElement Header => _header;

    /// <summary>当前缩放（DIP / 源像素）。</summary>
    internal double Zoom => _zoom;

    /// <summary>是不是「适应窗口」态。</summary>
    internal bool Fitted => _fitted;

    /// <summary>当前显示的图。</summary>
    internal BitmapSource? Image => _image;

    /// <summary>100% 的缩放（1 源像素 = 1 物理像素）。</summary>
    private double ActualZoom => _scale > 0 ? 1 / _scale : 1;

    /// <summary>换一张图：一律以「适应窗口」开场（整张原图一眼看全）。</summary>
    public void SetImage(BitmapSource image, double scale)
    {
        _image = image;
        _scale = scale > 0.1 ? scale : 1;
        _picture.Source = image;
        _fitted = true;
        FitToViewport();
    }

    /// <summary>适应窗口：整张图塞进可视区。</summary>
    public void FitToViewport()
    {
        if (_image == null) return;

        double viewWidth = ViewWidth();
        double viewHeight = ViewHeight();
        if (!(viewWidth > 0) || !(viewHeight > 0)) return;

        double fit = Math.Min(viewWidth / _image.PixelWidth, viewHeight / _image.PixelHeight);
        _zoom = Math.Clamp(Math.Min(fit, ActualZoom), MinZoom, MaxZoom);
        _fitted = true;
        Apply();
        _scroller.ScrollToHorizontalOffset(0);
        _scroller.ScrollToVerticalOffset(0);
    }

    /// <summary>
    /// 可视区尺寸：视口读数可能落后一拍（刚显示 / 刚拖完分隔条时还是旧值），
    /// 所以再夹到控件本身的尺寸——不然会按旧宽度「适应」，图比这一栏还宽、平白多一条横向滚动条。
    /// </summary>
    private double ViewWidth()
    {
        double limit = Math.Max(0, ActualWidth - 2);
        double viewport = _scroller.ViewportWidth;
        return Math.Min(viewport > 0 ? viewport : limit, limit);
    }

    private double ViewHeight()
    {
        double limit = Math.Max(0, ActualHeight - (_header.ActualHeight > 0 ? _header.ActualHeight : 28) - 2);
        double viewport = _scroller.ViewportHeight;
        return Math.Min(viewport > 0 ? viewport : limit, limit);
    }

    /// <summary>1:1（1 源像素 1 屏幕像素），以可视区中心为锚。</summary>
    public void ActualSize()
    {
        if (_image == null) return;
        ZoomAt(new Point(ViewWidth() / 2, ViewHeight() / 2), ActualZoom);
    }

    private void ZoomAt(Point anchor, double zoom)
    {
        if (_image == null) return;

        double next = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.0001) return;

        // 光标下的源像素坐标：缩放前后保持它在同一个位置
        double sourceX = (_scroller.HorizontalOffset + anchor.X) / _zoom;
        double sourceY = (_scroller.VerticalOffset + anchor.Y) / _zoom;

        _zoom = next;
        _fitted = false;
        Apply();

        _scroller.ScrollToHorizontalOffset(sourceX * _zoom - anchor.X);
        _scroller.ScrollToVerticalOffset(sourceY * _zoom - anchor.Y);
    }

    private void Apply()
    {
        if (_image == null) return;

        _picture.Width = Math.Max(1, _image.PixelWidth * _zoom);
        _picture.Height = Math.Max(1, _image.PixelHeight * _zoom);

        // 图比可视区小就居中（小图贴左上角看着像没对齐）；放大到超出时回到左上，滚动才正常
        double viewWidth = ViewWidth();
        double viewHeight = ViewHeight();
        _picture.HorizontalAlignment = _picture.Width <= viewWidth + 0.5
            ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _picture.VerticalAlignment = _picture.Height <= viewHeight + 0.5
            ? VerticalAlignment.Center : VerticalAlignment.Top;

        RenderOptions.SetBitmapScalingMode(_picture,
            _zoom >= ActualZoom - 1e-6 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _status.Text = $"{_image.PixelWidth} × {_image.PixelHeight} · " +
                       $"{Math.Round(_zoom * _scale * 100)}%";
    }

    // ============ 输入 ============

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image == null) return;
        ZoomAt(e.GetPosition(_scroller), _zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15));
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_image == null) return;

        if (e.ClickCount >= 2)
        {
            if (_fitted) ActualSize();
            else FitToViewport();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(_scroller);
        _dragOffsetX = _scroller.HorizontalOffset;
        _dragOffsetY = _scroller.VerticalOffset;
        _scroller.CaptureMouse();
        _scroller.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point at = e.GetPosition(_scroller);
        _scroller.ScrollToHorizontalOffset(_dragOffsetX - (at.X - _dragStart.X));
        _scroller.ScrollToVerticalOffset(_dragOffsetY - (at.Y - _dragStart.Y));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _scroller.ReleaseMouseCapture();
        _scroller.Cursor = Cursors.Arrow;
    }

    // ============ 小工具 ============

    private Button Mini(string glyph, string tip, Action? click = null, double width = 22) =>
        MiniCore(glyph, width, tip, click);

    /// <summary>
    /// 「适应窗口」的图标：四个角括号。**用内联几何**，别去查 <c>IconFit</c>——那套图标只合并在
    /// 覆盖层 / 工具栏那几个窗口里，识别窗和独立小窗都没有它。
    /// </summary>
    private Button FitButton()
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 1,5 L 1,1 L 5,1 M 13,1 L 17,1 L 17,5 " +
                                   "M 17,13 L 17,17 L 13,17 M 5,17 L 1,17 L 1,13"),
            Stroke = Brush("SecondaryTextBrush", Color.FromRgb(0x71, 0x71, 0x7A)),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        return MiniCore(icon, 24, "适应窗口（双击图片也行）", FitToViewport);
    }

    private Button MiniCore(object content, double width, string tip, Action? click)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 11,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Style = Find("BtnGhost") as Style,
        };
        if (click != null) button.Click += (_, _) => click();
        return button;
    }

    /// <summary>资源查找：控件还没进树时 <c>TryFindResource</c> 找不到应用级字典，补一层 Application。</summary>
    private object? Find(string key) => TryFindResource(key) ?? Application.Current?.TryFindResource(key);

    private Brush Brush(string key, Color fallback) =>
        Find(key) as Brush ?? new SolidColorBrush(fallback);
}
