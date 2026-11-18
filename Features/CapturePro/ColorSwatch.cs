using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一张颜色取样板要显示 / 复制的东西：色值本身 + 三行文案（显示用与复制用各一套）。
/// </summary>
/// <remarks>
/// 照上游 <c>PinnedColorSwatchValues</c>：<see cref="Display"/> 是卡片上那三行字
/// （<c>#7DD353</c> / <c>125, 211, 83</c> / <c>100.3, 59.3%, 57.6%</c>），
/// <see cref="Clipboard"/> 是点复制按钮时进剪贴板的**规范 CSS**（<c>rgb(125, 211, 83)</c> 这种）。
/// 半透明时显示行会补上 alpha，标签也换成 <c>RGBA:</c> / <c>HSLA:</c>。
/// </remarks>
internal sealed class ColorSwatchValues
{
    /// <summary>解析出来的颜色（8 bit 通道，<c>A</c> 也在这份里）。</summary>
    public Color Color { get; init; }

    /// <summary>不透明（alpha = 255）。</summary>
    public bool Opaque { get; init; }

    /// <summary>卡片上显示的三行值（不含标签）。</summary>
    public string[] Display { get; init; } = new string[3];

    /// <summary>三行「复制」进剪贴板的规范 CSS。</summary>
    public string[] Clipboard { get; init; } = new string[3];

    /// <summary>三行标签（<c>HEX:</c> / <c>RGB:</c> / <c>HSL:</c>，半透明时后两个是 <c>RGBA:</c> / <c>HSLA:</c>）。</summary>
    public string[] Labels { get; init; } = new string[3];

    /// <summary>alpha 的文本形式（<c>0.533</c>），复制按钮的提示与半透明值共用。</summary>
    public string AlphaText { get; init; } = "";
}

/// <summary>
/// 颜色取样板：把剪贴板里的一段颜色文本认成色值，并算出上游那三行文案。
/// </summary>
/// <remarks>
/// <para>
/// 逐条照 <c>src/presentation/services/pinnedcolorswatch.cpp</c> 的 <c>parse()</c> /
/// <c>parsePinnedColorSwatch()</c> 移植——认的形状、拒绝的形状、百分号混用规则、
/// alpha 的两种写法、HSL 的角度环绕都在这里，纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// <para>
/// 认得的写法：<c>#rgb</c> / <c>#rgba</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c>（<c>#</c> 可省，
/// 只省 <c>#</c> 时必须是 6 / 8 位）、<c>rgb()</c> / <c>rgba()</c> / <c>hsl()</c> / <c>hsla()</c>
/// 的逗号与空格斜杠两种写法；通道可以是百分比，alpha 可以是 <c>0–1</c> 或 <c>0–100%</c>。
/// 其余一律返回 <c>null</c>——那就还是普通文字，不冒充色卡。
/// </para>
/// </remarks>
internal static class ColorSwatch
{
    /// <summary>紧凑卡片（默认）。</summary>
    public const int CompactWidth = 342;
    public const int CompactHeight = 210;

    /// <summary>旧版卡片（历史记录里存过 470×288，按原尺寸重画）。</summary>
    public const int WideWidth = 470;
    public const int WideHeight = 288;

    /// <summary>卡片里的字号（两种尺寸都不缩放）。</summary>
    public const double FontSize = 16;

    /// <summary>第一行的标签。</summary>
    public const string ColorLabel = "颜色：";

    /// <summary>三个复制按钮的提示（照上游 <c>PinnedColorSwatch</c> 的中文文案）。</summary>
    public static readonly string[] CopyLabels = { "复制 HEX 颜色值", "复制 RGB 颜色值", "复制 HSL 颜色值" };

    private const string Nr = @"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)";

    private static readonly Regex NumberSyntax = new("^" + Nr + "$", RegexOptions.CultureInvariant);

