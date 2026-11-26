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

/// <summary>CaptureResultWindow 的「页签」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 页签 ============

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button) return;
        _tab = button.Tag as string ?? "text";

        // XAML 解析期间 IsChecked="True" 会先触发一次，那时后面的兄弟控件还没建出来
        if (OcrScroll == null) return;

        UpdateTabs();
        RefreshStatus();
    }

    private void UpdateTabs()
    {
        // XAML 解析期间 IsChecked="True" 会先触发一次 Checked，那时后面的兄弟控件还没建出来
        if (OcrScroll is not { } ocrScroll || OriginalToggle is not { } originalToggle ||
            FillToggle is not { } fillToggle ||
            CopyBtn is not { } copyBtn || EmptyText is not { } emptyText)
        {
            return;
        }

        bool text = _tab == "text";
        bool conversion = _tab is "markdown" or "html";
        bool document = _tab == "document";
        bool latex = _tab == "latex";

        ocrScroll.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        TablePage.Visibility = _tab == "table" ? Visibility.Visible : Visibility.Collapsed;
        QrPage.Visibility = _tab == "qr" ? Visibility.Visible : Visibility.Collapsed;
        ConversionPage.Visibility = conversion ? Visibility.Visible : Visibility.Collapsed;
        DocumentPage.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
        LatexPage.Visibility = latex ? Visibility.Visible : Visibility.Collapsed;

        // LaTeX 源码跟着「当前的识别文字」走（用户在文字页改过字就重新挑公式）
        if (latex)
        {
            RefreshLatex();
            LatexView.Focus();
        }

        // 只在内容 / 页真的变了时重渲染：翻开关不该把用户刚划好的选区清掉
        if (conversion &&
            (!ReferenceEquals(ConversionPage.Content, _conversion) || _renderedConversion != _tab))
        {
            ConversionPage.Render(_conversion, _tab == "html"
                ? "没有可转换成 HTML 的内容（这张截图没有识别到文字）。"
                : "没有可转换成 Markdown 的内容（这张截图没有识别到文字）。");
            _renderedConversion = _tab;
        }

        if (document && (!_documentRendered || !ReferenceEquals(DocumentView.Content, _conversion)))
        {
            DocumentView.Render(_conversion, "这张截图没有识别到文字，无法生成文档。");
            _documentRendered = true;
        }

        DocExportBtn.IsEnabled = !_conversion.IsEmpty;
        DocOpenBtn.IsEnabled = !_conversion.IsEmpty;
        DocInfo.Text = _conversion.IsEmpty
            ? "没有可导出的内容"
            : string.Format(CultureInfo.CurrentCulture, "{0} 块 · {1} 个字符",
                _conversion.Blocks.Count, _conversion.Length);

        emptyText.Visibility = text && _layer.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 原图对照跟页签无关：表格 / Markdown / 二维码页也能一边看原文一边看结果
        originalToggle.IsEnabled = _image != null;
        fillToggle.IsEnabled = text;

        copyBtn.Content = _tab switch
        {
            "table" => "复制表格",
            "qr" => "复制二维码内容",
            "markdown" => "复制 Markdown",
            "html" => "复制 HTML",
            "latex" => "复制公式源码",
            "document" => "复制文档",
            _ => CopyLabelForText(),
        };

        copyBtn.IsEnabled = latex
            ? LatexView.Text.Length > 0
            : conversion || document
                ? !_conversion.IsEmpty
                : _result is { Ok: true } || _layer.Count > 0;

        // 翻译只服务文字页：别的页签把这一组收起（不放死按钮）
        UpdateTranslateBar();
        UpdateTransformBar();

        // 表格页时贴选区摆好的窗口可能装不下整张表：切过去先把画布宽度对齐视口
        if (_tab == "table")
        {
            TableScroll.UpdateLayout();
            TableCanvas.SetViewportWidth(TableScroll.ViewportWidth);
            RefreshTableCommands();
        }
    }

}
