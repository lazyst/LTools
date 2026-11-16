using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>选区的形状：矩形（拖框）/ 自由（按住圈）/ 折线（逐点单击）。</summary>
internal enum SelectionMode
{
    Rect,
    Lasso,
    Polyline,
}

/// <summary>
/// 非矩形选区的几何：点列的外接框、补点阈值、移动 / 缩放、DIP ↔ 图像像素、闭合成几何。
/// **纯函数、不碰窗口**，所以 <c>--selftest</c> 直接断言（见 capturePro 组）。
/// </summary>
/// <remarks>
/// 点列一律存**窗口 DIP**（与选区框、把手同一套坐标），只在导出前换成「图像像素」写进
/// <see cref="CaptureDocument.Polygon"/>。矩形选区不用这里的东西——它仍然是 <see cref="Rect"/>。
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
    public static Geometry Geometry(IReadOnlyList<Point> points, bool closed)
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
