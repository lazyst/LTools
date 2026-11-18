using System;

namespace LTools.Features.CapturePro.Split;

/// <summary>切分方向：纵向切（左右分栏）/ 横向切（上下分栏）/ 宫格。</summary>
internal enum SplitDirection
{
    Vertical,
    Horizontal,
    Grid,
}

/// <summary>切分方式：智能（找内容空白处）/ 等分 / 精确（按像素或百分比）。</summary>
internal enum SplitType
{
    Equal,
    Precise,
    Smart,
}

/// <summary>精确切分的尺寸单位。</summary>
internal enum SplitUnit
{
    Pixels,
    Percentage,
}

/// <summary>
/// 一次切分的全部设置。照上游 <c>ImageSplitSettings</c>：方向 / 方式 / 单位 / 张数 /
/// 宫格行列 / 每张像素 / 每张百分比 / 重叠宽度。
/// </summary>
/// <remarks>
/// <para>
/// **百分比按原文保存**（字符串），因为用户可能写 <c>12.5</c>、<c>33.333</c>——
/// 换算成 <see cref="double"/> 再回写会悄悄改掉用户填的数。布局计算按十进制精确算（见
/// <see cref="SplitLayoutCalculator"/>），不经过浮点。
/// </para>
/// <para>
/// 这是「设置」而不是「状态」：手拖过的切分线不在这里，见 <see cref="SplitState"/>。
/// </para>
/// </remarks>
internal sealed class SplitSettings : IEquatable<SplitSettings>
{
    public SplitDirection Direction { get; set; } = SplitDirection.Vertical;

    public SplitType Type { get; set; } = SplitType.Smart;

    public SplitUnit Unit { get; set; } = SplitUnit.Percentage;

    /// <summary>纵向 / 横向切时的张数。</summary>
    public int Count { get; set; } = 2;

    /// <summary>宫格的行数。</summary>
    public int Rows { get; set; } = 2;

    /// <summary>宫格的列数。</summary>
    public int Columns { get; set; } = 2;

    /// <summary>精确切分、单位 = 像素时每张的宽 / 高。</summary>
    public int Pixels { get; set; } = 16;

    /// <summary>精确切分、单位 = 百分比时每张占的百分比原文。</summary>
    public string Percentage { get; set; } = "50";

    /// <summary>每张图在切分线两侧各多带多少像素（0 = 不重叠）。</summary>
    public int Overlap { get; set; }

    public SplitSettings Clone() => new()
    {
        Direction = Direction,
        Type = Type,
        Unit = Unit,
        Count = Count,
        Rows = Rows,
        Columns = Columns,
        Pixels = Pixels,
        Percentage = Percentage,
        Overlap = Overlap,
    };

    /// <summary>除重叠宽度以外完全一样：这种改动只动裁切边距，别丢掉用户手拖的切分线。</summary>
    public bool EqualExceptOverlap(SplitSettings other) =>
        other != null &&
        Direction == other.Direction &&
        Type == other.Type &&
        Unit == other.Unit &&
        Count == other.Count &&
        Rows == other.Rows &&
        Columns == other.Columns &&
        Pixels == other.Pixels &&
        Percentage == other.Percentage;

    public bool Equals(SplitSettings? other) =>
        other is not null && EqualExceptOverlap(other) && Overlap == other.Overlap;

    public override bool Equals(object? obj) => Equals(obj as SplitSettings);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Direction);
        hash.Add(Type);
        hash.Add(Unit);
        hash.Add(Count);
        hash.Add(Rows);
        hash.Add(Columns);
        hash.Add(Pixels);
        hash.Add(Percentage);
        hash.Add(Overlap);
        return hash.ToHashCode();
    }

    public override string ToString() =>
        $"{Direction}/{Type}/{Unit} count={Count} rows={Rows} columns={Columns} " +
        $"pixels={Pixels} percentage={Percentage} overlap={Overlap}";
}

/// <summary>
/// 当前切分状态：设置 + 两条轴上的切分线 + 是否被手拖过。撤销栈里存的就是它。
/// </summary>
/// <remarks>
/// 两条轴是数组，而记录的默认相等只比引用；这里换成逐元素比较，否则
/// 「把一条线拖回原位」会被当成改过（撤销栈多一条、脏判断失效）。
/// </remarks>
internal sealed record SplitState(
    SplitSettings Settings,
    int[] Vertical,
    int[] Horizontal,
    bool ManuallyEdited)
{
    public bool Equals(SplitState? other) =>
        other is not null &&
        Settings.Equals(other.Settings) &&
        Vertical.AsSpan().SequenceEqual(other.Vertical) &&
        Horizontal.AsSpan().SequenceEqual(other.Horizontal) &&
        ManuallyEdited == other.ManuallyEdited;

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Settings);
        foreach (int value in Vertical) hash.Add(value);
        foreach (int value in Horizontal) hash.Add(value);
        hash.Add(ManuallyEdited);
        return hash.ToHashCode();
    }
}
