using System;

namespace LTools.Core.Running;

/// <summary>
/// 「动作运行时」的四档（`configs/settings.json` 的 `ActionRun.Mode`）：
/// 独占（旧行为）/ 互操作 / 排队 / 并发。见 <c>docs/RUN_INTEROP.md</c>。
/// </summary>
public enum ActionRunMode
{
    /// <summary>运行中整段让路（今天的行为），第二个动作直接拒。</summary>
    exclusive,

    /// <summary>运行中照常使用 LTools；第二个动作拒（有提示）。</summary>
    interop,

    /// <summary>运行中照常使用；第二个动作排队，前一个结束自动开始。</summary>
    queue,

    /// <summary>最多 <see cref="RunLimits.MaxConcurrentCeiling"/> 个同时跑（同一动作也可以），满了排队。</summary>
    concurrent,
}

/// <summary>一次提交的结局（<c>ActionExecutor.RunRef</c> 返回值 / 钩子据此决定提示）。</summary>
public enum RunSubmission
{
    /// <summary>是内置功能，已交给 <c>FeatureRunner</c> 立刻执行（从不排队）。</summary>
    Feature,

    /// <summary>已开始跑（新线程）。</summary>
    Started,

    /// <summary>已排进运行队列。</summary>
    Queued,

    /// <summary>拒绝了（已有运行 / 队列已满 / 同一动作已在队列里）。</summary>
    Rejected,

    /// <summary>引用找不到（动作已删 / 拼错）。</summary>
    NotFound,
}

/// <summary>
/// 运行期的结构性上限：**唯一一处数字**。改这里的常量不需要动注册表 / 队列 / 输入闸。
/// </summary>
/// <remarks>
/// 32 = 32 条专用线程 + 每运行最多 5000 条轨迹的最坏情况仍在几十 MB 量级；注入被
/// <c>InputGate</c> 串行化之后，再多并发也不会让「输入型」动作更快，只有等待型动作受益。
/// </remarks>
public static class RunLimits
{
    /// <summary>同时运行的数量上限（含同一动作的多份）。</summary>
    public const int MaxConcurrentCeiling = 32;

    /// <summary>默认同时运行数：等于上限（= 不人为限制，每次运行都要人显式触发）。</summary>
    public const int DefaultMaxConcurrent = MaxConcurrentCeiling;

    /// <summary>队列上限。</summary>
    public const int MaxQueuedCeiling = 32;

    /// <summary>默认队列上限。</summary>
    public const int DefaultMaxQueued = 8;

    /// <summary>每次运行在内存里最多收多少条轨迹（失败报告用，沿用旧值）。</summary>
    public const int MaxTracesPerRun = 5000;
}

/// <summary>
/// 运行期配置（运行期缓存是 <see cref="AppState.ActionRun"/>；落盘在 B2 接 settings 域）。
/// </summary>
public sealed record ActionRunConfig(
    ActionRunMode Mode = ActionRunMode.concurrent,
    int MaxConcurrent = RunLimits.DefaultMaxConcurrent,
    int MaxQueued = RunLimits.DefaultMaxQueued)
{
    /// <summary>
    /// 出厂默认：最终形态「并发运行」（同一动作也能多份）。要回到旧行为把 <c>Mode</c> 调成
    /// <see cref="ActionRunMode.exclusive"/> 即可——这是发布开关，不是结构变化。
    /// </summary>
    public static ActionRunConfig Default { get; } = new();

    /// <summary>夹到合法范围；越界不报错。</summary>
    public ActionRunConfig Normalized() => new(
        Mode,
        Math.Clamp(MaxConcurrent, 1, RunLimits.MaxConcurrentCeiling),
        Math.Clamp(MaxQueued, 0, RunLimits.MaxQueuedCeiling));

    /// <summary>这一档是否允许多个同时跑。</summary>
    public bool AllowsConcurrency => Mode == ActionRunMode.concurrent;

    /// <summary>这一档是否允许排队。</summary>
    public bool AllowsQueue => Mode is ActionRunMode.queue or ActionRunMode.concurrent;

    /// <summary>实际生效的运行上限（非并发档一律 1）。</summary>
    public int EffectiveMaxConcurrent => AllowsConcurrency ? Normalized().MaxConcurrent : 1;

    /// <summary>实际生效的队列上限（不支持排队的档一律 0）。</summary>
    public int EffectiveMaxQueued => AllowsQueue ? Normalized().MaxQueued : 0;
}
