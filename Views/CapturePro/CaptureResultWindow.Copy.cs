using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>CaptureResultWindow 的「复制 / 关闭」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 复制 / 关闭 ============

    private void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();

    private void CopyCurrent()
    {
        switch (_tab)
        {
            case "table":
                // 有选区就复制选中的格子，没有就整张表（HTML + 文本一起写）
                TableCopySelection();
                break;
            case "qr":
                Copy(_result?.QrText, "二维码内容");
                break;
            case "markdown":
                CopyConversion(asHtml: false);
                break;
            case "html":
                CopyConversion(asHtml: true);
                break;
            case "document":
                CopyDocument();
                break;
            case "latex":
                CopyLatex();
                break;
            default:
                // 译文模式：复制整段译文（逐行）；开了「复制成图片」就把渲染好的图放进剪贴板
                if (_translatedLines != null)
                {
                    if (TranslationStore.CopyAsImage && RenderTranslationImage() is { } image)
                    {
                        if (NativeClipboard.SetImage(image))
                            ToastService.Show("已复制译文图", "识别结果", 1600);
                        else
                            ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0,
                                ToastKind.error);
                        break;
                    }

                    Copy(string.Join("\r\n", _translatedLines), "译文");
                    break;
                }

                // 有选区就复制选中的，没有就整份（与上游识别窗的复制语义一致）
                if (_model == null)
                {
                    Copy(_result?.Text, "识别文字");
                }
                else
                {
                    Copy(_model.HasSelection ? _model.SelectionText() : _model.AllText, "识别文字");
                }

                break;
        }
    }

    private static void Copy(string? text, string what)
    {
        if (string.IsNullOrEmpty(text))
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show($"已复制{what}", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    /// <summary>
    /// 复制 Markdown / HTML（照上游 <c>imageConversionClipboardMimeData</c>）：
    /// **选了就复制选中那段**，没选就复制整份源码；HTML 另外写一份 text/html 剪贴板格式。
    /// </summary>
    private void CopyConversion(bool asHtml)
    {
        if (_conversion.IsEmpty)
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        string name = asHtml ? "HTML" : "Markdown";
        string selected = ConversionPage.SelectedText;

        string text = selected.Length > 0 ? selected : (asHtml ? _conversion.Html : _conversion.Markdown);
        if (text.Length == 0)
        {
            ToastService.Show("没有可复制的内容", "识别结果", 1600);
            return;
        }

        bool ok;
        if (asHtml)
        {
            // 选中那段没有富文本结构，包一层段落再当 HTML 写；整份源码本身就是 HTML
            string html = selected.Length > 0
                ? "<p>" + EscapeHtml(selected).Replace("\r\n", "<br>").Replace("\n", "<br>") + "</p>"
                : _conversion.Html;
            ok = NativeClipboard.SetHtmlAndText(html, text);
        }
        else
        {
            ok = NativeClipboard.SetText(text);
        }

        if (ok) ToastService.Show($"已复制{name}", "识别结果", 1600);
        else ToastService.Show("复制失败（剪贴板被占用）", "识别结果", 0, ToastKind.error);
    }

    private static string EscapeHtml(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 双击标题栏 = 最大化 / 还原（和系统窗口一致）
        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        if (_maximized) return;      // 最大化时标题栏不拖：双击还原，别把窗口拽成半屏

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开（快速点击）：没有可拖的循环，忽略
        }
    }

}
