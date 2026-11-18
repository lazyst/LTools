using System;
using System.Globalization;
using System.Text;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 序号徽标类型（上游 <c>SnowCanvasSerialNumberType</c>）：空心 / 实心 × 圆 / 方，再加一个
/// 只画圆圈、不写数字的 <see cref="Circle"/>。
/// </summary>
internal enum SerialBadge
{
    OutlinedCircle,
    SolidCircle,
    OutlinedSquare,
    SolidSquare,
    Circle,
}

/// <summary>
/// 数字写法（上游 <c>SerialNumberNumericType</c>）：阿拉伯 / 罗马 / 小写字母 / 大写字母 / 中文。
/// </summary>
internal enum SerialNumbering
{
    Arabic,
    Roman,
    LowercaseLetters,
    UppercaseLetters,
    Chinese,
}

/// <summary>
/// 序号的一整套画法（上游 <c>SnowCanvasSerialNumberStyle</c>）。
/// **不可变**：改样式返回新实例；数值换算与默认值只在这一处。
/// </summary>
internal sealed record SerialSpec(
    SerialBadge Badge,
    SerialNumbering Numbering,
    Color Stroke,
    Color Fill,
    FillKind FillStyle,
    double FontSize,
    string FontFamily,
    double StrokeWidth,
    double Opacity)
{
    public const double MinFontSize = 6;
    public const double MaxFontSize = 256;
    public const double MaxStrokeWidth = 72;

    public static readonly Color DefaultStroke = Color.FromRgb(0xF5, 0x22, 0x2D);
    public static readonly Color DefaultFill = Color.FromRgb(0xFF, 0xCC, 0xC7);

    /// <summary>上游 <c>screenshotCanvasStyleDefaults().serialNumber</c>。</summary>
    public static SerialSpec Default { get; } = new(
        SerialBadge.OutlinedCircle, SerialNumbering.Arabic, DefaultStroke, DefaultFill, FillKind.None,
        24, string.Empty, 2, 1);

    public SerialSpec Clamped() => this with
    {
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, MinFontSize, MaxFontSize) : 24,
        StrokeWidth = double.IsFinite(StrokeWidth) ? Math.Clamp(StrokeWidth, 0, MaxStrokeWidth) : 2,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 1,
        FontFamily = FontFamily?.Trim() ?? string.Empty,
    };

    /// <summary>实心徽标：底色就是描边色，字取对比色。</summary>
    public bool Solid => Badge is SerialBadge.SolidCircle or SerialBadge.SolidSquare;

    /// <summary>方形徽标（空心方 / 实心方）。</summary>
    public bool Square => Badge is SerialBadge.OutlinedSquare or SerialBadge.SolidSquare;

    /// <summary>这种徽标写不写数字（上游 <c>supports_number()</c>：只有纯圆的 Circle 不写）。</summary>
    public bool Numbered => Badge != SerialBadge.Circle;

    /// <summary>
    /// 徽标直径 = 1.68 × 字号。上游 <c>resolve_serial_number_badge_diameter</c>：
    /// 行高 1.2 字号 + 上下各 0.2 行高，按 16 号基准等比缩放。
    /// **与写法和数字位数无关**（罗马 / 中文再长也不换尺寸）。
    /// </summary>
    public double Diameter => Math.Max(1, FontSize * 1.68);

    /// <summary>方形徽标的圆角 = 0.2 × 字号（上游 <c>SERIAL_NUMBER_SQUARE_CORNER_RADIUS_PER_FONT_SIZE</c>）。</summary>
    public double CornerRadius => FontSize * 0.2;

    /// <summary>实心徽标上的字色；空心徽标用描边色。</summary>
    public Color TextColor => Solid ? MarkRenderer.Contrast(Stroke) : Stroke;
}

/// <summary>
/// 序号文案：**只做纯文本换算**，绘制与自检共用一份（别在渲染里另写一套 switch）。
/// 逐条照上游 <c>serial_number_format.rs</c>：罗马 1–3999 超范围退回阿拉伯；
/// 字母 a..z → aa..；中文带 万 / 亿 / 兆 / 京 分组、10 写作「十」。
/// </summary>
internal static class SerialLabel
{
    private static readonly string[] ChineseDigits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
    private static readonly string[] ChineseGroupUnits = { "", "万", "亿", "兆", "京" };

