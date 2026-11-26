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

/// <summary>CaptureResultWindow 的「键盘 / 输入」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 键盘 / 输入 ============

    private bool TextEditable => _tab == "text" &&
                                 _translatedLines == null;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 表格单元格正在编辑时 Esc 只退出这一格（上游 QTableView 的语义），不关窗
            if (TableCanvas is { IsEditing: true })
            {
                TableCanvas.CancelEdit();
                e.Handled = true;
                return;
            }

            // 原文预览开着：Esc 先关它（跟「关掉最上面那层」的直觉一致）
            if (OriginalToggle.IsChecked == true)
            {
                OriginalToggle.IsChecked = false;
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
            return;
        }

        if (_tab == "table" && HandleTableKey(e))
        {
            e.Handled = true;
            return;
        }

        if (_tab == "table")
        {
            if (HandleTableKey(e)) e.Handled = true;
            return;
        }

        // Markdown / HTML / 文档：预览自己管滚动与选中，只补 Ctrl+A / Ctrl+C
        // （FlowDocumentScrollViewer 不带全选）
        if (_tab is "markdown" or "html" or "document")
        {
            CaptureConversionView view = _tab == "document" ? DocumentView : ConversionPage;
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && e.Key == Key.A)
            {
                view.SelectAllText();
                e.Handled = true;
                RefreshStatus();
            }
            else if (control && e.Key == Key.C)
            {
                CopyCurrent();
                e.Handled = true;
            }

            return;
        }

        if (_tab == "latex")
        {
            // 只读文本框：全选 / 复制由这里给命令（没焦点时 TextBox 自己的 Ctrl+A 不会生效）
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (control && e.Key == Key.A)
            {
                LatexView.Focus();
                LatexView.SelectAll();
                e.Handled = true;
                RefreshStatus();
            }
            else if (control && e.Key == Key.C)
            {
                CopyLatex();
                e.Handled = true;
            }

            return;
        }

        if (_model == null || !TextEditable)
        {
            // 译文模式只读：只保留 Ctrl+C（复制译文），别的编辑键都不认
            if (_translatedLines != null && e.Key == Key.C &&
                (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopyCurrent();
                e.Handled = true;
            }

            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.A when ctrl:
                _model.SelectAll();
                break;
            case Key.C when ctrl:
                Copy(_model.HasSelection ? _model.SelectionText() : _model.AllText, "识别文字");
                break;
            case Key.X when ctrl:
                if (!_model.HasSelection) break;
                Copy(_model.SelectionText(), "识别文字");
                _model.DeleteSelection();
                break;
            case Key.V when ctrl:
                if (NativeClipboard.TryGetText(out string pasted) && pasted.Length > 0)
                    _model.InsertText(pasted);
                break;
            case Key.Z when ctrl:
                if (shift) _model.Redo();
                else _model.Undo();
                break;
            case Key.Y when ctrl:
                _model.Redo();
                break;
            case Key.Back:
                _model.Backspace();
                break;
            case Key.Delete:
                _model.DeleteForward();
                break;
            case Key.Left:
                _model.MoveCaret(-1, shift);
                break;
            case Key.Right:
                _model.MoveCaret(1, shift);
                break;
            case Key.Up:
                _model.MoveLine(-1, shift);
                break;
            case Key.Down:
                _model.MoveLine(1, shift);
                break;
            case Key.Enter:
                _model.InsertText("\n");
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_model == null || !TextEditable) return;
        if (string.IsNullOrEmpty(e.Text)) return;
        if (char.IsControl(e.Text[0]) && e.Text != "\n") return;

        _model.InsertText(e.Text);
        e.Handled = true;
    }

}
