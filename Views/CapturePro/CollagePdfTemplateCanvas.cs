using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// PDF「模板拼接」的画布（照上游 <c>ImageStitchingPdfTemplateCanvas</c>）：一张纸按
/// <see cref="CollagePdfGeometry.ComposedPage"/> 逐格裁剪画图，选中图在自己格子**外面**的部分
/// 画成 25% 透明，格线画在图片上方（相邻两张共享的边才不会被盖掉）。
/// </summary>
/// <remarks>
/// <para>
/// 手势照上游：中键平移；滚轮以光标为中心缩放（0.05–16 倍）；F / Home 适应；
/// 左键点一下 = 选中；拖图 = 改中心偏移（拖到另一格上松手 = 交换）；拖四角把手 =
/// 以中心等比缩放；PageUp / PageDown 翻页；方向键换图；Delete / Backspace 删除；Esc 取消手势。
/// </para>
/// <para>
/// 纸面留白照上游 <c>geometry()</c>：可用区 <c>(24, 24, w-48, h-80)</c>（下部 56 留给工具条）。
/// 选中框是「图像矩形外扩 4、1px 主色」，只画四个 8×8 圆角 2 的白把手，边只做命中（12 宽命中带）。
/// </para>
/// </remarks>
internal sealed class CollagePdfTemplateCanvas : FrameworkElement
{
    private const double Inset = 24;
    private const double BottomReserve = 56;
    private const double HandleHit = 6;
    private const double HandleSize = 8;
    private const double FramePad = 4;
    private const double DragStart = 4;

    private static readonly Typeface UiTypeface = new("Segoe UI");
    private static readonly Brush White = ThemeBrush.Frozen(Colors.White);
    private static readonly Pen SelectionPen = ThemeBrush.Pen(Color.FromRgb(0x40, 0x96, 0xFF), 1);
    private static readonly Pen BorderPen = ThemeBrush.Pen(Color.FromRgb(0xD9, 0xD9, 0xD9), 1);
    private static readonly Pen CellPen = ThemeBrush.Pen(Color.FromRgb(0xF0, 0xF0, 0xF0), 1);
    private static readonly SolidColorBrush MarqueeFill = ThemeBrush.Frozen(Color.FromArgb(0x33, 0x40, 0x96, 0xFF));
    private static readonly SolidColorBrush ErrorBrush = ThemeBrush.Frozen(Color.FromRgb(0xD4, 0x3A, 0x3A));

    private CollageDocument? _document;
    private bool _editing = true;
    private bool _syncing;

    private CollagePdfComposedPage _page =
        new(default, default, Array.Empty<CollagePdfCell>(), "PDF 页面设置无效");
    private int _pageIndex;

    private double _zoom = 1;
    private Point _offset;
    private Rect _available;
    private Rect _paper;
    private double _fitScale = 1;
    private double _viewScale = 1;

    private bool _panning;
    private Point _panLast;

    private long _gestureId;
    private int _gestureCell = -1;
    private bool _moved;
    private Point _press;
    private Rect _initialImage;
    private Rect _pendingImage;
    private CollagePdfPlacement _pendingPlacement;
    private FrameHandle _handle;
    private long _targetId;

    public CollagePdfTemplateCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    // ============ 契约 ============

    public event Action<long>? RemoveRequested;

    public event Action<int>? PageChanged;

    /// <summary>纸面 / 选中框 / 缩放变了（窗口据此摆浮动工具条）。</summary>
    public event Action? LayoutChanged;

