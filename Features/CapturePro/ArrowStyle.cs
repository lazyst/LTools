using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>箭头类型（上游 <c>SnowCanvasArrowType</c>）：直线 / 曲线 / 折线。</summary>
internal enum ArrowType
{
    Straight,
    Curve,
    Elbow,
}

/// <summary>杆型（上游 <c>SnowCanvasArrowShaftType</c>）：等宽 / 往箭头方向渐宽。</summary>
internal enum ArrowShaft
{
    Plain,
    Tapered,
}

/// <summary>
/// 箭头的一整套画法（上游 <c>SnowCanvasArrowStyle</c> / <c>ArrowData</c>）。
/// **不可变**：改参数 = 换一个 <see cref="ArrowSpec"/>。
/// </summary>
internal readonly record struct ArrowSpec(
    Color Stroke,
    double Width,
    StrokeKind StrokeStyle,
    StrokeRendering Rendering,
    ArrowType Type,
    Arrowhead Start,
    Arrowhead End,
    double Ratio,
    ArrowShaft Shaft,
    Color OutlineColor,
    double OutlineWidth,
    double Opacity,
    uint Seed)
{
    /// <summary>外描边的默认色（上游 <c>default_arrow_outline_color</c>：与文字描边同一个 <c>#FFCCC7</c>）。</summary>
    public static readonly Color DefaultOutline = Color.FromRgb(0xFF, 0xCC, 0xC7);

    /// <summary>外描边宽上限（上游 <c>normalize_arrow_outline_width</c> 钳 0–72）。</summary>
    public const double MaxOutlineWidth = 72;

    /// <summary>
    /// 出厂值（上游 <c>screenshotCanvasStyleDefaults()</c>）：红线 / 线宽 2 / 实线 / 干净笔触 /
    /// **曲线** / 起点无箭头 / 终点三角箭头 / 比例 1 / 等宽杆 / 描边宽 0。
    /// </summary>
    public static readonly ArrowSpec Default = new(
        Color.FromRgb(0xF5, 0x22, 0x2D), 2, StrokeKind.Solid, StrokeRendering.Architect,
        ArrowType.Curve, Arrowhead.None, Arrowhead.Arrow, 1, ArrowShaft.Plain,
        DefaultOutline, 0, 1, 1);

    /// <summary>描边用的形状样式（填充不适用，笔触抖动跟着同一个 seed）。</summary>
    public ShapeSpec StrokeSpec => new(ShapeKind.Rectangle, Stroke, Width, StrokeStyle, Rendering,
        Colors.Transparent, FillKind.None, 0, Seed);

    /// <summary>越界钳回（线宽 1–72、比例 0.5–3、描边 0–72、不透明度 0–1）。</summary>
    public ArrowSpec Clamped() => this with
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 1, 72) : 2,
        Ratio = ArrowInk.Ratio(Ratio),
        OutlineWidth = double.IsFinite(OutlineWidth) ? Math.Clamp(OutlineWidth, 0, MaxOutlineWidth) : 0,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
    };
}

/// <summary>
/// 箭头的几何（移植 <c>snow-draw-engine-document/src/arrow/data.rs</c> 的路径生成 +
/// <c>render_core.rs</c> 的折线拐角 + <c>arrow/shaft.rs</c> 的渐宽杆）。纯计算，自检直接断言。
/// </summary>
internal static class ArrowGeometry
{
    /// <summary>折线拐角的圆角半径（上游 <c>ELBOW_ARROW_CORNER_RADIUS</c>）。</summary>
    public const double ElbowRadius = 16;

    /// <summary>曲线每段采样多少点（上游 <c>sampled_curve_points</c> 的 <c>steps_per_segment</c>）。</summary>
    public const int CurveSteps = 16;

    /// <summary>Catmull-Rom 的张力（上游 <c>curve_tension() = 1.0</c>）。</summary>
    public const double Tension = 1.0;

