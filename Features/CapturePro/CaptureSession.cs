using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Hooks;
using LTools.Native;
using LTools.Views;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>一次截图的成品：屏幕矩形（物理像素）+ 按选区重绘出来的图片 + 图里烘进的外观。</summary>
internal sealed record CaptureResult(Int32Rect Area, BitmapSource Image,
    PinAppearance Appearance, bool Scrolling);

/// <summary>
/// 「从采集历史重新裁选」要交给覆盖层的东西：当时那张整屏冻结画面 + 它的屏幕矩形 +
/// 每一块选区（窗口 DIP）。
/// </summary>
internal sealed record CaptureReopen(BitmapSource Image, Int32Rect Rect,
    IReadOnlyList<SelectionRegion> Regions, double Scale,
    IReadOnlyList<Int32Rect>? Screens = null,
    IReadOnlyList<Mark>? Marks = null,
    IReadOnlyList<Mark[]>? Undo = null,
    IReadOnlyList<Mark[]>? Redo = null,
    CaptureStyleDto? Style = null);

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
    /// <summary>覆盖层交回什么：成品 / 用户取消 / 已交给录屏（**不是**取消）。</summary>
    internal enum CaptureExit
    {
        Captured,
        Cancelled,
        Recording,
    }

    /// <summary>弹出覆盖层框选 + 标注。用户取消时返回 null。</summary>
    public static CaptureResult? TryCapture(Window? owner) => TryCapture(owner, out _);

    /// <summary>
    /// 从采集历史重新编辑：用当时那张整屏冻结画面重开覆盖层，把每一块选区、选区外观与**标注**
    /// （含撤销 / 重做栈）原样摆回去，交回一次**新的**成品（新的采集记录、复制 / 贴图 / 识别等出口照常）。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>CaptureHistoryDraft</c> 的 <c>displays</c> + <c>selection</c> + <c>canvasHistory</c> 重开：
    /// 底图就是历史那张，不再抓帧；这条记录没有底图（录屏 / 更早版本记的）时提示一句并返回 null。
    /// </remarks>
    public static CaptureResult? TryReopen(CaptureEntry entry)
    {
        BitmapSource? display = null;
        if (entry.HasDisplay)
        {
            try { display = CaptureCatalog.LoadDisplay(entry); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Reopen", "读整屏底图失败：" + ex.Message); }
        }

        if (display == null)
        {
            ToastService.Show("这条记录没有整屏底图（老版本记录或录屏），没法重新编辑",
                "采集历史", 2800, ToastKind.warning);
            return null;
        }

        // 与当初那次截图同一套换算：窗口按该屏的 DPI 摆，选区是「物理像素 ÷ DPI = DIP」。
        // 用 1 会让窗口按 1:1 的 DIP 尺寸摆出去、被屏幕夹小，文档比例跟着散掉（裁出来会整体偏移）。
        var dpiScale = ScreenPickerWindow.MonitorScale(ScreenShot.ToRectangle(entry.DisplayRect));
        double dpi = dpiScale.X > 0 ? dpiScale.X : 1;

        var regions = CaptureRegionDto.RegionsOf(entry, dpi);

        var screens = new System.Collections.Generic.List<Int32Rect>(entry.Screens.Count);
        foreach (var screen in entry.Screens) screens.Add(screen.Rect);

        // 标注 / 撤销栈 / 选区外观：这一步才让「重新编辑」等于当初那个编辑现场，而不是一张烘好的图
        var marks = MarkCodec.Read(entry.Annotations);
        var undo = MarkCodec.ReadHistory(entry.UndoMarks);
        var redo = MarkCodec.ReadHistory(entry.RedoMarks);

        return TryCapture(null, out _,
            reopen: new CaptureReopen(display, entry.DisplayRect, regions, dpi, screens,
                marks, undo, redo, entry.Style));
    }

    /// <summary>
    /// 弹出覆盖层框选 + 标注。
    /// </summary>
    /// <param name="exit">
    /// 这条出口是什么：<see cref="CaptureExit.Recording"/> 时返回 null 但**不是**取消
    /// （用户点了「录屏」，录屏已经在后台开跑）。调用方别把这种情况当成「已取消截图」。
    /// </param>
    /// <param name="onRecordingFinished">
    /// 录屏跑完（成功 / 失败 / 取消都算）之后回调一次，跑在录屏的后台线程上。
    /// 「截图」这个内置功能用它恢复被自己临时收起的自动化指示器。
    /// </param>
    public static CaptureResult? TryCapture(Window? owner, out CaptureExit exit,
        Action? onRecordingFinished = null,
        CaptureOverlayWindow.PendingAction pending = CaptureOverlayWindow.PendingAction.None,
        CaptureReopen? reopen = null)
    {
        exit = CaptureExit.Cancelled;

        // 文件名模板里的 {AN} / {WN}：必须在收起自己的窗口**之前**取（之后前台就变成桌面或别人了）
        var names = ExportNameContext.Of(ForegroundAppCache.Current?.ExeName,
            ForegroundAppCache.Current?.Title);

        var restore = TransientMinimize.MinimizeAll(owner);
        bool wasSuspended = HotkeyRegistry.Suspended;
        MouseHook.SetWindowPickerActive(true);
        HotkeyRegistry.Suspended = true;
        // 悬浮工具条：截图 / 录屏期间收起（照上游 floating_toolbar/hide_during_capture）
        FloatingToolbarService.SetCaptureActive(true);

        CaptureResult? result = null;
        CapturePlacement? placement = null;
        BitmapSource? display = null;
        var displayRect = Int32Rect.Empty;
        IReadOnlyList<Int32Rect>? screens = null;
        CaptureEditState? edit = null;
        bool pin = false, recognize = false, recognizeCopy = false, translate = false;
        bool table = false;
        bool scroll = false, live = false;
        bool record = false;
        var recordArea = Int32Rect.Empty;
        var finish = CaptureOverlayWindow.FinishKind.Copy;
        var scrollExit = CaptureOverlayWindow.ScrollExit.Copy;

        try
        {
            System.Threading.Thread.Sleep(TransientMinimize.SettleMs);   // 等最小化动画结束、桌面重绘完

            // 快门音（照上游 screenshot/screenshot_sound_notification，默认关）：
            // 在上游是 beginCapture 里、冻结画面之前响，这里放在抓帧之前同一个时机。
            // 重新裁选用的是历史里的画面，没有「又拍了一张」，不响。
            if (reopen == null && CaptureSoundSettings.Screenshot) ShutterSound.Play();

            // 覆盖层盖住**所有显示器**（跨屏选区）：冻结画面逐块抓再拼，坐标仍是物理像素。
            // 重新裁选直接拿历史里那张整屏冻结画面，不再抓帧。
            var area = reopen?.Rect ?? ScreenShot.Bounds();
            screens = reopen?.Screens ?? ScreenShot.Monitors();
            var shot = reopen?.Image ?? ScreenShot.Capture(area, ScreenshotBehavior.CaptureCursor);
            var (sx, sy) = reopen != null
                ? (reopen.Scale > 0 ? reopen.Scale : 1, reopen.Scale > 0 ? reopen.Scale : 1)
                : ScreenPickerWindow.MonitorScale(ScreenShot.ToRectangle(area));

            var overlay = new CaptureOverlayWindow(shot, ScreenShot.ToRectangle(area), sx, sy,
                pending: pending);
            overlay.ExportNames = names;
            if (reopen is { Regions.Count: > 0 }) overlay.LoadSelection(reopen.Regions);

            // 「重新编辑截图」：把当时的外观与标注（含撤销 / 重做栈）一起摆回去，用户能接着改、也能撤销
            overlay.LoadCaptureStyle(reopen?.Style);
            if (reopen is { Marks.Count: > 0 } reopened)
                overlay.LoadAnnotations(reopened.Marks!, reopened.Undo ?? Array.Empty<Mark[]>(),
                    reopened.Redo ?? Array.Empty<Mark[]>());

            bool? ok = overlay.ShowDialog();
            if (ok != true) return null;

            {
                if (overlay.RecordRequested)
                {
                    // 录屏出口**没有成品图**（这次不是截图，只是借覆盖层挑区域）：
                    // 只记下区域，绝不能在这里 return——那会跳过下面的「关掉覆盖层之后才开录」。
                    record = true;
                    recordArea = overlay.Document.Area;
                }
                else
                {
                    if (overlay.ResultImage == null) return null;

                    var captured = overlay.ResultImage;
                    pin = overlay.PinRequested;
                    recognize = overlay.RecognizeRequested;
                    recognizeCopy = overlay.RecognizeCopyRequested;
                    translate = overlay.TranslateRequested;
                    table = overlay.TableRequested;
                    scroll = overlay.ScrollRequested;
                    scrollExit = overlay.ScrollAfter;
                    live = overlay.LiveRequested;
                    finish = overlay.Finish;

                    placement = CapturePlacement.Of(overlay.Document.Area, overlay.Document.Style,
                        overlay.Document.Scale, scroll, overlay.Document.Regions);

                    // 整屏底图：覆盖层手里那张（用户可能点过「重新截图」换过底图，别拿外面那份旧的）
                    var baseImage = overlay.Document.Base;
                    if (!baseImage.IsFrozen && baseImage.CanFreeze) baseImage.Freeze();
                    display = baseImage;
                    displayRect = new Int32Rect(area.X, area.Y, area.Width, area.Height);

                    // 翻着采集历史按了「完成」：底图是那条记录的整屏画面，
                    // 按它自己的矩形记（记成这次整块桌面会让底图与矩形对不上）
                    if (overlay.BrowsingHistory)
                    {
                        displayRect = overlay.Document.Monitor;
                        screens = new[] { displayRect };
                    }

                    // 标注 + 撤销 / 重做栈 + 选区外观：下一次「重新编辑截图」照它整个现场摆回来
                    edit = CaptureEditState.Of(overlay.Document, overlay.Document.UndoStack,
                        overlay.Document.RedoStack);

                    result = new CaptureResult(overlay.Document.Area, captured,
                        placement.Appearance, placement.Scrolling);
                }
            }
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
            FloatingToolbarService.SetCaptureActive(false);
            restore();
        }

        // 两个「关掉覆盖层之后才做」的出口：这时自己的窗口已经还原，不会被一起收走。
        // 录屏排在前面：这一次没有截图成品，也不记采集历史。
        if (record)
        {
            if (StartRecording(recordArea, onRecordingFinished)) exit = CaptureExit.Recording;
            return null;
        }

        if (result == null) return null;

        // 长截图面板上的「编辑 / 切图」：成品图只交给对应的窗口，不复制、不记采集历史
        // （那是同一个画面的二次加工，不是新采集）。
        if (scrollExit != CaptureOverlayWindow.ScrollExit.Copy)
        {
            string name = $"长截图 {DateTime.Now:HHmmss}";
            if (scrollExit == CaptureOverlayWindow.ScrollExit.Edit)
                ImageEditSession.OpenImage(result.Image, name);
            else
                SplitSession.OpenImage(result.Image, name);

            exit = CaptureExit.Captured;
            return null;
        }

        // 长截图：拼接结果默认直接复制走（覆盖层那边不复制，只有这里知道最终的长图）
        if (scroll)
        {
            try
            {
                CaptureSave.CopyImage(result.Image, ExportSettings.ScenarioCopyScrolling, names);
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
        // 落位信息一起记：采集历史要能「按原选区 + 原外观」贴回（见 HistoryPinPlacement）；
        // 整屏底图也一起记：历史里能看当时整个屏幕，还能重新裁选。
        var entry = CaptureCatalog.Create(
            result.Image.PixelWidth, result.Image.PixelHeight,
            recognize ? CaptureCatalog.KindRecognize : CaptureCatalog.KindShot,
            SourceOf(finish), placement, display == null ? null : displayRect, screens, edit);

        var image = result.Image;
        var screen = display;
        System.Threading.Tasks.Task.Run(() =>
        {
            CaptureCatalog.SaveAssets(entry, image);
            if (screen != null) CaptureCatalog.SaveDisplay(entry, screen);
        });

        if (pin) CapturePinManager.Pin(result.Image, result.Area, appearance: result.Appearance);
        if (live) StartLivePin(result);
        if (recognize)
        {
            // 「快速识别」入口（ltools.captureRecognize / captureRecognizeCopy）才认 quick_* 自动动作
            bool quick = pending is CaptureOverlayWindow.PendingAction.Recognize
                or CaptureOverlayWindow.PendingAction.RecognizeCopy;
            CaptureResultWindow.Show(result.Image, OcrEnginePreference.DefaultForNewProbe, entry,
                result.Area, recognizeCopy, translate, quick, table ? "table" : null);
        }

        exit = CaptureExit.Captured;
        return result;
    }

    /// <summary>
    /// 「录屏」出口：把覆盖层里框的那一块交给 <see cref="RecordSession"/>（3 秒倒计时后开录）。
    /// </summary>
    /// <remarks>
    /// 放后台线程：抓帧 + 编码是重活，而 UI 线程上还挂着低级鼠标钩子。
    /// 失败用**常驻**提示——录屏失败时自己的窗口往往是收起来的，一闪而过的提示看不到。
    /// 返回 false = 选区太小，没开录（调用方不能把它当成「已经交给录屏」）。
    /// </remarks>
    private static bool StartRecording(Int32Rect area, Action? finished)
    {
        if (area.Width < 2 || area.Height < 2)
        {
            ToastService.Show("选区太小，没法录屏（至少 2 × 2）", "录屏", 2400, ToastKind.warning);
            return false;
        }

        // 覆盖层入口照 Snow Shot：先把面板摆出来等用户点「录制」，倒计时在点完之后才开始。
        // 格式 / 帧率 / 等待秒数 / 跟随鼠标都取设置里的默认值（见「录屏」设置卡片）。
        var plan = RecordPlan.FromSettings(AppState.Record, promptStart: true);
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
            finally
            {
                try { finished?.Invoke(); }
                catch (Exception ex) { AppLog.Warn("CapturePro.Record", "录屏收尾回调失败：" + ex.Message); }
            }
        });

        return true;
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
            CapturePinManager.Pin(result.Image, result.Area, appearance: result.Appearance);
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
    /// 直接截图（不弹覆盖层）：抓一块 → 复制到剪贴板 → 记一条采集历史。
    /// </summary>
    /// <remarks>
    /// 照 Snow Shot 的 <c>DirectCaptureWorkflow</c>（copy → 自动保存 → history）：
    /// 「截取当前显示器」「截取前台窗口」这两个入口不经过覆盖层，抓完直接落剪贴板。
    /// <paramref name="scenario"/> 是「执行完自动保存」的场景键（<see cref="ExportSettings.Scenarios"/>）。
    /// 失败抛异常（调用方是功能入口，由 <c>FeatureRunner</c> 弹失败提示）。
    /// </remarks>
    public static BitmapSource? CaptureDirect(System.Drawing.Rectangle area, string source, string scenario)
    {
        if (area.Width < 1 || area.Height < 1) return null;

        // 快门音（照上游 screenshot/shutter_sound_notification，默认开）：
        // 上游在 DirectCaptureWorkflow.enqueue 里就响，也就是抓图之前。
        if (CaptureSoundSettings.Shutter) ShutterSound.Play();

        // 文件名模板的 {AN} / {WN}：直接截图时前台就是目标窗口
        var names = ExportNameContext.Of(ForegroundAppCache.Current?.ExeName,
            ForegroundAppCache.Current?.Title);

        var image = ScreenCapture.CaptureSource(area.Left, area.Top, area.Width, area.Height);

        // 直接截图同样是「截图」成品：按设置决定要不要把指针画进去
        if (ScreenshotBehavior.CaptureCursor)
            image = ScreenShot.WithCursor(image,
                new Int32Rect(area.Left, area.Top, image.PixelWidth, image.PixelHeight));

        // 直接截图没有选区外观，也没有「贴图窗口」这一层：窗口矩形就是抓的那块，
        // 外观是空的。带上落位元数据后「贴剪贴板」能贴回原来的位置（照上游）。
        var capture = new ClipboardCapture(
            new Int32Rect(area.Left, area.Top, image.PixelWidth, image.PixelHeight), PinAppearance.None);
        CaptureSave.CopyImage(image, scenario, names, capture,
            new Int32Rect(area.Left, area.Top, image.PixelWidth, image.PixelHeight));

        var entry = CaptureCatalog.Create(image.PixelWidth, image.PixelHeight,
            CaptureCatalog.KindShot, source);
        System.Threading.Tasks.Task.Run(() => CaptureCatalog.SaveAssets(entry, image));
        return image;
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
        CaptureOverlayWindow.FinishKind.RecognizeCopy => "识别并复制",
        CaptureOverlayWindow.FinishKind.Scroll => "长截图",
        CaptureOverlayWindow.FinishKind.Live => "实时贴图",
        _ => "复制",
    };
}
