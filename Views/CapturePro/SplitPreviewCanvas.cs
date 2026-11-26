using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Split;

namespace LTools.Views.CapturePro;

/// <summary>
/// 切图预览画布（照上游 <c>ImageSplitPreviewCanvas</c>）：棋盘格底 + 图片 + 重叠带 + 虚线
/// 切分线 + 上下 / 左侧标尺 + 坐标徽标 + 每格序号。
/// </summary>
/// <remarks>
/// <para>
/// 坐标系是元素自己的 DIP：<see cref="ToImage"/> / <see cref="ToViewport"/> 只做缩放与平移，
/// 标尺、徽标、命中容差都在屏幕空间算，所以缩小到 10% 时线仍然好点、字仍然可读。
/// </para>
/// <para>
/// 交互照上游：点标尺 = 加一条线、拖线 = 移动（跨过相邻线时序号跟着变、落在别的线上就停住）、
/// Delete / Backspace = 删掉选中的线、Esc = 放弃这次拖动、F / Home = 适应窗口、
/// Ctrl+Z / Ctrl+Y = 撤销重做、中键 = 平移、滚轮 = 以光标为中心缩放。
/// </para>
/// </remarks>
internal sealed class SplitPreviewCanvas : FrameworkElement
{
    private const double RulerHeight = 28;
    private const double RulerWidth = 38;
    private const double PositionBadgeHeight = 22;
    private const double PositionBadgeSpacing = (RulerHeight - PositionBadgeHeight) / 2;
    private const double LineHitDistance = 6;
    private const double GuideSpacing = 2;
    private const double BadgeHeight = 22;
    private const double BadgeInset = 4;
    private const double BadgeRadius = 4;

    private static readonly Color SplitLineColor = Color.FromRgb(0x40, 0x96, 0xFF);
    private static readonly SolidColorBrush LineBrush = ThemeBrush.Frozen(SplitLineColor);
    private static readonly Pen SolidPen = ThemeBrush.Pen(SplitLineColor, 1);
    private static readonly Pen DashPen = ThemeBrush.DashPen(SplitLineColor, 1);
    private static readonly SolidColorBrush BandBrush = ThemeBrush.Frozen(Color.FromArgb(51, 0x40, 0x96, 0xFF));
    private static readonly SolidColorBrush BadgeBrush = ThemeBrush.Frozen(Color.FromArgb(130, 0, 0, 0));
    private static readonly SolidColorBrush WhiteBrush = ThemeBrush.Frozen(Colors.White);
    private static readonly Typeface UiTypeface = new("Segoe UI");

    private SplitDocument? _document;
    private BitmapSource? _image;
    private SplitSettings? _lastSettings;

    private double _zoom = 1;
    private Point _offset;
    private bool _automaticFit = true;

    private bool _panning;
    private Point _last;
    private bool _dragging;
    private bool _editing = true;

    private bool _selectedVertical = true;
    private int _selectedIndex = -1;
    private bool _hoveredVertical = true;
    private int _hoveredIndex = -1;
    private Point? _hoverPosition;

    private bool _checkerReady;
    private Brush? _checker;

    public SplitPreviewCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    /// <summary>画布展示的文档（换文档会重新适应窗口）。</summary>
    public SplitDocument? Document
    {
        get => _document;
        set
        {
            if (ReferenceEquals(_document, value)) return;
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            Refresh();
        }
    }

    /// <summary>能不能编辑切分线（正在载入 / 导出 / 算线时关闭）。</summary>
    public void SetEditingEnabled(bool enabled)
    {
        if (_editing == enabled) return;
        if (!enabled && _dragging) _document?.CancelLineDrag();
        _dragging = false;
        _editing = enabled;
        _hoveredIndex = -1;
        _hoverPosition = null;
        Cursor = null;
        InvalidateVisual();
    }

    /// <summary>当前缩放倍率（1 = 1 图像像素 1 DIP）。</summary>
    internal double Zoom => _zoom;

