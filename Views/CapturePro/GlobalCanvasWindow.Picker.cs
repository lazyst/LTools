using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布的取色（照上游 <c>beginColorSampling</c> / <c>colorSampling.commit</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游取的是**透明画布自己渲染出来的像素**（注释写明「不抓桌面，也不透过画布抓工具条」）；
/// 本项目的画布覆在桌面上、背景全透明，取自己的像素只会拿到透明色，所以改成
/// **把窗口暂时隐掉、抓一次光标所在显示器**，再在静态截图上取色——记在
/// <c>docs/CAPTURE_PARITY.md</c> 的平台差异里。
/// </para>
/// <para>
/// 交互：选中「取色」工具即进入取色；移动显示色卡，左键复制 <c>#RRGGBB</c> 并把颜色写进
/// 上一个绘图工具的那套样式（然后切回它接着画），Esc 退出。
/// </para>
/// </remarks>
public partial class GlobalCanvasWindow
{
    private bool _pickerSampling;
    private BitmapSource? _pickerShot;

    /// <summary>取到的颜色写给哪个工具（选「取色」之前的那个绘图工具）。</summary>
    private CaptureTool _colorTarget = CaptureTool.Pen;

    private void BeginPicker()
    {
        if (_pickerSampling || _clickThrough) return;

        CommitText();
        CancelGesture();

        _pickerSampling = true;
        Surface.Cursor = Cursors.Cross;

        // 抓桌面时先把自己隐掉：Opacity 0 立刻生效（Render 优先级），抓完原样恢复
        double saved = Opacity;
        Opacity = 0;
        try
        {
            Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            using var bitmap = ScreenCapture.Capture(_monitor.X, _monitor.Y,
                Math.Max(1, _monitor.Width), Math.Max(1, _monitor.Height));
            _pickerShot = ScreenPicker.ToBitmapSource(bitmap);
        }
        catch (Exception ex)
        {
            AppLog.Warn("GlobalCanvas.Picker", ex.Message);
        }
        finally
        {
            Opacity = saved;
        }

        if (_pickerShot == null)
        {
            _pickerSampling = false;
            UpdateCanvasCursor();
            return;
        }

        ShowPickerAt(Mouse.GetPosition(Surface));
    }

    private void EndPicker()
    {
        if (!_pickerSampling) return;
        _pickerSampling = false;
        _pickerShot = null;
        HidePicker();
        UpdateCanvasCursor();
    }

    private Color? ColorAt(Point view)
    {
        if (_pickerShot == null) return null;

        int x = (int)Math.Floor(view.X * _sx);
        int y = (int)Math.Floor(view.Y * _sy);
        if (x < 0 || y < 0 || x >= _pickerShot.PixelWidth || y >= _pickerShot.PixelHeight) return null;

        return PickerColor.Read(_pickerShot, x, y);
    }

    private void ShowPickerAt(Point view)
    {
        if (ColorAt(view) is not { } color)
        {
            HidePicker();
            return;
        }

        PickerSwatch.Background = new SolidColorBrush(color);
        PickerText.Text = PickerColor.Describe(color);

        PickerChip.Visibility = Visibility.Visible;

        // 摆在光标右下；贴边时收回来（和截图覆盖层同一套算法）
        PickerChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = PickerChip.DesiredSize.Width, h = PickerChip.DesiredSize.Height;
        double left = view.X + 16;
        double top = view.Y + 18;
        if (left + w > Surface.ActualWidth - 8) left = view.X - w - 12;
        if (top + h > Surface.ActualHeight - 8) top = view.Y - h - 12;

        System.Windows.Controls.Canvas.SetLeft(PickerChip,
            Math.Clamp(left, 8, Math.Max(8, Surface.ActualWidth - w - 8)));
        System.Windows.Controls.Canvas.SetTop(PickerChip,
            Math.Clamp(top, 8, Math.Max(8, Surface.ActualHeight - h - 8)));
    }

    private void HidePicker() => PickerChip.Visibility = Visibility.Collapsed;

    /// <summary>单击取色：复制十六进制，并把颜色写进上一个绘图工具的样式（然后切回它）。</summary>
    private void CommitPick(Point canvas)
    {
        if (!_pickerSampling) return;

        var color = ColorAt(ToView(canvas));
        EndPicker();
        if (color is not { } picked) return;

        string hex = PickerColor.Hex(picked);
        if (NativeClipboard.SetText(hex))
            ToastService.Show($"已复制 {hex}", "取色", 1600);
        else
            ToastService.Show("复制颜色失败（剪贴板被占用）", "取色", 0, ToastKind.error);

        var target = _colorTarget;
        _styles.For(target).Color = picked;
        CaptureStyleStore.SaveSoon();

        if (target != CaptureTool.Picker) SetTool(target);
        Toolbar.RefreshValues();
    }
}
