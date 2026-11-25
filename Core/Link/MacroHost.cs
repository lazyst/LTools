using System;
using System.Threading;
using System.Threading.Tasks;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 键鼠宏的**执行器侧**：录制与试放要钩子 / 键鼠注入，改名与删宏要动动作（内容属主在这里）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="MacroBridge"/> 配对：宿主发命令，这里做，然后把录制 / 回放的生命周期**广播**回去
/// （<c>macro.started</c> / <c>stopped</c> / <c>saved</c> / <c>played</c>）。
/// </para>
/// <para>
/// 广播不带 token 是刻意的：录制是进程内唯一的，谁发起的都一样，宿主那边只要有窗口藏着就显示回来。
/// 用热键开始的录制也走这条广播（<see cref="Start"/> 在 <c>App.OnStartup</c> 里调一次，
/// 不能只靠「第一次收到宏命令」时才挂事件——那时热键录制可能早就开始了）。
/// </para>
/// <para>
/// 改数据（改名 / 删宏）排到 UI 线程：删除会连带清面板槽位 / 快捷键 / 触发规则 / 悬浮项，
/// 悬浮窗的刷新本来就只能在 UI 线程。链路读循环在别的线程上等这个答复，所以**不能**在读循环里同步等 UI。
/// </para>
/// </remarks>
internal static class MacroHost
{
    private static int _started;

    /// <summary>挂上录制 / 回放的广播（幂等；<c>App.OnStartup</c> 调一次）。</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        MacroRecorder.RecordingStarted += () => Notify(MacroMethods.EventStarted);
        MacroRecorder.RecordingStopped += () => Notify(MacroMethods.EventStopped);
        MacroRecorder.MacroSaved += id => Notify(MacroMethods.EventSaved, new MacroSavedEvent(id));

        // 试放（面板 / 设置页 / 链路发起的都算）：跑完通知宿主把藏起来的窗口显示回来。
        MacroWrites.Played += error => Notify(MacroMethods.EventPlayed, new MacroPlayEvent(error));
    }

    /// <summary>这五条方法名归这里处理（同步入口与异步链路共用一张名单）。</summary>
    public static bool IsCommand(string method) => method is MacroMethods.Record or MacroMethods.Stop
        or MacroMethods.Play or MacroMethods.Rename or MacroMethods.Delete;

    /// <summary>处理一条宏命令；不是这些方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkChannel channel, LinkMessage request)
    {
        if (!IsCommand(request.Method)) return null;

        return request.Method switch
        {
            MacroMethods.Record => Task.FromResult(Record(request)),
            MacroMethods.Stop => Task.FromResult(Stop(request)),
            MacroMethods.Play => Task.FromResult(Play(request)),
            MacroMethods.Rename => OnUi(request, Rename),
            _ => OnUi(request, r => Delete(channel, r)),
        };
    }

    /// <summary>同步执行一条宏命令（自测 / 冒烟直接调它，不必起管道）。</summary>
    public static LinkMessage Handle(LinkMessage request) => request.Method switch
    {
        MacroMethods.Record => Record(request),
        MacroMethods.Stop => Stop(request),
        MacroMethods.Play => Play(request),
        MacroMethods.Rename => Rename(request),
        MacroMethods.Delete => Delete(null, request),
        _ => LinkMessage.Response(request, error: "未知的宏命令：" + request.Method),
    };

    // ============ 录制 ============

    private static LinkMessage Record(LinkMessage request)
    {
        MacroRecordRequest? req = request.Read<MacroRecordRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        if (MacroRecorder.IsRecording)
            return LinkMessage.Response(request, new MacroRecordReply(false, "已经在录制了"));

        // 被拒绝时 MacroRecorder 自己会弹提示（宏功能关了 / 正在回放 / 有动作在跑）：这里只回
        // 「没开始」，**不带原因**——宿主看到空原因就不再多弹一张卡片。
        if (string.IsNullOrWhiteSpace(req.Id)) MacroRecorder.Start();
        else MacroRecorder.StartInto(req.Id, req.Name);

        return LinkMessage.Response(request, new MacroRecordReply(MacroRecorder.IsRecording, null));
    }

    private static LinkMessage Stop(LinkMessage request)
    {
        MacroRecorder.Stop(force: true);
        return LinkMessage.Response(request);
    }

    // ============ 试放 ============

    private static LinkMessage Play(LinkMessage request)
    {
        MacroPlayRequest? req = request.Read<MacroPlayRequest>();
        if (req == null || string.IsNullOrWhiteSpace(req.Id))
            return LinkMessage.Response(request, new MacroPlayReply(false, "缺少宏 Id"));

        bool started = MacroWrites.Play(req.Id, out string? error);
        return LinkMessage.Response(request, new MacroPlayReply(started, error));
    }

    // ============ 改名 / 删除 ============

    private static LinkMessage Rename(LinkMessage request)
    {
        MacroRenameRequest? req = request.Read<MacroRenameRequest>();
        if (req == null || string.IsNullOrWhiteSpace(req.Id)) return LinkMessage.Response(request, error: "缺少参数");

        return MacroWrites.Rename(req.Id, req.Name, out string? error)
            ? LinkMessage.Response(request)
            : LinkMessage.Response(request, error: error);
    }

    private static LinkMessage Delete(LinkChannel? channel, LinkMessage request)
    {
        MacroDeleteRequest? req = request.Read<MacroDeleteRequest>();
        if (req == null || string.IsNullOrWhiteSpace(req.Id))
            return LinkMessage.Response(request, new MacroDeleteReply(0, "缺少宏 Id"));

        if (!MacroWrites.Delete(req.Id, out int removedActions, out string? error))
            return LinkMessage.Response(request, new MacroDeleteReply(0, error));

        // 删宏会连带删掉引用它的动作、清掉面板槽位 / 快捷键 / 触发 / 悬浮项——宿主那份全是旧的。
        if (removedActions > 0 && channel != null)
            _ = channel.NotifyAsync(LinkMethods.EventActionsChanged);

        return LinkMessage.Response(request, new MacroDeleteReply(removedActions, null));
    }

    // ============ 工具 ============

    /// <summary>把一条要碰 UI 线程的命令排过去，返回等它做完的 Task（读循环可以继续收答复）。</summary>
    private static Task<LinkMessage> OnUi(LinkMessage request, Func<LinkMessage, LinkMessage> apply)
    {
        var completion = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Enqueue(() =>
        {
            try { completion.TrySetResult(apply(request)); }
            catch (Exception ex)
            {
                AppLog.Warn("Macro", $"宏命令失败（{request.Method}）：{ex.Message}");
                completion.TrySetResult(LinkMessage.Response(request, error: ex.Message));
            }
        });
        return completion.Task;
    }

    private static void Notify(string method, object? payload = null)
    {
        try { _ = LinkWorker.NotifyAsync(method, payload); }
        catch (Exception ex) { AppLog.Warn("Macro", $"通知宿主失败（{method}）：{ex.Message}"); }
    }
}
