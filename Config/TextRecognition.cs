namespace LTools.Config;

/// <summary>
/// 文字识别的默认排版 / 标点（<c>configs/settings.json</c> 的 <c>TextRecognition</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>text_recognition/default_formatting</c> 与
/// <c>text_recognition/default_punctuation</c>：进文字编辑时先把这两个值应用一次，
/// 用户也能在识别窗的下拉里临时改。值域与 <c>OcrFormatting</c> / <c>OcrPunctuation</c> 的
/// 键一一对应（<c>none</c> / <c>keep</c> / <c>remove</c> / <c>smart</c> 与 <c>none</c> / <c>half</c> / <c>full</c>），
/// 认不出的值一律按 <c>none</c>。
/// </remarks>
public sealed class TextRecognitionFile
{
    /// <summary>默认排版（<c>none</c> / <c>keep</c> / <c>remove</c> / <c>smart</c>）。</summary>
    public string? DefaultFormatting { get; set; }

    /// <summary>默认标点（<c>none</c> / <c>half</c> / <c>full</c>）。</summary>
    public string? DefaultPunctuation { get; set; }
}
