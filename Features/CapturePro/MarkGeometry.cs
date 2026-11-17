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

    /// <summary>选区边框的命中带宽度（DIP）：照 Snow Shot 的 <c>kSelectionEdgeTolerance</c>。</summary>
    public const double EdgeTolerance = 8;

    /// <summary>
    /// 选区边框命中（照 Snow Shot 的 <c>screenshotSelectionDragModeForPoint</c>）：
    /// 离任意一条边不超过容差就算**缩放**（角优先），再往里是 <see cref="FrameHandle.Move"/>，
    /// 框外超过容差 = 不命中。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 八个「把手」只是画出来给人看的提示；真正能抓的是**整条边**——以前只判八个点周围
    /// ±8 的方块，边中间那一段按下去会被当成「拖选区」，用户以为边缘拖不动。
    /// </para>
    /// <para>
    /// 判定顺序照上游：先看外扩容差框（远在框外的点不能被算成「抓上边」），再拼四个方向位，
    /// 角（两位都命中）优先于边。**比容差还窄的小框**按宽 / 高的 1/3 收窄容差，
    /// 否则四条边的判定带互相重叠、整块都落进「移动」，小选区反而永远改不了大小。
    /// </para>
    /// </remarks>
    public static FrameHandle HitFrameEdge(Rect frame, Point point, double edgeTolerance)
    {
        if (frame.Width <= 0 || frame.Height <= 0) return FrameHandle.None;

        // 远在框外的点：位判定只看「离某条边多近」，不加这道闸的话屏幕另一头点一下也算抓上边
        if (!Inflate(frame, edgeTolerance).Contains(point)) return FrameHandle.None;

        double tx = Math.Min(edgeTolerance, frame.Width / 3);
        double ty = Math.Min(edgeTolerance, frame.Height / 3);

        int position = 0;
        if (point.Y <= frame.Top + ty) position |= 0b1000;
        if (point.X >= frame.Right - tx) position |= 0b0100;
        if (point.Y >= frame.Bottom - ty) position |= 0b0010;
        if (point.X <= frame.Left + tx) position |= 0b0001;

        return position switch
        {
            0b1001 => FrameHandle.TopLeft,
            0b1100 => FrameHandle.TopRight,
            0b0110 => FrameHandle.BottomRight,
            0b0011 => FrameHandle.BottomLeft,
            0b1000 => FrameHandle.Top,
            0b0100 => FrameHandle.Right,
            0b0010 => FrameHandle.Bottom,
            0b0001 => FrameHandle.Left,
            _ => FrameHandle.Move,
        };
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
