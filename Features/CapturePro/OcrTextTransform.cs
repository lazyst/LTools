using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>识别文字的「排版」选项（照上游 <c>ocrFormattingOptions</c>）。</summary>
internal static class OcrFormatting
{
    /// <summary>不做排版（原样保留 OCR 的行）。</summary>
    public const string None = "none";

    /// <summary>保留换行（与「不做排版」等价，但显式写在工具栏上）。</summary>
    public const string Keep = "keep";

    /// <summary>去掉所有换行，把整段接成一行。</summary>
    public const string Remove = "remove";

    /// <summary>智能排版：按版面把断行的句子接回段落。</summary>
    public const string Smart = "smart";

    /// <summary>下拉顺序（照上游）。</summary>
    public static readonly IReadOnlyList<string> Keys = new[] { None, Keep, Remove, Smart };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return None;
        string trimmed = value.Trim();
        foreach (string key in Keys)
            if (string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase)) return key;

        return None;
    }

    public static string LabelOf(string? value) => Normalize(value) switch
    {
        Keep => "保留换行",
        Remove => "去掉换行",
        Smart => "智能排版",
        _ => "不排版",
    };

    public static string Describe(string? value) => Normalize(value) switch
    {
        Keep => "原样保留 OCR 的行，不合并",
        Remove => "把所有行接成一行（适合再贴进别的编辑器）",
        Smart => "按版面把断行的句子接回段落（按行距与字号差不合并）",
        _ => "原样显示识别结果",
    };
}

/// <summary>识别文字的「标点」选项（照上游 <c>ocrPunctuationOptions</c>）。</summary>
internal static class OcrPunctuation
{
    public const string None = "none";
    public const string Half = "half";
    public const string Full = "full";

    /// <summary>下拉顺序（照上游）。</summary>
    public static readonly IReadOnlyList<string> Keys = new[] { None, Half, Full };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return None;
        string trimmed = value.Trim();
        foreach (string key in Keys)
            if (string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase)) return key;

        return None;
    }

    public static string LabelOf(string? value) => Normalize(value) switch
    {
        Half => "半角标点",
        Full => "全角标点",
        _ => "不转换",
    };

    public static string Describe(string? value) => Normalize(value) switch
    {
        Half => "把全角标点换成半角（，→ ,）",
        Full => "把半角标点换成全角（, → ，）",
        _ => "标点保持识别结果",
    };
}

/// <summary>一次排版转换的结果：新的行文本 + 新的文字层（行数可能与原层不同）。</summary>
internal sealed record OcrTransformResult(IReadOnlyList<string> Lines, IReadOnlyList<OcrTextLine> Layer);

/// <summary>
/// 识别文字的排版与标点转换（照上游 <c>screenshotocrtexttransform.cpp</c> + <c>screenshotocrtexteditingsession</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游的语义是**整段重排**：第一次选择非「无」的选项时记一份基线文本，之后每次变换都从基线重算
/// （所以「半角 → 全角 → 半角」不会叠加出错）；两个选项都回到「无」= 恢复基线（这会丢掉变换期间的编辑，
/// 上游也是这个行为）。<c>smart</c> 用版面聚出来的段落文本。
/// </para>
/// <para>
/// 与上游的差别在**落点**：上游是把新文本塞回 <c>QTextDocument</c> 再重新布局；本项目的文字层是
/// 「模型行 ↔ OCR 行框」一一对应的，所以这里同时给出**合并后的行框**（段落取成员行的并集），
/// 界面据此重建文字层，文字才画在正确的位置上。
/// </para>
/// </remarks>
internal static class OcrTextTransform
{
    private const string Ascii = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";

    private const string Wide = "！＂＃＄％＆＇（）＊＋，－．／：；＜＝＞？＠［＼］＾＿｀｛｜｝～";

