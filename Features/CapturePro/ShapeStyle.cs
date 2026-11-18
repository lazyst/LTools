using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>形状种类（上游 <c>SnowCanvasRectangleShape</c>）。</summary>
internal enum ShapeKind
{
    Rectangle,
    Ellipse,
    Diamond,

    /// <summary>思考气泡的云朵轮廓，只在气泡工具里出现（上游同上）。</summary>
    ThoughtBubble,
}

/// <summary>描边线型（上游 <c>SnowCanvasStrokeStyle</c>）。</summary>
internal enum StrokeKind
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>笔触风格（上游 <c>SnowCanvasStrokeRenderingStyle</c>）：干净 / 手绘 / 涂鸦。</summary>
internal enum StrokeRendering
{
    Architect,
    Artist,
    Cartoonist,
}

/// <summary>填充方式（上游 <c>SnowCanvasFillStyle</c>；<see cref="None"/> 就是它的 <c>Border</c>）。</summary>
internal enum FillKind
{
    None,
    Solid,
    Line,
    CrossLine,
}

/// <summary>画笔模式（上游 <c>SnowCanvasBrushMode</c>）。</summary>
internal enum BrushMode
{
    Regular,
    PressureSensitive,
}

/// <summary>直线的直线 / 曲线（上游 <c>SnowCanvasArrowType</c> 的 Straight / Curve）。</summary>
internal enum LineType
{
    Straight,
    Curve,
}

/// <summary>
/// 形状的一整套画法（形状族与气泡族共用，对应上游 <c>SnowCanvasShapeStyle</c>）。
/// **不可变**：改参数 = 换一个 <see cref="ShapeSpec"/>。
/// </summary>
internal readonly record struct ShapeSpec(
    ShapeKind Kind,
    Color Stroke,
    double Width,
    StrokeKind StrokeStyle,
    StrokeRendering Rendering,
    Color Fill,
    FillKind FillStyle,
    double Radius,
    uint Seed);

/// <summary>轮廓的一段：直线（控制点在 1/3、2/3）或三次贝塞尔。</summary>
internal readonly record struct InkSegment(Point A, Point C1, Point C2, Point B, bool Straight)
{
    public static InkSegment Line(Point a, Point b)
    {
        var delta = b - a;
        return new InkSegment(a, a + delta * (1.0 / 3.0), a + delta * (2.0 / 3.0), b, true);
    }

    public static InkSegment Cubic(Point a, Point c1, Point c2, Point b) => new(a, c1, c2, b, false);
}

/// <summary>轮廓的一部分（闭合子路径）：气泡的身体、尾巴、思考气泡的圆点各算一份。</summary>
internal readonly record struct InkPart(IReadOnlyList<InkSegment> Segments, bool Closed);

/// <summary>
/// 形状几何与上色（上游 <c>snow_canvas_stroke_rendering.cpp</c> /
/// <c>snow_canvas_fill_render.cpp</c> 的移植）：轮廓 → 填充 + 描边。
/// </summary>
/// <remarks>
/// <para>
/// 三种笔触风格是同一套「手绘抖动」：<c>Architect</c> 不抖（振幅 0）、<c>Artist</c> 最多偏 2 DIP、
/// <c>Cartoonist</c> 最多偏 4 DIP；非 Architect 的**实线**描边画两遍（上游 <c>passes = 2</c>），
/// 第二遍反向补一笔，看着像铅笔重复描过。
/// </para>
/// <para>
/// 抖动量由「哈希采样」生成（上游 <c>sample()</c>），不是随机数，所以**同一个 seed 每次画出来完全一样**：
/// seed 存在标注里（<see cref="NextSeed"/> 分配），拖动 / 重绘不会让线条自己动。
/// </para>
/// </remarks>
internal static class ShapeInk
{
    /// <summary>四段贝塞尔画圆的控制点系数。</summary>
    private const double Kappa = 0.5522847498307936;

    /// <summary>气泡尾巴最多张开 60°（上游 <c>SPEECH_BUBBLE_MAX_HALF_ANGLE</c>）。</summary>
    private const double MaxHalfAngle = Math.PI / 3;

    private static int _seed;

    /// <summary>给一条新标注分配抖动种子（同一条标注重绘多次结果一致）。</summary>
    public static uint NextSeed() =>
        unchecked((uint)Interlocked.Increment(ref _seed) * 2654435761u) | 1u;

