using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「翻译设置」：OpenAI 兼容端点 / 模型 / 密钥 / 源与目标语言 / 超时。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshottranslationsettingsdialog</c>：翻译前必须有可用的服务，
/// 所以结果窗点「翻译」时如果还没配，会直接把这个框弹出来（不让按钮点下去什么都不发生）。
/// 「测试连接」真的发一条 <c>["Hello, world!"]</c> 出去，成功就把译文显示在框内。
/// </para>
/// <para>
/// 保存走 <see cref="TranslationStore"/>（只写 <c>configs/settings.json</c>）；
/// 设置页的完整翻译页留到 P7，这里先保证功能闭环。
/// </para>
/// </remarks>
public partial class TranslationSettingsWindow : Window
{
    private TranslationConfig _initial = TranslationConfig.Empty;
    private bool _testing;

    internal TranslationSettingsWindow(TranslationConfig config)
    {
        InitializeComponent();

        _initial = config;
        EndpointBox.Text = config.Endpoint;
        ModelBox.Text = config.Model;
        KeyBox.Password = config.ApiKey;
        TimeoutBox.Text = Math.Max(1, config.TimeoutMs / 1000).ToString(CultureInfo.InvariantCulture);

        SourceBox.Items.Add(new ComboBoxItem { Content = "自动判断", Tag = TranslationLanguages.Auto });
        foreach (TranslationLanguage language in TranslationLanguages.All)
            SourceBox.Items.Add(new ComboBoxItem { Content = language.Label, Tag = language.Key });

        foreach (TranslationLanguage language in TranslationLanguages.All)
            TargetBox.Items.Add(new ComboBoxItem { Content = language.Label, Tag = language.Key });

        SelectTag(SourceBox, TranslationLanguages.NormalizeSource(config.Source));
        SelectTag(TargetBox, TranslationLanguages.Normalize(config.Target));

        OriginalImageBox.IsChecked = TranslationStore.OriginalImage;
        CopyAsImageBox.IsChecked = TranslationStore.CopyAsImage;
        foreach (string key in TranslationLayout.Keys)
            LayoutBox.Items.Add(new ComboBoxItem { Content = TranslationLayout.LabelOf(key), Tag = key });
        SelectTag(LayoutBox, TranslationStore.Layout);
        LayoutBox.SelectionChanged += (_, _) =>
        {
            LayoutHint.Text = TranslationLayout.Describe(TagOf(LayoutBox) as string);
            // 「画进原图」只在翻译页有意义，但复制成图片两个模式都能用，所以两者互不牵制
        };
        LayoutHint.Text = TranslationLayout.Describe(TagOf(LayoutBox) as string);

        Loaded += (_, _) => EndpointBox.Focus();
    }

    /// <summary>打开设置框；返回 true = 保存了（调用方据此继续翻译）。</summary>
    internal static bool Show(Window? owner, TranslationConfig config)
    {
        var window = new TranslationSettingsWindow(config)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? true,
        };

        return window.ShowDialog() == true;
    }

    /// <summary>当前控件里的配置（保存与测试共用）。</summary>
    internal TranslationConfig ReadConfig(out string error)
    {
        error = "";
        string endpoint = EndpointBox.Text.Trim();
        string model = ModelBox.Text.Trim();

        if (!TranslationClient.ValidateEndpoint(endpoint, out string endpointError))
        {
            error = endpointError;
            return TranslationConfig.Empty;
        }

        if (model.Length == 0)
        {
            error = "请填模型名（例如 gpt-4o-mini）";
            return TranslationConfig.Empty;
        }

        int seconds = 30;
        string timeoutText = TimeoutBox.Text.Trim();
        if (timeoutText.Length > 0 &&
            (!int.TryParse(timeoutText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out seconds) || seconds is < 1 or > 120))
        {
            error = "超时请填 1–120 秒";
            return TranslationConfig.Empty;
        }

        return TranslationConfig.Normalize(endpoint, model, KeyBox.Password, TagOf(TargetBox) as string,
            TagOf(SourceBox) as string, seconds * 1000);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_testing) return;

        TranslationConfig config = ReadConfig(out string error);
        if (error.Length > 0)
        {
            ErrorText.Text = error;
            return;
        }

        ErrorText.Text = "";
        TestText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
        TestText.Text = "正在测试 " + TranslationClient.ChatUrl(config.Endpoint) + " …";
        TestBtn.IsEnabled = false;
        _testing = true;

        try
        {
            TranslationResult result = await TranslationClient.TranslateAsync(config,
                new[] { "Hello, world!" }, TranslationLanguages.Detect("Hello, world!"), CancellationToken.None);

            if (result.Ok)
            {
                TestText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
                TestText.Text = "连接成功：" + (result.Lines.Count > 0 ? result.Lines[0] : "（空）");
            }
            else
            {
                TestText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                TestText.Text = result.Error;
            }
        }
        finally
        {
            TestBtn.IsEnabled = true;
            _testing = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        TranslationConfig config = ReadConfig(out string error);
        if (error.Length > 0)
        {
            ErrorText.Text = error;
            return;
        }

        TranslationStore.SaveWithDisplay(config, OriginalImageBox.IsChecked == true,
            CopyAsImageBox.IsChecked == true, TagOf(LayoutBox) as string);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static object? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag;

    private static void SelectTag(ComboBox box, string tag)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }
}
