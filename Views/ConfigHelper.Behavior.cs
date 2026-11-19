using System.Windows;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 截图行为」的七项设置（照上游 <c>screenshot/*</c>）。
/// </summary>
/// <remarks>
/// 读写与归一化都在 <see cref="ScreenshotBehavior"/>（落 <c>configs/settings.json</c> 的
/// <c>Screenshot</c> 段）。接管的是「截图」这条链路自己的行为：抓帧时画不画指针、
/// Esc 退出要不要确认、非选择工具能不能拖边框、拖边框怎么跟手、导出要不要按屏幕缩放降采样、
/// 另存 PDF 用多大纸、长截图自动滚动多快。采集入口自己的参数仍挂在功能参数上。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把七个控件刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateBehavior()
    {
        CaptureCursorBox.IsChecked = ScreenshotBehavior.CaptureCursor;
        CaptureConfirmExitBox.IsChecked = ScreenshotBehavior.ConfirmBeforeExitViaShortcut;
        CaptureQuickSelectionModBox.IsChecked = ScreenshotBehavior.QuickSelectionModification;

        CaptureResizeModeCombo.SelectedIndex =
            ScreenshotBehavior.SelectionResizeMode == ScreenshotBehavior.ResizeFollowPosition ? 1 : 0;

        CaptureHidpiCombo.SelectedIndex = HidpiIndex(ScreenshotBehavior.HidpiImageScaling);

        CapturePdfPageSizeCombo.SelectedIndex = ScreenshotBehavior.PdfPageSize switch
        {
            ScreenshotBehavior.PdfA4Landscape => 1,
            ScreenshotBehavior.PdfImageSize => 2,
            _ => 0,
        };

        CaptureScrollIntervalBox.Text =
            ScreenshotBehavior.ScrollingAutoScrollIntervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>HiDPI 下拉序号：0 = 关闭、1 = 自动、2… = 125…350。</summary>
    private static int HidpiIndex(string key)
    {
        if (key == ScreenshotBehavior.HidpiAuto) return 1;
        if (!ScreenshotBehavior.TryParsePercentage(key, out int percentage)) return 0;

        for (int index = 0; index < ScreenshotBehavior.HidpiPercentages.Count; index++)
            if (ScreenshotBehavior.HidpiPercentages[index] == percentage) return index + 2;

        return 0;
    }

    /// <summary>下拉序号 → HiDPI 设置键（认不出的按关闭）。</summary>
    private static string HidpiKey(int index)
    {
        if (index == 1) return ScreenshotBehavior.HidpiAuto;
        if (index >= 2 && index - 2 < ScreenshotBehavior.HidpiPercentages.Count)
            return ScreenshotBehavior.HidpiPercentages[index - 2]
                .ToString(System.Globalization.CultureInfo.InvariantCulture);

        return ScreenshotBehavior.HidpiDisabled;
    }

    private void CaptureBehavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        ScreenshotBehavior.SetCaptureCursor(CaptureCursorBox.IsChecked == true);
        ScreenshotBehavior.SetConfirmBeforeExitViaShortcut(CaptureConfirmExitBox.IsChecked == true);
        ScreenshotBehavior.SetQuickSelectionModification(CaptureQuickSelectionModBox.IsChecked == true);

        ScreenshotBehavior.SetSelectionResizeMode(CaptureResizeModeCombo.SelectedIndex == 1
            ? ScreenshotBehavior.ResizeFollowPosition
            : ScreenshotBehavior.ResizeFollowMovement);

        ScreenshotBehavior.SetHidpiImageScaling(HidpiKey(CaptureHidpiCombo.SelectedIndex));

        ScreenshotBehavior.SetPdfPageSize(CapturePdfPageSizeCombo.SelectedIndex switch
        {
            1 => ScreenshotBehavior.PdfA4Landscape,
            2 => ScreenshotBehavior.PdfImageSize,
            _ => ScreenshotBehavior.PdfA4Portrait,
        });
    }

    /// <summary>自动滚动间隔：失焦写入并回填归一化后的值。</summary>
    private void CaptureScrollInterval_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        CommitScrollInterval();
    }

    private void CaptureScrollInterval_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        CommitScrollInterval();
        e.Handled = true;
    }

    private void CommitScrollInterval()
    {
        int raw = ScreenshotBehavior.DefaultScrollingAutoScrollIntervalMs;
        if (int.TryParse(CaptureScrollIntervalBox.Text.Trim(), out int parsed)) raw = parsed;

        ScreenshotBehavior.SetScrollingAutoScrollIntervalMs(raw);
        CaptureScrollIntervalBox.Text = ScreenshotBehavior.ScrollingAutoScrollIntervalMs
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
