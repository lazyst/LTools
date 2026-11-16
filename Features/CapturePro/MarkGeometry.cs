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

    // ============ 八个把手：选区与标注共用 ============

    /// <summary>八个把手（点中判定按这个顺序：角优先，角比边更好按）。</summary>
    public static readonly FrameHandle[] Handles =
    {
        FrameHandle.TopLeft, FrameHandle.TopRight, FrameHandle.BottomLeft, FrameHandle.BottomRight,
        FrameHandle.Top, FrameHandle.Bottom, FrameHandle.Left, FrameHandle.Right,
    };

    /// <summary>把手方块边长（DIP）：选区与标注共用同一个尺寸，看着才是同一套。</summary>
    public const double HandleSize = 9;

    /// <summary>把手在框上的位置。</summary>
    public static Point GripPoint(Rect frame, FrameHandle handle)
    {
        double cx = frame.X + frame.Width / 2;
        double cy = frame.Y + frame.Height / 2;
        return handle switch
        {
            FrameHandle.TopLeft => new Point(frame.Left, frame.Top),
            FrameHandle.TopRight => new Point(frame.Right, frame.Top),
            FrameHandle.BottomLeft => new Point(frame.Left, frame.Bottom),
            FrameHandle.BottomRight => new Point(frame.Right, frame.Bottom),
            FrameHandle.Top => new Point(cx, frame.Top),
            FrameHandle.Bottom => new Point(cx, frame.Bottom),
            FrameHandle.Left => new Point(frame.Left, cy),
            FrameHandle.Right => new Point(frame.Right, cy),
            _ => new Point(cx, cy),
        };
    }

    /// <summary>点中哪个把手；都不中且在框里就是 <see cref="FrameHandle.Move"/>。</summary>
    public static FrameHandle HitFrame(Rect frame, Point point, double gripHit)
    {
        foreach (var handle in Handles)
        {
            Point at = GripPoint(frame, handle);
            if (Math.Abs(point.X - at.X) <= gripHit && Math.Abs(point.Y - at.Y) <= gripHit) return handle;
        }

        return frame.Contains(point) ? FrameHandle.Move : FrameHandle.None;
    }

    /// <summary>
    /// 拖把手之后的新框：只动被拖的那条边 / 角，夹在 <paramref name="limit"/> 里，
    /// 小于 <paramref name="min"/> 时原样退回（既不把框拖成 0，也不让它翻过来）。
    /// </summary>
    public static Rect ResizeFrame(Rect frame, FrameHandle handle, Vector delta, double min, Rect limit)
    {
        double left = frame.Left, top = frame.Top, right = frame.Right, bottom = frame.Bottom;

        if (handle is FrameHandle.Left or FrameHandle.TopLeft or FrameHandle.BottomLeft) left += delta.X;
        if (handle is FrameHandle.Right or FrameHandle.TopRight or FrameHandle.BottomRight) right += delta.X;
        if (handle is FrameHandle.Top or FrameHandle.TopLeft or FrameHandle.TopRight) top += delta.Y;
        if (handle is FrameHandle.Bottom or FrameHandle.BottomLeft or FrameHandle.BottomRight) bottom += delta.Y;

        left = Math.Clamp(left, limit.Left, limit.Right);
        right = Math.Clamp(right, limit.Left, limit.Right);
        top = Math.Clamp(top, limit.Top, limit.Bottom);
        bottom = Math.Clamp(bottom, limit.Top, limit.Bottom);

        if (right - left < min || bottom - top < min) return frame;
        return new Rect(left, top, right - left, bottom - top);
    }

    // ============ 标注的缩放 ============

    /// <summary>选中框在标注外接框外再让多少（DIP）：贴着描边画会让「选中了」看不出来。</summary>
    public const double FramePad = 3;

    /// <summary>画选中框 / 摆手柄用的矩形（判定与绘制都取它，看到的就是按得到的）。</summary>
    public static Rect PaddedFrame(Rect frame) => Inflate(frame, FramePad);

    /// <summary>
    /// 旧框 → 新框时「线宽 / 字号 / 块大小」的倍数：取两个方向的几何平均，
    /// 这样只横向拉长时不会把线画成一条歪斜的纸条。
    /// </summary>
    public static double ScaleOf(Rect from, Rect to)
    {
        double sx = from.Width > 0.01 ? to.Width / from.Width : 1;
        double sy = from.Height > 0.01 ? to.Height / from.Height : 1;
        return Math.Max(0.05, Math.Sqrt(Math.Abs(sx * sy)));
    }

    /// <summary>把框里的点按比例搬进新框（两个方向各算各的，所以能拉伸）。</summary>
    public static Point MapPoint(Point point, Rect from, Rect to)
    {
        double tx = from.Width > 0.01 ? (point.X - from.Left) / from.Width : 0;
        double ty = from.Height > 0.01 ? (point.Y - from.Top) / from.Height : 0;
        return new Point(to.Left + tx * to.Width, to.Top + ty * to.Height);
    }

    public static List<Point> MapAll(IReadOnlyList<Point> points, Rect from, Rect to)
    {
        var list = new List<Point>(points.Count);
        foreach (var p in points) list.Add(MapPoint(p, from, to));
        return list;
    }

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
