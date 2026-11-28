using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Running;
using LTools.Features.Running;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「一次运行怎么跑」：引擎、轨迹、失败报告、结果提示、收尾。
/// </summary>
/// <remarks>
/// <para>
/// 运行的身份（注册表 / 准入 / 排队 / 停止）在 <see cref="RunCoordinator"/>；
/// 本类只管一次运行本身，**不再持有全局锁**，同一时刻可以有多个 <see cref="RunCore"/> 在各自的专用线程上跑。
/// </para>
/// <para>
/// 线程模型：每个运行一条后台线程（<see cref="RunCoordinator"/> 创建）；停止令牌、
/// 「按住不放」的登记、日志 run id 都按运行隔离（见 <c>docs/RUN_INTEROP.md</c> §4）。
/// </para>
/// </remarks>
internal static class AutomationRunner
{
    /// <summary>是否有自动化正在运行（钩子 / 宏热键读取；并发时 = 有任何一个在跑）。</summary>
    public static bool IsRunning => RunningActions.IsAny;

    /// <summary>
    /// 由 <see cref="ActionExecutor"/> 在**调用线程**（通常是 UI 线程）先调用：
    /// 清掉可能残留的 CapsLock 按下态，避免从 CapsLock 菜单启动后状态卡住。
    /// </summary>
    /// <remarks>并发时**别清**：别的运行可能正靠它算「CapsLock 按住 → 算修饰键」，清掉会让面板手势错乱。</remarks>
    public static void PrepareToRun()
    {
        if (RunningActions.Count > 0) return;

        AppState.CapsLockIsDown = false;
        AppState.CapsLockPressTime = 0;
        AppState.OtherKeyPressed = false;
    }

    /// <summary>
    /// 编辑器「试运行」/ MCP / 子动作：跑一份内存里的动作（**不必先保存**），轨迹与结果回调给调用方。
    /// </summary>
    /// <returns>false = 已有动作在运行（试运行不排队，见 docs/RUN_INTEROP.md §3.4）。</returns>
    /// <remarks>
    /// 试运行是调试态，结束**一定**给提示（成功也弹，2.5 秒消失；失败常驻）——
    /// 否则「改一步、跑一下」时只能盯着状态行，看不到真实运行时的样子。
    /// 干跑（<paramref name="dryRun"/>）例外：它不该看起来像「真跑了一遍」，所以不给提示。
    /// </remarks>
    public static bool TryRunProgram(string name, ActionProgram program, string? startPath,
        Action<StepTrace>? trace, Action<ActionRunResult>? completed, string? resourceRoot = null,
        bool dryRun = false, RunSource source = RunSource.Manual, Action<SlowStepInfo>? slowStep = null)
    {
        var job = new RunJob
        {
            Program = program,
            Name = name,
            StartPath = startPath,
            Trace = trace,
            Completed = completed,
            ResourceRoot = resourceRoot,
            DryRun = dryRun,
            Source = source,
            SlowStep = slowStep,
            Notify = false,
            NoticeOverride = dryRun ? null : RunNoticeMode.always,
        };

        return RunCoordinator.SubmitTrial(job, out _);
    }

    /// <summary>请求停止全部运行（停止热键 / 编辑器「停止」/ 托盘菜单）。</summary>
    public static void Stop()
    {
        AppLog.Info("Flow", "收到停止请求");

        // 让用户看见「按了、正在等当前步骤返回」：不可取消的长调用（UIA / OCR / 大文件）
        // 只能等它自己收尾，光按停止没有任何视觉反馈会让人以为键没生效。
        try
        {
            RecordingIndicatorWindow.ShowAutomation(
                $"正在停止 · 等待当前步骤返回 · {MacroSettings.PlaybackStopHotkeyDisplay}");
        }
        catch (Exception ex)
        {
            AppLog.Debug("Flow", $"更新运行指示器失败：{ex.Message}");
        }

        int stopped = RunCoordinator.CancelAll(out int queued);
        MacroPlayer.Stop();   // 动作里的宏步骤也要停

        // 「按了要有反应」：实际停掉了东西才提示（重复按停止不刷屏）。
        if (stopped > 0 || queued > 0)
        {
            string text = stopped > 0 ? $"已停止 {stopped} 个" : "已停止";
            if (queued > 0) text += $" · 清空队列 {queued} 项";
            ToastService.Show(text, "已停止", 2000, ToastKind.info, key: "run:stopped");
        }
    }

