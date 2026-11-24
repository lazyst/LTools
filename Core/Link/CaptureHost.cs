using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.Link;

/// <summary>
/// 截图的**宿主侧**：覆盖层 / 结果窗 / 贴图 / 采集历史都跑在这里（`docs/PROCESS_SPLIT.md` §15.4 第 3 步）。
/// </summary>
/// <remarks>
/// <para>
/// 入口是 <see cref="CaptureSession.TryCapture"/>（它按进程身份分流，宿主里就是本地跑），
/// 所以「截图怎么工作」仍然只有一份实现；这里只负责「在 UI 线程上跑 + 把结果翻成链路消息」。
/// </para>
/// <para>
/// 框选是模态的（<c>ShowDialog</c>），会占住宿主 UI 线程直到用户选完 / 按 Esc——与屏幕取点、系统文件框
/// 同一个已知限制：**程序关不掉它**，停止热键只结束本次运行，框留到用户自己取消。
/// </para>
/// </remarks>
internal static class CaptureHost
{
    private static LinkServer? _link;

    /// <summary>宿主启动链路服务端后挂上（与 <see cref="PanelHostBridge.Attach"/> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>录屏跑完了：告诉提权进程去恢复它临时收起的自动化指示器（它不是取消，别让人以为截图卡住）。</summary>
    public static void NotifyRecordingFinished()
    {
        if (!AppRole.IsUiHost) return;      // 提权进程里自己就跑了回调，不需要绕一圈
        try { _link?.NotifyAsync(LinkMethods.EventCaptureRecordDone); }
        catch (Exception ex) { AppLog.Warn("Capture", $"通知录屏结束失败：{ex.Message}"); }
    }

    /// <summary>处理一条截图请求；不是截图方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) => request.Method switch
    {
        LinkMethods.CaptureRun => RunAsync(request),
        LinkMethods.CaptureDirect => DirectAsync(request),
        _ => null,
    };

    private static async Task<LinkMessage> RunAsync(LinkMessage request)
    {
        CaptureRunRequest? req = request.Read<CaptureRunRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        var pending = Enum.TryParse<CaptureOverlayWindow.PendingAction>(req.Pending, ignoreCase: true, out var parsed)
            ? parsed
            : CaptureOverlayWindow.PendingAction.None;

        var done = new TaskCompletionSource<(CaptureSession.CaptureExit Exit, CaptureOutcome? Outcome)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 覆盖层是模态框：整个框选期间都占着 UI 线程，结果回来时任务才完成。
        Ui.Post(() =>
        {
            try
            {
                Window? owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
                CaptureOutcome? outcome = CaptureSession.TryCapture(owner, out var exit, pending, req.WantImage);
                done.TrySetResult((exit, outcome));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });

        try
        {
            var (exit, outcome) = await done.Task.ConfigureAwait(false);
            var area = outcome?.Area ?? Int32Rect.Empty;
            return LinkMessage.Response(request, new CaptureRunReply(
                NameOf(exit), area.X, area.Y, area.Width, area.Height, outcome?.File));
        }
        catch (Exception ex)
        {
            return LinkMessage.Response(request, error: ex.Message);
        }
    }

    private static Task<LinkMessage> DirectAsync(LinkMessage request)
    {
        CaptureDirectRequest? req = request.Read<CaptureDirectRequest>();
        if (req == null) return Task.FromResult(LinkMessage.Response(request, error: "缺少参数"));

        try
        {
            // 直接截图不建窗口（抓帧 + 剪贴板 + 记采集历史）：链路线程上跑就行，别排队等 UI。
            CaptureSession.CaptureDirect(
                new System.Drawing.Rectangle(req.X, req.Y, req.W, req.H), req.Source ?? "", req.Scenario ?? "");
            return Task.FromResult(LinkMessage.Response(request, new CaptureDirectReply(true, null)));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Capture", $"直接截图失败：{ex.Message}");
            return Task.FromResult(LinkMessage.Response(request, new CaptureDirectReply(false, ex.Message)));
        }
    }

    private static string NameOf(CaptureSession.CaptureExit exit) => exit switch
    {
        CaptureSession.CaptureExit.Captured => "captured",
        CaptureSession.CaptureExit.Recording => "recording",
        _ => "cancelled",
    };
}
