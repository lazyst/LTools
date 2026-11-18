using System;
using System.Globalization;
using System.Windows.Media;

namespace LTools.Features.CapturePro.Collage;

/// <summary>拼接方向：纵向 / 横向 / 宫格。</summary>
internal enum CollageDirection
{
    Vertical,
    Horizontal,
    Custom,
}

/// <summary>尺寸方式：统一尺寸（同一格尺寸等比缩放）/ 原始尺寸（各图自己的像素）。</summary>
internal enum CollageSizing
{
    Uniform,
    Original,
}

/// <summary>
/// 一次拼接的全部设置（照上游 <c>ImageStitchingSettings</c>）。
/// </summary>
/// <remarks>
/// 外边距 / 间距**永远存输出像素**：界面上可以切到百分比输入（参考长度 = 首图宽 / 首图高 /
/// 宫格列宽），但那只是输入方式，提交前就换算成像素，所以这里和磁盘上都只有一种单位。
/// </remarks>
internal sealed record CollageSettings(
    CollageDirection Direction,
    CollageSizing Sizing,
    int Border,
    int Spacing,
    Color Background,
    int Rows,
    int Columns,
    bool CustomGridInitialized)
{
    /// <summary>新开一个空窗时的默认设置（与上游默认值一致：纵向 / 统一尺寸 / 白底）。</summary>
    public static CollageSettings Default => new(CollageDirection.Vertical, CollageSizing.Uniform,
        0, 0, Colors.White, 1, 1, false);

    /// <summary>外边距 / 间距 / 行列都是合法值（照上游 <c>settingsValid</c>）。</summary>
    public bool IsValid => Border >= 0 && Spacing >= 0 && Rows >= 1 && Columns >= 1;

    /// <summary>
    /// 除背景色以外是否完全一致。照上游 <c>setSettings</c>：几何一变就清空所有图片的
    /// 手摆位置，只换背景色不动位置。
    /// </summary>
    public bool GeometryEquals(CollageSettings other) =>
        Direction == other.Direction && Sizing == other.Sizing && Border == other.Border &&
        Spacing == other.Spacing && Rows == other.Rows && Columns == other.Columns &&
        CustomGridInitialized == other.CustomGridInitialized;

    public string DirectionKey => Direction switch
    {
        CollageDirection.Horizontal => "horizontal",
        CollageDirection.Custom => "custom",
        _ => "vertical",
    };

    public string SizingKey => Sizing == CollageSizing.Original ? "original" : "uniform";

    public static CollageDirection ParseDirection(string? text) => text switch
    {
        "horizontal" => CollageDirection.Horizontal,
        "custom" => CollageDirection.Custom,
        _ => CollageDirection.Vertical,
    };

    public static CollageSizing ParseSizing(string? text) =>
        string.Equals(text, "original", StringComparison.OrdinalIgnoreCase)
            ? CollageSizing.Original
            : CollageSizing.Uniform;
}

/// <summary>
/// 背景色的文本形式：<c>#RRGGBBAA</c>（照上游 <c>colorFromRgbaString</c> /
/// <c>colorToRgbaString</c>）。解析不了就当白色不透明。
/// </summary>
internal static class CollageColor
{
    public static Color Parse(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 9 && value[0] == '#' &&
            uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out uint rgba))
        {
            return Color.FromArgb((byte)(rgba & 0xFF), (byte)((rgba >> 24) & 0xFF),
                (byte)((rgba >> 16) & 0xFF), (byte)((rgba >> 8) & 0xFF));
        }

        return Colors.White;
    }

    public static string Format(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";

    /// <summary>界面上给人看的 <c>#RRGGBB</c>（不带透明度）。</summary>
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>解析 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>，失败返回 null。</summary>
    public static Color? TryParseHex(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 3)
        {
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        }

        if (value.Length == 6 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF));
        }

        if (value.Length == 8 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            return Color.FromArgb((byte)((argb >> 24) & 0xFF), (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF), (byte)(argb & 0xFF));
        }

        return null;
    }
}
