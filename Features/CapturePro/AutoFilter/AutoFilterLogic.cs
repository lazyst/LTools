using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>识别到的一块区域，坐标是**窗口 DIP**（已从检测图的像素换算过来）。</summary>
internal readonly record struct AutoRegion(int Id, Rect Bounds, string Category);

/// <summary>
/// 自动打码的画布逻辑：命中 / 框选 / 填充事务 / 绘制顺序。
/// </summary>
/// <remarks>
/// <para>
/// 逐条照抄上游 <c>auto_filter.rs</c> + <c>auto_filter_workflow.rs</c>：
/// 命中取「光标下面积最小的那个」（同面积取 id 小的）；框选取与矩形**相交**的全部区域；
/// 单击 / 框选松手都是 <c>toggle = true</c> 的填充事务（同类型再点一次删掉，不同类型替换）；
/// 「填充区域」下拉是 <c>toggle = false</c>（只补不删）。
/// </para>
/// <para>
/// 纯函数（不碰 WPF 的 <see cref="Rect"/> 之外的东西），<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class AutoFilterLogic
{
    /// <summary>上游 <c>POINTER_DRAG_THRESHOLD</c>：位移不到这么多 DIP 仍算「点一下」。</summary>
    public const double DragThreshold = 4.0;

    /// <summary>检测记录还配不配当前选区（上游 <c>available()</c>：<c>sourceBounds == bounds</c>）。</summary>
    public static bool BoundsMatch(Rect a, Rect b) =>
        Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6 &&
        Math.Abs(a.Width - b.Width) < 1e-6 && Math.Abs(a.Height - b.Height) < 1e-6;

    /// <summary>
    /// 检测结果（源图像素）→ 窗口 DIP：按选区的宽高比缩放，再与选区求交、丢掉空区域。
    /// 与上游 <c>finish()</c> 里那段换算一一对应。
    /// </summary>
    public static List<AutoRegion> Project(IReadOnlyList<DetectedRegion> detected, Rect bounds, int pixelWidth,
        int pixelHeight)
    {
        var output = new List<AutoRegion>(detected.Count);
        if (pixelWidth <= 0 || pixelHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return output;

        double sx = bounds.Width / pixelWidth;
        double sy = bounds.Height / pixelHeight;

        foreach (var region in detected)
        {
            var r = region.Bounds;
            var box = new Rect(bounds.X + r.X * sx, bounds.Y + r.Y * sy, r.W * sx, r.H * sy);
            box.Intersect(bounds);
            if (box.Width <= 0 || box.Height <= 0) continue;
            output.Add(new AutoRegion(region.Id, box, region.Category));
        }

        return output;
    }

    /// <summary>光标下最小的那块（同面积取 id 小的；没有 = null）。</summary>
    public static AutoRegion? RegionAt(IReadOnlyList<AutoRegion> regions, Point point)
    {
        AutoRegion? best = null;
        foreach (var region in regions)
        {
            var r = region.Bounds;
            if (point.X < r.Left || point.X >= r.Right || point.Y < r.Top || point.Y >= r.Bottom) continue;
            if (best is not { } current || IsSmaller(region, current)) best = region;
        }

        return best;
    }

    private static bool IsSmaller(AutoRegion a, AutoRegion b)
    {
        double areaA = a.Bounds.Width * a.Bounds.Height;
        double areaB = b.Bounds.Width * b.Bounds.Height;
        return areaA < areaB || (Math.Abs(areaA - areaB) < 1e-9 && a.Id < b.Id);
    }

    /// <summary>框选命中的区域（上游按「矩形相交」算，不是包含）。</summary>
    public static List<int> RegionsIn(IReadOnlyList<AutoRegion> regions, Rect marquee)
    {
        var hits = new List<int>();
        foreach (var region in regions)
        {
            var r = region.Bounds;
            if (r.Left < marquee.Right && r.Right > marquee.Left &&
                r.Top < marquee.Bottom && r.Bottom > marquee.Top)
                hits.Add(region.Id);
        }

        return hits;
    }

    /// <summary>这一块已有的填充（第一个 <c>AutoRegionId</c> 匹配的滤镜；没有 = null）。</summary>
    public static Mark.Filter? FillOf(IReadOnlyList<Mark> marks, int regionId) =>
        marks.OfType<Mark.Filter>().FirstOrDefault(f => f.AutoRegionId == regionId);

    /// <summary>
    /// 这一块是不是已经用**同类型**填过（上游高亮成绿色 = true，红色 = false）。
    /// </summary>
    public static bool FilledWith(IReadOnlyList<Mark> marks, int regionId, FilterKind kind) =>
        FillOf(marks, regionId) is { } fill && fill.Kind == kind;

    /// <summary>
    /// 填充事务（上游 <c>auto_filter_fill_transaction</c>）：
    /// <paramref name="toggle"/> 且同类型 → 删掉；不同类型 → 原地替换；没有 → 追加。
    /// 改完把绘制顺序归一（自动填充按区域 id 排、智能擦除在前、其余保持原序）。
    /// 返回有没有真的改动。
    /// </summary>
    public static bool Fill(List<Mark> marks, IReadOnlyList<AutoRegion> regions, IReadOnlyCollection<int> ids,
        FilterKind kind, double strength, bool toggle)
    {
        var wanted = ids.ToHashSet();
        var remove = new HashSet<int>();
        var replaced = new Dictionary<int, Mark>();
        var appended = new List<Mark>();
        bool changed = false;

        foreach (var region in regions)
        {
            if (!wanted.Contains(region.Id)) continue;

            int index = marks.FindIndex(m => m is Mark.Filter { AutoRegionId: not null } f && f.AutoRegionId == region.Id);
            Mark.Filter? current = index >= 0 ? (Mark.Filter)marks[index] : null;

            if (current != null && toggle && current.Kind == kind)
            {
                remove.Add(index);
                changed = true;
                continue;
            }

            if (current != null)
            {
                if (current.Kind == kind && Math.Abs(current.Strength - strength) < 1e-9) continue;   // 一模一样：别动
                replaced[index] = new Mark.Filter(current.A, current.B, kind, strength, region.Id);
                changed = true;
                continue;
            }

            appended.Add(new Mark.Filter(region.Bounds.TopLeft, region.Bounds.BottomRight, kind, strength, region.Id));
            changed = true;
        }

        if (!changed) return false;

        var next = new List<Mark>(marks.Count + appended.Count);
        for (int i = 0; i < marks.Count; i++)
        {
            if (remove.Contains(i)) continue;
            next.Add(replaced.TryGetValue(i, out var mark) ? mark : marks[i]);
        }

        next.AddRange(appended);
        NormalizeOrder(next);

        marks.Clear();
        marks.AddRange(next);
        return true;
    }

    /// <summary>
    /// 绘制顺序归一（上游 <c>normalize_auto_filter_order</c>）：智能擦除 → 自动填充（按区域 id）→ 其余。
    /// 自动填充永远压在普通标注下面，否则后画的一块会把前面打好的码盖住。
    /// </summary>
    public static void NormalizeOrder(List<Mark> marks)
    {
        var smart = marks.OfType<Mark.Filter>().Where(f => f.AutoRegionId == null && f.Kind == FilterKind.SmartErase).ToList();
        var fills = marks.OfType<Mark.Filter>().Where(f => f.AutoRegionId != null).ToList();
        if (smart.Count == 0 && fills.Count == 0) return;

        var special = new HashSet<Mark>(smart);
        foreach (var fill in fills) special.Add(fill);

        var ordered = new List<Mark>(marks.Count);
        ordered.AddRange(smart);
        ordered.AddRange(fills.OrderBy(f => f.AutoRegionId!.Value));
        ordered.AddRange(marks.Where(m => !special.Contains(m)));

        if (ordered.SequenceEqual(marks)) return;

        marks.Clear();
        marks.AddRange(ordered);
    }
}
