using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗的翻译页：目标语言下拉、翻译 / 取消 / 切回原文、设置入口。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 识别窗的「翻译」入口（<c>beginTextTranslation</c>）：确认后把**当前文字层的每一行**
/// 送去翻译，译文按原来的行框摆回画布（overlay translation），切回原文还能继续编辑。
/// 翻译期间按钮变成「取消」（真能取消，<see cref="TranslationClient"/> 走 linked token）。
/// </para>
/// <para>
/// 没配端点时点「翻译」直接把设置框弹出来——不做「点一下什么都没发生」的死按钮。
/// 目标语言换一下立刻落盘（<see cref="TranslationStore.SetTarget"/>），下次还是这个目标。
/// </para>
/// </remarks>
public partial class CaptureResultWindow
{
    private CancellationTokenSource? _translationCts;
    private IReadOnlyList<string>? _translatedLines;
    private TranslationOverlay? _translationOverlay;
    private bool _targetsLoading;

    /// <summary>当前显示的译文（没开翻译 = null；冒烟读它做断言）。</summary>
    internal IReadOnlyList<string>? TranslatedLines => _translatedLines;

    /// <summary>译文落点（行框 + 颜色；智能合并时行数比文字层少）。</summary>
    internal TranslationOverlay? TranslationOverlay => _translationOverlay;

    /// <summary>最近一次翻译失败的原因（成功 / 取消时为空）。</summary>
    internal string TranslationError { get; private set; } = "";

    /// <summary>正在翻译（冒烟用它等结果）。</summary>
    internal bool Translating => _translationCts != null;

    private void FillTranslateTargets()
    {
        _targetsLoading = true;
        foreach (TranslationLanguage language in TranslationLanguages.All)
            TargetBox.Items.Add(new ComboBoxItem { Content = language.Label, Tag = language.Key });

        SelectTargetLanguage(TranslationStore.Config.Target);
        _targetsLoading = false;
        UpdateTranslateBar();
    }

    private void SelectTargetLanguage(string key)
    {
        string normalized = TranslationLanguages.Normalize(key);
        for (int i = 0; i < TargetBox.Items.Count; i++)
        {
            if (TargetBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, normalized, StringComparison.Ordinal))
            {
                TargetBox.SelectedIndex = i;
                return;
            }
        }

