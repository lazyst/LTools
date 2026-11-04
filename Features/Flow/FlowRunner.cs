using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 自动化流程执行器：单线程解释嵌套块，支持变量、条件、循环、跳出 / 继续 / 停止。
/// </summary>
/// <remarks>
/// <para>
/// 与旧组合动作的兼容：<see cref="ActionDto.Flow"/> 为空时用 <see cref="FlowMigration"/> 把 Steps 转成流程。
/// </para>
/// <para>
/// 线程模型：在 <see cref="ActionExecutor"/> 的后台线程里同步执行；内部动作仍由
/// <see cref="ActionExecutor"/> 同步 dispatch 到 UI 线程。等待全部走可取消的 <see cref="RunContext.Wait"/>。
/// </para>
/// <para>
/// 安全阀：同一时间只允许一个流程；停止热键与宏回放共用（Ctrl+F3）；步数 / 时长超限自动中止。
/// 运行期间键盘钩子会屏蔽 LTools 自己的 CapsLock 手势（见 Hooks/KeyboardHook）。
/// </para>
/// </remarks>
internal static class FlowRunner
{
    private static int _running;
    private static volatile CancellationTokenSource? _cts;   // Stop() 从钩子 / UI 线程读取
    private static bool _stopKeySwallowed;

    private static readonly List<FlowNode> Empty = new();

    /// <summary>是否有流程正在运行（钩子 / 宏热键读取）。</summary>
    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// 由 <see cref="ActionExecutor.Run"/> 在**调用线程**（通常是 UI 线程）先调用：
    /// 清掉可能残留的 CapsLock 按下态，避免从 CapsLock 菜单启动后状态卡住。
    /// </summary>
    public static void PrepareToRun()
    {
        AppState.CapsLockIsDown = false;
        AppState.CapsLockPressTime = 0;
        AppState.OtherKeyPressed = false;
        AppState.CapsLockEscPressed = false;
    }

