using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;
using LTools.Core.Running;
using LTools.Features.Builtin;
using LTools.Features.Running;

namespace LTools.Features;

/// <summary>
/// 动作执行的唯一入口：所有动作的 <see cref="ActionDto.Program"/> 交给引擎。
/// </summary>
/// <remarks>
/// <para>
/// 线程模型：提交只做登记并**给这次运行开一条专用线程**（<see cref="RunCoordinator"/>），
/// 不阻塞调用方（钩子回调不得阻塞 &gt;300ms）。能不能开 / 排队 / 拒绝只在准入一处判。
/// </para>
/// <para>
/// 内置功能（<c>common.*</c> / <c>ltools.*</c>）交给 <see cref="FeatureRunner"/>，从不排队；
/// 动作走运行注册表（同一动作也能并发多份）。
/// </para>
/// </remarks>
internal static class ActionExecutor
{
    /// <summary>执行动作（后台线程，不阻塞调用方）。光标坐标默认取当前位置。</summary>
    public static void Run(ActionDto action) => Run(action, 0, 0);

    /// <summary>
    /// 按「绑定引用」运行：内置功能交给 <see cref="FeatureRunner"/>，动作走运行注册表。
    /// 返回这次提交的结局（钩子 / 面板 / 悬浮据此给提示）。
    /// </summary>
    public static RunSubmission RunRef(string reference, int cursorX, int cursorY,
        RunSource source = RunSource.Manual)
    {
        if (string.IsNullOrWhiteSpace(reference)) return RunSubmission.NotFound;

        // 普通权限宿主里不执行：面板搬过去之后，磁贴点击一律转给提权进程（见 PanelHostBridge）。
        if (PanelHostBridge.TryForwardRun(reference, cursorX, cursorY, source)) return RunSubmission.Started;

        if (FeatureRunner.TryRun(reference, cursorX, cursorY)) return RunSubmission.Feature;

        var action = ActionRegistry.FindById(reference);
        if (action == null) return RunSubmission.NotFound;

        return Run(action, cursorX, cursorY, null, source);
    }

    /// <summary>
    /// 按引用运行，并带上初始变量（自动触发的事件输出 / <c>ActionParam</c>）。
    /// 内置功能引用（<c>common.*</c> / <c>ltools.*</c>）走 <see cref="FeatureRunner"/>，
    /// 不接变量：<paramref name="initialVariables"/> 只对动作有效。见 <c>docs/AUTO_TRIGGER.md</c> §13.3。
    /// </summary>
    public static RunSubmission RunRef(string reference, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables)
    {
        if (string.IsNullOrWhiteSpace(reference)) return RunSubmission.NotFound;
        if (FeatureRunner.TryRun(reference, cursorX, cursorY)) return RunSubmission.Feature;

        var action = ActionRegistry.FindById(reference);
        if (action == null) return RunSubmission.NotFound;

        return Run(action, cursorX, cursorY, initialVariables, RunSource.Trigger);
    }

    public static void Run(ActionDto action, int cursorX, int cursorY) =>
        Run(action, cursorX, cursorY, null);

    /// <summary>
    /// 提交一次运行：清 CapsLock 残留状态（只在没有别的运行中时）→ 准入 → 开专用线程 / 排队 / 拒绝。
    /// </summary>
    public static RunSubmission Run(ActionDto action, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables,
        RunSource source = RunSource.Manual)
    {
        // 从 CapsLock 菜单启动时 CapsLock 仍可能处于按下态；运行期间钩子会屏蔽 CapsLock，
        // 若不在启动前清掉，状态机可能一直认为它按着（见 AutomationRunner.PrepareToRun）。
        AutomationRunner.PrepareToRun();
        return RunCoordinator.SubmitAction(action, cursorX, cursorY, initialVariables, source);
    }

    /// <summary>
    /// 同步执行一个动作（已位于后台线程）。失败一律上抛
    /// （由 <see cref="Run"/> 或 <see cref="AutomationRunner"/> 记录）。
    /// <paramref name="ctx"/> 只用来取取消令牌与光标坐标。
    /// </summary>
    internal static VarValue? Execute(ActionDto action, int cursorX, int cursorY, RunContext? ctx = null)
    {
        RunProgram(action, cursorX, cursorY, ctx);
        return null;   // 结果变量由步骤的 out 映射写回，不再经返回值传递
    }

    /// <summary>用引擎执行动作体；停止与失败按运行期的异常语义上抛。</summary>
    private static void RunProgram(ActionDto action, int cursorX, int cursorY, RunContext? ctx)
    {
        ActionProgram program = action.Program
            ?? throw new InvalidOperationException($"动作「{action.Name}」没有动作体");

        var options = new ActionRunOptions
        {
            Token = ctx?.Token ?? CancellationToken.None,
            CursorX = cursorX,
            CursorY = cursorY,
        };

        ActionRunResult result = new ActionEngine(StepRegistry.Default).Run(program, options);

        if (result.Stopped)
            throw new FlowAbortException("已停止", userCancelled: true);

        if (!result.Completed)
            throw new InvalidOperationException(
                result.Error ?? $"动作「{action.Name}」执行失败");
    }
}
