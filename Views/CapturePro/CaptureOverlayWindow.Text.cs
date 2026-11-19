using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 文字工具：在单击处就地弹一个输入框，Enter 提交，Shift+Enter 换行，Esc 放弃。
/// </summary>
/// <remarks>
/// 输入框是覆盖层里的普通 <see cref="TextBox"/>（不是弹窗）：截图期间窗口是全屏遮罩，
/// 再开一个窗口要另管焦点与层级。提交时把文字写进文档（<see cref="Mark.Text"/>），
/// 字号 / 颜色取当前 <see cref="ToolStyle"/>——和别的工具共用同一份样式状态。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private Point _textOrigin;
    private bool _textEditing;

    private void OpenTextEntry(Point at)
    {
        CommitText();   // 正在编辑的那一段先落地，再开新的一段

        _textOrigin = at;
        _textEditing = true;

        TextEntry.Text = string.Empty;

        // 输入框就照这一套文字样式显示（字体 / 字号 / 粗斜 / 对齐 / 字色），
        // 敲的时候看到的和落地后是同一份——上游的编辑器也是直接用样式渲染的。
        var spec = _style.TextSpecOf();
        TextEntry.FontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(spec.FontFamily) ? TextInk.FallbackFamily : spec.FontFamily);
        TextEntry.FontSize = TextInk.SizeOf(spec);
        TextEntry.FontWeight = spec.Bold ? FontWeights.Bold : FontWeights.Normal;
        TextEntry.FontStyle = spec.Italic ? FontStyles.Italic : FontStyles.Normal;
        TextEntry.TextAlignment = spec.Align switch
        {
            TextAlign.Center => TextAlignment.Center,
            TextAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left,
        };

        TextEntry.Foreground = new SolidColorBrush(spec.Color);
        // 背景胶囊在画布上由标注自己画；输入框只垫一层同色底，避免白底上白字看不见
        TextEntry.Background = TextInk.HasFill(spec) && spec.FillStyle == FillKind.Solid
            ? new SolidColorBrush(spec.Fill)
            : Brushes.Transparent;

        TextEntry.Visibility = Visibility.Visible;

        // 先把框摆好再量：贴右边 / 底边时往回收，别让输入框跑到窗口外
        Canvas.SetLeft(TextEntry, at.X);
        Canvas.SetTop(TextEntry, at.Y);
        TextEntry.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = Math.Max(TextEntry.DesiredSize.Width, TextEntry.MinWidth);
        double h = Math.Max(TextEntry.DesiredSize.Height, 24);
        Canvas.SetLeft(TextEntry, Math.Clamp(at.X, 2, Math.Max(2, ActualWidth - w - 2)));
        Canvas.SetTop(TextEntry, Math.Clamp(at.Y, 2, Math.Max(2, ActualHeight - h - 2)));

        TextEntry.Focus();
        Keyboard.Focus(TextEntry);

        // 这一下是 PreviewMouseDown 里开的框：等这一轮输入处理完再抢一次焦点，
        // 免得 WPF 在按下收尾时把焦点还给窗口，用户敲的字全落到覆盖层上。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_textEditing) return;
            TextEntry.Focus();
            Keyboard.Focus(TextEntry);
        }), DispatcherPriority.Input);
    }

    /// <summary>把输入框里的文字写成一条标注（空白的丢掉，不算记一次撤销）。</summary>
    private void CommitText()
    {
        if (!_textEditing) return;

        string text = TextEntry.Text;
        CloseTextEntry();

        if (string.IsNullOrWhiteSpace(text)) return;

        _doc.Add(new Mark.Text(_textOrigin, text, _style.TextSpecOf()));
        RefreshMarks();
        NoteCreated();
    }

    /// <summary>放弃这一段文字（Esc / 切工具）。</summary>
    private void CancelText()
    {
        if (!_textEditing) return;
        CloseTextEntry();
        UpdateChrome();
    }

    private void CloseTextEntry()
    {
        _textEditing = false;
        TextEntry.Visibility = Visibility.Collapsed;
        TextEntry.Text = string.Empty;
        Focus();   // 焦点回到覆盖层，方向键 / Enter 继续可用
    }

    private void TextEntry_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            CommitText();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
            return;
        }

        // 上游 drawing/delete_text_after_cursor（默认开）：关掉之后 Delete 什么都不删，只剩退格。
        if (e.Key == Key.Delete && !DrawingSettings.DeleteTextAfterCursor)
            e.Handled = true;
    }

    /// <summary>点到别处 = 提交（点画布、点工具栏都算）。</summary>
    private void TextEntry_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    /// <summary>事件源是否来自文字输入框（点在输入框里只是挪光标，不能当手势）。</summary>
    private bool IsOnTextEntry(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, TextEntry)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
}
