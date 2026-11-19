using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LTools.Features;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 文字识别」：默认排版 / 标点、模型档（含下载）、
/// 检测缩放与处理、常驻与热启动、填充样式、文字遮罩、保存结果图、原图预览。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>text_recognition/*</c> 那 11 项，读写与归一化全在
/// <see cref="TextRecognitionStore"/>（落 <c>configs/settings.json</c> 的 <c>TextRecognition</c> 段）。
/// 这里的控件只做「回显 + 落盘」，不自己存一份。
/// </para>
/// <para>
/// 模型档下拉直接对应上游的 <c>extra_small</c> / <c>small</c> / <c>medium</c>；
/// 后两档需要下载（<see cref="OcrModelStore"/>），所以卡片里带「下载模型」按钮与进度条——
/// 这也是全项目唯一的模型下载入口（以前 <c>DownloadSmallAsync</c> 没有任何 UI 调它）。
/// </para>
/// </remarks>
public partial class ConfigHelperWindow
{
    private CancellationTokenSource? _ocrDownload;

    /// <summary>把这一卡片刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateOcr()
    {
        OcrFormattingCombo.SelectedIndex = IndexOf(OcrFormatting.Keys, TextRecognitionStore.Formatting);
        OcrPunctuationCombo.SelectedIndex = IndexOf(OcrPunctuation.Keys, TextRecognitionStore.Punctuation);
        OcrModelCombo.SelectedIndex = IndexOf(TextRecognitionStore.ModelTypes,
            TextRecognitionStore.ModelType);

        OcrResizeCombo.SelectedIndex =
            TextRecognitionStore.DetectorResizePolicy == TextRecognitionStore.ResizeMin ? 1 : 0;
        OcrProcessingCombo.SelectedIndex =
            TextRecognitionStore.DetectionProcessing == TextRecognitionStore.ProcessingSpeedFirst ? 1 : 0;
        OcrFillCombo.SelectedIndex = OcrFillStyle.IsBlur(TextRecognitionStore.FillStyle) ? 1 : 0;

        OcrResidentBox.IsChecked = TextRecognitionStore.ResidentProcess;
        OcrHotStartBox.IsChecked = TextRecognitionStore.ModelHotStart;
        OcrHighlightBox.IsChecked = TextRecognitionStore.ShowTextHighlighting;
        OcrSaveImageBox.IsChecked = TextRecognitionStore.SaveResultAsImage;
        OcrPreviewBox.IsChecked = TextRecognitionStore.ShowOriginalPreview;

        RefreshOcrModelStatus();
    }

