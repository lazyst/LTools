using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Views.Controls;

/// <summary>
/// 磁贴网格的落位几何：**纯函数，只吃矩形与点**，<c>--selftest</c> 可以直接断言。
/// </summary>
/// <remarks>
/// 两个调用方——拖动排序（<see cref="TileDragSort"/>）和从资源管理器拖文件进面板
/// （<c>SuperPanelWindow.Panel_DragOver</c>）——必须给出同一个「插到第几个」，
/// 否则同一个位置在两条路上表现不一样。计算磁贴矩形（要真窗口）留在调用方。
/// </remarks>
internal static class TileLayout
{
    /// <summary>
    /// 点在网格里的插入下标（<c>0..tiles.Count</c>）：先按 Y 找到行，再按行内各磁贴的 X 中点
    /// 决定插在谁前面。点在所有行上面 → 0；在两行之间的缝里 → 归下面那一行（离鼠标最近）；
    /// 在最后一行下面 → 末尾（松手在网格底下就是「加到最后」）。
    /// </summary>
    public static int InsertIndex(IReadOnlyList<Rect> tiles, Point point)
    {
        if (tiles.Count == 0) return 0;

        // 行开始下标：Top 相差超过 8 就是换行了（磁贴 56 / 72 高，行距 2，不会被误判）。
        var rowStarts = new List<int> { 0 };
        for (int i = 1; i < tiles.Count; i++)
            if (Math.Abs(tiles[i].Top - tiles[rowStarts[^1]].Top) > 8)
                rowStarts.Add(i);

        int lastStart = rowStarts[^1];
        double lastBottom = tiles[lastStart].Bottom;
        for (int i = lastStart; i < tiles.Count; i++) lastBottom = Math.Max(lastBottom, tiles[i].Bottom);
        if (point.Y > lastBottom) return tiles.Count;

        int row = rowStarts.Count - 1;
        for (int s = 0; s < rowStarts.Count; s++)
        {
            int start = rowStarts[s];
            int end = s + 1 < rowStarts.Count ? rowStarts[s + 1] : tiles.Count;
            double bottom = start;
            for (int i = start; i < end; i++) bottom = Math.Max(bottom, tiles[i].Bottom);

            if (point.Y < tiles[start].Top) { row = s; break; }
            if (point.Y <= bottom) { row = s; break; }
            row = s;
        }

        int first = rowStarts[row];
        int last = row + 1 < rowStarts.Count ? rowStarts[row + 1] : tiles.Count;
        for (int i = first; i < last; i++)
        {
            double centerX = tiles[i].Left + tiles[i].Width / 2;
            if (point.X < centerX) return i;
        }
        return last;
    }
}