    /// <summary>
    /// 参数行里列出的 14 种端点（照上游 <c>buildArrowFamily</c> 的顺序；
    /// <see cref="Arrowhead.Square"/> / <see cref="Arrowhead.InvertedTriangle"/> 只给测距的端点用）。
    /// </summary>
    public static readonly Arrowhead[] Heads =
    {
        Arrowhead.None, Arrowhead.Arrow, Arrowhead.Bar, Arrowhead.Dot, Arrowhead.Circle,
        Arrowhead.CircleOutline, Arrowhead.IndentedTriangle, Arrowhead.Triangle,
        Arrowhead.TriangleOutline, Arrowhead.Diamond, Arrowhead.DiamondOutline,
        Arrowhead.CrowfootOne, Arrowhead.CrowfootMany, Arrowhead.CrowfootOneOrMany,
    };

    /// <summary>
    /// 落盘时用的点列：折线（Elbow）要 4 个点，另两种就首尾两点
    /// （上游 <c>default_elbow_arrow_points</c>；水平或垂直拖时退化成两点）。
    /// </summary>
    public static Point[] Points(Point start, Point end, ArrowType type) =>
        type == ArrowType.Elbow ? ElbowPoints(start, end) : new[] { start, end };

    /// <summary>折线的默认三点半：长边走一半再拐（上游 <c>default_elbow_arrow_points</c>）。</summary>
    public static Point[] ElbowPoints(Point start, Point end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (Math.Abs(dx) <= 1e-6 || Math.Abs(dy) <= 1e-6) return new[] { start, end };

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            double midX = (start.X + end.X) / 2;
            return new[]
            {
                start, new Point(midX, start.Y), new Point(midX, end.Y), end,
            };
        }

