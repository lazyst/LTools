using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 标注几何的纯函数集合：命中测试、外接矩形、文字量宽。
/// **不碰窗口、不碰位图**，所以 <c>--selftest</c> 能直接断言（见 <c>Core/SelfTest</c> 的 capturePro 组）。
/// </summary>
internal static class MarkGeometry
{
    /// <summary>文字背景 / 命中框在排版尺寸外留的边（DIP）。</summary>
    public const double TextPad = 4;

    private static readonly Typeface Face =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>两点决定的规范化矩形（左上 → 右下）。</summary>
    public static Rect RectOf(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static Rect Inflate(Rect rect, double amount)
    {
        var r = rect;
        r.Inflate(amount, amount);
        return r;
    }

    /// <summary>点列的外接矩形，按线宽向外扩（描边会出界）。</summary>
    public static Rect BoundsOf(IReadOnlyList<Point> points, double width)
    {
        if (points.Count == 0) return Rect.Empty;
        double left = points[0].X, right = points[0].X, top = points[0].Y, bottom = points[0].Y;
        for (int i = 1; i < points.Count; i++)
        {
            var p = points[i];
            if (p.X < left) left = p.X;
            if (p.X > right) right = p.X;
            if (p.Y < top) top = p.Y;
            if (p.Y > bottom) bottom = p.Y;
        }
        double half = Math.Max(0, width) / 2;
        return new Rect(left - half, top - half, right - left + width, bottom - top + width);
    }

    public static List<Point> Shift(IReadOnlyList<Point> points, Vector delta)
    {
        var list = new List<Point>(points.Count);
        foreach (var p in points) list.Add(p + delta);
        return list;
    }

    /// <summary>箭头三角头的尺寸（线越粗头越大，但不会细成一根针）。</summary>
    public static double HeadSize(double width) => Math.Max(10, width * 3.2);

    /// <summary>点到线段所在线段的最短距离。</summary>
    public static double DistanceToSegment(Point point, Point a, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        if (len2 <= double.Epsilon) return (point - a).Length;
        double t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        return (point - new Point(a.X + t * dx, a.Y + t * dy)).Length;
    }

    public static bool NearSegment(Point point, Point a, Point b, double tolerance) =>
        DistanceToSegment(point, a, b) <= tolerance;

    /// <summary>点是否落在折线附近（含单点 / 折角）。</summary>
    public static bool NearPolyline(IReadOnlyList<Point> points, Point point, double tolerance)
    {
        if (points.Count == 0) return false;
        if (points.Count == 1) return (point - points[0]).Length <= tolerance;
        for (int i = 1; i < points.Count; i++)
            if (NearSegment(point, points[i - 1], points[i], tolerance)) return true;
        return false;
    }

    /// <summary>文字排版尺寸。用固定 pixelsPerDip = 1：显示与导出都画几何（不画字形），与 DPI 无关。</summary>
    public static Size MeasureText(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text)) return new Size(fontSize, fontSize * 1.35);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, fontSize, Brushes.Black, 1.0);
        return new Size(Math.Ceiling(ft.WidthIncludingTrailingWhitespace), Math.Ceiling(ft.Height));
    }

    /// <summary>序号徽标里数字的字号。</summary>
    public static double SerialFontSize(int number) => number >= 100 ? 11 : number >= 10 ? 13 : 15;

    // —— 下面三个给渲染用：把「形状描述」变成可画的几何 ——

    /// <summary>折线 → 可描边的 PathGeometry。</summary>
    public static PathGeometry Polyline(IReadOnlyList<Point> points, bool closed = false)
    {
        var geo = new PathGeometry();
        if (points.Count == 0) return geo;
        var figure = new PathFigure { StartPoint = points[0], IsClosed = closed, IsFilled = false };
        if (points.Count == 1)
            figure.Segments.Add(new LineSegment(points[0], true));
        else
        {
            var poly = new PolyLineSegment();
            for (int i = 1; i < points.Count; i++) poly.Points.Add(points[i]);
            figure.Segments.Add(poly);
        }
        geo.Figures.Add(figure);
        return geo;
    }

    public static Geometry ShapeOf(Rect rect, bool ellipse) => ellipse
        ? new EllipseGeometry(rect)
        : new RectangleGeometry(rect);

    /// <summary>箭头三角头的三个顶点。</summary>
    public static Point[] ArrowHead(Point start, Point end, double width)
    {
        double size = HeadSize(width);
        var dir = end - start;
        if (dir.Length <= 0.01) return Array.Empty<Point>();
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);
        var back = end - dir * size;
        return new[]
        {
            end,
            back + normal * (size * 0.42),
            back - normal * (size * 0.42),
        };
    }
}
