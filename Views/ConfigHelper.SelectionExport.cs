using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「截图 Pro → 截图行为 → 选区背景与版面」：选区背景不透明度 +
/// 导出时的版面压缩（照上游 <c>screenshot_selection/background_opacity_percent</c> 与
/// <c>layout_compaction_enabled</c> / <c>layout_compaction_spacing</c>）。
/// </summary>
/// <remarks>
/// 两项都是**导出**选项，落 <c>configs/ui.json</c> 的 <c>CaptureSelection</c>（与圆角 / 阴影同一份），
/// 覆盖层打开时读一次；覆盖层的预览不压缩（见 <c>docs/CAPTURE_PARITY.md</c> §5）。
/// </remarks>
public partial class ConfigHelperWindow
{
    private bool _selectionExportLoading;

    /// <summary>把这一卡片刷成当前设置（调用方负责 <c>_initializing</c>）。</summary>
    private void PopulateSelectionExport()
    {
        _selectionExportLoading = true;
        try
        {
            int opacity = SelectionStore.Style.BackgroundOpacity;
            CaptureBackgroundOpacitySlider.Value = opacity;
            CaptureBackgroundOpacityText.Text = opacity + "%";

            CaptureCompactionBox.IsChecked = SelectionStore.Compaction;
            CaptureCompactionSpacingBox.Text =
                SelectionStore.CompactionSpacing.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _selectionExportLoading = false;
        }
    }

    private void CaptureBackgroundOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        CaptureBackgroundOpacityText.Text = value + "%";
        if (_initializing || _selectionExportLoading) return;
        SelectionStore.SetBackgroundOpacity(value);
    }

    private void CaptureCompaction_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _selectionExportLoading) return;
        SelectionStore.SetCompaction(CaptureCompactionBox.IsChecked == true);
    }

    private void CaptureCompactionSpacing_Changed(object sender, RoutedEventArgs e) =>
        CommitCompactionSpacing();

    private void CaptureCompactionSpacing_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitCompactionSpacing();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitCompactionSpacing()
    {
        if (_initializing || _selectionExportLoading) return;

        string text = CaptureCompactionSpacingBox.Text.Trim();
        int value = SelectionStore.CompactionSpacing;
        if (text.Length > 0 &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            value = parsed;

        value = Math.Clamp(value, 0, LayoutCompaction.MaxSpacing);
        SelectionStore.SetCompactionSpacing(value);
        CaptureCompactionSpacingBox.Text = value.ToString(CultureInfo.InvariantCulture);
    }
}
