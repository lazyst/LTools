using System;
using System.Collections.Generic;
using System.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Running;

namespace LTools.Features.Running;

/// <summary>
/// 运行的唯一入口：准入 → 开专用线程 / 排队 / 拒绝；出队；停止。见 <c>docs/RUN_INTEROP.md</c> §3。
/// </summary>
/// <remarks>
/// <para>
/// 钩子 / 面板 / 悬浮 / 托盘 / 自动触发全部只调 <see cref="SubmitAction"/>；「能不能开」的判定
/// 只在 <see cref="RunAdmission"/>，本类不重复判。
/// </para>
/// <para>
/// 每个运行一条**专用线程**（不是线程池）：引擎是同步阻塞的，32 条 <c>Task.Run</c> 会把线程池
/// 的阻塞线程占满，配置写入 / 别的 <c>Task.Run</c> 会跟着变慢。
/// </para>
/// </remarks>
internal static class RunCoordinator
{
    /// <summary>真实动作的提交（内置功能不走这里）。</summary>
    public static RunSubmission SubmitAction(ActionDto action, int cursorX, int cursorY,
        IReadOnlyDictionary<string, Value>? initialVariables, RunSource source)
    {
        if (action?.Program == null)
        {
            string name = action?.Name ?? "动作";
            AppLog.Error("Flow", $"动作「{name}」没有动作体");
            ToastService.Show($"「{name}」没有动作体，无法运行", "动作失败",
                ToastService.LongMs, ToastKind.error, key: "run:noBody",
                onClick: AutomationRunner.OpenLogsFolder, actionText: "查看日志");
            return RunSubmission.Rejected;
        }

        var job = new RunJob
        {
            Program = action.Program,
            Name = action.Name,
            ActionId = action.Id,
            InitialVariables = initialVariables,
            Source = source,
            Notify = true,
            CursorX = cursorX,
            CursorY = cursorY,
            NeedsAsk = initialVariables == null && ActionParams.InputsOf(action.Program).Count > 0,
        };

        return Submit(job);
    }

    /// <summary>试运行（编辑器 / MCP / 子动作）：忙时直接拒，不排队，整段独占输入。</summary>
    public static bool SubmitTrial(RunJob job, out string? error)
    {
        error = null;

        lock (StartGate)
        {
            if (RunningActions.Count > 0)
            {
                error = "已有动作正在运行，先等它结束或按停止";
                return false;
            }

            return StartNow(job, exclusiveInput: true) == RunSubmission.Started;
        }
    }

    /// <summary>准入后的分流：开跑 / 排队 / 拒绝（提示都在这里发）。</summary>
    /// <remarks>
    /// 「判定 + 登记」在同一把锁里：钩子连按 / 面板双击可能两个线程同时进来，
    /// 分开做的话两个都会看到「还没满」一起冲过上限（32 个的上限就成了 33）。
    /// 锁里只做计数判定与登记，开线程 / 弹提示都在外面。
    /// </remarks>
    public static RunSubmission Submit(RunJob job)
    {
        AdmissionOutcome outcome;
        int queuePosition = 0;
        bool added = false;

        lock (StartGate)
        {
            ActionRunConfig cfg = AppState.ActionRun;
            outcome = RunAdmission.Decide(
                cfg, RunningActions.Count, RunQueue.Count, RunQueue.Contains(job.ActionId), job.Source);

            if (outcome == AdmissionOutcome.Start)
                return StartNow(job, exclusiveInput: false);

            if (outcome == AdmissionOutcome.Queued)
            {
                queuePosition = RunQueue.Count + 1;
                added = RunQueue.TryEnqueue(
                    new RunRequest(job.ActionId, job.Name, job.CursorX, job.CursorY, job.Source, job.NeedsAsk));
            }
        }

        switch (outcome)
        {
            case AdmissionOutcome.Queued:
            {
                if (!added) return RunSubmission.Queued;

                if (ShouldNotifyBusy(job))
                    ToastService.Show($"「{job.Name}」已排队（第 {queuePosition} 个）", "动作排队",
                        ToastService.DefaultDurationMs, ToastKind.info, key: "run:busy",
                        onClick: AutomationRunner.Stop, actionText: "停止运行");
                return RunSubmission.Queued;
            }

            case AdmissionOutcome.RefuseQueued:
                if (ShouldNotifyBusy(job))
                    ToastService.Show($"「{job.Name}」已经在队列里了", "动作排队",
                        ToastService.DefaultDurationMs, ToastKind.info, key: "run:busy");
                return RunSubmission.Queued;

            default:
                if (ShouldNotifyBusy(job))
                    ToastService.Show(
                        $"「{job.Name}」正在运行，等它结束或按 {MacroSettings.PlaybackStopHotkeyDisplay} 停止",
                        "动作被拒绝", ToastService.DefaultDurationMs, ToastKind.warning, key: "run:busy",
                        onClick: AutomationRunner.Stop, actionText: "停止运行");
                return RunSubmission.Rejected;
        }
    }

    /// <summary>准入与登记的锁（见 <see cref="Submit"/>；<see cref="Drain"/> 里重入是同一线程）。</summary>
    private static readonly object StartGate = new();

    private static long _lastBusyNotifyTick;