    /// <summary>一次运行的实现（由 <see cref="RunCoordinator"/> 在自己的线程上调用）。</summary>
    internal static void RunCore(ActionRunSession session)
    {
        RunJob job = session.Job;
        long started = Environment.TickCount64;
        int steps = 0;
        ActionRunResult? result = null;   // 运行历史要用（引擎之外抛异常时保持 null）
        string? fatal = null;
        bool reported = false;
        bool doneSent = false;            // 结果回调只发一次（试运行靠它收尾，漏发会让编辑器永远停在「运行中」）
        string? reportPath = null;

        void SendDone(ActionRunResult? r)
        {
            if (doneSent || r == null) return;
            doneSent = true;
            try { job.Completed?.Invoke(r); }
            catch (Exception ex) { AppLog.Warn("Flow", $"运行结果回调失败：{ex.Message}"); }
        }

        // 日志行首带 [run-xxxx]；并发时各是各的（AsyncLocal），单运行时别处线程的日志也能补上。
        using var logScope = AppLog.BeginRun(session.RunId);
        using var ambient = ActionRunScope.Begin(new ActionRunScope.Scope
        {
            Token = session.Cts.Token,
            Hold = session.Hold,
            Label = session.Label,
        });

        // 试运行（编辑器 / MCP / 子动作）整段独占输入：用户动作的注入会等它结束，
        // 免得调试的键鼠和用户正在跑的动作交错。
        using IDisposable? exclusiveGate = session.ExclusiveInput
            ? InputGate.EnterRunExclusive(session.Cts.Token)
            : null;

        // 轨迹始终收一份（编辑器试运行也会拿到同一份）：失败时写进失败报告。
        // 上限按运行算，并发 32 个最坏情况也有限。
        var traces = new List<StepTrace>(64);
        void OnTrace(StepTrace t)
        {
            if (traces.Count < RunLimits.MaxTracesPerRun) traces.Add(t);
            try { job.Trace?.Invoke(t); }
            catch (Exception ex) { AppLog.Warn("Flow", $"轨迹回调失败：{ex.Message}"); }
        }

        try
        {
            // 参数框等到「真正要跑」才问：排队期间动作可能已经被改，排队也不该先弹框。
            if (job.NeedsAsk && !job.DryRun && job.InitialVariables == null)
            {
                IReadOnlyDictionary<string, Value>? values = AskParameters(session);
                if (values == null)
                {
                    result = ActionRunResult.Success(new Dictionary<string, Value>(), 0,
                        TimeSpan.FromMilliseconds(Environment.TickCount64 - started), stopped: true);
                    AppLog.Info("Flow", $"「{session.Label}」参数输入已取消");
                    return;   // finally 照常收尾
                }

                job = new RunJob
                {
                    Program = job.Program,
                    Name = job.Name,
                    ActionId = job.ActionId,
                    StartPath = job.StartPath,
                    Trace = job.Trace,
                    Notify = job.Notify,
                    NoticeOverride = job.NoticeOverride,
                    Completed = job.Completed,
                    ResourceRoot = job.ResourceRoot,
                    InitialVariables = values,
                    DryRun = job.DryRun,
                    Source = job.Source,
                    SlowStep = job.SlowStep,
                    CursorX = job.CursorX,
                    CursorY = job.CursorY,
                    NeedsAsk = false,
                };
            }

            AppLog.Info("Flow", $"开始执行「{session.Label}」· 光标 ({job.CursorX},{job.CursorY})");

            // 排查「为什么打到别处」：运行开始记一次前台程序（并发 / 用户中途切走时是唯一线索）。
            LTools.Native.ForegroundApp? foreground = LTools.Native.ForegroundApp.Capture();
            if (foreground != null)
                AppLog.Info("Flow", $"前台：{foreground.ExeName} · {foreground.Title}");

            MacroHotkey.RegisterPlaybackStop();
            RecordingIndicatorWindow.ShowAutomation();
            if (session.Cts.Token.WaitHandle.WaitOne(80))
                throw new FlowAbortException("已停止", userCancelled: true);

            var options = new ActionRunOptions
            {
                Token = session.Cts.Token,
                CursorX = job.CursorX,
                CursorY = job.CursorY,
                StartPath = job.StartPath,
                StepTrace = OnTrace,
                ResourceRoot = job.ResourceRoot,
                InitialVariables = job.InitialVariables,
                DryRun = job.DryRun,
                // 「截图」步骤只有从编辑器试运行时才收起 LTools 自己的窗口（编辑器正挡着要截的地方）；
                // 面板 / 快捷键 / 悬浮 / 触发发起的运行一律不动它们，用户可能正想把自己的窗口截进去。
                HideOwnWindows = job.Source is RunSource.Editor or RunSource.SubAction,
                Log = message => AppLog.Debug("Flow", message),
                StepStarted = step =>
                {
                    steps = step;
                    session.Step = step;
                    if ((step % 5) == 0) RecordingIndicatorWindow.ShowAutomation();
                },
                OnSlowStep = info => ReportSlowStep(session, info, job.Notify, job.SlowStep),
            };

            result = new ActionEngine(StepRegistry.Default).Run(job.Program, options);

            // 先落报告再提示：失败 toast 点开的就是这份文件，别让人点了个还没写出来的路径。
            if (!result.Completed && !result.Stopped)
            {
                reportPath = FailureLog.Write(session.RunId, session.Label, result, traces);
                session.ReportPath = reportPath;
                reported = true;
            }

            if (job.Notify || job.NoticeOverride != null)
                NotifyFinished(session, result, job.NoticeOverride, reportPath);

            SendDone(result);

            if (result.Stopped)
                AppLog.Info("Flow", $"已停止「{session.Label}」· {result.Steps} 步");
            else if (!result.Completed)
                // 失败用 Error：级别选「仅错误」的用户也得能看到动作失败，别让它只出现在 Warn 里。
                AppLog.Error("Flow", $"中止「{session.Label}」：{result.Error} · {result.Steps} 步");
            else
                AppLog.Info("Flow",
                    $"完成「{session.Label}」· {result.Steps} 步 · {Environment.TickCount64 - started} ms");
        }
        catch (FlowAbortException ex)
        {
            if (ex.UserCancelled)
                AppLog.Info("Flow", $"已停止「{session.Label}」· {steps} 步");
            else
                AppLog.Error("Flow", $"中止「{session.Label}」：{ex.Message} · {steps} 步");

            result ??= ActionRunResult.Failure(new Dictionary<string, Value>(), steps,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started),
                ex.Message, ex, stopped: ex.UserCancelled);
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
            AppLog.Error("Flow", $"失败「{session.Label}」第 {steps} 步", ex);
            CrashLog.Write($"Flow[{session.Label}] 第{steps}步", ex);

            // 引擎之外抛的异常（启动失败、回调崩了）也留一份报告，内容只有错误与环境。
            result ??= ActionRunResult.Failure(new Dictionary<string, Value>(), steps,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started),
                ex.Message, ex, stopped: false);

