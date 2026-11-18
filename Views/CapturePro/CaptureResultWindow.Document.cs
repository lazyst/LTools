using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;
using Microsoft.Win32;

namespace LTools.Views.CapturePro;

/// <summary>
/// 识别结果窗的文档页：复制富文本、导出（docx / pdf / md / html / tex）、用默认程序打开。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的文档模式出口（<c>screenshotdocumentexport</c> / <c>screenshotrecognitionfileexport</c>）：
/// 上游复制整份文档是「HTML + 图片」的富文本，选中一段就复制那一段；导出走同一个保存对话框，
/// 按扩展名选 Word / PDF / Markdown / HTML / LaTeX；「打开文档」是导一份临时 docx 交给默认程序。
/// </para>
/// <para>
/// 这里的内容来自 OCR 派生的块（<see cref="ConversionDocument"/>），没有云端模型给的图片与公式；
/// PDF 走系统浏览器的 headless 打印（见 <see cref="DocumentExport"/> 的说明）。
/// </para>
/// </remarks>
public partial class CaptureResultWindow
{
    private const string DocumentTemporaryFolder = "ltools-capture-doc";

    /// <summary>最近一次导出失败的原因（冒烟读它做断言）。</summary>
    internal string DocumentError { get; private set; } = "";

    /// <summary>
    /// 复制文档（照上游 <c>documentCopyRequest</c>）：选了复制选中那段，没选复制整份。
    /// 富文本写 HTML + 纯文本两份，粘到 Word / 邮件里保留标题与表格。
    /// </summary>
    private void CopyDocument()
    {
        if (_conversion.IsEmpty)
        {
            ToastService.Show("没有可复制的文档内容", "识别结果", 1600);
            return;
        }

        string selected = DocumentView.SelectedText;
        string text = selected.Length > 0 ? selected : _conversion.PlainText;
        string html = selected.Length > 0
            ? "<p>" + EscapeHtml(selected).Replace("\r\n", "<br>").Replace("\n", "<br>") + "</p>"
            : _conversion.Html;

        if (text.Length == 0)
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        if (NativeClipboard.SetHtmlAndText(html, text))
        {
            ToastService.Show(selected.Length > 0 ? "已复制选中那段" : "已复制整份文档", "识别结果", 1600);
        }
        else
        {
            ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
        }
    }

    private void DocExport_Click(object sender, RoutedEventArgs e) => DocExport();

    private void DocExport()
    {
        if (_conversion.IsEmpty) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出文档",
            FileName = "识别文档.docx",
            DefaultExt = ".docx",
            Filter = DocumentExport.Filter,
            AddExtension = true,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        // 用户可能在下拉里换了类型：按最终扩展名定格式，再对齐扩展名
        string path = DocumentExport.NormalizePath(dialog.FileName, DocumentExport.KindOf(dialog.FileName));
        if (!ExportDocument(path, out string error))
        {
            ToastService.Show("导出失败：" + error, "识别结果", 0, ToastKind.error);
            return;
        }

        string saved = path;
        ToastService.Show($"已导出 {Path.GetFileName(saved)}", "识别结果", 6000, ToastKind.success,
            onClick: () => OpenDocumentFile(saved), actionText: "打开文件");
    }

    /// <summary>
    /// 导出到指定路径（保存对话框与冒烟共用）。按扩展名定格式，失败时把原因写进
    /// <see cref="DocumentError"/> 并返回 false。
    /// </summary>
    internal bool ExportDocument(string path)
    {
        bool ok = ExportDocument(path, out string error);
        DocumentError = error;
        return ok;
    }

    private bool ExportDocument(string path, out string error)
    {
        DocumentExportKind kind = DocumentExport.KindOf(path);
        bool ok = DocumentExport.Write(kind, _conversion, path, out error);
        DocumentError = error;
        if (!ok)
        {
            AppLog.Warn("CapturePro.Document", "导出失败：" + error);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 导一份临时 docx 并返回路径（「打开文档」与冒烟共用；不启动任何程序）。
    /// 上游 <c>openTemporaryDocument</c> 也是「写临时文件 → 交给系统默认程序」。
    /// </summary>
    internal string? PrepareDocumentFile()
    {
        if (_conversion.IsEmpty) return null;

        byte[]? bytes = DocumentExport.Docx(_conversion);
        if (bytes == null) return null;

        try
        {
            string folder = Path.Combine(Path.GetTempPath(), DocumentTemporaryFolder);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "识别文档-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".docx");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            DocumentError = ex.Message;
            AppLog.Warn("CapturePro.Document", "写临时文档失败：" + ex.Message);
            return null;
        }
    }

    private void DocOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_conversion.IsEmpty) return;

        string? path = PrepareDocumentFile();
        if (path == null)
        {
            ToastService.Show("打开失败：" + (DocumentError.Length > 0 ? DocumentError : "文档内容为空"),
                "识别结果", 0, ToastKind.error);
            return;
        }

        if (!OpenDocumentFile(path))
            ToastService.Show("没有可以打开 docx 的程序", "识别结果", 2400, ToastKind.warning);
    }

    private static bool OpenDocumentFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Document", "打开文档失败：" + ex.Message);
            return false;
        }
    }
}
