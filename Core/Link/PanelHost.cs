using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Features;
using LTools.Features.Panel;

namespace LTools.Core.Link;

/// <summary>
/// 面板命令在**提权侧**的处理（宿主的写操作经这里落盘）。
/// </summary>
/// <remarks>
/// <para>
/// 拆分过渡期（R1 / P1b）的写属主仍是提权进程：宿主只读地显示面板，改了之后把整份
/// <see cref="PanelConfig"/> 交回来，由这里调 <see cref="PanelLibrary"/> / <see cref="ConfigStore"/> 写盘。
/// 这样既不用给面板写路径逐个建命令，也不会出现两个进程各写一半。
/// </para>
/// <para>
/// 全部动作都在实现里做「参数校验 → 改数据 → 落盘」，**任何异常都翻成失败答复**，不把异常抛回读循环。
/// </para>
/// </remarks>
internal static class PanelHost
{
    /// <summary>给 <see cref="LinkServer"/> 用的处理函数（同步实现 + 包一层 Task，避免占用读循环）。</summary>
    public static Task<LinkMessage> HandleAsync(LinkChannel channel, LinkMessage request) =>
        Task.FromResult(Handle(request));

    /// <summary>直接处理一条面板命令（自测 / 冒烟直接调它，不必起管道）。</summary>
    internal static LinkMessage Handle(LinkMessage request)
    {
        try
        {
            return request.Method switch
            {
                LinkMethods.PanelSnapshot => LinkMessage.Response(request, Snapshot()),
                LinkMethods.PanelReplace => Replace(request),
                LinkMethods.ActionAdd => Add(request),
                LinkMethods.ActionDelete => Delete(request),
                LinkMethods.ActionDuplicate => Duplicate(request),
                LinkMethods.RunAction => ForwardRun(request),
                _ => LinkMessage.Response(request, error: "未知命令：" + request.Method),
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"面板命令失败（{request.Method}）：{ex.Message}");
            return LinkMessage.Response(request, error: ex.Message);
        }
    }

    // ============ 读 ============

    private static PanelSnapshot Snapshot() => new(
        PanelLibrary.Current,
        ActionRegistry.All
            .Select(a => new ActionSummary(a.Id, a.Name, a.Icon ?? "", a.Program?.Steps.Count ?? 0))
            .ToList());

    // ============ 写 ============

    private static LinkMessage Replace(LinkMessage request)
    {
        PanelConfig? panel = request.Read<PanelConfig>();
        if (panel == null) return LinkMessage.Response(request, error: "缺少面板配置");

        return PanelLibrary.Replace(panel)
            ? LinkMessage.Response(request)
            : LinkMessage.Response(request, error: "面板配置写盘失败");
    }

    private static LinkMessage Add(LinkMessage request)
    {
        ActionAddRequest? add = request.Read<ActionAddRequest>();
        if (add == null || string.IsNullOrWhiteSpace(add.Name))
            return LinkMessage.Response(request, error: "缺少动作名称");

        var action = new ActionDto { Name = add.Name.Trim(), Icon = add.Icon };
        if (!ConfigStore.AddAction(action))
            return LinkMessage.Response(request, error: "新建动作失败");

        ConfigStore.Save();
        return LinkMessage.Response(request, action);
    }

    private static LinkMessage Delete(LinkMessage request)
    {
        ActionIdRequest? target = request.Read<ActionIdRequest>();
        if (target == null || string.IsNullOrEmpty(target.Id))
            return LinkMessage.Response(request, error: "缺少动作 Id");

        // RemoveAction 内部已经落盘（含快捷键 / 面板 / 触发 / 悬浮引用与资源回收），别再补一次 Save。
        return ConfigStore.RemoveAction(target.Id)
            ? LinkMessage.Response(request)
            : LinkMessage.Response(request, error: "没有这个动作");
    }

    private static LinkMessage Duplicate(LinkMessage request)
    {
        ActionIdRequest? target = request.Read<ActionIdRequest>();
        if (target == null || string.IsNullOrEmpty(target.Id))
            return LinkMessage.Response(request, error: "缺少动作 Id");

        ActionDto? copy = ConfigStore.DuplicateAction(target.Id);
        if (copy == null) return LinkMessage.Response(request, error: "复制动作失败");

        ConfigStore.Save();
        return LinkMessage.Response(request, copy);
    }

    /// <summary>宿主点了磁贴：真正执行留在这里（提权进程），宿主不跑动作。</summary>
    private static LinkMessage ForwardRun(LinkMessage request)
    {
        RunActionRequest? run = request.Read<RunActionRequest>();
        if (run == null || string.IsNullOrWhiteSpace(run.Reference))
            return LinkMessage.Response(request, error: "缺少动作引用");

        RunSource source = Enum.TryParse(run.Source, ignoreCase: true, out RunSource parsed)
            ? parsed
            : RunSource.Panel;

        // 与面板本地点击完全同一条路：ActionExecutor.RunRef 会 spawn 后台线程 / 走 AutomationRunner。
        Ui.Post(() => ActionExecutor.RunRef(run.Reference, run.X, run.Y, source));
        return LinkMessage.Response(request);
    }
}
