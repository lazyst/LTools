using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 花括号的几何（移植 <c>snow-draw-engine-document/src/bracket.rs</c>）：
/// 三个点 a（起）/ c（尖）/ b（终）定义一条「局部坐标系」，四段三次贝塞尔拼出
/// 两条对称的弧 + 中间那个圆角尖。**不含描边溢出**，绘制 / 命中 / 包围盒共用。
/// </summary>
internal static class BracketInk
{
    /// <summary>一段三次贝塞尔的控制点。</summary>
    internal readonly record struct Cubic(Point P0, Point C1, Point C2, Point P3)
    {
        public Point At(double t)
        {
            double u = 1 - t;
            double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
            return new Point(
                a * P0.X + b * C1.X + c * C2.X + d * P3.X,
                a * P0.Y + b * C1.Y + c * C2.Y + d * P3.Y);
        }
    }

    /// <summary>
    /// 造四段弧。三点退化（首尾重合）时返回 null——上游 <c>bracket_segments</c> 同一条判定。
    /// </summary>
    public static Cubic[]? Segments(Point a, Point c, Point b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!double.IsFinite(length) || length <= 1e-6) return null;

        // v = 主线方向；n = 它的左手法线（尖往哪边鼓就看 depth 的符号）
        double vx = dx / length, vy = dy / length;
        double nx = vy, ny = -vx;
        double depth = (c.X - a.X) * nx + (c.Y - a.Y) * ny;
        double tip = (c.X - a.X) * vx + (c.Y - a.Y) * vy;

        Point P(double x, double y) => new(a.X + nx * x + vx * y, a.Y + ny * x + vy * y);

        double spine = depth * 0.5;
        var upper = P(spine, tip * 0.5);
        var lower = P(spine, (tip + length) / 2);

        return new[]
        {
            new Cubic(a, P(spine, 0), P(spine, tip * 0.25), upper),
            new Cubic(upper, P(spine, tip * 0.75), P(spine, tip), c),
            new Cubic(c, P(spine, tip), P(spine, tip + (length - tip) * 0.25), lower),
            new Cubic(lower, P(spine, tip + (length - tip) * 0.75), P(spine, length), b),
        };
    }

    /// <summary>四段弧的并集包围盒（再按描边一半外扩）。</summary>
    public static Rect Bounds(Cubic[] segments, double strokeWidth)
    {
        var box = Rect.Empty;
        foreach (var s in segments)
        {
            var b = MarkGeometry.RectOf(s.P0, s.P3);
            b.Union(s.C1);
            b.Union(s.C2);
            box = box.IsEmpty ? b : Rect.Union(box, b);
        }

        double r = Math.Max(0, strokeWidth) / 2;
        box.Inflate(r, r);
        return box;
    }

    /// <summary><paramref name="point"/> 到这条括号的距离（按采样折线算，够精确也够快）。</summary>
    public static bool Hit(Cubic[] segments, Point point, double tolerance)
    {
        const int steps = 16;
        foreach (var s in segments)
        {
            var previous = s.P0;
            for (int i = 1; i <= steps; i++)
            {
                var next = s.At((double)i / steps);
                if (MarkGeometry.NearSegment(point, previous, next, tolerance)) return true;
                previous = next;
            }
        }

        return false;
    }

    /// <summary>把四段弧画成几何（绘制与导出共用同一份）。</summary>
    public static Geometry ToGeometry(IReadOnlyList<Cubic> segments)
    {
        var figure = new PathFigure { StartPoint = segments[0].P0, IsClosed = false, IsFilled = false };
        foreach (var s in segments)
            figure.Segments.Add(new BezierSegment(s.C1, s.C2, s.P3, true));
        var geo = new PathGeometry(new[] { figure });
        geo.Freeze();
        return geo;
    }
}
