using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 取色工具：光标旁显示色卡与色值，单击按当前格式复制颜色。
/// </summary>
/// <remarks>
/// <para>
/// 读的是冻结底图（<see cref="CaptureDocument.Base"/>），不是画好的成品——标注与压暗不参与；
/// 也不要求先框选：整个冻结画面都能取（取色本来就不属于「标注在选区里」那件事）。
/// 取完不关覆盖层，可以连着取几个；<c>Esc</c> 退回选择工具。
/// </para>
/// <para>
/// 照上游 <c>screenshot_ui/color_picker_*</c>：色值格式（hex / 无井号 / rgb / hsl，轻按 Shift 轮换）、
/// 坐标口径（全局屏幕 / 相对选区左上角，Shift+P 切换）、显示时机（选区外不显示 / 总是显示 / 总是不显示）。
/// 显示时机只收色卡与取样点的十字；**单击仍然取色并复制**（上游的取色命令也不看这个开关）。
/// </para>
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
            HidePicker();      // 越界（窗口边缘）只是没取到，不报错
            return;
        }

        bool visible = PickerShouldShow(p);
        if (!visible)
        {
            HidePicker();
            return;
        }

        PickerSwatch.Background = new SolidColorBrush(color);
        PickerText.Text = PickerColor.Format(color, OverlayUiSettings.ColorPickerFormat);
        PickerPosText.Text = PickerPositionText(x, y);

        PickerChip.Visibility = Visibility.Visible;
        _pickerShown = true;

        UpdatePickerReticle(p, color);

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

    /// <summary>取色器显示时机（照上游 <c>screenshotColorPickerVisible</c>；LTools 取色是独立工具）。</summary>
    private bool PickerShouldShow(Point p)
    {
        bool hasSelection = _sel is Rect s && IsValid(s);
        bool inside = false;

        if (hasSelection)
        {
            // 上游按 4 物理像素的容差判「在选区内」
            double tolerance = 4.0 / Math.Max(0.01, _doc.Scale);
            var area = _sel!.Value;
            area.Inflate(tolerance, tolerance);
            inside = area.Contains(p);
        }

        return OverlayUiSettings.PickerVisible(hasSelection, inside, intelligentSelecting: false);
    }

    /// <summary>取样点的十字与小方框（参考线颜色透明时只有小方框）。</summary>
    private void UpdatePickerReticle(Point p, Color color)
    {
        Surface.PickerPoint = p;
        Surface.PickerGuideColor = GuideColor(OverlayUiSettings.PickerCenterGuideColor, p);
        Surface.PickerMarkerColor = GuideLines.Readable(color);
        Surface.InvalidateVisual();
    }

    private string PickerPositionText(int imageX, int imageY)
    {
        int x = _doc.Monitor.X + imageX;
        int y = _doc.Monitor.Y + imageY;

        if (OverlayUiSettings.RelativePickerCoordinates && _sel is Rect sel && IsValid(sel))
        {
            var (x1, y1) = ToImage(sel.TopLeft);
            x -= x1;
            y -= y1;
        }

        return PickerColor.PositionText(x, y);
    }

    private void HidePicker()
    {
        if (_pickerShown)
        {
            _pickerShown = false;
            PickerChip.Visibility = Visibility.Collapsed;
        }

        if (Surface.PickerPoint != null)
        {
            Surface.PickerPoint = null;
            Surface.InvalidateVisual();
        }
    }

    /// <summary>
    /// 单击取色：按当前格式复制色值。**不关闭覆盖层**（连着取几个是常态），也不落标注。
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

        string text = PickerColor.Format(color, OverlayUiSettings.ColorPickerFormat);
        if (NativeClipboard.SetText(text))
            ToastService.Show($"已复制 {text}", "取色", 1600);
        else
            ToastService.Show("复制颜色失败（剪贴板被占用）", "取色", 0, ToastKind.error);
    }
}
