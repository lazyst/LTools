using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「坐标 / 缩放换算」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 布局 / 换算 ============

    /// <summary>
    /// 按物理像素把窗口钉死在显示器矩形上。
    /// 混合 DPI 下 WPF 的 DIP→像素换算可能差几个像素（主屏 100%、副屏 150% 时尤其明显），
    /// 这里用 Win32 再确认一次，保证冻结画面与屏幕 1:1 对齐、取点不偏移。
    /// </summary>
    private void PinToMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(hwnd, Win32.HwndTopmost,
            _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, Win32.SwpNoactivate);
    }

    private void PlaceHint()
    {
        HintBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = HintBox.DesiredSize.Width;
        Canvas.SetLeft(HintBox, Math.Max(12, (ActualWidth - w) / 2));
        Canvas.SetTop(HintBox, 26);
    }

    private void PlaceInfo(Point dip)
    {
        InfoBox.Visibility = Visibility.Visible;
        InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = InfoBox.DesiredSize.Width;
        double h = InfoBox.DesiredSize.Height;
        double x = dip.X + 18;
        double y = dip.Y + 20;
        if (x + w > ActualWidth - 8) x = dip.X - w - 18;
        if (y + h > ActualHeight - 8) y = dip.Y - h - 20;
        Canvas.SetLeft(InfoBox, Math.Max(8, x));
        Canvas.SetTop(InfoBox, Math.Max(8, y));
    }

    private void PlaceMagnifier(Point dip, int imageX, int imageY)
    {
        const double zoom = 8;
        const double box = 132;
        const double inner = 64;    // 放大镜中心（132 - 2px 边框 → 128，中心 64）

        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(zoom, zoom));
        transform.Children.Add(new TranslateTransform(inner - imageX * zoom, inner - imageY * zoom));
        MagImage.RenderTransform = transform;

        double x = dip.X + 22;
        double y = dip.Y + 22;
        if (x + box > ActualWidth - 8) x = dip.X - box - 22;
        if (y + box > ActualHeight - 8) y = dip.Y - box - 22;
        Canvas.SetLeft(MagBox, Math.Max(8, x));
        Canvas.SetTop(MagBox, Math.Max(8, y));
        Canvas.SetZIndex(MagBox, 10);
        MagBox.Visibility = Visibility.Visible;
    }

    private Point Clamp(Point p) => new(
        Math.Clamp(p.X, 0, Math.Max(0, ActualWidth - 1)),
        Math.Clamp(p.Y, 0, Math.Max(0, ActualHeight - 1)));

    /// <summary>DIP → 冻结画面像素（画面就是显示器的 1:1 物理像素）。</summary>
    private (int X, int Y) ToImage(Point dip)
    {
        double sx = ActualWidth > 0 ? _shot.PixelWidth / ActualWidth : 1;
        double sy = ActualHeight > 0 ? _shot.PixelHeight / ActualHeight : 1;
        int x = (int)Math.Round(dip.X * sx);
        int y = (int)Math.Round(dip.Y * sy);
        return (Math.Clamp(x, 0, _shot.PixelWidth - 1), Math.Clamp(y, 0, _shot.PixelHeight - 1));
    }

    /// <summary>物理屏幕坐标 → 窗口 DIP。</summary>
    private bool TryToDip(Win32.Point screen, out Point dip)
    {
        dip = default;
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        double sx = (double)ActualWidth / _shot.PixelWidth;
        double sy = (double)ActualHeight / _shot.PixelHeight;
        dip = new Point((screen.X - _monitor.X) * sx, (screen.Y - _monitor.Y) * sy);
        return true;
    }

    private string SampleHex(int x, int y)
    {
        try
        {
            var one = new CroppedBitmap(_shot, new Int32Rect(x, y, 1, 1));
            var px = new byte[4];
            one.CopyPixels(px, 4, 0);
            return $"#{px[2]:X2}{px[1]:X2}{px[0]:X2}";   // Bgra32
        }
        catch
        {
            return "#000000";
        }
    }

    /// <summary>取显示器有效 DPI（换算成 WPF DIP 的缩放比）；取不到时按 100%。</summary>
    internal static (double X, double Y) MonitorScale(System.Drawing.Rectangle monitor)
    {
        try
        {
            var center = new Win32.Point
            {
                X = monitor.X + monitor.Width / 2,
                Y = monitor.Y + monitor.Height / 2,
            };
            var hmon = Win32.MonitorFromPoint(center, Win32.MonitorDefaultToNearest);
            if (hmon != IntPtr.Zero &&
                Win32.GetDpiForMonitor(hmon, Win32.MdtEffectiveDpi, out uint dx, out uint dy) == 0 &&
                dx > 0 && dy > 0)
                return (dx / 96.0, dy / 96.0);
        }
        catch { /* shcore 不可用（理论上 Win10+ 都有） */ }
        return (1.0, 1.0);
    }
}