    private static readonly (int Value, string Symbol)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    /// <summary>把序号换成界面上的写法。</summary>
    public static string Of(int number, SerialNumbering numbering) => numbering switch
    {
        SerialNumbering.Roman => Roman(number),
        SerialNumbering.LowercaseLetters => Alpha(number, upper: false),
        SerialNumbering.UppercaseLetters => Alpha(number, upper: true),
        SerialNumbering.Chinese => Chinese(number),
        _ => Math.Max(0, number).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>参数行上的短标签（一个字符以内，别把参数条撑开）。</summary>
    public static string Chip(SerialNumbering numbering) => numbering switch
    {
        SerialNumbering.Roman => "I",
        SerialNumbering.LowercaseLetters => "a",
        SerialNumbering.UppercaseLetters => "A",
        SerialNumbering.Chinese => "一",
        _ => "1",
    };

    public static string Name(SerialNumbering numbering) => numbering switch
    {
        SerialNumbering.Roman => "罗马数字",
        SerialNumbering.LowercaseLetters => "小写字母",
        SerialNumbering.UppercaseLetters => "大写字母",
        SerialNumbering.Chinese => "中文数字",
        _ => "阿拉伯数字",
    };

    /// <summary>徽标类型的中文名（参数行 chip 与 tooltip 共用）。</summary>
    public static string Chip(SerialBadge badge) => badge switch
    {
        SerialBadge.SolidCircle => "实心圆",
        SerialBadge.OutlinedSquare => "空心方",
        SerialBadge.SolidSquare => "实心方",
        SerialBadge.Circle => "圆圈",
        _ => "空心圆",
    };

    public static string Name(SerialBadge badge) => "徽标：" + Chip(badge);

    public static string Name(FilterKind kind) => kind switch
    {
        FilterKind.Mosaic => "马赛克",
        FilterKind.Grayscale => "灰度",
        FilterKind.Invert => "反相",
        FilterKind.Emboss => "浮雕",
        FilterKind.Brighten => "亮度",
        FilterKind.SmartErase => "智能",
        _ => "模糊",
    };

    private static string Roman(int number)
    {
        if (number <= 0 || number > 3999) return Math.Max(0, number).ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        int rest = number;
        foreach (var (value, symbol) in RomanTable)
        {
            while (rest >= value)
            {
                sb.Append(symbol);
                rest -= value;
            }
        }
        return sb.ToString();
    }

    /// <summary>表格列名式字母序号：1 → a、26 → z、27 → aa（上游 <c>LowercaseLetters</c> / <c>UppercaseLetters</c>）。</summary>
    private static string Alpha(int number, bool upper)
    {
        if (number <= 0) return Math.Max(0, number).ToString(CultureInfo.InvariantCulture);

        char baseChar = upper ? 'A' : 'a';
        var sb = new StringBuilder();
        int rest = number;
        while (rest > 0)
        {
            rest--;
            sb.Insert(0, (char)(baseChar + rest % 26));
            rest /= 26;
        }
        return sb.ToString();
    }

    /// <summary>中文数字（照上游 <c>chinese_number</c>：四位一组，组间按需补零）。</summary>
    private static string Chinese(int number)
    {
        if (number <= 0) return "零";

        var groups = new int[ChineseGroupUnits.Length];
        int rest = number;
        for (int i = 0; i < groups.Length; i++)
        {
            groups[i] = rest % 10_000;
            rest /= 10_000;
        }

        var sb = new StringBuilder();
        bool gap = false;
        for (int index = groups.Length - 1; index >= 0; index--)
        {
            int group = groups[index];
            if (group == 0)
            {
                if (sb.Length > 0) gap = true;
                continue;
            }
            if (sb.Length > 0 && (gap || group < 1000)) sb.Append(ChineseDigits[0]);
            gap = false;

            bool groupStarted = false, zeroPending = false;
            foreach (var (place, unit) in new[] { (1000, "千"), (100, "百"), (10, "十"), (1, "") })
            {
                int digit = group / place % 10;
                if (digit == 0)
                {
                    if (groupStarted) zeroPending = true;
                    continue;
                }
                if (zeroPending) sb.Append(ChineseDigits[0]);
                sb.Append(ChineseDigits[digit]);
                sb.Append(unit);
                groupStarted = true;
                zeroPending = false;
            }
            sb.Append(ChineseGroupUnits[index]);
        }

        // 只省最高位的「一十」，后面组里的「一十」照写（一万零一十）
        if (sb.Length >= 2 && sb[0] == '一' && sb[1] == '十') sb.Remove(0, 1);
        return sb.ToString();
    }
}
