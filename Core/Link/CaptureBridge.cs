using System;
using System.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Views;

namespace LTools.Core.Link;

/// <summary>
/// 截图的**执行器侧**：把「框选 / 直接截图」转给普通权限宿主（覆盖层、结果窗、贴图、采集历史都在那边）。
/// </summary>
/// <remarks>
/// <para>
/// 返回 <c>null</c> = 没转交（宿主没起 / 刚启动还没连上 / 对端出错），调用方照旧在本进程跑覆盖层——
/// 「宿主挂了」只是体验降级，不会让截图键按了没反应。
/// </para>
/// <para>
/// 转交之前要**自己**做三件事（宿主的窗口它自己会收，这里的窗口归提权进程）：
/// 收起全部窗口（<see cref="TransientMinimize"/>，**只在编辑器试运行时**——其余入口不动用户自己的窗口）、
/// 鼠标钩子给遮罩让路（<see cref="PickerHook"/>）、热键挂起（单键绑定会吞掉框选里的 <c>Enter</c> / <c>Esc</c>）。
/// 悬浮工具条与提示卡片同理。
/// </para>
/// <para>
/// 这个方法是**阻塞**的：调用方是功能 / 动作执行线程。停止热键不打断它（框选本来就是等用户），
/// 宿主的覆盖层自己收 Esc。
/// </para>
/// </remarks>
internal static class CaptureBridge
{
    /// <summary>框选可以让用户慢慢点：给一个够长的上限，只防对端消失后永远挂着。</summary>
    private const int RunTimeoutMs = 12 * 60 * 60 * 1000;

    /// <summary>直接截图（不弹窗）很快；宿主卡住时别把功能入口吊住。</summary>
    private const int DirectTimeoutMs = 15_000;

    private static readonly object Gate = new();

    /// <summary>录屏跑完要跑一次的回调（「截图」功能用它恢复被收起的自动化指示器）。</summary>
    private static Action? _afterRecording;

    /// <summary>弹覆盖层框选；<c>null</c> = 没转交（调用方本地跑）。</summary>
    /// <param name="hideOwnWindows">
    /// 抓帧前收不收 LTools 自己的窗口：只有动作编辑器试运行「截图」步骤才收
    /// （编辑器正挡着要截的地方）；面板 / 快捷键 / 悬浮发起的截图一律不动它们。
    /// </param>
    public static CaptureRunReply? TryRun(string pending, bool wantImage, bool hideOwnWindows,
        bool selectionOnly, Action? onRecordingFinished)
    {
        if (!LinkWorker.IsConnected) return null;

        // 先登记：录屏可能在回复到达之前就失败收尾（那样 recordDone 会先到，回调不能丢）。
        SetAfterRecording(onRecordingFinished);

        // 自己的窗口先收起（否则会被框进冻结画面），钩子让路 / 热键挂起 / 悬浮条与提示卡收起。
        Action restore = () => { };
        bool wasSuspended = HotkeyRegistry.Suspended;
        try
        {
            if (hideOwnWindows) Ui.Run(() => restore = TransientMinimize.MinimizeAll(null));
            PickerHook.SetActive(true);
            HotkeyRegistry.Suspended = true;
            FloatingToolbarService.SetCaptureActive(true);
            using var toasts = ToastService.SuppressForCapture();

            // 收起自己窗口才需要等最小化动画；不收就直接请求
            if (hideOwnWindows) Thread.Sleep(TransientMinimize.SettleMs);

            LinkMessage reply = LinkWorker
                .RequestAsync(LinkMethods.CaptureRun,
                    new CaptureRunRequest(pending, wantImage, hideOwnWindows, selectionOnly), RunTimeoutMs)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("Capture", $"宿主没能弹出截图覆盖层：{reply.Error}，改在本进程跑");
                return null;
            }

            CaptureRunReply? result = reply.Read<CaptureRunReply>();
            if (result == null || result.Exit != "recording") SetAfterRecording(null);
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"截图请求失败：{ex.Message}，改在本进程跑");
            SetAfterRecording(null);
            return null;
        }
        finally
        {
            HotkeyRegistry.Suspended = wasSuspended;
            PickerHook.SetActive(false);
            FloatingToolbarService.SetCaptureActive(false);
            Ui.Run(restore);
        }
    }

    /// <summary>
    /// 直接截图（当前显示器 / 前台窗口）：宿主抓帧、复制、记采集历史。
    /// </summary>
    /// <returns><c>true</c> = 宿主做完了；<c>false</c> = 没转成（调用方本地兜底）。</returns>
    public static bool TryRunDirect(int x, int y, int w, int h, string source, string scenario)
    {
        if (!LinkWorker.IsConnected) return false;

        try
        {
            LinkMessage reply = LinkWorker
                .RequestAsync(LinkMethods.CaptureDirect,
                    new CaptureDirectRequest(x, y, w, h, source, scenario), DirectTimeoutMs)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                AppLog.Warn("Capture", $"宿主没能直接截图：{reply.Error}，改在本进程跑");
                return false;
            }

            if (reply.Read<CaptureDirectReply>() is { Ok: false } failed)
            {
                AppLog.Warn("Capture", $"宿主直接截图失败：{failed.Error}，改在本进程跑");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"直接截图请求失败：{ex.Message}，改在本进程跑");
            return false;
        }
    }

    /// <summary>宿主说录屏跑完了：执行器恢复被收起的自动化指示器（在 UI 线程上做）。</summary>
    public static void OnRecordingFinished()
    {
        Action? callback;
        lock (Gate)
        {
            callback = _afterRecording;
            _afterRecording = null;
        }
        if (callback == null) return;
        Ui.Post(callback);
    }

    private static void SetAfterRecording(Action? callback)
    {
        lock (Gate) _afterRecording = callback;
    }
}
