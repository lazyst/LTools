using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 设置「截图输出」页：格式 / 质量 / 压缩 / 目录 / 文件名模板 / 另存为对话框 / 自动保存场景。
/// </summary>
/// <remarks>
/// 值的真源是 <see cref="ExportSettings"/>（运行期缓存，磁盘形状见 <c>Config/Export.cs</c>）；
/// 改一项走一次 <see cref="ExportSettings.Save"/>（只写 <c>configs/settings.json</c>，后台落盘）。
/// 回填期间用 <c>_initializing</c> 挡住控件事件，免得「打开页面」被当成「用户改了设置」。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>按当前设置回填这一页（切页时调一次）。</summary>
    private void PopulateExport()
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        try
        {
            ExportDirBox.Text = ExportSettings.SaveDirectory;
            SelectByTag(ExportFormatCombo, ExportSettings.Format);
            SelectByTag(ExportCompressionCombo, ExportSettings.CompressionKey(ExportSettings.Compression));
            SelectByTag(ExportDialogCombo, ExportSettings.SaveAsDialog);

            ExportQualityBox.Text = ExportSettings.Quality.ToString();
            ExportManualNameBox.Text = ExportSettings.ManualNameFormat;
            ExportAutoNameBox.Text = ExportSettings.AutoNameFormat;

            ExportShowResultBox.IsChecked = ExportSettings.ShowSaveResult;
            ExportCopyFileBox.IsChecked = ExportSettings.CopyImageFile;

            foreach (var row in new[] { ExportAutoCopyBox, ExportAutoScrollingBox, ExportAutoPinnedBox,
                                        ExportAutoMonitorBox, ExportAutoWindowBox, ExportAutoPinBox })
            {
                string key = row.Tag as string ?? "";
                row.IsChecked = key.Length > 0 && ExportSettings.ShouldAutoSave(key);
            }

            UpdateExportQualityEnabled();
        }
        finally { _initializing = wasInitializing; }
    }

    /// <summary>质量只对 JPEG 有效：别的格式把那一行置灰（值仍然保留）。</summary>
    private void UpdateExportQualityEnabled()
    {
        bool jpeg = ExportSettings.CurrentFormat == SaveFormat.Jpeg;
        ExportQualityBox.IsEnabled = jpeg;
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && (item.Tag as string) == tag)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    private static string? TagOf(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    // ============ 目录 ============

    private void ExportDir_LostFocus(object sender, RoutedEventArgs e) => SaveExportDirectory();

    private void ExportDir_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveExportDirectory();
        e.Handled = true;
    }

    private void SaveExportDirectory()
    {
        if (_initializing) return;
        string dir = ExportSettings.NormalizeDirectory(ExportDirBox.Text);
        if (dir == ExportSettings.SaveDirectory) return;

        ExportSettings.Save(() => ExportSettings.SaveDirectory = dir);
        ExportDirBox.Text = dir;
    }

    private void ExportBrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择截图保存目录",
            InitialDirectory = ExportSettings.Directory,
        };
        if (dialog.ShowDialog(this) != true) return;

        string dir = ExportSettings.NormalizeDirectory(dialog.FolderName);
        ExportSettings.Save(() => ExportSettings.SaveDirectory = dir);
        ExportDirBox.Text = dir;
    }

    // ============ 格式 / 质量 / 压缩 ============

    private void ExportFormat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string? tag = TagOf(ExportFormatCombo);
        if (tag == null || tag == ExportSettings.Format) return;

        ExportSettings.Save(() => ExportSettings.Format = tag);
        UpdateExportQualityEnabled();
    }

    private void ExportCompression_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string? tag = TagOf(ExportCompressionCombo);
        if (tag == null || tag == ExportSettings.CompressionKey(ExportSettings.Compression)) return;

        ExportSettings.Save(() => ExportSettings.Compression = ExportSettings.ParseCompression(tag));
    }

    private void ExportDialog_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        string? tag = TagOf(ExportDialogCombo);
        if (tag == null || tag == ExportSettings.SaveAsDialog) return;

        ExportSettings.Save(() => ExportSettings.SaveAsDialog = tag);
    }

    private void ExportQuality_LostFocus(object sender, RoutedEventArgs e) => SaveExportQuality();

    private void ExportQuality_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveExportQuality();
        e.Handled = true;
    }

    private void SaveExportQuality()
    {
        if (_initializing) return;
        int quality = ExportSettings.NormalizeQuality(
            int.TryParse(ExportQualityBox.Text.Trim(), out int parsed) ? parsed : ExportSettings.Quality);
        ExportQualityBox.Text = quality.ToString();

        if (quality == ExportSettings.Quality) return;
        ExportSettings.Save(() => ExportSettings.Quality = quality);
    }

    // ============ 文件名模板 ============

    private void ExportManualName_LostFocus(object sender, RoutedEventArgs e) => SaveExportManualName();

    private void ExportManualName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveExportManualName();
        e.Handled = true;
    }

    private void SaveExportManualName()
    {
        if (_initializing) return;
        string format = ExportSettings.NormalizeNameFormat(ExportManualNameBox.Text);
        if (format == ExportSettings.ManualNameFormat) return;

        ExportSettings.Save(() => ExportSettings.ManualNameFormat = format);
        ExportManualNameBox.Text = format;
    }

    private void ExportAutoName_LostFocus(object sender, RoutedEventArgs e) => SaveExportAutoName();

    private void ExportAutoName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveExportAutoName();
        e.Handled = true;
    }

    private void SaveExportAutoName()
    {
        if (_initializing) return;
        string format = ExportSettings.NormalizeNameFormat(ExportAutoNameBox.Text);
        if (format == ExportSettings.AutoNameFormat) return;

        ExportSettings.Save(() => ExportSettings.AutoNameFormat = format);
        ExportAutoNameBox.Text = format;
    }

    // ============ 保存行为 ============

    private void ExportShowResult_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool value = ExportShowResultBox.IsChecked == true;
        if (value == ExportSettings.ShowSaveResult) return;

        ExportSettings.Save(() => ExportSettings.ShowSaveResult = value);
    }

    private void ExportCopyFileBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool value = ExportCopyFileBox.IsChecked == true;
        if (value == ExportSettings.CopyImageFile) return;

        ExportSettings.Save(() => ExportSettings.CopyImageFile = value);
    }

    // ============ 执行完自动保存 ============

    private void ExportAutoSave_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is not CheckBox box) return;

        string key = box.Tag as string ?? "";
        if (key.Length == 0) return;

        bool value = box.IsChecked == true;
        if (value == ExportSettings.ShouldAutoSave(key)) return;

        ExportSettings.Save(() => ExportSettings.SetAutoSave(key, value));
    }
}
