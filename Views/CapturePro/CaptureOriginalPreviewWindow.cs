using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果旁边的「原文预览」窗（照上游 <c>screenshotoriginalimagepreviewwindow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把它做成**独立小窗**而不是识别窗里的一个勾选：识别窗正好盖住选区、里面画的是重排后的文字，
/// 想对照原文就得有个看得见原图的地方，而且它要能自己缩放 / 平移、钉在最上面。
/// </para>
/// <para>
/// 交互照上游：滚轮**在指针位置**缩放（100% = 1 源像素 1 物理像素）、左键拖动平移、
/// 双击在「适应窗口 / 100%」之间切、标题栏拖动挪位置、Esc / ✕ 关掉。不抢焦点出场。
/// 同一个窗也服务拼图（<see cref="ImageCollageWindow"/>）的「看原图」。
/// </para>
/// </remarks>
internal sealed class CaptureOriginalPreviewWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8;

    private readonly BitmapSource _image;
    private readonly double _scale;
    private readonly Image _picture;
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _title;

    private double _zoom;          // DIP / 源像素
    private bool _fitted = true;
    private bool _dragging;
    private Point _dragStart;
    private double _dragOffsetX, _dragOffsetY;

    public CaptureOriginalPreviewWindow(BitmapSource image, double scale)
    {
        _image = image;
        _scale = scale > 0.1 ? scale : 1;

        Title = "原文预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Background = Brushes.Transparent;

        var chrome = new WindowChrome
        {
            CaptionHeight = 30,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, chrome);

        _picture = new Image
        {
            Source = image,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);

        _scroller = new ScrollViewer
        {
            Content = _picture,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush?)TryFind("SurfaceAltBrush") ?? Brushes.Black,
            Cursor = Cursors.SizeAll,
        };
        _scroller.PreviewMouseWheel += Scroller_Wheel;
        _scroller.PreviewMouseLeftButtonDown += Scroller_MouseDown;
        _scroller.PreviewMouseLeftButtonUp += Scroller_MouseUp;
        _scroller.PreviewMouseMove += Scroller_MouseMove;
        _scroller.PreviewMouseRightButtonDown += (_, _) => Close();

        _title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 34, 0),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush?)TryFind("SecondaryTextBrush") ?? Brushes.Gray,
        };

        var close = new Button
        {
            Content = "✕",
            Width = 30,
            Height = 28,
            Focusable = false,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush?)TryFind("TextBrush") ?? Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "关掉原文预览",
        };
        close.Click += (_, _) => Close();

        var header = new Grid { Height = 30, Cursor = Cursors.SizeAll };
        header.Children.Add(_title);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, _) =>
        {
            try { DragMove(); } catch (InvalidOperationException) { /* 鼠标已经抬起 */ }
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        root.Children.Add(header);
        Grid.SetRow(_scroller, 1);
        root.Children.Add(_scroller);

        Content = new Border
        {
            Background = (Brush?)TryFind("SurfaceBrush") ?? Brushes.Black,
            BorderBrush = (Brush?)TryFind("BorderBrush") ?? Brushes.DimGray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = root,
        };

        double dipWidth = image.PixelWidth / _scale;
        double dipHeight = image.PixelHeight / _scale;
        Width = Math.Clamp(dipWidth + 2, 240, 1100);
        Height = Math.Clamp(dipHeight + 34, 160, 860);

        _zoom = 1 / _scale;      // 100% = 1 源像素 1 物理像素
        Loaded += (_, _) => { Apply(); FitToViewport(); };
        SizeChanged += (_, _) => { if (_fitted) FitToViewport(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    /// <summary>窗口在屏幕上的 DIP 矩形（识别窗据此摆位）。</summary>
    public Rect Bounds => new(Left, Top, Width, Height);

    /// <summary>100% 的缩放（1 源像素 = 1 物理像素）。</summary>
    private double ActualZoom => 1 / _scale;

    /// <summary>当前缩放（冒烟读它）。</summary>
    internal double Zoom => _zoom;

    /// <summary>是否处于「适应窗口」态（冒烟读它）。</summary>
    internal bool Fitted => _fitted;

    /// <summary>
    /// 贴在 <paramref name="anchor"/> 旁边：右边优先，放不下翻左边 / 下方 / 上方，最后夹进工作区。
    /// 摆位规则只有一份（<see cref="OriginalPreviewPlacement"/>），这里只是把窗口搬过去。
    /// </summary>
    public void PlaceBeside(Rect anchor, Rect workArea)
    {
        Rect placed = OriginalPreviewPlacement.Place(anchor, workArea, new Size(Width, Height));
        if (placed.IsEmpty) return;

        Left = placed.Left;
        Top = placed.Top;
        if (_fitted) FitToViewport();
    }

    // ============ 缩放 / 平移 ============

    /// <summary>适应窗口：整张图塞进可视区（打开时的默认态）。</summary>
    internal void FitToViewport()
    {
        double viewWidth = _scroller.ViewportWidth > 0 ? _scroller.ViewportWidth : Width - 2;
        double viewHeight = _scroller.ViewportHeight > 0 ? _scroller.ViewportHeight : Height - 32;
        if (viewWidth <= 0 || viewHeight <= 0) return;

        double fit = Math.Min(viewWidth / _image.PixelWidth, viewHeight / _image.PixelHeight);
        _zoom = Math.Clamp(Math.Min(fit, ActualZoom), MinZoom, MaxZoom);
        _fitted = true;
        Apply();
        _scroller.ScrollToHorizontalOffset(0);
        _scroller.ScrollToVerticalOffset(0);
    }

    private void Apply()
    {
        _picture.Width = Math.Max(1, _image.PixelWidth * _zoom);
        _picture.Height = Math.Max(1, _image.PixelHeight * _zoom);
        _title.Text = $"{_image.PixelWidth} × {_image.PixelHeight} · " +
                      $"{Math.Round(_zoom * _scale * 100)}%";
    }

    private void Scroller_Wheel(object sender, MouseWheelEventArgs e)
    {
        Point at = e.GetPosition(_scroller);
        double next = Math.Clamp(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < 0.0001) return;

        // 光标下的源像素坐标：缩放前后保持它在同一个位置
        double sourceX = (_scroller.HorizontalOffset + at.X) / _zoom;
        double sourceY = (_scroller.VerticalOffset + at.Y) / _zoom;

        _zoom = next;
        _fitted = false;
        Apply();

        _scroller.ScrollToHorizontalOffset(sourceX * _zoom - at.X);
        _scroller.ScrollToVerticalOffset(sourceY * _zoom - at.Y);
        e.Handled = true;
    }

    private void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            if (_fitted) { _zoom = Math.Clamp(ActualZoom, MinZoom, MaxZoom); _fitted = false; Apply(); }
            else FitToViewport();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragStart = e.GetPosition(_scroller);
        _dragOffsetX = _scroller.HorizontalOffset;
        _dragOffsetY = _scroller.VerticalOffset;
        _scroller.CaptureMouse();
        e.Handled = true;
    }

    private void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point at = e.GetPosition(_scroller);
        _scroller.ScrollToHorizontalOffset(_dragOffsetX - (at.X - _dragStart.X));
        _scroller.ScrollToVerticalOffset(_dragOffsetY - (at.Y - _dragStart.Y));
    }

    private void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _scroller.ReleaseMouseCapture();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        long ex = Native.Win32.GetWindowLongPtr(hwnd, Native.Win32.GwlExstyle).ToInt64();
        Native.Win32.SetWindowLongPtr(hwnd, Native.Win32.GwlExstyle,
            (IntPtr)(ex | Native.Win32.WsExToolwindow));

        Native.Win32.SetWindowPos(hwnd, Native.Win32.HwndTopmost, 0, 0, 0, 0,
            Native.Win32.SwpNomove | Native.Win32.SwpNosize | Native.Win32.SwpNoactivate);
    }

    private object? TryFind(string key) => TryFindResource(key) ??
                                          Application.Current?.TryFindResource(key);
}
