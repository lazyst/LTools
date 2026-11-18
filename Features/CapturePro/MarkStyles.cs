using System;
using System.Globalization;
using System.Text;

namespace LTools.Features.CapturePro;

/// <summary>序号徽标的形状。</summary>
internal enum SerialShape
{
    Circle,
    Square,
    Rounded,
}

/// <summary>序号的数字写法（对应 Snow Shot 的五种编号）。</summary>
internal enum SerialNumbering
{
    Arabic,
    RomanUpper,
    RomanLower,
    AlphaUpper,
    AlphaLower,
    Chinese,
}

/// <summary>矩形滤镜的类型（马赛克是单独的工具，不在这里）。</summary>
internal enum FilterKind
{
    Blur,
    Grayscale,
    Invert,
    Emboss,
    Brighten,

    /// <summary>
    /// 智能擦除：把这块当洞，从周围完好的像素重建（对应上游 <c>FilterType.SmartErase</c>）。
    /// 算法在 <see cref="SmartErase"/>。
    /// </summary>
    SmartErase,
}

/// <summary>
/// 序号文案：**只做纯文本换算**，绘制与自检共用一份（别在渲染里另写一套 switch）。
/// 换算超范围（罗马数字 &gt; 3999、中文 &gt; 9999）时退回阿拉伯数字，不抛错、不留空。
/// </summary>
internal static class SerialLabel
{
    private static readonly string[] ChineseDigits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
    private static readonly string[] ChineseUnits = { "", "十", "百", "千" };

    private static readonly (int Value, string Symbol)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
        (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    /// <summary>把序号换成界面上的写法。</summary>
    public static string Of(int number, SerialNumbering numbering) => numbering switch
    {
        SerialNumbering.RomanUpper => Roman(number).ToUpperInvariant(),
        SerialNumbering.RomanLower => Roman(number).ToLowerInvariant(),
        SerialNumbering.AlphaUpper => Alpha(number),
        SerialNumbering.AlphaLower => Alpha(number).ToLowerInvariant(),
        SerialNumbering.Chinese => Chinese(number),
        _ => number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>参数行上的短标签（一个字符以内，别把参数条撑开）。</summary>
    public static string Chip(SerialNumbering numbering) => numbering switch
    {
        SerialNumbering.RomanUpper => "I",
        SerialNumbering.RomanLower => "i",
        SerialNumbering.AlphaUpper => "A",
        SerialNumbering.AlphaLower => "a",
        SerialNumbering.Chinese => "一",
        _ => "1",
    };

    /// <summary>形状的中文名（tooltip 用）。</summary>
    public static string Name(SerialShape shape) => shape switch
    {
        SerialShape.Square => "方",
        SerialShape.Rounded => "圆角方",
        _ => "圆",
    };

    /// <summary>滤镜的中文名（参数行 chip 与 tooltip 共用）。</summary>
    public static string Name(FilterKind kind) => kind switch
    {
        FilterKind.Grayscale => "灰度",
        FilterKind.Invert => "反相",
        FilterKind.Emboss => "浮雕",
        FilterKind.Brighten => "亮度",
        FilterKind.SmartErase => "智能",
        _ => "模糊",
    };

    private static string Roman(int number)
    {
        if (number <= 0 || number > 3999) return number.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>表格列名式字母序号：1 → A、26 → Z、27 → AA。</summary>
    private static string Alpha(int number)
    {
        if (number <= 0) return number.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        int rest = number;
        while (rest > 0)
        {
            rest--;
            sb.Insert(0, (char)('A' + rest % 26));
            rest /= 26;
        }
        return sb.ToString();
    }

    private static string Chinese(int number)
    {
        if (number <= 0 || number > 9999) return number.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        bool pendingZero = false;
        for (int unit = 3; unit >= 0; unit--)
        {
            int digit = number / (int)Math.Pow(10, unit) % 10;
            if (digit == 0)
            {
                if (sb.Length > 0) pendingZero = true;
                continue;
            }

            if (pendingZero)
            {
                sb.Append(ChineseDigits[0]);
                pendingZero = false;
            }

            // 10–19 读「十」而不是「一十」
            if (!(digit == 1 && unit == 1 && sb.Length == 0)) sb.Append(ChineseDigits[digit]);
            sb.Append(ChineseUnits[unit]);
        }

        return sb.Length == 0 ? number.ToString(CultureInfo.InvariantCulture) : sb.ToString();
    }
}
