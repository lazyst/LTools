using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拼图预览画布（照上游 <c>ImageStitchingPreviewCanvas</c>）：棋盘格 + 背景 + 逐图裁剪绘制 +
/// 选中框 / 八把手 + 拖到别的图上交换。
/// </summary>
/// <remarks>
/// <para>
/// 坐标系是元素自己的 DIP：<see cref="ToViewport"/> 只做缩放和平移，图片位置 / 裁剪都来自
/// <see cref="CollageDocument.Layout"/><!-- 输出像素 -->，所以画布与导出永远对得上。
/// </para>
/// <para>
/// 交互照上游：中键拖动 = 平移、滚轮 = 以光标为中心缩放（1.15 倍每格）、F / Home = 适应窗口、
/// Delete / Backspace = 删掉选中的、Esc = 放弃这次拖动（没有再拖就取消选中）、方向键 = 换选中并居中。
/// 左键按图上是移动（拖到别的图上松手 = 交换），按八个把手 / 边上 = 以中心为准缩放。
/// </para>
/// </remarks>
internal sealed class CollageCanvas : FrameworkElement
{
    private const double FitMargin = 48;
    private const double HandleHit = 6;

    private static readonly SolidColorBrush MarqueeBrush = ThemeBrush.Frozen(Color.FromRgb(0x40, 0x96, 0xFF));
    private static readonly Pen MarqueePen = ThemeBrush.DashPen(Color.FromRgb(0x40, 0x96, 0xFF), 1.5);
    private static readonly Pen SelectionPen = ThemeBrush.Pen(Color.FromRgb(0x40, 0x96, 0xFF), 1.5);
    private static readonly Pen CellPen = ThemeBrush.Pen(Color.FromRgb(0xF0, 0xF0, 0xF0), 1);
    private static readonly SolidColorBrush HandleFill = ThemeBrush.Frozen(Colors.White);
    private static readonly SolidColorBrush ErrorBrush = ThemeBrush.Frozen(Color.FromRgb(0xD4, 0x3A, 0x3A));
    private static readonly Typeface UiTypeface = new("Segoe UI");

    private CollageDocument? _document;
    private double _zoom = 1;
    private Point _offset;
    private bool _autoFit = true;
    private bool _editing = true;

    private bool _panning;
    private Point _lastPan;

    private long _pressedId;
    private FrameHandle _handle = FrameHandle.None;
    private Point _press;
    private Rect _initialImage;
    private CollagePlacement _pendingPlacement;
    private Rect _pendingImage;
    private bool _dragging;
    private long _swapTarget;

    private bool _checkerReady;
    private Brush? _checker;

