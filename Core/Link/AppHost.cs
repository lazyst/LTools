using System.Threading.Tasks;
using LTools.Core;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 应用级操作的**执行器侧**：开机自启、清空运行 / 触发记录。
/// </summary>
/// <remarks>
/// 两件事都只有这里做得了：自启的任务计划要提权、注册的也是本进程的 exe；两份记录
/// （<c>state/*.jsonl</c>）只有这个进程在写，它进程内还有副本（自己删文件下一轮追加会写回来）。
/// </remarks>
internal static class AppHost
{
    /// <summary>这条方法名归这里处理（同步入口与异步链路共用）。</summary>
    public static bool IsCommand(string method) =>
        method == AppMethods.AutoStart || method == AppMethods.ClearHistory;

    /// <summary>处理一条应用级命令；不是时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) =>
        IsCommand(request.Method) ? Task.FromResult(Handle(request)) : null;

    /// <summary>同步执行（自测 / 冒烟直接调它，不必起管道）。</summary>
    public static LinkMessage Handle(LinkMessage request)
    {
        if (request.Method == AppMethods.ClearHistory)
        {
            AppClearHistoryRequest? clear = request.Read<AppClearHistoryRequest>();
            switch (clear?.Kind)
            {
                case HistoryRecords.Run:
                    RunHistory.Clear();
                    return LinkMessage.Response(request, new AppAck(true, null));

                case HistoryRecords.Trigger:
                    TriggerHistory.Clear();
                    return LinkMessage.Response(request, new AppAck(true, null));

                default:
                    return LinkMessage.Response(request, new AppAck(false, "未知的记录：" + clear?.Kind));
            }
        }

        AppAutoStartRequest? req = request.Read<AppAutoStartRequest>();
        string action = (req?.Action ?? "").Trim().ToLowerInvariant();

        switch (action)
        {
            case AutoStartActions.Query:
                break;

            case AutoStartActions.Enable:
                if (!AutoStartService.Enable())
                    return LinkMessage.Response(request, new AppAutoStartReply(false, default,
                        "启用失败：检查权限或任务计划服务"));
                break;

            case AutoStartActions.Disable:
                if (!AutoStartService.Disable())
                    return LinkMessage.Response(request, new AppAutoStartReply(false, default,
                        "禁用失败：检查权限或任务计划服务"));
                break;

            default:
                return LinkMessage.Response(request, new AppAutoStartReply(false, default,
                    "未知的自启操作：" + action));
        }

        return LinkMessage.Response(request, new AppAutoStartReply(true, AutoStartService.Query(), null));
    }
}
