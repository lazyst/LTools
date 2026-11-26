using System.Windows;

namespace LTools.Views;

/// <summary>ScreenPickerWindow 的「取点」部分。</summary>
public partial class ScreenPickerWindow : Window
{
    // ============ 取点 ============

    /// <summary>
    /// 放大镜只在「屏幕取色」时显示：取点只要十字准线 + 坐标，
    /// 那个 132×132 的黑框会压住要点的目标（纯判定，冒烟可直接断言）。
    /// </summary>
    internal static bool ShowMagnifier(bool wantColor) => wantColor;

    /// <summary>信息行：取点只报坐标；取色把色值放在前面（纯函数，冒烟可直接断言）。</summary>
    internal static string PointInfoText(bool wantColor, string hex, int x, int y) =>
        wantColor ? $"{hex}   {x}, {y}" : $"{x}, {y}";

    /// <summary>
    /// 顶部那句提示。调用方给了提示文字（「屏幕取点」的 <c>hint</c> 参数）就放在最前面，
    /// 后面仍跟着「怎么操作」——只写自定义那句话会让人不知道怎么确认 / 取消。
    /// </summary>
    private string PointHint() => _wantColor
        ? "单击取色 · 右键 / Esc 取消"
        : _pickHint.Length > 0
            ? _pickHint + " · 单击取点 · 右键 / Esc 取消"
            : "单击取点 · 右键 / Esc 取消";

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
        if (ShowMagnifier(_wantColor)) PlaceMagnifier(dip, ix, iy);
    }

    private void ConfirmPoint()
    {
        if (!_hasPoint) return;
        Confirmed = true;
        Close();
    }
}
