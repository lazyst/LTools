using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 版面压缩（照上游 <c>screenshotlayoutcompaction.h</c>）：把选区里**整段空白的行列**抽掉，
/// 再把剩下的每一段按 <c>spacing</c> 贴紧，导出尺寸跟着变小。
/// </summary>
/// <remarks>
/// <para>
/// 上游是对一条 <c>QPainterPath</c> 的每条轮廓取外接框当「段」；LTools 的选区本来就是
/// 「一块一块」的（<see cref="SelectionRegion"/>），所以直接拿每块加区的外接框当段，
/// 合并重叠的段 → 抽掉内部空段 → 依次排开。少于两块 = 没有整段空白可抽，返回 <c>null</c>。
/// </para>
/// <para>
/// **纯函数、不碰位图**，所以 <c>--selftest</c> 直接断言（见 capturePro 组的「版面压缩」）。
/// </para>
/// </remarks>
internal static class LayoutCompaction
{
    /// <summary>留白上限（照上游 <c>ScreenshotLayoutCompaction::maximumSpacing</c>）。</summary>
    public const int MaxSpacing = 100;

    /// <summary>一段：源区间 [Begin, End) 与它在输出里的起点。</summary>
    internal sealed record Band(int Begin, int End, int Destination)
    {
        public int Length => End - Begin;
    }

    /// <summary>一块选区搬到哪儿：源矩形（内容像素）+ 输出位移。</summary>
    internal sealed record Tile(int Region, int SourceX, int SourceY, int Width, int Height, int Dx, int Dy);

    /// <summary>压缩方案：输出尺寸 + 两条轴上的段 + 每块的位移。</summary>
    internal sealed record Plan(int Width, int Height, IReadOnlyList<Band> Columns,
        IReadOnlyList<Band> Rows, IReadOnlyList<Tile> Tiles)
    {
        public bool IsValid => Width > 0 && Height > 0;
    }

    /// <summary>
    /// 算压缩方案。<paramref name="blocks"/> 是每块加区在**内容像素**下的外接框；
    /// 少于两块、或本来就没有整段空白（压缩前后一样）时返回 <c>null</c>。
    /// </summary>
    public static Plan? Build(IReadOnlyList<Rect> blocks, int width, int height, int spacing)
    {
        if (blocks.Count < 2 || width <= 0 || height <= 0) return null;

        spacing = Math.Clamp(spacing, 0, MaxSpacing);

        var columns = Bands(blocks.Select(b => (int)Math.Floor(b.Left)),
            blocks.Select(b => (int)Math.Ceiling(b.Right)), width);
        var rows = Bands(blocks.Select(b => (int)Math.Floor(b.Top)),
            blocks.Select(b => (int)Math.Ceiling(b.Bottom)), height);
        if (columns.Count == 0 || rows.Count == 0) return null;

        int outWidth = Position(columns, spacing);
        int outHeight = Position(rows, spacing);
        if (outWidth <= 0 || outHeight <= 0) return null;

        bool changed = columns.Any(b => b.Destination != b.Begin)
                       || rows.Any(b => b.Destination != b.Begin);
        if (!changed) return null;

        var tiles = new List<Tile>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            Rect block = blocks[i];
            int centerX = (int)Math.Round(block.Left + block.Width / 2);
            int centerY = (int)Math.Round(block.Top + block.Height / 2);
            Band column = At(columns, centerX);
            Band row = At(rows, centerY);

            tiles.Add(new Tile(i, (int)Math.Round(block.X), (int)Math.Round(block.Y),
                Math.Max(1, (int)Math.Round(block.Width)), Math.Max(1, (int)Math.Round(block.Height)),
                column.Destination - column.Begin, row.Destination - row.Begin));
        }

        return new Plan(outWidth, outHeight, columns, rows, tiles);
    }

    /// <summary>合并重叠的区间，夹到 [0, extent]；首段从 0 起、末段到 extent 收（只抽内部空白）。</summary>
    private static List<Band> Bands(IEnumerable<int> begins, IEnumerable<int> ends, int extent)
    {
        var intervals = begins.Zip(ends, (b, e) => (Begin: Math.Max(0, b), End: Math.Min(extent, e)))
            .Where(pair => pair.End > pair.Begin)
            .OrderBy(pair => pair.Begin)
            .ThenBy(pair => pair.End)
            .ToList();

        var merged = new List<Band>(intervals.Count);
        foreach (var (begin, end) in intervals)
        {
            if (merged.Count > 0 && begin <= merged[^1].End)
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, end) };
            else
                merged.Add(new Band(begin, end, begin));
        }

        if (merged.Count == 0) return merged;

        // 只抽内部空白：外圈的取整余量照旧留着（与不压缩时同一口径）
        merged[0] = merged[0] with { Begin = 0 };
        merged[^1] = merged[^1] with { End = extent };
        return merged;
    }

    /// <summary>按留白依次排开，返回输出长度。</summary>
    private static int Position(List<Band> bands, int spacing)
    {
        int end = 0;
        for (int i = 0; i < bands.Count; i++)
        {
            bands[i] = bands[i] with { Destination = end };
            end += bands[i].Length + (i == bands.Count - 1 ? 0 : spacing);
        }

        return end;
    }

    /// <summary>某个坐标落在哪一段（越界时取最近的一段）。</summary>
    private static Band At(IReadOnlyList<Band> bands, int coordinate)
    {
        for (int i = bands.Count - 1; i >= 0; i--)
            if (coordinate >= bands[i].Begin) return bands[i];
        return bands[0];
    }
}
