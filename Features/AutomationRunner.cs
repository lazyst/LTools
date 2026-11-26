using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Storage;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「一次自动化运行」的会话：单实例保护、停止热键、运行指示器、日志与收尾。
/// </summary>
/// <remarks>
/// <para>
/// 执行本身由 <see cref="ActionEngine"/> 负责；这里只管「一次运行」的边界——
/// 动作体就是 <see cref="ActionDto.Program"/>。
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
    /// <summary>失败报告里最多保留多少步轨迹（循环几千次的动作不该把报告撑爆）。</summary>
    private const int MaxTraces = 5000;

    private static int _running;
    private static volatile CancellationTokenSource? _cts;   // Stop() 从钩子 / UI 线程读取
    // 阶段 2：HandleStopHotkey 在钩子线程成对读写（keydown 置位、keyup 清位），补 volatile 明确可见性。
    private static volatile bool _stopKeySwallowed;

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

    /// <summary>
    /// 执行一个动作（在后台线程里跑，异常在此记录并提示，不再上抛）。
    /// <paramref name="initialVariables"/> 是自动触发注入的事件输出变量，只有从
    /// <c>ActionExecutor.RunRef</c> 的四参重载进来时才非 null；编辑器试运行 / 面板 / 快捷键都不传。
    /// </summary>
    public static void Run(ActionDto action, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables = null,
        RunSource source = RunSource.Manual)
    {
        ActionProgram? program = action.Program;
        if (program == null)
        {
            AppLog.Error("Flow", $"动作「{action.Name}」没有动作体");
            ToastService.Show($"「{action.Name}」没有动作体，无法运行", "动作失败",
                ToastService.LongMs, ToastKind.error, key: "run:" + action.Name,
                onClick: OpenLogsFolder, actionText: "查看日志");
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            TrayService.Notify("已有自动化正在运行");
            return;
        }

        Task.Run(() => RunCore(program, action.Name, cursorX, cursorY,
            startPath: null, trace: null, notify: true, noticeOverride: null, completed: null,
            resourceRoot: ActionStore.ResourceRootFor(action.Id), initialVariables: initialVariables,
            source: source));
    }

    /// <summary>
    /// 编辑器「试运行」：跑一份内存里的动作（**不必先保存**），轨迹与结果回调给调用方。
    /// </summary>
    /// <returns>false = 已有动作在运行（与真实运行共用同一个互斥，避免两套键鼠注入打架）。</returns>
    /// <remarks>
    /// 轨迹回调在后台线程上同步触发；调用方要自己投递到 UI 线程并攒批。
    /// 试运行是调试态，结束**一定**给提示（成功也弹，2.5 秒消失；失败常驻）——
    /// 否则「改一步、跑一下」时只能盯着状态行，看不到真实运行时的样子。
    /// 干跑（<paramref name="dryRun"/>）例外：它不该看起来像「真跑了一遍」，所以不给提示，
    /// 调用方自己读轨迹（MCP 的 <c>ltools_run_output</c>）。
    /// </remarks>
    public static bool TryRunProgram(string name, ActionProgram program, string? startPath,
        Action<StepTrace>? trace, Action<ActionRunResult>? completed, string? resourceRoot = null,
        bool dryRun = false, RunSource source = RunSource.Manual, Action<SlowStepInfo>? slowStep = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;

        Task.Run(() => RunCore(program, name, 0, 0,
            startPath, trace, notify: false, noticeOverride: dryRun ? null : RunNoticeMode.always,
            completed, resourceRoot, dryRun: dryRun, source: source, slowStep: slowStep));
        return true;
    }

    /// <summary>一次运行的公共实现：真实动作与编辑器试运行都走这里。</summary>
    /// <param name="notify">是否按用户设置给运行结果提示（真实运行为 true）。</param>
    /// <param name="noticeOverride">试运行传 <see cref="RunNoticeMode.always"/>，忽略用户设置。</param>
    /// <param name="resourceRoot">
    /// 额外资源根：草稿是 <c>staging/&lt;id&gt;/files</c>，正式动作是 <c>actions/&lt;id&gt;/files</c>；
    /// 图片相对路径先在这里找。
    /// </param>
    /// <param name="dryRun">
    /// 干跑：只解析参数 + 跑取值动作单元，不注入键鼠（见 <see cref="ActionRunOptions.DryRun"/>）。
    /// 干跑**不动**运行期截图目录（清空 / 回收都是副作用），也不改任何运行期状态。
    /// </param>
    /// <param name="source">
    /// 谁发起的（面板 / 快捷键 / 悬浮 / 自动触发 / 编辑器 / AI / 子动作），只写进运行历史的「来源」列。
    /// </param>
    /// <param name="slowStep">
    /// 慢步骤回调（编辑器用它把「第 N 行已运行 42 秒」写在状态行上）；日志与运行指示器由本类统一处理。
    /// </param>
    private static void RunCore(ActionProgram program, string name, int cursorX, int cursorY,
        string? startPath, Action<StepTrace>? trace, bool notify, RunNoticeMode? noticeOverride,
        Action<ActionRunResult>? completed, string? resourceRoot = null,
        IReadOnlyDictionary<string, Value>? initialVariables = null, bool dryRun = false,
        RunSource source = RunSource.Manual, Action<SlowStepInfo>? slowStep = null)
    {
        // 运行期截图（变量 .file）只服务一次运行：开始新一次就整体清空上一轮的缓存目录。
        // resources/images 里仍可能有「剪贴板图片」等程序生成图，按引用 + 时间界线回收。
        // 干跑不算一次运行：删掉用户 / 上一次运行的截图是副作用，跳过。
        if (!dryRun)
        {
            ImageLibrary.ClearCaptures();
            ImageLibrary.CollectGarbage(DateTime.Now);
        }

        _stopKeySwallowed = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        long started = Environment.TickCount64;
        int steps = 0;

        // 每次运行一个短 id：行首带 [run-3f2a]，失败报告按它把「这次运行」的日志捞出来。
        string runId = "run-" + IdFactory.ShortId(3);
        using var runScope = AppLog.BeginRun(runId);

        // 轨迹始终收一份（编辑器试运行也会拿到同一份）：失败时写进 logs/last-failure.log，
        // 否则「第 7 步为什么算出空值」只能开 debug 重跑，而很多场景根本重不了。
        var traces = new List<StepTrace>(64);
        void OnTrace(StepTrace t)
        {
            if (traces.Count < MaxTraces) traces.Add(t);
            trace?.Invoke(t);
        }

        AppLog.Info("Flow", $"开始执行「{name}」· 光标 ({cursorX},{cursorY})");

        bool reported = false;
        ActionRunResult? result = null;   // 运行历史要用（引擎之外抛异常时保持 null）
        string? fatal = null;

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
                StepTrace = OnTrace,
                ResourceRoot = resourceRoot,
                InitialVariables = initialVariables,
                DryRun = dryRun,
                // 「截图」步骤只有从编辑器试运行时才收起 LTools 自己的窗口（编辑器正挡着要截的地方）；
                // 面板 / 快捷键 / 悬浮 / 触发发起的运行一律不动它们，用户可能正想把自己的窗口截进去。
                HideOwnWindows = source is RunSource.Editor or RunSource.SubAction,
                Log = message => AppLog.Debug("Flow", message),
                StepStarted = step =>
                {
                    steps = step;
                    if ((step % 5) == 0) RecordingIndicatorWindow.ShowAutomation(IndicatorText(step));
                },
                OnSlowStep = info => ReportSlowStep(name, info, notify, slowStep),
            };

            result = new ActionEngine(StepRegistry.Default).Run(program, options);

            // 先落报告再提示：失败 toast 点开的就是这份文件，别让人点了个还没写出来的路径。
            if (!result.Completed && !result.Stopped)
            {
                FailureLog.Write(runId, name, result, traces);
                reported = true;
            }

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
                // 失败用 Error：级别选「仅错误」的用户也得能看到动作失败，别让它只出现在 Warn 里。
                AppLog.Error("Flow", $"中止「{name}」：{ex.Message} · {steps} 步");
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
            AppLog.Error("Flow", $"失败「{name}」第 {steps} 步", ex);
            CrashLog.Write($"Flow[{name}] 第{steps}步", ex);

            // 引擎之外抛的异常（启动失败、回调崩了）也留一份报告，内容只有错误与环境。
            if (!reported)
                FailureLog.Write(runId, name,
                    ActionRunResult.Failure(new Dictionary<string, Value>(), steps,
                        TimeSpan.FromMilliseconds(Environment.TickCount64 - started),
                        ex.Message, ex, stopped: false),
                    traces);

            if (notify || noticeOverride != null)
                ToastService.Show($"「{name}」启动失败：{ex.Message}", "动作失败",
                    ToastService.LongMs, ToastKind.error, key: "run:" + name,
                    onClick: OpenFailureReport, actionText: "查看日志");
        }
        finally
        {
            // 运行历史（P2-2）：每次运行结束记一行（时间 / 动作 / 来源 / 结果 / 耗时 / 失败位置）。
            // 干跑不记——它不注入键鼠，不代表「真的跑过一次」，混进来会把统计搅乱。
            // 写失败只留一条警告，不能影响运行结果（所以这里自己 try/catch，别靠调用方）。
            if (!dryRun)
                RunHistory.Record(runId, name, source, result, traces,
                    Environment.TickCount64 - started, steps, fatal);

            // 跨步骤「按下 / 抬起」的兜底：无论正常结束、失败还是被停止热键打断，
            // 都把仍按着的鼠标键与键盘键释放掉（正式运行 / 编辑器试运行 / 草稿试运行共用这里）。
            InputHoldState.ReleaseAll();

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
                onClick: OpenFailureReport, actionText: "查看日志", position: position);
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
    private static void ReportSlowStep(string name, SlowStepInfo info, bool notify,
        Action<SlowStepInfo>? forward)
    {
        int seconds = Math.Max(1, (int)Math.Round(info.Elapsed.TotalSeconds));

        AppLog.Warn("Flow",
            $"「{name}」{info.Label}「{info.UnitName}」已运行 {seconds} 秒，仍在执行（没有可打断的等待点）");

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
            "动作有点慢", 8000, ToastKind.warning, key: "slow:" + name,
            onClick: OpenLogsFolder, actionText: "查看日志");
    }

    /// <summary>
    /// 失败提示的「查看日志」按钮：优先打开 <c>logs/last-failure.log</c>
    /// （这次运行的轨迹 + 相关日志），没有这份文件就退回日志目录。
    /// </summary>
    private static void OpenFailureReport()
    {
        try
        {
            string report = FailureLog.LatestPath;
            if (File.Exists(report))
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
        if (_cts != null)
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
        }

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