    public CollageCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>画布展示的文档（换文档会重新适应窗口）。</summary>
    public CollageDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.SelectionChanged -= OnSelectionChanged;
            }

            _document = value;
            if (_document != null)
            {
                _document.Changed += OnDocumentChanged;
                _document.SelectionChanged += OnSelectionChanged;
            }

            _autoFit = true;
            CancelGesture();
            InvalidateMeasure();
            InvalidateVisual();
            FitToCanvas();
            ViewportChanged?.Invoke();
        }
    }

    /// <summary>能不能编辑（载入 / 导出时关掉）。</summary>
    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            if (!value) CancelGesture();
            _editing = value;
            Cursor = null;
            InvalidateVisual();
        }
    }

    /// <summary>缩放倍率（1 = 1 输出像素 1 DIP）。</summary>
    public double Zoom => _zoom;

    /// <summary>输出图左上角在画布里的位置（DIP）。</summary>
    public Point Offset => _offset;

    /// <summary>选中项在画布里的矩形（空 = 没选中 / 不在视野）。</summary>
    public Rect SelectionFrame
    {
        get
        {
            var document = _document;
            if (document == null) return Rect.Empty;
            int index = document.IndexOf(document.SelectedId);
            var layout = document.Layout;
            if (index < 0 || index >= layout.ImageRects.Count) return Rect.Empty;
            return ToViewport(layout.ImageRects[index]);
        }
    }

    /// <summary>缩放 / 平移 / 选中变化（窗口用来摆浮动工具条）。</summary>
    public event Action? ViewportChanged;

    public bool CanFitToCanvas => _document?.Layout.IsValid == true;

    // ============ 视口 ============

    /// <summary>适应窗口：四周留 48、最多 100%（照上游 <c>fitToCanvas</c>）。</summary>
    public void FitToCanvas()
    {
        var document = _document;
        if (document == null) return;

        CancelGesture();
        var viewport = Viewport();
        var layout = document.Layout;
        if (!layout.IsValid)
        {
            _zoom = 1;
            _offset = new Point(viewport.X, viewport.Y);
            _autoFit = true;
            InvalidateVisual();
            ViewportChanged?.Invoke();
            return;
        }

        _autoFit = true;
        double areaWidth = Math.Max(1, viewport.Width - FitMargin);
        double areaHeight = Math.Max(1, viewport.Height - FitMargin);
        _zoom = Math.Min(1, Math.Min(areaWidth / layout.Width, areaHeight / layout.Height));
        if (!(_zoom > 0) || double.IsInfinity(_zoom)) _zoom = 1;
        _offset = new Point(viewport.X + (viewport.Width - layout.Width * _zoom) / 2,
            viewport.Y + (viewport.Height - layout.Height * _zoom) / 2);
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    /// <summary>把某张图居中（必要时缩小到看得全），照上游 <c>centerImage</c>。</summary>
    public void CenterItem(long id)
    {
        var document = _document;
        if (document == null) return;

        CancelGesture();
        int index = document.IndexOf(id);
        if (index < 0) return;
        document.Select(id);

        var layout = document.Layout;
        if (!layout.IsValid || index >= layout.ImageRects.Count) return;
        var image = layout.ImageRects[index];
        if (image.Width <= 0 || image.Height <= 0) return;

        var viewport = Viewport();
        _autoFit = false;
        _zoom = Math.Min(_zoom, Math.Min(Math.Max(1, viewport.Width - FitMargin) / image.Width,
            Math.Max(1, viewport.Height - FitMargin) / image.Height));
        _offset = new Point(viewport.X + viewport.Width / 2 - (image.X + image.Width / 2.0) * _zoom,
            viewport.Y + viewport.Height / 2 - (image.Y + image.Height / 2.0) * _zoom);
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    /// <summary>方向键换选中（照上游：在文档顺序里前后挪一格并居中）。</summary>
    public void SelectAdjacent(int delta)
    {
        var document = _document;
        if (document == null || document.Items.Count == 0) return;

        int index = document.IndexOf(document.SelectedId);
        int next = Math.Clamp(index + delta, 0, document.Items.Count - 1);
        CenterItem(document.Items[next].Id);
    }

    /// <summary>删掉选中的一张（Delete / Backspace）。</summary>
    public void DeleteSelected()
    {
        var document = _document;
        if (document == null || !_editing) return;
        if (document.SelectedId == 0) return;
        document.Remove(document.SelectedId);
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Checker(), null, new Rect(RenderSize));

        var document = _document;
        if (document == null) return;
        if (document.Items.Count == 0) return;

        var layout = document.Layout;
        if (!layout.IsValid)
        {
            var text = new FormattedText(
                layout.Error.Length > 0 ? layout.Error : "拼接结果算不出来",
                CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, UiTypeface, 14,
                ErrorBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(40, RenderSize.Width - 48),
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(text, new Point(24, Math.Max(24, (RenderSize.Height - text.Height) / 2)));
            return;
        }

        var viewport = Viewport();
        var output = new Rect(_offset.X, _offset.Y, layout.Width * _zoom, layout.Height * _zoom);
        dc.DrawRectangle(new SolidColorBrush(document.Settings.Background), null, output);

        // 选中项被自己那一格裁掉的部分，用 25% 不透明度再画一遍（照上游）。
        long selected = document.SelectedId;
        int selectedIndex = document.IndexOf(selected);
        if (selectedIndex >= 0 && selectedIndex < layout.ImageRects.Count)
        {
            var clip = ToViewport(layout.Clips[selectedIndex]);
            dc.PushOpacity(0.25);
            dc.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(viewport), new RectangleGeometry(clip)));
            DrawItem(dc, document.Items[selectedIndex], layout.ImageRects[selectedIndex]);
            dc.Pop();
            dc.Pop();
        }

        for (int i = 0; i < document.Items.Count && i < layout.ImageRects.Count; i++)
        {
            var image = layout.ImageRects[i];
            if (image.Width <= 0 || image.Height <= 0) continue;
            dc.PushClip(new RectangleGeometry(ToViewport(layout.Clips[i])));
            DrawItem(dc, document.Items[i], image);
            dc.Pop();
        }

        dc.DrawRectangle(null, CellPen, output);
        for (int i = 0; i < layout.Clips.Count; i++)
            dc.DrawRectangle(null, CellPen, ToViewport(layout.Clips[i]));

        if (_swapTarget != 0)
        {
            int target = document.IndexOf(_swapTarget);
            if (target >= 0 && target < layout.Clips.Count)
                dc.DrawRectangle(null, MarqueePen, ToViewport(layout.Clips[target]));
        }

        DrawSelection(dc);
    }

    private void DrawItem(DrawingContext dc, CollageItem item, Int32Rect image)
    {
        Rect target = _pressedId == item.Id && _dragging ? _pendingImage : ToViewport(image);
        dc.PushTransform(new MatrixTransform(CollageRender.MatrixOf(item, target)));
        dc.DrawImage(item.Preview, new Rect(0, 0, item.Source.PixelWidth, item.Source.PixelHeight));
        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc)
    {
        if (!_editing) return;

        var document = _document;
        if (document == null) return;
        int index = document.IndexOf(document.SelectedId);
        if (index < 0) return;

        Rect frame = _pressedId != 0 && _dragging ? _pendingImage : SelectionFrame;
        if (frame.IsEmpty) return;

        dc.DrawRectangle(null, SelectionPen, frame);
        double size = MarkGeometry.HandleSize;
        foreach (var handle in MarkGeometry.Handles)
        {
            Point at = MarkGeometry.GripPoint(frame, handle);
            var rect = new Rect(at.X - size / 2, at.Y - size / 2, size, size);
            dc.DrawRectangle(HandleFill, SelectionPen, rect);
        }
    }

    private Brush Checker()
    {
        if (_checkerReady && _checker != null) return _checker;

        var surface = TryFindResource("SurfaceBrush") as Brush ?? Brushes.White;
        var fill = ThemeBrush.Frozen(Color.FromRgb(0xF0, 0xF0, 0xF1));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(surface, null, new RectangleGeometry(new Rect(0, 0, 20, 20))));
        var squares = new GeometryGroup();
        squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 10, 10)));
        squares.Children.Add(new RectangleGeometry(new Rect(10, 10, 10, 10)));
        group.Children.Add(new GeometryDrawing(fill, null, squares));
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        _checker = brush;
        _checkerReady = true;
        return brush;
    }

    // ============ 坐标 ============

    private Rect Viewport() => new(0, 0, Math.Max(0, RenderSize.Width), Math.Max(0, RenderSize.Height));

    private Rect ToViewport(Int32Rect rect) => new(
        _offset.X + rect.X * _zoom, _offset.Y + rect.Y * _zoom, rect.Width * _zoom,
        rect.Height * _zoom);

    // ============ 文档事件 ============

    private void OnDocumentChanged()
    {
        var document = _document;
        if (document == null) return;

        if (document.Items.Count == 0)
        {
            _autoFit = true;
            _zoom = 1;
            _offset = default;
            InvalidateVisual();
            ViewportChanged?.Invoke();
            return;
        }

        if (_autoFit)
        {
            FitToCanvas();
            return;
        }

        // 不自动适应时把选中的图留在视野中心（照上游 anchored 的逻辑）。
        int index = document.IndexOf(document.SelectedId);
        var layout = document.Layout;
        if (index >= 0 && index < layout.ImageRects.Count)
        {
            var image = layout.ImageRects[index];
            var viewport = Viewport();
            var center = new Point(_offset.X + (image.X + image.Width / 2.0) * _zoom,
                _offset.Y + (image.Y + image.Height / 2.0) * _zoom);
            var middle = new Point(viewport.Width / 2, viewport.Height / 2);
            _offset.Offset(middle.X - center.X, middle.Y - center.Y);
        }

        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    private void OnSelectionChanged()
    {
        CancelGesture();
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    // ============ 鼠标 ============

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var document = _document;
        if (document == null) return;

        Focus();
        if (e.ChangedButton == MouseButton.Middle)
        {
            CancelGesture();
            _panning = true;
            _autoFit = false;
            _lastPan = e.GetPosition(this);
            Cursor = Cursors.SizeAll;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        CancelGesture();
        _autoFit = false;
        var position = e.GetPosition(this);

        var handle = _editing ? MarkGeometry.HitFrameEdge(SelectionFrame, position, HandleHit)
            : FrameHandle.None;
        long selected = document.SelectedId;
        int index = document.IndexOf(selected);
        var layout = document.Layout;

        if (IsResizeHandle(handle) && index >= 0 && index < layout.ImageRects.Count)
        {
            _pressedId = selected;
            _handle = handle;
            _press = position;
            _initialImage = ToViewport(layout.ImageRects[index]);
            _pendingImage = _initialImage;
            _pendingPlacement = document.Items[index].Placement;
            Cursor = CursorFor(handle);
            CaptureMouse();
            e.Handled = true;
            return;
        }

        long hit = HitItem(position, includeWhitespace: false);
        if (hit == 0)
        {
            document.Select(0);
            e.Handled = true;
            return;
        }

        document.Select(hit);
        if (!_editing) return;

        int hitIndex = document.IndexOf(hit);
        if (hitIndex < 0 || hitIndex >= layout.ImageRects.Count) return;
        _pressedId = hit;
        _handle = FrameHandle.None;
        _press = position;
        _initialImage = ToViewport(layout.ImageRects[hitIndex]);
        _pendingImage = _initialImage;
        _pendingPlacement = document.Items[hitIndex].Placement;
        Cursor = Cursors.SizeAll;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var document = _document;
        if (document == null) return;

        var position = e.GetPosition(this);

        if (_panning && e.MiddleButton == MouseButtonState.Pressed)
        {
            Vector delta = position - _lastPan;
            _lastPan = position;
            _offset += delta;
            InvalidateVisual();
            ViewportChanged?.Invoke();
            return;
        }

        if (_pressedId != 0 && _editing && e.LeftButton == MouseButtonState.Pressed)
        {
            if (!_dragging)
            {
                Vector distance = position - _press;
                if (Math.Abs(distance.X) + Math.Abs(distance.Y) < 4) return;
            }

            var delta = new Vector((position.X - _press.X) / Math.Max(_zoom, 1e-9),
                (position.Y - _press.Y) / Math.Max(_zoom, 1e-9));
            Rect pending = IsResizeHandle(_handle)
                ? CenteredResize(_initialImage, _handle, delta, _zoom)
                : new Rect(_initialImage.X + delta.X, _initialImage.Y + delta.Y, _initialImage.Width,
                    _initialImage.Height);

            int index = document.IndexOf(_pressedId);
            if (index < 0) return;
            var item = document.Items[index];
            var (transformedWidth, _) = item.TransformedSize;
            if (transformedWidth <= 0) return;

            var clip = document.Layout.Clips[index];
            var clipCenter = new Point(clip.X + clip.Width / 2.0, clip.Y + clip.Height / 2.0);
            var output = new Rect((pending.X - _offset.X) / Math.Max(_zoom, 1e-9),
                (pending.Y - _offset.Y) / Math.Max(_zoom, 1e-9),
                pending.Width / Math.Max(_zoom, 1e-9), pending.Height / Math.Max(_zoom, 1e-9));
            var placement = new CollagePlacement(
                output.X + output.Width / 2 - clipCenter.X,
                output.Y + output.Height / 2 - clipCenter.Y,
                output.Width / transformedWidth);

            var resolved = CollageLayoutCalculator.ResolveStaticRect(item,
                document.Layout.AutomaticRects[index], clip);
            if (resolved.Width <= 0 || resolved.Height <= 0) return;

            _dragging = true;
            _pendingPlacement = placement;
            _pendingImage = pending;

            long target = IsResizeHandle(_handle) ? 0 : HitItem(position, includeWhitespace: true);
            _swapTarget = target != _pressedId ? target : 0;
            Cursor = IsResizeHandle(_handle) ? CursorFor(_handle)
                : _swapTarget != 0 ? Cursors.Hand : Cursors.SizeAll;
            InvalidateVisual();
            ViewportChanged?.Invoke();
            return;
        }

        if (_editing && e.LeftButton == MouseButtonState.Released)
        {
            var handle = MarkGeometry.HitFrameEdge(SelectionFrame, position, HandleHit);
            Cursor = handle == FrameHandle.None ? null : CursorFor(handle);
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var document = _document;
        if (document == null) return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = false;
            Cursor = null;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || _pressedId == 0) return;

        var position = e.GetPosition(this);
        if (_dragging)
        {
            var placement = _pendingPlacement;
            long target = IsResizeHandle(_handle) ? 0 : HitItem(position, includeWhitespace: true);
            long pressed = _pressedId;
            CancelGesture();
            if (target != 0 && target != pressed) document.Swap(pressed, target);
            else document.SetStaticPlacement(pressed, placement);
        }
        else
        {
            CancelGesture();
        }

        ReleaseMouseCapture();
        Cursor = null;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var document = _document;
        if (document == null || !document.Layout.IsValid) return;

        CancelGesture();
        double steps = e.Delta / 120.0;
        if (steps == 0) return;

        var position = e.GetPosition(this);
        double next = Math.Clamp(_zoom * Math.Pow(1.15, steps), 1e-9, 16.0);
        _offset = new Point(position.X - (position.X - _offset.X) * (next / _zoom),
            position.Y - (position.Y - _offset.Y) * (next / _zoom));
        _zoom = next;
        _autoFit = false;
        InvalidateVisual();
        ViewportChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.F or Key.Home)
        {
            FitToCanvas();
            e.Handled = true;
        }
        else if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            bool active = _pressedId != 0;
            CancelGesture();
            if (!active) _document?.Select(0);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        {
            SelectAdjacent(e.Key is Key.Left or Key.Up ? -1 : 1);
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_autoFit) FitToCanvas();
        else
        {
            InvalidateVisual();
            ViewportChanged?.Invoke();
        }
    }

    // ============ 手势 ============

    private void CancelGesture()
    {
        _pressedId = 0;
        _handle = FrameHandle.None;
        _dragging = false;
        _swapTarget = 0;
        _pendingImage = default;
        _pendingPlacement = default;
        _panning = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>
    /// 拖把手的新框：**以中心为准**缩放（照上游 <c>centeredResize</c>）——拖角只缩一个比例，
    /// 拖边只缩一个轴，中心始终不动。
    /// </summary>
    public static Rect CenteredResize(Rect initial, FrameHandle handle, Vector delta, double viewScale)
    {
        double halfWidth = initial.Width / 2;
        double halfHeight = initial.Height / 2;
        var vector = handle switch
        {
            FrameHandle.TopLeft => new Vector(-halfWidth, -halfHeight),
            FrameHandle.TopRight => new Vector(halfWidth, -halfHeight),
            FrameHandle.BottomRight => new Vector(halfWidth, halfHeight),
            FrameHandle.BottomLeft => new Vector(-halfWidth, halfHeight),
            FrameHandle.Top => new Vector(0, -halfHeight),
            FrameHandle.Right => new Vector(halfWidth, 0),
            FrameHandle.Bottom => new Vector(0, halfHeight),
            FrameHandle.Left => new Vector(-halfWidth, 0),
            _ => new Vector(0, 0),
        };

        double denominator = vector.X * vector.X + vector.Y * vector.Y;
        if (denominator <= 0 || viewScale <= 0) return initial;

        double maximumEdge = Math.Max(initial.Width, initial.Height) * viewScale;
        double scale = Math.Clamp(1 + (delta.X * vector.X + delta.Y * vector.Y) / denominator,
            2.0 / maximumEdge, 1_000_000.0 / maximumEdge);
        double width = initial.Width * scale;
        double height = initial.Height * scale;
        return new Rect(initial.X + halfWidth - width / 2, initial.Y + halfHeight - height / 2,
            width, height);
    }

    /// <summary>
    /// 能不能当缩放把手：<see cref="MarkGeometry.HitFrameEdge"/> 框内返回 <see cref="FrameHandle.Move"/>，
    /// 那个不是把手（按图中间是拖动 / 交换），别当成缩放。
    /// </summary>
    private static bool IsResizeHandle(FrameHandle handle) =>
        handle is not (FrameHandle.None or FrameHandle.Move);

    /// <summary>光标下的图（文档顺序第一个命中）；<paramref name="includeWhitespace"/> 时格内空白也算。</summary>
    private long HitItem(Point position, bool includeWhitespace)
    {
        var document = _document;
        if (document == null) return 0;
        var layout = document.Layout;
        if (!layout.IsValid || _zoom <= 0) return 0;

        for (int i = 0; i < document.Items.Count; i++)
        {
            if (i >= layout.Clips.Count || i >= layout.ImageRects.Count) break;
            if (!ToViewport(layout.Clips[i]).Contains(position)) continue;
            if (includeWhitespace || ToViewport(layout.ImageRects[i]).Contains(position))
                return document.Items[i].Id;
        }

        return 0;
    }

    private static Cursor CursorFor(FrameHandle handle) => handle switch
    {
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

}
