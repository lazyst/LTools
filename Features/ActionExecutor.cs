using System;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features.Migration;

namespace LTools.Features;

/// <summary>
/// 动作执行的唯一入口：所有动作都变成 <see cref="ActionProgram"/> 交给 <see cref="ActionEngine"/>。
/// </summary>
/// <remarks>
/// <para>
/// 旧格式（磁盘上还没迁移的动作）由 <see cref="ActionPrograms.For"/> 第一次运行时懒迁移；
/// 迁移完就缓存在动作对象上，之后只有一条执行路径。
/// </para>
/// <para>
/// 线程模型：<see cref="Run"/> 一律 spawn 到后台线程（钩子回调不得阻塞 &gt;300ms）。
/// 组合动作（含新编辑器保存的一切动作）走 <see cref="AutomationRunner"/>：它有单实例保护、
/// 停止热键和运行指示器；单步动作直接执行，两个单步动作可以并行。
/// 失败由本层统一经 <see cref="CrashLog.Write"/> 记录（含动作名）。
/// </para>
/// </remarks>
internal static class ActionExecutor
{
    /// <summary>执行动作（后台线程，不阻塞调用方）。光标坐标默认取当前位置。</summary>
    public static void Run(ActionDto action) => Run(action, 0, 0);

    /// <summary>
    /// 执行动作（spawn 后台线程，不阻塞调用方）。cursorX/cursorY 为面板唤起时记录的光标坐标
    /// （0=取当前光标位置，供 <c>windowPin.toggle</c> 等需坐标的内部命令使用）。
    /// </summary>
    public static void Run(ActionDto action, int cursorX, int cursorY)
    {
        bool session = action.Type == ActionType.composite;

        if (session)
        {
            // 从 CapsLock 菜单启动时 CapsLock 仍可能处于按下态；运行期间钩子会屏蔽 CapsLock，
            // 若不在启动前清掉，状态机可能一直认为它按着（见 AutomationRunner.PrepareToRun）。
            AutomationRunner.PrepareToRun();
            Task.Run(() => AutomationRunner.Run(action, cursorX, cursorY));
            return;
        }

        Task.Run(() =>
        {
            long started = Environment.TickCount64;
            AppLog.Info("Action", $"执行「{action.Name}」({action.Type})");
            try
            {
                Execute(action, cursorX, cursorY);
                AppLog.Info("Action",
                    $"完成「{action.Name}」· {Environment.TickCount64 - started} ms");
            }
            catch (Exception ex)
            {
                AppLog.Error("Action", $"失败「{action.Name}」({action.Type})", ex);
                CrashLog.Write($"ActionExecutor[{action.Name}]", ex);
            }
        });
    }

    /// <summary>
    /// 同步执行一个动作（已位于后台线程）。旧格式先懒迁移；失败一律上抛
    /// （由 <see cref="Run"/> 或 <see cref="AutomationRunner"/> 记录）。
    /// <paramref name="ctx"/> 只用来取取消令牌与光标坐标。
    /// </summary>
    internal static VarValue? Execute(ActionDto action, int cursorX, int cursorY, RunContext? ctx = null)
    {
        RunProgram(action, cursorX, cursorY, ctx);
        return null;   // 结果变量由步骤的 out 映射写回，不再经返回值传递
    }

    /// <summary>用新引擎执行动作体；停止与失败按运行期的异常语义上抛。</summary>
    private static void RunProgram(ActionDto action, int cursorX, int cursorY, RunContext? ctx)
    {
        ActionProgram program = ActionPrograms.For(action);

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
