using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>多区域选区里一块的加 / 减：加区并进来，减区挖掉。</summary>
internal enum RegionMode
{
    Add,
    Subtract,
}

/// <summary>
/// 多区域选区的一块：矩形或点列（自由 / 折线），带一个「加 / 减」。
/// </summary>
/// <remarks>
/// 坐标由调用方决定：窗口里一律是**窗口 DIP**，写进 <see cref="CaptureDocument.Regions"/> 前换成
/// 「相对底图左上角的图像像素」；导出时再按选区外接框换一遍局部坐标（见 <see cref="CaptureDocument.Render"/>）。
/// 这里是**纯数据 + 纯几何**，不碰窗口也不碰位图，`--selftest` 直接断言。
/// </remarks>
internal sealed record SelectionRegion(RegionMode Mode, Rect Box, Point[]? Points, bool Smooth = false)
{
    /// <summary>是点列（点够多）而不是矩形。</summary>
    public bool IsPolygon => Points is { Length: >= SelectionShape.MinPoints };

    /// <summary>
    /// 这一块自己的外接框：矩形就是矩形；点列是点列的框；平滑曲线按**曲线的框**——
    /// Catmull-Rom 会在边中间外凸，按点列算会把外凸那一段裁掉（上游 <c>fromPath</c> 也是取 path 的 bounds）。
    /// </summary>
    public Rect Bounds => !IsPolygon
        ? Box
        : Smooth
            ? SelectionShape.SmoothBounds(Points!)
            : SelectionShape.Bounds(Points!);

    /// <summary>这一块的几何：点列闭合填充（<see cref="Smooth"/> 时是平滑曲线），矩形就是矩形。</summary>
    public Geometry Geometry() => IsPolygon
        ? SelectionShape.Geometry(Points!, closed: true, smooth: Smooth)
        : new RectangleGeometry(Box);

    public static SelectionRegion FromRect(RegionMode mode, Rect rect) => new(mode, rect, null);

    public static SelectionRegion FromPoints(RegionMode mode, IReadOnlyList<Point> points,
        bool smooth = false) => new(mode, SelectionShape.Bounds(points), Copy(points), smooth);

    /// <summary>整体平移。</summary>
    public SelectionRegion Offset(Vector delta) => new(
        Mode,
        new Rect(Box.X + delta.X, Box.Y + delta.Y, Box.Width, Box.Height),
        Points is null ? null : SelectionShape.Offset(Points, delta),
        Smooth);

    /// <summary>按外接框从 <paramref name="from"/> 变到 <paramref name="to"/>（与标注缩放同一口径）。</summary>
    public SelectionRegion Scale(Rect from, Rect to)
    {
        if (from.Width <= 0 || from.Height <= 0)
            return Offset(new Vector(to.X - from.X, to.Y - from.Y));

        double sx = to.Width / from.Width;
        double sy = to.Height / from.Height;

        var box = new Rect(
            to.X + (Box.X - from.X) * sx,
            to.Y + (Box.Y - from.Y) * sy,
            Box.Width * sx,
            Box.Height * sy);

        return new SelectionRegion(Mode, box,
            Points is null ? null : SelectionShape.Scale(Points, from, to), Smooth);
    }

    /// <summary>
    /// 窗口 DIP → 图像像素（相对底图左上角）。点列按点取整；矩形按两条边取整、宽度**含两端像素**
    /// （与一整块矩形选区的导出尺寸同一口径，见 <c>CaptureOverlayWindow.SyncArea</c>）。
    /// </summary>
    public SelectionRegion ToImage(Point origin, double scale)
    {
        double s = scale > 0 ? scale : 1;

        if (Points is { } points)
        {
            var image = SelectionShape.ToImage(points, origin, s);
            return new SelectionRegion(Mode, SelectionShape.Bounds(image), image, Smooth);
        }

        double x1 = Math.Round((Box.X - origin.X) * s);
        double y1 = Math.Round((Box.Y - origin.Y) * s);
        double x2 = Math.Round((Box.Right - origin.X) * s);
        double y2 = Math.Round((Box.Bottom - origin.Y) * s);

        var box = new Rect(x1, y1, Math.Max(1, x2 - x1 + 1), Math.Max(1, y2 - y1 + 1));
        return new SelectionRegion(Mode, box, null);
    }

    /// <summary>夹到底图里面（点列按点、矩形按边），四舍五入之后可能差出去一两个像素。</summary>
    public SelectionRegion Clamp(int width, int height)
    {
        if (Points is not { } points)
        {
            double x = Math.Clamp(Box.X, 0, Math.Max(0, width - 1));
            double y = Math.Clamp(Box.Y, 0, Math.Max(0, height - 1));
            return new SelectionRegion(Mode,
                new Rect(x, y, Math.Max(1, Math.Min(Box.Width, width - x)), Math.Max(1, Math.Min(Box.Height, height - y))),
                null);
        }

        var copy = new Point[points.Length];
        for (int i = 0; i < points.Length; i++)
            copy[i] = new Point(
                Math.Clamp(points[i].X, 0, Math.Max(0, width - 1)),
                Math.Clamp(points[i].Y, 0, Math.Max(0, height - 1)));

        return new SelectionRegion(Mode, SelectionShape.Bounds(copy), copy, Smooth);
    }

    private static Point[] Copy(IReadOnlyList<Point> points)
    {
        if (points is Point[] array) return (Point[])array.Clone();

        var copy = new Point[points.Count];
        for (int i = 0; i < points.Count; i++) copy[i] = points[i];
        return copy;
    }
}

