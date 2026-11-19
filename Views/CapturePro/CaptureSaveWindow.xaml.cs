using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「另存为」对话框：左预览、右路径 / 文件名 / 格式 / 尺寸 / 质量。
/// </summary>
/// <remarks>
/// 与覆盖层同一套深色外观（不套主题的浅色控件）；格式与尺寸是文字胶囊，点一下就换，不用展开下拉。
/// 写文件与编码都在 <see cref="CaptureSave.Encode"/> 里，窗口只负责校验与提示——
/// 编码是纯函数才能在 <c>--selftest</c> 里断言（文件头 / 缩放后的像素尺寸）。
/// </remarks>
public partial class CaptureSaveWindow : Window
{
    private static string? _lastFolder;      // 本次进程里记住上次存的目录（不落盘）

    private readonly BitmapSource _image;
    private readonly string? _suggestedName;
    private readonly ExportNameContext _names;
    private readonly List<(RadioButton Button, SaveFormat Value)> _formatChips = new();
    private readonly List<(RadioButton Button, double Value)> _scaleChips = new();

    private SaveFormat _format = ExportSettings.CurrentFormat;
    private double _percent = 100;

    /// <param name="suggestedName">默认文件名（不含扩展名）；空 = 按「截图输出」的模板生成。</param>
    /// <param name="names">文件名模板里 <c>{AN}</c> / <c>{WN}</c> 的取值。</param>
    public CaptureSaveWindow(BitmapSource image, string? suggestedName = null,
        ExportNameContext names = default)
    {
        InitializeComponent();
        _image = image;
        _suggestedName = suggestedName;
        _names = names;
    }

    /// <summary>真的写下去了（取消 / 失败都是 false）。</summary>
    public bool Saved { get; private set; }

    /// <summary>写下去的绝对路径（<see cref="Saved"/> 为 true 时有效）。</summary>
    public string SavedPath { get; private set; } = "";

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Preview.Source = _image;
        FolderBox.Text = _lastFolder ?? CaptureSave.DefaultFolder;
        NameBox.Text = string.IsNullOrWhiteSpace(_suggestedName)
            ? CaptureSave.DefaultNameFor(_names)
            : _suggestedName!;

        BuildChips();
        QualityBox.Text = ExportSettings.Quality.ToString();
        UpdateMeta();

        // 文件名默认全选：想改名直接打字，不想改就按 Enter
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void BuildChips()
    {
        foreach (SaveFormat format in Enum.GetValues<SaveFormat>())
        {
            var chip = NewChip(CaptureSave.Label(format), "captureSaveFormat");
            chip.Checked += (_, _) =>
            {
                if (_format == format) return;
                _format = format;
                UpdateMeta();
            };
            chip.IsChecked = _format == format;
            _formatChips.Add((chip, format));
            FormatRow.Children.Add(chip);
        }

        foreach (double percent in new double[] { 100, 75, 50, 25 })
        {
            var chip = NewChip(percent.ToString("0") + "%", "captureSaveScale");
            chip.Checked += (_, _) =>
            {
                if (Math.Abs(_percent - percent) < 1e-9) return;
                _percent = percent;
                UpdateMeta();
            };
            chip.IsChecked = Math.Abs(_percent - percent) < 1e-9;
            _scaleChips.Add((chip, percent));
            ScaleRow.Children.Add(chip);
        }
    }

    private RadioButton NewChip(string text, string group) => new()
    {
        Style = (Style)FindResource("Chip"),
        GroupName = group,
        Content = text,
    };

    /// <summary>扩展名与「输出多大」跟着格式 / 尺寸走。</summary>
    private void UpdateMeta()
    {
        ExtensionText.Text = CaptureSave.Extension(_format);

        var (w, h) = CaptureSave.OutputSize(_image, _percent);
        MetaText.Text = $"{_image.PixelWidth} × {_image.PixelHeight} px → {w} × {h} px";

        // 质量只对 JPEG 有意义：其余格式置灰，避免「调了没反应」
        bool jpeg = _format == SaveFormat.Jpeg;
        QualityBox.IsEnabled = jpeg;
        QualityBox.Opacity = jpeg ? 1.0 : 0.45;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存文件夹",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : CaptureSave.DefaultFolder,
        };

        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string folder = FolderBox.Text.Trim();
        if (folder.Length == 0) { Fail("请填保存文件夹"); return; }

        string name = NameBox.Text.Trim();
        if (name.Length == 0) { Fail("请填文件名"); return; }

        string extension = CaptureSave.Extension(_format);
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^extension.Length].TrimEnd();          // 用户自己带了扩展名，别存成 x.png.png
        if (name.Length == 0) { Fail("请填文件名"); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Fail("文件名里有不能用的字符"); return; }

        int quality = 90;
        if (_format == SaveFormat.Jpeg &&
            (!int.TryParse(QualityBox.Text.Trim(), out quality) || quality < 1 || quality > 100))
        {
            Fail("质量填 1–100");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            Fail("建不了文件夹：" + ex.Message);
            return;
        }

        string path = Path.Combine(folder, name + extension);
        if (File.Exists(path) &&
            !ConfirmDialog.Confirm(this, "保存截图", $"「{name}{extension}」已经存在，覆盖吗？", okText: "覆盖"))
            return;

        try
        {
            using var stream = File.Create(path);
            CaptureSave.Encode(stream, _image, _format, quality, _percent);
        }
        catch (Exception ex)
        {
            Fail("写文件失败：" + ex.Message);
            return;
        }

        _lastFolder = folder;
        Saved = true;
        SavedPath = path;

        // 「保存后显示结果」：留着对话框，把路径与体积显示出来（照 Snow Shot 的 show_save_result）
        if (ExportSettings.ShowSaveResult)
        {
            ShowResult(path);
            return;
        }

        Finish();
    }

    /// <summary>存完之后不关窗：显示结果条，主按钮换成「关闭」（Esc 也能关）。</summary>
    private void ShowResult(string path)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        ResultText.Text = $"已保存 · {Path.GetFileName(path)} · {CaptureCatalog.FormatBytes(new FileInfo(path).Length)}";
        ResultText.ToolTip = path;
        ResultBar.Visibility = Visibility.Visible;

        SaveBtn.Visibility = Visibility.Collapsed;
        CancelBtn.Content = "关闭";
        CancelBtn.Style = (Style)FindResource("PrimaryBtn");
        CancelBtn.IsCancel = false;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SavedPath.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"/select,\"{SavedPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.SaveOpenFolder", ex);
        }
    }

    /// <summary>取消 = 没存就关；已经存过（结果条那一步）就是「关闭」，照样算保存成功。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish();

    /// <summary>关窗：存过就交回 <c>DialogResult = true</c>，没存就是取消。</summary>
    private void Finish()
    {
        if (Saved)
        {
            try { DialogResult = true; }
            catch (InvalidOperationException) { Close(); }   // 非模态打开时 DialogResult 不可用
            return;
        }

        Close();
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>自绘卡片没有标题栏，按住标题那一行拖动窗口。</summary>
    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
        try { DragMove(); }
        catch (InvalidOperationException) { }   // 鼠标已经松开 / 窗口正在关，忽略
    }
}

