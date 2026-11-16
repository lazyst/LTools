using System;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Hooks;
using LTools.Native;
using LTools.Views;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>一次截图的成品：屏幕矩形（物理像素）+ 按选区重绘出来的图片。</summary>
internal sealed record CaptureResult(Int32Rect Area, BitmapSource Image);

/// <summary>
/// 截图覆盖层的唯一入口：收起 LTools 自己的窗口 → 冻结抓帧 → 打开覆盖层 → 交回成品。
/// </summary>
/// <remarks>
/// 三条必须在 <c>finally</c> 里成对还原的东西：
/// ① <see cref="TransientMinimize"/>（不收起自己的窗口会把自己截进去，也会挡住框选）；
/// ② <see cref="MouseHook.SetWindowPickerActive"/>（否则「裸右键长按 = 面板」会吞掉右键，用户要按两次 Esc）；
/// ③ <see cref="HotkeyRegistry.Suspended"/>（单键绑定会吞掉框选里的按键，<c>Enter</c> / <c>Esc</c> 就没了）。
/// 图片走 <see cref="CaptureDocument.Render"/>：按选区**重绘**，不是截屏再裁。
/// </remarks>
internal static class CaptureSession
{
    /// <summary>弹出覆盖层框选 + 标注。用户取消时返回 null。</summary>
    public static CaptureResult? TryCapture(Window? owner)
    {
        var restore = TransientMinimize.MinimizeAll(owner);
        bool wasSuspended = HotkeyRegistry.Suspended;
        MouseHook.SetWindowPickerActive(true);
        HotkeyRegistry.Suspended = true;

        try
        {
            System.Threading.Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完

            var monitor = ScreenPicker.MonitorUnderCursor();
            using var bmp = ScreenCapture.Capture(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            var shot = ScreenPicker.ToBitmapSource(bmp);
            var (sx, sy) = ScreenPickerWindow.MonitorScale(monitor);

            var overlay = new CaptureOverlayWindow(shot, monitor, sx, sy);
            bool? ok = overlay.ShowDialog();
            if (ok != true || overlay.ResultImage == null) return null;

            var result = new CaptureResult(overlay.Document.Area, overlay.ResultImage);

            // 「贴图」出口：覆盖层已经关掉，现在把成品钉到屏幕上（正好盖住刚才截的那块）
            if (overlay.PinRequested) CapturePinManager.Pin(result.Image, result.Area, overlay.MonitorScale);

            return result;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.TryCapture", ex);
            TrayService.Notify("截图失败：" + ex.Message);
            return null;
        }
        finally
        {
            HotkeyRegistry.Suspended = wasSuspended;
            MouseHook.SetWindowPickerActive(false);
            restore();
        }
    }
}
