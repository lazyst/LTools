using System.Windows;

namespace LTools.Core;

/// <summary>
/// 提示与确认服务。
/// <see cref="Notify"/>：显示跟随鼠标右下角的文本提示（不再用系统托盘气球，
/// 对应原版 AHK <c>ToolTip</c>），实现见 <see cref="MouseTip"/>。
/// 确认/提示弹窗统一走 <see cref="LTools.Views.ConfirmDialog"/>（Modern 风格）。
/// </summary>
internal static class TrayService
{
    /// <summary>显示跟随鼠标的提示文本（替代系统通知）。</summary>
    public static void Notify(string message) => MouseTip.Show(message);
}
