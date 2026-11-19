using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>另存为支持的图片格式（WPF 自带编码器里这四种够用，WebP / AVIF / PDF 不做）。</summary>
internal enum SaveFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff,
}

/// <summary>
/// 截图落盘的唯一实现：另存为对话框、快速保存、打印，贴图窗口的右键菜单也走这里。
/// </summary>
/// <remarks>
/// 取消 / 失败都返回 false，调用方自己决定留在原地还是继续；
/// 失败只 toast（<c>logs/ltools-*.log</c> 里有堆栈），不弹阻塞框。
/// 编码是纯函数（<see cref="Encode"/> / <see cref="Scaled"/>），<c>--selftest</c> 直接断言文件头与像素尺寸。
/// </remarks>
internal static class CaptureSave
{
    /// <summary>弹「另存为」对话框（预览 + 格式 / 尺寸 / 质量）；返回 true = 真的存下去了。</summary>
    /// <param name="suggestedName">默认文件名（不含扩展名）；空 = 按「截图输出」里的模板生成。</param>
    /// <param name="names">文件名模板里 <c>{AN}</c> / <c>{WN}</c> 的取值（抓图那一刻的前台程序 / 窗口）。</param>
    public static bool TrySave(Window? owner, BitmapSource image, string? suggestedName = null,
        ExportNameContext names = default)
    {
        if (ExportSettings.UseSystemDialog)
            return TrySaveSystem(owner, image, suggestedName, names);

        var dialog = new CaptureSaveWindow(image, suggestedName, names);
        if (owner != null) dialog.Owner = owner;

        dialog.ShowDialog();
        if (!dialog.Saved) return false;   // 取消 = 留在覆盖层继续改

        ToastService.Show($"{Path.GetFileName(dialog.SavedPath)} · {CaptureCatalog.FormatBytes(new FileInfo(dialog.SavedPath).Length)}",
            "已保存", 3000, ToastKind.success, key: "capture-save",
            onClick: () => RevealInExplorer(dialog.SavedPath), actionText: "打开文件夹");
        return true;
    }

    /// <summary>
    /// 「另存为」走 Windows 自带对话框（设置里 <c>save_as_file_dialog = system</c>）。
    /// </summary>
    /// <remarks>
    /// 格式跟着扩展名走（用户把名字改成 <c>x.jpg</c> 就按 JPEG 编码）；
    /// 没写扩展名就补当前设置的格式。取消 = 返回 false，调用方留在原地。
    /// </remarks>
    private static bool TrySaveSystem(Window? owner, BitmapSource image, string? suggestedName,
        ExportNameContext names)
    {
        var format = ExportSettings.CurrentFormat;
        string suggested = string.IsNullOrWhiteSpace(suggestedName)
            ? ExportNames.FileStem(ExportSettings.ManualNameFormat, DateTime.Now, names)
            : suggestedName!;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存截图",
            InitialDirectory = Directory.Exists(ExportSettings.Directory)
                ? ExportSettings.Directory
                : DefaultFolder,
            FileName = suggested,
            DefaultExt = Extension(format),
            Filter = "PNG 图片|*.png|JPEG 图片|*.jpg;*.jpeg|BMP 图片|*.bmp|TIFF 图片|*.tif;*.tiff",
            FilterIndex = format switch
            {
                SaveFormat.Jpeg => 2,
                SaveFormat.Bmp => 3,
                SaveFormat.Tiff => 4,
                _ => 1,
            },
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(owner) != true) return false;

        try
        {
            var chosen = FormatOfExtension(Path.GetExtension(dialog.FileName));
            using var stream = File.Create(dialog.FileName);
            Encode(stream, image, chosen, ExportSettings.Quality, 100);
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.SaveSystem", ex);
            TrayService.Notify("保存失败：" + ex.Message);
            return false;
        }

