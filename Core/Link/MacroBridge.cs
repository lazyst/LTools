using System;
using System.Threading;
using System.Threading.Tasks;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 键鼠宏的**宿主侧**：录制 / 试放 / 改名 / 删宏交给执行器（要钩子，动作属主也在那边）。
/// </summary>
/// <remarks>
/// <para>
/// 只有宿主里的窗口会调；执行器自己走 <see cref="MacroWrites"/> 的本地分支。失败一律给可显示的
/// 原因（超时 / 没连上 / 执行器拒绝），调用方据此提示，别假装成功。
/// </para>
/// <para>
/// 录制 / 回放的通知是**广播**（见 <see cref="MacroMethods"/>）：录制是进程内唯一的，所以
/// <see cref="IsRecording"/> 既在「自己发起」时置位，也在收到 <c>macro.started</c> 时置位——
/// 用热键开始录制时，开着的设置页一样要跟着让开。
/// </para>
/// </remarks>
internal static class MacroBridge
{
    /// <summary>开始 / 试放的答复只要「有没有真的开始」，给宽一点无所谓。</summary>
    private const int StartTimeoutMs = 3_000;

    /// <summary>改名 / 删宏会连带改动作并落盘（再反向请求本进程写目录），给宽一点；超时算失败。</summary>
    private const int WriteTimeoutMs = 10_000;

    private static LinkServer? _link;
    private static int _recording;
    private static int _stops;

    /// <summary>录制真的开始（自己发起的与热键发起的都算）。</summary>
    public static event Action? Started;

    /// <summary>录制结束（没录到内容也算结束）：藏起来的窗口可以回来了。</summary>
    public static event Action? Stopped;

    /// <summary>宏已保存（宏 Id）：横幅 / 列表刷新摘要。</summary>
    public static event Action<string>? Saved;

    /// <summary>试放跑完（失败原因，null = 正常放完）。</summary>
    public static event Action<string?>? Played;

    public static bool IsRecording => Volatile.Read(ref _recording) != 0;

    /// <summary>宿主启动链路服务端后挂上（与其它 Bridge 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>开始录制（<paramref name="id"/> 非空 = 重新录制到那个宏）。</summary>
    public static bool StartInto(string? id, string? name, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上，没能开始录制";
            return false;
        }

        int stops = Volatile.Read(ref _stops);

        try
        {
            LinkMessage reply = link.RequestAsync(MacroMethods.Record,
                    new MacroRecordRequest(id, name), timeoutMs: StartTimeoutMs)
                .GetAwaiter().GetResult();

            MacroRecordReply? result = reply.Read<MacroRecordReply>();
            if (!reply.Ok || result is not { Started: true })
            {
                // 原因全是 null = 执行器那边已经提示过（宏功能关了 / 正在回放 / 有动作在跑），
                // 调用方看到空原因就别再弹第二张卡片。
                error = result?.Error ?? reply.Error;
                return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        // 事件可能比答复先到（执行器回完就广播）：只有这期间没停过才把本地状态置成录制中，
        // 否则会把刚收到的 `macro.stopped` 又翻回「正在录制」。
        if (Volatile.Read(ref _stops) == stops) Volatile.Write(ref _recording, 1);
        return true;
    }

    /// <summary>结束录制（关编辑器时兜底）；结束通知照常回来。</summary>
    public static void Stop()
    {
        LinkServer? link = _link;
        if (link == null || !link.IsConnected) return;
        _ = Task.Run(async () =>
        {
            try { await link.RequestAsync(MacroMethods.Stop, timeoutMs: StartTimeoutMs); }
            catch (Exception ex) { AppLog.Warn("Macro", "停止录制失败：" + ex.Message); }
        });
    }

    /// <summary>试放一个宏；回放本身在后台跑，跑完发 <see cref="Played"/>。</summary>
    public static bool Play(string id, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上，没有试放";
            return false;
        }

        try
        {
            LinkMessage reply = link.RequestAsync(MacroMethods.Play, new MacroPlayRequest(id),
                    timeoutMs: StartTimeoutMs)
                .GetAwaiter().GetResult();

            MacroPlayReply? result = reply.Read<MacroPlayReply>();
            if (!reply.Ok || result is not { Started: true })
            {
                error = result?.Error ?? reply.Error ?? "没能开始试放";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>改名（元数据 + 引用它的动作名）。</summary>
    public static bool Rename(string id, string name, out string? error) =>
        Send(MacroMethods.Rename, new MacroRenameRequest(id, name), out error) != null;

    /// <summary>删宏（事件文件 + 元数据 + 引用它的动作）。<paramref name="removedActions"/> = 一起删掉的动作数。</summary>
    public static bool Delete(string id, out int removedActions, out string? error)
    {
        removedActions = 0;
        error = null;

        LinkMessage? reply = Send(MacroMethods.Delete, new MacroDeleteRequest(id), out error);
        if (reply == null) return false;

        MacroDeleteReply? result = reply.Read<MacroDeleteReply>();
        if (result == null)
        {
            error = "主程序没回删宏结果";
            return false;
        }

        removedActions = result.RemovedActions;
        return true;
    }

    /// <summary>执行器发来的宏通知（录制 / 试放）；不是这些方法时什么都不做。</summary>
    public static void OnEvent(LinkMessage message)
    {
        switch (message.Method)
        {
            case MacroMethods.EventStarted:
                Volatile.Write(ref _recording, 1);
                Started?.Invoke();
                break;

            case MacroMethods.EventStopped:
                Volatile.Write(ref _recording, 0);
                Interlocked.Increment(ref _stops);
                Stopped?.Invoke();
                break;

            case MacroMethods.EventSaved:
                if (message.Read<MacroSavedEvent>() is { } saved) Saved?.Invoke(saved.MacroId);
                break;

            case MacroMethods.EventPlayed:
                Played?.Invoke(message.Read<MacroPlayEvent>()?.Error);
                break;
        }
    }

    /// <summary>发一条「执行器改数据」的请求；成功返回答复（没有负载的用它判 Ok），失败返回 null。</summary>
    private static LinkMessage? Send(string method, object payload, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上";
            return null;
        }

        try
        {
            LinkMessage reply = link.RequestAsync(method, payload, timeoutMs: WriteTimeoutMs)
                .GetAwaiter().GetResult();
            if (!reply.Ok)
            {
                error = reply.Error ?? "主程序拒绝了这次改动";
                return null;
            }
            return reply;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
