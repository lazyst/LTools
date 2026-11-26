using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro.Collage;

namespace LTools.Views.CapturePro;

/// <summary>
/// PDF「分页拼接」的页面网格（照上游 <c>ImageStitchingPdfPageGrid</c>）：一行卡片 = 一页，
/// 卡片里按 <see cref="CollagePdfGeometry.PageLayout"/> 画出这一页的纸面 / 内容区 / 图像，
/// 左上角是页码徽标，底部是文件名。
/// </summary>
/// <remarks>
/// <para>
/// 卡片 220×332，间距 16，左右各留 16；可用宽度放不下时按整列收缩、放得下就居中，
/// 所以窗口一宽出来列数自己会增加（照上游 <c>refresh()</c> 的 <c>columns</c> / <c>leadingInset</c>）。
/// </para>
/// <para>
/// 手势：单击选中、拖出 4px 开始重排（虚线占位 + 半透明幽灵 + 靠近上下边缘自动滚），
/// 方向键 / Home / End 换页并把目标滚进视野，Enter / Space 看原图，Delete / Backspace 删除。
/// 悬停或卡片有焦点时右下角浮出「替换 / 删除」两个 28×28 小钮。滚动条自绘（10 宽）。
/// </para>
/// </remarks>
internal sealed class CollagePdfPageGrid : FrameworkElement
{
    private const double GridInset = 16;
    private const double GridGap = 16;
    private const double PreferredCardWidth = 220;
    private const double CardHeight = 332;
    private const double EdgeScrollZone = 48;
    private const double ScrollbarWidth = 10;
    private const double ToolbarReserve = 56;
    private const double ActionButton = 28;
    private const double DragStart = 4;

    private static readonly Typeface UiTypeface = new("Segoe UI");

    private static readonly Brush White = ThemeBrush.Frozen(Colors.White);
    private static readonly Brush BadgeFill = ThemeBrush.Frozen(Color.FromArgb(0x82, 0, 0, 0));
    private static readonly Brush GhostShade = ThemeBrush.Frozen(Color.FromArgb(0x18, 0, 0, 0));

    private CollageDocument? _document;
    private bool _editing = true;
    private double _pixelsPerDip = 1;

    private int _columns = 1;
    private double _cardWidth = PreferredCardWidth;
    private double _leadingInset = GridInset;
    private double _contentHeight;
    private double _scroll;
    private double _maxScroll;
    private bool _overflow;

    private int _hoverIndex = -1;
    private int _hoverButton;

    private long _pressedId;
    private Point _press;
    private bool _pressed;

    private long _dragId;
    private Point _dragPoint;
    private int _insertion = -1;
    private DispatcherTimer? _scrollTimer;

    private bool _scrollbarDrag;
    private Point _scrollbarPress;
    private double _scrollbarStart;

