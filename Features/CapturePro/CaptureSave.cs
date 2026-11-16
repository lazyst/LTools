using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 「另存为 png」的唯一实现：覆盖层的完成动作与贴图窗口的右键菜单都走这里。
/// </summary>
/// <remarks>
/// 取消 / 失败都返回 false，调用方自己决定留在原地还是继续；
/// 失败只 toast（<c>logs/ltools-*.log</c> 里有堆栈），不弹阻塞框。
/// </remarks>
internal static class CaptureSave
{
    /// <summary>弹另存为框并写 png；返回 true = 真的存下去了。</summary>
    public static bool TrySave(Window? owner, BitmapSource image)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            FileName = $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            OverwritePrompt = true,
        };

        bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return false;

        try
        {
            using var stream = File.Create(dialog.FileName);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.Save", ex);
            TrayService.Notify("保存截图失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>快速保存目录：<c>图片\LTools 截图</c>（不读配置，装完就能用）。</summary>
    public static string QuickSaveDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LTools 截图");

    /// <summary>
    /// 快速保存：不弹框，直接按时间戳落盘 png（Snow Shot 的「快速保存」）。
    /// 成功给一条带「打开文件夹」的 toast，失败只提示、不写半个文件。
    /// </summary>
    public static bool TryQuickSave(BitmapSource image) => TryQuickSave(image, out _);

    /// <summary>同上，带回落盘路径（<c>--selftest</c> 与调用方核对用）。</summary>
    public static bool TryQuickSave(BitmapSource image, out string path)
    {
        path = "";
        try
        {
            string dir = QuickSaveDir;
            Directory.CreateDirectory(dir);
            string file = UniquePath(dir, $"截图-{DateTime.Now:yyyyMMdd-HHmmss}.png");

            using (var stream = File.Create(file))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }

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
