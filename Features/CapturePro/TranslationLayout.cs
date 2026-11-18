using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>一次翻译要摆回画布的东西：送给模型的文本 + 它们落在哪些行框上。</summary>
/// <remarks>
/// <see cref="Texts"/> 与 <see cref="Layer"/> 一一对应；按原行翻译时就是识别出来的行，
/// 智能合并时是合并后的段落（行数更少、行框是成员的并集）。
/// </remarks>
internal sealed record TranslationOverlay(IReadOnlyList<string> Texts, IReadOnlyList<OcrTextLine> Layer);

/// <summary>
/// 翻译前的版面处理（照上游 <c>screenshot_translation/layout_processing</c> 的
/// <c>smart_merge</c> / <c>original</c>）：<c>smart_merge</c> 先把 OCR 的断行按版面接成段落再翻，
/// 译文按段落重排（长句会在段框里折行）；<c>original</c> 逐行翻、逐行摆回。
/// </summary>
/// <remarks>
/// 分组规则与 P4-g 的「智能排版」完全同一份（<see cref="OcrTextTransform"/> 的
/// <see cref="OcrFormatting.Smart"/>）：纵向间距、行高差、列表记号。别再另写一套分组，
/// 否则「排版」下拉接出来的段落和翻译接出来的段落会不一样。
/// </remarks>
internal static class TranslationLayout
{
    /// <summary>智能合并（上游默认）。</summary>
    public const string SmartMerge = "smart_merge";

    /// <summary>按原始 OCR 行。</summary>
    public const string Original = "original";

    /// <summary>下拉顺序（上游同为「智能合并 / 原始」两项）。</summary>
    public static readonly IReadOnlyList<string> Keys = new[] { SmartMerge, Original };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SmartMerge;
        string trimmed = value.Trim();
        foreach (string key in Keys)
            if (string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase)) return key;

        return SmartMerge;
    }

    public static string LabelOf(string? value) => Normalize(value) switch
    {
        Original => "按原行",
        _ => "智能合并",
    };

    public static string Describe(string? value) => Normalize(value) switch
    {
        Original => "逐行翻译、逐行摆回（行数一定对得上）",
        _ => "先按版面把断行接成段落再翻，长句在段框里折行（更像文章）",
    };

    /// <summary>按当前版面把「要翻译的文本 / 落点」准备好。</summary>
    public static TranslationOverlay Prepare(IReadOnlyList<string> texts,
        IReadOnlyList<OcrTextLine> layer, string? layout)
    {
        if (Normalize(layout) == Original || layer.Count == 0)
            return new TranslationOverlay(new List<string>(texts), layer);

        OcrTransformResult merged = OcrTextTransform.Apply(texts, layer, OcrFormatting.Smart,
            OcrPunctuation.None);
        return new TranslationOverlay(merged.Lines, merged.Layer);
    }

    /// <summary>把译文换进同一批行框（行数必须与 <paramref name="overlay"/> 对齐）。</summary>
    public static TranslationOverlay ReplaceTexts(TranslationOverlay overlay,
        IReadOnlyList<string> translated)
    {
        var layer = new List<OcrTextLine>(overlay.Layer.Count);
        for (int i = 0; i < overlay.Layer.Count; i++)
        {
            OcrTextLine source = overlay.Layer[i];
            layer.Add(new OcrTextLine
            {
                Index = i,
                Text = i < translated.Count ? translated[i] : "",
                Box = source.Box,
                TextColor = source.TextColor,
                HasTextColor = source.HasTextColor,
                BackgroundColor = source.BackgroundColor,
                FontSize = source.FontSize,
            });
        }

        return new TranslationOverlay(translated, layer);
    }

    /// <summary>空层 / 空文本的兜底（冒烟与自检直接用）。</summary>
    public static readonly TranslationOverlay Empty =
        new(Array.Empty<string>(), Array.Empty<OcrTextLine>());
}