    public CollagePdfPageGrid()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
    }

    // ============ 契约 ============

    public event Action<long>? SelectionRequested;

    public event Action<long>? ReplaceRequested;

    public event Action<long>? RemoveRequested;

    public event Action<long>? PreviewRequested;

    /// <summary>选中卡片在控件坐标系里的矩形（空 = 没有选中 / 被滚出视野）。</summary>
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

            CancelDrag();
            Refresh();
        }
    }

    /// <summary>能不能编辑（加载 / 导出时整块关掉，只留选中与预览）。</summary>
    public bool Editing
    {
        get => _editing;
        set
        {
            if (_editing == value) return;
            _editing = value;
            if (!value) CancelDrag();
            InvalidateVisual();
        }
    }

    /// <summary>列数（冒烟读）。</summary>
    internal int ColumnCount => _columns;

    /// <summary>当前滚动偏移（冒烟读）。</summary>
    internal double ScrollOffset => _scroll;

    /// <summary>正在插入的下标（-1 = 没在拖）。</summary>
    internal int InsertionIndex => _insertion;

    /// <summary>正在重排或拖滚动条（窗口的 Esc 先问它，别直接关窗）。</summary>
    public bool IsGesturing => _dragId != 0 || _scrollbarDrag;

    /// <summary>选中卡片的矩形（控件坐标；空 = 不可见）。</summary>
    public Rect SelectedCardRect
    {
        get
        {
            int index = IndexOf(_document is null ? 0 : _document.SelectedId);
            if (index < 0) return Rect.Empty;
            var rect = CardRect(index);
            var viewport = new Rect(0, 0, ActualWidth, ActualHeight);
            return rect.IntersectsWith(viewport) ? rect : Rect.Empty;
        }
    }

    /// <summary>第 <paramref name="index"/> 张卡片的矩形（控件坐标，可超出可视区）。</summary>
    internal Rect CardRectAt(int index) => CardRect(index);

    /// <summary>重新布局并重绘（尺寸 / 文档变化后调用）。</summary>
    public void Refresh()
    {
        EnsureLayout();
        InvalidateVisual();
        LayoutChanged?.Invoke();
    }

    /// <summary>把某一张滚进视野（照上游 <c>navigate</c> 的滚动规则）。</summary>
    public void ScrollTo(long id)
    {
        int index = IndexOf(id);
        if (index < 0) return;

        EnsureLayout();
        var bounds = ContentRect(index);
        double unobscured = Math.Max(1, ActualHeight - ToolbarReserve - GridInset * 2);
        if (bounds.Top < _scroll + GridInset)
            _scroll = bounds.Top - GridInset;
        else if (bounds.Bottom > _scroll + unobscured)
            _scroll = bounds.Bottom - unobscured;

        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
        InvalidateVisual();
        LayoutChanged?.Invoke();
    }

    /// <summary>Esc / 关窗 / 重排时把拖动收干净（不提交）。</summary>
    public void CancelDrag()
    {
        _scrollTimer?.Stop();
        _pressed = false;
        _pressedId = 0;
        _dragId = 0;
        _insertion = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    // ============ 布局 ============

    private void OnDocumentChanged()
    {
        if (_dragId == 0) CancelDrag();
        Refresh();
    }

    private int Count => _document?.Items.Count ?? 0;

    private int IndexOf(long id)
    {
        if (id == 0 || _document == null) return -1;
        for (int i = 0; i < _document.Items.Count; i++)
        {
            if (_document.Items[i].Id == id) return i;
        }

        return -1;
    }

    private void EnsureLayout()
    {
        double viewport = Math.Max(1, ActualWidth);
        int count = Count;

        for (int pass = 0; pass < 2; pass++)
        {
            double available = Math.Max(1, viewport - GridInset * 2);
            _columns = Math.Max(1, (int)((available + GridGap) / (PreferredCardWidth + GridGap)));
            _cardWidth = Math.Min(PreferredCardWidth, available);
            double occupied = _columns * _cardWidth + (_columns - 1) * GridGap;
            _leadingInset = Math.Max(0, (viewport - occupied) / 2);

            int rows = (count + _columns - 1) / _columns;
            _contentHeight = GridInset + rows * (CardHeight + GridGap) + ToolbarReserve + GridInset;
            bool overflow = _contentHeight > ActualHeight + 0.5;
            if (!overflow || pass == 1)
            {
                _overflow = overflow;
                break;
            }

            // 出现纵向滚动条时可用宽度要让出它（否则卡片右缘会被压住）。
            viewport = Math.Max(1, ActualWidth - ScrollbarWidth);
        }

        _maxScroll = Math.Max(0, _contentHeight - ActualHeight);
        _scroll = Math.Clamp(_scroll, 0, _maxScroll);
    }

    /// <summary>内容坐标（未减滚动偏移）的卡片矩形。</summary>
    private Rect ContentRect(int index) =>
        new(_leadingInset + (index % _columns) * (_cardWidth + GridGap),
            GridInset + (index / _columns) * (CardHeight + GridGap), _cardWidth, CardHeight);

    /// <summary>控件坐标（已减滚动偏移）的卡片矩形。</summary>
    private Rect CardRect(int index)
    {
        var rect = ContentRect(index);
        return new Rect(rect.X, rect.Y - _scroll, rect.Width, rect.Height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Refresh();
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        var bounds = new Rect(RenderSize);
        dc.DrawRectangle(Brushes.Transparent, null, bounds);
        if (_document == null || bounds.Width <= 0 || bounds.Height <= 0) return;

        _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureLayout();

        var items = _document.Items;
        for (int index = 0; index < items.Count; index++)
        {
            var rect = CardRect(index);
            if (rect.Bottom < 0 || rect.Top > bounds.Height) continue;
            if (index != IndexOf(_dragId) || _dragId == 0)
                DrawCard(dc, items[index], index, rect, index == IndexOf(_document.SelectedId));
        }

        if (_insertion >= 0) DrawInsertion(dc);

        if (_dragId != 0)
        {
            int index = IndexOf(_dragId);
            if (index >= 0)
            {
                double scale = 0.5;
                double width = _cardWidth * scale;
                double height = CardHeight * scale;
                var origin = new Point(_dragPoint.X - width / 2, _dragPoint.Y - height / 2);
                dc.PushOpacity(0.92);
                dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawRoundedRectangle(GhostShade, null,
                    new Rect(0, 0, _cardWidth, CardHeight), 4, 4);
                DrawCard(dc, items[index], index, new Rect(0, 0, _cardWidth, CardHeight), false,
                    false);
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
        }

        if (_overflow) DrawScrollbar(dc, bounds);
    }

    private void DrawCard(DrawingContext dc, CollageItem item, int index, Rect card, bool selected,
        bool actions = true)
    {
        var primary = Brush("CapturePrimary", Color.FromRgb(0x16, 0x77, 0xFF));
        var secondary = Brush("CaptureBorderSecondary", Color.FromRgb(0xF0, 0xF0, 0xF0));
        var fillAlter = Brush("SurfaceAltBrush", Color.FromRgb(0xFA, 0xFA, 0xFA));
        var ink = Brush("TextBrush", Color.FromRgb(0x26, 0x26, 0x26));

        var outer = new Rect(card.X + 1, card.Y + 1, card.Width - 2, card.Height - 2);
        var border = new Pen(selected ? primary : secondary, selected ? 2 : 1);
        dc.DrawRoundedRectangle(fillAlter, border, outer, 4, 4);

        // 纸面 + 图像 + 内容区（照上游 PdfPageCard.positionChildren）
        var settings = _document?.LastValidPdfSettings ?? CollagePdfSettings.Default;
        var layout = CollagePdfGeometry.PageLayout(item.TransformedSize, settings);
        if (layout.IsValid)
        {
            var available = new Rect(card.X + 10, card.Y + 10, Math.Max(1, card.Width - 20),
                Math.Max(1, card.Height - 48));
            var paperSize = Fit(layout.PageMillimeters.Width, layout.PageMillimeters.Height,
                available.Size);
            var paper = new Rect(available.X + (available.Width - paperSize.Width) / 2,
                available.Y + (available.Height - paperSize.Height) / 2, paperSize.Width,
                paperSize.Height);

            dc.DrawRectangle(White, new Pen(secondary, 1), paper);

            double scale = paper.Width / layout.PageMillimeters.Width;
            var content = new Rect(paper.X + layout.ContentMillimeters.X * scale,
                paper.Y + layout.ContentMillimeters.Y * scale,
                layout.ContentMillimeters.Width * scale, layout.ContentMillimeters.Height * scale);
            var image = new Rect(paper.X + layout.ImageMillimeters.X * scale,
                paper.Y + layout.ImageMillimeters.Y * scale,
                layout.ImageMillimeters.Width * scale, layout.ImageMillimeters.Height * scale);

            if (image.Width > 0 && image.Height > 0)
            {
                var preview = item.Preview;
                dc.PushClip(new RectangleGeometry(paper));
                dc.PushTransform(new MatrixTransform(CollagePdfRender.MatrixOf(item.Transform,
                    preview.PixelWidth, preview.PixelHeight, image)));
                dc.DrawImage(preview, new Rect(0, 0, preview.PixelWidth, preview.PixelHeight));
                dc.Pop();
                dc.Pop();
            }

            dc.DrawRectangle(null, new Pen(secondary, 1), Inset(content, 0.5));

            DrawBadge(dc, paper, (index + 1).ToString(CultureInfo.InvariantCulture));
        }

        // 底部文件名（照上游 labelRect）
        var label = new Rect(card.X + 10, card.Bottom - 29, Math.Max(1, card.Width - 20), 20);
        DrawText(dc, item.Label, 12, ink, label);

        if (actions && (_hoverIndex == index || IsFocusedCard(index)))
        {
            var (replace, remove) = ActionRects(card);
            DrawActionButton(dc, replace, "IconReplace",
                Brush("CapturePrimary", Color.FromRgb(0x16, 0x77, 0xFF)),
                _hoverIndex == index && _hoverButton == 1);
            DrawActionButton(dc, remove, "IconTrash",
                Brush("CaptureDanger", Color.FromRgb(0xFF, 0x4D, 0x4F)),
                _hoverIndex == index && _hoverButton == 2);
        }
    }

    private bool IsFocusedCard(int index) =>
        IsKeyboardFocused && _document != null && index == IndexOf(_document.SelectedId);

    private void DrawBadge(DrawingContext dc, Rect paper, string text)
    {
        double width = Math.Max(24, Measure(text, 12) + 12);
        var rect = new Rect(paper.X + 4, paper.Y + 4, width, 22);
        dc.DrawRoundedRectangle(BadgeFill, null, rect, 4, 4);
        DrawText(dc, text, 12, White, rect, TextAlignment.Center);
    }

    private void DrawText(DrawingContext dc, string text, double size, Brush brush, Rect rect,
        TextAlignment alignment = TextAlignment.Center)
    {
        if (rect.Width <= 1 || rect.Height <= 1) return;

        var formatted = new FormattedText(text ?? "", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, UiTypeface, size, brush, _pixelsPerDip)
        {
            MaxTextWidth = rect.Width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = alignment,
        };
        dc.DrawText(formatted, new Point(rect.X, rect.Y + (rect.Height - formatted.Height) / 2));
    }

    private double Measure(string text, double size) => new FormattedText(text,
        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, UiTypeface, size, Brushes.Black,
        _pixelsPerDip).WidthIncludingTrailingWhitespace;

    private void DrawActionButton(DrawingContext dc, Rect rect, string iconKey, Brush accent,
        bool hovered)
    {
        var surface = Brush("SurfaceBrush", Colors.White);
        dc.DrawEllipse(hovered ? accent : surface, new Pen(accent, 1),
            new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2,
            rect.Height / 2);

        var geometry = TryFindResource(iconKey) as Geometry;
        if (geometry == null) return;

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        double target = 14;
        double scale = Math.Min(target / bounds.Width, target / bounds.Height);
        var map = new Matrix();
        map.Translate(-bounds.X - bounds.Width / 2, -bounds.Y - bounds.Height / 2);
        map.Scale(scale, scale);
        map.Translate(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        dc.PushTransform(new MatrixTransform(map));
        dc.DrawGeometry(hovered ? surface : accent, null, geometry);
        dc.Pop();
    }

    private void DrawInsertion(DrawingContext dc)
    {
        int count = Count;
        if (count == 0) return;

        bool trailing = _insertion >= count;
        var bounds = CardRect(trailing ? count - 1 : _insertion);
        double x = trailing ? bounds.Right + GridGap / 2 : bounds.Left - GridGap / 2;
        var primary = Brush("CapturePrimary", Color.FromRgb(0x16, 0x77, 0xFF));

        var vertical = new Point(x, bounds.Bottom);
        var top = new Point(x, bounds.Top);
        dc.DrawLine(new Pen(primary, 3), top, vertical);
        dc.DrawEllipse(primary, null, top, 3, 3);
        dc.DrawEllipse(primary, null, vertical, 3, 3);
    }

    private void DrawScrollbar(DrawingContext dc, Rect bounds)
    {
        double track = Math.Max(1, bounds.Height);
        double thumb = ThumbHeight();
        double travel = Math.Max(0, track - thumb);
        double offset = _maxScroll > 0 ? travel * (_scroll / _maxScroll) : 0;
        var rect = new Rect(bounds.Right - 7, offset, 4, thumb);
        var brush = Brush("CaptureTextDisabled", Color.FromArgb(0x40, 0, 0, 0));
        dc.DrawRoundedRectangle(brush, null, rect, 2, 2);
    }

    // ============ 鼠标 ============

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);

        if (_scrollbarDrag)
        {
            double travel = Math.Max(1, ActualHeight - ThumbHeight());
            double delta = position.Y - _scrollbarPress.Y;
            _scroll = Math.Clamp(_scrollbarStart + delta * (_maxScroll / travel), 0, _maxScroll);
            InvalidateVisual();
            LayoutChanged?.Invoke();
            return;
        }

        if (_dragId != 0)
        {
            _dragPoint = position;
            UpdateInsertion(position);
            return;
        }

        if (_pressed && _editing && e.LeftButton == MouseButtonState.Pressed)
        {
            Vector distance = position - _press;
            if (Math.Abs(distance.X) + Math.Abs(distance.Y) >= DragStart)
            {
                _dragId = _pressedId;
                _dragPoint = position;
                UpdateInsertion(position);
                EnsureScrollTimer().Start();
                InvalidateVisual();
                return;
            }
        }

        int index = CardAt(position);
        int button = 0;
        if (index >= 0 && (_hoverIndex == index || IsFocusedCard(index)))
        {
            var (replace, remove) = ActionRects(CardRect(index));
            if (replace.Contains(position)) button = 1;
            else if (remove.Contains(position)) button = 2;
        }

        if (index != _hoverIndex || button != _hoverButton)
        {
            _hoverIndex = index;
            _hoverButton = button;
            Cursor = button != 0 ? Cursors.Hand : index >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Point position = e.GetPosition(this);

        if (_overflow && e.GetPosition(this).X >= ActualWidth - ScrollbarWidth)
        {
            if (ScrollbarThumbRect().Contains(position))
            {
                _scrollbarDrag = true;
                _scrollbarPress = position;
                _scrollbarStart = _scroll;
                CaptureMouse();
                e.Handled = true;
                return;
            }

            _scroll = Math.Clamp(_scroll + (position.Y < ScrollbarThumbRect().Top ? -1 : 1) *
                Math.Max(1, ActualHeight), 0, _maxScroll);
            InvalidateVisual();
            LayoutChanged?.Invoke();
            e.Handled = true;
            return;
        }

        int index = CardAt(position);
        if (index < 0 || _document == null) return;

        var item = _document.Items[index];
        var (replaceRect, removeRect) = ActionRects(CardRect(index));
        bool shown = _hoverIndex == index || IsFocusedCard(index);

        if (shown && replaceRect.Contains(position))
        {
            if (_editing) ReplaceRequested?.Invoke(item.Id);
            e.Handled = true;
            return;
        }

        if (shown && removeRect.Contains(position))
        {
            if (_editing) RemoveRequested?.Invoke(item.Id);
            e.Handled = true;
            return;
        }

        Focus();
        SelectionRequested?.Invoke(item.Id);
        _pressed = true;
        _pressedId = item.Id;
        _press = position;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_scrollbarDrag)
        {
            _scrollbarDrag = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (_dragId != 0)
        {
            long source = _dragId;
            int insertion = _insertion;
            EndDrag();

            int index = IndexOf(source);
            if (index >= 0 && insertion >= 0)
            {
                int target = Math.Clamp(insertion > index ? insertion - 1 : insertion, 0, Count - 1);
                _document?.Move(source, target);
            }

            e.Handled = true;
            return;
        }

        if (_pressed)
        {
            _pressed = false;
            _pressedId = 0;
            if (IsMouseCaptured) ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressed = false;
        _pressedId = 0;
        _dragId = 0;
        _insertion = -1;
        _scrollbarDrag = false;
        _scrollTimer?.Stop();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragId != 0 || _scrollbarDrag) return;
        if (_hoverIndex < 0 && _hoverButton == 0) return;
        _hoverIndex = -1;
        _hoverButton = 0;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_maxScroll <= 0) return;

        CancelDrag();
        _scroll = Math.Clamp(_scroll - e.Delta / 120.0 * 60, 0, _maxScroll);
        InvalidateVisual();
        LayoutChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_document == null || Count == 0) return;

        if (e.Key == Key.Escape && (_dragId != 0 || _scrollbarDrag))
        {
            CancelDrag();
            e.Handled = true;
            return;
        }

        long selected = _document.SelectedId;
        int index = IndexOf(selected);

        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.Home:
            case Key.End:
            {
                if (index < 0) index = 0;
                if (e.Key == Key.Left) index--;
                else if (e.Key == Key.Right) index++;
                else if (e.Key == Key.Up) index -= _columns;
                else if (e.Key == Key.Down) index += _columns;
                else if (e.Key == Key.Home) index = 0;
                else index = Count - 1;

                index = Math.Clamp(index, 0, Count - 1);
                long next = _document.Items[index].Id;
                SelectionRequested?.Invoke(next);
                ScrollTo(next);
                e.Handled = true;
                return;
            }
            case Key.Enter:
            case Key.Space:
                if (selected != 0) PreviewRequested?.Invoke(selected);
                e.Handled = true;
                return;
            case Key.Delete:
            case Key.Back:
                if (_editing && selected != 0) RemoveRequested?.Invoke(selected);
                e.Handled = true;
                return;
        }
    }

    // ============ 拖动 ============

    private DispatcherTimer EnsureScrollTimer()
    {
        if (_scrollTimer != null) return _scrollTimer;

        _scrollTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _scrollTimer.Tick += (_, _) => AutoScroll();
        return _scrollTimer;
    }

    private void AutoScroll()
    {
        if (_dragId == 0 || _maxScroll <= 0)
        {
            _scrollTimer?.Stop();
            return;
        }

        double delta = 0;
        if (_dragPoint.Y < EdgeScrollZone)
            delta = -Math.Clamp(Math.Round((EdgeScrollZone - _dragPoint.Y) / 2), 1, 24);
        else if (_dragPoint.Y > ActualHeight - EdgeScrollZone)
            delta = Math.Clamp(Math.Round((_dragPoint.Y - ActualHeight + EdgeScrollZone) / 2), 1, 24);
        else
        {
            _scrollTimer?.Stop();
            return;
        }

        _scroll = Math.Clamp(_scroll + delta, 0, _maxScroll);
        UpdateInsertion(_dragPoint);
    }

    private void UpdateInsertion(Point position)
    {
        int count = Count;
        if (count == 0)
        {
            _insertion = -1;
            InvalidateVisual();
            return;
        }

        double y = position.Y + _scroll;
        int row = Math.Max(0, (int)Math.Floor((y - GridInset) / (CardHeight + GridGap)));
        double relativeX = position.X - _leadingInset;
        int column = Math.Clamp((int)Math.Floor(relativeX / (_cardWidth + GridGap)), 0,
            _columns - 1);
        int pointed = Math.Min(count, row * _columns + column);

        int insertion = pointed;
        double rowOffset = y - GridInset - row * (CardHeight + GridGap);
        if (y < GridInset)
            insertion = 0;
        else if (rowOffset >= CardHeight)
            insertion = Math.Min(count, (row + 1) * _columns);
        else if (pointed < count && position.X >= CardRect(pointed).X + CardRect(pointed).Width / 2)
            insertion++;

        _insertion = Math.Clamp(insertion, 0, count);
        InvalidateVisual();
    }

    private void EndDrag()
    {
        _scrollTimer?.Stop();
        _scrollTimer = null;
        _pressed = false;
        _pressedId = 0;
        _dragId = 0;
        _insertion = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    // ============ 命中 ============

    private int CardAt(Point position)
    {
        int count = Count;
        for (int index = 0; index < count; index++)
        {
            if (CardRect(index).Contains(position)) return index;
        }

        return -1;
    }

    private static (Rect Replace, Rect Remove) ActionRects(Rect card)
    {
        var remove = new Rect(card.Right - 4 - ActionButton, card.Bottom - 4 - ActionButton,
            ActionButton, ActionButton);
        var replace = new Rect(remove.X - ActionButton - 4, remove.Y, ActionButton, ActionButton);
        return (replace, remove);
    }

    private double ThumbHeight()
    {
        double track = Math.Max(1, ActualHeight);
        return Math.Max(28, track * track / Math.Max(track, _contentHeight));
    }

    private Rect ScrollbarThumbRect()
    {
        double thumb = ThumbHeight();
        double travel = Math.Max(0, ActualHeight - thumb);
        double offset = _maxScroll > 0 ? travel * (_scroll / _maxScroll) : 0;
        return new Rect(ActualWidth - ScrollbarWidth, offset, ScrollbarWidth, thumb);
    }

    // ============ 小工具 ============

    private static Size Fit(double width, double height, Size available)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
            return new Size(0, 0);

        double scale = Math.Min(available.Width / width, available.Height / height);
        if (!double.IsFinite(scale) || scale <= 0) return new Size(0, 0);
        return new Size(width * scale, height * scale);
    }

    private static Rect Inset(Rect rect, double amount) =>
        Inset(rect, amount, amount);

    private static Rect Inset(Rect rect, double x, double y) =>
        new(rect.X + x, rect.Y + y, Math.Max(0, rect.Width - x * 2),
            Math.Max(0, rect.Height - y * 2));

    private Brush Brush(string key, Color fallback)
    {
        if (TryFindResource(key) is Brush brush) return brush;
        return ThemeBrush.Frozen(fallback);
    }

}
