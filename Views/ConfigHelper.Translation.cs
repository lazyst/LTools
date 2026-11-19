using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 截图 Pro →「文字识别」页里的在线翻译卡片（照 Snow Shot 的 <c>screenshot_translation/*</c>）：
/// 源语言 / 目标语言 / 第二目标语言 / 语言检测置信度门槛，加上通往完整设置（端点 / 模型 / 密钥）的入口。
/// </summary>
/// <remarks>
/// 上游这五项都在「翻译设置」对话框里；LTools 把最常改的四项摊到设置页，端点 / 模型 / 密钥这类
/// 一次性配置仍留在对话框（<see cref="LTools.Views.CapturePro.TranslationSettingsWindow"/>），
/// 从这里的「设置…」进。两边写的是同一份 <c>settings.json</c> 的 <c>Translation</c> 段。
/// </remarks>
public partial class ConfigHelperWindow
{
    private bool _translationLoading;   // 回填期间抑制写盘

    /// <summary>把翻译设置刷到控件（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateTranslation()
    {
        _translationLoading = true;
        try
        {
            if (TranslationSourceCombo.Items.Count == 0)
            {
                TranslationSourceCombo.Items.Add(new ComboBoxItem
                {
                    Content = "自动判断",
                    Tag = TranslationLanguages.Auto,
                });
                foreach (TranslationLanguage language in TranslationLanguages.All)
                    TranslationSourceCombo.Items.Add(new ComboBoxItem
                    {
                        Content = language.Label,
                        Tag = language.Key,
                    });

                foreach (TranslationLanguage language in TranslationLanguages.All)
                    TranslationTargetCombo.Items.Add(new ComboBoxItem
                    {
                        Content = language.Label,
                        Tag = language.Key,
                    });

                // 第一项 = 不用（照上游：空值就是不换目标语言）
                TranslationSecondaryCombo.Items.Add(new ComboBoxItem { Content = "不用", Tag = "" });
                foreach (TranslationLanguage language in TranslationLanguages.All)
                    TranslationSecondaryCombo.Items.Add(new ComboBoxItem
                    {
                        Content = language.Label,
                        Tag = language.Key,
                    });
            }

            TranslationConfig config = TranslationStore.Config;
            SelectTag(TranslationSourceCombo, TranslationLanguages.NormalizeSource(config.Source));
            SelectTag(TranslationTargetCombo, TranslationLanguages.Normalize(config.Target));
            SelectTag(TranslationSecondaryCombo, TranslationStore.SecondaryTarget);
            TranslationThresholdBox.Text = TranslationStore.DetectionThreshold
                .ToString(CultureInfo.InvariantCulture);
            UpdateTranslationSummary(config);
        }
        finally { _translationLoading = false; }
    }

    private void UpdateTranslationSummary(TranslationConfig config)
    {
        string target = TranslationLanguages.LabelOf(config.Target);
        string secondary = TranslationStore.SecondaryTarget;
        string secondText = secondary.Length == 0
            ? "不换"
            : TranslationLanguages.LabelOf(secondary);

        TranslationSummaryText.Text = config.IsReady
            ? string.Format(CultureInfo.CurrentCulture,
                "服务：{0} · {1}；{2} → {3}（原文已是{3}时译成 {4}）。",
                config.Model, config.Endpoint, "自动判断原文语言", target, secondText)
            : "还没配翻译服务：点「设置…」填端点与模型（OpenAI 兼容的 chat completions）。";
    }

    private void TranslationLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _translationLoading) return;

        string source = TagOf(TranslationSourceCombo) ?? TranslationLanguages.Auto;
        string target = TagOf(TranslationTargetCombo) ?? TranslationLanguages.DefaultTarget;
        string secondary = TagOf(TranslationSecondaryCombo) ?? "";

        TranslationConfig config = TranslationStore.Config with
        {
            Source = TranslationLanguages.NormalizeSource(source),
            Target = TranslationLanguages.Normalize(target),
        };

        TranslationStore.SaveWithDisplay(config, TranslationStore.OriginalImage,
            TranslationStore.CopyAsImage, TranslationStore.Layout, secondary,
            TranslationStore.DetectionThreshold);
        UpdateTranslationSummary(config);
    }

    private void TranslationThreshold_LostFocus(object sender, RoutedEventArgs e) => CommitThreshold();

    private void TranslationThreshold_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitThreshold();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    /// <summary>门槛只在失焦 / 回车时落盘（每敲一键就写一次会把 ui 线程顶住）。</summary>
    private void CommitThreshold()
    {
        if (_initializing || _translationLoading) return;

        string text = TranslationThresholdBox.Text.Trim();
        int value = TranslationLanguages.DefaultThreshold;
        if (text.Length > 0 &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            value = parsed;

        value = TranslationLanguages.NormalizeThreshold(value);
        TranslationStore.SetDetectionThreshold(value);
        TranslationThresholdBox.Text = value.ToString(CultureInfo.InvariantCulture);
        UpdateTranslationSummary(TranslationStore.Config);
    }

    private void TranslationSettings_Click(object sender, RoutedEventArgs e)
    {
        if (LTools.Views.CapturePro.TranslationSettingsWindow.Show(this, TranslationStore.Config))
            PopulateTranslation();
    }
}
