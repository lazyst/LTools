using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features.Builtin;

namespace LTools.Features;

/// <summary>
/// 动作执行的唯一入口：所有动作的 <see cref="ActionDto.Program"/> 交给 <see cref="ActionEngine"/>。
/// </summary>
/// <remarks>
/// <para>
/// 线程模型：<see cref="Run"/> 一律 spawn 到后台线程（钩子回调不得阻塞 &gt;300ms）。
/// 组合动作走 <see cref="AutomationRunner"/>：它有单实例保护、
/// 停止热键和运行指示器；单步动作直接执行，两个单步动作可以并行。
/// 失败由本层统一经 <see cref="CrashLog.Write"/> 记录（含动作名）。
/// </para>
/// </remarks>
internal static class ActionExecutor
{
    /// <summary>执行动作（后台线程，不阻塞调用方）。光标坐标默认取当前位置。</summary>
    public static void Run(ActionDto action) => Run(action, 0, 0);

    /// <summary>
    /// 按「绑定引用」运行：内置功能（<c>common.*</c> / <c>ltools.*</c>）交给
    /// <see cref="FeatureRunner"/>，动作（<c>a1</c>）走原有路径。返回是否找到并投递。
    /// </summary>
    /// <remarks>
    /// 这是**用户手动触发**的入口（面板磁贴 / 快捷键 / 悬浮按钮）：动作声明了「输入」变量时，
    /// 先在 UI 线程上问一次参数（见 <see cref="RunInteractive"/>）。自动触发走四参重载，不弹窗。
    /// </remarks>
    public static bool RunRef(string reference, int cursorX, int cursorY,
        RunSource source = RunSource.Manual)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (FeatureRunner.TryRun(reference, cursorX, cursorY)) return true;

        var action = ActionRegistry.FindById(reference);
        if (action == null) return false;
        RunInteractive(action, cursorX, cursorY, source);
        return true;
    }

    /// <summary>
    /// 用户手动触发的运行：有「输入」变量就先问一次参数，没有就立刻投递（与以前完全一致）。
    /// </summary>
    /// <remarks>
    /// 问参数**只能在 UI 线程**（WPF 对话框），而这里可能是钩子线程（快捷键），
    /// 所以一律 <see cref="Ui.Post"/>：钩子回调不能等对话框。问参数期间临时挂起快捷键，
    /// 否则单键绑定会把用户输进框里的字母再当成一次触发。
    /// </remarks>
    private static void RunInteractive(ActionDto action, int cursorX, int cursorY, RunSource source)
    {
        // 已有自动化在跑：不弹参数框（问完也跑不了），直接交给 Run 去报「已有自动化正在运行」。
        if (!ActionParams.NeedsAsk(action) || AutomationRunner.IsRunning)
        {
            Run(action, cursorX, cursorY, null, source);
            return;
        }

        Ui.Post(() =>
        {
            bool suspended = HotkeyRegistry.Suspended;
            HotkeyRegistry.Suspended = true;
            IReadOnlyDictionary<string, Value>? values;
            try
            {
                values = ActionParams.Ask(null, action.Id, action.Name + " · 参数",
                    ActionParams.InputsOf(action.Program),
                    "填好点「运行」；这些值会记住，下次运行还是它们。");
            }
            finally
            {
                HotkeyRegistry.Suspended = suspended;
            }

            if (values == null) return;   // 取消 = 这次不跑
            Run(action, cursorX, cursorY, values, source);
        });
    }

    /// <summary>
    /// 按引用运行，并带上初始变量（自动触发的事件输出 / <c>ActionParam</c>）。
    /// 内置功能引用（<c>common.*</c> / <c>ltools.*</c>）走 <see cref="FeatureRunner"/>，
    /// 不接变量：<paramref name="initialVariables"/> 只对动作有效。见 <c>docs/AUTO_TRIGGER.md</c> §13.3。
    /// </summary>
    public static bool RunRef(string reference, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (FeatureRunner.TryRun(reference, cursorX, cursorY)) return true;

        var action = ActionRegistry.FindById(reference);
        if (action == null) return false;
        Run(action, cursorX, cursorY, initialVariables, RunSource.Trigger);
        return true;
    }

    /// <summary>
    /// 执行动作（spawn 后台线程，不阻塞调用方）。cursorX/cursorY 为面板唤起时记录的光标坐标
    /// （0=取当前光标位置，供 <c>windowPin.toggle</c> 等需坐标的内部命令使用）。
    /// </summary>
    /// <remarks>
    /// 动作只有一种形状（<see cref="ActionDto.Program"/>），所以这里不做类型分支：
    /// 一律交给 <see cref="AutomationRunner"/>（单实例保护、停止热键、运行指示器、结果提示）。
    /// </remarks>
    public static void Run(ActionDto action, int cursorX, int cursorY) =>
        Run(action, cursorX, cursorY, null);

    /// <summary>带初始变量的运行入口（自动触发专用；其余入口走上面那个三参重载）。</summary>
    public static void Run(ActionDto action, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables,
        RunSource source = RunSource.Manual)
    {
        // 从 CapsLock 菜单启动时 CapsLock 仍可能处于按下态；运行期间钩子会屏蔽 CapsLock，
        // 若不在启动前清掉，状态机可能一直认为它按着（见 AutomationRunner.PrepareToRun）。
        AutomationRunner.PrepareToRun();
        Task.Run(() => AutomationRunner.Run(action, cursorX, cursorY, initialVariables, source));
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
