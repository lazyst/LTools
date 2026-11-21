using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Views;

/// <summary>
/// 预览里的查找条（Ctrl+F）：段落内按「字符 + 指针」铺平后搜索，跨 Run 也能命中
/// （粗体 / 链接里的半个词照样找得到），命中处直接选中并滚到可视区。
/// </summary>
/// <remarks>
/// 不做正则、不跨段落匹配：速记预览是只读的，查找只要「找到并看见」。
/// 源码编辑区的查找交给 AvalonEdit 自己（这里不装它的英文搜索面板）。
/// </remarks>
public partial class QuickNoteWindow
{
    private sealed record FindMatch(TextPointer Start, TextPointer End);

    private readonly List<FindMatch> _findMatches = new();
    private int _findIndex = -1;

    private void ShowFinder()
    {
        if (!_previewMode) return;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) RunFind();
    }

    private void CloseFinder()
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        FindBar.Visibility = Visibility.Collapsed;
        _findMatches.Clear();
        _findIndex = -1;
        FindCount.Text = "";
        HideFindHighlights();
        PreviewBox.Focus();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => RunFind();

    private void FindPrev_Click(object sender, RoutedEventArgs e) => StepFind(-1);

    private void FindNext_Click(object sender, RoutedEventArgs e) => StepFind(1);

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFinder();

    private void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            StepFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseFinder();
            e.Handled = true;
        }
    }

    private void RunFind()
    {
        _findMatches.Clear();
        _findIndex = -1;
        string query = FindBox.Text;
        if (query.Length == 0 || PreviewBox.Document == null)
        {
            FindCount.Text = "";
            HideFindHighlights();
            return;
        }

        foreach (Paragraph paragraph in Paragraphs(PreviewBox.Document))
            Collect(paragraph, query);

        FindCount.Text = _findMatches.Count.ToString();
        if (_findMatches.Count > 0)
        {
            _findIndex = 0;
            SelectMatch(0);
        }
        else
        {
            HideFindHighlights();
        }
    }

    private void StepFind(int delta)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        if (_findMatches.Count == 0) { RunFind(); return; }
        _findIndex = (_findIndex + delta + _findMatches.Count) % _findMatches.Count;
        SelectMatch(_findIndex);
    }

    private void Collect(Paragraph paragraph, string query)
    {
        var chars = new List<char>();
        var pointers = new List<TextPointer>();

        void Walk(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        // 一次走过这个 Run 的每个字符，别对每个字符重新 GetPositionAtOffset(i)（那是 O(n²)）
                        TextPointer cursor = run.ContentStart;
                        foreach (char c in run.Text)
                        {
                            chars.Add(c);
                            pointers.Add(cursor);
                            cursor = cursor.GetPositionAtOffset(1) ?? cursor;
                        }
                        break;

                    case LineBreak lineBreak:
                        chars.Add('\n');
                        pointers.Add(lineBreak.ContentStart);
                        break;

                    case Span span:
                        Walk(span.Inlines);
                        break;
                }
            }
        }

        Walk(paragraph.Inlines);
        if (chars.Count < query.Length) return;

        string text = new(chars.ToArray());
        int from = 0;
        while (from <= text.Length - query.Length)
        {
            int hit = text.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) break;

            TextPointer start = pointers[hit];
            TextPointer end = pointers[hit + query.Length - 1].GetPositionAtOffset(1) ?? start;
            _findMatches.Add(new FindMatch(start, end));
            from = hit + Math.Max(1, query.Length);
        }
    }

    private void SelectMatch(int index)
    {
        if (index < 0 || index >= _findMatches.Count) return;
        FindMatch match = _findMatches[index];
        PreviewBox.Selection.Select(match.Start, match.End);
        FindCount.Text = (index + 1) + "/" + _findMatches.Count;
        DrawFindHighlights();

        Rect rect = match.Start.GetCharacterRect(LogicalDirection.Forward);
        double target = PreviewBox.VerticalOffset + rect.Top - 40;
        PreviewBox.ScrollToVerticalOffset(Math.Max(0, target));
    }

    // ============ 命中高亮（自绘，盖在预览上面） ============

    /// <summary>最多画这么多处（长文里搜单个常用字，几千个边框只会拖慢渲染）。</summary>
    private const int MaxHighlightBoxes = 200;

    private void PreviewBox_ScrollChanged(object sender, ScrollChangedEventArgs e) => DrawFindHighlights();

    private void PreviewBox_SizeChanged(object sender, SizeChangedEventArgs e) => DrawFindHighlights();

    private void HideFindHighlights()
    {
        FindLayer.Children.Clear();
        FindLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 当前命中画得实一点、其余命中淡一点；位置按 <see cref="TextPointer.GetCharacterRect"/> 现算，
    /// 所以滚动 / 改窗口宽度（重新折行）之后要重画（<c>ScrollChanged</c> / <c>SizeChanged</c>）。
    /// 框画在预览**上面**（半透明）：垫在下面会被代码块 / 行内代码自己的底色盖住。
    /// </summary>
    private void DrawFindHighlights()
    {
        FindLayer.Children.Clear();
        if (_findMatches.Count == 0 || FindBar.Visibility != Visibility.Visible)
        {
            FindLayer.Visibility = Visibility.Collapsed;
            return;
        }

        var brush = (Brush)FindResource("EditorSelectionBrush");
        int drawn = 0;
        for (int i = 0; i < _findMatches.Count && drawn < MaxHighlightBoxes; i++)
        {
            bool current = i == _findIndex;
            foreach (Rect rect in RectsOf(_findMatches[i].Start, _findMatches[i].End))
            {
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                var box = new Border
                {
                    Background = brush,
                    Opacity = current ? 0.6 : 0.3, // 淡的那些只是「还有几处」，别和当前命中抢注意力
                    CornerRadius = new CornerRadius(2),
                    Width = rect.Width,
                    Height = rect.Height,
                };
                Canvas.SetLeft(box, rect.Left);
                Canvas.SetTop(box, rect.Top);
                FindLayer.Children.Add(box);
                drawn++;
            }
        }
        FindLayer.Visibility = FindLayer.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>一段命中覆盖到的每个矩形（软换行折成两行时按行拆开，别画成一个大方块）。</summary>
    private static List<Rect> RectsOf(TextPointer start, TextPointer end)
    {
        var rects = new List<Rect>();
        Rect line = Rect.Empty;
        double lineTop = double.NaN;
        TextPointer? pointer = start;
        for (int guard = 0; pointer != null && pointer.CompareTo(end) < 0 && guard < 5000; guard++)
        {
            Rect rect = pointer.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty)
            {
                if (double.IsNaN(lineTop)) lineTop = rect.Top;
                if (Math.Abs(rect.Top - lineTop) > 1)
                {
                    if (!line.IsEmpty) rects.Add(line);
                    line = Rect.Empty;
                    lineTop = rect.Top;
                }
                line = line.IsEmpty ? rect : Rect.Union(line, rect);
            }
            pointer = pointer.GetNextInsertionPosition(LogicalDirection.Forward);
        }
        if (!line.IsEmpty) rects.Add(line);
        return rects;
    }

    // ============ 文档遍历 ============

    private static IEnumerable<Paragraph> Paragraphs(FlowDocument document)
    {
        foreach (Block block in document.Blocks)
            foreach (Paragraph paragraph in Paragraphs(block))
                yield return paragraph;
    }

    private static IEnumerable<Paragraph> Paragraphs(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                yield return paragraph;
                break;

            case Section section:
                foreach (Block child in section.Blocks)
                    foreach (Paragraph found in Paragraphs(child))
                        yield return found;
                break;

            case List list:
                foreach (ListItem item in list.ListItems)
                    foreach (Block child in item.Blocks)
                        foreach (Paragraph found in Paragraphs(child))
                            yield return found;
                break;

            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                    foreach (TableRow row in group.Rows)
                        foreach (TableCell cell in row.Cells)
                            foreach (Block child in cell.Blocks)
                                foreach (Paragraph found in Paragraphs(child))
                                    yield return found;
                break;
        }
    }
}