    /// <summary>去掉所有换行（照上游 <c>removeOcrLineBreaks</c>：只删 <c>\n</c>）。</summary>
    public static string RemoveLineBreaks(string text) =>
        (text ?? "").Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    /// <summary>半角 ↔ 全角标点（照上游 <c>convertOcrPunctuation</c> 的 32 个字符表）。</summary>
    public static string ConvertPunctuation(string text, bool fullWidth)
    {
        string source = text ?? "";
        var sb = new StringBuilder(source.Length);
        foreach (char c in source)
        {
            if (fullWidth)
            {
                int index = Ascii.IndexOf(c, StringComparison.Ordinal);
                if (index >= 0)
                {
                    sb.Append(Wide[index]);
                    continue;
                }
            }
            else
            {
                int index = Wide.IndexOf(c, StringComparison.Ordinal);
                if (index >= 0)
                {
                    sb.Append(Ascii[index]);
                    continue;
                }
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>只做标点转换（排版另外算）。</summary>
    public static string ApplyPunctuation(string text, string punctuation) =>
        OcrPunctuation.Normalize(punctuation) switch
        {
            OcrPunctuation.Half => ConvertPunctuation(text, false),
            OcrPunctuation.Full => ConvertPunctuation(text, true),
            _ => text ?? "",
        };

    /// <summary>
    /// 从基线算一次变换。返回的新文字层行数可能与基线不同（<c>remove</c> 合成一行、<c>smart</c> 按段落合并）。
    /// </summary>
    public static OcrTransformResult Apply(IReadOnlyList<string> baselineTexts,
        IReadOnlyList<OcrTextLine> baselineLayer, string formatting, string punctuation)
    {
        string format = OcrFormatting.Normalize(formatting);
        string marks = OcrPunctuation.Normalize(punctuation);

        if (baselineLayer.Count == 0)
            return new OcrTransformResult(Array.Empty<string>(), Array.Empty<OcrTextLine>());

        List<Span> spans = format switch
        {
            OcrFormatting.Remove => new List<Span> { All(baselineTexts, baselineLayer) },
            OcrFormatting.Smart => Paragraphs(baselineTexts, baselineLayer),
            _ => Individual(baselineTexts, baselineLayer),
        };

        var texts = new List<string>(spans.Count);
        var layer = new List<OcrTextLine>(spans.Count);
        for (int i = 0; i < spans.Count; i++)
        {
            Span span = spans[i];
            string text = marks == OcrPunctuation.None ? span.Text : ApplyPunctuation(span.Text, marks);
            texts.Add(text);
            layer.Add(new OcrTextLine
            {
                Index = i,
                Text = text,
                Box = span.Box,
                TextColor = span.TextColor,
                HasTextColor = span.HasTextColor,
                BackgroundColor = span.BackgroundColor,
            });
        }

        return new OcrTransformResult(texts, layer);
    }

    /// <summary>智能排版只有它自己：把行接成段落的文本（自测与界面共用）。</summary>
    public static IReadOnlyList<string> SmartTexts(IReadOnlyList<string> baselineTexts,
        IReadOnlyList<OcrTextLine> baselineLayer)
    {
        if (baselineLayer.Count == 0) return Array.Empty<string>();
        List<Span> spans = Paragraphs(baselineTexts, baselineLayer);
        var texts = new List<string>(spans.Count);
        foreach (Span span in spans) texts.Add(span.Text);
        return texts;
    }

    // ============ 分组 ============

    private static List<Span> Individual(IReadOnlyList<string> texts, IReadOnlyList<OcrTextLine> layer)
    {
        var spans = new List<Span>(layer.Count);
        for (int i = 0; i < layer.Count; i++)
        {
            OcrTextLine line = layer[i];
            spans.Add(new Span(i < texts.Count ? texts[i] : line.Text, line));
        }

        return spans;
    }

    private static Span All(IReadOnlyList<string> texts, IReadOnlyList<OcrTextLine> layer)
    {
        int count = Math.Max(texts.Count, layer.Count);
        var sb = new StringBuilder();
        var boxes = new List<Int32Rect>(layer.Count);
        for (int i = 0; i < count; i++)
        {
            string text = i < texts.Count ? texts[i] : i < layer.Count ? layer[i].Text : "";
            if (text.Length > 0) sb.Append(text);
            if (i < layer.Count) boxes.Add(layer[i].Box);
        }

        return new Span(sb.ToString(), Union(boxes, layer));
    }

    /// <summary>
    /// 智能排版的分组：纵向间距超过中位行高的 0.75、或行高差超过 1.6 倍就另起一段；
    /// 以列表记号开头的行自己一段（不再与上一行接起来）。
    /// </summary>
    private static List<Span> Paragraphs(IReadOnlyList<string> texts, IReadOnlyList<OcrTextLine> layer)
    {
        var spans = new List<Span>();
        var heights = new List<int>(layer.Count);
        foreach (OcrTextLine line in layer) heights.Add(Math.Max(1, line.Box.Height));
        heights.Sort();
        int median = Math.Max(1, heights[heights.Count / 2]);
        int maxGap = Math.Max(2, (int)Math.Round(median * 0.75));

        var current = new StringBuilder();
        var boxes = new List<Int32Rect>(4);
        int currentHeight = 0;
        int bottom = 0;

        void Flush()
        {
            if (current.Length == 0 && boxes.Count == 0) return;
            spans.Add(new Span(current.ToString(), Union(boxes, layer)));
            current.Clear();
            boxes.Clear();
            currentHeight = 0;
            bottom = 0;
        }

        for (int i = 0; i < layer.Count; i++)
        {
            OcrTextLine line = layer[i];
            string text = (i < texts.Count ? texts[i] : line.Text).Trim();
            if (text.Length == 0) continue;

            bool listItem = ConversionDocument.StartsListItem(text);
            if (boxes.Count > 0)
            {
                int gap = line.Box.Y - bottom;
                bool tooFar = gap > maxGap;
                bool differentSize = currentHeight > 0 &&
                                     (line.Box.Height > currentHeight * 1.6 ||
                                      line.Box.Height * 1.6 < currentHeight);
                if (tooFar || differentSize || listItem) Flush();
            }

            if (current.Length > 0) ConversionDocument.JoinText(current, text);
            else current.Append(text);

            boxes.Add(line.Box);
            currentHeight = line.Box.Height;
            bottom = line.Box.Y + line.Box.Height;
        }

        Flush();
        return spans;
    }

    private static Int32Rect Union(List<Int32Rect> boxes, IReadOnlyList<OcrTextLine> layer)
    {
        if (boxes.Count == 0) return layer.Count > 0 ? layer[0].Box : default;

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (Int32Rect box in boxes)
        {
            left = Math.Min(left, box.X);
            top = Math.Min(top, box.Y);
            right = Math.Max(right, box.X + Math.Max(1, box.Width));
            bottom = Math.Max(bottom, box.Y + Math.Max(1, box.Height));
        }

        return new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>分组结果：合并出来的文本 + 成员行框的并集 + 取第一行的颜色。</summary>
    private sealed class Span
    {
        public Span(string text, Int32Rect box)
        {
            Text = text;
            Box = box;
        }

        public Span(string text, OcrTextLine source)
        {
            Text = text;
            Box = source.Box;
            TextColor = source.TextColor;
            HasTextColor = source.HasTextColor;
            BackgroundColor = source.BackgroundColor;
        }

        public string Text { get; }
        public Int32Rect Box { get; }
        public Rgb TextColor { get; } = Rgb.Black;
        public bool HasTextColor { get; }
        public Rgb BackgroundColor { get; } = Rgb.White;
    }
}
