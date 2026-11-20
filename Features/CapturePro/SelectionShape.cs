using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 选区的形状（照上游 <c>ScreenshotRegionType</c>）：矩形（拖框）/ 折线（逐点单击）/
/// 曲线（逐点单击、平滑成曲线）/ 自由绘制（按住随手画）。
/// </summary>
/// <remarks>
/// 枚举顺序与上游一致（矩形 → 折线 → 曲线 → 自由绘制），快捷键轮换就是「下一档」；
/// 落盘用的是 <see cref="SelectionRegionTypes.Id"/> 的字符串，别按枚举数值存。
/// </remarks>
internal enum SelectionMode
{
    Rect,
    Polyline,
    Curve,
    Lasso,
}

/// <summary>形状 id（落盘 / 设置页 / 上游对齐用，照 <c>screenshotRegionTypeId</c>）。</summary>
internal static class SelectionRegionTypes
{
    public const string Rectangle = "rectangle";
    public const string Polyline = "polyline";
    public const string Curve = "curve";
    public const string Freehand = "freehand";

    /// <summary>形状 → id（默认矩形）。</summary>
    public static string Id(SelectionMode mode) => mode switch
    {
        SelectionMode.Polyline => Polyline,
        SelectionMode.Curve => Curve,
        SelectionMode.Lasso => Freehand,
        _ => Rectangle,
    };

    /// <summary>id → 形状；认不出的（老配置 / 手改坏）回矩形。</summary>
    public static SelectionMode FromId(string? id) => (id ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Polyline => SelectionMode.Polyline,
        Curve => SelectionMode.Curve,
        Freehand => SelectionMode.Lasso,
        _ => SelectionMode.Rect,
    };

    /// <summary>界面上的中文名（形状条 / 提示 / 设置页共用一份）。</summary>
    public static string Label(SelectionMode mode) => mode switch
    {
        SelectionMode.Polyline => "折线",
        SelectionMode.Curve => "曲线",
        SelectionMode.Lasso => "自由绘制",
        _ => "矩形",
    };

    /// <summary>是不是「自定义区域」（矩形以外都算，照上游 <c>custom()</c>）。</summary>
    public static bool IsCustom(SelectionMode mode) => mode != SelectionMode.Rect;
}

/// <summary>
/// 非矩形选区的几何：点列的外接框、补点阈值、移动 / 缩放、DIP ↔ 图像像素、闭合成几何。
/// **纯函数、不碰窗口**，所以 <c>--selftest</c> 直接断言（见 capturePro 组）。
/// </summary>
/// <remarks>
/// 点列一律存**窗口 DIP**（与选区框、把手同一套坐标），只在导出前换成「图像像素」写进
/// <see cref="SelectionRegion"/> 里的点列。矩形选区不用这里的东西——它仍然是 <see cref="Rect"/>。
/// </remarks>
internal static class SelectionShape
{
    /// <summary>套索补点阈值（DIP）：比这更近的移动丢点，几千个点会让命中 / 几何慢下来。</summary>
    public const double MinStep = 1.5;

    /// <summary>套索 / 折线至少要有几个点才算一个面。</summary>
    public const int MinPoints = 3;

    /// <summary>套索补点：离上一个点太近就不收（鼠标不用动也有事件）。</summary>
    public static void Append(List<Point> points, Point p)
    {
        if (points.Count > 0 && (p - points[^1]).Length < MinStep) return;
        points.Add(p);
    }

    /// <summary>点列的外接框（空点列 = <see cref="Rect.Empty"/>）。</summary>
    public static Rect Bounds(IReadOnlyList<Point> points)
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

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>够不够成一个选区：至少三个点，外接框两个方向都到了最小边长。</summary>
    public static bool IsUsable(IReadOnlyList<Point> points, double minSize)
    {
        if (points.Count < MinPoints) return false;
        Rect box = Bounds(points);
        return box.Width >= minSize && box.Height >= minSize;
    }

    /// <summary>整体平移。</summary>
    public static Point[] Offset(IReadOnlyList<Point> points, Vector delta)
    {
        var result = new Point[points.Count];
        for (int i = 0; i < points.Count; i++) result[i] = points[i] + delta;
        return result;
    }

