using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的「文字」工具：在单击处就地弹一个输入框，Enter 提交，Shift+Enter 换行，Esc 放弃。
/// </summary>
/// <remarks>
/// 与截图覆盖层同一套交互（见 <c>CaptureOverlayWindow.Text.cs</c>），差别只有两点：
/// ① 起点是**图像像素**，摆到 <see cref="Canvas"/> 上要乘当前缩放；
/// ② 输入框字号也乘缩放——敲的时候看到的字号与落地后一样大。
/// 文字落地后属于 <see cref="Mark.Text"/>（文档单位 = 图像像素），所以输入框自己缩放、
/// 标注不缩放。
/// </remarks>
public partial class ImageEditWindow
{
    /// <summary>正在输入的那一段文字的左上角（图像像素）。</summary>
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
        var spec = CurrentStyle.TextSpecOf();
        TextEntry.FontFamily = new FontFamily(
            string.IsNullOrWhiteSpace(spec.FontFamily) ? TextInk.FallbackFamily : spec.FontFamily);
        TextEntry.FontSize = Math.Max(1, TextInk.SizeOf(spec) * _zoom);
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
        PlaceTextEntry(at);

        TextEntry.Focus();
        Keyboard.Focus(TextEntry);

        // 这一下是 PreviewMouseDown 里开的框：等这一轮输入处理完再抢一次焦点，
        // 免得 WPF 在按下收尾时把焦点还给窗口，用户敲的字全落到画布上。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_textEditing) return;
            TextEntry.Focus();
            Keyboard.Focus(TextEntry);
        }), DispatcherPriority.Input);
    }

    /// <summary>把输入框摆到图像坐标对应的窗口位置（贴边时往回收）。</summary>
    private void PlaceTextEntry(Point at)
    {
        var view = new Point(_offset.X + at.X * _zoom, _offset.Y + at.Y * _zoom);

        System.Windows.Controls.Canvas.SetLeft(TextEntry, view.X);
        System.Windows.Controls.Canvas.SetTop(TextEntry, view.Y);
        TextEntry.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double w = Math.Max(TextEntry.DesiredSize.Width, TextEntry.MinWidth);
        double h = Math.Max(TextEntry.DesiredSize.Height, 24);

        System.Windows.Controls.Canvas.SetLeft(TextEntry,
            Math.Clamp(view.X, 2, Math.Max(2, Surface.ActualWidth - w - 2)));
        System.Windows.Controls.Canvas.SetTop(TextEntry,
            Math.Clamp(view.Y, 2, Math.Max(2, Surface.ActualHeight - h - 2)));
    }

    /// <summary>把输入框里的文字写成一条标注（空白的丢掉，不算记一次撤销）。</summary>
    private void CommitText()
    {
        if (!_textEditing) return;

        string text = TextEntry.Text;
        CloseTextEntry();

        if (string.IsNullOrWhiteSpace(text)) return;

        _doc.PushUndo();
        _doc.Doc!.Add(new Mark.Text(_textOrigin, text, CurrentStyle.TextSpecOf()));
        RefreshMarks();
    }

    /// <summary>放弃这一段文字（Esc / 切工具 / 进平移）。</summary>
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
        Surface.Focus();   // 焦点回到画布，方向键 / 工具快捷键继续可用
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
        }
    }

    /// <summary>点到别处 = 提交（点画布、点工具条都算）。</summary>
    private void TextEntry_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    /// <summary>事件源是否来自文字输入框（点在输入框里只是挪光标，不能当手势）。</summary>
    private bool IsOnTextEntry(DependencyObject? source)
    {
        for (var node = source; node != null;)
        {
            if (ReferenceEquals(node, TextEntry)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