    /// <summary>图像左上角在画布里的位置（DIP）。</summary>
    internal Point Offset => _offset;

    /// <summary>适应窗口：四周留 24，最多 100%（照上游 <c>fitToCanvas</c>）。</summary>
    public void FitToCanvas()
    {
        var image = _document?.Image;
        if (image == null) return;

        var viewport = Viewport();
        double areaWidth = Math.Max(1, viewport.Width - 48);
        double areaHeight = Math.Max(1, viewport.Height - 48);

        _automaticFit = true;
        _zoom = Math.Min(1, Math.Min(areaWidth / image.PixelWidth, areaHeight / image.PixelHeight));
        if (!(_zoom > 0) || double.IsInfinity(_zoom)) _zoom = 1;

        _offset = new Point(
            viewport.X + (viewport.Width - image.PixelWidth * _zoom) / 2,
            viewport.Y + (viewport.Height - image.PixelHeight * _zoom) / 2);

        ApplyCamera();
    }

    /// <summary>键盘入口；返回 true = 这次按键归画布（取消拖动 / 删线 / 适应 / 撤销重做）。</summary>
    internal bool HandleKey(KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (e.Key == Key.Escape && _dragging)
        {
            _document?.CancelLineDrag();
            _dragging = false;
            Cursor = null;
            ReleaseIfCaptured();
            InvalidateVisual();
            return true;
        }

        if ((e.Key == Key.Delete || e.Key == Key.Back) && _editing && _selectedIndex >= 0)
        {
            _document?.RemoveLine(_selectedVertical, _selectedIndex);
            _selectedIndex = -1;
            _hoveredIndex = -1;
            InvalidateVisual();
            return true;
        }

        if (e.Key == Key.F || e.Key == Key.Home)
        {
            FitToCanvas();
            return true;
        }

        if (control && e.Key == Key.Z && !shift)
        {
            _document?.Undo();
            return true;
        }

        if (control && (e.Key == Key.Y || (e.Key == Key.Z && shift)))
        {
            _document?.Redo();
            return true;
        }

        return false;
    }

    // ============ 坐标 ============

    private Rect Viewport() => new(RulerWidth, RulerHeight,
        Math.Max(1, ActualWidth - RulerWidth), Math.Max(1, ActualHeight - RulerHeight));

    private Rect ImageRect()
    {
        var image = _document?.Image;
        return image == null
            ? Rect.Empty
            : new Rect(_offset, new Size(image.PixelWidth * _zoom, image.PixelHeight * _zoom));
    }

    private Rect VisibleImageRect() => Rect.Intersect(ImageRect(), Viewport());

    private Point ToImage(Point point) =>
        new((point.X - _offset.X) / _zoom, (point.Y - _offset.Y) / _zoom);

    private Point ToViewport(Point point) =>
        new(_offset.X + point.X * _zoom, _offset.Y + point.Y * _zoom);

    private Rect RulerRect(bool vertical)
    {
        var visible = VisibleImageRect();
        if (visible.IsEmpty) return Rect.Empty;
        return vertical
            ? new Rect(visible.Left, visible.Top - RulerHeight, visible.Width, RulerHeight)
            : new Rect(visible.Left - RulerWidth, visible.Top, RulerWidth, visible.Height);
    }

    private bool Allows(bool vertical)
    {
        var document = _document;
        if (document == null) return false;
        var direction = document.State.Settings.Direction;
        return direction == SplitDirection.Grid ||
               (vertical && direction == SplitDirection.Vertical) ||
               (!vertical && direction == SplitDirection.Horizontal);
    }

    private static int RoundedCoordinate(double value) =>
        (int)Math.Clamp(Math.Floor(value + 0.5), 0, int.MaxValue);

    // ============ 文档变化 ============

    private void OnDocumentChanged() => Refresh();

