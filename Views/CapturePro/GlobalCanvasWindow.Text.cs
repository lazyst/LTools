using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的「文字」工具：在单击处就地弹一个输入框，Enter 提交，Shift+Enter 换行，Esc 放弃。
/// </summary>
/// <remarks>
/// 与图片编辑窗同一套交互，差别是坐标：标注落在**画布坐标**，输入框要摆到画布坐标经相机
/// 换出来的窗口位置；字号也乘缩放——敲的时候看到的与落地后一样大。
/// </remarks>
public partial class GlobalCanvasWindow
{
    /// <summary>正在输入的那一段文字的左上角（画布坐标）。</summary>
    private Point _textOrigin;

    private bool _textEditing;

    private void OpenTextEntry(Point at)
    {
        CommitText();   // 正在编辑的那一段先落地，再开新的一段

        _textOrigin = at;
        _textEditing = true;

        TextEntry.Text = string.Empty;

        // 输入框照这一套文字样式显示：敲的时候看到的和落地后是同一份
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
        TextEntry.Background = TextInk.HasFill(spec) && spec.FillStyle == FillKind.Solid
            ? new SolidColorBrush(spec.Fill)
            : Brushes.Transparent;

        TextEntry.Visibility = Visibility.Visible;
        PlaceTextEntry();

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

    /// <summary>把输入框摆到画布坐标经相机换出来的窗口位置（贴边时往回收）。</summary>
    private void PlaceTextEntry()
    {
        var view = ToView(_textOrigin);

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

        _doc.Add(new Mark.Text(_textOrigin, text, CurrentStyle.TextSpecOf()));
        RefreshMarks();
    }

    /// <summary>放弃这一段文字（Esc / 切工具 / 退出）。</summary>
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
            return;
        }
    }

    /// <summary>点到别处 = 提交（点画布、点工具条都算）。</summary>
    private void TextEntry_LostFocus(object sender, RoutedEventArgs e) => CommitText();
}
