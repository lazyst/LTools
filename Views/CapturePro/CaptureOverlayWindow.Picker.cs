using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 取色工具：光标旁显示色卡与色值，单击把 <c>#RRGGBB</c> 复制到剪贴板。
/// </summary>
/// <remarks>
/// 读的是冻结底图（<see cref="CaptureDocument.Base"/>），不是画好的成品——标注与压暗不参与；
/// 也不要求先框选：整个冻结画面都能取（取色本来就不属于「标注在选区里」那件事）。
/// 取完不关覆盖层，可以连着取几个；`Esc` 退回选择工具。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private bool _pickerShown;

    /// <summary>光标旁那块色卡（只在取色工具下出现）。</summary>
    private void ShowPickerAt(Point p)
    {
        var (x, y) = ToImage(p);
        if (PickerColor.Read(_doc.Base, x, y) is not Color color)
        {
            HidePicker();
            return;
        }

        PickerSwatch.Background = new SolidColorBrush(color);
        PickerText.Text = PickerColor.Describe(color);

        PickerChip.Visibility = Visibility.Visible;
        _pickerShown = true;

        // 摆在光标右下；贴边时收回来（和悬停标签同一套算法）
        PickerChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = PickerChip.DesiredSize.Width, h = PickerChip.DesiredSize.Height;
        double left = p.X + 16;
        double top = p.Y + 18;
        if (left + w > ActualWidth - 8) left = p.X - w - 12;
        if (top + h > ActualHeight - 8) top = p.Y - h - 12;

        Canvas.SetLeft(PickerChip, Math.Clamp(left, 8, Math.Max(8, ActualWidth - w - 8)));
        Canvas.SetTop(PickerChip, Math.Clamp(top, 8, Math.Max(8, ActualHeight - h - 8)));
    }

    private void HidePicker()
    {
        if (!_pickerShown) return;
        _pickerShown = false;
        PickerChip.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 单击取色：复制十六进制色值。**不关闭覆盖层**（连着取几个是常态），也不落标注。
    /// </summary>
    private void PickColor(Point p)
    {
        var (x, y) = ToImage(p);
        if (PickerColor.Read(_doc.Base, x, y) is not Color color)
        {
            ShowPickerAt(p);      // 越界（窗口边缘）只是没取到，不报错
            return;
        }

        ShowPickerAt(p);

        string hex = PickerColor.Hex(color);
        if (NativeClipboard.SetText(hex))
            ToastService.Show($"已复制 {hex}", "取色", 1600);
        else
            ToastService.Show("复制颜色失败（剪贴板被占用）", "取色", 0, ToastKind.error);
    }
}
