using System;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作级写入的**宿主侧**：把「加 / 改 / 删 / 复制」交给执行器（内容属主）做。
/// </summary>
/// <remarks>
/// 只有宿主里的面板 / 编辑器会调；执行器自己走 <see cref="ConfigStore"/> 的本地分支。
/// 失败一律给可显示的原因（超时 / 宿主没连上 / 执行器拒绝），调用方据此提示，别假装保存成功。
/// </remarks>
internal static class ActionEditBridge
{
    /// <summary>落盘要反过来请求本进程写目录，给宽一点；超时就算失败。</summary>
    private const int TimeoutMs = 5_000;

    private static LinkServer? _link;

    /// <summary>宿主启动链路服务端后挂上（与 <c>PanelBridge</c> 同一处）。</summary>
    public static void Attach(LinkServer server) => _link = server;

    public static bool Add(ActionDto dto, out ActionDto? saved, out string? error) =>
        Send(LinkMethods.ActionAdd, new ActionEditRequest(dto), out saved, out error);

    public static bool Update(ActionDto dto, out string? error) =>
        Send(LinkMethods.ActionUpdate, new ActionEditRequest(dto), out _, out error);

    public static bool Delete(string id, out string? error) =>
        Send(LinkMethods.ActionDelete, new ActionEditRequest(Id: id), out _, out error);

    public static ActionDto? Duplicate(string id, out string? error) =>
        Send(LinkMethods.ActionDuplicate, new ActionEditRequest(Id: id), out ActionDto? saved, out error)
            ? saved
            : null;

    private static bool Send(string method, ActionEditRequest payload,
        out ActionDto? saved, out string? error)
    {
        saved = null;
        error = null;

        LinkServer? link = _link;
        if (link == null || !link.IsConnected)
        {
            error = "主程序没连上，动作没能保存";
            return false;
        }

        try
        {
            LinkMessage reply = link.RequestAsync(method, payload, timeoutMs: TimeoutMs)
                .GetAwaiter().GetResult();

            if (!reply.Ok)
            {
                error = reply.Error ?? "主程序拒绝了这次改动";
                return false;
            }

            ActionEditReply? result = reply.Read<ActionEditReply>();
            if (result is not { Ok: true })
            {
                error = result?.Error ?? "主程序没能保存这次改动";
                return false;
            }

            saved = result.Action;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
