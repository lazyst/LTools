using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LTools.Views;

/// <summary>
/// 速记正文的 Markdown 书写辅助：续列表 / 缩进 / 包裹记号 / 一行工具栏。
/// </summary>
/// <remarks>
/// <para>
/// 全部作用在 AvalonEdit 的 <c>TextDocument</c> 上，**不改文件内容模型**：仍然是纯 Markdown 源码，
/// 存盘、预览、搜索都不受影响。工具栏按钮 <c>Focusable=False</c>，点它不会把编辑器的选区 / 光标抢走。
/// </para>
/// <para>
/// Enter 续列表只在「光标在行尾 + 没有选区」时接管，其余情况（行中间回车、选中替换、输入法组字中的 <c>Key.ImeProcessed</c>）
/// 都交回 AvalonEdit 的默认行为。
/// </para>
/// </remarks>
public partial class QuickNoteWindow
{
    private static readonly Regex ListLine = new(@"^(\s*)([-*+]|\d+[.)])(\s+)(\[[ xX]\]\s+)?(.*)$", RegexOptions.Compiled);
    private static readonly Regex QuoteLine = new(@"^(\s*)>\s?(.*)$", RegexOptions.Compiled);

    // ============ 键盘 ============

    private void BodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (!ctrl && !shift && e.Key == Key.Enter)
        {
            if (ContinueListOrQuote()) e.Handled = true;
        }
        else if (!ctrl && e.Key == Key.Tab)
        {
            IndentLines(outdent: shift);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.B) { Wrap("**"); e.Handled = true; }
        else if (ctrl && e.Key == Key.I) { Wrap("*"); e.Handled = true; }
        else if (ctrl && e.Key == Key.E) { Wrap("`"); e.Handled = true; }
        else if (ctrl && e.Key == Key.K) { InsertLink(); e.Handled = true; }
        else if (ctrl && e.Key == Key.V) { if (PasteImageFromClipboard()) e.Handled = true; }
    }

    /// <summary>
    /// Enter 续列表：接同一种记号（有序 +1、待办变未勾）；空项回车 = 退出列表（把记号删掉）。
    /// 引用行 <c>&gt; </c> 同样续写。
    /// </summary>
    private bool ContinueListOrQuote()
    {
        if (BodyBox.SelectionLength > 0) return false;

        var document = BodyBox.Document;
        var line = document.GetLineByOffset(BodyBox.CaretOffset);
        if (BodyBox.CaretOffset != line.EndOffset) return false;

        string text = document.GetText(line);

        Match list = ListLine.Match(text);
        if (list.Success)
        {
            string indent = list.Groups[1].Value;
            string marker = list.Groups[2].Value;
            string gap = list.Groups[3].Value;
            bool task = list.Groups[4].Success;
            string content = list.Groups[5].Value;

            if (content.Trim().Length == 0)
            {
                document.Replace(line.Offset, line.Length, "");
                BodyBox.CaretOffset = line.Offset;
                return true;
            }

            if (char.IsAsciiDigit(marker[0]))
            {
                int digits = marker.Length - 1;
                int number = int.Parse(marker[..digits]);
                marker = (number + 1) + marker[^1].ToString();
            }
            string next = "\n" + indent + marker + gap + (task ? "[ ] " : "");
            document.Insert(line.EndOffset, next);
            BodyBox.CaretOffset = line.EndOffset + next.Length;
            return true;
        }

        Match quote = QuoteLine.Match(text);
        if (quote.Success)
        {
            if (quote.Groups[2].Value.Trim().Length == 0)
            {
                document.Replace(line.Offset, line.Length, "");
                BodyBox.CaretOffset = line.Offset;
                return true;
            }
            string next = "\n" + quote.Groups[1].Value + "> ";
            document.Insert(line.EndOffset, next);
            BodyBox.CaretOffset = line.EndOffset + next.Length;
            return true;
        }

        return false;
    }

    /// <summary>Tab 缩进两格、Shift+Tab 退两格；有选区时整块处理（代码块里的嵌套列表靠它）。</summary>
    private void IndentLines(bool outdent)
    {
        const string unit = "  ";
        var document = BodyBox.Document;
        int start = BodyBox.SelectionStart;
        int end = start + BodyBox.SelectionLength;

        if (BodyBox.SelectionLength == 0)
        {
            if (!outdent)
            {
                document.Insert(start, unit);
                BodyBox.CaretOffset = start + unit.Length;
                return;
            }

            var caretLine = document.GetLineByOffset(start);
            int remove = 0;
            while (remove < unit.Length && start - remove - 1 >= caretLine.Offset
                   && document.GetCharAt(start - remove - 1) == ' ')
                remove++;
            if (remove > 0)
            {
                document.Remove(start - remove, remove);
                BodyBox.CaretOffset = start - remove;
            }
            return;
        }

        int first = document.GetLineByOffset(start).LineNumber;
        int last = document.GetLineByOffset(end > start ? end - 1 : end).LineNumber;

        // 从下往上改：上面的行挪动不会让下面的偏移失效
        for (int n = last; n >= first; n--)
        {
            var line = document.GetLineByNumber(n);
            if (!outdent)
            {
                document.Insert(line.Offset, unit);
                continue;
            }
            int remove = 0;
            while (remove < unit.Length && remove < line.Length && document.GetCharAt(line.Offset) == ' ')
            {
                document.Remove(line.Offset, 1);
                remove++;
            }
        }

        var firstLine = document.GetLineByNumber(first);
        var lastLine = document.GetLineByNumber(last);
        BodyBox.Select(firstLine.Offset, Math.Max(0, lastLine.EndOffset - firstLine.Offset));
    }

    // ============ 记号包裹 / 插入 ============

    /// <summary>给选区套同一对记号（粗体 / 斜体 / 行内代码）；没有选区就把光标放进中间。</summary>
    private void Wrap(string marker)
    {
        var document = BodyBox.Document;
        int start = BodyBox.SelectionStart;
        string selected = BodyBox.SelectedText;

        document.Replace(start, selected.Length, marker + selected + marker);
        if (selected.Length > 0) BodyBox.Select(start + marker.Length, selected.Length);
        else BodyBox.CaretOffset = start + marker.Length;
        BodyBox.Focus();
    }

    private void InsertLink()
    {
        var document = BodyBox.Document;
        int start = BodyBox.SelectionStart;
        string selected = BodyBox.SelectedText;
        string label = selected.Length > 0 ? selected : "链接文字";

        document.Replace(start, selected.Length, "[" + label + "]()");
        BodyBox.CaretOffset = start + label.Length + 3; // 落在括号里，直接填地址
        BodyBox.Focus();
    }

    /// <summary>行首记号开关（标题 / 列表 / 待办 / 引用）：已经有就去掉，没有就加上（缩进保留）。</summary>
    private void ToggleLinePrefix(string prefix)
    {
        var document = BodyBox.Document;
        var line = document.GetLineByOffset(BodyBox.CaretOffset);
        string text = document.GetText(line);
        string indent = text[..(text.Length - text.TrimStart().Length)];
        string body = text[indent.Length..];

        if (body.StartsWith(prefix, StringComparison.Ordinal))
            document.Remove(line.Offset + indent.Length, prefix.Length);
        else
            document.Insert(line.Offset + indent.Length, prefix);

        BodyBox.Focus();
    }

    private void InsertTable()
    {
        var document = BodyBox.Document;
        var line = document.GetLineByOffset(BodyBox.CaretOffset);
        bool blankLine = document.GetText(line).Trim().Length == 0;

        string table = "| 列 1 | 列 2 |\n| --- | --- |\n|  |  |\n";
        string insert = (blankLine ? "" : "\n") + table;
        int at = blankLine ? line.Offset : line.EndOffset;
        document.Insert(at, insert);
        BodyBox.CaretOffset = at + insert.Length - table.Length + 2; // 第一个表头格子里
        BodyBox.Focus();
    }

    // ============ 工具栏 ============

    private void MarkdownToolbar_Click(object sender, RoutedEventArgs e)
    {
        switch (((Button)sender).Name)
        {
            case "MdToolBold": Wrap("**"); break;
            case "MdToolItalic": Wrap("*"); break;
            case "MdToolCode": Wrap("`"); break;
            case "MdToolH1": ToggleLinePrefix("# "); break;
            case "MdToolH2": ToggleLinePrefix("## "); break;
            case "MdToolBullet": ToggleLinePrefix("- "); break;
            case "MdToolOrdered": ToggleLinePrefix("1. "); break;
            case "MdToolTask": ToggleLinePrefix("- [ ] "); break;
            case "MdToolQuote": ToggleLinePrefix("> "); break;
            case "MdToolLink": InsertLink(); break;
            case "MdToolTable": InsertTable(); break;
        }
    }
}