    /// <summary>手绘抖动最多能超出轮廓多少 DIP（上游 <c>stroke_rendering_outset</c>）。</summary>
    public static double Outset(StrokeRendering style) => style switch
    {
        StrokeRendering.Artist => 2.0,
        StrokeRendering.Cartoonist => 4.0,
        _ => 0.0,
    };

    // ============ 轮廓 ============

    /// <summary>把形状描述变成轮廓段（闭合，顺序即绘制顺序）。</summary>
    public static List<InkSegment> Contour(Rect rect, ShapeKind kind, double radius)
    {
        var list = new List<InkSegment>();
        if (rect.Width <= 0.01 || rect.Height <= 0.01) return list;

        // 上游：菱形的圆角上限是短边 / 4（不然四个尖角会被削平），其余是短边 / 2。
        double limit = Math.Min(rect.Width, rect.Height) / (kind == ShapeKind.Diamond ? 4 : 2);
        double r = Math.Clamp(radius, 0, Math.Max(0, limit));

        switch (kind)
        {
            case ShapeKind.Ellipse:
                AppendEllipse(list, rect);
                break;
            case ShapeKind.Diamond:
                AppendDiamond(list, rect, r);
                break;
            case ShapeKind.ThoughtBubble:
                AppendThoughtBubble(list, rect);
                break;
            default:
                AppendRoundedRect(list, rect, r);
                break;
        }
        return list;
    }

    private static void AppendRoundedRect(List<InkSegment> list, Rect rect, double radius)
    {
        double l = rect.Left, t = rect.Top, r = rect.Right, b = rect.Bottom;
        double k = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        if (k <= 0.01)
        {
            var p0 = new Point(l, t);
            var p1 = new Point(r, t);
            var p2 = new Point(r, b);
            var p3 = new Point(l, b);
            list.Add(InkSegment.Line(p0, p1));
            list.Add(InkSegment.Line(p1, p2));
            list.Add(InkSegment.Line(p2, p3));
            list.Add(InkSegment.Line(p3, p0));
            return;
        }

        double c = k * Kappa;
        // 顺时针：上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角
        list.Add(InkSegment.Line(new Point(l + k, t), new Point(r - k, t)));
        list.Add(InkSegment.Cubic(new Point(r - k, t), new Point(r - k + c, t), new Point(r, t + k - c), new Point(r, t + k)));
        list.Add(InkSegment.Line(new Point(r, t + k), new Point(r, b - k)));
        list.Add(InkSegment.Cubic(new Point(r, b - k), new Point(r, b - k + c), new Point(r - k + c, b), new Point(r - k, b)));
        list.Add(InkSegment.Line(new Point(r - k, b), new Point(l + k, b)));
        list.Add(InkSegment.Cubic(new Point(l + k, b), new Point(l + k - c, b), new Point(l, b - k + c), new Point(l, b - k)));
        list.Add(InkSegment.Line(new Point(l, b - k), new Point(l, t + k)));
        list.Add(InkSegment.Cubic(new Point(l, t + k), new Point(l, t + k - c), new Point(l + k - c, t), new Point(l + k, t)));
    }

    private static void AppendEllipse(List<InkSegment> list, Rect rect)
    {
        double cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        double rx = rect.Width / 2, ry = rect.Height / 2;
        double kx = rx * Kappa, ky = ry * Kappa;

        var top = new Point(cx, cy - ry);
        var right = new Point(cx + rx, cy);
        var bottom = new Point(cx, cy + ry);
        var left = new Point(cx - rx, cy);

        list.Add(InkSegment.Cubic(top, new Point(cx + kx, cy - ry), new Point(cx + rx, cy - ky), right));
        list.Add(InkSegment.Cubic(right, new Point(cx + rx, cy + ky), new Point(cx + kx, cy + ry), bottom));
        list.Add(InkSegment.Cubic(bottom, new Point(cx - kx, cy + ry), new Point(cx - rx, cy + ky), left));
        list.Add(InkSegment.Cubic(left, new Point(cx - rx, cy - ky), new Point(cx - kx, cy - ry), top));
    }

