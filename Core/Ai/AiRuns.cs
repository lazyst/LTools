using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Ai;

/// <summary>一次 AI 试运行的轨迹与结果（内存表，进程重启即清空）。</summary>
internal sealed class AiRun
{
    public required string Id { get; init; }

    public required string DraftId { get; init; }

    public required string Name { get; init; }

    public DateTime StartedAt { get; init; } = DateTime.Now;

    /// <summary>running / ok / failed / stopped。</summary>
    public string State { get; set; } = "running";

    public bool Finished { get; set; }

    public string? Error { get; set; }

    public int Steps { get; set; }

    public double ElapsedMs { get; set; }

    public List<StepTrace> Traces { get; } = new();
}

/// <summary>
/// AI 试运行登记表：<c>ltools_staging_run</c> 起跑，<c>ltools_run_output</c> 按 cursor 分页读轨迹。
/// </summary>
/// <remarks>
/// 与编辑器试运行共用 <see cref="AutomationRunner.TryRunProgram"/> 的互斥：已有自动化在跑时返回 busy，
/// 不排队（用户可能正在用键鼠）。轨迹回调在后台线程同步到达，这里只做入队，不做重活。
/// </remarks>
internal static class AiRuns
{
    private const int MaxRuns = 20;
    private const int MaxTraces = 5000;

    private static readonly object Gate = new();
    private static readonly List<AiRun> Runs = new();

    /// <summary>启动一次试运行；返回 null 表示已有自动化在运行。</summary>
    /// <param name="resourceRoot">
    /// 草稿的资源根（<c>staging/&lt;id&gt;/files</c>）：动作里的 <c>files/x.png</c> 先在它下面找，
    /// 所以 MCP / 面板试运行不用把路径改成绝对路径。
    /// </param>
    /// <param name="notify">
    /// 跑完给个结果提示（面板里点「试运行」时用）。MCP 的 agent 自己会读 <c>ltools_run_output</c>，
    /// 不传这个参数，免得 agent 每次试运行都给用户弹提示。
    /// </param>
    public static AiRun? Start(string draftId, string name, ActionProgram program, string? startPath,
                               string? resourceRoot = null, bool notify = false)
    {
        var run = new AiRun { Id = NewId(), DraftId = draftId, Name = name };

        bool started = AutomationRunner.TryRunProgram(
            name,
            program,
            startPath,
            trace =>
            {
                lock (Gate)
                {
                    if (run.Traces.Count >= MaxTraces) return;
                    run.Traces.Add(trace);
                }
            },
            result =>
            {
                lock (Gate)
                {
                    run.Finished = true;
                    run.State = result.Stopped ? "stopped" : result.Ok ? "ok" : "failed";
                    run.Error = result.Error;
                    run.Steps = result.Steps;
                    run.ElapsedMs = result.Elapsed.TotalMilliseconds;
                }

                if (notify) NotifyResult(run);
            },
            resourceRoot);

        if (!started) return null;

        lock (Gate)
        {
            Runs.Insert(0, run);
            foreach (var old in Runs.Where(r => r.Finished).Skip(MaxRuns).ToList())
                Runs.Remove(old);
        }

        AppLog.Info("Ai", $"AI 试运行 {run.Id}：{name}（草稿 {draftId}）");
        return run;
    }

    /// <summary>试运行结束的结果提示（面板里点「试运行」才有；与普通动作的运行提示分开）。</summary>
    private static void NotifyResult(AiRun run)
    {
        if (run.State == "ok")
            ToastService.Show($"试运行完成 · {run.Steps} 步 · {run.ElapsedMs:0} ms", run.Name, 2500, ToastKind.success);
        else if (run.State == "stopped")
            ToastService.Show("试运行已停止", run.Name, 2500, ToastKind.info);
        else
            ToastService.Show(run.Error ?? "试运行失败", run.Name, ToastService.StickyMs, ToastKind.error,
                key: "ai-run:" + run.DraftId);
    }

    public static AiRun? Find(string id)
    {
        lock (Gate)
        {
            return Runs.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>读轨迹：<paramref name="cursor"/> 是上次回执的 next（第几条）。</summary>
    public static (AiRun? Run, IReadOnlyList<(int Seq, StepTrace Trace)> Items, int Next, bool HasMore) Read(
        string id, int cursor, int limit = 200)
    {
        lock (Gate)
        {
            var run = Runs.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (run == null) return (null, Array.Empty<(int, StepTrace)>(), 0, false);

            limit = Math.Clamp(limit, 1, 500);
            cursor = Math.Max(0, cursor);
            int end = Math.Min(run.Traces.Count, cursor + limit);
            var items = new List<(int, StepTrace)>(Math.Max(0, end - cursor));
            for (int i = cursor; i < end; i++) items.Add((i + 1, run.Traces[i]));

            return (run, items, end, end < run.Traces.Count);
        }
    }

    /// <summary>停止这次运行（只允许停当前正在跑的那次）。</summary>
    public static bool Stop(string id)
    {
        var run = Find(id);
        if (run == null || run.Finished) return false;
        AutomationRunner.Stop();
        return true;
    }

    private static string NewId()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return "run_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
