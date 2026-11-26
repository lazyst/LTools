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

/// <summary>ScreenPickerWindow 的「布局」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 坐标 ============

    /// <summary>窗口 DIP → 屏幕物理像素（吸附目标用物理像素记录）。</summary>
    private System.Drawing.Point ToScreen(Point dip)
    {
        var (ix, iy) = ToImage(dip);
        return new System.Drawing.Point(_monitor.X + ix, _monitor.Y + iy);
    }

    /// <summary>屏幕物理像素矩形 → 窗口 DIP。</summary>
    private Rect ToDipRect(System.Drawing.Rectangle b)
    {
        double sx = ActualWidth > 0 ? ActualWidth / _shot.PixelWidth : 1;
        double sy = ActualHeight > 0 ? ActualHeight / _shot.PixelHeight : 1;
        return new Rect(
            (b.X - _monitor.X) * sx,
            (b.Y - _monitor.Y) * sy,
            b.Width * sx,
            b.Height * sy);
    }

}
