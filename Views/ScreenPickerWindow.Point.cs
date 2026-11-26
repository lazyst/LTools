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

/// <summary>ScreenPickerWindow 的「取点」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 取点 ============

    /// <summary>
    /// 放大镜只在「屏幕取色」时显示：取点只要十字准线 + 坐标，
    /// 那个 132×132 的黑框会压住要点的目标（纯判定，冒烟可直接断言）。
    /// </summary>
    internal static bool ShowMagnifier(bool pointMode, bool wantColor) => pointMode && wantColor;

    /// <summary>信息行：取点只报坐标；取色把色值放在前面（纯函数，冒烟可直接断言）。</summary>
    internal static string PointInfoText(bool wantColor, string hex, int x, int y) =>
        wantColor ? $"{hex}   {x}, {y}" : $"{x}, {y}";

    private void UpdateCrosshair(Point dip)
    {
        var (ix, iy) = ToImage(dip);

        CrossH.Visibility = Visibility.Visible;
        CrossV.Visibility = Visibility.Visible;
        CrossH.X1 = 0; CrossH.X2 = ActualWidth;
        CrossH.Y1 = CrossH.Y2 = dip.Y;
        CrossV.Y1 = 0; CrossV.Y2 = ActualHeight;
        CrossV.X1 = CrossV.X2 = dip.X;

        Hex = SampleHex(ix, iy);
        PointX = _monitor.X + ix;
        PointY = _monitor.Y + iy;
        _hasPoint = true;

        InfoText.Text = PointInfoText(_wantColor, Hex, PointX, PointY);
        PlaceInfo(dip);
        if (ShowMagnifier(_pointMode, _wantColor)) PlaceMagnifier(dip, ix, iy);
    }

    private void ConfirmPoint()
    {
        if (!_hasPoint) return;
        Confirmed = true;
        Close();
    }

}