    /// <summary>执行一个 composite 动作（已在后台线程）。异常在此记录并提示，不再上抛。</summary>
    public static void RunAction(ActionDto action, int cursorX, int cursorY)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            TrayService.Notify("已有自动化正在运行");
            return;
        }

        _stopKeySwallowed = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ctx = new RunContext(action.Name, cts.Token, cursorX, cursorY);

        try
        {
            MacroHotkey.RegisterPlaybackStop();
            RecordingIndicatorWindow.ShowAutomation(IndicatorText(ctx));
            if (!ctx.Wait(80)) throw new FlowAbortException("已停止", userCancelled: true);

            var flow = FlowMigration.Ensure(action);
            var signal = ExecuteBlock(flow, ctx, action);

            // 根层的 break/continue 没有意义，按停止处理（编辑器会校验，这里是畸形配置兜底）。
            if (signal == Signal.breakLoop || signal == Signal.continueLoop)
                CrashLog.Write("FlowRunner.RootSignal",
                    new InvalidOperationException("流程根层出现 break/continue，已忽略"));
        }
        catch (FlowAbortException ex)
        {
            if (!ex.UserCancelled)
                TrayService.Notify("自动化：" + ex.Message);
        }
        catch (Exception ex)
        {
            CrashLog.Write($"Flow[{action.Name}] 第{ctx.StepCount}步", ex);
            TrayService.Notify("自动化失败：" + ex.Message);
        }
        finally
        {
            _cts = null;
            cts.Dispose();
            Volatile.Write(ref _running, 0);
            MacroHotkey.UnregisterPlaybackStop();
            RecordingIndicatorWindow.HideAutomation();
        }
    }

    /// <summary>请求停止流程（停止热键 / 钩子兜底）。</summary>
    public static void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        MacroPlayer.Stop();   // 流程里的宏步骤也要停
    }

    /// <summary>
    /// 键盘钩子的停止热键兜底：与 <see cref="MacroPlayer.HandleStopHotkey"/> 同样的成对吞键逻辑。
    /// 只在流程运行时由钩子调用。
    /// </summary>
    public static bool HandleStopHotkey(ushort vk, bool isDown, bool isUp)
    {
        if (vk != MacroSettings.PlaybackStopVk) return false;

        if (isUp)
        {
            if (!_stopKeySwallowed) return false;
            _stopKeySwallowed = false;
            return true;
        }
        if (!isDown || !IsRunning) return false;
        if (!MacroSettings.AreModifiersDown(MacroSettings.PlaybackStopModifiers)) return false;

        _stopKeySwallowed = true;
        Stop();
        return true;
    }

    // —— 解释器 ——

    private enum Signal
    {
        none,
        breakLoop,
        continueLoop,
        stop,
    }

    private static Signal ExecuteBlock(List<FlowNode> nodes, RunContext ctx, ActionDto owner)
    {
        foreach (var node in nodes)
        {
            ctx.EnterStep();
            if (node.DelayMs > 0 && !ctx.Wait(node.DelayMs))
                throw new FlowAbortException("已停止", userCancelled: true);

            if ((ctx.StepCount % 5) == 0)
                RecordingIndicatorWindow.ShowAutomation(IndicatorText(ctx));

            Signal signal;
            try
            {
                signal = ExecuteNode(node, ctx, owner);
            }
            catch (FlowAbortException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ctx.SetError(ex);
                CrashLog.Write($"Flow[{owner.Name}] 第{ctx.StepCount}步 {FlowText.Summary(node)}", ex);

                // 配置错误（未定义变量 / 图片缺失 / 正则无效等）不会随时间恢复，直接中止；
                // 运行期瞬时失败才交给节点的 OnFail 策略（默认继续下一步）。
                if (ex is FlowException ||
                    (node.OnFail ?? owner.CompositeOnFail ?? OnFailStrategy.@continue) == OnFailStrategy.abort)
                    throw new FlowAbortException($"第 {ctx.StepCount} 步失败：{ex.Message}");

                signal = Signal.none;
            }

            if (signal != Signal.none) return signal;
        }
        return Signal.none;
    }

    private static Signal ExecuteNode(FlowNode node, RunContext ctx, ActionDto owner)
    {
        switch (node.Kind)
        {
            case FlowNodeKind.action:
            {
                var action = node.Action ?? throw new FlowException("动作节点为空");
                if (action.Type == ActionType.composite)
                    throw new FlowException("流程不能嵌套流程");
                ActionExecutor.Execute(ActionMaterializer.Materialize(action, ctx), ctx.CursorX, ctx.CursorY);
                return Signal.none;
            }

            case FlowNodeKind.set:
            {
                var set = node.Set ?? throw new FlowException("赋值节点为空");
                if (string.IsNullOrWhiteSpace(set.Var)) throw new FlowException("赋值节点缺少变量名");
                ctx.Vars[set.Var.Trim()] = ProbeEvaluator.Evaluate(set.Probe, ctx);
                return Signal.none;
            }

            case FlowNodeKind.@if:
            {
                var branch = node.If ?? throw new FlowException("条件节点为空");
                bool ok = ProbeEvaluator.Test(branch.Condition, ctx);
                return ExecuteBlock(ok ? branch.Then : branch.Else ?? Empty, ctx, owner);
            }

            case FlowNodeKind.loop:
                return ExecuteLoop(node.Loop ?? throw new FlowException("循环节点为空"), ctx, owner);

            case FlowNodeKind.@break:
                return Signal.breakLoop;

            case FlowNodeKind.@continue:
                return Signal.continueLoop;

            case FlowNodeKind.stop:
                return Signal.stop;

            default:
                throw new FlowException($"未知流程节点：{node.Kind}");
        }
    }

    private static Signal ExecuteLoop(LoopNode loop, RunContext ctx, ActionDto owner)
    {
        int count = loop.Mode == LoopMode.count ? Math.Clamp(loop.Count, 0, 100_000) : int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            ctx.EnterStep();   // 循环迭代也计入预算，防止空循环空转

            if (loop.Mode == LoopMode.@while)
            {
                if (loop.While == null) throw new FlowException("条件循环缺少条件");
                if (!ProbeEvaluator.Test(loop.While, ctx)) break;
            }

            ctx.PushLoop(i);
            try
            {
                var signal = ExecuteBlock(loop.Body, ctx, owner);
                if (signal == Signal.stop) return Signal.stop;
                if (signal == Signal.breakLoop) break;
                // continueLoop：直接进入下一轮
            }
            finally
            {
                ctx.PopLoop();
            }
        }
        return Signal.none;
    }

    private static string IndicatorText(RunContext ctx)
    {
        string step = ctx.StepCount > 0 ? $" · 第 {ctx.StepCount} 步" : "";
        return $"自动化中{step} · {MacroSettings.PlaybackStopHotkeyDisplay} 停止";
    }
}
