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
    /// 点预览里的某一块 → 回到源码的对应行。选中文字（在复制）或点在链接上（要跳浏览器）时不动。
    /// </summary>
    private void PreviewBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_previewMode) return;
        if (PreviewBox.Selection != null && !PreviewBox.Selection.IsEmpty) return;
        if (FindAncestor<Hyperlink>(e.OriginalSource as DependencyObject) != null) return;

        TextPointer? position = PreviewBox.GetPositionFromPoint(e.GetPosition(PreviewBox), true);
        int? line = position == null ? null : SourceLineOf(position);
        if (line == null) return;

        ExitPreview();
        JumpToLine(line.Value);
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
