using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的导出与导入入口：复制 / 快速保存 / 另存为 / 贴到屏幕 / 打印、粘贴、拖放。
/// </summary>
/// <remarks>
/// 成品一律走 <see cref="ImageEditDocument.Render"/>（底图 + 标注**重绘**），
/// 落盘 / 剪贴板 / 打印复用 <see cref="CaptureSave"/> 与 <see cref="NativeClipboard"/>；
/// 成功导出后调 <see cref="ImageEditDocument.MarkSaved"/>（照上游 <c>markExported</c>，
/// 复制也算「已经导出」，关窗不再追问）。
/// </remarks>
public partial class ImageEditWindow
{
    private BitmapSource? RenderImage()
    {
        if (!_doc.HasImage) return null;

        try
        {
            return _doc.Render();
        }
        catch (Exception ex)
        {
            AppLog.Warn("ImageEditWindow.Render", ex.Message);
            Notice("导出失败：" + ex.Message);
            return null;
        }
    }

    private void CopyImage()
    {
        if (RenderImage() is not { } image) return;

        if (!NativeClipboard.SetImage(image))
        {
            Notice("复制失败：剪贴板被占用");
            return;
        }

        _doc.MarkSaved();
        Notice("已复制到剪贴板");
        UpdateChrome();
    }

    private void QuickSave()
    {
        if (RenderImage() is not { } image) return;

        if (CaptureSave.TryQuickSave(image, out string path))
        {
            _doc.MarkSaved();
            Notice("已保存到 " + System.IO.Path.GetFileName(path));
            UpdateChrome();
        }
        else
        {
            Notice("保存失败");
        }
    }

    /// <summary>另存为；返回 true = 真的存下去了（取消 / 失败返回 false）。</summary>
    private bool SaveAs()
    {
        if (RenderImage() is not { } image) return false;

        // 默认文件名跟着源文件（照上游：保存对话框预填原图名字），剪贴板 / 历史类没有源文件就用时间戳
        string? suggested = string.IsNullOrWhiteSpace(_doc.SourcePath)
            ? null
            : System.IO.Path.GetFileNameWithoutExtension(_doc.SourcePath);

        if (!CaptureSave.TrySave(this, image, suggested))
        {
            Notice("没有保存");
            return false;
        }

        _doc.MarkSaved();
        Notice("已保存");
        UpdateChrome();
        return true;
    }

    private void PinImage()
    {
        if (RenderImage() is not { } image) return;

        double scale = PinClipboard.ScreenScale();
        var workArea = PinClipboard.WorkAreaDip(scale);
        CapturePinManager.PinClipboardImage(image, scale, workArea, PinStore.SourceEdit);

        _doc.MarkSaved();
        Notice("已贴到屏幕");
        UpdateChrome();
    }

    private void PrintImage()
    {
        if (RenderImage() is not { } image) return;

        if (!CaptureSave.TryPrint(this, image))
        {
            Notice("没有打印");
            return;
        }

        _doc.MarkSaved();
        Notice("已送去打印");
        UpdateChrome();
    }

    // ============ 粘贴 / 拖放 ============

    /// <summary>Ctrl+V：剪贴板里的图替换当前窗口（有改动先问），复制的图片文件第一张开这里。</summary>
    private void PasteFromClipboard()
    {
        if (!_doc.HasImage)
        {
            if (LoadClipboard())
            {
                Notice("");
                return;
            }

            Notice("剪贴板里没有图片");
            return;
        }

        if (!ConfirmReplace()) return;

        if (LoadClipboard()) return;
        Notice("剪贴板里没有图片");
    }

    private bool LoadClipboard()
    {
        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            Apply(image, "剪贴板图片", null);
            return true;
        }

        if (NativeClipboard.TryGetFileList(out var files))
        {
            var images = files.Where(PinFiles.IsImage).ToList();
            if (images.Count > 0)
            {
                PresentFile(images[0]);
                if (images.Count > 1) ImageEditSession.OpenFiles(images.Skip(1).ToList());
                return true;
            }
        }

        return false;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        ImageEditSession.OpenFiles(paths);
    }
}
