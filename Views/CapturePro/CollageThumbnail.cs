using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拼图缩略图（照上游 <c>ImageStitchingThumbnail</c>）：108×84 卡片，棋盘格上按当前方向画预览，
/// 底部居中写序号；选中（或拖到它上面）时套 2px 主色框，悬停 / 聚焦时右下角浮出
/// 「替换 / 删除」两个 28×28 小钮。
/// </summary>
/// <remarks>
/// 拖到另一张缩略图上松手 = 交换（照上游：不是排序，是 swap）。卡片自己只负责识别手势并抛事件，
/// 幽灵层和命中判定由 <see cref="ImageCollageWindow"/> 做——它才有覆盖整窗的 <c>DragLayer</c>。
/// </remarks>
internal sealed class CollageThumbnail : Grid
{
    public const double CardWidth = 108;
    public const double CardHeight = 84;

    /// <summary>图片区（照上游 <c>outer.adjusted(5,5,-5,-20)</c> 换算到内框）。</summary>
    private const double AreaWidth = 96;
    private const double AreaHeight = 57;

    private static readonly Brush Checker = BuildChecker();

    private readonly Border _frame;
    private readonly Border _imageArea;
    private readonly Image _picture;
    private readonly TextBlock _ordinal;
    private readonly Border _selection;
    private readonly StackPanel _actions;
    private readonly Button _replace;
    private readonly Button _remove;

    private bool _editing = true;
    private bool _selected;
    private bool _target;
    private bool _hovered;
    private bool _pressed;
    private bool _dragging;
    private Point _press;

    public CollageThumbnail(long id, FrameworkElement resources)
    {
        ItemId = id;
        Width = CardWidth;
        Height = CardHeight;
        Focusable = true;
        Cursor = Cursors.Hand;
        Margin = new Thickness(0, 0, 8, 0);
        SnapsToDevicePixels = true;

        _frame = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Background = BrushOr(resources, "SurfaceBrush", Brushes.White),
            BorderBrush = BrushOr(resources, "CaptureBorderSecondary", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
        };

        _imageArea = new Border
        {
            Margin = new Thickness(5, 5, 5, 20),
            Background = Checker,
            ClipToBounds = true,
        };

        _picture = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _imageArea.Child = _picture;

        var body = new Grid();
        body.Children.Add(_imageArea);

        _ordinal = new TextBlock
        {
            Height = 17,
            Margin = new Thickness(4, 0, 4, 2),
            VerticalAlignment = VerticalAlignment.Bottom,
            TextAlignment = TextAlignment.Center,
            FontSize = 11,
            Foreground = BrushOr(resources, "CaptureTextSecondary", Brushes.Gray),
        };
        body.Children.Add(_ordinal);

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Visibility = Visibility.Collapsed,
        };

        _replace = ActionButton(resources, "IconReplace", "替换图像",
            () => ReplaceRequested?.Invoke(ItemId));
        _replace.Margin = new Thickness(0, 0, 4, 0);
        _remove = ActionButton(resources, "IconTrash", "删除图像",
            () => RemoveRequested?.Invoke(ItemId));
        _actions.Children.Add(_replace);
        _actions.Children.Add(_remove);
        body.Children.Add(_actions);

        _frame.Child = body;
        Children.Add(_frame);