            if (!reported)
            {
                reportPath = FailureLog.Write(session.RunId, session.Label, result, traces);
                session.ReportPath = reportPath;
            }

            if (job.Notify || job.NoticeOverride != null)
                ToastService.Show($"「{session.Label}」启动失败：{ex.Message}", "动作失败",
                    ToastService.LongMs, ToastKind.error, key: "run:" + session.RunId,
                    onClick: () => OpenFailureReport(reportPath), actionText: "查看日志");
        }
        finally
        {
            // 结果回调兜底：正常路径已经发过（doneSent），这里覆盖「参数取消 / 引擎之外抛异常」。
            SendDone(result);

            // 运行历史（P2-2）：每次运行结束记一行（时间 / 动作 / 来源 / 结果 / 耗时 / 失败位置）。
            // 干跑不记——它不注入键鼠，不代表「真的跑过一次」，混进来会把统计搅乱。
            if (!job.DryRun)
                RunHistory.Record(session.RunId, session.Label, job.Source, result, traces,
                    Environment.TickCount64 - started, steps, fatal);

            // 跨步骤「按下 / 抬起」的兜底：只放**自己这一份**按着的键（并发时别放掉别人的）。
            session.Hold.ReleaseAll();

            MacroHotkey.UnregisterPlaybackStop();
            RunningActions.Remove(session);

            // 空出槽位之后再取下一个：MaxConcurrent = 1 时先 Drain 会永远卡住。
            RunCoordinator.Drain();

            // 指示器（B1 单行）：全空就收；还有别的运行就显示最先开始的那个。
            IReadOnlyList<ActionRunSession> remaining = RunningActions.Snapshot();
            if (remaining.Count == 0) RecordingIndicatorWindow.HideAutomation();
            else RecordingIndicatorWindow.ShowAutomation();
        }
    }

    /// <summary>
    /// 运行前问一次参数（在**运行线程**上同步等 UI 线程；钩子线程绝不走这里）。
    /// 用户取消返回 null，由调用方当成「这次不跑」。
    /// </summary>
    /// <remarks>
    /// 并发时**同时只问一个**（同 <c>UiDialogHost</c> 的交互框队列）：两个参数框叠在一起，
    /// 用户分不清哪个属于哪个动作。后到的运行会一直等到前一个框关掉；按停止能把它从等待里叫醒。
    /// </remarks>
    private static IReadOnlyDictionary<string, Value>? AskParameters(ActionRunSession session)
    {
        if (!Ui.Available) return null;

        try
        {
            ParamGate.Wait(session.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        IReadOnlyDictionary<string, Value>? values = null;
        try
        {
            Ui.Run(() =>
            {
                bool suspended = HotkeyRegistry.Suspended;
                HotkeyRegistry.Suspended = true;
                try
                {
                    values = ActionParams.Ask(null, session.Job.ActionId, session.Job.Name + " · 参数",
                        ActionParams.InputsOf(session.Job.Program),
                        "填好点「运行」；这些值会记住，下次运行还是它们。");
                }
                finally
                {
                    HotkeyRegistry.Suspended = suspended;
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", $"询问参数失败：{ex.Message}");
        }
        finally
        {
            ParamGate.Release();
        }

        return values;
    }

    /// <summary>参数框串行化（一次只问一个）。</summary>
    private static readonly SemaphoreSlim ParamGate = new(1, 1);

    /// <summary>
    /// 把运行结果变成提示：失败**常驻**（红色，点一下看这次运行的报告），成功按设置（默认静默）。
    /// </summary>
    /// <param name="modeOverride">
    /// 非 null 时忽略用户设置（编辑器试运行传 <see cref="RunNoticeMode.always"/>）：
    /// 试跑就是要立刻看到结果，哪怕用户把真实运行的提示关了。
    /// </param>
    private static void NotifyFinished(ActionRunSession session, ActionRunResult result,
        RunNoticeMode? modeOverride, string? reportPath)
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
        string key = "run:" + session.RunId;
        string name = session.Label;

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

            // 失败性质（未找到 / 超时 / 前置条件）：正文里还没这么说时补一个短前缀，
            // 让人一眼分得清「没等到」和「参数写错」。
            if (result.ErrorKind is ActionErrorKind.notFound or ActionErrorKind.timeout
                or ActionErrorKind.precondition)
            {
                string kind = ActionErrors.Label(result.ErrorKind);
                if (!body.Contains(kind, StringComparison.Ordinal)) body = $"{kind} · {body}";
            }

            string title = result.FailedStepLabel is { Length: > 0 } label
                ? $"动作失败 · {label}"
                : "动作失败";

            ToastService.Show(body, $"{title}（{name}）",
                ToastService.LongMs, ToastKind.error, key: key,
                onClick: () => OpenFailureReport(reportPath), actionText: "查看日志", position: position);
            return;
        }

        if (mode == RunNoticeMode.always)
        {
            ToastService.Show(
                $"「{name}」· {result.Steps} 步 · {result.Elapsed.TotalMilliseconds:0} ms",
                trial ? "试运行完成" : "运行成功", 2500, ToastKind.success, key, position: position);
        }
    }

    /// <summary>
    /// 一步跑得太久（默认 30 秒）：写一条警告日志、把运行指示器改成「第 N 行已运行 X 秒」，
    /// 真实运行再给一条 8 秒的提示；编辑器试运行把文案交给状态行。**不杀线程**。
    /// </summary>
    private static void ReportSlowStep(ActionRunSession session, SlowStepInfo info, bool notify,
        Action<SlowStepInfo>? forward)
    {
        int seconds = Math.Max(1, (int)Math.Round(info.Elapsed.TotalSeconds));

        AppLog.Warn("Flow",
            $"「{session.Label}」{info.Label}「{info.UnitName}」已运行 {seconds} 秒，仍在执行（没有可打断的等待点）");

        try
        {
            RecordingIndicatorWindow.ShowAutomation(
                $"自动化中 · {info.Label} 已运行 {seconds} 秒 · {MacroSettings.PlaybackStopHotkeyDisplay} 停止");
        }
        catch (Exception ex)
        {
            AppLog.Debug("Flow", $"更新运行指示器失败：{ex.Message}");
        }

        try { forward?.Invoke(info); }
        catch (Exception ex) { AppLog.Debug("Flow", $"慢步骤回调失败：{ex.Message}"); }

        if (!notify) return;

        ToastService.Show(
            $"{info.Label}「{info.UnitName}」已运行 {seconds} 秒，仍在这一步里。",
            "动作有点慢", 8000, ToastKind.warning, key: "slow:" + session.RunId,
            onClick: OpenLogsFolder, actionText: "查看日志");
    }

    /// <summary>
    /// 失败提示的「查看日志」按钮：优先打开**这次运行自己**的报告，没有就打开最新那份，
    /// 再没有就退回日志目录。
    /// </summary>
    internal static void OpenFailureReport(string? path)
    {
        try
        {
            string? report = !string.IsNullOrEmpty(path) && File.Exists(path)
                ? path
                : File.Exists(FailureLog.LatestPath) ? FailureLog.LatestPath : null;

            if (report != null)
            {
                Process.Start(new ProcessStartInfo { FileName = report, UseShellExecute = true });
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", $"打开失败报告失败：{ex.Message}");
        }

        OpenLogsFolder();
    }

    /// <summary>打开运行日志目录（失败报告不存在时的退路）。</summary>
    internal static void OpenLogsFolder()
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
}
