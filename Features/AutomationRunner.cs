using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Storage;
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
    }

    /// <summary>执行一个动作（在后台线程里跑，异常在此记录并提示，不再上抛）。</summary>
    public static void Run(ActionDto action, int cursorX, int cursorY)
    {
        // 旧格式动作第一次运行时懒迁移（可能写盘，放在调用线程没问题）。
        ActionProgram program;
        try
        {
            program = ActionPrograms.For(action);
        }
        catch (Exception ex)
        {
            AppLog.Error("Flow", $"迁移「{action.Name}」失败", ex);
            CrashLog.Write($"Flow[{action.Name}] 迁移", ex);
            ToastService.Show($"「{action.Name}」启动失败：{ex.Message}", "动作失败",
                ToastService.StickyMs, ToastKind.error, key: "run:" + action.Name, OpenLogsFolder);
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            TrayService.Notify("已有自动化正在运行");
            return;
        }

        Task.Run(() => RunCore(program, action.Name, cursorX, cursorY,
            startPath: null, trace: null, notify: true, noticeOverride: null, completed: null,
            resourceRoot: ActionStore.ResourceRootFor(action.Id)));
    }

    /// <summary>
    /// 编辑器「试运行」：跑一份内存里的动作（**不必先保存**），轨迹与结果回调给调用方。
    /// </summary>
    /// <returns>false = 已有动作在运行（与真实运行共用同一个互斥，避免两套键鼠注入打架）。</returns>
    /// <remarks>
    /// 轨迹回调在后台线程上同步触发；调用方要自己投递到 UI 线程并攒批。
    /// 试运行是调试态，结束**一定**给提示（成功也弹，2.5 秒消失；失败常驻）——
    /// 否则「改一步、跑一下」时只能盯着状态行，看不到真实运行时的样子。
    /// </remarks>
    public static bool TryRunProgram(string name, ActionProgram program, string? startPath,
        Action<StepTrace>? trace, Action<ActionRunResult>? completed, string? resourceRoot = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

        Task.Run(() => RunCore(program, name, 0, 0,
            startPath, trace, notify: false, noticeOverride: RunNoticeMode.always, completed, resourceRoot));
        return true;
    }

    /// <summary>一次运行的公共实现：真实动作与编辑器试运行都走这里。</summary>
    /// <param name="notify">是否按用户设置给运行结果提示（真实运行为 true）。</param>
    /// <param name="noticeOverride">试运行传 <see cref="RunNoticeMode.always"/>，忽略用户设置。</param>
    /// <param name="resourceRoot">
    /// 额外资源根：草稿是 <c>staging/&lt;id&gt;/files</c>，正式动作是 <c>actions/&lt;id&gt;/files</c>；
    /// 图片相对路径先在这里找。
    /// </param>
    private static void RunCore(ActionProgram program, string name, int cursorX, int cursorY,
        string? startPath, Action<StepTrace>? trace, bool notify, RunNoticeMode? noticeOverride,
        Action<ActionRunResult>? completed, string? resourceRoot = null)
    {
        // 运行期截图（变量 .file）只服务一次运行：开始新一次就整体清空上一轮的缓存目录。
        // resources/images 里仍可能有「剪贴板图片」等程序生成图，按引用 + 时间界线回收。
        ImageLibrary.ClearCaptures();
        ImageLibrary.CollectGarbage(DateTime.Now);

        _stopKeySwallowed = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        long started = Environment.TickCount64;
        int steps = 0;

        AppLog.Info("Flow", $"开始执行「{name}」· 光标 ({cursorX},{cursorY})");

        try
        {
            MacroHotkey.RegisterPlaybackStop();
            RecordingIndicatorWindow.ShowAutomation(IndicatorText(0));
            if (cts.Token.WaitHandle.WaitOne(80))
                throw new FlowAbortException("已停止", userCancelled: true);

            var options = new ActionRunOptions
            {
                Token = cts.Token,
                CursorX = cursorX,
                CursorY = cursorY,
                StartPath = startPath,
                StepTrace = trace,
                ResourceRoot = resourceRoot,
                Log = message => AppLog.Debug("Flow", message),
                StepStarted = step =>
                {
                    steps = step;
                    if ((step % 5) == 0) RecordingIndicatorWindow.ShowAutomation(IndicatorText(step));
                },
            };

            ActionRunResult result = new ActionEngine(StepRegistry.Default).Run(program, options);

            if (notify || noticeOverride != null) NotifyFinished(name, result, noticeOverride);

            try { completed?.Invoke(result); }
            catch (Exception ex) { AppLog.Warn("Flow", $"运行结果回调失败：{ex.Message}"); }

            if (result.Stopped)
                throw new FlowAbortException("已停止", userCancelled: true);

            if (!result.Completed)
            {
                throw result.Exception is not null
                    ? new FlowAbortException(result.Error ?? "执行失败", result.Exception)
                    : new FlowAbortException(result.Error ?? "执行失败");
            }

            AppLog.Info("Flow",
                $"完成「{name}」· {result.Steps} 步 · {Environment.TickCount64 - started} ms");
        }
        catch (FlowAbortException ex)
        {
            if (ex.UserCancelled)
                AppLog.Info("Flow", $"已停止「{name}」· {steps} 步");
            else
                AppLog.Warn("Flow", $"中止「{name}」：{ex.Message} · {steps} 步");
        }
        catch (Exception ex)
        {
            AppLog.Error("Flow", $"失败「{name}」第 {steps} 步", ex);
            CrashLog.Write($"Flow[{name}] 第{steps}步", ex);

            if (notify || noticeOverride != null)
                ToastService.Show($"「{name}」启动失败：{ex.Message}", "动作失败",
                    ToastService.StickyMs, ToastKind.error, key: "run:" + name, onClick: OpenLogsFolder);
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

    /// <summary>
    /// 把运行结果变成提示：失败**常驻**（红色，点一下看日志），成功按设置（默认静默）。
    /// </summary>
    /// <param name="modeOverride">
    /// 非 null 时忽略用户设置（编辑器试运行传 <see cref="RunNoticeMode.always"/>）：
    /// 试跑就是要立刻看到结果，哪怕用户把真实运行的提示关了。
    /// </param>
    /// <remarks>
    /// 失败以前走的是 <see cref="MouseTip"/>（跟鼠标、1.8 秒消失），跑完去看时早就没了；
    /// 现在用 toast，并且同一个动作用同一个 key，下一次运行会覆盖上一次的失败提示。
    /// </remarks>
    private static void NotifyFinished(string name, ActionRunResult result, RunNoticeMode? modeOverride = null)
    {
        RunNoticeMode mode = RunNoticeMode.onFailure;
        ToastPosition position = ToastPosition.bottomRight;
        try
        {
            var config = ConfigStore.LoadConfig();
            mode = config.RunNotice;
            position = config.ToastPosition;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", $"读取提示设置失败：{ex.Message}");
        }

        if (modeOverride is RunNoticeMode forced) mode = forced;
        if (mode == RunNoticeMode.off) return;

        bool trial = modeOverride != null;
        string key = "run:" + name;

        if (result.Stopped)
        {
            ToastService.Show($"「{name}」· {result.Steps} 步", "已停止", 2000, ToastKind.info,
                key, position: position);
            return;
        }

        if (!result.Completed)
        {
            // 标题已经带了「第 1 行」，正文里再重复一遍只会显得啰嗦。
            string body = result.Error ?? "执行失败";
            if (result.FailedStepLabel is { Length: > 0 } failedAt
                && body.StartsWith(failedAt, StringComparison.Ordinal))
                body = body[failedAt.Length..].TrimStart('：', ' ');

            string title = result.FailedStepLabel is { Length: > 0 } label
                ? $"动作失败 · {label}"
                : "动作失败";

            ToastService.Show(body, $"{title}（{name}）",
                ToastService.StickyMs, ToastKind.error, key, OpenLogsFolder, position);
            return;
        }

        if (mode == RunNoticeMode.always)
        {
            ToastService.Show(
                $"「{name}」· {result.Steps} 步 · {result.Elapsed.TotalMilliseconds:0} ms",
                trial ? "试运行完成" : "运行成功", 2500, ToastKind.success, key, position: position);
        }
    }

    /// <summary>失败提示的「点击查看详情」：打开运行日志目录（运行记录面板做好后换成它）。</summary>
    private static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.LogsDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", $"打开日志目录失败：{ex.Message}");
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
