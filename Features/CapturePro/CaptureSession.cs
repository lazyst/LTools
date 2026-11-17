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
        bool pin = false, recognize = false, scroll = false, live = false, record = false;
        var recordArea = Int32Rect.Empty;
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
            if (ok != true) return null;

            // 录屏出口**没有成品图**（这次不是截图，只是借覆盖层挑区域）：
            // 先记下区域，等 finally 把自己的窗口还原之后再开录，免得遮罩被录进去。
            if (overlay.RecordRequested)
            {
                record = true;
                recordArea = overlay.Document.Area;
                return null;
            }

            if (overlay.ResultImage == null) return null;

            var captured = overlay.ResultImage;
            pin = overlay.PinRequested;
            recognize = overlay.RecognizeRequested;
            scroll = overlay.ScrollRequested;
            live = overlay.LiveRequested;
            finish = overlay.Finish;

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
        // 录屏排在前面：这一次没有截图成品，也不记采集历史。
        if (record)
        {
            StartRecording(recordArea);
            return null;
        }

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
        if (live) StartLivePin(result);
        if (recognize)
            CaptureResultWindow.Show(result.Image, OcrEnginePreference.DefaultForNewProbe, entry);

        return result;
    }

    /// <summary>
    /// 「录屏」出口：把覆盖层里框的那一块交给 <see cref="RecordSession"/>（3 秒倒计时后开录）。
    /// </summary>
    /// <remarks>
    /// 放后台线程：抓帧 + 编码是重活，而 UI 线程上还挂着低级鼠标钩子。
    /// 失败用**常驻**提示——录屏失败时自己的窗口往往是收起来的，一闪而过的提示看不到。
    /// </remarks>
    private static void StartRecording(Int32Rect area)
    {
        if (area.Width < 2 || area.Height < 2) return;

        var plan = new RecordPlan { Mode = RecordMode.Region };
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                RecordSession.TryRun(null, plan, area);
            }
            catch (Exception ex)
            {
                CrashLog.Write("CapturePro.Record", ex);
                ToastService.Show("录屏失败：" + ex.Message, "录屏", 0, ToastKind.error);
            }
        });
    }

    /// <summary>
    /// 实时贴图：跟住**选区中心下面的顶层窗口**。取不到（桌面 / 任务栏 / 自己的窗口）就退回普通贴图，
    /// 并说明一句——静默退化成静态图会让人以为「实时没生效」。
    /// </summary>
    /// <remarks>
    /// 必须在覆盖层关掉、自己的窗口还原之后再取：那一拍屏幕上的窗口就是用户框选时看到的那些，
    /// 覆盖层自己（盖住全屏、句柄在最上面）已经不在。
    /// </remarks>
    private static void StartLivePin(CaptureResult result)
    {
        var hwnd = WindowAt(result.Area);
        if (hwnd == IntPtr.Zero)
        {
            CapturePinManager.Pin(result.Image, result.Area);
            ToastService.Show("没找到可跟随的窗口，已贴成普通图片", "实时贴图", 2800,
                ToastKind.warning, key: "pin-live");
            return;
        }

        CapturePinManager.PinLive(result.Image, result.Area, hwnd);
    }

    /// <summary>选区中心那个顶层窗口；桌面 / 任务栏 / LTools 自己（刚还原的窗口）都不算。</summary>
    private static IntPtr WindowAt(Int32Rect area)
    {
        int x = area.X + area.Width / 2;
        int y = area.Y + area.Height / 2;

        var hwnd = Win32.TopLevelWindowAtPoint(x, y);
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        if (ForegroundApp.IsShellWindow(hwnd) || ForegroundApp.IsOwnWindow(hwnd)) return IntPtr.Zero;

        return hwnd;
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

    /// <summary>采集历史里的「完成动作」文案（与覆盖层工具条里的叫法一致）。</summary>
    private static string SourceOf(CaptureOverlayWindow.FinishKind kind) => kind switch
    {
        CaptureOverlayWindow.FinishKind.Save => "另存为",
        CaptureOverlayWindow.FinishKind.QuickSave => "快速保存",
        CaptureOverlayWindow.FinishKind.Print => "打印",
        CaptureOverlayWindow.FinishKind.Pin => "贴图",
        CaptureOverlayWindow.FinishKind.Recognize => "识别",
        CaptureOverlayWindow.FinishKind.Scroll => "长截图",
        CaptureOverlayWindow.FinishKind.Live => "实时贴图",
        _ => "复制",
    };
}
