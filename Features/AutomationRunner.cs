using System;
using System.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features.Migration;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「一次自动化运行」的会话：单实例保护、停止热键、运行指示器、日志与收尾。
/// </summary>
/// <remarks>
/// <para>
/// 执行本身由 <see cref="ActionEngine"/> 负责；这里只管「一次运行」的边界——
/// 动作体从 <see cref="ActionPrograms.For"/> 取（旧动作第一次运行时懒迁移）。
/// </para>
/// <para>
/// 线程模型：在 <see cref="ActionExecutor.Run"/> 的后台线程里同步执行；停止热键与宏回放共用
/// （<see cref="MacroSettings.PlaybackStopVk"/>）。运行期间键盘钩子会屏蔽 LTools 自己的
/// CapsLock 手势（见 <c>Hooks/KeyboardHook</c>）。
/// </para>
/// <para>
/// 安全阀：同一时间只允许一个运行；步数 / 时长上限在引擎里，本层只负责把被中止的结果
/// 变成用户看得见的提示。
/// </para>
/// </remarks>
internal static class AutomationRunner
{
    private static int _running;
    private static volatile CancellationTokenSource? _cts;   // Stop() 从钩子 / UI 线程读取
    private static bool _stopKeySwallowed;

    /// <summary>是否有自动化正在运行（钩子 / 宏热键读取）。</summary>
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

    /// <summary>执行一个动作（已在后台线程）。异常在此记录并提示，不再上抛。</summary>
    public static void Run(ActionDto action, int cursorX, int cursorY)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            TrayService.Notify("已有自动化正在运行");
            return;
        }

        // 运行期截图（变量的 .file）只在这一次运行内有效：开始新一次 = 上一次的已经用完，顺手回收。
        // 界线用「现在」：本次稍后截的图比它新，不会被误删；上一次留下的（含上次没清掉的）在这里清干净。
        ImageLibrary.CollectGarbage(DateTime.Now);

        _stopKeySwallowed = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        long started = Environment.TickCount64;
        int steps = 0;

        AppLog.Info("Flow", $"开始执行「{action.Name}」· 光标 ({cursorX},{cursorY})");

        try
        {
            ActionProgram program = ActionPrograms.For(action);

            MacroHotkey.RegisterPlaybackStop();
            RecordingIndicatorWindow.ShowAutomation(IndicatorText(0));
            if (cts.Token.WaitHandle.WaitOne(80))
                throw new FlowAbortException("已停止", userCancelled: true);

            var options = new ActionRunOptions
            {
                Token = cts.Token,
                CursorX = cursorX,
                CursorY = cursorY,
                Log = message => AppLog.Debug("Flow", message),
                StepStarted = step =>
                {
                    steps = step;
                    if ((step % 5) == 0) RecordingIndicatorWindow.ShowAutomation(IndicatorText(step));
                },
            };

            ActionRunResult result = new ActionEngine(StepRegistry.Default).Run(program, options);

            if (result.Stopped)
                throw new FlowAbortException("已停止", userCancelled: true);

            if (!result.Completed)
            {
                throw result.Exception is not null
                    ? new FlowAbortException(result.Error ?? "执行失败", result.Exception)
                    : new FlowAbortException(result.Error ?? "执行失败");
            }

            AppLog.Info("Flow",
                $"完成「{action.Name}」· {result.Steps} 步 · {Environment.TickCount64 - started} ms");
        }
        catch (FlowAbortException ex)
        {
            if (ex.UserCancelled)
                AppLog.Info("Flow", $"已停止「{action.Name}」· {steps} 步");
            else
            {
                AppLog.Warn("Flow", $"中止「{action.Name}」：{ex.Message} · {steps} 步");
                TrayService.Notify("自动化：" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Flow", $"失败「{action.Name}」第 {steps} 步", ex);
            CrashLog.Write($"Flow[{action.Name}] 第{steps}步", ex);
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

    /// <summary>请求停止运行（停止热键 / 钩子兜底）。</summary>
    public static void Stop()
    {
        if (_cts != null) AppLog.Info("Flow", "收到停止请求");
        try { _cts?.Cancel(); } catch { /* 已释放 */ }
        MacroPlayer.Stop();   // 动作里的宏步骤也要停
    }

    /// <summary>
    /// 键盘钩子的停止热键兜底：与 <see cref="MacroPlayer.HandleStopHotkey"/> 同样的成对吞键逻辑。
    /// 只在运行中由钩子调用。
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

    private static string IndicatorText(int step) =>
        $"自动化中{(step > 0 ? $" · 第 {step} 步" : "")} · {MacroSettings.PlaybackStopHotkeyDisplay} 停止";
}
