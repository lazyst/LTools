using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Features;
using LTools.Features.Floating;
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
    public static Task<LinkMessage> HandleAsync(LinkChannel channel, LinkMessage request)
    {
        // 动作级操作会开模态编辑器 / 确认框：必须排到 UI 线程，而且完成后要通知宿主重拉快照。
        if (string.Equals(request.Method, LinkMethods.ActionOp, StringComparison.Ordinal))
            return Task.FromResult(HandleActionOp(channel, request));

        // 试运行（编辑器在宿主里）：在提权进程里真跑一次，轨迹按事件推回去。
        if (RunHost.TryHandleAsync(request) is { } run) return run;

        // 宏录制（编辑器在宿主里，录制要钩子）：结束 / 存好按 token 推回去。
        if (MacroHost.TryHandleAsync(channel, request) is { } macro) return macro;

        // 动作级写入（宿主里的面板 / 编辑器）：内容属主还是这里，改完照旧由宿主落盘。
        if (ActionEditHost.TryHandleAsync(channel, request) is { } edit) return edit;

        return Task.FromResult(Handle(request));
    }

    private static LinkMessage HandleActionOp(LinkChannel channel, LinkMessage request)
    {
        ActionOpRequest? op = request.Read<ActionOpRequest>();
        if (op == null || string.IsNullOrWhiteSpace(op.Op))
            return LinkMessage.Response(request, error: "缺少操作名");

        Ui.Post(() =>
        {
            try
            {
                ApplyActionOp(op);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Link", $"面板操作失败（{op.Op}）：{ex.Message}");
            }
            finally
            {
                // 动作可能刚建 / 刚删 / 刚改：让宿主重读一次（面板与动作清单都跟着刷新）。
                _ = channel.NotifyAsync(LinkMethods.EventActionsChanged);
            }
        });

        return LinkMessage.Response(request);
    }

    private static void ApplyActionOp(ActionOpRequest op)
    {
        AppLog.Info("Link", $"面板操作：{op.Op}（id={op.Id} 分组={op.GroupId}）");

        switch (op.Op)
        {
            case PanelOps.Float:
                if (!string.IsNullOrEmpty(op.Id)) FloatingManager.ToggleAction(op.Id);
                break;

            case PanelOps.FloatGroup:
                if (PanelLibrary.FindGroup(op.GroupId) is { } floatGroup) FloatingManager.FloatGroup(floatGroup);
                break;

            case PanelOps.ReplayMacro:
                if (!string.IsNullOrEmpty(op.Id)) ReplayMacro(op.Id);
                break;

            case PanelOps.RecordMacro:
                if (!string.IsNullOrEmpty(op.Id)) MacroRecorder.StartInto(op.Id, op.Name);
                else MacroRecorder.Start();
                break;

            case PanelOps.EditInStash:
                if (!string.IsNullOrEmpty(op.Id)) EditInStash(op.Id);
                break;

            case PanelOps.StashOpen:
                if (!string.IsNullOrEmpty(op.Id)) StashCall(StashOps.Open, op.Id);
                break;

            case PanelOps.StashRun:
                if (!string.IsNullOrEmpty(op.Id)) StashCall(StashOps.Run, op.Id);
                break;

            case PanelOps.StashPromote:
                if (!string.IsNullOrEmpty(op.Id))
                    StashCall(id => StashOps.Promote(id, op.Kind ?? "new"), op.Id);
                break;

            case PanelOps.StashDelete:
                if (!string.IsNullOrEmpty(op.Id)) StashCall(StashOps.Delete, op.Id);
                break;

            case PanelOps.StashClear:
                StashCall(_ => StashOps.ClearAll(), "");
                break;

            default:
                AppLog.Warn("Link", "未知的面板操作：" + op.Op);
                break;
        }
    }

    /// <summary>试放一个宏：与面板里那一项同一条路（面板先收起、后台回放、失败只提示）。</summary>
    private static void ReplayMacro(string macroId)
    {
        MacroDto? macro = MacroStore.Load(macroId);
        if (macro == null)
        {
            ToastService.Show("宏文件已不存在（通常随引用它的动作一起删除），请重新录制", "键鼠宏",
                ToastService.LongMs, ToastKind.error, key: "macro:play:hint");
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                MacroPlayer.PlaySync(macro, MacroStore.PlaybackOf(macroId));
            }
            catch (Exception ex)
            {
                AppLog.Error("Macro", $"试放失败「{macro.Name}」", ex);
                ToastService.Show("试放失败：" + ex.Message, "键鼠宏", ToastService.LongMs,
                    ToastKind.error, key: "macro:play:hint");
            }
        });
    }

    /// <summary>暂存区的一个写操作：面板在宿主里（宿主已经收起），提权侧直接执行。</summary>
    private static void StashCall(Action<string> action, string id) => action(id);

    /// <summary>把动作复制进暂存区并让宿主切到暂存区页（草稿要写 <c>staging/</c>，只能在提权进程做）。</summary>
    private static void EditInStash(string actionId)
    {
        ActionDto? action = ActionRegistry.FindById(actionId);
        if (action == null) return;

        try
        {
            DraftStore.Create(action.Name, action.Program?.Description, actionId, "user");
            PanelLauncher.ShowStash();
            ToastService.Show("已复制到暂存区；改完再决定是否覆盖原动作", action.Name, 3000, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, "复制到暂存区失败", 4000, ToastKind.error);
        }
    }

    /// <summary>直接处理一条面板命令（自测 / 冒烟直接调它，不必起管道）。</summary>
    internal static LinkMessage Handle(LinkMessage request)
    {
        try
        {
            return request.Method switch
            {
                LinkMethods.PanelSnapshot => LinkMessage.Response(request, Snapshot()),
                LinkMethods.PanelReplace => Replace(request),
                LinkMethods.RunAction => ForwardRun(request),
                LinkMethods.OpenWindow => OpenWindow(request),
                _ when ActionEditHost.IsWrite(request.Method) => ActionEditHost.Handle(request),
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
            .ToList(),
        FloatingLibrary.Items
            .SelectMany(item => item.Members)
            .Select(member => member.ActionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
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

    /// <summary>宿主点「设置 / 暂存区 / 面板场景」：这些窗口都还在提权进程里开。</summary>
    private static LinkMessage OpenWindow(LinkMessage request)
    {
        OpenWindowRequest? open = request.Read<OpenWindowRequest>();
        if (open == null || string.IsNullOrWhiteSpace(open.Target))
            return LinkMessage.Response(request, error: "缺少打开目标");

        string target = open.Target;
        string? arg = open.Arg;

        Ui.Post(() =>
        {
            switch (target)
            {
                case LinkTargets.Settings:
                    ConfigHelper.Open();
                    break;

                case LinkTargets.PanelScene:
                    ConfigHelper.OpenPanelScene(arg ?? "");
                    break;

                case LinkTargets.Ai:
                    ConfigHelper.OpenAi();
                    break;

                case LinkTargets.Macros:
                    ConfigHelper.OpenMacros();
                    break;

                default:
                    AppLog.Warn("Link", "未知的打开目标：" + target);
                    break;
            }
        });

        return LinkMessage.Response(request);
    }
}