    /// <summary>某个键在顺序表里的下标（找不到按 0）。</summary>
    private static int IndexOf(System.Collections.Generic.IReadOnlyList<string> keys, string value)
    {
        for (int i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], value, StringComparison.OrdinalIgnoreCase)) return i;

        return 0;
    }

    // ============ 下拉 / 勾选 ============

    private void CaptureOcr_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        TextRecognitionStore.SetFormatting(KeyAt(OcrFormatting.Keys, OcrFormattingCombo.SelectedIndex));
        TextRecognitionStore.SetPunctuation(KeyAt(OcrPunctuation.Keys, OcrPunctuationCombo.SelectedIndex));
        TextRecognitionStore.SetModelType(KeyAt(TextRecognitionStore.ModelTypes,
            OcrModelCombo.SelectedIndex));

        TextRecognitionStore.SetDetectorResizePolicy(OcrResizeCombo.SelectedIndex == 1
            ? TextRecognitionStore.ResizeMin
            : TextRecognitionStore.ResizeMax);
        TextRecognitionStore.SetDetectionProcessing(OcrProcessingCombo.SelectedIndex == 1
            ? TextRecognitionStore.ProcessingSpeedFirst
            : TextRecognitionStore.ProcessingAccuracyFirst);
        TextRecognitionStore.SetFillStyle(OcrFillCombo.SelectedIndex == 1
            ? OcrFillStyle.Blur
            : OcrFillStyle.BackgroundFill);

        TextRecognitionStore.SetResidentProcess(OcrResidentBox.IsChecked == true);
        TextRecognitionStore.SetModelHotStart(OcrHotStartBox.IsChecked == true);
        TextRecognitionStore.SetShowTextHighlighting(OcrHighlightBox.IsChecked == true);
        TextRecognitionStore.SetSaveResultAsImage(OcrSaveImageBox.IsChecked == true);
        TextRecognitionStore.SetShowOriginalPreview(OcrPreviewBox.IsChecked == true);

        RefreshOcrModelStatus();
    }

    private static string KeyAt(System.Collections.Generic.IReadOnlyList<string> keys, int index) =>
        index >= 0 && index < keys.Count ? keys[index] : keys[0];

    // ============ 模型下载 ============

    /// <summary>当前下拉选中的档位。</summary>
    private PaddleOcr.Tier SelectedTier => PaddleOcr.TierOf(TextRecognitionStore.ModelType);

    /// <summary>模型就位情况 + 体积，显示在下拉下面那一行。</summary>
    private void RefreshOcrModelStatus()
    {
        PaddleOcr.Tier tier = SelectedTier;
        bool ready = PaddleOcr.IsReady(tier);
        bool canDownload = OcrModelStore.CanDownload(tier);

        OcrDownloadBtn.IsEnabled = canDownload && !ready && _ocrDownload == null;
        OcrDownloadBtn.Content = ready ? "已就位" : canDownload ? "下载模型" : "随包内置";

        string size = canDownload ? $" · {Mb(OcrModelStore.TotalBytes(tier))}" : "";
        OcrModelStatus.Text = ready
            ? $"{PaddleOcr.DisplayName(tier)} 已就位{size}"
            : canDownload
                ? $"{PaddleOcr.DisplayName(tier)} 尚未下载{size}，下载后才能选它"
                : $"{PaddleOcr.DisplayName(tier)} 随包内置";
    }

    private static string Mb(long bytes) =>
        bytes <= 0 ? "" : bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.0} GB"
            : $"{bytes / 1024.0 / 1024:0.0} MB";

    private async void OcrDownload_Click(object sender, RoutedEventArgs e)
    {
        // 下载中再点一次 = 取消（不做一个点不动的按钮）
        if (_ocrDownload != null)
        {
            OcrDownloadBtn.IsEnabled = false;
            _ocrDownload.Cancel();
            return;
        }

        PaddleOcr.Tier tier = SelectedTier;
        if (!OcrModelStore.CanDownload(tier) || PaddleOcr.IsReady(tier)) return;

        _ocrDownload = new CancellationTokenSource();
        OcrDownloadBtn.IsEnabled = true;
        OcrDownloadBtn.Content = "取消下载";
        OcrDownloadProgress.Value = 0;
        OcrDownloadProgress.Visibility = Visibility.Visible;
        OcrModelStatus.Text = $"正在下载 {PaddleOcr.DisplayName(tier)}…";

        var progress = new Progress<double>(value =>
        {
            OcrDownloadProgress.Value = Math.Clamp(value, 0, 1);
            OcrModelStatus.Text = $"正在下载 {PaddleOcr.DisplayName(tier)}… {value * 100:0}%";
        });

        try
        {
            await OcrModelStore.DownloadAsync(tier, progress, _ocrDownload.Token);
            PaddleOcr.OnSettingsChanged();
            OcrModelStatus.Text = $"{PaddleOcr.DisplayName(tier)} 已就位";
        }
        catch (OperationCanceledException)
        {
            OcrModelStatus.Text = "下载已取消";
        }
        catch (Exception ex)
        {
            OcrModelStatus.Text = "下载失败：" + ex.Message;
            LTools.Core.AppLog.Warn("CapturePro.Ocr", "模型下载失败：" + ex.Message);
        }
        finally
        {
            _ocrDownload.Dispose();
            _ocrDownload = null;
            OcrDownloadProgress.Visibility = Visibility.Collapsed;
            RefreshOcrModelStatus();
        }
    }
}
