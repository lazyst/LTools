using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拼接窗的三条出口（照上游 <c>ImageStitchingWindow</c> 的 <c>copy()</c> / <c>save()</c> /
/// <c>imageEditing</c>）：复制到剪贴板、另存为文件、交给图片编辑窗。
/// </summary>
/// <remarks>
/// <para>
/// 渲染在后台线程做（<see cref="CollageRender.Render"/> 按行分带，长截图也不会一次吃掉几 GB），
/// 完成回 UI 线程再写剪贴板 / 弹保存框。导出期间 <c>_exporting</c> 关掉画布与表单的编辑，
/// 避免中途改设置让「画出来的是哪一份」说不清。
/// </para>
/// <para>
/// 复制 / 保存成功照上游直接关窗（<see cref="FinishExportAndClose"/>，不走「放弃图像拼接？」确认）；
/// 失败只在页脚说一句、窗口留着，用户还能改设置重试。图像编辑不关窗——编辑窗是另一个窗口，
/// 用户可能还要接着拼。
/// </para>
/// </remarks>
public partial class ImageCollageWindow
{
    private enum ExportKind
    {
        Copy,
        Save,
        Edit,
    }

    // ============ 入口 ============

    private void Copy_Click(object sender, RoutedEventArgs e) => StartExport(ExportKind.Copy);

    private void Save_Click(object sender, RoutedEventArgs e) => StartExport(ExportKind.Save);

    private void Edit_Click(object sender, RoutedEventArgs e) => StartExport(ExportKind.Edit);

    // ============ 导出 ============

    /// <summary>准备成品图（后台）→ 回 UI 线程走对应出口。</summary>
    private void StartExport(ExportKind kind)
    {
        if (_closing || _loading || _exporting || _pdfExporting || _confirming) return;

        if (_doc.OutputType == CollageOutputType.PdfDocument)
        {
            if (kind != ExportKind.Copy) StartPdfExport();
            return;
        }

        var layout = _doc.Layout;
        if (!layout.IsValid)
        {
            SetStatus("没有可拼接的图片", StatusKind.Error);
            return;
        }

        // 快照：UUID 与像素都不会变，导出期间就算有图被删也不会画到半张。
        var items = new List<CollageItem>(_doc.Items);
        var settings = _doc.Settings;

        _exporting = true;
        SetStatus("正在准备图像拼接…", StatusKind.Progress);
        UpdateChrome();

        Task.Run(() => CollageRender.Render(items, settings, layout)).ContinueWith(task =>
        {
            var image = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            var error = task.Exception?.GetBaseException();

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing) return;

                _exporting = false;
                if (image == null)
                {
                    UpdateChrome();
                    SetStatus("无法准备图像拼接图像以供导出", StatusKind.Error);
                    if (error != null)
                        AppLog.Warn("CapturePro.Collage", "拼接导出失败：" + error.Message);
                    return;
                }

                switch (kind)
                {
                    case ExportKind.Copy:
                        CopyImage(image);
                        break;
                    case ExportKind.Save:
                        SaveImage(image);
                        break;
                    default:
                        EditImage(image);
                        break;
                }
            }));
        });
    }

    private void CopyImage(BitmapSource image)
    {
        SetStatus("正在复制图像拼接…", StatusKind.Progress);

        if (CollageExport.Copy(image))
        {
            FinishExportAndClose();
            return;
        }

        UpdateChrome();
        SetStatus("图像拼接过大，无法复制到剪贴板。请保存为文件", StatusKind.Error);
    }

    private void SaveImage(BitmapSource image)
    {
        if (CollageExport.Save(this, image))
        {
            FinishExportAndClose();
            return;
        }

        // 用户取消保存：窗口与编辑都留着（照上游：没导出成功就不关窗）。
        UpdateChrome();
        SetStatus("", StatusKind.None);
    }

    private void EditImage(BitmapSource image)
    {
        UpdateChrome();
        SetStatus("", StatusKind.None);

        if (!ImageEditSession.OpenImage(image, "图像拼接"))
            SetStatus("无法打开图像编辑窗", StatusKind.Error);
    }

    /// <summary>导出成功后照上游关窗：跳过「放弃此图像拼接？」确认，顺手把设置写盘。</summary>
    private void FinishExportAndClose()
    {
        SetStatus("", StatusKind.None);
        _closing = true;
        ++_generation;
        FlushSettings();
        Close();
    }
}
