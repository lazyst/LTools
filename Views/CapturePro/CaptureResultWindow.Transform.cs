using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗的「排版 / 标点」两个下拉 + 「重置」（照 Snow Shot 文字模式工具条的
/// <c>screenshotOcrTextFormattingSelect</c> / <c>screenshotOcrTextPunctuationSelect</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游语义（<c>screenshotocrtexteditingsession</c>）：第一次选到非「无」时记一份**基线**，
/// 之后每次变换都从基线重算（所以「半角 → 全角」不会叠加出错）；两个都回到「无」= 恢复基线。
/// **任何手动编辑都会作废变换**（上游 <c>appendHistory</c> 里 <c>clearTransforms</c>），
/// 这里的判定是「文字变了但不是我改的」→ 基线丢掉、两个下拉静默回「无」，文字保持用户改过的样子。
/// </para>
/// <para>
/// 本项目的文字层是「模型行 ↔ OCR 行框」一一对应，所以合并会让行数变少（去换行 = 1 行、
/// 智能排版 = 每段 1 行），重建文字层用合并后的行框并集（<see cref="OcrTextTransform"/> 给出）。
/// </para>
/// <para>
/// 上游的 <c>resetTextRequested</c> = 清掉变换并**回到最初的 OCR 文本**，这里就是「重置」按钮；
/// 它不写默认值（默认值只在用户改下拉时记住）。
/// </para>
/// </remarks>
public partial class CaptureResultWindow
{
    private List<string>? _transformBaselineLines;
    private IReadOnlyList<OcrTextLine>? _transformBaselineLayer;
    private string _transformAppliedText = "";
    private bool _loadingTransform;
    private bool _applyingTransform;
    private int _ocrWidth = 1;
    private int _ocrHeight = 1;

    /// <summary>当前排版键（冒烟读它做断言）。</summary>
    internal string SelectedFormatting =>
        (FormattingBox.SelectedItem as ComboBoxItem)?.Tag as string ?? OcrFormatting.None;

    /// <summary>当前标点键。</summary>
    internal string SelectedPunctuation =>
        (PunctuationBox.SelectedItem as ComboBoxItem)?.Tag as string ?? OcrPunctuation.None;

    /// <summary>文字层现在的行数（变换会让它变；冒烟断言用）。</summary>
    internal int TextLineCount => _layer.Count;

    private void FillTransformOptions()
    {
        if (FormattingBox == null || PunctuationBox == null) return;

        _loadingTransform = true;
        try
        {
            FormattingBox.Items.Clear();
            foreach (string key in OcrFormatting.Keys)
            {
                FormattingBox.Items.Add(new ComboBoxItem
                {
                    Content = OcrFormatting.LabelOf(key),
                    Tag = key,
                    ToolTip = OcrFormatting.Describe(key),
                });
            }

            PunctuationBox.Items.Clear();
            foreach (string key in OcrPunctuation.Keys)
            {
                PunctuationBox.Items.Add(new ComboBoxItem
                {
                    Content = OcrPunctuation.LabelOf(key),
                    Tag = key,
                    ToolTip = OcrPunctuation.Describe(key),
                });
            }

            (string formatting, string punctuation) = TextRecognitionStore.Defaults();
            SelectFormatting(formatting);
            SelectPunctuation(punctuation);
        }
        finally
        {
            _loadingTransform = false;
        }

        UpdateTransformBar();
    }

