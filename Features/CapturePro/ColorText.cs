using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>色值写法（光标旁取色卡的格式轮换与取色弹窗的格式下拉共用一份）。</summary>
internal enum ColorFormatKind
{
    /// <summary><c>#RRGGBB</c>（半透明时 <c>#RRGGBBAA</c>）。</summary>
    Hex,

    /// <summary><c>RRGGBB</c>（不带井号）。</summary>
    HexWithoutHash,

    /// <summary><c>rgb(r, g, b)</c> / <c>rgba(r, g, b, a)</c>。</summary>
    Rgb,

    /// <summary><c>hsl(h, s%, l%)</c> / <c>hsla(…, a)</c>。</summary>
    Hsl,

    /// <summary><c>hsb(h, s%, b%)</c> / <c>hsb(…, a)</c>（上游取色弹窗的第三种写法，HSB = HSV）。</summary>
    Hsb,
}

/// <summary>
/// 色值的写法、解析与 HSB 换算——纯函数，<c>--selftest</c> 直接断言。
/// </summary>
/// <remarks>
/// <para>
/// 取色卡（<see cref="PickerColor.Format"/>）与取色弹窗（<c>CaptureColorDialog</c>）都用这一份：
/// 上游取色卡的 4 种写法（hex / 无井号 / rgb / hsl）在这里，加上取色弹窗的 <c>hsb()</c>。
/// </para>
/// <para>
/// 输出的形状照上游 <c>ColorPickerValueModel::colorToString</c>：alpha 一律写成
/// <c>0.533</c> 这样的分数（三位小数、去掉尾巴的 0），HSL 的饱和 / 亮度带 <c>%</c> 且保留一位小数，
/// HSB 的 h / s / b 取整；HEX 大写（与项目里其它色值一致）。
/// </para>
/// <para>
/// 解析额外认 <c>hsb()</c> / <c>hsv()</c>，其余写法（<c>#rgb</c> / <c>#rgba</c> / <c>#rrggbbaa</c> /
/// <c>rgb()</c> / <c>hsl()</c> 的逗号与空格斜杠两种写法、百分比通道）复用
/// <see cref="ColorSwatch.TryParseColor"/>，别再写第二套。
/// </para>
/// </remarks>
internal static class ColorText
{
    private const string Nr = @"[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)";

    private static readonly Regex NumberSyntax = new("^" + Nr + "$", RegexOptions.CultureInvariant);

    private static readonly Regex HsbPattern = new(@"^(hsb|hsv)\(\s*([^()]*)\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>配置里的写法键 → 枚举（认不出按 HEX）。</summary>
    public static ColorFormatKind KindOf(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "hex_without_hash" => ColorFormatKind.HexWithoutHash,
        "rgb" => ColorFormatKind.Rgb,
        "hsl" => ColorFormatKind.Hsl,
        "hsb" => ColorFormatKind.Hsb,
        _ => ColorFormatKind.Hex,
    };

    /// <summary>枚举 → 配置里的写法键。</summary>
    public static string KeyOf(ColorFormatKind kind) => kind switch
    {
        ColorFormatKind.HexWithoutHash => "hex_without_hash",
        ColorFormatKind.Rgb => "rgb",
        ColorFormatKind.Hsl => "hsl",
        ColorFormatKind.Hsb => "hsb",
        _ => "hex",
    };

    /// <summary>轮换顺序：HEX → 无井号 → RGB → HSL → HSB → HEX（照上游的循环再加 HSB）。</summary>
    public static ColorFormatKind Next(ColorFormatKind kind) => kind switch
    {
        ColorFormatKind.Hex => ColorFormatKind.HexWithoutHash,
        ColorFormatKind.HexWithoutHash => ColorFormatKind.Rgb,
        ColorFormatKind.Rgb => ColorFormatKind.Hsl,
        ColorFormatKind.Hsl => ColorFormatKind.Hsb,
        _ => ColorFormatKind.Hex,
    };

    /// <summary>提示条里那句「长什么样」。</summary>
    public static string Hint(ColorFormatKind kind) => kind switch
    {
        ColorFormatKind.HexWithoutHash => "RRGGBB",
        ColorFormatKind.Rgb => "rgb(r, g, b)",
        ColorFormatKind.Hsl => "hsl(h, s%, l%)",
        ColorFormatKind.Hsb => "hsb(h, s%, b%)",
        _ => "#RRGGBB",
    };

    /// <summary>按格式出文本（<paramref name="withAlpha"/> = false 时永远不带 alpha）。</summary>
    public static string Format(Color color, ColorFormatKind kind, bool withAlpha = true)
    {
        bool opaque = !withAlpha || color.A == 255;

        switch (kind)
        {
            case ColorFormatKind.HexWithoutHash:
                return HexText(color, opaque)[1..];

            case ColorFormatKind.Rgb:
                return opaque
                    ? string.Create(CultureInfo.InvariantCulture, $"rgb({color.R}, {color.G}, {color.B})")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"rgba({color.R}, {color.G}, {color.B}, {AlphaFraction(color.A)})");

            case ColorFormatKind.Hsl:
            {
                var (hue, saturation, lightness) = PickerColor.ToHsl(color);
                return opaque
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"hsl({hue}, {saturation:0.0}%, {lightness:0.0}%)")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"hsla({hue}, {saturation:0.0}%, {lightness:0.0}%, {AlphaFraction(color.A)})");
            }