        ToastService.Show($"{Path.GetFileName(dialog.FileName)} · {CaptureCatalog.FormatBytes(new FileInfo(dialog.FileName).Length)}",
            "已保存", 3000, ToastKind.success, key: "capture-save",
            onClick: () => RevealInExplorer(dialog.FileName), actionText: "打开文件夹");
        return true;
    }

    /// <summary>扩展名 → 格式（认不出就按当前设置的格式）。</summary>
    internal static SaveFormat FormatOfExtension(string? extension) =>
        (extension ?? "").Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => SaveFormat.Jpeg,
            "bmp" => SaveFormat.Bmp,
            "tif" or "tiff" => SaveFormat.Tiff,
            "png" => SaveFormat.Png,
            _ => ExportSettings.CurrentFormat,
        };

    /// <summary>默认落点（「截图输出」里的保存目录，没设就是系统「图片」文件夹）。</summary>
    public static string DefaultFolder => ExportSettings.Directory;

    /// <summary>默认文件名（不含扩展名）：手动另存的模板 + 现在的时间。</summary>
    public static string DefaultName => DefaultNameFor(default);

    /// <summary>同上，带 <c>{AN}</c> / <c>{WN}</c> 的上下文。</summary>
    public static string DefaultNameFor(ExportNameContext names) =>
        ExportNames.FileStem(ExportSettings.ManualNameFormat, DateTime.Now, names);

    /// <summary>扩展名（带点，小写）。</summary>
    public static string Extension(SaveFormat format) => format switch
    {
        SaveFormat.Jpeg => ".jpg",
        SaveFormat.Bmp => ".bmp",
        SaveFormat.Tiff => ".tiff",
        _ => ".png",
    };

    public static string Label(SaveFormat format) => format switch
    {
        SaveFormat.Jpeg => "JPEG",
        SaveFormat.Bmp => "BMP",
        SaveFormat.Tiff => "TIFF",
        _ => "PNG",
    };

    /// <summary>按百分比算出输出像素尺寸（至少 1×1）；100% 就是原尺寸。</summary>
    public static (int Width, int Height) OutputSize(BitmapSource image, double percent)
    {
        double factor = percent > 0 ? percent / 100 : 1;
        return (Math.Max(1, (int)Math.Round(image.PixelWidth * factor)),
            Math.Max(1, (int)Math.Round(image.PixelHeight * factor)));
    }

    /// <summary>按百分比缩小（100% 原样返回同一个实例，不白复制一份）。</summary>
    public static BitmapSource Scaled(BitmapSource image, double percent)
    {
        if (percent >= 99.5 || percent <= 0) return image;

        var scaled = new TransformedBitmap(image, new ScaleTransform(percent / 100, percent / 100));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>编码写进流：格式决定编码器，<paramref name="quality"/> 只对 JPEG 生效。</summary>
    /// <remarks>PNG 走自己写的 <see cref="PngWriter"/>（「压缩力度」才真的有用），其余用 WPF 编码器。</remarks>
    public static void Encode(Stream stream, BitmapSource image, SaveFormat format, int quality, double percent)
    {
        var output = Scaled(image, percent);

        if (format == SaveFormat.Png)
        {
            byte[] png = PngWriter.Encode(output, ExportSettings.CurrentCompression);
            stream.Write(png, 0, png.Length);
            return;
        }

        BitmapEncoder encoder = format switch
        {
            SaveFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            SaveFormat.Bmp => new BmpBitmapEncoder(),
            SaveFormat.Tiff => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => new PngBitmapEncoder(),
        };

        encoder.Frames.Add(BitmapFrame.Create(output));
        encoder.Save(stream);
    }

    /// <summary>编码到内存（自检断言文件头用；也是「估算大小」的现成实现）。</summary>
    public static byte[] EncodeBytes(BitmapSource image, SaveFormat format, int quality = 90, double percent = 100)
    {
        using var stream = new MemoryStream();
        Encode(stream, image, format, quality, percent);
        return stream.ToArray();
    }

    /// <summary>快速保存目录（「截图输出」里的保存目录，默认系统「图片」文件夹）。</summary>
    public static string QuickSaveDir => ExportSettings.Directory;

    /// <summary>
    /// 快速保存：不弹框，按「截图输出」的格式 / 质量 / 文件名模板落盘（Snow Shot 的「快速保存」）。
    /// 成功给一条带「打开文件夹」的 toast，失败只提示、不写半个文件。
    /// </summary>
    public static bool TryQuickSave(BitmapSource image) => TryQuickSave(image, out _);

    /// <summary>同上，带回落盘路径（<c>--selftest</c> 与调用方核对用）。</summary>
    public static bool TryQuickSave(BitmapSource image, out string path) =>
        TryQuickSave(image, out path, default);

    /// <summary>同上，文件名模板里的 <c>{AN}</c> / <c>{WN}</c> 用 <paramref name="names"/>。</summary>
    public static bool TryQuickSave(BitmapSource image, out string path, ExportNameContext names)
    {
        path = "";
        try
        {
            string file = WriteExport(image, ExportSettings.AutoNameFormat, names);
            path = file;
            ToastService.Show($"已保存到 {Path.GetFileName(file)}", "快速保存", 3000, ToastKind.success,
                key: "capture-quicksave", onClick: () => RevealInExplorer(file), actionText: "打开文件夹");
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.QuickSave", ex);
            TrayService.Notify("快速保存失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 「执行完自动保存」：这个场景勾了才写文件（设置里的
    /// <c>auto_save_after_execution_actions</c>）。返回落盘路径，没勾 / 失败返回 null。
    /// </summary>
    /// <remarks>
    /// 失败只记日志 + 一条 warning：复制 / 贴图本身已经成功，不能因为「顺手存一份」失败
    /// 就把主流程说成失败。
    /// </remarks>
    public static string? AutoSave(BitmapSource image, string scenario, ExportNameContext names = default)
    {
        if (!ExportSettings.ShouldAutoSave(scenario)) return null;

        try
        {
            return WriteExport(image, ExportSettings.AutoNameFormat, names);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.AutoSave", $"自动保存失败（{scenario}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 复制截图到剪贴板：顺带处理「执行完自动保存」与「把图片文件也复制到剪贴板」。
    /// </summary>
    /// <remarks>
    /// 两件事共用**同一份**导出文件（照上游）：先按自动保存的文件名模板写一份，
    /// 只有 <c>copy_image_file_to_clipboard</c> 打开时才把它的 CF_HDROP 一起放进去。
    /// 写文件失败不影响图片本身进剪贴板。
    /// </remarks>
    public static bool CopyImage(BitmapSource image, string scenario, ExportNameContext names = default)
    {
        string? file = null;
        if (ExportSettings.CopyImageFile || ExportSettings.ShouldAutoSave(scenario))
        {
            try { file = WriteExport(image, ExportSettings.AutoNameFormat, names); }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.CopyImage", "导出文件失败：" + ex.Message);
            }
        }

        try
        {
            if (file != null && ExportSettings.CopyImageFile)
                return NativeClipboard.SetImageAndFiles(image, new[] { file });
            return NativeClipboard.SetImage(image);
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.CopyImage", ex);
            return false;
        }
    }

    /// <summary>按「截图输出」写一份文件（格式 / 质量 / 压缩 / 目录 / 模板），返回绝对路径。</summary>
    public static string WriteExport(BitmapSource image, string nameFormat, ExportNameContext names)
    {
        string dir = ExportSettings.Directory;
        Directory.CreateDirectory(dir);

        var format = ExportSettings.CurrentFormat;
        string stem = ExportNames.FileStem(nameFormat, DateTime.Now, names);
        string file = UniquePath(dir, ExportNames.Compose(stem, Extension(format)));

        using var stream = File.Create(file);
        Encode(stream, image, format, ExportSettings.Quality, 100);
        return file;
    }

    /// <summary>
    /// 批量另存：选一个文件夹，逐个写成 png（贴图多选后的「另存所选」）。
    /// 返回 true = 真的存下去了；取消 / 失败都留在原地。
    /// </summary>
    public static bool TrySaveMany(Window? owner, IReadOnlyList<BitmapSource> images)
    {
        if (images.Count == 0) return false;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "另存所选贴图到文件夹",
            InitialDirectory = DefaultFolder,
            Multiselect = false,
        };

        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return false;

        try
        {
            var saved = SaveManyTo(dialog.FolderName, images);
            ToastService.Show($"已保存 {saved.Count} 张到 {Path.GetFileName(dialog.FolderName)}",
                "批量另存", 3000, ToastKind.success, key: "capture-save-many",
                onClick: () => RevealInExplorer(saved[0]), actionText: "打开文件夹");
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.SaveMany", ex);
            TrayService.Notify("批量另存失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 批量另存的落盘部分（不弹框）：<paramref name="dir"/> 下按「贴图-时间戳-N.png」逐个写，
    /// 同秒重名自动加序号。返回落盘的路径，异常交给调用方（<c>--selftest</c> 直接断言文件与尺寸）。
    /// </summary>
    public static IReadOnlyList<string> SaveManyTo(string dir, IReadOnlyList<BitmapSource> images)
    {
        Directory.CreateDirectory(dir);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var saved = new List<string>(images.Count);

        for (int i = 0; i < images.Count; i++)
        {
            string file = UniquePath(dir, $"贴图-{stamp}-{i + 1}.png");
            using (var stream = File.Create(file))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(images[i]));
                encoder.Save(stream);
            }

            saved.Add(file);
        }

        return saved;
    }

    /// <summary>同目录下不重名的路径（同一秒连按两次不会覆盖前一张）。</summary>
    public static string UniquePath(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            path = Path.Combine(dir, $"{stem}-{i}{ext}");
            if (!File.Exists(path)) return path;
        }
        return Path.Combine(dir, $"{stem}-{DateTime.Now:ffff}{ext}");
    }

    private static void RevealInExplorer(string file)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Reveal", ex);
        }
    }

    /// <summary>
    /// 打印：系统打印框取消 = 返回 false（调用方留在覆盖层继续改）。
    /// 图片按比例缩进可打印区并居中，不裁、不拉伸。
    /// </summary>
    public static bool TryPrint(Window? owner, BitmapSource image)
    {
        try
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            bool? ok = dialog.ShowDialog();
            if (ok != true) return false;

            double areaW = Math.Max(1, dialog.PrintableAreaWidth);
            double areaH = Math.Max(1, dialog.PrintableAreaHeight);
            double w = Math.Max(1, image.Width);
            double h = Math.Max(1, image.Height);
            double fit = Math.Min(areaW / w, areaH / h);

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                double dw = w * fit, dh = h * fit;
                dc.DrawImage(image, new Rect((areaW - dw) / 2, (areaH - dh) / 2, dw, dh));
            }

            dialog.PrintVisual(visual, "LTools 截图");
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Print", ex);
            TrayService.Notify("打印失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>弹另存为框把录屏成品（mp4 / gif）复制出去；返回 true = 真的存下去了。</summary>
    public static bool TrySaveFile(Window? owner, string source, string extension)
    {
        if (!File.Exists(source))
        {
            TrayService.Notify("录制文件不在了：" + source);
            return false;
        }

        string ext = string.IsNullOrWhiteSpace(extension) ? "mp4" : extension.Trim().ToLowerInvariant();
        string label = ext == "gif" ? "GIF 动图 (*.gif)|*.gif" : "MP4 视频 (*.mp4)|*.mp4";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = label,
            DefaultExt = "." + ext,
            FileName = $"录屏-{DateTime.Now:yyyyMMdd-HHmmss}.{ext}",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            OverwritePrompt = true,
        };

        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return false;

        try
        {
            File.Copy(source, dialog.FileName, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.SaveFile", ex);
            TrayService.Notify("保存录屏失败：" + ex.Message);
            return false;
        }
    }
}