        if (TargetBox.Items.Count > 0) TargetBox.SelectedIndex = 0;
    }

    private string SelectedTarget =>
        (TargetBox.SelectedItem as ComboBoxItem)?.Tag as string ?? TranslationLanguages.DefaultTarget;

    private void TargetBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_targetsLoading) return;
        TranslationStore.SetTarget(SelectedTarget);
        RefreshStatus();
    }

    private void Translate_Click(object sender, RoutedEventArgs e) => _ = ToggleTranslationAsync();

    private void TranslateSettings_Click(object sender, RoutedEventArgs e)
    {
        if (TranslateSettings())
        {
            SelectTargetLanguage(TranslationStore.Config.Target);
            RefreshStatus();
        }
    }

    /// <summary>弹翻译设置；返回 true = 保存了。冒烟用反射调它（并在外面直接改配置）。</summary>
    internal bool TranslateSettings()
    {
        bool saved = TranslationSettingsWindow.Show(this, TranslationStore.Config);
        if (saved)
        {
            UpdateTranslateBar();
            // 「译文画进原图」「复制成图片」是保存后立刻生效的显示项：
            // 已经显示译文时把画布底图 / 文案一起换过来，不必重新翻一次。
            if (_translatedLines != null)
            {
                OcrCanvas.SetTranslation(_translatedLines, _translationOverlay?.Layer,
                    TranslationStore.OriginalImage);
                UpdateTabs();
                RefreshStatus();
            }
        }

        return saved;
    }

    /// <summary>翻一遍 / 取消 / 切回原文（「翻译」按钮与「截图并翻译」入口共用）。</summary>
    internal async System.Threading.Tasks.Task ToggleTranslationAsync()
    {
        // 正在翻译：这一下是「取消」
        if (_translationCts is { } running)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经结束
            }

            StatusText.Text = "正在取消翻译…";
            return;
        }

        // 已经显示译文：切回原文
        if (_translatedLines != null)
        {
            ClearTranslation();
            return;
        }

        if (_model == null || _model.LineCount == 0)
        {
            ToastService.Show("没有可翻译的文字", "识别结果", 1600);
            return;
        }

        TranslationConfig config = TranslationStore.Config;
        if (!config.IsReady)
        {
            if (!TranslateSettings())
            {
                StatusText.Text = "翻译需要先配置端点与模型";
                ToastService.Show("翻译需要先配置端点与模型", "识别结果", 2400, ToastKind.warning);
                return;
            }

            config = TranslationStore.Config;
            if (!config.IsReady) return;
        }

        // 翻的是「当前文字层」：用户改过的字也一起翻（与上游一致）。
        // 版面处理（上游 layout_processing）默认 smart_merge：先把断行接成段落再翻，
        // 译文按段落重排（段框 = 成员行框的并集），长句在段框里折行。
        var source = new List<string>(_model.Lines);
        TranslationOverlay prepared = TranslationLayout.Prepare(source, _layer,
            TranslationStore.Layout);
        string unit = TranslationLayout.Normalize(TranslationStore.Layout) == TranslationLayout.Original
            ? "行"
            : "段";
        string detected = TranslationLanguages.Detect(TranslationLanguages.Join(prepared.Texts));
        string sourceKey = config.Source == TranslationLanguages.Auto ? detected : config.Source;

        var cts = new CancellationTokenSource();
        _translationCts = cts;
        TranslationError = "";
        TranslateBtn.Content = "取消";
        CopyBtn.Content = "复制文字";
        StatusText.Text = string.Format(CultureInfo.CurrentCulture, "正在翻译 {0} {1} · {2} → {3}",
            prepared.Texts.Count,
            unit,
            sourceKey == TranslationLanguages.Auto
                ? "自动"
                : TranslationLanguages.LabelOf(sourceKey),
            TranslationLanguages.LabelOf(config.Target));

        TranslationResult result;
        try
        {
            result = await TranslationClient.TranslateAsync(config, prepared.Texts, sourceKey, cts.Token);
        }
        finally
        {
            cts.Dispose();
            _translationCts = null;
        }

        TranslateBtn.Content = "翻译";

        if (!result.Ok)
        {
            if (result.Error == "已取消翻译")
            {
                RefreshStatus();
                return;
            }

            TranslationError = result.Error;
            AppLog.Warn("CapturePro.Translate", "翻译失败：" + result.Error);
            ToastService.Show("翻译失败：" + result.Error, "识别结果", 0, ToastKind.error);
            RefreshStatus();
            return;
        }

        TranslationError = "";
        _translationOverlay = TranslationLayout.ReplaceTexts(prepared, result.Lines);
        _translatedLines = _translationOverlay.Texts;
        OcrCanvas.SetTranslation(_translatedLines, _translationOverlay.Layer,
            TranslationStore.OriginalImage);
        TranslateBtn.Content = "原文";
        UpdateTabs();
        RefreshStatus();

        ToastService.Show(string.Format(CultureInfo.CurrentCulture, "已翻译 {0} {1} → {2}",
            result.Lines.Count, unit, TranslationLanguages.LabelOf(config.Target)), "识别结果", 2000);
    }

    /// <summary>把译文渲染成一张图（复制译文图 / 冒烟断言）。没开翻译时为 null。</summary>
    internal BitmapSource? RenderTranslationImage()
    {
        if (_translationOverlay == null) return null;
        return OcrImageRenderer.Render(_image, _translationOverlay.Layer, _translationOverlay.Texts,
            TranslationStore.OriginalImage, fillBackground: true);
    }

    /// <summary>切回原文（译文缓存丢掉；再点「翻译」重新请求）。</summary>
    private void ClearTranslation()
    {
        _translatedLines = null;
        _translationOverlay = null;
        TranslationError = "";
        OcrCanvas.SetTranslation(null);
        TranslateBtn.Content = "翻译";
        UpdateTabs();
        RefreshStatus();
    }

    /// <summary>翻译只服务文字页：别的页签把这一组收起来（不放死按钮）。</summary>
    private void UpdateTranslateBar()
    {
        if (TranslateBar == null) return;
        TranslateBar.Visibility = _tab == "text" ? Visibility.Visible : Visibility.Collapsed;
        if (_translationCts == null) TranslateBtn.Content = _translatedLines != null ? "原文" : "翻译";
        TranslateBtn.IsEnabled = _translationCts != null || _model is { LineCount: > 0 };
        TranslateBtn.ToolTip = _model is { LineCount: > 0 }
            ? null
            : "没有识别到文字，翻译不可用";
    }

    /// <summary>复制按钮在译文模式下的文案由这里决定（<see cref="UpdateTabs"/> 调）。</summary>
    private string CopyLabelForText()
    {
        if (_translatedLines == null) return "复制文字";
        return TranslationStore.CopyAsImage ? "复制译文图" : "复制译文";
    }
}