    private static readonly Regex HexPattern = new(
        "^(?:#([0-9a-f]{3}|[0-9a-f]{4}|[0-9a-f]{6}|[0-9a-f]{8})|([0-9a-f]{6}|[0-9a-f]{8}))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FunctionPattern = new(
        @"^(rgb|rgba|hsl|hsla)\(\s*([^()]*)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>紧凑卡片的缩放（上游 16/22；旧版卡片是 1.0）。</summary>
    public static double ScaleFor(int cardWidth) => cardWidth == WideWidth ? 1.0 : 16.0 / 22.0;

    /// <summary>某张卡片该用多大（认不出的宽度按紧凑卡片）。</summary>
    public static (int Width, int Height) SizeOf(int cardWidth) =>
        cardWidth == WideWidth ? (WideWidth, WideHeight) : (CompactWidth, CompactHeight);

    /// <summary>把一段文本认成色值；认不出返回 <c>null</c>（那就还是普通文字）。</summary>
    public static ColorSwatchValues? Parse(string? raw)
    {
        Color? color = ParseColor(raw);
        if (color == null) return null;

        Color value = color.Value;
        bool opaque = value.A == 255;
        string alpha = FormatAlpha(value.A);

        var (hue, saturation, lightness) = RgbToHsl(value);
        string hueText = hue.ToString("0.0", CultureInfo.InvariantCulture);
        string saturationText = (saturation * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string lightnessText = (lightness * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        string hex = HexText(value, opaque);
        string rgb = string.Create(CultureInfo.InvariantCulture, $"{value.R}, {value.G}, {value.B}");
        string hsl = string.Join(", ", hueText, saturationText, lightnessText);

        return new ColorSwatchValues
        {
            Color = value,
            Opaque = opaque,
            AlphaText = alpha,
            Display = new[]
            {
                hex,
                opaque ? rgb : rgb + ", " + alpha,
                opaque ? hsl : hsl + ", " + alpha,
            },
            Clipboard = new[]
            {
                hex,
                opaque
                    ? string.Create(CultureInfo.InvariantCulture, $"rgb({rgb})")
                    : string.Create(CultureInfo.InvariantCulture, $"rgba({rgb}, {alpha})"),
                opaque
                    ? $"hsl({hueText}, {saturationText}, {lightnessText})"
                    : $"hsla({hueText}, {saturationText}, {lightnessText}, {alpha})",
            },
            Labels = opaque
                ? new[] { "HEX:", "RGB:", "HSL:" }
                : new[] { "HEX:", "RGBA:", "HSLA:" },
        };
    }

    /// <summary>只认色值本身（卡片之外的地方要问「这段文字是不是颜色」时用它）。</summary>
    public static bool LooksLikeColor(string? text) => ParseColor(text) != null;

    // ============ 解析 ============

    private static Color? ParseColor(string? raw)
    {
        if (raw == null) return null;
        string text = raw.Trim();
        if (text.Length == 0 || text.Length > 256) return null;

        var hexMatch = HexPattern.Match(text);
        if (hexMatch.Success)
        {
            string hex = hexMatch.Groups[1].Success ? hexMatch.Groups[1].Value : hexMatch.Groups[2].Value;
            if (hex.Length < 5)
            {
                var expanded = new System.Text.StringBuilder();
                foreach (char c in hex) expanded.Append(c).Append(c);
                hex = expanded.ToString();
            }

            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            int a = hex.Length == 8 ? Convert.ToInt32(hex.Substring(6, 2), 16) : 255;
            return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
        }

        var match = FunctionPattern.Match(text);
        if (!match.Success) return null;

        string function = match.Groups[1].Value.ToLowerInvariant();
        string body = match.Groups[2].Value.Trim();
        bool comma = body.Contains(',');

        var components = new List<string>();
        string alpha = "";
        if (comma)
        {
            if (body.Contains('/')) return null;
            foreach (string part in body.Split(',')) components.Add(part.Trim());
            if (components.Count == 4)
            {
                alpha = components[^1];
                components.RemoveAt(components.Count - 1);
                if (alpha.Length == 0) return null;
            }
            if (components.Count != 3 || (function.EndsWith('a') && alpha.Length == 0)) return null;
        }
        else
        {
            string[] parts = body.Split('/');
            if (parts.Length > 2) return null;
            foreach (string part in Whitespace.Split(parts[0].Trim()))
                if (part.Length > 0) components.Add(part);
            if (parts.Length == 2)
            {
                alpha = parts[1].Trim();
                if (alpha.Length == 0) return null;
            }
            if (components.Count != 3) return null;
        }

        double opacity = 1;
        if (alpha.Length > 0)
        {
            bool percent = alpha.EndsWith('%');
            double? parsed = Number(alpha, percent ? 100 : 1, percent);
            if (parsed == null) return null;
            opacity = parsed.Value / (percent ? 100 : 1);
        }

        int red, green, blue;
        if (function.StartsWith("rgb", StringComparison.Ordinal))
        {
            var channels = new int[3];
            for (int i = 0; i < 3; i++)
            {
                bool percent = components[i].EndsWith('%');
                if (comma && percent != components[0].EndsWith('%')) return null;
                double? parsed = Number(components[i], percent ? 100 : 255, percent);
                if (parsed == null) return null;
                channels[i] = RoundHalfUp(percent ? parsed.Value * 255 / 100 : parsed.Value);
            }
            red = channels[0];
            green = channels[1];
            blue = channels[2];
        }
        else
        {
            string hueText = components[0].ToLowerInvariant();
            if (hueText.EndsWith("deg", StringComparison.Ordinal)) hueText = hueText[..^3];
            double? hue = Number(hueText, 1e9, false, -1e9);
            double? saturation = Number(components[1], 100, true);
            double? lightness = Number(components[2], 100, true);
            if (hue == null || saturation == null || lightness == null) return null;

            double degrees = hue.Value % 360;
            if (degrees < 0) degrees += 360;
            (red, green, blue) = HslToRgb(degrees / 360, saturation.Value / 100, lightness.Value / 100);
        }

        return Color.FromArgb((byte)Math.Clamp(RoundHalfUp(opacity * 255), 0, 255),
            (byte)red, (byte)green, (byte)blue);
    }

    /// <summary>
    /// 上游 <c>number()</c>：可带 <c>%</c>，只认普通小数写法（<c>1.</c> / <c>.5</c> / <c>+1</c> / <c>-1</c>），
    /// 并且必须落在 <c>[minimum, maximum]</c>。
    /// </summary>
    private static double? Number(string text, double maximum, bool percentage = false, double minimum = 0)
    {
        string value = text;
        if (percentage)
        {
            if (!value.EndsWith('%')) return null;
            value = value[..^1];
        }
        if (!NumberSyntax.IsMatch(value)) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return null;
        if (double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < minimum || parsed > maximum)
            return null;
        return parsed;
    }

    // ============ 格式化 ============

    private static string HexText(Color color, bool opaque)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        return opaque ? text : text + color.A.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// alpha 的文本形式：<c>0.533</c>（三位小数、去掉尾巴上的 0 与小数点）。
    /// 上游是从 8 bit 反算的，所以 <c>#FF000088</c> 复制出来仍是 <c>0.533</c>。
    /// </summary>
    private static string FormatAlpha(byte alpha)
    {
        string text = (alpha / 255.0).ToString("0.000", CultureInfo.InvariantCulture);
        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }

    private static (double Hue, double Saturation, double Lightness) RgbToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2;
        if (max.Equals(min)) return (0, 0, lightness);

        double delta = max - min;
        double saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue = max.Equals(r)
            ? (g - b) / delta + (g < b ? 6 : 0)
            : max.Equals(g)
                ? (b - r) / delta + 2
                : (r - g) / delta + 4;
        hue /= 6;
        return (Math.Max(0, hue * 360), saturation, lightness);
    }

    private static (int Red, int Green, int Blue) HslToRgb(double hue, double saturation, double lightness)
    {
        if (saturation <= 0)
        {
            int gray = Math.Clamp(RoundHalfUp(lightness * 255), 0, 255);
            return (gray, gray, gray);
        }

        double q = lightness < 0.5 ? lightness * (1 + saturation) : lightness + saturation - lightness * saturation;
        double p = 2 * lightness - q;

        static double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        return (Math.Clamp(RoundHalfUp(Channel(p, q, hue + 1.0 / 3) * 255), 0, 255),
            Math.Clamp(RoundHalfUp(Channel(p, q, hue) * 255), 0, 255),
            Math.Clamp(RoundHalfUp(Channel(p, q, hue - 1.0 / 3) * 255), 0, 255));
    }

    /// <summary>四舍五入（<c>Math.Round</c> 是银行家舍入，会把 127.5 变成 127，与上游不一致）。</summary>
    private static int RoundHalfUp(double value) => (int)Math.Floor(value + 0.5);
}
