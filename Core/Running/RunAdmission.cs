namespace LTools.Core.Running;

/// <summary>准入的结果。</summary>
internal enum AdmissionOutcome
{
    /// <summary>立刻开一份新的运行。</summary>
    Start,

    /// <summary>进运行队列，等前一个结束。</summary>
    Queued,

    /// <summary>队列里已经有一条同样的动作（别越按越多）。</summary>
    RefuseQueued,

    /// <summary>没有位置：档位不允许 / 队列满了。</summary>
    RefuseFull,
}

/// <summary>
/// 一次提交「开跑 / 排队 / 拒」的**唯一判定**（纯函数，<c>--selftest</c> 直接断言）。
/// </summary>
/// <remarks>
/// <para>顺序就是优先级：试运行与自动触发不排队 → 空闲直开 → 并发档未满直开（同一动作也算）
/// → 队列里已有同名就拒 → 队列未满就排 → 否则拒。</para>
/// <para>**不看是不是同一个动作**：同一动作允许并发多份（见 <c>docs/RUN_INTEROP.md</c> §16）。
/// 「队列里同名只留一条」只约束还在排队的那一条。</para>
/// </remarks>
internal static class RunAdmission
{
    public static AdmissionOutcome Decide(
        ActionRunConfig config, int runningCount, int queuedCount,
        bool sameActionQueued, RunSource source)
    {
        ActionRunConfig cfg = config.Normalized();

        // 调试态（编辑器 / MCP / 子动作试运行）忙时直接拒：不挤掉用户正在跑的动作，也不排队。
        if (source is RunSource.Editor or RunSource.Mcp or RunSource.SubAction)
            return runningCount > 0 ? AdmissionOutcome.RefuseFull : AdmissionOutcome.Start;

        // 自动触发不排队：后台联动连环触发只会更难排查（见 docs/AUTO_TRIGGER.md）。
        if (source == RunSource.Trigger && runningCount > 0)
            return AdmissionOutcome.RefuseFull;

        if (runningCount == 0) return AdmissionOutcome.Start;

        if (cfg.AllowsConcurrency && runningCount < cfg.EffectiveMaxConcurrent)
            return AdmissionOutcome.Start;

        if (cfg.EffectiveMaxQueued > 0 && sameActionQueued) return AdmissionOutcome.RefuseQueued;

        if (cfg.EffectiveMaxQueued > 0 && queuedCount < cfg.EffectiveMaxQueued)
            return AdmissionOutcome.Queued;

        return AdmissionOutcome.RefuseFull;
    }
}
