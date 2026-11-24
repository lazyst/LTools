using System;
using System.Threading.Tasks;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作级写入的**执行器侧**：内容属主，收到命令就「改内存 + 落盘 + 通知」。
/// </summary>
/// <remarks>
/// <para>
/// 全部在**链路线程**上做：<see cref="ActionRegistry"/> 是并发字典，落盘走 <c>action.saveAll</c>
/// 交给宿主写目录，<see cref="ConfigStore.NotifyChanged"/> 的契约本来就是「可能在后台线程，
/// 订阅端自己投 UI」（见它的注释）——所以这里不需要先排到 UI 线程。
/// </para>
/// <para>
/// 宿主那边在等答复，而落盘又要反过来请求宿主（<c>action.saveAll</c>）：两侧读循环都是异步的，
/// 能处理这种嵌套往返；但命令超时（5 秒）必须当失败返回，别让宿主以为保存成功。
/// </para>
/// </remarks>
internal static class ActionEditHost
{
    /// <summary>这四条方法名归这里处理（<see cref="Handle"/> 与异步入口共用一张名单）。</summary>
    public static bool IsWrite(string method) => method is LinkMethods.ActionAdd or LinkMethods.ActionUpdate
        or LinkMethods.ActionDelete or LinkMethods.ActionDuplicate;

    /// <summary>处理一条动作级写入；不是这四种方法时返回 null（调用方回「未知命令」）。</summary>
    public static Task<LinkMessage>? TryHandleAsync(LinkMessage request) =>
        IsWrite(request.Method) ? Task.FromResult(Handle(request)) : null;

    /// <summary>同步执行一条动作级写入（自测 / 冒烟直接调它，不必起管道）。</summary>
    public static LinkMessage Handle(LinkMessage request)
    {
        ActionEditRequest? req = request.Read<ActionEditRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        try
        {
            ActionEditReply reply = request.Method switch
            {
                LinkMethods.ActionAdd => Add(req.Dto),
                LinkMethods.ActionUpdate => Update(req.Dto),
                LinkMethods.ActionDelete => Delete(req.Id),
                _ => Duplicate(req.Id),
            };
            return LinkMessage.Response(request, reply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Action", $"动作写入失败（{request.Method}）：{ex.Message}");
            return LinkMessage.Response(request, new ActionEditReply(false, null, ex.Message));
        }
    }

    private static ActionEditReply Add(ActionDto? dto)
    {
        if (dto == null) return new ActionEditReply(false, null, "缺少动作内容");
        if (!ConfigStore.AddAction(dto)) return new ActionEditReply(false, null, "动作名不能为空");
        ConfigStore.Save();
        return new ActionEditReply(true, dto, null);
    }

    private static ActionEditReply Update(ActionDto? dto)
    {
        if (dto == null) return new ActionEditReply(false, null, "缺少动作内容");
        if (string.IsNullOrEmpty(dto.Id)) return new ActionEditReply(false, null, "缺少动作 Id");

        // 别让「改一个不存在的动作」静默变成新建：先确认真有它。
        if (ActionRegistry.FindById(dto.Id) == null)
            return new ActionEditReply(false, null, "这个动作已经不存在了（可能刚被删掉）");

        ConfigStore.UpdateAction(dto);
        ConfigStore.Save();
        return new ActionEditReply(true, dto, null);
    }

    private static ActionEditReply Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return new ActionEditReply(false, null, "缺少动作 Id");
        bool removed = ConfigStore.RemoveAction(id);
        return new ActionEditReply(true, null, removed ? null : "动作本来就不在了");
    }

    private static ActionEditReply Duplicate(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return new ActionEditReply(false, null, "缺少动作 Id");

        ActionDto? copy = ConfigStore.DuplicateAction(id);
        if (copy == null) return new ActionEditReply(false, null, "原动作已经不存在了");

        ConfigStore.Save();
        return new ActionEditReply(true, copy, null);
    }
}