            case ColorFormatKind.Hsb:
            {
                var (hue, saturation, brightness) = ToHsb(color);
                int h = (int)Math.Round(hue, MidpointRounding.AwayFromZero) % 360;
                int s = (int)Math.Round(saturation, MidpointRounding.AwayFromZero);
                int b = (int)Math.Round(brightness, MidpointRounding.AwayFromZero);
                string hsb = string.Create(CultureInfo.InvariantCulture, $"hsb({h}, {s}%, {b}%)");
                return opaque ? hsb : $"hsb({h}, {s}%, {b}%, {AlphaFraction(color.A)})";
            }

            default:
                return HexText(color, opaque);
        }
    }

    /// <summary>按配置里的写法键出文本。</summary>
    public static string Format(Color color, string? key) => Format(color, KindOf(key));

    /// <summary>
    /// 认一段色值文本：<c>#rgb</c> / <c>#rgba</c> / <c>#rrggbb</c> / <c>#rrggbbaa</c>（井号可省）、
    /// <c>rgb()</c> / <c>rgba()</c> / <c>hsl()</c> / <c>hsla()</c>、<c>hsb()</c> / <c>hsv()</c>。
    /// </summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        var match = HsbPattern.Match(trimmed);
        if (match.Success)
        {
            if (!TryParseHsb(match.Groups[2].Value, out color)) return false;
            return true;
        }

        if (ColorSwatch.TryParseColor(trimmed, out color)) return true;

        color = default;
        return false;
    }

    /// <summary>RGB → HSB（H 0–360，S / B 0–100；照 Qt 的 <c>QColor::toHsv</c>）。</summary>
    public static (double Hue, double Saturation, double Brightness) ToHsb(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double brightness = max * 100.0;
        double saturation = max <= 0 ? 0 : delta / max * 100.0;

        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? (g - b) / delta + (g < b ? 6 : 0)
                : max == g ? (b - r) / delta + 2
                : (r - g) / delta + 4;
            hue *= 60;
        }

        return (hue, saturation, brightness);
    }

    /// <summary>HSB → RGB（H 0–360 可环绕，S / B 0–100）。</summary>
    public static Color FromHsb(double hue, double saturation, double brightness, byte alpha = 255)
    {
        double h = hue % 360;
        if (h < 0) h += 360;
        double s = Math.Clamp(saturation, 0, 100) / 100.0;
        double v = Math.Clamp(brightness, 0, 100) / 100.0;

        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(alpha,
            (byte)Math.Clamp(Math.Round((r + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((g + m) * 255), 0, 255),
            (byte)Math.Clamp(Math.Round((b + m) * 255), 0, 255));
    }

    // ============ 内部 ============

    private static string HexText(Color color, bool opaque)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"#{color.R:X2}{color.G:X2}{color.B:X2}");
        return opaque ? text : text + color.A.ToString("X2", CultureInfo.InvariantCulture);
    }

    /// <summary>alpha 的分数写法：<c>0.533</c>（三位小数、去掉尾巴的 0，照上游 <c>formatPercent</c>）。</summary>
    private static string AlphaFraction(byte alpha)
    {
        string text = (alpha / 255.0).ToString("0.000", CultureInfo.InvariantCulture);
        text = text.TrimEnd('0');
        return text.EndsWith('.') ? text[..^1] : text;
    }

    /// <summary><c>hsb(h, s%, b%)</c> / <c>hsb(h, s%, b%, a)</c> 的括号里那一段。</summary>
    private static bool TryParseHsb(string body, out Color color)
    {
        color = default;

        var parts = new List<string>();
        if (body.Contains(','))
        {
            foreach (string part in body.Split(',')) parts.Add(part.Trim());
        }
        else
        {
            string[] slash = body.Split('/');
            if (slash.Length > 2) return false;
            foreach (string part in Whitespace.Split(slash[0].Trim()))
                if (part.Length > 0) parts.Add(part);
            if (slash.Length == 2) parts.Add(slash[1].Trim());
        }

        if (parts.Count is not (3 or 4)) return false;

        double? hue = Number(parts[0], 1e9, false, -1e9);
        double? saturation = Number(parts[1], 100, true);
        double? brightness = Number(parts[2], 100, true);
        if (hue == null || saturation == null || brightness == null) return false;

        byte alpha = 255;
        if (parts.Count == 4)
        {
            string text = parts[3];
            bool percent = text.EndsWith('%');
            double? parsed = Number(text, percent ? 100 : 1, percent);
            if (parsed == null) return false;
            alpha = (byte)Math.Clamp(Math.Floor(parsed.Value / (percent ? 100 : 1) * 255 + 0.5),
                0, 255);
        }

        color = FromHsb(hue.Value, saturation.Value, brightness.Value, alpha);
        return true;
    }

    private static double? Number(string text, double maximum, bool percentage, double minimum = 0)
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
}