        double midY = (start.Y + end.Y) / 2;
        return new[]
        {
            start, new Point(start.X, midY), new Point(end.X, midY), end,
        };
    }

    /// <summary>
    /// 路径段：直线就一段一段连；曲线是 Catmull-Rom 转三次贝塞尔；折线的拐角是「直线 → 二次 → 直线」。
    /// </summary>
    public static List<InkSegment> Path(IReadOnlyList<Point> points, ArrowType type)
    {
        var list = new List<InkSegment>();
        if (points == null || points.Count < 2) return list;

        if (type == ArrowType.Elbow) return ElbowPath(points);
        if (type == ArrowType.Curve) return CurvePath(points);

        for (int i = 1; i < points.Count; i++) list.Add(InkSegment.Line(points[i - 1], points[i]));
        return list;
    }

    /// <summary>把曲线打成折线（命中测试与渐宽杆用）。</summary>
    public static List<Point> Flatten(IReadOnlyList<Point> points, ArrowType type)
    {
        var flat = new List<Point>();
        foreach (var segment in Path(points, type))
        {
            if (flat.Count == 0) flat.Add(segment.A);
            if (segment.Straight)
            {
                flat.Add(segment.B);
                continue;
            }

            for (int step = 1; step <= CurveSteps; step++)
                flat.Add(Bezier(segment, (double)step / CurveSteps));
        }

        if (flat.Count == 0 && points is { Count: > 0 }) flat.Add(points[0]);
        return flat;
    }

    /// <summary>
    /// 某一端的端点位置 / 方向 / 最后一段长度（上游 <c>arrowEndpointGeometry</c>）：
    /// 曲线按 t = 0.85（起点 0.15）处的切线，另两种按最后一段的弦。
    /// </summary>
    public static bool Endpoint(IReadOnlyList<Point> points, ArrowType type, bool start,
        out Point tip, out Vector direction, out double length)
    {
        tip = default;
        direction = default;
        length = 0;
        if (points == null || points.Count < 2) return false;

        if (type == ArrowType.Curve)
        {
            var path = Path(points, type);
            if (path.Count == 0) return false;

            var segment = start ? path[0] : path[^1];
            tip = start ? segment.A : segment.B;
            var previous = Bezier(segment, start ? 0.15 : 0.85);
            var delta = tip - previous;
            length = delta.Length;
            if (length <= 1e-6) return false;

            direction = delta / length;
            return true;
        }

        tip = start ? points[0] : points[^1];
        var other = start ? points[1] : points[^2];
        var span = tip - other;
        length = span.Length;
        if (length <= 1e-6) return false;

        direction = span / length;
        return true;
    }

    /// <summary>端点能不能做渐宽杆（上游 <c>supports_tapered_shaft</c>）。</summary>
    public static bool SupportsTapered(Arrowhead head) => head is
        Arrowhead.Arrow or Arrowhead.Triangle or Arrowhead.TriangleOutline or Arrowhead.IndentedTriangle;

    // ============ 曲线 / 折线 ============

    private static List<InkSegment> CurvePath(IReadOnlyList<Point> points)
    {
        var list = new List<InkSegment>();
        int last = points.Count - 1;
        for (int i = 0; i < last; i++)
        {
            Point previous = points[i == 0 ? 0 : i - 1];
            Point start = points[i];
            Point end = points[i + 1];
            Point next = points[i + 2 < points.Count ? i + 2 : last];

            var c1 = start + (end - previous) * (Tension / 6.0);
            var c2 = end - (next - start) * (Tension / 6.0);
            list.Add(InkSegment.Cubic(start, c1, c2, end));
        }
        return list;
    }

    /// <summary>折线：每个拐角处「进角点 → 二次曲线过拐点 → 出角点」（上游 <c>generate_elbow_arrow_path_commands</c>）。</summary>
    private static List<InkSegment> ElbowPath(IReadOnlyList<Point> points)
    {
        var list = new List<InkSegment>();
        if (points.Count < 2) return list;

        Point at = points[0];
        for (int index = 1; index < points.Count - 1; index++)
        {
            Point previous = points[index - 1];
            Point corner = points[index];
            Point next = points[index + 1];

            double radius = Math.Min(ElbowRadius,
                Math.Min((next - corner).Length / 2, (corner - previous).Length / 2));

            Point enter = IsHorizontal(corner, previous)
                ? new Point(previous.X < corner.X ? corner.X - radius : corner.X + radius, corner.Y)
                : new Point(corner.X, previous.Y < corner.Y ? corner.Y - radius : corner.Y + radius);

            Point exit = IsHorizontal(next, corner)
                ? new Point(next.X < corner.X ? corner.X - radius : corner.X + radius, corner.Y)
                : new Point(corner.X, next.Y < corner.Y ? corner.Y - radius : corner.Y + radius);

            list.Add(InkSegment.Line(at, enter));

            // 二次 → 三次：控制点各按 2/3 拉
            var c1 = enter + (corner - enter) * (2.0 / 3.0);
            var c2 = exit + (corner - exit) * (2.0 / 3.0);
            list.Add(InkSegment.Cubic(enter, c1, c2, exit));
            at = exit;
        }

        list.Add(InkSegment.Line(at, points[^1]));
        return list;
    }

    private static bool IsHorizontal(Point a, Point b) =>
        Math.Abs(a.X - b.X) > Math.Abs(a.Y - b.Y);

    private static Point Bezier(InkSegment segment, double t)
    {
        double u = 1 - t;
        return new Point(
            u * u * u * segment.A.X + 3 * u * u * t * segment.C1.X + 3 * u * t * t * segment.C2.X +
            t * t * t * segment.B.X,
            u * u * u * segment.A.Y + 3 * u * u * t * segment.C1.Y + 3 * u * t * t * segment.C2.Y +
            t * t * t * segment.B.Y);
    }

    // ============ 渐宽杆（上游 arrow/shaft.rs） ============

    /// <summary>一个箭头画出来的形状（渐宽杆要接着它算宽度）。</summary>
    public readonly record struct HeadShape(
        Arrowhead Head, Point Tip, Point A, Point B, Point Back, Point Neck, Vector Direction,
        double HalfWidth)
    {
        /// <summary>箭头轮廓（上游 <c>HeadGeometry::contour</c>）。</summary>
        public Point[] Contour() => Head == Arrowhead.IndentedTriangle
            ? new[] { Mix(Back, Tip, 0.25), A, Tip, B }
            : new[] { Back, A, Tip, B };
    }

    /// <summary>
    /// 按 <see cref="ArrowInk"/> 同一套尺寸算箭头形状（尖端 / 两肩 / 基部 / 颈部）。
    /// 渐宽杆拿它的「肩宽 ÷ 4」当杆在箭头那一端的半径。
    /// </summary>
    public static HeadShape? Head(Point tip, Vector direction, Arrowhead head, double strokeWidth,
        double ratio, double segmentLength)
    {
        if (head == Arrowhead.None || !(segmentLength > 0.01)) return null;
        if (direction.Length < 1e-9) return null;
        direction.Normalize();

        double stroke = Math.Max(0.1, strokeWidth);
        double size = ArrowInk.Size(head) * Math.Sqrt(Math.Max(stroke / 2.0, 1.0)) * ArrowInk.Ratio(ratio);
        double multiplier = ArrowInk.IsDiamond(head) ? 0.25 : 0.5;
        double minSize = Math.Min(size, segmentLength * multiplier);

        var back = tip - direction * minSize;
        double angle = ArrowInk.AngleDegrees(head) * Math.PI / 180.0;
        var a = ArrowInk.Rotate(tip, back, -angle);
        var b = ArrowInk.Rotate(tip, back, angle);
        var neck = head == Arrowhead.IndentedTriangle ? Mix(back, tip, 0.125) : back;
        return new HeadShape(head, tip, a, b, back, neck, direction, (a - b).Length * 0.25);
    }

    /// <summary>
    /// 渐宽杆的填充轮廓（多条闭合折线）：杆从「另一端的箭头宽度」线性长到本端箭头宽度，
    /// 本端箭头与杆连成一片；虚线 / 点线不连（箭头单独一块），照上游 <c>tapered_arrow_geometry</c>。
    /// 返回空 = 这条箭头不该走渐宽（没有可用箭头 / 端点类型不支持）。
    /// </summary>
    public static List<InkPart> Tapered(ArrowSpec spec, IReadOnlyList<Point> points)
    {
        if (spec.Shaft != ArrowShaft.Tapered || spec.Width <= 0.01) return new List<InkPart>();
        if (spec.Start == Arrowhead.None && spec.End == Arrowhead.None) return new List<InkPart>();
        if (!Supported(spec.Start) || !Supported(spec.End)) return new List<InkPart>();

        bool reverse = spec.End == Arrowhead.None;
        var flat = Flatten(points, spec.Type);
        if (reverse) flat.Reverse();
        if (flat.Count < 2) return new List<InkPart>();

        var distances = new List<double> { 0 };
        for (int i = 1; i < flat.Count; i++) distances.Add(distances[^1] + (flat[i] - flat[i - 1]).Length);
        double full = distances[^1];
        if (full < 1e-6) return new List<InkPart>();

        // 主箭头 = 有箭头的那一端（终点优先）；另一端有箭头就是次箭头
        var primaryHead = reverse ? spec.Start : spec.End;
        var secondaryHead = reverse ? spec.End : spec.Start;
        double primaryLength = PointLength(flat[^2], flat[^1]);
        var primary = Head(flat[^1], flat[^1] - flat[^2], primaryHead, spec.Width, spec.Ratio, primaryLength);
        if (primary == null) return new List<InkPart>();

        HeadShape? secondary = null;
        if (secondaryHead != Arrowhead.None)
        {
            double length = PointLength(flat[1], flat[0]);
            secondary = Head(flat[0], flat[0] - flat[1], secondaryHead, spec.Width, spec.Ratio, length);
        }

        double start = secondary is { } s
            ? Math.Min(PointLength(s.Tip, s.Neck), full * 0.8)
            : 0;
        double end = full - Math.Min(PointLength(primary.Value.Tip, primary.Value.Neck), full * 0.8);
        if (end - start < 1e-6) return new List<InkPart>();

        var clipped = Clip(flat, distances, start, end, secondary?.Neck, primary.Value.Neck);
        if (clipped == null) return new List<InkPart>();

        var (line, lineDistances, total) = clipped.Value;
        bool hollow = primary.Value.Head == Arrowhead.TriangleOutline;
        bool integrateSecondary = secondary is { } sc && !hollow && sc.Head != Arrowhead.TriangleOutline;

        var parts = new List<InkPart>();
        if (hollow || spec.StrokeStyle == StrokeKind.Solid)
        {
            var contour = Ribbon(line, lineDistances, total,
                secondary?.HalfWidth ?? 0, primary.Value.HalfWidth, 0, total);
            if (contour.Count < 2) return parts;

            int half = contour.Count / 2;
            var normal = Normal(primary.Value.Direction);
            contour[half - 1] = primary.Value.Neck + normal * primary.Value.HalfWidth;
            contour[half] = primary.Value.Neck - normal * primary.Value.HalfWidth;
            contour.InsertRange(half, new[] { primary.Value.A, primary.Value.Tip, primary.Value.B });
            if (integrateSecondary) contour.AddRange(new[] { secondary!.Value.A, secondary.Value.Tip, secondary.Value.B });
            parts.Add(Closed(contour));
        }
        else
        {
            double stroke = spec.Width;
            var (dash, gap) = spec.StrokeStyle == StrokeKind.Dashed
                ? (4.0 * stroke, 2.0 * stroke)
                : (stroke, 2.0 * stroke);

            for (double from = 0; from < total; from += dash + gap)
            {
                double to = Math.Min(from + dash, total);
                if (spec.StrokeStyle == StrokeKind.Dotted)
                {
                    double at = (from + to) / 2;
                    var center = AtDistance(line, lineDistances, at);
                    double radius = (secondary?.HalfWidth ?? 0) + (primary.Value.HalfWidth -
                        (secondary?.HalfWidth ?? 0)) * at / total;
                    parts.Add(Closed(Circle(center, radius)));
                }
                else
                {
                    var dashContour = Ribbon(line, lineDistances, total,
                        secondary?.HalfWidth ?? 0, primary.Value.HalfWidth, from, to);
                    if (dashContour.Count >= 2) parts.Add(Closed(dashContour));
                }
            }

            parts.Add(Closed(primary.Value.Contour()));
            if (integrateSecondary) parts.Add(Closed(secondary!.Value.Contour()));
            else if (secondary is { } head && !integrateSecondary)
                parts.Add(Closed(head.Contour()));
        }

        return parts;

        static bool Supported(Arrowhead head) => head == Arrowhead.None || SupportsTapered(head);
    }

    private static List<Point> Circle(Point center, double radius)
    {
        var list = new List<Point>(20);
        for (int i = 0; i < 20; i++)
        {
            // 顺时针，与杆 / 箭头同向（上游注释：重叠的点不会挖洞）
            double angle = -i * Math.Tau / 20;
            list.Add(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
        return list;
    }

    /// <summary>沿中线按弧长取样，宽度从 <paramref name="startWidth"/> 线性长到 <paramref name="endWidth"/>。</summary>
    private static List<Point> Ribbon(IReadOnlyList<Point> points, IReadOnlyList<double> distances,
        double total, double startWidth, double endWidth, double from, double to)
    {
        var samples = new List<(Point Point, double Distance)>
        {
            (AtDistance(points, distances, from), from),
        };
        for (int i = 0; i < points.Count; i++)
            if (distances[i] > from && distances[i] < to) samples.Add((points[i], distances[i]));
        samples.Add((AtDistance(points, distances, to), to));

        var left = new List<Point>();
        var right = new List<Point>();
        for (int i = 0; i < samples.Count; i++)
        {
            var (point, distance) = samples[i];
            Vector before = Unit(samples[i == 0 ? 0 : i - 1].Point, point, i == 0);
            Vector after = Unit(point, samples[Math.Min(i + 1, samples.Count - 1)].Point,
                i + 1 == samples.Count);

            Vector n = i == 0 ? Normal(after)
                : i + 1 == samples.Count ? Normal(before)
                : Bounded(Normal(before), Normal(after));

            double radius = startWidth + (endWidth - startWidth) * distance / total;
            double turn = before.X * after.Y - before.Y * after.X;

            if (i > 0 && i + 1 < samples.Count && Math.Abs(turn) > 1e-6)
            {
                var from_ = Normal(before);
                double angle = Math.Atan2(turn, before.X * after.X + before.Y * after.Y);
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(angle) / 0.15));
                for (int step = 0; step <= steps; step++)
                {
                    double t = angle * step / steps;
                    var arc = new Vector(
                        from_.X * Math.Cos(t) - from_.Y * Math.Sin(t),
                        from_.X * Math.Sin(t) + from_.Y * Math.Cos(t));
                    // 外侧走圆弧、内侧用有界交点（上游注释：避免锐角处的长尖刺）
                    left.Add(point + (turn < 0 ? arc : n) * radius);
                    right.Add(point - (turn > 0 ? arc : n) * radius);
                }
            }
            else
            {
                left.Add(point + n * radius);
                right.Add(point - n * radius);
            }
        }

        right.Reverse();
        left.AddRange(right);
        return left;

        static Vector Unit(Point a, Point b, bool degenerate)
        {
            var v = b - a;
            return degenerate || v.Length < 1e-12 ? new Vector(0, 0) : v / v.Length;
        }

        static Vector Bounded(Vector before, Vector after)
        {
            var bisector = before + after;
            if (bisector.Length < 1e-9) return after;
            bisector.Normalize();
            double dot = bisector.X * after.X + bisector.Y * after.Y;
            return bisector * (1.0 / Math.Max(dot, 0.5));
        }
    }

    private static (List<Point> Points, List<double> Distances, double Total)? Clip(
        IReadOnlyList<Point> points, IReadOnlyList<double> distances, double start, double end,
        Point? startNeck, Point endNeck)
    {
        double total = end - start;
        if (total < 1e-6) return null;

        var clipped = new List<Point> { AtDistance(points, distances, start) };
        var clippedDistances = new List<double> { 0 };
        for (int i = 0; i < points.Count; i++)
        {
            if (distances[i] <= start || distances[i] >= end) continue;
            clipped.Add(points[i]);
            clippedDistances.Add(distances[i] - start);
        }
        clipped.Add(AtDistance(points, distances, end));
        clippedDistances.Add(total);

        if (startNeck is { } neck) clipped[0] = neck;
        clipped[^1] = endNeck;
        return (clipped, clippedDistances, total);
    }

    private static Point AtDistance(IReadOnlyList<Point> points, IReadOnlyList<double> distances, double at)
    {
        if (points.Count == 0) return default;
        if (points.Count == 1) return points[0];

        int index = 1;
        while (index < distances.Count - 1 && distances[index] < at) index++;

        double span = distances[index] - distances[index - 1];
        double t = span < 1e-12 ? 0 : Math.Clamp((at - distances[index - 1]) / span, 0, 1);
        return points[index - 1] + (points[index] - points[index - 1]) * t;
    }

    private static InkPart Closed(IReadOnlyList<Point> points)
    {
        var segments = new List<InkSegment>(points.Count);
        for (int i = 0; i < points.Count; i++)
            segments.Add(InkSegment.Line(points[i], points[(i + 1) % points.Count]));
        return new InkPart(segments, true);
    }

    private static double PointLength(Point a, Point b) => (a - b).Length;

    private static Vector Normal(Vector direction) => new(-direction.Y, direction.X);

    private static Point Mix(Point a, Point b, double t) => a + (b - a) * t;
}
