using System;
using System.IO;
using System.Windows;
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
}
