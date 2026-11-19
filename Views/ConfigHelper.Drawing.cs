using System.Windows;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「通用 → 截图 Pro · 绘制行为」的六个设置（照上游 <c>drawing/*</c>）。
/// </summary>
/// <remarks>
/// 读写与默认值都在 <see cref="DrawingSettings"/>（落 <c>configs/settings.json</c> 的
/// <c>Drawing</c> 段），两份工具清单在 <see cref="CaptureToolListWindow"/>，
/// 行为在三个画布（截图覆盖层 / 图片编辑窗 / 全屏画布）。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>把六个控件刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateDrawing()
    {
        CaptureAutoSelectBox.IsChecked = DrawingSettings.AutoSelectNew;
        CaptureDeleteTextAfterCursorBox.IsChecked = DrawingSettings.DeleteTextAfterCursor;
        CaptureRememberToolBox.IsChecked = DrawingSettings.RememberLastTool;
        CaptureAlwaysShowFirstBox.IsChecked = DrawingSettings.AlwaysShowFirstGroup;

        CaptureAutoSelectExcludedHint.Text = Summary(DrawingSettings.AutoSelectExcluded);
        CaptureQuickSelectionDisabledHint.Text = Summary(DrawingSettings.QuickSelectionDisabled);
    }

    private static string Summary(System.Collections.Generic.IReadOnlyList<string> ids)
    {
        string text = CaptureToolIds.Describe(ids);
        return text.Length == 0 ? "（一个都没选：所有工具都参与）" : "现在：" + text;
    }

    private void CaptureDrawing_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        DrawingSettings.SetAutoSelectNew(CaptureAutoSelectBox.IsChecked == true);
        DrawingSettings.SetDeleteTextAfterCursor(CaptureDeleteTextAfterCursorBox.IsChecked == true);
        DrawingSettings.SetRememberLastTool(CaptureRememberToolBox.IsChecked == true);
        DrawingSettings.SetAlwaysShowFirstGroup(CaptureAlwaysShowFirstBox.IsChecked == true);
    }

    private void CaptureAutoSelectExcluded_Click(object sender, RoutedEventArgs e)
    {
        CaptureToolListWindow.Show(this, quickSelection: false);
        CaptureAutoSelectExcludedHint.Text = Summary(DrawingSettings.AutoSelectExcluded);
    }

    private void CaptureQuickSelectionDisabled_Click(object sender, RoutedEventArgs e)
    {
        CaptureToolListWindow.Show(this, quickSelection: true);
        CaptureQuickSelectionDisabledHint.Text = Summary(DrawingSettings.QuickSelectionDisabled);
    }
}