    private void Refresh()
    {
        var image = _document?.Image;
        bool newImage = !ReferenceEquals(image, _image);
        if (newImage)
        {
            _image = image;
            _selectedIndex = -1;
            _hoveredIndex = -1;
            _hoverPosition = null;
            FitToCanvas();
        }
        else if (_document != null &&
                 !_document.State.Settings.Equals(_lastSettings))
        {
            _selectedIndex = -1;
            _hoveredIndex = -1;
        }

        _lastSettings = _document?.State.Settings.Clone();

        var layout = _document?.Layout;
        if (layout != null)
        {
            if (_selectedIndex >= Lines(layout, _selectedVertical).Count) _selectedIndex = -1;
            if (_hoveredIndex >= Lines(layout, _hoveredVertical).Count) _hoveredIndex = -1;
        }

        InvalidateVisual();
    }

    private static SplitLines Lines(SplitLayout layout, bool vertical) =>
        vertical ? layout.Vertical : layout.Horizontal;

    private void ApplyCamera()
    {
        RenderOptions.SetBitmapScalingMode(this,
            _zoom < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        InvalidateVisual();
    }

    // ============ 绘制 ============

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width < 2 || bounds.Height < 2) return;

        dc.DrawRectangle(Resource("BgBrush", Brushes.White), null, bounds);

        var document = _document;
        var image = document?.Image;
        if (document == null || image == null) return;

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var viewport = Viewport();
        var imageRect = ImageRect();
        var layout = document.Layout;

        dc.PushClip(new RectangleGeometry(viewport));
        dc.DrawRectangle(Checker(), null, imageRect);
        dc.DrawImage(image, imageRect);

        if (layout.Overlap > 0) DrawBands(dc, layout, imageRect);
        dc.DrawRectangle(null, new Pen(Resource("BorderBrush", Brushes.Gray), 1), imageRect);

        var ruler = HoveredRulerLine();
        DrawLines(dc, layout, imageRect, ruler);
        DrawSequenceBadges(dc, layout, pixelsPerDip);
        dc.Pop();

        DrawRulers(dc, imageRect, pixelsPerDip);

