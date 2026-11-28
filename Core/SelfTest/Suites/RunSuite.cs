using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Running;
using LTools.Features;
using LTools.Features.Running;

namespace LTools.Core.SelfTest;

/// <summary>
/// 运行内核的纯逻辑：准入表、注册表、队列、输入闸、按运行隔离（日志 runId / 按住键 / 等待按键）。
/// 真并发跑动作走 <c>tests/RunConcurrencySmoke</c>；钩子行为只能真机点。
/// </summary>
internal static class RunSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("runAdmission", "独占档：空闲开跑，忙碌直接拒", Exclusive));
        cases.Add(new("runAdmission", "互操作档：忙碌拒（不排队）", Interop));
        cases.Add(new("runAdmission", "排队档：忙碌排队，队列满了才拒", QueueMode));
        cases.Add(new("runAdmission", "并发档：未满就再开一份（同一动作也算）", Concurrent));
        cases.Add(new("runAdmission", "队列里已有同名：拒（不越按越多）", SameActionQueued));
        cases.Add(new("runAdmission", "自动触发 / 试运行：忙碌不排队", TriggerAndTrial));
        cases.Add(new("runAdmission", "配置夹取与各档的有效上限", ConfigClamp));
        cases.Add(new("runCore", "注册表：登记 / 摘除 / 计数 / 快照", RegistryBasics));
        cases.Add(new("runCore", "注册表：同一动作的副本号与显示名", CopyIndex));
        cases.Add(new("runCore", "注册表：按 runId 停一个 / 全停", CancelOneAndAll));
        cases.Add(new("runCore", "按住键是每运行一份（互不干扰）", HoldIsolation));
        cases.Add(new("runCore", "失败报告按运行分文件，runId 不会跑出目录", ReportPaths));
        cases.Add(new("runQueue", "队列：先进先出 / 同名去重 / 清空", QueueBasics));
        cases.Add(new("runHook", "动作运行时：档位决定钩子让路", HookPolicy));
        cases.Add(new("runHook", "运行中轻按 CapsLock 不补 Esc", CapsLockTap));
        cases.Add(new("runHook", "「被拒」提示 1 秒内只提示一次", BusyThrottle));
        cases.Add(new("runTarget", "目标窗口：留空不动、找不到报「未找到」", TargetWindow));
        cases.Add(new("runObservability", "托盘计数：没有运行时是 LTools，有运行时带个数", TrayCount));
        cases.Add(new("runGate", "输入闸：同线程可重入、跨线程互斥", GateExclusion));
        cases.Add(new("runGate", "输入闸：等待时按停止能取消", GateCancellation));
        cases.Add(new("runLog", "日志 runId 按运行隔离，单运行时给别处线程兜底", LogScope));
        cases.Add(new("runKeyWaiter", "等待按键：多个等待者先 arm 先命中", Waiters));
    }

    private static RunJob Job(string name, string actionId = "", RunSource source = RunSource.Manual) => new()
    {
        Program = new ActionProgram { Name = name },
        Name = name,
        ActionId = actionId,
        Source = source,
    };

    private static void Exclusive()
    {
        var cfg = new ActionRunConfig(ActionRunMode.exclusive);
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 0, 0, false, RunSource.Panel), "空闲直开");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Panel),
            "独占档忙碌直接拒");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, true, RunSource.Panel),
            "独占档不看同名排队");
    }

    private static void Interop()
    {
        var cfg = new ActionRunConfig(ActionRunMode.interop);
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 0, 0, false, RunSource.Hotkey), "空闲直开");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Hotkey),
            "互操作档不排队");
        Check.Equal(0, cfg.Normalized().EffectiveMaxQueued, "互操作档的有效队列上限是 0");
    }

    private static void QueueMode()
    {
        var cfg = new ActionRunConfig(ActionRunMode.queue, MaxQueued: 2);
        Check.Equal(AdmissionOutcome.Queued, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Hotkey), "忙碌排队");
        Check.Equal(AdmissionOutcome.Queued, RunAdmission.Decide(cfg, 1, 1, false, RunSource.Hotkey),
            "队列还没满继续排");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 2, false, RunSource.Hotkey),
            "队列满了才拒");
        Check.Equal(1, cfg.EffectiveMaxConcurrent, "排队档仍是单跑");
    }

    private static void Concurrent()
    {
        var cfg = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 3, MaxQueued: 5);
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Floating),
            "未满就再开一份");
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 1, 0, true, RunSource.Floating),
            "同一动作也可以再开一份");
        Check.Equal(AdmissionOutcome.Queued, RunAdmission.Decide(cfg, 3, 0, false, RunSource.Floating),
            "满了排队");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 3, 5, false, RunSource.Floating),
            "满员且队列满就拒");
    }

    private static void SameActionQueued()
    {
        var cfg = new ActionRunConfig(ActionRunMode.queue, MaxQueued: 5);
        Check.Equal(AdmissionOutcome.RefuseQueued, RunAdmission.Decide(cfg, 1, 1, true, RunSource.Panel),
            "队列里已有同名：拒");
        var conc = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 1, MaxQueued: 5);
        Check.Equal(AdmissionOutcome.RefuseQueued, RunAdmission.Decide(conc, 1, 1, true, RunSource.Panel),
            "并发档满了也按同名去重");
    }

    private static void TriggerAndTrial()
    {
        var cfg = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 8, MaxQueued: 8);
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Trigger),
            "自动触发忙碌丢弃");
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 0, 0, false, RunSource.Trigger),
            "自动触发空闲照跑");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Editor),
            "试运行忙碌直接拒");
        Check.Equal(AdmissionOutcome.Start, RunAdmission.Decide(cfg, 0, 0, false, RunSource.Editor),
            "试运行空闲照跑");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.Mcp),
            "MCP 试运行也不排队");
        Check.Equal(AdmissionOutcome.RefuseFull, RunAdmission.Decide(cfg, 1, 0, false, RunSource.SubAction),
            "子动作试运行也不排队");
    }

    private static void ConfigClamp()
    {
        Check.Equal(32, RunLimits.MaxConcurrentCeiling, "并发上限常量是 32");
        Check.Equal(32, RunLimits.DefaultMaxConcurrent, "默认并发数等于上限");
        Check.Equal(8, RunLimits.DefaultMaxQueued, "默认队列 8");

        Check.Equal(32, new ActionRunConfig(MaxConcurrent: 999).Normalized().MaxConcurrent, "上限夹到 32");
        Check.Equal(1, new ActionRunConfig(MaxConcurrent: 0).Normalized().MaxConcurrent, "下限夹到 1");
        Check.Equal(32, new ActionRunConfig(MaxQueued: 99).Normalized().MaxQueued, "队列夹到 32");
        Check.Equal(0, new ActionRunConfig(MaxQueued: -1).Normalized().MaxQueued, "队列夹到 0");
        Check.Equal(ActionRunMode.concurrent, ActionRunConfig.Default.Mode, "出厂档是「并发运行」");
        Check.True(new ActionRunConfig(ActionRunMode.concurrent).AllowsConcurrency, "并发档允许并发");
        Check.False(new ActionRunConfig(ActionRunMode.queue).AllowsConcurrency, "排队档不并发");
    }

    private static void RegistryBasics()
    {
        int before = RunningActions.Count;
        var a = new ActionRunSession(Job("A", "act-a"), "run-test-a", 1, false);
        var b = new ActionRunSession(Job("B", "act-b"), "run-test-b", 1, false);

        Check.True(RunningActions.TryAdd(a), "登记 A");
        Check.True(RunningActions.TryAdd(b), "登记 B");
        Check.Equal(before + 2, RunningActions.Count, "计数 +2");
        Check.True(RunningActions.IsAny, "有任何一个在跑");

        IReadOnlyList<ActionRunSession> snapshot = RunningActions.Snapshot();
        Check.Contains(string.Join(",", snapshot.Select(x => x.RunId)), "run-test-a", "快照含 A");

        RunningActions.Remove(a);
        Check.Equal(before + 1, RunningActions.Count, "摘掉一个计数 -1");
        RunningActions.Remove(b);
        Check.Equal(before, RunningActions.Count, "摘掉后回到原计数");
        Check.Equal(before > 0, RunningActions.IsAny, "全空后 IsAny 与计数一致");
    }

    private static void CopyIndex()
    {
        string id = "act-copy-" + Guid.NewGuid().ToString("N")[..6];
        Check.Equal(1, RunningActions.NextCopyIndex(id), "第一份是 1");
        Check.Equal(2, RunningActions.NextCopyIndex(id), "第二份是 2");

        var first = new ActionRunSession(Job("同动作", id), "run-copy-1-" + id, 1, false);
        var second = new ActionRunSession(Job("同动作", id), "run-copy-2-" + id, 2, false);
        Check.Equal("同动作", first.Label, "第一份不带后缀");
        Check.Equal("同动作 #2", second.Label, "第二份带 #2");
        Check.Equal(2, RunningActions.CountOf(id), "计数按动作算");

        RunningActions.TryAdd(first);
        RunningActions.TryAdd(second);

        // 归还：两次 NextCopyIndex 对应两次 Remove
        RunningActions.Remove(first);
        Check.Equal(1, RunningActions.CountOf(id), "摘一份后还剩 1");
        RunningActions.Remove(second);
        Check.Equal(0, RunningActions.CountOf(id), "全摘掉后计数归零");
    }

    private static void CancelOneAndAll()
    {
        var a = new ActionRunSession(Job("停A", "act-stop-a"), "run-stop-a", 1, false);
        var b = new ActionRunSession(Job("停B", "act-stop-b"), "run-stop-b", 1, false);
        RunningActions.TryAdd(a);
        RunningActions.TryAdd(b);

        Check.True(RunningActions.Cancel(a.RunId), "按 runId 停一个");
        Check.True(a.Cts.IsCancellationRequested, "只有那个被取消");
        Check.False(b.Cts.IsCancellationRequested, "另一个不受影响");

        int cancelled = RunningActions.CancelAll();
        Check.True(cancelled >= 2, "全停覆盖两个");
        Check.True(b.Cts.IsCancellationRequested, "全停取消另一个");

        RunningActions.Remove(a);
        RunningActions.Remove(b);
        Check.False(RunningActions.Cancel("run-none"), "停不存在的 runId 返回 false");
    }

    private static void HoldIsolation()
    {
        var a = new InputHoldState();
        var b = new InputHoldState();

        a.PressKey(0x11);
        a.PressMouse(1);
        Check.Equal(2, a.HeldCount, "A 记了两个");
        Check.Equal(0, b.HeldCount, "B 不受影响");

        a.ReleaseKey(0x11);
        Check.Equal(1, a.HeldCount, "A 放掉一个");
        Check.Equal(0, b.HeldCount, "B 仍是 0");
    }

    private static void ReportPaths()
    {
        string path = FailureLog.PathFor("run-abc/../x");
        string name = Path.GetFileName(path);
        Check.True(name.EndsWith(".log", StringComparison.Ordinal), "报告是 .log");
        Check.NotContains(name, "/", "文件名不含目录分隔符");
        Check.NotContains(name, "..", "文件名不含 ..");
        Check.Contains(path, Path.Combine("logs", "failures"), "报告进 logs/failures/");

        string normal = FailureLog.PathFor("run-3f2a");
        Check.Equal("run-3f2a.log", Path.GetFileName(normal), "正常 runId 原样保留");
    }

    private static void QueueBasics()
    {
        RunQueue.Clear();
        var a = new RunRequest("act-q-a", "A", 0, 0, RunSource.Panel, false);
        var b = new RunRequest("act-q-b", "B", 1, 2, RunSource.Hotkey, true);

        Check.True(RunQueue.TryEnqueue(a), "入队 A");
        Check.False(RunQueue.TryEnqueue(a), "同名不重复入队");
        Check.True(RunQueue.TryEnqueue(b), "入队 B");
        Check.Equal(2, RunQueue.Count, "队列两条");
        Check.True(RunQueue.Contains("act-q-a"), "Contains 命中");

        Check.Equal("act-q-a", RunQueue.TryDequeue()?.ActionId, "先进先出");
        Check.Equal("act-q-b", RunQueue.TryDequeue()?.ActionId, "第二条接着出");
        Check.Equal(0, RunQueue.TryDequeue() is null ? 0 : 1, "空了就取不到");

        RunQueue.TryEnqueue(a);
        RunQueue.TryEnqueue(b);
        Check.Equal(2, RunQueue.Clear(), "清空返回条数");
        Check.Equal(0, RunQueue.Count, "清空后为空");
    }

    private static void HookPolicy()
    {
        var exclusive = new ActionRunConfig(ActionRunMode.exclusive);
        var interop = new ActionRunConfig(ActionRunMode.interop);
        var queue = new ActionRunConfig(ActionRunMode.queue);
        var concurrent = new ActionRunConfig(ActionRunMode.concurrent);

        // 空闲时任何档都接管（正常运行）。
        foreach (ActionRunConfig cfg in new[] { exclusive, interop, queue, concurrent })
        {
            Check.True(RunHookPolicy.HandlesHotkey(cfg, 0), $"{cfg.Mode}：空闲接管热键");
            Check.True(RunHookPolicy.MouseGesturesEnabled(cfg, 0), $"{cfg.Mode}：空闲鼠标手势可用");
        }

        Check.False(RunHookPolicy.HandlesHotkey(exclusive, 1), "独占档：运行中热键整段让路");
        Check.False(RunHookPolicy.MouseGesturesEnabled(exclusive, 1), "独占档：运行中鼠标手势让路");

        Check.True(RunHookPolicy.HandlesHotkey(interop, 1), "互操作档：运行中热键照常接管");
        Check.True(RunHookPolicy.MouseGesturesEnabled(interop, 1), "互操作档：运行中可开面板");
        Check.True(RunHookPolicy.HandlesHotkey(queue, 3), "排队档：运行中热键照常接管");
        Check.True(RunHookPolicy.HandlesHotkey(concurrent, 32), "并发档：32 个在跑也接管热键");

        // CapsLock 轻按：只有完全没有运行时才补 Esc。
        Check.True(RunHookPolicy.CapsLockTapSendsEsc(0), "空闲轻按 CapsLock 发 Esc");
        Check.False(RunHookPolicy.CapsLockTapSendsEsc(1), "运行中轻按 CapsLock 不发 Esc");
    }

    private static void CapsLockTap()
    {
        // 与上一条同一口径，但单独列出来：它是「运行中不许往目标程序补 Esc」的验收点。
        Check.False(RunHookPolicy.CapsLockTapSendsEsc(2), "两个运行在跑时也不补 Esc");
        Check.Equal(1000, RunHookPolicy.BusyNotifyIntervalMs, "提示节流窗口 1 秒");
    }

    private static void BusyThrottle()
    {
        Check.True(RunHookPolicy.ShouldNotifyBusy(0, 1000), "第一次（没有上次）要提示");
        Check.False(RunHookPolicy.ShouldNotifyBusy(1000, 1500), "500ms 内不重复提示");
        Check.False(RunHookPolicy.ShouldNotifyBusy(1000, 1999), "差 1ms 到窗口仍不提示");
        Check.True(RunHookPolicy.ShouldNotifyBusy(1000, 2000), "满 1 秒再提示");
        Check.True(RunHookPolicy.ShouldNotifyBusy(1000, 9000), "很久之后再按照常提示");
    }

    private static void TargetWindow()
    {
        // 留空 = 什么都不做（不查窗口、不抛错）。
        RunTarget.Activate("", null, "测试");
        RunTarget.Activate("   ", null, "测试");

        // 找不到 → 「未找到」性质（不是 unknown：调用方可以把它当「等等再来」）。
        string missing = "__不存在的窗口__" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            RunTarget.Activate(missing, null, "模拟按键");
            Check.True(false, "找不到窗口时必须报错");
        }
        catch (ActionException ex)
        {
            Check.Equal(ActionErrorKind.notFound, ActionErrors.KindOf(ex), "找不到窗口 = 未找到");
            Check.Contains(ex.Message, "模拟按键", "文案带动作单元名");
        }
    }

    private static void TrayCount()
    {
        int before = RunningActions.Count;
        Check.Equal(before > 0 ? $"LTools · 正在运行 {before} 个动作" : "LTools",
            TrayMenu.TooltipText(), "托盘提示跟随注册表");

        var session = new ActionRunSession(Job("托盘计数", "act-tray"), "run-tray-selftest", 1, false);
        RunningActions.TryAdd(session);
        try
        {
            Check.Contains(TrayMenu.TooltipText(), $"{before + 1} 个动作", "加一个运行后带个数");
        }
        finally
        {
            RunningActions.Remove(session);
        }

        Check.Equal(before > 0 ? $"LTools · 正在运行 {before} 个动作" : "LTools",
            TrayMenu.TooltipText(), "摘掉后回到原样");
    }

    private static void GateExclusion()
    {
        Check.False(InputGate.IsHeld, "初始没被占");

        bool otherEntered = false;
        var release = new ManualResetEventSlim(false);
        Task other;

        using (InputGate.Enter(InputKind.Mouse))
        {
            Check.True(InputGate.IsHeld, "进入后持有");

            using (InputGate.Enter(InputKind.Mouse))
                Check.True(InputGate.IsHeld, "同线程重入仍然持有");

            Check.True(InputGate.IsHeld, "内层释放后外层还持有");

            // 另一个线程应该进不来：给它 120ms，拿不到就是互斥生效。
            other = Task.Run(() =>
            {
                using (InputGate.Enter(InputKind.Text))
                {
                    otherEntered = true;
                    release.Wait(2000);
                }
            });

            Thread.Sleep(120);
            Check.False(otherEntered, "跨线程互斥（另一个进不来）");
        }

        // 外层释放之后它才能进来（上面不能等它，否则自己把自己锁住）。
        release.Set();
        other.Wait(3000);
        Check.True(otherEntered, "释放后另一个进来了");
        Check.False(InputGate.IsHeld, "全部释放后不持有");
    }

    private static void GateCancellation()
    {
        using var held = InputGate.Enter(InputKind.Mouse);
        using var cts = new CancellationTokenSource();

        var waiter = Task.Run(() =>
        {
            try
            {
                using (InputGate.Enter(InputKind.Keyboard, cts.Token))
                    return "entered";
            }
            catch (ActionAbortedException)
            {
                return "aborted";
            }
        });

        Thread.Sleep(80);
        cts.Cancel();
        Check.Equal("aborted", waiter.GetAwaiter().GetResult(), "等锁时取消 → 按已停止抛出");
    }

    private static void LogScope()
    {
        AppLog.SetSingleRunFallback(null);
        Check.True(AppLog.CurrentRunId == null, "没有运行时为 null");

        using (AppLog.BeginRun("run-log-1"))
            Check.Equal("run-log-1", AppLog.CurrentRunId, "本线程拿到自己的 runId");

        Check.True(AppLog.CurrentRunId == null, "释放后清掉");
        AppLog.SetSingleRunFallback("run-log-single");
        Check.Equal("run-log-single", AppLog.CurrentRunId, "单运行时给别处线程兜底");
        AppLog.SetSingleRunFallback(null);

        // 两个线程各自的 runId 不串。
        string? a = null, b = null;
        Task t1 = Task.Run(() => { using (AppLog.BeginRun("run-log-a")) { Thread.Sleep(60); a = AppLog.CurrentRunId; } });
        Task t2 = Task.Run(() => { using (AppLog.BeginRun("run-log-b")) { Thread.Sleep(60); b = AppLog.CurrentRunId; } });
        Task.WaitAll(t1, t2);
        Check.Equal("run-log-a", a, "线程 A 的 runId");
        Check.Equal("run-log-b", b, "线程 B 的 runId");
    }

    private static void Waiters()
    {
        KeyWaiter.Waiter? first = null, second = null, quiet = null;
        try
        {
            first = KeyWaiter.Arm("", swallow: true);      // 任意键，吞
            second = KeyWaiter.Arm("", swallow: true);     // 任意键，吞（排在后面）
            quiet = KeyWaiter.Arm("", swallow: false);     // 任意键，不吞

            // 先 arm 的先命中；同一次按键只给一个等待者。
            Check.True(KeyWaiter.HandleKey(0x41, isDown: true, isUp: false), "第一个等待者吞下按键");
            Check.True(first.Done, "第一个 Done");
            Check.False(second.Done, "第二个没拿到");
            Check.False(quiet.Done, "第三个也没拿到");

            Check.True(KeyWaiter.HandleKey(0x41, isDown: false, isUp: true), "配对的抬起被吞");
            Check.False(KeyWaiter.HandleKey(0x41, isDown: false, isUp: true), "抬起只吞一次");

            KeyWaiter.Disarm(first);
            KeyWaiter.Disarm(second);

            Check.False(KeyWaiter.HandleKey(0x42, isDown: true, isUp: false), "不吞的等待者放过按键");
            Check.True(quiet.Done, "但它自己算等到了");
        }
        finally
        {
            if (first != null) KeyWaiter.Disarm(first);
            if (second != null) KeyWaiter.Disarm(second);
            if (quiet != null) KeyWaiter.Disarm(quiet);
        }
    }
}