    /// <summary>按「外接框从 <paramref name="from"/> 变到 <paramref name="to"/>」等比 / 变形（与标注缩放同一口径）。</summary>
    public static Point[] Scale(IReadOnlyList<Point> points, Rect from, Rect to)
    {
        var result = new Point[points.Count];
        if (from.Width <= 0 || from.Height <= 0) return Offset(points, new Vector(to.X - from.X, to.Y - from.Y));

        double sx = to.Width / from.Width;
        double sy = to.Height / from.Height;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            result[i] = new Point(to.X + (p.X - from.X) * sx, to.Y + (p.Y - from.Y) * sy);
        }

        return result;
    }

    /// <summary>窗口 DIP → 图像像素（相对底图左上角；四舍五入，不夹 —— 夹由调用方按底图尺寸做）。</summary>
    public static Point[] ToImage(IReadOnlyList<Point> dip, Point origin, double scale)
    {
        double s = scale > 0 ? scale : 1;
        var result = new Point[dip.Count];
        for (int i = 0; i < dip.Count; i++)
            result[i] = new Point(Math.Round((dip[i].X - origin.X) * s),
                Math.Round((dip[i].Y - origin.Y) * s));
        return result;
    }

    /// <summary>点列 → 几何（<paramref name="closed"/> 时首尾相连并填满，导出 / 压暗挖洞都靠它）。</summary>
    /// <param name="smooth">
    /// 真 = 平滑成曲线（照上游 <c>snowCanvasCatmullRomPath</c> 的**向心 Catmull-Rom**，
    /// 每段一条三次贝塞尔）：曲线 / 自由绘制用它；折线仍是一段一段的直线。
    /// </param>
    public static Geometry Geometry(IReadOnlyList<Point> points, bool closed, bool smooth = false)
    {
        if (!smooth || points.Count < 3) return Polyline(points, closed);

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], closed, closed);
            int edges = closed ? points.Count : points.Count - 1;
            for (int edge = 0; edge < edges; edge++)
            {
                Point end = points[(edge + 1) % points.Count];
                if (TryControls(points, edge, closed, out Point control1, out Point control2))
                    ctx.BezierTo(control1, control2, end, true, false);
                else
                    ctx.LineTo(end, true, false);      // 退化（重合点 / 零长边）就退回直线
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Geometry Polyline(IReadOnlyList<Point> points, bool closed)
    {
        var geometry = new StreamGeometry();
        if (points.Count > 0)
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(points[0], closed, closed);
                for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
            }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>自由绘制补点的化简容差（DIP；上游按 0.125 物理像素做 RDP）。</summary>
    public const double SimplifyTolerance = 0.125;

    /// <summary>
    /// 点列化简（Ramer–Douglas–Peucker，迭代版，照上游 <c>simplifyScreenshotRegionPoints</c>）：
    /// 自由绘制一笔能收几千个点，不化简会让几何 / 命中慢下来。首尾点一定保留。
    /// </summary>
    public static Point[] Simplify(IReadOnlyList<Point> points, double tolerance)
    {
        if (points.Count < 3 || tolerance <= 0) return Copy(points);

        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;

        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, points.Count - 1));
        double threshold = tolerance * tolerance;

        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            Point start = points[first];
            Point end = points[last];
            double dx = end.X - start.X;
            double dy = end.Y - start.Y;
            double lengthSquared = dx * dx + dy * dy;

            double largest = threshold;
            int split = -1;
            for (int i = first + 1; i < last; i++)
            {
                double amount = lengthSquared > 0
                    ? Math.Clamp(((points[i].X - start.X) * dx + (points[i].Y - start.Y) * dy)
                        / lengthSquared, 0, 1)
                    : 0;
                double ex = points[i].X - start.X - dx * amount;
                double ey = points[i].Y - start.Y - dy * amount;
                double distance = ex * ex + ey * ey;
                if (distance > largest)
                {
                    largest = distance;
                    split = i;
                }
            }

            if (split < 0) continue;
            keep[split] = true;
            pending.Push((first, split));
            pending.Push((split, last));
        }

        var result = new List<Point>(points.Count);
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
        return result.ToArray();
    }

    /// <summary>
    /// 平滑曲线（闭合）的**精确**外接框：按每段三次贝塞尔采样求 min/max。
    /// 别用 <see cref="Geometry.Bounds"/>——WPF 对带曲线的几何返回的是**控制点包络**，
    /// 比真实曲线大一圈（实测 500 宽的方框能报到 595），导出会平白多出一大块透明边。
    /// </summary>
    public static Rect SmoothBounds(IReadOnlyList<Point> points)
    {
        if (points.Count < 3) return Bounds(points);

        double left = double.MaxValue, right = double.MinValue;
        double top = double.MaxValue, bottom = double.MinValue;

        void Take(Point p)
        {
            if (p.X < left) left = p.X;
            if (p.X > right) right = p.X;
            if (p.Y < top) top = p.Y;
            if (p.Y > bottom) bottom = p.Y;
        }

        for (int i = 0; i < points.Count; i++) Take(points[i]);

        const int Samples = 24;
        for (int edge = 0; edge < points.Count; edge++)
        {
            Point start = points[edge];
            Point end = points[(edge + 1) % points.Count];
            if (!TryControls(points, edge, closed: true, out Point c1, out Point c2))
            {
                Take(end);
                continue;
            }

            for (int step = 1; step <= Samples; step++)
            {
                double t = (double)step / Samples;
                double u = 1 - t;
                double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
                Take(new Point(
                    a * start.X + b * c1.X + c * c2.X + d * end.X,
                    a * start.Y + b * c1.Y + c * c2.Y + d * end.Y));
            }
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>一段的三次贝塞尔控制点（向心 Catmull-Rom）；退化时返回 false，调用方退回直线。</summary>
    private static bool TryControls(IReadOnlyList<Point> vertices, int edge, bool closed,
        out Point control1, out Point control2)
    {
        control1 = control2 = default;

        Point start = vertices[edge];
        Point end = vertices[(edge + 1) % vertices.Count];

        Point previous = edge > 0
            ? vertices[edge - 1]
            : closed ? vertices[^1] : Reflect(vertices[0], vertices.Count > 1 ? vertices[1] : vertices[0]);

        Point next = edge + 2 < vertices.Count
            ? vertices[edge + 2]
            : closed ? vertices[(edge + 2) % vertices.Count] : Reflect(end, start);

        double t0 = 0;
        double t1 = t0 + Chord(previous, start);
        double t2 = t1 + Chord(start, end);
        double t3 = t2 + Chord(end, next);
        if (t1 <= t0 || t2 <= t1 || t3 <= t2) return false;

        double segment = t2 - t1;
        if (!TryTangent(previous, start, end, t0, t1, t2, out Vector tangent1)) return false;
        if (!TryTangent(start, end, next, t1, t2, t3, out Vector tangent2)) return false;

        control1 = new Point(start.X + tangent1.X * segment / 3, start.Y + tangent1.Y * segment / 3);
        control2 = new Point(end.X - tangent2.X * segment / 3, end.Y - tangent2.Y * segment / 3);
        return true;
    }

    private static bool TryTangent(Point p0, Point p1, Point p2, double t0, double t1, double t2,
        out Vector tangent)
    {
        tangent = default;
        double d10 = t1 - t0;
        double d21 = t2 - t1;
        double d20 = t2 - t0;
        if (d10 <= 0 || d21 <= 0 || d20 <= 0) return false;

        tangent = new Vector(
            (p1.X - p0.X) / d10 - (p2.X - p0.X) / d20 + (p2.X - p1.X) / d21,
            (p1.Y - p0.Y) / d10 - (p2.Y - p0.Y) / d20 + (p2.Y - p1.Y) / d21);
        return true;
    }

    /// <summary>向心参数（弦长的平方根），照上游 <c>chord_parameter</c>。</summary>
    private static double Chord(Point a, Point b) =>
        Math.Sqrt(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));

    private static Point Reflect(Point anchor, Point neighbor) =>
        new(anchor.X * 2 - neighbor.X, anchor.Y * 2 - neighbor.Y);

    private static Point[] Copy(IReadOnlyList<Point> points)
    {
        if (points is Point[] array) return (Point[])array.Clone();

        var copy = new Point[points.Count];
        for (int i = 0; i < points.Count; i++) copy[i] = points[i];
        return copy;
    }

    /// <summary>点是否落在这个（已闭合的）多边形里：射线法，用来判断「点在选区里」。不算边上的模糊地带。</summary>
    public static bool Contains(IReadOnlyList<Point> points, Point p)
    {
        if (points.Count < MinPoints) return false;

        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }

        return inside;
    }
}
