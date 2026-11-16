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
/// 冻结范围是**所有显示器的并集**（<see cref="ScreenShot.Bounds"/>），所以框选可以跨屏拖；
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
        bool pin = false, recognize = false, scroll = false;
        var finish = CaptureOverlayWindow.FinishKind.Copy;

        try
        {
            System.Threading.Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完

            // 覆盖层盖住**所有显示器**（跨屏选区）：冻结画面逐块抓再拼，坐标仍是物理像素
            var area = ScreenShot.Bounds();
            var shot = ScreenShot.Capture(area);
            var (sx, sy) = ScreenPickerWindow.MonitorScale(ScreenShot.ToRectangle(area));

            var overlay = new CaptureOverlayWindow(shot, ScreenShot.ToRectangle(area), sx, sy);
            bool? ok = overlay.ShowDialog();
            if (ok != true || overlay.ResultImage == null) return null;

            var captured = overlay.ResultImage;
            pin = overlay.PinRequested;
            recognize = overlay.RecognizeRequested;
            scroll = overlay.ScrollRequested;
            finish = overlay.Finish;

            // 长截图在覆盖层关掉之后、自己的窗口还原之前跑：收起的窗口不会被滚进画面，
            // 热键 / 鼠标钩子也还没恢复，自动滚屏不会撞上它们。
            if (scroll)
            {
                var stitched = CaptureScrollWindow.Run(overlay.Document);
                if (stitched == null) return null;   // 用户取消了长截图
                captured = stitched;
            }

            result = new CaptureResult(overlay.Document.Area, captured);
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

        // 长截图：拼接结果默认直接复制走（覆盖层那边不复制，只有这里知道最终的长图）
        if (scroll)
        {
            try
            {
                NativeClipboard.SetImage(result.Image);
                ToastService.Show(
                    $"{result.Image.PixelWidth} × {result.Image.PixelHeight} px 已复制到剪贴板",
                    "长截图", 3000, ToastKind.success);
            }
            catch (Exception ex)
            {
                CrashLog.Write("CapturePro.ScrollCopy", ex);
                TrayService.Notify("长截图复制失败：" + ex.Message);
            }
        }

        // 采集历史：先记一条（只写 catalog.json），图片丢后台写；识别文字稍后由结果窗回填。
        var entry = CaptureCatalog.Create(
            result.Image.PixelWidth, result.Image.PixelHeight,
            recognize ? CaptureCatalog.KindRecognize : CaptureCatalog.KindShot,
            SourceOf(finish));

        var image = result.Image;
        System.Threading.Tasks.Task.Run(() => CaptureCatalog.SaveAssets(entry, image));

        if (pin) CapturePinManager.Pin(result.Image, result.Area);
        if (recognize)
            CaptureResultWindow.Show(result.Image, OcrEnginePreference.DefaultForNewProbe, entry);

        return result;
    }

    /// <summary>
    /// 「贴图内标注」：把一张已有的图交给覆盖层编辑（覆盖层正好盖在那张贴图上），
    /// 返回标注完的成品；取消 = null。
    /// </summary>
    /// <remarks>
    /// 热键挂起 + 鼠标钩子让路与 <see cref="TryCapture"/> 是同一套（漏一处就是「标注时按一下字母触发了别的动作」），
    /// 所以入口收在这里，别在贴图窗口里另写一份。**不记采集历史**：标的是已有的图，不是新采集。
    /// </remarks>
    public static BitmapSource? TryEdit(BitmapSource image, Int32Rect area)
    {
        if (image == null || area.Width < 2 || area.Height < 2) return null;

        bool wasSuspended = HotkeyRegistry.Suspended;
        MouseHook.SetWindowPickerActive(true);
        HotkeyRegistry.Suspended = true;

        try
        {
            var (sx, sy) = ScreenPickerWindow.MonitorScale(ScreenShot.ToRectangle(area));
            var overlay = new CaptureOverlayWindow(image, ScreenShot.ToRectangle(area), sx, sy, editOnly: true);
            bool? ok = overlay.ShowDialog();
            return ok == true ? overlay.ResultImage : null;
        }
        catch (Exception ex)
        {
            CrashLog.Write("CapturePro.TryEdit", ex);
            TrayService.Notify("标注失败：" + ex.Message);
            return null;
        }
        finally
        {
            HotkeyRegistry.Suspended = wasSuspended;
            MouseHook.SetWindowPickerActive(false);
        }
    }

    /// <summary>采集历史里的「完成动作」文案（与覆盖层动作栏的叫法一致）。</summary>
    private static string SourceOf(CaptureOverlayWindow.FinishKind kind) => kind switch
    {
        CaptureOverlayWindow.FinishKind.Save => "另存为",
        CaptureOverlayWindow.FinishKind.QuickSave => "快速保存",
        CaptureOverlayWindow.FinishKind.Print => "打印",
        CaptureOverlayWindow.FinishKind.Pin => "贴图",
        CaptureOverlayWindow.FinishKind.Recognize => "识别",
        CaptureOverlayWindow.FinishKind.Scroll => "长截图",
        _ => "复制",
    };
}
