using System;
using LTools.Core;

namespace LTools.Core.Link;

/// <summary>
/// 应用级操作的**宿主侧**：开机自启、清空运行 / 触发记录。
/// </summary>
/// <remarks>
/// 设置页在宿主里，但这几件事只有执行器做得了：自启要建 <c>RunLevel=HighestAvailable</c> 的任务计划
/// （普通权限会被拒）且注册的是执行器的 <c>LTools.exe</c>；两份记录（<c>state/*.jsonl</c>）
/// 由执行器产生、进程内还有副本，宿主自己删文件下一轮追加又会写回来。
/// </remarks>
internal static class AppBridge
{
    /// <summary>自启操作要走任务计划 COM，清记录要重写整份文件——给足时间，但别在 UI 线程上无限等。</summary>
    private const int TimeoutMs = 10_000;

    private static LinkServer? _link;

    /// <summary>宿主启动链路服务端后挂上（与 <see cref="PanelHostBridge.Attach"/> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    /// <summary>断开（宿主退出 / 冒烟收尾）。</summary>
    public static void Detach() => _link = null;

    /// <summary>查询自启状态；没连上 / 超时 / 被拒都返回 null 并给出可显示的原因。</summary>
    public static AutoStartInfo? Query(out string? error)
    {
        AppAutoStartReply? reply = Call(AppMethods.AutoStart, new AppAutoStartRequest(AutoStartActions.Query),
            "主程序没连上，开机自启只能在主程序里改", out error);
        return reply?.Info;
    }

    /// <summary>启用 / 禁用；成功返回做完之后的任务现状。</summary>
    public static AutoStartInfo? SetEnabled(bool enable, out string? error)
    {
        AppAutoStartReply? reply = Call(AppMethods.AutoStart,
            new AppAutoStartRequest(enable ? AutoStartActions.Enable : AutoStartActions.Disable),
            "主程序没连上，开机自启只能在主程序里改", out error);
        return reply?.Info;
    }

    /// <summary>清空一份记录（<see cref="HistoryRecords"/>）：没连上 / 被拒都返回 false 并给出原因。</summary>
    public static bool ClearHistory(string kind, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上，历史记录只能在主程序里清空";
            return false;
        }

        try
        {
            LinkMessage reply = link
                .RequestAsync(AppMethods.ClearHistory, new AppClearHistoryRequest(kind), timeoutMs: TimeoutMs)
                .GetAwaiter().GetResult();

            AppAck? ack = reply.Read<AppAck>();
            if (!reply.Ok || ack == null)
            {
                error = reply.Error ?? "主程序没有答复";
                return false;
            }
            if (!ack.Ok)
            {
                error = ack.Error ?? "清空失败";
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

    private static AppAutoStartReply? Call(string method, object payload, string offlineReason, out string? error)
    {
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = offlineReason;
            return null;
        }

        try
        {
            LinkMessage reply = link.RequestAsync(method, payload, timeoutMs: TimeoutMs)
                .GetAwaiter().GetResult();

            AppAutoStartReply? result = reply.Read<AppAutoStartReply>();
            if (!reply.Ok || result == null)
            {
                error = reply.Error ?? "主程序没有答复";
                return null;
            }
            if (!result.Ok)
            {
                error = result.Error ?? "自启操作失败";
                return null;
            }
            return result;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