        // 选中 / 交换目标的 2px 主色框画在最上面，不动内容布局（照上游直接描 2px）
        _selection = new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            BorderBrush = BrushOr(resources, "CapturePrimary", Brushes.DodgerBlue),
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_selection);

        MouseEnter += (_, _) =>
        {
            _hovered = true;
            UpdateActions();
        };
        MouseLeave += (_, _) =>
        {
            _hovered = false;
            UpdateActions();
        };
        GotKeyboardFocus += (_, _) => UpdateChrome();
        LostKeyboardFocus += (_, _) => UpdateChrome();
    }

    public long ItemId { get; }

    /// <summary>正被别的缩略图拖在上面（松手会交换）。</summary>
    public bool IsTarget
    {
        get => _target;
        set
        {
            if (_target == value) return;
            _target = value;
            UpdateChrome();
        }
    }

    public event Action<long>? SelectRequested;

    public event Action<long>? ReplaceRequested;

    public event Action<long>? RemoveRequested;

    /// <summary>拖出 4px 阈值后开拖（窗口据此立幽灵）。</summary>
    public event Action<long>? DragStarted;

    /// <summary>拖动中（参数 = 屏幕坐标）。</summary>
    public event Action<long, Point>? DragMoved;

    /// <summary>松手（参数 = 屏幕坐标；窗口据此判定交换目标）。</summary>
    public event Action<long, Point>? DragEnded;

    /// <summary>能不能编辑（加载 / 导出时关掉）。</summary>
    public void SetEditing(bool editing)
    {
        if (_editing == editing) return;
        _editing = editing;
        if (!editing) CancelDrag();
        _replace.IsEnabled = editing;
        _remove.IsEnabled = editing;
        Cursor = editing ? Cursors.Hand : Cursors.Arrow;
        UpdateActions();
    }

    /// <summary>刷新一张图的内容（序号 1 起）。</summary>
    public void Update(CollageItem item, int ordinal, bool selected)
    {
        var (width, height) = item.TransformedSize;
        double scale = Math.Min(AreaWidth / Math.Max(1, width), AreaHeight / Math.Max(1, height));
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;

        _picture.Source = item.Preview;
        _picture.LayoutTransform = item.PreviewLayoutTransform;
        _picture.Width = Math.Max(1, Math.Round(item.Source.PixelWidth * scale));
        _picture.Height = Math.Max(1, Math.Round(item.Source.PixelHeight * scale));

        _ordinal.Text = ordinal.ToString(CultureInfo.InvariantCulture);
        ToolTip = $"{ordinal} · {item.Label}";
        _selected = selected;
        UpdateChrome();
    }

    /// <summary>屏幕上这一点是否落在卡片里（拖动时找交换目标）。</summary>
    public bool ContainsScreen(Point screen)
    {
        if (!IsVisible || ActualWidth <= 0) return false;
        Point local = PointFromScreen(screen);
        return local.X >= 0 && local.Y >= 0 && local.X <= ActualWidth && local.Y <= ActualHeight;
    }

    /// <summary>取消这次拖动（Esc / 关窗）。</summary>
    public void CancelDrag()
    {
        _pressed = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    // ============ 外观 ============

    private void UpdateChrome()
    {
        bool emphasize = _selected || _target || IsKeyboardFocusWithin;
        _selection.Visibility = emphasize ? Visibility.Visible : Visibility.Collapsed;
        UpdateActions();
    }

    private void UpdateActions()
    {
        bool show = _hovered || IsKeyboardFocusWithin;
        _actions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ============ 鼠标 / 键盘 ============

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (VisualTree.FindAncestor<Button>((DependencyObject)e.OriginalSource) != null) return;

        Focus();
        SelectRequested?.Invoke(ItemId);
        _press = e.GetPosition(this);
        _pressed = true;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed || !_editing || e.LeftButton != MouseButtonState.Pressed) return;

        Point position = e.GetPosition(this);
        if (!_dragging)
        {
            Vector distance = position - _press;
            if (Math.Abs(distance.X) + Math.Abs(distance.Y) < 4) return;
            _dragging = true;
            DragStarted?.Invoke(ItemId);
        }

        DragMoved?.Invoke(ItemId, PointToScreen(position));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;

        Point position = PointToScreen(e.GetPosition(this));
        bool dragging = _dragging;
        _pressed = false;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (dragging) DragEnded?.Invoke(ItemId, position);
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressed = false;
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter)
        {
            SelectRequested?.Invoke(ItemId);
            e.Handled = true;
        }
        else if ((e.Key is Key.Delete or Key.Back) && _editing)
        {
            RemoveRequested?.Invoke(ItemId);
            e.Handled = true;
        }
    }

    // ============ 构造小件 ============

    private static Button ActionButton(FrameworkElement resources, string iconKey, string tip,
        Action click)
    {
        var icon = new System.Windows.Shapes.Path
        {
            Data = resources.TryFindResource(iconKey) as Geometry ?? Geometry.Empty,
            Style = resources.TryFindResource("CanvasActionPath") as Style,
        };

        var button = new Button
        {
            Content = icon,
            ToolTip = tip,
            Style = resources.TryFindResource("ThumbAction") as Style,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Brush BrushOr(FrameworkElement resources, string key, Brush fallback) =>
        resources.TryFindResource(key) as Brush ?? fallback;

    private static Brush BuildChecker()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        var fill = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF1));
        fill.Freeze();
        var tiles = new GeometryGroup();
        tiles.Children.Add(new RectangleGeometry(new Rect(0, 0, 10, 10)));
        tiles.Children.Add(new RectangleGeometry(new Rect(10, 10, 10, 10)));
        group.Children.Add(new GeometryDrawing(fill, null, tiles));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}

