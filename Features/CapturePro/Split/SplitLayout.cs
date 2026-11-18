using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 算好的切分布局：图尺寸 + 两条轴上的切分线 + 重叠宽度 + 校验错误。
/// </summary>
/// <remarks>
/// <para>
/// 区域用**半开区间像素边界**表示，<see cref="BaseRegionAt"/> 给「不算重叠」的原始格子，
/// <see cref="RegionAt"/> 给真正要导出的裁切矩形（每边外扩 <see cref="Overlap"/>，夹在图内）。
/// </para>
/// <para>
/// 序号 / 预览编号一律按 <see cref="BaseRegionAt"/> 的格子算（重叠只影响导出），
/// 与上游 <c>baseRegionAt</c> / <c>regionAt</c> 同口径。
/// </para>
/// </remarks>
internal sealed class SplitLayout
{
    public SplitLayout(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public SplitLines Vertical { get; } = new();

    public SplitLines Horizontal { get; } = new();

    public int Overlap { get; set; }

    /// <summary>字段名 → 中文说明（<c>count</c> / <c>rows</c> / <c>columns</c> / <c>pixels</c> / <c>percentage</c> / <c>overlap</c> / <c>image</c> / <c>type</c> / <c>direction</c>）。</summary>
    public Dictionary<string, string> Errors { get; } = new(StringComparer.Ordinal);

    public bool IsValid =>
        Width > 0 && Height > 0 && Errors.Count == 0 && Overlap >= 0 &&
        Vertical.IsValid(Width) && Horizontal.IsValid(Height);

    /// <summary>切法算不出来时是 0（按钮置灰），不是 1。</summary>
    public long OutputCount
    {
        get
        {
            if (Width <= 0 || Height <= 0 || Errors.Count > 0 || Overlap < 0 ||
                Vertical.Count >= Width || Horizontal.Count >= Height)
            {
                return 0;
            }
            return (long)(Vertical.Count + 1) * (Horizontal.Count + 1);
        }
    }

    /// <summary>第 <paramref name="index"/> 张的原始格子（不含重叠）。</summary>
    public Int32Rect BaseRegionAt(long index)
    {
        if (index < 0 || index >= OutputCount) return Int32Rect.Empty;

        long columnCount = Vertical.Count + 1;
        int column = (int)(index % columnCount);
        int row = (int)(index / columnCount);

        int left = column == 0 ? 0 : Vertical[column - 1];
        int right = column == Vertical.Count ? Width : Vertical[column];
        int top = row == 0 ? 0 : Horizontal[row - 1];
        int bottom = row == Horizontal.Count ? Height : Horizontal[row];
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>第 <paramref name="index"/> 张真正导出的矩形（每边外扩重叠宽度）。</summary>
    public Int32Rect RegionAt(long index)
    {
        var baseRegion = BaseRegionAt(index);
        if (baseRegion.Width <= 0 || baseRegion.Height <= 0 || Overlap == 0) return baseRegion;

        int left = Math.Max(0, baseRegion.X - Overlap);
        int top = Math.Max(0, baseRegion.Y - Overlap);
        int right = Math.Min(Width, baseRegion.X + baseRegion.Width + Overlap);
        int bottom = Math.Min(Height, baseRegion.Y + baseRegion.Height + Overlap);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>复制一份布局（只共用图尺寸，线各自一份）。</summary>
    public SplitLayout Clone()
    {
        var copy = new SplitLayout(Width, Height) { Overlap = Overlap };
        copy.Vertical.ReplaceWith(Vertical.Values);
        copy.Horizontal.ReplaceWith(Horizontal.Values);
        foreach (var pair in Errors) copy.Errors[pair.Key] = pair.Value;
        return copy;
    }
}

/// <summary>算好的一份布局 + 它是按哪份设置算的（异步计算回来时用来对账）。</summary>
internal sealed record SplitPrepared(SplitSettings Settings, SplitLayout Layout);
