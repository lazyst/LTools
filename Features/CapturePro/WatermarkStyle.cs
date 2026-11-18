using System;
using System.Linq;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 水印的一整套画法（上游 <c>SnowCanvasWatermarkConfig</c>）。
/// 默认值、范围与**格距公式**都只在这里，渲染与自检共用。
/// </summary>
internal sealed record WatermarkSpec(
    string Text,
    Color Color,
    double FontSize,
    string FontFamily,
    double Angle,
    double Gap,
    double Opacity)
{
    public const double MinFontSize = 6;
    public const double MaxFontSize = 256;
    public const double MinGap = 10;
    public const double MaxGap = 200;
    public const double MinAngle = -90;
    public const double MaxAngle = 90;

    /// <summary>上游 <c>screenshotCanvasStyleDefaults().watermark</c>。</summary>
    public static WatermarkSpec Default { get; } = new(
        string.Empty, Colors.Black, 16, string.Empty, 30, 56, 0.16);

    public WatermarkSpec Clamped() => this with
    {
        FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, MinFontSize, MaxFontSize) : 16,
        Angle = double.IsFinite(Angle) ? Math.Clamp(Angle, MinAngle, MaxAngle) : 30,
        Gap = double.IsFinite(Gap) ? Math.Clamp(Gap, MinGap, MaxGap) : 56,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, 0, 1) : 0.16,
        FontFamily = FontFamily?.Trim() ?? string.Empty,
    };

    /// <summary>
    /// 平铺格距：**上游实际用的是 `max(gap + fontSize × 4, 32)`**（`paint_watermark`），
    /// 不是配置里那个 gap 本身——照抄 gap 会让水印挤成一片。
    /// </summary>
    public double Step => Math.Max(Gap + FontSize * 4, 32);

    /// <summary>每个印记的排版高度（行高 1.2 字号）。</summary>
    public double LineHeight => FontSize * 1.2;
}

/// <summary>一个水印模板（上游 <c>WatermarkTemplate</c>：名字 + 内容）。</summary>
internal sealed record WatermarkTemplate(string Name, string Value);

/// <summary>
/// 水印模板文本的展开（照上游 <c>resolve_watermark_template</c>）：
/// <c>{text}</c> 换成当前水印文字，<c>{YYYY}</c> / <c>{MM}</c> / <c>{DD}</c> /
/// <c>{HH}</c> / <c>{mm}</c> / <c>{ss}</c> 换成套用模板那一刻的时间；
/// 认不出的占位（含嵌套花括号）原样保留。纯函数，自检直接断言。
/// </summary>
internal static class WatermarkTemplateText
{
    private static readonly string[] TimestampTokens = { "YYYY", "MM", "DD", "HH", "mm", "ss" };

    /// <summary>模板里的占位符提示（参数行 template 下拉与 tooltip 共用）。</summary>
    public const string Hint = "可用 {text} 与 {YYYY} {MM} {DD} {HH} {mm} {ss}";

    public static string Resolve(string? template, string text, DateTime now)
    {
        if (string.IsNullOrEmpty(template)) return text ?? string.Empty;

        var sb = new System.Text.StringBuilder(template.Length + 16);
        int cursor = 0;
        while (true)
        {
            int relativeOpen = template.IndexOf('{', cursor);
            if (relativeOpen < 0)
            {
                sb.Append(template, cursor, template.Length - cursor);
                break;
            }

            int open = relativeOpen;
            sb.Append(template, cursor, open - cursor);

            int depth = 0, close = -1;
            bool nested = false;
            for (int i = open; i < template.Length; i++)
            {
                char ch = template[i];
                if (ch == '{')
                {
                    depth++;
                    nested |= depth > 1;
                }
                else if (ch == '}')
                {
                    if (depth == 0) continue;
                    depth--;
                    if (depth == 0)
                    {
                        close = i;
                        break;
                    }
                }
            }

            if (close < 0)
            {
                sb.Append(template, open, template.Length - open);
                break;
            }

            string placeholder = template[open..(close + 1)];
            if (nested)
            {
                sb.Append(placeholder);
            }
            else
            {
                string content = template[(open + 1)..close];
                if (content == "text") sb.Append(text ?? string.Empty);
                else if (ContainsTimestamp(content)) sb.Append(Expand(content, now));
                else sb.Append(placeholder);
            }
            cursor = close + 1;
        }
        return sb.ToString();
    }

    private static bool ContainsTimestamp(string value) =>
        TimestampTokens.Any(value.Contains);

    private static string Expand(string value, DateTime now) => value
        .Replace("YYYY", now.ToString("yyyy"))
        .Replace("MM", now.ToString("MM"))
        .Replace("DD", now.ToString("dd"))
        .Replace("HH", now.ToString("HH"))
        .Replace("mm", now.ToString("mm"))
        .Replace("ss", now.ToString("ss"));
}
