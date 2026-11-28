using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Running;
using LTools.Features;
using LTools.Features.Running;

namespace RunConcurrencySmoke;

/// <summary>
/// 运行内核的端到端冒烟：真 <see cref="RunCoordinator"/> + 真专用线程 + 假动作（<c>flow.delay</c>）。
/// 不注入键鼠、不弹窗口（钩子行为见 <c>docs/RUN_INTEROP.md</c> §13 的真机清单）。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    /// <summary>每次跑一个短标记，避免和上一次 / 真实动作库里的同名记录混在一起。</summary>
    private static readonly string Tag = Guid.NewGuid().ToString("N")[..6];

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        ActionRunConfig original = AppState.ActionRun;
        try
        {
            Defaults();
            Exclusive();
            ConcurrentDifferent();
            ConcurrentSameAction();
            QueueAndDedupe();
            CancelOne();
            TrialRun();
            StopAllClearsQueue();
            History();
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine($"FAIL 冒烟本身异常：{ex}");
        }
        finally
        {
            RunCoordinator.CancelAll();
            WaitFor(() => RunningActions.Count == 0, 4000);
            AppState.ActionRun = original;
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 各段 ============

    private static void Defaults()
    {
        Check("默认档是「并发运行」", ActionRunConfig.Default.Mode == ActionRunMode.concurrent);
        Check("默认并发数 = 上限 32", ActionRunConfig.Default.MaxConcurrent == RunLimits.MaxConcurrentCeiling);
        Check("默认队列 8", ActionRunConfig.Default.MaxQueued == RunLimits.DefaultMaxQueued);
        Check("没有运行时托盘提示就是 LTools", TrayMenu.TooltipText() == "LTools");
    }

    private static void Exclusive()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.exclusive);

        ActionDto a = Make("独占A", 600);
        ActionDto b = Make("独占B", 100);

        Check("独占：第一个开跑", Submit(a) == RunSubmission.Started);
        Check("独占：它在跑", WaitFor(() => RunningActions.Count == 1));
        Check("独占：第二个被拒", Submit(b) == RunSubmission.Rejected);
        Check("独占：仍然只有一个", RunningActions.Count == 1);

        RunCoordinator.CancelAll();
        Check("独占：停止后清空", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void ConcurrentDifferent()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 4, MaxQueued: 4);

        ActionDto a = Make("并发A", 800);
        ActionDto b = Make("并发B", 800);

        Check("并发：两个都开跑",
            Submit(a) == RunSubmission.Started && Submit(b) == RunSubmission.Started);
        Check("并发：两份同时在跑", WaitFor(() => RunningActions.Count == 2));

        var snapshot = RunCoordinator.Snapshot();
        Check("并发：runId 各是各的", snapshot.Select(x => x.RunId).Distinct().Count() == 2);
        Check("并发：动作各是各的", snapshot.Select(x => x.ActionId).Distinct().Count() == 2);
        Check("并发：托盘提示带个数", TrayMenu.TooltipText().Contains("2 个动作", StringComparison.Ordinal),
            TrayMenu.TooltipText());

        RunCoordinator.CancelAll();
        Check("并发：全停后清空", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void ConcurrentSameAction()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 4, MaxQueued: 4);

        ActionDto x = Make("同动作", 800);
        Check("同动作：第一份开跑", Submit(x) == RunSubmission.Started);
        Check("同动作：第二份也开跑", Submit(x) == RunSubmission.Started);
        Check("同动作：两份并存",
            WaitFor(() => RunningActions.Count == 2 && RunningActions.CountOf(x.Id) == 2));

        var labels = RunCoordinator.Snapshot()
            .Where(s => s.ActionId == x.Id)
            .Select(s => s.Label)
            .ToList();
        Check("同动作：第二份标 #2", labels.Any(l => l.EndsWith("#2", StringComparison.Ordinal)),
            string.Join(" / ", labels));

        RunCoordinator.CancelAll();
        Check("同动作：全停后清空", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void QueueAndDedupe()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.queue, MaxConcurrent: 1, MaxQueued: 4);

        ActionDto a = Make("排队A", 700);
        ActionDto b = Make("排队B", 150);

        Check("排队：A 开跑", Submit(a) == RunSubmission.Started);
        Check("排队：B 入队", Submit(b) == RunSubmission.Queued);
        Check("排队：队列里有 1 条", WaitFor(() => RunQueue.Count == 1));

        Check("排队：B 再按不重复入队",
            Submit(b) == RunSubmission.Queued && RunQueue.Count == 1);

        Check("排队：A 结束后 B 自动跑完",
            WaitFor(() => RunningActions.Count == 0 && RunQueue.Count == 0, 6000));
    }

    private static void CancelOne()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.concurrent, MaxConcurrent: 4, MaxQueued: 4);

        ActionDto a = Make("停A", 1500);
        ActionDto b = Make("停B", 1500);

        Submit(a);
        Submit(b);
        Check("停一个：两份都在跑", WaitFor(() => RunningActions.Count == 2));

        ActionRunSession? first = RunCoordinator.Snapshot().FirstOrDefault(s => s.ActionId == a.Id);
        Check("停一个：找得到 A", first != null);
        if (first != null) RunCoordinator.Cancel(first.RunId);

        Check("停一个：A 退出、B 还在",
            WaitFor(() => RunningActions.Count == 1
                          && RunCoordinator.Snapshot()[0].ActionId == b.Id, 3000));

        RunCoordinator.CancelAll();
        Check("停一个：最后全停", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void TrialRun()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.queue, MaxConcurrent: 1, MaxQueued: 4);

        ActionDto a = Make("试运行占用", 800);
        Submit(a);
        WaitFor(() => RunningActions.Count == 1);

        bool busy = AutomationRunner.TryRunProgram("试运行忙碌", FakeProgram(100), null, null, null);
        Check("试运行：忙碌直接拒", !busy);

        RunCoordinator.CancelAll();
        WaitFor(() => RunningActions.Count == 0, 3000);

        bool idle = AutomationRunner.TryRunProgram("试运行空闲", FakeProgram(100), null, null, null);
        Check("试运行：空闲能跑", idle);
        Check("试运行：跑完", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void StopAllClearsQueue()
    {
        AppState.ActionRun = new ActionRunConfig(ActionRunMode.queue, MaxConcurrent: 1, MaxQueued: 4);

        ActionDto a = Make("全停A", 1500);
        ActionDto b = Make("全停B", 300);

        Submit(a);
        Submit(b);
        Check("全停：B 已排队", WaitFor(() => RunQueue.Count == 1));

        int cancelled = RunCoordinator.CancelAll();
        Check("全停：停掉当前这个", cancelled >= 1);
        Check("全停：队列被清空", RunQueue.Count == 0);
        Check("全停：当前也退出", WaitFor(() => RunningActions.Count == 0, 3000));
    }

    private static void History()
    {
        var rows = RunHistory.Load()
            .Where(r => r.Action.Contains(Tag, StringComparison.Ordinal))
            .ToList();

        Check("运行历史：每次运行记一行", rows.Count >= 8, $"共 {rows.Count} 行");
        Check("运行历史：同名副本能区分", rows.Any(r => r.Action.Contains("#2", StringComparison.Ordinal)));
        Check("运行历史：来源都是面板", rows.All(r => r.Source == "panel"),
            string.Join(",", rows.Select(r => r.Source).Distinct()));
    }

    // ============ 工具 ============

    private static ActionDto Make(string label, int delayMs)
    {
        var dto = new ActionDto
        {
            Id = $"smoke-{Tag}-{label}",
            Name = $"{label}-{Tag}",
        };

        ActionProgram program = FakeProgram(delayMs);
        dto.Program = program;

        ActionRegistry.Register(dto);
        return dto;
    }

    private static ActionProgram FakeProgram(int delayMs)
    {
        var program = new ActionProgram { Name = "smoke-" + Tag };
        var step = new Step { Unit = "flow.delay" };
        step.In["ms"] = StepArg.FromNumber(delayMs);
        program.Steps.Add(step);
        return program;
    }

    private static RunSubmission Submit(ActionDto action) =>
        RunCoordinator.SubmitAction(action, 0, 0, null, RunSource.Panel);

    private static bool WaitFor(Func<bool> condition, int timeoutMs = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"ok   {what}");
            return;
        }

        _fail++;
        Console.WriteLine($"FAIL {what}{(detail == null ? "" : " · " + detail)}");
    }
}
