namespace LTools.Core.Actions;

/// <summary>执行一个步骤时的上下文。</summary>
public sealed class StepContext
{
    private readonly IReadOnlyDictionary<string, string> _outputMap;
    private readonly Action<string, Value> _emit;

    internal StepContext(
        ActionEngine engine,
        ActionRunState state,
        VariableScope scope,
        Step step,
        IReadOnlyDictionary<string, string>? outputMap = null,
        Action<string, Value>? emit = null)
    {
        Engine = engine;
        State = state;
        Scope = scope;
        Step = step;
        _outputMap = outputMap ?? new Dictionary<string, string>();
        _emit = emit ?? ((_, _) => { });
    }

    public ActionEngine Engine { get; }

    public VariableScope Scope { get; }

    public Step Step { get; }

    /// <summary>步骤路径（<c>/steps/2/then/0</c>）：运行轨迹与编辑器定位用，与运行时步数无关。</summary>
    public string Path { get; internal set; } = "";

    /// <summary>给人看的步骤位置（<c>第 3 行 › 否则 › 第 1 行</c>）。</summary>
    public string Label { get; internal set; } = "";

    /// <summary>步骤嵌套深度（顶层为 1）。</summary>
    public int Depth { get; internal set; }

    public IReadOnlyDictionary<string, string> OutputMap => _outputMap;

    public CancellationToken Token => State.Token;

    public int CursorX => State.CursorX;

    public int CursorY => State.CursorY;

    public int MaxLoopIterations => State.MaxLoopIterations;

    /// <summary>本次运行的资源根（草稿的 <c>files/</c>）；正式运行为空。</summary>
    public string? ResourceRoot => State.Options.ResourceRoot;

    /// <summary>干跑（不注入键鼠、不弹窗口），见 <see cref="ActionRunOptions.DryRun"/>。</summary>
    public bool DryRun => State.Options.DryRun;

    /// <summary>截图前收不收 LTools 自己的窗口（只有编辑器试运行才收）。</summary>
    public bool HideOwnWindows => State.Options.HideOwnWindows;

    /// <summary>本次运行最近一次被「失败继续」吞掉的错误文本。</summary>
    public string LastError => State.LastError;

    /// <summary>最近一次被吞掉的错误发生在哪一行（<c>第 3 行</c>）；没有错误时为空。</summary>
    public string LastErrorStep => State.LastErrorLabel;

    /// <summary>最近一次被吞掉的错误是哪个动作单元（名字）。</summary>
    public string LastErrorStepUnit => State.LastErrorUnit;

    /// <summary>最近一次被吞掉的错误属于哪一类。</summary>
    public ActionErrorKind LastErrorKind => State.LastErrorKind;

    internal ActionRunState State { get; }

    /// <summary>写出一个输出；如果步骤没有把它映射到变量，则什么也不做（内联取值时收集起来）。</summary>
    public void SetOutput(string key, Value value) => _emit(key, value ?? Value.Empty);

    /// <summary>该输出是否映射到了变量（循环动作单元可用它避免无意义的赋值）。</summary>
    public bool HasOutput(string key) => _outputMap.ContainsKey(key);

    /// <summary>执行当前步骤的子步骤（循环体 / 分支体）。子步骤的路径挂在当前步骤下面。</summary>
    public void RunBody(IReadOnlyList<Step> steps)
    {
        string branch = ReferenceEquals(steps, Step.Body) ? "body"
            : ReferenceEquals(steps, Step.Then) ? "then"
            : ReferenceEquals(steps, Step.Else) ? "else"
            : "body";

        string branchLabel = branch switch
        {
            "then" => "满足时",
            "else" => "否则",
            _ => "循环体",
        };

        Engine.RunSteps(steps, this,
            $"{Path}/{branch}",
            Label.Length == 0 ? branchLabel : $"{Label} › {branchLabel}");
    }

    /// <summary>
    /// 跑一轮循环体：「继续下一次」/「跳出循环」在这里收掉（不再往上冒）。
    /// </summary>
    /// <remarks>
    /// 只管「跑一轮」，不管间隔：两轮之间要歇多久是循环类动作单元自己的
    /// 「每轮间隔」参数（<c>intervalMs</c>），由调用方在下一轮开始前调
    /// <see cref="WaitLoopInterval"/>。步骤选项里的「执行后等待」始终只属于这一步本身，
    /// 循环步骤上填的仍然是整段循环跑完后等一次。
    /// </remarks>
    /// <returns>false = 这一轮遇到「跳出循环」，调用方应结束循环。</returns>
    public bool RunLoopIteration()
    {
        bool stop = false;
        try { RunBody(Step.Body); }
        catch (ContinueSignal) { }
        catch (BreakSignal) { stop = true; }
        return !stop;
    }

    /// <summary>
    /// 循环的「每轮间隔」：两轮之间等一会儿（0 = 不等）。
    /// 只在确定还有下一轮时才调，所以最后一轮跑完（以及本轮「跳出循环」之后）不会多等一次。
    /// </summary>
    public void WaitLoopInterval(int milliseconds)
    {
        if (DryRun || milliseconds <= 0) return;
        if (!Wait(milliseconds))
            throw new ActionAbortedException("已停止", userCancelled: true);
    }

    /// <summary>调用一个子动作（输入按变量名从 <paramref name="args"/> 取，输出按步骤 out 映射写回）。</summary>
    public void RunSubAction(string id, StepArgs args) => Engine.RunSubAction(this, id, args);

    /// <summary>求值一个内联取值步骤。</summary>
    public Value EvaluateInline(Step step) => Engine.EvaluateInline(step, this);

    /// <summary>重新求值当前步骤的参数（条件循环每轮都要重新取值）。</summary>
    internal StepArgs ResolveArgs() =>
        Engine.ResolveArgs(Step, Engine.Registry.Get(Step.Unit).Spec, this);

    /// <summary>可取消等待；返回 false 表示被用户停止。</summary>
    public bool Wait(int milliseconds) => State.Wait(milliseconds);

    /// <summary>进入循环一轮（取消 / 时间检查）。</summary>
    public void EnterIteration() => State.EnterIteration();

    internal StepContext ForStep(
        Step step,
        IReadOnlyDictionary<string, string>? outputMap = null,
        Action<string, Value>? emit = null)
    {
        var map = outputMap ?? step.Out;
        return new StepContext(
            Engine,
            State,
            Scope,
            step,
            map,
            emit ?? ((key, value) =>
            {
                if (map.TryGetValue(key, out string? variable) && !string.IsNullOrWhiteSpace(variable))
                    Scope.Set(variable, value);
            }));
    }
}
