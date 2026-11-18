using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>
/// 自动打码的界面状态：识别结果、检测期扫描光带、结果闪红、悬停 / 框选高亮。
/// </summary>
/// <remarks>
/// <para>
/// 只有一份状态：覆盖层写、画布读。区域坐标一律**窗口 DIP**（识别后立刻换算好），
/// 与标注、选区同一个坐标系。
/// </para>
/// <para>
/// 检测记录还配不配当前选区由 <see cref="Matches"/> 判定（上游 <c>available()</c>：
/// 有记录、<c>sourceBounds</c> 与当前选区相同、区域非空），三者缺一「填充区域」就该置灰。
/// </para>
/// </remarks>
internal sealed class AutoFilterState
{
    /// <summary>结果闪红的时长（上游 <c>m_flashUntil = arrived + 300</c>）。</summary>
    public const double FlashMs = 300;

    /// <summary>扫描光带一圈的时长（上游 <c>elapsed % 1400</c>）。</summary>
    public const double ScanPeriodMs = 1400;

    /// <summary>识别到的区域（按上游输出顺序，<see cref="AutoRegion.Id"/> 1 起）。</summary>
    public List<AutoRegion> Regions { get; } = new();

    /// <summary>这批区域是按哪块选区识别的（像素 / DIP 都一样比，见 <see cref="Matches"/>）。</summary>
    public Rect Bounds { get; private set; } = Rect.Empty;

    /// <summary>检测进行中（画扫描光带）。</summary>
    public bool Detecting { get; set; }

    /// <summary>扫描光带的相位起点（<c>Stopwatch</c> 毫秒，跨检测保留，带子不会跳）。</summary>
    public double ScanStartMs { get; set; }

    /// <summary>结果闪红到什么时候（<see cref="Environment.TickCount64"/> 毫秒）。</summary>
    public long FlashUntil { get; set; }

    /// <summary>悬停命中的区域 id（-1 = 没有）。</summary>
    public int HoverId { get; set; } = -1;

    /// <summary>框选矩形（DIP；空 = 没在框选）。</summary>
    public Rect? Marquee { get; set; }

    /// <summary>手势命中的区域（按下时就定下来，松手一次性提交）。</summary>
    public List<int> Hits { get; } = new();

    /// <summary>手势进行中（此时高亮 <see cref="Hits"/>，否则高亮悬停那一块）。</summary>
    public bool GestureActive { get; set; }

    /// <summary>这批区域还算不算数：有结果、跟当前选区对得上、且不是空的。</summary>
    public bool Matches(Rect bounds) =>
        Regions.Count > 0 && Bounds.Width > 0 && AutoFilterLogic.BoundsMatch(Bounds, bounds);

    /// <summary>换一批结果（选区的 bounds 一起记下来）。</summary>
    public void Set(Rect bounds, IEnumerable<AutoRegion> regions)
    {
        Bounds = bounds;
        Regions.Clear();
        Regions.AddRange(regions);
        HoverId = -1;
        Marquee = null;
        Hits.Clear();
        GestureActive = false;
    }

    /// <summary>丢掉结果（选区变了 / 退出工具）。</summary>
    public void Clear()
    {
        Bounds = Rect.Empty;
        Regions.Clear();
        HoverId = -1;
        Marquee = null;
        Hits.Clear();
        GestureActive = false;
        Detecting = false;
        FlashUntil = 0;
    }

    /// <summary>现在该不该画闪红。</summary>
    public bool Flashing(long nowMs) => FlashUntil > nowMs;

    /// <summary>扫描光带在这一刻的位置（选区 DIP，带子高 42、中心在 y）。</summary>
    public double ScanY(long nowMs)
    {
        double phase = nowMs % ScanPeriodMs / ScanPeriodMs;
        return Bounds.Top + phase * (Bounds.Height + 60) - 30;
    }
}