/// <summary>
/// 多区域选区的组合规则：加区并起来、减区挖掉，外接框只看加区。
/// **纯函数**，画面挖洞、标注裁剪、导出裁剪三处共用这一份（别再各写一遍）。
/// </summary>
internal static class SelectionSet
{
    /// <summary>外接框 = 所有加区的并集（减区不扩大取景范围）。一块加区都没有 = <see cref="Rect.Empty"/>。</summary>
    public static Rect Bounds(IReadOnlyList<SelectionRegion> regions)
    {
        Rect box = Rect.Empty;
        for (int i = 0; i < regions.Count; i++)
            if (regions[i].Mode == RegionMode.Add) box.Union(regions[i].Bounds);
        return box;
    }

    /// <summary>
    /// 合起来的几何：加区逐个并、减区逐个挖。没有加区（或全被挖空）时返回 null ——
    /// 调用方拿它当「没有可导出的内容」，别导出一张全透明的图。
    /// </summary>
    /// <param name="radius">
    /// 矩形块的圆角半径（与 <paramref name="regions"/> 同一坐标系）。点列块不受影响——
    /// 上游对自定义路径也是 <c>cornerRadius = 0</c>。
    /// </param>
    public static Geometry? Combine(IReadOnlyList<SelectionRegion> regions, double radius = 0)
    {
        Geometry? geometry = null;
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            Geometry shape = Shape(region, radius);
            if (region.Mode == RegionMode.Subtract)
            {
                if (geometry == null) continue;                  // 还没有加区，减什么都没意义
                geometry = new CombinedGeometry(GeometryCombineMode.Exclude, geometry, shape);
                continue;
            }

            geometry = geometry == null
                ? shape
                : new CombinedGeometry(GeometryCombineMode.Union, geometry, shape);
        }

        if (geometry == null) return null;
        geometry.Freeze();
        return geometry.IsEmpty() ? null : geometry;
    }

    private static Geometry Shape(SelectionRegion region, double radius)
    {
        if (radius <= 0 || region.IsPolygon) return region.Geometry();

        double r = Math.Clamp(radius, 0, Math.Max(0, Math.Min(region.Box.Width, region.Box.Height) / 2));
        if (r <= 0) return region.Geometry();

        var rounded = new RectangleGeometry(region.Box, r, r);
        rounded.Freeze();
        return rounded;
    }

    /// <summary>点是不是落在选区里（多块 / 挖洞 / 圆角都算对）。</summary>
    public static bool Contains(IReadOnlyList<SelectionRegion> regions, Point p, double radius = 0) =>
        Combine(regions, radius) is { } geometry && geometry.FillContains(p);

    /// <summary>
    /// 在**选区外**（压暗的阴影上）按下去时，是「拖动整块选区」还是「重新框选」。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 照 Snow Shot：Move 工具下，单块矩形选区外面那圈也算「整块移动」的那片抓手
    /// （<c>dragModeForVirtualPosition</c> 对外面的点给 <c>All</c>）；多块 / 挖洞 / 自由 / 折线
    /// 才走「外面 = 重新框选」（上游 <c>outsideClickRecreatesSelection</c>）。
    /// </para>
    /// <para>
    /// 用户报过「选区之后拖阴影部分应该能挪选区，结果又拖出一个新框」——框完了想微调位置，
    /// 手很自然会落在选区外面那圈压暗的阴影上。加区 / 减区仍然优先：按着 <c>Shift</c> / <c>Alt</c>
    /// 拖出去就是加 / 减，不是移动。
    /// </para>
    /// </remarks>
    public static bool MovesOnOutsideDrag(
        IReadOnlyList<SelectionRegion> regions, bool moveTool, bool shift, bool alt) =>
        moveTool && !shift && !alt &&
        regions.Count == 1 &&
        regions[0].Mode == RegionMode.Add &&
        !regions[0].IsPolygon;

    /// <summary>
    /// 光标下最上层那块**加区**（列表顺序 = 叠放顺序，后画的在上；重叠时取最后一块）。
    /// 减区是挖掉的洞，永远命中不到；没命中返回 -1。
    /// </summary>
    public static int TopmostAt(IReadOnlyList<SelectionRegion> regions, Point p, double radius = 0)
    {
        // 挖掉的洞里「不在选区里」：减区不会成为拖动目标，也不该被算成命中了下面那块
        if (!Contains(regions, p, radius)) return -1;

        for (int i = regions.Count - 1; i >= 0; i--)
        {
            if (regions[i].Mode != RegionMode.Add) continue;
            if (Shape(regions[i], radius).FillContains(p)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 在多块选区上按下拖动时：**只挪命中的那一块**还是**挪整个选区**。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>ScreenshotSelectionModel::beginMoveDrag</c>：多块编辑里默认只动命中的那块
    /// （<c>regionAt(startPosition)</c>），按住「移动整个选区」（默认 <c>Space</c>）才整体动。
    /// 单块选区没有「哪一块」的问题，一律整体动（也就等于拖这一块）。
    /// </remarks>
    public static bool MovesSingleRegion(int regionCount, int hit, bool moveEntireHeld) =>
        regionCount > 1 && hit >= 0 && !moveEntireHeld;

    /// <summary>拖一块区域时用来吸附的参考边：**别的区域**的四条边（上游 <c>beginRegionMove</c> 的 references）。</summary>
    public static (double[] Xs, double[] Ys) OtherEdges(IReadOnlyList<SelectionRegion> regions, int skip)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = 0; i < regions.Count; i++)
        {
            if (i == skip) continue;
            Rect box = regions[i].Bounds;
            xs.Add(box.Left);
            xs.Add(box.Right);
            ys.Add(box.Top);
            ys.Add(box.Bottom);
        }
        return (xs.ToArray(), ys.ToArray());
    }
}
