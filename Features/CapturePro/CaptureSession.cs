using System;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
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

        CaptureResult? result = null;
        bool pin = false, recognize = false;
        double scale = 1;
        var finish = CaptureOverlayWindow.FinishKind.Copy;

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

            result = new CaptureResult(overlay.Document.Area, overlay.ResultImage);
            pin = overlay.PinRequested;
            recognize = overlay.RecognizeRequested;
            scale = overlay.MonitorScale;
            finish = overlay.Finish;
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

        // 两个「关掉覆盖层之后才做」的出口：这时自己的窗口已经还原，不会被一起收走。
        if (result == null) return null;

        // 采集历史：先记一条（只写 catalog.json），图片丢后台写；识别文字稍后由结果窗回填。
        var entry = CaptureCatalog.Create(
            result.Image.PixelWidth, result.Image.PixelHeight,
            recognize ? CaptureCatalog.KindRecognize : CaptureCatalog.KindShot,
            SourceOf(finish));

        var image = result.Image;
        System.Threading.Tasks.Task.Run(() => CaptureCatalog.SaveAssets(entry, image));

        if (pin) CapturePinManager.Pin(result.Image, result.Area, scale);
        if (recognize)
            CaptureResultWindow.Show(result.Image, OcrEnginePreference.DefaultForNewProbe, entry);

        return result;
    }

    /// <summary>采集历史里的「完成动作」文案（与覆盖层动作栏的叫法一致）。</summary>
    private static string SourceOf(CaptureOverlayWindow.FinishKind kind) => kind switch
    {
        CaptureOverlayWindow.FinishKind.Save => "另存为",
        CaptureOverlayWindow.FinishKind.Pin => "贴图",
        CaptureOverlayWindow.FinishKind.Recognize => "识别",
        _ => "复制",
    };
}