    public CollageDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null)
            {
                _document.Changed -= OnDocumentChanged;
                _document.SelectionChanged -= OnDocumentChanged;
            }

            _document = value;
            if (_document != null)
            {
                _document.Changed += OnDocumentChanged;
                _document.SelectionChanged += OnDocumentChanged;
            }

            Refresh();
        }
    }

    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            if (!value) CancelGesture();
            InvalidateVisual();
        }
    }

    /// <summary>当前页号（0 起）。</summary>
    public int PageIndex => _pageIndex;

    /// <summary>总页数（按上一次有效设置算）。</summary>
    public int PageCount
    {
        get
        {
            if (_document == null) return 0;
            return CollagePdfGeometry.PageCount(_document.Items.Count, Settings);
        }
    }

    /// <summary>选中图在画布里的矩形（空 = 没有选中 / 不在这一页）。</summary>
    public Rect SelectionFrame
    {
        get
        {
            var cell = CellOf(SelectedId);
            return cell == null ? Rect.Empty : ImageRect(cell.Value);
        }
    }

    /// <summary>纸面矩形（控件坐标；冒烟读）。</summary>
    public Rect PaperRect => _paper;

    /// <summary>正在拖 / 平移。</summary>
    public bool IsGesturing => _gestureId != 0 || _panning;

    internal double Zoom => _zoom;

    internal Point ViewportOffset => _offset;

    internal long SwapTargetId => _targetId;

    // ============ 刷新 ============

    private CollagePdfSettings Settings =>
        _document?.LastValidPdfSettings ?? CollagePdfSettings.Default;

    private long SelectedId => _document?.SelectedId ?? 0;

    private void OnDocumentChanged()
    {
        if (_syncing) return;
        Refresh();
    }

    /// <summary>重算当前页并按需跟随选中项（照上游 <c>refresh</c>）。</summary>
    public void Refresh(bool followSelection = true)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            CancelGesture();
            bool hadValid = _page.IsValid;
            int previous = _pageIndex;

            var settings = Settings;
            int count = _document == null ? 0 : CollagePdfGeometry.PageCount(_document.Items.Count,
                settings);
            int capacity = Math.Max(1, CollagePdfGeometry.Capacity(settings));
            int selectedIndex = _document?.IndexOf(SelectedId) ?? -1;
            if (followSelection && selectedIndex >= 0) _pageIndex = selectedIndex / capacity;
            _pageIndex = Math.Clamp(_pageIndex, 0, Math.Max(0, count - 1));

            _page = _document == null
                ? new CollagePdfComposedPage(default, default, Array.Empty<CollagePdfCell>(),
                    "PDF 页面设置无效")
                : CollagePdfGeometry.ComposedPage(CollagePdfRender.Snapshot(_document.Items),
                    settings, _pageIndex);

            if (!hadValid && _page.IsValid)
            {
                _zoom = 1;
                _offset = default;
            }

            Geometry();
            if (_pageIndex != previous) PageChanged?.Invoke(_pageIndex);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>换页（照上游 <c>setPageIndex</c>：选中项不在这一页时选这一页的第一张）。</summary>
    public void SetPage(int index)
    {
        if (_document == null) return;
        var settings = Settings;
        int count = CollagePdfGeometry.PageCount(_document.Items.Count, settings);
        index = Math.Clamp(index, 0, Math.Max(0, count - 1));
        int capacity = Math.Max(1, CollagePdfGeometry.Capacity(settings));
        int first = index * capacity;

        if (first < _document.Items.Count)
        {
            int selectedIndex = _document.IndexOf(SelectedId);
            if (selectedIndex < first || selectedIndex >= first + capacity)
                _document.Select(_document.Items[first].Id);
        }

        _pageIndex = index;
        Refresh();
        PageChanged?.Invoke(_pageIndex);
    }

    /// <summary>适应画布（照上游 <c>fitToCanvas</c>）。</summary>
    public void FitToCanvas()
    {
        CancelGesture();
        _zoom = 1;
        _offset = default;
        Geometry();
    }

    /// <summary>Esc / 关窗时收干净手势。</summary>
    public void CancelGesture()
    {
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Arrow;
        }

        if (_gestureId != 0 || _targetId != 0)
        {
            _gestureId = 0;
            _gestureCell = -1;
            _targetId = 0;
            _moved = false;
            _handle = FrameHandle.None;
            Cursor = Cursors.Arrow;
            InvalidateVisual();
        }

        // Esc 打断拖动后 `OnMouseUp` 会因为 `_gestureId == 0` 早退，捕获必须在这里还回去，
        // 否则画布会一直吃鼠标（缩略图 / 按钮之后全点不动）。
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void Geometry()
    {
        _available = new Rect(Inset, Inset, Math.Max(1, ActualWidth - Inset * 2),
            Math.Max(1, ActualHeight - Inset - BottomReserve));

        if (_page.IsValid)
        {
            double modelWidth = _page.PageMillimeters.Width;
            double modelHeight = _page.PageMillimeters.Height;
            double scale = Math.Min(_available.Width / modelWidth, _available.Height / modelHeight);
            if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;

            _fitScale = scale;
            _viewScale = _fitScale * _zoom;
            var size = new Size(modelWidth * _viewScale, modelHeight * _viewScale);
            _paper = new Rect(
                _available.X + _available.Width / 2 + _offset.X - size.Width / 2,
                _available.Y + _available.Height / 2 + _offset.Y - size.Height / 2,
                size.Width, size.Height);
        }
        else
        {
            _paper = Rect.Empty;
            _fitScale = 1;
            _viewScale = 1;
        }

        InvalidateVisual();
        LayoutChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        CancelGesture();
        Geometry();
    }

    private Rect Mapped(CollagePdfRect rect) =>
        new(_paper.X + rect.X * _viewScale, _paper.Y + rect.Y * _viewScale,
            rect.Width * _viewScale, rect.Height * _viewScale);

    private CollagePdfCell? CellOf(long id)
    {
        foreach (var cell in _page.Cells)
            if (cell.ItemId == id && cell.ItemId != 0) return cell;
        return null;
    }

    private Rect ClipRect(CollagePdfCell cell) => Mapped(cell.ClipMillimeters);

    private Rect ImageRect(CollagePdfCell cell) =>
        _gestureCell >= 0 && cell.ItemId == _gestureId && _moved
            ? new Rect(_paper.X + _pendingImage.X * _viewScale,
                _paper.Y + _pendingImage.Y * _viewScale, _pendingImage.Width * _viewScale,
                _pendingImage.Height * _viewScale)
            : Mapped(cell.ImageMillimeters);

    /// <summary>光标下最上面那格的图 Id（格内空白也算）。</summary>
    public long HitCell(Point position)
    {
        foreach (var cell in _page.Cells)
        {
            if (cell.ItemId != 0 && ClipRect(cell).Contains(position)) return cell.ItemId;
        }

        return 0;
    }

    private long HitImage(Point position)
    {
        foreach (var cell in _page.Cells)
        {
            if (cell.ItemId != 0 && ClipRect(cell).Contains(position) &&
                ImageRect(cell).Contains(position))
                return cell.ItemId;
        }

        return 0;
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (_document == null || _document.Items.Count == 0) return;

        if (!_page.IsValid)
        {
            var area = new Rect(Inset, Inset, Math.Max(1, bounds.Width - Inset * 2),
                Math.Max(1, bounds.Height - Inset - BottomReserve));
            var text = new FormattedText(string.IsNullOrEmpty(_page.Error) ? "PDF 页面设置无效" :
                    _page.Error, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiTypeface,
                13, ErrorBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = Math.Max(1, area.Width),
                TextAlignment = TextAlignment.Center,
            };
            dc.DrawText(text, new Point(area.X, area.Y + (area.Height - text.Height) / 2));
            return;
        }

        dc.DrawRectangle(White, null, _paper);

        // 选中图在**自己格子外面**的部分画 25% 透明（照上游：先整张半透明，再逐格正常画）
        var selected = CellOf(SelectedId);
        if (selected != null)
        {
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(bounds), new RectangleGeometry(ClipRect(selected.Value)));
            dc.PushClip(outside);
            dc.PushOpacity(0.25);
            DrawItem(dc, ImageRect(selected.Value), _document.Items[selected.Value.ItemIndex]);
            dc.Pop();
            dc.Pop();
        }

        foreach (var cell in _page.Cells)
        {
            if (cell.ItemId == 0 || cell.ItemIndex < 0 || cell.ItemIndex >= _document.Items.Count)
                continue;

            var clip = ClipRect(cell);
            if (clip.Width <= 0 || clip.Height <= 0) continue;

            dc.PushClip(new RectangleGeometry(clip));
            DrawItem(dc, ImageRect(cell), _document.Items[cell.ItemIndex]);
            dc.Pop();
        }

        dc.DrawRectangle(null, BorderPen, InsetRect(_paper, 0.5));

        // 格线画在图片上方：相邻两张共享的那条边不会被其中一张盖掉
        foreach (var cell in _page.Cells)
        {
            if (cell.ItemId == 0) continue;
            dc.DrawRectangle(null, CellPen, InsetRect(ClipRect(cell), 0.5));
        }

        if (_targetId != 0 && _targetId != _gestureId)
        {
            var target = CellOf(_targetId);
            if (target != null)
                dc.DrawRectangle(MarqueeFill, SelectionPen, ClipRect(target.Value));
        }

        if (CellOf(SelectedId) is { } frame)
            DrawSelection(dc, ImageRect(frame));
    }

    private void DrawItem(DrawingContext dc, Rect target, CollageItem item)
    {
        if (target.Width <= 0 || target.Height <= 0) return;

        var preview = item.Preview;
        if (preview.PixelWidth <= 0 || preview.PixelHeight <= 0) return;

        dc.PushTransform(new MatrixTransform(CollagePdfRender.MatrixOf(item.Transform,
            preview.PixelWidth, preview.PixelHeight, target)));
        dc.DrawImage(preview, new Rect(0, 0, preview.PixelWidth, preview.PixelHeight));
        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc, Rect image)
    {
        if (image.IsEmpty) return;

        var frame = Rect.Inflate(image, FramePad, FramePad);
        dc.DrawRectangle(null, SelectionPen, frame);

        double half = HandleSize / 2;
        foreach (var corner in new[]
                 {
                     frame.TopLeft, frame.TopRight, frame.BottomRight, frame.BottomLeft,
                 })
        {
            dc.DrawRoundedRectangle(White, SelectionPen,
                new Rect(corner.X - half, corner.Y - half, HandleSize, HandleSize), 2, 2);
        }
    }

    // ============ 鼠标 ============

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Middle)
        {
            CancelGesture();
            _panning = true;
            _panLast = e.GetPosition(this);
            Cursor = Cursors.Hand;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        if (!_editing) return;
        if (_page.IsValid && _document != null)
        {
            Focus();
            CancelGesture();

            Point position = e.GetPosition(this);
            long selected = SelectedId;
            var bounds = SelectionFrame;
            var handle = HandleAt(bounds, position);
            long id = handle == FrameHandle.None ? HitImage(position) : selected;
            _document.Select(id);
            if (id == 0)
            {
                e.Handled = true;
                return;
            }

            for (int index = 0; index < _page.Cells.Count; index++)
            {
                var cell = _page.Cells[index];
                if (cell.ItemId != id) continue;

                _gestureId = id;
                _gestureCell = index;
                _handle = handle;
                _press = position;
                _initialImage = ToRect(cell.ImageMillimeters);
                _pendingImage = _initialImage;
                var item = _document.Items[cell.ItemIndex];
                _pendingPlacement = new CollagePdfPlacement(
                    cell.ImageMillimeters.CenterX - cell.ClipMillimeters.CenterX,
                    cell.ImageMillimeters.CenterY - cell.ClipMillimeters.CenterY,
                    cell.ImageMillimeters.Width / Math.Max(1, item.TransformedSize.Width));
                break;
            }

            Cursor = handle == FrameHandle.None ? Cursors.SizeAll : CursorFor(handle);
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_document != null && _document.Items.Count > 0)
        {
            // 页面无效（设置校验不过）时仍能点空白清选择，不至于连换图都不行。
            _document.Select(HitCell(e.GetPosition(this)));
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);

        if (_panning)
        {
            _offset += position - _panLast;
            _panLast = position;
            Geometry();
            e.Handled = true;
            return;
        }

        if (_gestureId == 0 || _document == null || e.LeftButton != MouseButtonState.Pressed)
        {
            var handle = HandleAt(SelectionFrame, position);
            Cursor = handle == FrameHandle.None ? Cursors.Arrow : CursorFor(handle);
            return;
        }

        Vector distance = position - _press;
        if (!_moved && Math.Abs(distance.X) + Math.Abs(distance.Y) < DragStart) return;
        _moved = true;

        if (_gestureCell < 0 || _gestureCell >= _page.Cells.Count) return;
        var cell = _page.Cells[_gestureCell];
        Vector delta = distance / Math.Max(1e-9, _viewScale);

        if (_handle == FrameHandle.None)
        {
            _pendingImage = new Rect(_initialImage.X + delta.X, _initialImage.Y + delta.Y,
                _initialImage.Width, _initialImage.Height);
            long target = HitCell(position);
            _targetId = target != _gestureId ? target : 0;
            Cursor = _targetId != 0 ? Cursors.SizeAll : Cursors.Hand;
        }
        else
        {
            _pendingImage = CollageCanvas.CenteredResize(_initialImage, _handle, delta, _viewScale);
            _targetId = 0;
        }

        if (cell.ItemIndex >= 0 && cell.ItemIndex < _document.Items.Count)
        {
            var item = _document.Items[cell.ItemIndex];
            _pendingPlacement = _pendingPlacement with
            {
                CenterOffsetXMm = _pendingImage.X + _pendingImage.Width / 2 - cell.ClipMillimeters.CenterX,
                CenterOffsetYMm = _pendingImage.Y + _pendingImage.Height / 2 - cell.ClipMillimeters.CenterY,
                MillimetersPerPixel = _pendingImage.Width / Math.Max(1, item.TransformedSize.Width),
            };
        }

        InvalidateVisual();
        LayoutChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            Cursor = Cursors.Arrow;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || _gestureId == 0) return;

        long id = _gestureId;
        bool moved = _moved;
        var placement = _pendingPlacement;
        long releasedOver = HitCell(e.GetPosition(this));
        long target = _handle == FrameHandle.None && releasedOver != id ? releasedOver : 0;

        CancelGesture();
        Geometry();

        if (moved && _editing && _document != null)
        {
            if (target != 0)
                _document.Swap(id, target);
            else if (placement.IsValid)
                _document.SetPdfPlacement(id, placement);
        }

        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        CancelGesture();
        if (!_page.IsValid || _viewScale <= 0) return;

        double steps = e.Delta / 120.0;
        if (steps == 0) return;

        Point position = e.GetPosition(this);
        double nextZoom = Math.Clamp(_zoom * Math.Pow(1.15, steps), 0.05, 16);
        double nextScale = _fitScale * nextZoom;
        var nextOrigin = new Point(
            position.X - (position.X - _paper.X) * (nextScale / _viewScale),
            position.Y - (position.Y - _paper.Y) * (nextScale / _viewScale));

        _zoom = nextZoom;
        _offset = new Point(
            nextOrigin.X - (_available.X + _available.Width / 2 -
                            _page.PageMillimeters.Width * nextScale / 2),
            nextOrigin.Y - (_available.Y + _available.Height / 2 -
                            _page.PageMillimeters.Height * nextScale / 2));
        Geometry();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_panning || _gestureId != 0) return;
        Cursor = Cursors.Arrow;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            CancelGesture();
            Geometry();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.F or Key.Home)
        {
            FitToCanvas();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.PageUp or Key.PageDown)
        {
            SetPage(_pageIndex + (e.Key == Key.PageUp ? -1 : 1));
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_editing && SelectedId != 0) RemoveRequested?.Invoke(SelectedId);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            if (_document == null || _document.Items.Count == 0) return;
            int index = _document.IndexOf(SelectedId);
            int next = Math.Clamp(index + (e.Key is Key.Left or Key.Up ? -1 : 1), 0,
                _document.Items.Count - 1);
            _document.Select(_document.Items[next].Id);
            e.Handled = true;
        }
    }

    // ============ 命中 ============

    /// <summary>
    /// 选中框命中（照上游 <c>snowCanvasImageSelectionHandleAt</c>）：四个角 12×12 优先，
    /// 再是四条 12 宽的命中带；都不中返回 <see cref="FrameHandle.None"/>（= 拖图 / 交换）。
    /// </summary>
    private static FrameHandle HandleAt(Rect image, Point point)
    {
        if (image.IsEmpty) return FrameHandle.None;

        var frame = Rect.Inflate(image, FramePad, FramePad);
        var corners = new[]
        {
            (FrameHandle.TopLeft, frame.TopLeft), (FrameHandle.TopRight, frame.TopRight),
            (FrameHandle.BottomRight, frame.BottomRight), (FrameHandle.BottomLeft, frame.BottomLeft),
        };
        foreach (var (handle, at) in corners)
        {
            if (Math.Abs(point.X - at.X) <= HandleHit && Math.Abs(point.Y - at.Y) <= HandleHit)
                return handle;
        }

        if (point.Y >= frame.Top - HandleHit && point.Y <= frame.Top + HandleHit &&
            point.X >= frame.Left - HandleHit && point.X <= frame.Right + HandleHit)
            return FrameHandle.Top;
        if (point.X >= frame.Right - HandleHit && point.X <= frame.Right + HandleHit &&
            point.Y >= frame.Top - HandleHit && point.Y <= frame.Bottom + HandleHit)
            return FrameHandle.Right;
        if (point.Y >= frame.Bottom - HandleHit && point.Y <= frame.Bottom + HandleHit &&
            point.X >= frame.Left - HandleHit && point.X <= frame.Right + HandleHit)
            return FrameHandle.Bottom;
        if (point.X >= frame.Left - HandleHit && point.X <= frame.Left + HandleHit &&
            point.Y >= frame.Top - HandleHit && point.Y <= frame.Bottom + HandleHit)
            return FrameHandle.Left;

        return FrameHandle.None;
    }

    private static Cursor CursorFor(FrameHandle handle) => handle switch
    {
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

    // ============ 小工具 ============

    private static Rect ToRect(CollagePdfRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static Rect InsetRect(Rect rect, double amount) =>
        new(rect.X + amount, rect.Y + amount, Math.Max(0, rect.Width - amount * 2),
            Math.Max(0, rect.Height - amount * 2));

}