    private void SelectFormatting(string key)
    {
        string normalized = OcrFormatting.Normalize(key);
        for (int i = 0; i < FormattingBox.Items.Count; i++)
        {
            if (FormattingBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, normalized, StringComparison.Ordinal))
            {
                FormattingBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void SelectPunctuation(string key)
    {
        string normalized = OcrPunctuation.Normalize(key);
        for (int i = 0; i < PunctuationBox.Items.Count; i++)
        {
            if (PunctuationBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, normalized, StringComparison.Ordinal))
            {
                PunctuationBox.SelectedIndex = i;
                return;
            }
        }
    }

    private void FormattingBox_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyTextTransform(persist: true);

    private void PunctuationBox_Changed(object sender, SelectionChangedEventArgs e) =>
        ApplyTextTransform(persist: true);

    private void TextReset_Click(object sender, RoutedEventArgs e) => ResetText();

    /// <summary>识别完成 / 灌结果时按默认值先应用一次（上游 <c>applyInitialTransforms</c>）。</summary>
    private void ApplyInitialTransforms()
    {
        if (_model == null || _model.LineCount == 0)
        {
            UpdateTransformBar();
            return;
        }

        string formatting = SelectedFormatting;
        string marks = SelectedPunctuation;
        if (formatting == OcrFormatting.None && marks == OcrPunctuation.None)
        {
            _transformBaselineLines = null;
            _transformBaselineLayer = null;
            _transformAppliedText = _model.AllText;
            UpdateTransformBar();
            return;
        }

        _transformBaselineLines = new List<string>(_model.Lines);
        _transformBaselineLayer = _layer;
        ApplyTransformCore(formatting, marks);
        UpdateTransformBar();
    }

    private void ApplyTextTransform(bool persist)
    {
        if (_loadingTransform || _applyingTransform || _model == null) return;

        // 译文态是只读的：拉一下下拉不该悄悄改掉原文
        if (_translatedLines != null)
        {
            _loadingTransform = true;
            try
            {
                (string formatting, string punctuation) = TextRecognitionStore.Defaults();
                SelectFormatting(formatting);
                SelectPunctuation(punctuation);
            }
            finally
            {
                _loadingTransform = false;
            }

            return;
        }

        string text = SelectedFormatting;
        string marks = SelectedPunctuation;

        if (persist)
        {
            TextRecognitionStore.SetFormatting(text);
            TextRecognitionStore.SetPunctuation(marks);
        }

        if (text == OcrFormatting.None && marks == OcrPunctuation.None)
        {
            RestoreTransformBaseline();
            return;
        }

        if (_transformBaselineLines == null)
        {
            _transformBaselineLines = new List<string>(_model.Lines);
            _transformBaselineLayer = _layer;
        }

        ApplyTransformCore(text, marks);
    }

    private void ApplyTransformCore(string formatting, string punctuation)
    {
        if (_model == null || _transformBaselineLines == null || _transformBaselineLayer == null)
            return;

        OcrTransformResult result = OcrTextTransform.Apply(_transformBaselineLines,
            _transformBaselineLayer, formatting, punctuation);
        if (result.Layer.Count == 0) return;

        _applyingTransform = true;
        try
        {
            _layer = result.Layer;
            _model.ReplaceAll(result.Lines);
            _transformAppliedText = _model.AllText;
            OcrCanvas.Attach(_model, _layer, _ocrWidth, _ocrHeight);
        }
        finally
        {
            _applyingTransform = false;
        }

        UpdateZoom();
        RefreshStatus();
    }

    /// <summary>两个下拉都回到「无」= 恢复基线文本（上游 <c>transformedText</c> 的空变换）。</summary>
    private void RestoreTransformBaseline()
    {
        if (_model == null || _transformBaselineLines == null || _transformBaselineLayer == null)
        {
            UpdateTransformBar();
            return;
        }

        List<string> texts = _transformBaselineLines;
        IReadOnlyList<OcrTextLine> layer = _transformBaselineLayer;
        _transformBaselineLines = null;
        _transformBaselineLayer = null;

        _applyingTransform = true;
        try
        {
            _layer = layer;
            _model.ReplaceAll(texts);
            _transformAppliedText = _model.AllText;
            OcrCanvas.Attach(_model, _layer, _ocrWidth, _ocrHeight);
        }
        finally
        {
            _applyingTransform = false;
        }

        UpdateZoom();
        RefreshStatus();
    }

    /// <summary>
    /// 「重置」= 上游 <c>resetTextRequested</c>：丢掉变换、回到最初的 OCR 文本与行框，
    /// 两个下拉回「无」。不写默认值。
    /// </summary>
    internal void ResetText()
    {
        if (_model == null || _result == null) return;

        _transformBaselineLines = null;
        _transformBaselineLayer = null;

        _applyingTransform = true;
        _loadingTransform = true;
        try
        {
            SelectFormatting(OcrFormatting.None);
            SelectPunctuation(OcrPunctuation.None);

            _layer = _result.TextLayer.Count > 0
                ? _result.TextLayer
                : OcrTextLayer.FromLines(_result.Lines);
            _model.ReplaceAll(_layer.Select(line => line.Text));
            _transformAppliedText = _model.AllText;
            OcrCanvas.Attach(_model, _layer, _ocrWidth, _ocrHeight);
        }
        finally
        {
            _loadingTransform = false;
            _applyingTransform = false;
        }

        UpdateZoom();
        RefreshStatus();
    }

    /// <summary>
    /// 手动编辑会作废变换（上游 <c>appendHistory</c> 里 <c>clearTransforms</c>）：
    /// 只看**文本**有没有变，光标 / 选区变化不算。
    /// </summary>
    private void DropTransformsOnUserEdit()
    {
        if (_applyingTransform || _transformBaselineLines == null || _model == null) return;
        if (string.Equals(_model.AllText, _transformAppliedText, StringComparison.Ordinal)) return;

        _transformBaselineLines = null;
        _transformBaselineLayer = null;
        _transformAppliedText = _model.AllText;

        _loadingTransform = true;
        try
        {
            SelectFormatting(OcrFormatting.None);
            SelectPunctuation(OcrPunctuation.None);
        }
        finally
        {
            _loadingTransform = false;
        }

        UpdateTransformBar();
    }

    /// <summary>排版 / 标点只服务文字页；译文态禁用（译文是只读 overlay）。</summary>
    private void UpdateTransformBar()
    {
        if (TransformBar == null) return;

        TransformBar.Visibility = _tab == "text" ? Visibility.Visible : Visibility.Collapsed;

        bool enabled = _model is { LineCount: > 0 } && _translatedLines == null;
        FormattingBox.IsEnabled = enabled;
        PunctuationBox.IsEnabled = enabled;
        TextResetBtn.IsEnabled = enabled;

        TransformBar.ToolTip = _translatedLines != null
            ? "译文是只读的：点「原文」切回去再改排版"
            : _model is { LineCount: > 0 }
                ? null
                : "没有识别到文字";
    }
}
