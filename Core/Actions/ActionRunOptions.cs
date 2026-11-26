namespace LTools.Core.Actions;

/// <summary>一次运行的参数与预算。</summary>
public sealed class ActionRunOptions
{
    /// <summary>停止热键 / 外部取消。</summary>
    public CancellationToken Token { get; init; }

    /// <summary>面板唤起时记录的光标坐标（0 = 运行时取当前位置）。</summary>
    public int CursorX { get; init; }

    public int CursorY { get; init; }

    /// <summary>单次运行最多执行的步骤数（循环体里的步骤也算）。</summary>
    public int MaxSteps { get; init; } = 100_000;

    /// <summary>单个循环最多执行的轮次。</summary>
    public int MaxLoopIterations { get; init; } = 20_000;

    /// <summary>单次运行最长时长。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>诊断日志回调（不依赖 UI）。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// 每一步结束时的轨迹回调（成功 / 失败 / 失败继续 / 被停止）。在后台线程同步调用，
    /// **不要在这里做重活**；抛出的异常会被引擎吞掉，不影响动作本身。
    /// </summary>
    public Action<StepTrace>? StepTrace { get; init; }

    /// <summary>
    /// 只从这个步骤路径开始执行（<c>/steps/2</c>）；空 = 从头。编辑器「从选中行开始试运行」用。
    /// 只支持顶层步骤：路径指向分支内部时前面的步骤会被跳过，等于什么都不跑。
    /// </summary>
    public string? StartPath { get; init; }

    /// <summary>
    /// 每开始一步回调一次（步序号从 1 开始）。用于进度提示 / 运行指示器；
    /// 在后台线程上同步调用，**不要在这里做重活**。
    /// </summary>
    public Action<int>? StepStarted { get; init; }

    /// <summary>
    /// 全局子动作解析（id → 定义）；缺省从数据根 <c>subactions/</c> 读取。
    /// </summary>
    public Func<string, SubActionDef?>? SubActionResolver { get; init; }

    /// <summary>
    /// 运行开始前注入的初始变量（自动触发的事件输出 / <c>ActionParam</c>）。
    /// 只覆盖变量表里**已声明**的同名变量，按声明类型 Coerce；未声明的忽略并记一条诊断日志。
    /// 编辑器试运行不传，行为不变。见 <c>docs/AUTO_TRIGGER.md</c> §13.3。
    /// </summary>
    public IReadOnlyDictionary<string, Value>? InitialVariables { get; init; }

    /// <summary>
    /// 本次运行的资源根（草稿试运行时是草稿的 <c>files/</c> 目录）。
    /// 图片的相对路径先在它下面找，再走常规顺序（见 <c>ImageLibrary.Resolve</c>）；
    /// 正式运行为空，解析规则完全不变。
    /// </summary>
    public string? ResourceRoot { get; init; }

    /// <summary>
    /// 干跑：只解析参数 + 跑取值动作单元，**不注入键鼠、不弹窗口**。
    /// MCP <c>ltools_staging_run(dryRun: true)</c> 用，让 agent 敢试；哪些动作单元照跑由
    /// <see cref="UnitSpec.RunsInDryRun"/> 决定（流程控制 / 取值照跑，普通步骤跳过并记一条
    /// <see cref="StepTraceStatus.skipped"/> 轨迹）。
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// 单步运行超过这个时长就回调一次 <see cref="OnSlowStep"/>（默认 30 秒；≤ 0 = 关掉）。
    /// 只是**看得见**，不杀线程：真正卡在 UIA / OCR / 大文件里的调用只能等它自己返回。
    /// </summary>
    public TimeSpan SlowStepWarn { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 慢步骤回调：在计时器线程上触发，同一步（按步骤路径）每次运行只报一次。
    /// **不要在这里做重活**，抛出的异常会被引擎吞掉。
    /// </summary>
    public Action<SlowStepInfo>? OnSlowStep { get; init; }
}
