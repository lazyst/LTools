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

/// <summary>CaptureResultWindow 的「结果填充」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 结果填充 ============

    private void FillQr(RecognizeResult result)
    {
        QrView.Text = result.QrText;
        QrInfo.Text = result.QrText.Length > 0
            ? string.Format(CultureInfo.CurrentCulture, "格式 {0} · {1} 个字符", result.QrType,
                result.QrText.Length)
            : "这张截图里没有找到二维码。";
    }

    /// <summary>状态行：识别完成后的字数 / 选区字数（没有识别完就报引擎与用时）。</summary>
    private void RefreshStatus()
    {
        if (_result == null)
        {
            StatusText.Text = "正在识别…";
            return;
        }

        if (!_result.Ok)
        {
            StatusText.Text = "识别失败";
            return;
        }

        if (_tab == "table")
        {
            if (_tableDocument.IsEmpty)
            {
                StatusText.Text = "没有识别出表格";
                return;
            }

            TableRange range = TableCanvas.Selection;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "表格 {0} 行 × {1} 列 · 选中 {2} 格",
                _tableDocument.RowCount, _tableDocument.ColumnCount,
                range.IsValid ? range.RowCount * range.ColumnCount : 0);
            return;
        }

        if (_tab is "markdown" or "html")
        {
            string name = _tab == "html" ? "HTML" : "Markdown";
            if (_conversion.IsEmpty)
            {
                StatusText.Text = name + " · 没有可转换的内容";
                return;
            }

            string selected = ConversionPage.SelectedText;
            StatusText.Text = selected.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, "{0} · 已选中 {1} 个字符 · 共 {2} 块",
                    name, selected.Length, _conversion.Blocks.Count)
                : string.Format(CultureInfo.CurrentCulture, "{0} · {1} 块 · {2} 个字符 · 可划选后复制",
                    name, _conversion.Blocks.Count, _conversion.Length);
            return;
        }

        if (_tab == "document")
        {
            if (_conversion.IsEmpty)
            {
                StatusText.Text = "文档 · 没有可用的内容";
                return;
            }

            string selected = DocumentView.SelectedText;
            StatusText.Text = selected.Length > 0
                ? string.Format(CultureInfo.CurrentCulture, "文档 · 已选中 {0} 个字符 · 共 {1} 块",
                    selected.Length, _conversion.Blocks.Count)
                : string.Format(CultureInfo.CurrentCulture, "文档 · {0} 块 · {1} 个字符 · 可划选后复制",
                    _conversion.Blocks.Count, _conversion.Length);
            return;
        }

        if (_tab == "latex")
        {
            if (LatexView.Text.Length == 0)
            {
                StatusText.Text = "LaTeX · 没有可整理的源码";
                return;
            }

            int chars = LatexView.SelectionLength;
            StatusText.Text = chars > 0
                ? string.Format(CultureInfo.CurrentCulture, "LaTeX · 已选中 {0} 个字符", chars)
                : "LaTeX · 只给源码（本地不渲染公式）· 可划选后复制";
            return;
        }

        if (_translatedLines != null)
        {
            StatusText.Text = string.Format(CultureInfo.CurrentCulture,
                "译文 · {0} 行 · 目标 {1} · 点「原文」切回",
                _translatedLines.Count, TranslationLanguages.LabelOf(SelectedTarget));
            return;
        }

        if (TranslationError.Length > 0)
        {
            StatusText.Text = "翻译失败：" + TranslationError;
            return;
        }

        if (_model != null && _model.HasSelection)
        {
            int chars = _model.SelectionText().Length;
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, "已选中 {0} 个字符 · 共 {1} 行",
                chars, _layer.Count);
            return;
        }

        StatusText.Text = _layer.Count == 0
            ? "没有识别到文字"
            : string.Format(CultureInfo.CurrentCulture, "共 {0} 行 · 点一下选字，双击选整行，Ctrl+A 全选",
                _layer.Count);
    }

}
