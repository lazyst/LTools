using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using LTools.Core;

namespace LTools.Views;

/// <summary>
/// 速记的 Markdown 预览（Ctrl+P）：只读渲染 + 复制源码 + 点某一块回到源码对应行。
/// </summary>
/// <remarks>
/// 预览永远是**派生**的：只读 <see cref="QuickNoteWindow"/> 里编辑器当前的文本，不反向写回，
/// 渲染失败也不影响编辑区。真正写盘仍走 <c>SaveCurrent</c>。
/// </remarks>
public partial class QuickNoteWindow
{
    private bool _previewMode;

    /// <summary>Ctrl+P / 标题栏按钮：在「Markdown 源码」和「渲染预览」之间切。</summary>
    private void TogglePreview()
    {
        if (_previewMode) ExitPreview();
        else EnterPreview();
    }

    private void EnterPreview()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _findMatches.Clear();
        HideFindHighlights();
        PreviewBox.Document = MarkdownRender.Build(BodyBox.Text, PreviewBaseDirectory(), this);
        PreviewBox.Visibility = Visibility.Visible;
        BodyBox.Visibility = Visibility.Collapsed;
        BodyPlaceholder.Visibility = Visibility.Collapsed;
        MarkdownToolbar.Visibility = Visibility.Collapsed;
        CopyMarkdownBtn.Visibility = Visibility.Visible;
        PreviewToggleBtn.Content = "编辑";
        _previewMode = true;
        PreviewBox.Focus();
    }

    private void ExitPreview()
    {
        if (!_previewMode) return;
        _previewMode = false;
        FindBar.Visibility = Visibility.Collapsed;
        _findMatches.Clear();
        HideFindHighlights();
        PreviewBox.Visibility = Visibility.Collapsed;
        PreviewBox.Document = new FlowDocument(); // 丢掉渲染出来的那份（RichTextBox 不接受 null）
        BodyBox.Visibility = Visibility.Visible;
        BodyPlaceholder.Visibility = BodyBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkdownToolbar.Visibility = Visibility.Visible;
        CopyMarkdownBtn.Visibility = Visibility.Collapsed;
        PreviewToggleBtn.Content = "预览";
        BodyBox.Focus();
    }

    /// <summary>图片的相对路径按这条速记所在的分类目录解析；还没保存的笔记按速记根目录。</summary>
    private string PreviewBaseDirectory()
    {
        string? path = _current?.Path;
        if (!string.IsNullOrEmpty(path))
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) return dir;
        }
        return AppPaths.NotesDir;
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e) => TogglePreview();

    private void CopyMarkdown_Click(object sender, RoutedEventArgs e)
    {
        string text = BodyBox.Text;
        if (text.Length == 0)
        {
            TrayService.Notify("这条速记还没有内容");
            return;
        }
        try
        {
            Clipboard.SetText(text);
            TrayService.Notify("已复制 Markdown 源码");
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "复制 Markdown", ex.Message);
        }
    }

    /// <summary>
    /// 双击预览里的某一块 → 回到源码的对应行。
    /// </summary>
    /// <remarks>
    /// 单击不跳转：原来单击（光标落在正文里、没有拖选）就直接退出预览，想「选中一句复制」或
    /// 只是点一下看光标位置，都会莫名其妙掉回源码。现在单击只做选中 / 点链接，双击才是「去改这一块」。
    /// 双击落在链接上时不动（要跳浏览器）。
    /// </remarks>
    private void PreviewBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_previewMode) return;
        if (VisualTree.FindAncestor<Hyperlink>(e.OriginalSource as DependencyObject) != null) return;
        TryJumpFromPreviewPoint(e.GetPosition(PreviewBox));
    }

    /// <summary>双击预览里的某一点：定位到源码行并切回源码，返回是否跳了（冒烟直接调它）。</summary>
    internal bool TryJumpFromPreviewPoint(Point point)
    {
        if (!_previewMode) return false;
        TextPointer? position = PreviewBox.GetPositionFromPoint(point, true);
        int? line = position == null ? null : SourceLineOf(position);
        if (line == null) return false;

        ExitPreview();
        JumpToLine(line.Value);
        return true;
    }

    /// <summary>从命中的段落往上找最近一个带源码行号的块（渲染时每块都打了 <c>Tag = 行号</c>）。</summary>
    private static int? SourceLineOf(TextPointer position)
    {
        DependencyObject? node = position.Paragraph;
        while (node != null)
        {
            if (node is TextElement { Tag: int line }) return line;
            node = (node as FrameworkContentElement)?.Parent ?? (node as FrameworkElement)?.Parent;
        }
        return null;
    }

    private void JumpToLine(int line)
    {
        var document = BodyBox.Document;
        if (document == null || document.LineCount == 0) return;
        line = Math.Clamp(line, 1, document.LineCount);
        BodyBox.CaretOffset = document.GetOffset(line, 1);
        BodyBox.ScrollToLine(line);
        BodyBox.Focus();
    }
}
