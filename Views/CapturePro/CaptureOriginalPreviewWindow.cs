using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「原文」预览**弹出后**的独立小窗（照上游 <c>screenshotoriginalimagepreviewwindow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 默认形态是识别窗里内嵌的一栏（<see cref="OriginalPreviewView"/>），点栏头的「⧉」才变成这个窗。
/// 尺寸固定一档（<see cref="OriginalPreviewPlacement.DefaultSize"/>，300–420 宽）、打开即
/// 「适应窗口」——**不再按图片原始像素开窗**（858×748 的图开一个 858×748 的窗，两侧塞不下就只能
/// 盖在识别窗上，又大又丑）。摆位见 <see cref="OriginalPreviewPlacement.Place"/>：右 → 左 → 上 → 下，
/// 四周都不够才贴到识别窗内侧右边。
/// </para>
/// <para>
/// 外壳：白卡 + 1px 描边 + 8 圆角 + 一层**单独的投影**（投影画在一个没有子元素的 Border 上，
/// 图片缩放时不会被反复模糊）；标题条拖动挪位置，右下角可以拖大小，Esc / ✕ 关。
/// 不抢焦点出场（<c>ShowActivated = false</c>），识别窗里还能接着打字。
/// </para>
/// </remarks>
internal sealed class CaptureOriginalPreviewWindow : Window
{
    /// <summary>卡片四周留给投影的透明边。</summary>
    private const double ShadowRoom = 16;

    private readonly OriginalPreviewView _view;
    private readonly Border _shadow;
    private readonly Border _card;
    private bool _resizing;
    private Point _resizeStart;
    private Size _resizeFrom;

    public CaptureOriginalPreviewWindow(BitmapSource image, double scale)
    {
        _view = new OriginalPreviewView { CanPopOut = false };
        _view.SetImage(image, scale);
        _view.CloseRequested += RequestClose;

        Title = "原文预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 300 + ShadowRoom * 2;
        MinHeight = 220 + ShadowRoom * 2;

        _shadow = new Border
        {
            Background = Brush("SurfaceBrush", Colors.White),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.16,
                Color = Colors.Black,
            },
        };

        _card = new Border
        {
            Background = Brush("SurfaceBrush", Colors.White),
            BorderBrush = Brush("BorderStrongBrush", Color.FromRgb(0xD4, 0xD4, 0xD8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = _view,
        };

        var grip = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 11,0 L 0,11 M 11,4 L 4,11 M 11,8 L 8,11"),
            Stroke = Brush("BorderStrongBrush", Color.FromRgb(0xD4, 0xD4, 0xD8)),
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "拖这里改大小",
            Width = 12,
            Height = 12,
        };
        grip.MouseLeftButtonDown += (_, e) =>
        {
            _resizing = true;
            _resizeStart = PointToScreen(e.GetPosition(this));
            _resizeFrom = new Size(Width, Height);
            grip.CaptureMouse();
            e.Handled = true;
        };
        grip.MouseMove += (_, e) =>
        {
            if (!_resizing) return;
            Point now = PointToScreen(e.GetPosition(this));
            Width = Math.Max(MinWidth, _resizeFrom.Width + (now.X - _resizeStart.X));
            Height = Math.Max(MinHeight, _resizeFrom.Height + (now.Y - _resizeStart.Y));
        };
        grip.MouseLeftButtonUp += (_, _) =>
        {
            if (!_resizing) return;
            _resizing = false;
            grip.ReleaseMouseCapture();
        };

        var root = new Grid();
        var inset = new Thickness(ShadowRoom);
        _shadow.Margin = inset;
        _card.Margin = inset;
        grip.Margin = new Thickness(0, 0, ShadowRoom + 6, ShadowRoom + 6);
        root.Children.Add(_shadow);
        root.Children.Add(_card);
        root.Children.Add(grip);
        Content = root;

        // 标题条拖动挪位置（按钮自己会把 MouseDown 标成已处理，不会误拖）
        _view.Header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            var before = new Point(Left, Top);
            try { DragMove(); }
            catch (InvalidOperationException) { /* 鼠标已经抬起 */ }

            // 用户自己挪过就不再自动跟着识别窗摆（不然一拖识别窗，它又被拽回去）
            if (Math.Abs(Left - before.X) > 0.5 || Math.Abs(Top - before.Y) > 0.5) UserMoved = true;
        };

        Size size = OriginalPreviewPlacement.DefaultSize(SystemParameters.WorkArea);
        Width = size.Width + ShadowRoom * 2;
        Height = size.Height + ShadowRoom * 2;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            RequestClose();
            e.Handled = true;
        };
    }

    /// <summary>点了 ✕ / 按了 Esc：宿主如果接管（把「原文预览」勾选关掉）就走宿主，没人接管才自己关。</summary>
    public event Action? CloseRequested;

    private void RequestClose()
    {
        Action? handler = CloseRequested;
        if (handler != null) handler();
        else Close();
    }

    /// <summary>里面的主体（冒烟读缩放 / 适应态用）。</summary>
    internal OriginalPreviewView View => _view;

    /// <summary>当前缩放。</summary>
    internal double Zoom => _view.Zoom;

    /// <summary>是不是「适应窗口」态。</summary>
    internal bool Fitted => _view.Fitted;

    /// <summary>当前显示的图。</summary>
    internal BitmapSource? Image => _view.Image;

    /// <summary>用户自己拖过这个窗（之后别再自动跟着识别窗摆位）。</summary>
    internal bool UserMoved { get; private set; }

    /// <summary>窗口在屏幕上的矩形（含投影留白，DIP）。</summary>
    public Rect Bounds => new(Left, Top, Width, Height);

    /// <summary>
    /// 贴着 <paramref name="anchor"/> 摆：右 → 左 → 上 → 下，四周都不够才贴内侧右边。
    /// 压在识别窗上时把投影加重一点，读作「浮在上面的一张卡」。
    /// </summary>
    public void PlaceBeside(Rect anchor, Rect workArea)
    {
        var cardSize = new Size(Math.Max(200, Width - ShadowRoom * 2),
            Math.Max(160, Height - ShadowRoom * 2));

        Rect placed = OriginalPreviewPlacement.Place(anchor, workArea, cardSize);
        if (placed.IsEmpty) return;

        Left = placed.Left - ShadowRoom;
        Top = placed.Top - ShadowRoom;

        bool overlapping = OriginalPreviewPlacement.Overlaps(placed, anchor);
        _shadow.Opacity = 1;
        if (_shadow.Effect is DropShadowEffect effect)
        {
            effect.BlurRadius = overlapping ? 22 : 14;
            effect.Opacity = overlapping ? 0.24 : 0.16;
        }
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

    private Brush Brush(string key, Color fallback) =>
        (TryFindResource(key) ?? Application.Current?.TryFindResource(key)) as Brush
        ?? new SolidColorBrush(fallback);
}
