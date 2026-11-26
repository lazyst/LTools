using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>CaptureResultWindow 的「摆位 / 缩放」部分。</summary>
public partial class CaptureResultWindow : Window
{
    // ============ 摆位 / 缩放 ============

    /// <summary>贴选区摆位：优先盖住选区，放不下就夹到所在显示器的工作区里。</summary>
    private void Place()
    {
        if (_area.Width <= 0 || _area.Height <= 0)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        ScreenGeometry? screen = CaptureRecordWindow.ScreenOf(_area);
        _scale = screen?.Scale ?? 1;
        if (_scale <= 0) _scale = 1;

        double areaLeft = _area.X / _scale;
        double areaTop = _area.Y / _scale;
        double areaWidth = _area.Width / _scale;
        double areaHeight = _area.Height / _scale;

        Width = Math.Clamp(areaWidth + 26, 460, 1000);
        Height = Math.Clamp(areaHeight + 132, 340, 840);

        double left = areaLeft + (areaWidth - Width) / 2;
        double top = areaTop;

        if (screen != null)
        {
            double workLeft = screen.WorkLeft / _scale;
            double workTop = screen.WorkTop / _scale;
            double workRight = (screen.WorkLeft + screen.WorkWidth) / _scale;
            double workBottom = (screen.WorkTop + screen.WorkHeight) / _scale;

            left = Math.Clamp(left, workLeft, Math.Max(workLeft, workRight - Width));
            top = Math.Clamp(top, workTop, Math.Max(workTop, workBottom - Height));
        }

        Left = left;
        Top = top;
    }

    /// <summary>
    /// 文字层缩放：1 源像素 = 1 屏幕物理像素（= 1 / DPI 缩放 DIP）；窗口装不下时再等比缩小。
    /// </summary>
    private void UpdateZoom()
    {
        if (OcrCanvas == null) return;

        double imageWidth = Math.Max(1, OcrCanvas.ImageWidth);
        double imageHeight = Math.Max(1, OcrCanvas.ImageHeight);

        double availableWidth = OcrScroll.ActualWidth - 4;
        double availableHeight = OcrScroll.ActualHeight - 4;
        if (double.IsNaN(availableWidth) || availableWidth <= 0) availableWidth = Width - 30;
        if (double.IsNaN(availableHeight) || availableHeight <= 0) availableHeight = Height - 170;

        double zoom = 1 / _scale;
        if (imageWidth * zoom > availableWidth) zoom = availableWidth / imageWidth;
        if (imageHeight * zoom > availableHeight) zoom = Math.Min(zoom, availableHeight / imageHeight);
        zoom = Math.Max(zoom, 0.05);

        OcrCanvas.SetZoom(zoom);
    }

}
