using System;
using System.Threading.Tasks;
using LTools.Features;

namespace LTools.Core.Link;

/// <summary>
/// 动作级写入的**执行器侧**：内容属主，收到命令就「改内存 + 落盘 + 通知」。
/// </summary>
/// <remarks>
/// <para>
/// 全部在**UI 线程**上做（<see cref="Ui.Run"/>）：删除会连带改面板磁贴 / 快捷键 / 触发规则 / 悬浮项，
/// 其中悬浮窗的刷新本来就只能在 UI 线程。落盘走 <c>action.saveAll</c> 交给宿主写目录。
/// </para>
/// <para>
/// 宿主那边在等答复，而落盘又要反过来请求宿主（<c>action.saveAll</c>）：两侧读循环都是异步的，
/// 能处理这种嵌套往返；但命令超时（5 秒）必须当失败返回，别让宿主以为保存成功。
/// </para>
/// <para>
/// 删除之后要推一次 <c>actions.changed</c>：引用清理只在执行器这份内存里做（面板槽位 / 快捷键 /
/// 触发 / 悬浮），宿主那份还是旧的，必须整份重读。新建 / 覆盖 / 复制不动引用，宿主把答复里的
/// 动作镜像进自己的清单就够了——**别在每次写入后都让宿主整份重载**（会和宿主紧接着的面板推送打架）。
/// </para>
/// </remarks>
internal static class ActionEditHost
{
    /// <summary>这四条方法名归这里处理（同步入口与异步链路共用一张名单）。</summary>
    public static bool IsWrite(string method) => method is LinkMethods.ActionAdd or LinkMethods.ActionUpdate
        or LinkMethods.ActionDelete or LinkMethods.ActionDuplicate;

    /// <summary>处理一条动作级写入；不是这四种方法时返回 null（调用方回「未知命令」）。</summary>
    /// <remarks>
    /// **不能在这里同步等 UI 线程**：读循环正等着这个答复，而写入会再向对端要一次落盘
    /// （<c>action.saveAll</c>）——同步等就会把读循环卡住，那个答复永远收不到（实测 5 秒超时）。
    /// 所以只把活排到 UI 线程，返回一个等它做完的 Task。
    /// </remarks>
    public static Task<LinkMessage>? TryHandleAsync(LinkChannel channel, LinkMessage request)
    {
        if (!IsWrite(request.Method)) return null;

        var completion = new TaskCompletionSource<LinkMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Enqueue(() =>
        {
            try { completion.TrySetResult(Handle(channel, request)); }
            catch (Exception ex)
            {
                AppLog.Warn("Action", $"动作写入失败（{request.Method}）：{ex.Message}");
                completion.TrySetResult(LinkMessage.Response(request, new ActionEditReply(false, null, ex.Message)));
            }
        });
        return completion.Task;
    }

    /// <summary>同步执行一条动作级写入（自测 / 冒烟直接调它，不必起管道）。</summary>
    public static LinkMessage Handle(LinkMessage request) => Handle(null, request);

    private static LinkMessage Handle(LinkChannel? channel, LinkMessage request)
    {
        ActionEditRequest? req = request.Read<ActionEditRequest>();
        if (req == null) return LinkMessage.Response(request, error: "缺少参数");

        try
        {
            LinkMessage reply = LinkMessage.Response(request, error: "未知的动作命令：" + request.Method);
            Ui.Run(() => reply = Apply(channel, request, req));
            return reply;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Action", $"动作写入失败（{request.Method}）：{ex.Message}");
            return LinkMessage.Response(request, new ActionEditReply(false, null, ex.Message));
        }
    }

    private static LinkMessage Apply(LinkChannel? channel, LinkMessage request, ActionEditRequest req)
    {
        ActionEditReply reply = request.Method switch
        {
            LinkMethods.ActionAdd => Add(req.Dto),
            LinkMethods.ActionUpdate => Update(req.Dto),
            LinkMethods.ActionDelete => Delete(req.Id),
            _ => Duplicate(req.Id),
        };

        if (reply.Ok && request.Method == LinkMethods.ActionDelete && channel != null)
            _ = channel.NotifyAsync(LinkMethods.EventActionsChanged);

        return LinkMessage.Response(request, reply);
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