    /// <summary>
    /// 「被拒 / 已排队」的提示节流：**只压快捷键**（按住连发会一秒刷几十条），
    /// 面板 / 悬浮点一下是人主动操作，照常提示。见 <c>docs/RUN_INTEROP.md</c> §6.4。
    /// </summary>
    private static bool ShouldNotifyBusy(RunJob job)
    {
        if (job.Source != RunSource.Hotkey) return true;

        long now = Environment.TickCount64;
        if (!RunHookPolicy.ShouldNotifyBusy(Interlocked.Read(ref _lastBusyNotifyTick), now)) return false;
        Interlocked.Exchange(ref _lastBusyNotifyTick, now);
        return true;
    }

    /// <summary>开一份新运行（准入已经判过）。</summary>
    private static RunSubmission StartNow(RunJob job, bool exclusiveInput)
    {
        // 没有别的运行：这次是「新的一轮」，运行期截图照旧整体清空（有别的运行在跑时不能删它的 .file）。
        if (RunningActions.Count == 0 && !job.DryRun)
        {
            ImageLibrary.ClearCaptures();
            ImageLibrary.CollectGarbage(DateTime.Now);
        }

        string runId = "run-" + IdFactory.ShortId(3);
        int copyIndex = RunningActions.NextCopyIndex(job.ActionId);
        var session = new ActionRunSession(job, runId, copyIndex, exclusiveInput);

        if (!RunningActions.TryAdd(session))
        {
            ToastService.Show($"「{job.Name}」没能启动（运行标识冲突）", "动作失败",
                ToastService.LongMs, ToastKind.error, key: "run:" + job.Name);
            return RunSubmission.Rejected;
        }

        var thread = new Thread(() => AutomationRunner.RunCore(session))
        {
            IsBackground = true,
            Name = runId,
        };
        session.Thread = thread;

        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            RunningActions.Remove(session);
            AppLog.Error("Flow", $"启动「{job.Name}」失败", ex);
            ToastService.Show($"「{job.Name}」没能启动：{ex.Message}", "动作失败",
                ToastService.LongMs, ToastKind.error, key: "run:" + job.Name);
            return RunSubmission.Rejected;
        }

        return RunSubmission.Started;
    }

    /// <summary>运行结束（或空出槽位）后取下一个排队项；由 <c>RunCore</c> 的 finally 调。</summary>
    public static void Drain()
    {
        while (true)
        {
            ActionRunConfig cfg = AppState.ActionRun;
            if (!cfg.AllowsQueue)
            {
                RunQueue.Clear();
                return;
            }

            if (RunningActions.Count >= cfg.EffectiveMaxConcurrent) return;

            RunRequest? request = RunQueue.TryDequeue();
            if (request == null) return;

            ActionDto? action = ActionRegistry.FindById(request.ActionId);
            if (action?.Program == null)
            {
                ToastService.Show($"「{request.DisplayName}」已被删除，跳过", "动作排队",
                    ToastService.DefaultDurationMs, ToastKind.warning, key: "run:busy");
                continue;
            }

            var job = new RunJob
            {
                Program = action.Program,
                Name = action.Name,
                ActionId = action.Id,
                Source = request.Source,
                Notify = true,
                CursorX = request.CursorX,
                CursorY = request.CursorY,
                NeedsAsk = request.NeedsAsk,
            };

            if (Submit(job) == RunSubmission.Started) return;

            // 轮到它时档位 / 上限又变了：别再往下排，避免半截队列。
            RunQueue.Clear();
            return;
        }
    }

    /// <summary>停某一个（指示器 / 运行列表里的 ✕）。</summary>
    public static bool Cancel(string runId) => RunningActions.Cancel(runId);

    /// <summary>
    /// 停掉试运行（编辑器「停止」/ 关窗、MCP 试跑、跨进程试运行）。
    /// **不碰用户动作**：试运行整段独占输入，调试时点「停止」不该把用户正在跑的动作也停掉。
    /// </summary>
    public static int CancelTrials()
    {
        int cancelled = 0;
        foreach (ActionRunSession session in RunningActions.Snapshot())
        {
            if (!session.ExclusiveInput) continue;
            if (RunningActions.Cancel(session.RunId)) cancelled++;
        }
        return cancelled;
    }

    /// <summary>停全部 + 清队列（停止热键 / 退出 / 编辑器「停止」）。返回停掉的运行数。</summary>
    public static int CancelAll() => CancelAll(out _);

    /// <summary>停全部 + 清队列，并给出清掉的队列条数（停止提示要报两个数）。</summary>
    public static int CancelAll(out int queued)
    {
        int cancelled = RunningActions.CancelAll();
        queued = RunQueue.Clear();
        if (queued > 0)
            AppLog.Info("Flow", $"已清空运行队列（{queued} 项）");
        return cancelled;
    }

    /// <summary>当前运行快照（指示器 / 运行列表 / 自测）。</summary>
    public static IReadOnlyList<ActionRunSession> Snapshot() => RunningActions.Snapshot();

    /// <summary>
    /// 退出时用：停全部 + 把每个运行 / 兜底里还按着的键放掉（不等线程——后台线程不挡退出）。
    /// </summary>
    public static void CancelAllAndRelease()
    {
        CancelAll();
        foreach (ActionRunSession session in Snapshot())
        {
            try { session.Hold.ReleaseAll(); } catch { /* 进程要退了 */ }
        }
        InputHoldState.ReleaseAmbient();
    }
}