        var position = OverlayLine(ruler);
        if (position is { } line) DrawPositionBadge(dc, line, pixelsPerDip);
    }

    /// <summary>重叠带：每条切分线两侧各铺 <c>overlap</c> 像素（照上游 20% 蓝）。</summary>
    private void DrawBands(DrawingContext dc, SplitLayout layout, Rect imageRect)
    {
        var visible = Rect.Intersect(imageRect, Viewport());
        if (visible.IsEmpty) return;

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            foreach (bool vertical in new[] { true, false })
            {
                var lines = Lines(layout, vertical);
                if (lines.Count == 0) continue;

                double first = vertical ? visible.Left : visible.Top;
                double last = vertical ? visible.Right : visible.Bottom;
                double origin = vertical ? _offset.X : _offset.Y;
                int start = lines.LowerBound(RoundedCoordinate((first - origin) / _zoom));
                for (int i = start; i < lines.Count; i++)
                {
                    double position = ToViewport(new Point(vertical ? lines[i] : 0,
                        vertical ? 0 : lines[i])).X;
                    if (!vertical) position = ToViewport(new Point(0, lines[i])).Y;

                    double begin = position - layout.Overlap * _zoom;
                    double end = position + layout.Overlap * _zoom;
                    if (end < first || begin > last) continue;

                    if (vertical)
                    {
                        ctx.BeginFigure(new Point(Math.Max(begin, first - 1), visible.Top), true, true);
                        ctx.LineTo(new Point(Math.Max(begin, first - 1), visible.Bottom), false, false);
                        ctx.LineTo(new Point(Math.Min(end, last + 1), visible.Bottom), false, false);
                        ctx.LineTo(new Point(Math.Min(end, last + 1), visible.Top), false, false);
                    }
                    else
                    {
                        ctx.BeginFigure(new Point(visible.Left, Math.Max(begin, first - 1)), true, true);
                        ctx.LineTo(new Point(visible.Right, Math.Max(begin, first - 1)), false, false);
                        ctx.LineTo(new Point(visible.Right, Math.Min(end, last + 1)), false, false);
                        ctx.LineTo(new Point(visible.Left, Math.Min(end, last + 1)), false, false);
                    }
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(BandBrush, null, geometry);
    }

    /// <summary>切分线：普通是虚线、选中 / 悬停 / 标尺预览是实线（照上游）。</summary>
    private void DrawLines(DrawingContext dc, SplitLayout layout, Rect imageRect,
        (bool Vertical, int Coordinate)? ruler)
    {
        foreach (bool vertical in new[] { true, false })
        {
            var lines = Lines(layout, vertical);
            if (lines.Count == 0) continue;

            int selected = _selectedVertical == vertical ? _selectedIndex : -1;
            int hovered = _hoveredVertical == vertical ? _hoveredIndex : -1;
            int preview = ruler is { } line && line.Vertical == vertical ? line.Coordinate : -1;

            double visibleFirst = vertical
                ? ToImage(new Point(Viewport().Left, Viewport().Top)).X
                : ToImage(new Point(Viewport().Left, Viewport().Top)).Y;
            double visibleLast = vertical
                ? ToImage(new Point(Viewport().Right, Viewport().Bottom)).X
                : ToImage(new Point(Viewport().Left, Viewport().Bottom)).Y;

            int index = LowerBoundReal(lines, visibleFirst);
            while (index < lines.Count && lines[index] <= visibleLast)
            {
                int coordinate = lines[index];
                if (index != selected && index != hovered && coordinate != preview)
                    DrawLine(dc, vertical, coordinate, imageRect, false);

                // 线密到亚像素时直接跳过一段（照上游的 kGuideSpacing）
                index = LowerBoundReal(lines, coordinate + Math.Max(1.0, GuideSpacing / _zoom));
            }

            DrawHighlighted(dc, lines, selected, preview, vertical, imageRect, visibleFirst, visibleLast);
            if (hovered != selected)
                DrawHighlighted(dc, lines, hovered, preview, vertical, imageRect, visibleFirst, visibleLast);
            if (preview >= 0)
                DrawLine(dc, vertical, preview, imageRect, true);
        }
    }

    private void DrawHighlighted(DrawingContext dc, SplitLines lines, int index, int preview,
        bool vertical, Rect imageRect, double visibleFirst, double visibleLast)
    {
        if (index < 0 || index >= lines.Count) return;
        int coordinate = lines[index];
        if (coordinate == preview || coordinate < visibleFirst || coordinate > visibleLast) return;
        DrawLine(dc, vertical, coordinate, imageRect, true);
    }

    private void DrawLine(DrawingContext dc, bool vertical, int coordinate, Rect imageRect,
        bool highlighted)
    {
        double position = vertical
            ? ToViewport(new Point(coordinate, 0)).X
            : ToViewport(new Point(0, coordinate)).Y;

        if (vertical)
        {
            dc.DrawLine(highlighted ? SolidPen : DashPen,
                new Point(position, imageRect.Top), new Point(position, imageRect.Bottom));
        }
        else
        {
            dc.DrawLine(highlighted ? SolidPen : DashPen,
                new Point(imageRect.Left, position), new Point(imageRect.Right, position));
        }
    }

    /// <summary>每个「放得下」的格子左上角画序号（照上游 readableCells）。</summary>
    private void DrawSequenceBadges(DrawingContext dc, SplitLayout layout, double pixelsPerDip)
    {
        if (layout.Vertical.Count + 1 > 999) return;

        var readableColumns = ReadableCells(true, 26);
        var readableRows = ReadableCells(false, BadgeHeight + BadgeInset + 2);
        if (readableColumns.Count == 0 || readableRows.Count == 0) return;

        long columns = layout.Vertical.Count + 1;
        foreach (int row in readableRows)
        {
            foreach (int column in readableColumns)
            {
                var region = layout.BaseRegionAt(row * columns + column);
                if (region.Width <= 0 || region.Height <= 0) continue;

                var target = new Rect(ToViewport(new Point(region.X, region.Y)),
                    new Size(region.Width * _zoom, region.Height * _zoom));
                string label = (row * columns + column + 1).ToString(CultureInfo.InvariantCulture);

                var text = Text(label, 12, WhiteBrush, pixelsPerDip);
                double width = Math.Max(24, text.Width + 12);
                if (target.Width < width + BadgeInset + 2) continue;
                if (target.Top + BadgeHeight < Viewport().Top ||
                    target.Top > Viewport().Bottom ||
                    target.Left + width < Viewport().Left ||
                    target.Left > Viewport().Right)
                {
                    continue;
                }

                var badge = new Rect(target.Left + BadgeInset, target.Top + BadgeInset, width,
                    BadgeHeight);
                dc.DrawRoundedRectangle(BadgeBrush, null, badge, BadgeRadius, BadgeRadius);
                dc.DrawText(text, new Point(badge.Left + (badge.Width - text.Width) / 2,
                    badge.Top + (badge.Height - text.Height) / 2));
            }
        }
    }

    /// <summary>视口内「够宽」的格子序号（按最小跨度采样，不做整格遍历）。</summary>
    private List<int> ReadableCells(bool vertical, double minimumSpan)
    {
        var result = new List<int>();
        var document = _document;
        var layout = document?.Layout;
        if (document?.Image == null || layout == null) return result;

        var lines = Lines(layout, vertical);
        int dimension = vertical ? layout.Width : layout.Height;
        var viewport = Viewport();
        double first = vertical ? viewport.Left : viewport.Top;
        double last = vertical ? viewport.Right : viewport.Bottom;
        double origin = vertical ? _offset.X : _offset.Y;
        double step = minimumSpan / 2;

        int previous = -1;
        void Sample(double position)
        {
            double coordinate = (position - origin) / _zoom;
            if (coordinate < 0 || coordinate > dimension) return;

            int index = UpperBoundReal(lines, coordinate);
            if (index == previous) return;
            previous = index;

            int start = index == 0 ? 0 : lines[index - 1];
            int end = index == lines.Count ? dimension : lines[index];
            if ((end - start) * _zoom >= minimumSpan) result.Add(index);
        }

        for (double position = first; position < last; position += step) Sample(position);
        Sample(last);
        return result;
    }

    /// <summary>上下 / 左侧标尺：底色、边框、4 / 8 像素刻度、主刻度数字（左侧竖排）。</summary>
    private void DrawRulers(DrawingContext dc, Rect imageRect, double pixelsPerDip)
    {
        foreach (bool vertical in new[] { true, false })
        {
            var ruler = RulerRect(vertical);
            if (ruler.IsEmpty || ruler.Width <= 0 || ruler.Height <= 0) continue;

            dc.PushClip(new RectangleGeometry(ruler));
            dc.DrawRectangle(Resource("SurfaceBrush", Brushes.White), null, ruler);

            var separator = new Pen(Resource("BorderBrush", Brushes.Gray), 1);
            dc.DrawLine(separator, vertical
                ? new Point(ruler.Left, ruler.Bottom - 0.5)
                : new Point(ruler.Right - 0.5, ruler.Top), vertical
                ? new Point(ruler.Right, ruler.Bottom - 0.5)
                : new Point(ruler.Right - 0.5, ruler.Bottom));

            double desired = 80 / _zoom;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, desired))));
            double major = magnitude;
            foreach (double factor in new[] { 1.0, 2.0, 5.0, 10.0 })
            {
                if (magnitude * factor >= desired)
                {
                    major = magnitude * factor;
                    break;
                }
            }

            major = Math.Max(1, major);
            double minor = Math.Max(1, major / 5);

            double first = vertical
                ? ToImage(new Point(ruler.Left, ruler.Top)).Y
                : ToImage(new Point(ruler.Left, ruler.Top)).X;
            double last = vertical
                ? ToImage(new Point(ruler.Left, ruler.Bottom)).Y
                : ToImage(new Point(ruler.Right, ruler.Top)).X;
            double dimension = vertical ? imageRect.Height / _zoom : imageRect.Width / _zoom;
            double begin = Math.Max(0, Math.Ceiling(first / minor) * minor);

            var tickPen = new Pen(Resource("HintTextBrush", Brushes.Gray), 1);
            for (double coordinate = begin; coordinate <= Math.Min(last, dimension);
                 coordinate += minor)
            {
                var point = ToViewport(vertical
                    ? new Point(0, coordinate)
                    : new Point(coordinate, 0));
                bool isMajor = Math.Abs(coordinate / major - Math.Round(coordinate / major)) < .001;
                double length = isMajor ? 8 : 4;

                if (vertical)
                {
                    dc.DrawLine(tickPen, new Point(ruler.Right, point.Y),
                        new Point(ruler.Right - length, point.Y));
                }
                else
                {
                    dc.DrawLine(tickPen, new Point(point.X, ruler.Bottom),
                        new Point(point.X, ruler.Bottom - length));
                }

                if (!isMajor) continue;

                string label = RoundedCoordinate(coordinate).ToString(CultureInfo.InvariantCulture);
                var text = Text(label, 10, Resource("SecondaryTextBrush", Brushes.Gray),
                    pixelsPerDip);

                if (vertical)
                {
                    double span = Math.Min(text.Width + 6, ruler.Height);
                    double top = Math.Clamp(point.Y - span / 2, ruler.Top,
                        Math.Max(ruler.Top, ruler.Bottom - span));
                    dc.PushTransform(new RotateTransform(-90, ruler.Left + 16, top + span / 2));
                    dc.DrawText(text, new Point(ruler.Left + 16 - text.Width / 2, top + span / 2 - 8));
                    dc.Pop();
                }
                else
                {
                    double span = Math.Min(text.Width + 6, ruler.Width);
                    double left = Math.Clamp(point.X - span / 2, ruler.Left,
                        Math.Max(ruler.Left, ruler.Right - span));
                    dc.DrawText(text, new Point(left + (span - text.Width) / 2, ruler.Top + 1));
                }
            }

            dc.Pop();
        }
    }

    private void DrawPositionBadge(DrawingContext dc, (bool Vertical, int Coordinate) line,
        double pixelsPerDip)
    {
        string label = (line.Vertical ? "X: " : "Y: ") +
                       line.Coordinate.ToString(CultureInfo.InvariantCulture) + " px";
        var text = Text(label, 12, WhiteBrush, pixelsPerDip);
        double width = text.Width + 12;

        var ruler = RulerRect(line.Vertical);
        if (ruler.IsEmpty) return;

        var point = line.Vertical
            ? ToViewport(new Point(line.Coordinate, 0))
            : ToViewport(new Point(0, line.Coordinate));
        var center = line.Vertical
            ? new Point(point.X, ruler.Top + ruler.Height / 2)
            : new Point(ruler.Left + ruler.Width / 2, point.Y);
        if (!ruler.Contains(center)) return;

        double left = line.Vertical
            ? center.X - width / 2
            : ruler.Right - PositionBadgeSpacing - width;
        double top = line.Vertical
            ? ruler.Bottom - PositionBadgeSpacing - PositionBadgeHeight
            : center.Y - PositionBadgeHeight / 2;

        left = Math.Clamp(left, 0, Math.Max(0, ActualWidth - width));
        top = Math.Clamp(top, 0, Math.Max(0, ActualHeight - PositionBadgeHeight));

        var badge = new Rect(left, top, width, PositionBadgeHeight);
        dc.DrawRoundedRectangle(Resource("PrimaryBrush", Brushes.SteelBlue), null, badge, 3, 3);
        dc.DrawText(text, new Point(badge.Left + (badge.Width - text.Width) / 2,
            badge.Top + (badge.Height - text.Height) / 2));
    }

    // ============ 命中 ============

    private (bool Vertical, int Coordinate)? HoveredRulerLine()
    {
        if (_hoverPosition is not { } position || _dragging || _panning) return null;
        return RulerLineAt(position);
    }

    private (bool Vertical, int Coordinate)? RulerLineAt(Point point)
    {
        var document = _document;
        var image = document?.Image;
        if (!_editing || document == null || image == null || !document.Layout.IsValid)
            return null;

        var pixel = ToImage(point);
        foreach (bool vertical in new[] { true, false })
        {
            if (!Allows(vertical) || !RulerRect(vertical).Contains(point)) continue;
            int coordinate = RoundedCoordinate(vertical ? pixel.X : pixel.Y);
            int dimension = vertical ? image.PixelWidth : image.PixelHeight;
            if (coordinate > 0 && coordinate < dimension) return (vertical, coordinate);
        }

        return null;
    }

    /// <summary>光标下的切分线（离得最近、6 DIP 以内）。</summary>
    private (bool Vertical, int Index) Hit(Point point)
    {
        var document = _document;
        if (document?.Image == null) return (true, -1);

        var viewport = Viewport();
        if (!viewport.Contains(point) || RulerRect(true).Contains(point) ||
            RulerRect(false).Contains(point))
        {
            return (true, -1);
        }

        var imageRect = ImageRect();
        imageRect.Inflate(LineHitDistance, LineHitDistance);
        if (!imageRect.Contains(point)) return (true, -1);

        bool axis = true;
        int result = -1;
        double closest = LineHitDistance;
        var imagePoint = ToImage(point);

        foreach (bool vertical in new[] { true, false })
        {
            if (!Allows(vertical)) continue;

            var lines = Lines(document.Layout, vertical);
            double value = vertical ? imagePoint.X : imagePoint.Y;
            int candidate = LowerBoundReal(lines, value);
            foreach (int shift in new[] { -1, 0 })
            {
                int index = candidate + shift;
                if (index < 0 || index >= lines.Count) continue;

                double distance = Math.Abs(lines[index] - value) * _zoom;
                if (distance > closest) continue;
                closest = distance;
                result = index;
                axis = vertical;
            }
        }

        return (axis, result);
    }

    /// <summary>标尺预览 &gt; 悬停 &gt; 选中：坐标徽标显示的那一条（照上游 <c>positionLine</c>）。</summary>
    private (bool Vertical, int Coordinate)? OverlayLine(
        (bool Vertical, int Coordinate)? ruler)
    {
        if (ruler is { } preview) return preview;

        var layout = _document?.Layout;
        if (layout == null) return null;

        var lines = Lines(layout, _hoveredVertical);
        if (_hoveredIndex >= 0 && _hoveredIndex < lines.Count)
            return (_hoveredVertical, lines[_hoveredIndex]);

        lines = Lines(layout, _selectedVertical);
        return _selectedIndex >= 0 && _selectedIndex < lines.Count
            ? (_selectedVertical, lines[_selectedIndex])
            : null;
    }

    // ============ 鼠标 ============

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var point = e.GetPosition(this);
        _hoverPosition = point;

        if (e.ChangedButton == MouseButton.Middle)
        {
            _panning = true;
            _automaticFit = false;
            _last = point;
            CaptureMouse();
            Cursor = Cursors.ScrollAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left || !_editing || _document?.Image == null) return;

        if (RulerLineAt(point) is { } ruler)
        {
            if (_document.CreateLine(ruler.Vertical, ruler.Coordinate))
            {
                var lines = Lines(_document.Layout, ruler.Vertical);
                int index = LowerBoundReal(lines, ruler.Coordinate);
                if (index < lines.Count && lines[index] == ruler.Coordinate)
                {
                    _selectedVertical = ruler.Vertical;
                    _selectedIndex = index;
                    _hoveredIndex = -1;
                }
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hit = Hit(point);
        _selectedVertical = hit.Vertical;
        _selectedIndex = hit.Index;
        _hoveredIndex = -1;
        if (hit.Index >= 0 && _document.BeginLineDrag(hit.Vertical, hit.Index))
        {
            _dragging = true;
            CaptureMouse();
            Cursor = hit.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        _hoverPosition = point;

        if (_panning)
        {
            _offset += point - _last;
            _last = point;
            ApplyCamera();
            return;
        }

        if (_dragging)
        {
            var pixel = ToImage(point);
            _document?.MoveDraggedLine(RoundedCoordinate(
                _selectedVertical ? pixel.X : pixel.Y));
            return;
        }

        if (!_editing) return;

        var hit = Hit(point);
        _hoveredVertical = hit.Vertical;
        _hoveredIndex = hit.Index;

        if (hit.Index >= 0) Cursor = hit.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
        else if (HoveredRulerLine() != null) Cursor = Cursors.Cross;
        else Cursor = null;

        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.ChangedButton == MouseButton.Middle) _panning = false;

        if (e.ChangedButton == MouseButton.Left && _dragging)
        {
            _document?.EndLineDrag();
            _dragging = false;
        }

        if (!_panning && !_dragging)
        {
            ReleaseIfCaptured();
            Cursor = null;
        }

        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);

        // 捕获被系统收走（切窗 / 弹框）时按「已松手」收尾，别让文档一直停在拖动中
        if (_dragging)
        {
            _document?.EndLineDrag();
            _dragging = false;
        }

        _panning = false;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_document?.Image == null) return;

        var point = e.GetPosition(this);
        _hoverPosition = point;

        double steps = e.Delta / (double)Mouse.MouseWheelDeltaForOneLine;
        if (steps == 0) return;

        double next = Math.Clamp(_zoom * Math.Pow(1.15, steps), 1e-9, 16);
        if (Math.Abs(next - _zoom) < 1e-12) return;

        _offset = point - (point - _offset) * (next / _zoom);
        _zoom = next;
        _automaticFit = false;
        ApplyCamera();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        _hoveredIndex = -1;
        _hoverPosition = null;
        if (!_dragging && !_panning) Cursor = null;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_automaticFit) FitToCanvas();
        InvalidateVisual();
    }

    // ============ 小工具 ============

    private void ReleaseIfCaptured()
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private Brush Resource(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private FormattedText Text(string text, double size, Brush brush, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, UiTypeface, size,
            brush, pixelsPerDip);

    private Brush Checker()
    {
        if (_checkerReady && _checker != null) return _checker;

        var surface = Resource("SurfaceBrush", Brushes.White);
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
            Viewport = new Rect(0, 0, 20, 20),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();

        _checker = brush;
        _checkerReady = true;
        return brush;
    }

    /// <summary>第一个 <c>&gt;= value</c> 的下标（value 可以是小数）。</summary>
    private static int LowerBoundReal(SplitLines lines, double value)
    {
        int low = 0, high = lines.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (lines[mid] < value) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>第一个 <c>&gt; value</c> 的下标。</summary>
    private static int UpperBoundReal(SplitLines lines, double value)
    {
        int low = 0, high = lines.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (lines[mid] <= value) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>把 BitmapSource 包一层，只是为了用引用相等判断「换图了没有」。</summary>
    private sealed class BitmapSourceHolder(System.Windows.Media.Imaging.BitmapSource image)
    {
        public System.Windows.Media.Imaging.BitmapSource Image { get; } = image;
    }
}