    /// <summary>四角圆角菱形（上游 <c>roundedDiamondPath</c> + <c>normalize_shape_corner_radii</c>）。</summary>
    private static void AppendDiamond(List<InkSegment> list, Rect rect, double radius)
    {
        double a = rect.Width / 2, b = rect.Height / 2;
        if (a <= 0 || b <= 0) return;

        var center = new Point(rect.X + a, rect.Y + b);
        Point[] vertices =
        {
            new(center.X, center.Y - b), new(center.X + a, center.Y),
            new(center.X, center.Y + b), new(center.X - a, center.Y),
        };

        if (radius <= 0.0001)
        {
            for (int i = 0; i < 4; i++)
                list.Add(InkSegment.Line(vertices[i], vertices[(i + 1) % 4]));
            return;
        }

        double edge = Math.Sqrt(a * a + b * b);
        double[] cotangents = { b / a, a / b, b / a, a / b };
        double scale = 1;
        for (int i = 0; i < 4; i++)
        {
            double sum = radius * cotangents[i] + radius * cotangents[(i + 1) % 4];
            if (sum > edge) scale = Math.Min(scale, edge / sum);
        }

        Vector[] inward = { new(0, 1), new(-1, 0), new(0, -1), new(1, 0) };

        Point? at = null;
        for (int i = 0; i < 4; i++)
        {
            double rad = radius * scale;
            var vertex = vertices[i];
            double trim = rad * cotangents[i] / edge;
            var entry = vertex + (vertices[(i + 3) % 4] - vertex) * trim;
            var exit = vertex + (vertices[(i + 1) % 4] - vertex) * trim;

            if (at is { } from) list.Add(InkSegment.Line(from, entry));

            double sine = (i % 2 == 0 ? a : b) / edge;
            var arcCenter = vertex + inward[i] * (rad / sine);
            var start = (entry - arcCenter) / rad;
            var end = (exit - arcCenter) / rad;
            double angle = Math.Atan2(start.Y, start.X);
            double sweep = Math.Max(0, Math.Atan2(
                start.X * end.Y - start.Y * end.X, start.X * end.X + start.Y * end.Y));
            int steps = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2) - 1e-12));
            double delta = sweep / steps;
            double k = 4.0 / 3.0 * Math.Tan(delta / 4.0);

            var cursor = entry;
            for (int step = 0; step < steps; step++)
            {
                double a0 = angle + step * delta;
                double a1 = angle + (step + 1) * delta;
                var u0 = new Vector(Math.Cos(a0), Math.Sin(a0));
                var u1 = new Vector(Math.Cos(a1), Math.Sin(a1));
                var c1 = arcCenter + new Vector((u0.X - k * u0.Y) * rad, (u0.Y + k * u0.X) * rad);
                var c2 = arcCenter + new Vector((u1.X + k * u1.Y) * rad, (u1.Y - k * u1.X) * rad);
                var to = step + 1 == steps ? exit : arcCenter + u1 * rad;
                list.Add(InkSegment.Cubic(cursor, c1, c2, to));
                cursor = to;
            }
            at = exit;
        }

        if (at is { } last) list.Add(InkSegment.Line(last, list[0].A));
    }

    /// <summary>思考气泡的云朵轮廓（上游 <c>thought_bubble::body_segments</c> 的 100×100 手绘云）。</summary>
    private static void AppendThoughtBubble(List<InkSegment> list, Rect rect)
    {
        Point Map(double x, double y) =>
            new(rect.X + rect.Width * (x / 100.0), rect.Y + rect.Height * (y / 100.0));

        double[,] points =
        {
            { 55, 0, 61, 4, 64, 11.5 },
            { 72, 5, 84, 10, 85, 24.5 },
            { 94, 22, 100, 30, 98, 43.5 },
            { 99.4, 46, 100, 49, 100, 52 },
            { 100, 61, 95, 68, 89, 69 },
            { 90, 80, 82, 90, 73, 87 },
            { 68, 98, 57, 100, 49, 92 },
            { 45, 97, 41, 100, 36, 100 },
            { 30, 100, 24, 96, 22, 89 },
            { 11, 93, 3, 81, 7, 69 },
            { 2, 65, 0, 60, 0, 54 },
            { 0, 45, 4, 38, 10, 36 },
            { 6, 24, 15, 12, 27, 16 },
            { 30, 5, 38, 0, 47, 0 },
        };

        var start = Map(47, 0);
        for (int i = 0; i < points.GetLength(0); i++)
        {
            var end = Map(points[i, 4], points[i, 5]);
            list.Add(InkSegment.Cubic(start, Map(points[i, 0], points[i, 1]),
                Map(points[i, 2], points[i, 3]), end));
            start = end;
        }
    }

    /// <summary>圆点轮廓（思考气泡的尾巴）。</summary>
    public static List<InkSegment> Circle(Point center, double radius)
    {
        double k = radius * Kappa;
        var top = new Point(center.X, center.Y - radius);
        var right = new Point(center.X + radius, center.Y);
        var bottom = new Point(center.X, center.Y + radius);
        var left = new Point(center.X - radius, center.Y);
        return new List<InkSegment>
        {
            InkSegment.Cubic(top, new Point(center.X + k, center.Y - radius),
                new Point(center.X + radius, center.Y - k), right),
            InkSegment.Cubic(right, new Point(center.X + radius, center.Y + k),
                new Point(center.X + k, center.Y + radius), bottom),
            InkSegment.Cubic(bottom, new Point(center.X - k, center.Y + radius),
                new Point(center.X - radius, center.Y + k), left),
            InkSegment.Cubic(left, new Point(center.X - radius, center.Y - k),
                new Point(center.X - k, center.Y - radius), top),
        };
    }

    // ============ 气泡尾巴 ============

    /// <summary>
    /// 尾巴根部：身体轮廓上、以「指向尾巴尖」为轴左右张开 <c>baseWidth</c> 宽的两点
    /// （上游 <c>tail_bases</c> / <c>effective_half_angle</c> 的移植）。
    /// 尾巴尖落在身体里时返回 <c>null</c>（那就没有尾巴）。
    /// </summary>
    public static (Point Left, Point Right)? TailBases(
        IReadOnlyList<InkSegment> body, Rect rect, Point tip, double baseWidth)
    {
        if (body.Count == 0) return null;

        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var delta = tip - center;
        double distance = delta.Length;
        if (distance <= 1e-6) return null;

        double axis = Math.Atan2(delta.Y, delta.X);
        double boundary = RayRadius(body, center, axis);
        if (distance <= boundary) return null;

        double low = 0, high = MaxHalfAngle;
        for (int i = 0; i < 36; i++)
        {
            double mid = (low + high) / 2;
            if (Chord(body, center, axis, mid) < baseWidth) low = mid;
            else high = mid;
        }

        double spread = (low + high) / 2;
        if (spread <= 1e-4) return null;

        return (RayPoint(body, center, axis - spread), RayPoint(body, center, axis + spread));
    }

    private static double Chord(IReadOnlyList<InkSegment> body, Point center, double axis, double spread) =>
        (RayPoint(body, center, axis - spread) - RayPoint(body, center, axis + spread)).Length;

    /// <summary>从形状中心沿某个角度打到轮廓上的点。</summary>
    public static Point RayPoint(IReadOnlyList<InkSegment> body, Point center, double angle) =>
        center + new Vector(Math.Cos(angle), Math.Sin(angle)) * RayRadius(body, center, angle);

    /// <summary>从形状中心沿某个角度打到轮廓的距离（形状对外是星形，取最近的那个交点）。</summary>
    public static double RayRadius(IReadOnlyList<InkSegment> body, Point center, double angle)
    {
        var poly = Flatten(body, 24);
        if (poly.Count < 2) return 0;

        var dir = new Vector(Math.Cos(angle), Math.Sin(angle));
        double best = double.PositiveInfinity;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            var edge = b - a;
            double denom = Vector.CrossProduct(dir, edge);
            if (Math.Abs(denom) < 1e-12) continue;

            var diff = a - center;
            double t = Vector.CrossProduct(diff, edge) / denom;
            double u = Vector.CrossProduct(diff, dir) / denom;
            if (t > 0 && u >= 0 && u <= 1) best = Math.Min(best, t);
        }

        return double.IsInfinity(best) ? 0 : best;
    }

    /// <summary>
    /// 把身体轮廓在尾巴根部切开，去掉被尾巴盖住的那一段，接上「根部 → 尾巴尖 → 根部」两条腿。
    /// 上游 <c>speech_bubble</c> 的三角形尾巴就是这条轮廓（所以身体边线不会横穿尾巴）。
    /// </summary>
    public static List<InkSegment> WithTail(
        IReadOnlyList<InkSegment> body, Point first, Point second, Point tip)
    {
        var segments = new List<InkSegment>(body);
        if (segments.Count == 0) return segments;

        int s1 = SplitAt(segments, first);
        int s2 = SplitAt(segments, second);
        if (s1 < 0 || s2 < 0 || s1 == s2) return segments;

        var arc12 = Walk(segments, s1, s2);
        var arc21 = Walk(segments, s2, s1);
        bool forward = Length(arc12) >= Length(arc21);
        var keep = forward ? arc12 : arc21;

        var outline = new List<InkSegment>(keep.Count + 2);
        outline.Add(InkSegment.Line(tip, forward ? first : second));
        outline.AddRange(keep);
        outline.Add(InkSegment.Line(forward ? second : first, tip));
        return outline;
    }

    /// <summary>沿闭合轮廓从 <paramref name="from"/> 的**下一个**走到 <paramref name="to"/>（含）。</summary>
    private static List<InkSegment> Walk(List<InkSegment> list, int from, int to)
    {
        var result = new List<InkSegment>();
        int index = from;
        for (int guard = 0; guard <= list.Count; guard++)
        {
            index = (index + 1) % list.Count;
            result.Add(list[index]);
            if (index == to) break;
        }
        return result;
    }

    private static double Length(List<InkSegment> segments)
    {
        double sum = 0;
        foreach (var s in segments)
            sum += s.Straight ? (s.B - s.A).Length
                : (s.C1 - s.A).Length + (s.C2 - s.C1).Length + (s.B - s.C2).Length;
        return sum;
    }

    /// <summary>
    /// 在轮廓上离 <paramref name="point"/> 最近的位置切开，返回「以该点为终点」的那一段下标。
    /// 点正好落在已有顶点上时不切。
    /// </summary>
    private static int SplitAt(List<InkSegment> list, Point point)
    {
        int bestIndex = -1;
        double bestDistance = double.PositiveInfinity, bestT = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var seg = list[i];
            for (int step = 0; step <= 48; step++)
            {
                double t = step / 48.0;
                var at = seg.Straight ? seg.A + (seg.B - seg.A) * t : Bezier(seg.A, seg.C1, seg.C2, seg.B, t);
                double distance = (at - point).Length;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                bestIndex = i;
                bestT = t;
            }
        }

        if (bestIndex < 0) return -1;
        if (bestT <= 1e-6) return (bestIndex - 1 + list.Count) % list.Count;
        if (bestT >= 1 - 1e-6) return bestIndex;

        var (left, right) = Split(list[bestIndex], bestT);
        list[bestIndex] = left;
        list.Insert(bestIndex + 1, right);
        return bestIndex + 1;
    }

    /// <summary>
    /// 思考气泡的尾巴：身体外面一串由大到小的圆（上游 <c>thought_bubble::circles</c> 的移植）。
    /// 挨着轮廓 / 落进身体的圆直接丢掉（与上游 <c>clear_circle</c> 同口径）。
    /// </summary>
    public static List<(Point Center, double Radius)> ThoughtTrail(
        IReadOnlyList<InkSegment> body, Rect rect, Point tip, double baseWidth, double stroke)
    {
        var result = new List<(Point Center, double Radius)>();
        if (body.Count == 0 || baseWidth <= 0.01) return result;

        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        var delta = tip - center;
        double distance = delta.Length;
        if (distance <= 1e-6) return result;

        var axisVector = delta / distance;
        double boundary = RayRadius(body, center, Math.Atan2(axisVector.Y, axisVector.X));
        double available = Math.Max(0, distance - boundary);

        double strokeWidth = Math.Max(stroke, 0.01);
        double gap = Math.Max(Math.Min(baseWidth * 0.22, available * 0.25), strokeWidth + 1.0);
        if (available <= gap + strokeWidth * 0.5) return result;

        double requested = baseWidth * 0.5;
        int count = (int)Math.Clamp(Math.Ceiling(available / (baseWidth + gap)), 1, 3);

        double[] weights;
        double radius;
        double weightSum;
        while (true)
        {
            weights = new double[count];
            for (int i = 0; i < count; i++) weights[i] = Math.Pow(0.68, i);
            weightSum = 2 * weights.Sum() - weights[^1];
            radius = Math.Min(requested, Math.Max(0, available - gap * count) / weightSum);
            if (count == 1 || radius * weights[^1] > 0.75) break;
            count--;
        }

        double extra = Math.Max(0, available - radius * weightSum - gap * count);
        double spacing = gap + extra / count;
        double cursor = boundary + spacing;

        for (int i = 0; i < count; i++)
        {
            double r = radius * weights[i];
            var at = i + 1 == count ? tip : center + axisVector * (cursor + r);
            if (r > 0.25 && !InsideBody(body, center, at) && !NearOutline(body, at, r + strokeWidth + 0.5))
                result.Add((at, r));
            cursor += 2 * r + spacing;
        }

        return result;
    }

    private static bool InsideBody(IReadOnlyList<InkSegment> body, Point center, Point point)
    {
        var delta = point - center;
        double distance = delta.Length;
        if (distance <= 1e-9) return true;
        double radius = RayRadius(body, center, Math.Atan2(delta.Y, delta.X));
        return distance < radius - 1e-9 * Math.Max(radius, 1);
    }

    private static bool NearOutline(IReadOnlyList<InkSegment> body, Point point, double distance)
    {
        var poly = Flatten(body, 12);
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            if (MarkGeometry.NearSegment(point, a, b, distance)) return true;
        }
        return false;
    }

    // ============ 画 ============

    /// <summary>一个闭合轮廓的上色：先填充（实心 / 斜线 / 交叉）再描边（实 / 虚 / 点 + 笔触风格）。</summary>
    public static void Draw(DrawingContext dc, IReadOnlyList<InkSegment> contour, bool closed, ShapeSpec spec) =>
        Draw(dc, new[] { new InkPart(contour, closed) }, spec);

    /// <summary>多块轮廓（气泡的身体 + 尾巴 + 圆点）按同一套样式一次画完。</summary>
    public static void Draw(DrawingContext dc, IReadOnlyList<InkPart> parts, ShapeSpec spec)
    {
        if (parts.Count == 0 || parts.All(p => p.Segments.Count == 0)) return;

        if (spec.FillStyle != FillKind.None && spec.Fill.A > 0)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var part in parts) group.Children.Add(BuildPath(part.Segments, part.Closed, filled: true));
            group.Freeze();
            PaintFill(dc, group, spec);
        }

        DrawStroke(dc, parts, spec);
    }

    /// <summary>只要描边（直线 / 折线这类不闭合的笔迹用）。</summary>
    public static void DrawStroke(DrawingContext dc, IReadOnlyList<InkSegment> contour, bool closed, ShapeSpec spec) =>
        DrawStroke(dc, new[] { new InkPart(contour, closed) }, spec);

    private static void DrawStroke(DrawingContext dc, IReadOnlyList<InkPart> parts, ShapeSpec spec)
    {
        if (spec.Width <= 0.01 || spec.Stroke.A == 0) return;

        var brush = new SolidColorBrush(spec.Stroke);
        brush.Freeze();
        var pen = new Pen(brush, spec.Width)
        {
            StartLineCap = spec.StrokeStyle == StrokeKind.Solid ? PenLineCap.Flat : PenLineCap.Round,
            EndLineCap = spec.StrokeStyle == StrokeKind.Solid ? PenLineCap.Flat : PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        ApplyDash(pen, spec);
        pen.Freeze();

        // 上游：只有「实线 + 非 Architect」才画两遍；虚线 / 点线只画一遍
        int passes = spec.StrokeStyle == StrokeKind.Solid && spec.Rendering != StrokeRendering.Architect ? 2 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            foreach (var part in parts)
            {
                if (part.Segments.Count == 0) continue;
                var path = spec.Rendering == StrokeRendering.Architect
                    ? BuildPath(part.Segments, part.Closed)
                    : VariedPath(part.Segments, part.Closed, spec, pass);
                dc.DrawGeometry(null, pen, path);
            }
        }
    }

    /// <summary>虚 / 点线：与上游 <c>configureShapeStroke</c> 一样按**线宽的倍数**给（4/4 与 0.01/3）。</summary>
    public static double[] DashPattern(StrokeKind style) => style switch
    {
        StrokeKind.Dashed => new[] { 4.0, 4.0 },
        StrokeKind.Dotted => new[] { 0.01, 3.0 },
        _ => Array.Empty<double>(),
    };

    private static void ApplyDash(Pen pen, ShapeSpec spec)
    {
        var pattern = DashPattern(spec.StrokeStyle);
        if (pattern.Length > 0) pen.DashStyle = new DashStyle(pattern, 0);
    }

    /// <summary>轮廓 → 可画 / 可测的 <see cref="PathGeometry"/>（不抖动的原样）。</summary>
    public static PathGeometry BuildPath(IReadOnlyList<InkSegment> contour, bool closed, bool filled = false)
    {
        var geo = new PathGeometry();
        if (contour.Count == 0) return geo;

        var figure = new PathFigure { StartPoint = contour[0].A, IsClosed = closed, IsFilled = filled };
        foreach (var seg in contour)
        {
            if (seg.Straight) figure.Segments.Add(new LineSegment(seg.B, true));
            else figure.Segments.Add(new BezierSegment(seg.C1, seg.C2, seg.B, true));
        }

        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    /// <summary>轮廓打成折线（命中测试用；椭圆 / 菱形按曲线采样，别按外接矩形判）。</summary>
    public static List<Point> Flatten(IReadOnlyList<InkSegment> contour, int steps = 10)
    {
        var list = new List<Point>();
        foreach (var seg in contour)
        {
            list.Add(seg.A);
            if (seg.Straight) continue;
            for (int i = 1; i < steps; i++)
                list.Add(Bezier(seg.A, seg.C1, seg.C2, seg.B, (double)i / steps));
        }
        if (contour.Count > 0) list.Add(contour[^1].B);
        return list;
    }

    /// <summary>
    /// 命中：填了色时整块算命中（上游 <c>fill_style.is_visible</c> + 内部判定），
    /// 描边按「中线 ± 半个线宽 + 宽容」算（虚线也算连续的一条）。
    /// </summary>
    public static bool HitTest(
        IReadOnlyList<InkPart> parts, ShapeSpec spec, Point point, double tolerance, bool interiorAlways = false)
    {
        if (parts.Count == 0) return false;

        if (interiorAlways || spec.FillStyle != FillKind.None)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var part in parts.Where(p => p.Closed))
                group.Children.Add(BuildPath(part.Segments, true, filled: true));
            if (group.Children.Count > 0 && group.FillContains(point)) return true;
        }

        double band = spec.Width / 2 + Math.Max(tolerance, 0);
        foreach (var part in parts)
        {
            var poly = Flatten(part.Segments, 16);
            if (poly.Count >= 2 && MarkGeometry.NearPolyline(poly, point, band)) return true;
        }
        return false;
    }

    private static void PaintFill(DrawingContext dc, Geometry geometry, ShapeSpec spec) =>
        PaintFillGeometry(dc, geometry, spec.Fill, spec.FillStyle, spec.Width);

    /// <summary>
    /// 把一块任意几何按填充方式上色（形状填充与文字背景胶囊共用这一份：
    /// 斜线 / 交叉的线宽与间距在上游是同一套 <c>drawStyledFill</c>）。
    /// </summary>
    /// <param name="referenceStripeWidth">
    /// 算斜线粗细的参考宽度：形状用描边线宽，文字背景用「字号 / 24」
    /// （上游 <c>drawTextBackgroundFill</c> 的 <c>kTextFillReferenceFontSize</c>）。
    /// </param>
    public static void PaintFillGeometry(DrawingContext dc, Geometry geometry, Color color,
        FillKind style, double referenceStripeWidth)
    {
        if (style == FillKind.None || color.A == 0) return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        if (style == FillKind.Solid)
        {
            dc.DrawGeometry(brush, null, geometry);
            return;
        }

        var bounds = geometry.Bounds;
        if (bounds.Width <= 0.01 || bounds.Height <= 0.01) return;

        // 上游：线宽 = clamp(1 + (参考宽 - 1) × 0.6, 0.5, 6)，间距 = clamp(线宽 × 4, 3, 21)
        double lineWidth = Math.Clamp(1 + (referenceStripeWidth - 1) * 0.6, 0.5, 6);
        double spacing = Math.Clamp(lineWidth * 4, 3, 21);

        var pen = new Pen(brush, lineWidth) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();

        dc.PushClip(geometry);
        DrawHatch(dc, pen, bounds, spacing, mirror: false);
        if (style == FillKind.CrossLine) DrawHatch(dc, pen, bounds, spacing, mirror: true);
        dc.Pop();
    }

    private static void DrawHatch(DrawingContext dc, Pen pen, Rect bounds, double spacing, bool mirror)
    {
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        double radius = Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height) / 2 + spacing;

        // 屏幕坐标 y 向下：mirror=false 是「/」，true 是「\」
        var dir = mirror ? new Vector(1, 1) : new Vector(1, -1);
        dir.Normalize();
        var normal = new Vector(-dir.Y, dir.X);

        for (double t = -radius; t <= radius; t += spacing)
        {
            var at = center + normal * t;
            dc.DrawLine(pen, at - dir * radius, at + dir * radius);
        }
    }

    // ============ 手绘抖动（上游 variedPath 的移植） ============

    private static double Sample(uint seed, uint index)
    {
        unchecked
        {
            uint value = seed + 0x9e3779b9U * (index + 1U);
            value = (value ^ (value >> 16)) * 0x85ebca6bU;
            value = (value ^ (value >> 13)) * 0xc2b2ae35U;
            value ^= value >> 16;
            return value / 2147483647.5 - 1.0;
        }
    }

    private static double Excursion(StrokeRendering style, double scale)
    {
        if (double.IsNaN(scale) || scale <= 0) return 0;
        return Math.Min(Outset(style), scale * 0.1);
    }

    private static Vector Normal(Vector delta)
    {
        double length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        return length > 1e-9 ? new Vector(-delta.Y / length, delta.X / length) : new Vector(0, 1);
    }

    private static PathGeometry VariedPath(
        IReadOnlyList<InkSegment> contour, bool closed, ShapeSpec spec, int pass)
    {
        double passGain = pass == 0 ? 0.98 : 0.5;
        uint seed = spec.Seed ^ (pass == 0 ? 0x68bc21ebU : 0x02e5be93U);

        var geo = new PathGeometry();
        var originalStart = contour[0].A;
        var originalAt = originalStart;
        int segment = 0;

        Vector Displace(Point point, Vector delta, double scale, uint index) =>
            Normal(delta) * (Excursion(spec.Rendering, scale) * 0.22 * Sample(seed, index));

        var firstDelta = contour[0].B - contour[0].A;
        var start = originalStart + Displace(originalStart, firstDelta, firstDelta.Length, 0);

        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };

        for (int i = 0; i < contour.Count; i++)
        {
            var seg = contour[i];
            var end = seg.B;
            var c1 = seg.C1;
            var c2 = seg.C2;

            var delta = end - originalAt;
            double span = seg.Straight
                ? delta.Length
                : Math.Max(delta.Length, Math.Max((originalAt - c1).Length, (originalAt - c2).Length));
            if (span <= 1e-9)
            {
                originalAt = end;
                continue;
            }

            var n = Normal(delta);
            double amplitude = Excursion(spec.Rendering, span) * passGain;
            double bendSample = Sample(spec.Seed ^ 0x632be59bU, (uint)(segment * 8 + 5));
            double bend = Math.CopySign(0.88 + 0.04 * Math.Abs(bendSample), bendSample)
                          * (pass == 0 ? 1 : -1) * (seg.Straight ? 1.0 : 0.65);

            Vector Jitter(int index) => n * (amplitude * (bend + 0.06 * Sample(seed, (uint)index)));

            if (seg.Straight)
            {
                c1 = originalAt + delta * (0.28 + 0.04 * Sample(seed, (uint)(segment * 8 + 1)));
                c2 = originalAt + delta * (0.71 + 0.04 * Sample(seed, (uint)(segment * 8 + 2)));
            }

            bool closes = (end - originalStart).Length < 1e-8;
            var variedEnd = closes
                ? start
                : end + Displace(end, delta, span, (uint)((segment + 1) * 8));

            figure.Segments.Add(new BezierSegment(
                c1 + Jitter(segment * 8 + 3), c2 + Jitter(segment * 8 + 4), variedEnd, true));

            originalAt = end;
            if (closes) figure.IsClosed = true;
            segment++;
        }

        if (closed) figure.IsClosed = true;
        geo.Figures.Add(figure);
        geo.Freeze();
        return geo;
    }

    private static Point Bezier(Point a, Point c1, Point c2, Point b, double t)
    {
        double u = 1 - t;
        double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
        return new Point(a.X * w0 + c1.X * w1 + c2.X * w2 + b.X * w3,
            a.Y * w0 + c1.Y * w1 + c2.Y * w2 + b.Y * w3);
    }

    /// <summary>把一段三次贝塞尔按 <paramref name="t"/> 劈成两段（de Casteljau）。</summary>
    private static (InkSegment A, InkSegment B) Split(InkSegment c, double t)
    {
        var a = c.A + (c.C1 - c.A) * t;
        var b = c.C1 + (c.C2 - c.C1) * t;
        var d = c.C2 + (c.B - c.C2) * t;
        var e = a + (b - a) * t;
        var f = b + (d - b) * t;
        var m = e + (f - e) * t;
        return (InkSegment.Cubic(c.A, a, e, m), InkSegment.Cubic(m, f, d, c.B));
    }
}
