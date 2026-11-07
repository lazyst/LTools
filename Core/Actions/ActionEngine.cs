using System.Diagnostics;

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
    /// 动作内子动作（<see cref="ActionProgram.SubActions"/>）总是优先。
    /// </summary>
    public Func<string, SubActionDef?>? SubActionResolver { get; init; }
}

/// <summary>一次运行的结果。失败不抛异常，由调用方按 <see cref="Error"/> 处理。</summary>
public sealed class ActionRunResult
{
    public bool Completed { get; private init; }

    /// <summary>被用户停止 / <c>flow.stop</c> 正常结束。</summary>
    public bool Stopped { get; private init; }

    public string? Error { get; private init; }

    /// <summary>失败发生在哪一行（<c>第 3 行 › 否则 › 第 1 行</c>）；不是步骤失败时为 null。</summary>
    public string? FailedStepLabel { get; private init; }

    public Exception? Exception { get; private init; }

    public IReadOnlyDictionary<string, Value> Variables { get; private init; } =
        new Dictionary<string, Value>();

    public int Steps { get; private init; }

    public TimeSpan Elapsed { get; private init; }

    public bool Ok => Completed && !Stopped && Error == null;

    internal static ActionRunResult Success(
        IReadOnlyDictionary<string, Value> variables, int steps, TimeSpan elapsed, bool stopped = false) =>
        new()
        {
            Completed = true,
            Stopped = stopped,
            Variables = variables,
            Steps = steps,
            Elapsed = elapsed,
        };

    internal static ActionRunResult Failure(
        IReadOnlyDictionary<string, Value> variables, int steps, TimeSpan elapsed,
        string error, Exception? exception, bool stopped, string? failedStepLabel = null) =>
        new()
        {
            Completed = false,
            Stopped = stopped,
            Variables = variables,
            Steps = steps,
            Elapsed = elapsed,
            Error = error,
            Exception = exception,
            FailedStepLabel = failedStepLabel,
        };
}

/// <summary>
/// 动作执行引擎：按步骤顺序执行单元，处理作用域、子步骤递归、停止信号与失败策略。
/// </summary>
/// <remarks>
/// 引擎不碰 WPF，只在后台线程上跑；需要 UI 的单元（提示 / 输入框）自己投递到 UI 线程。
/// 变量只在本次运行内有效，不落盘。
/// </remarks>
public sealed class ActionEngine
{
    private readonly StepRegistry _registry;

    public ActionEngine(StepRegistry? registry = null) =>
        _registry = registry ?? StepRegistry.Default;

    public StepRegistry Registry => _registry;

    /// <summary>执行一个动作。失败返回 <see cref="ActionRunResult.Error"/>，不抛异常。</summary>
    public ActionRunResult Run(ActionProgram program, ActionRunOptions? options = null)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));

        var opt = options ?? new ActionRunOptions();
        var started = Stopwatch.StartNew();
        var root = new VariableScope();
        var state = new ActionRunState(opt, program);
        var ctx = new StepContext(this, state, root, new Step { Unit = "<root>" });

        try
        {
            ValidateProgram(program);
            SeedVariables(program.Variables, root);
            RunSteps(program.Steps, ctx);

            // 「从选中行开始」给了个对不上的路径：说清楚，别悄悄跑了个空动作。
            if (!string.IsNullOrEmpty(opt.StartPath) && !state.Started)
                throw new ActionException($"找不到起点步骤：{opt.StartPath}");

            return ActionRunResult.Success(root.Snapshot(), state.StepCount, started.Elapsed);
        }
        catch (StopSignal)
        {
            return ActionRunResult.Success(root.Snapshot(), state.StepCount, started.Elapsed, stopped: true);
        }
        catch (ActionAbortedException ex)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                ex.Message, ex, stopped: true);
        }
        catch (BreakSignal)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                "「跳出循环」只能出现在循环体里", null, stopped: false);
        }
        catch (ContinueSignal)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                "「继续下一次」只能出现在循环体里", null, stopped: false);
        }
        catch (StepFailureException ex)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                ex.Message, ex.InnerException ?? ex, stopped: false, state.FailedStepLabel);
        }
        catch (ActionException ex)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                ex.Message, ex, stopped: false, state.FailedStepLabel);
        }
        catch (Exception ex)
        {
            return ActionRunResult.Failure(root.Snapshot(), state.StepCount, started.Elapsed,
                ex.Message, ex, stopped: false, state.FailedStepLabel);
        }
    }

    /// <summary>执行一组步骤（循环体 / 分支体复用）。异常向调用方传播，由顶层统一收口。</summary>
    internal void RunSteps(IReadOnlyList<Step> steps, StepContext parent) =>
        RunSteps(steps, parent, parent.Path, parent.Label);

    /// <param name="pathPrefix">父级步骤路径（<c>/steps/2/then</c>）；顶层为空串。</param>
    /// <param name="labelPrefix">父级人类可读位置（<c>第 3 行 › 否则</c>）；顶层为空串。</param>
    internal void RunSteps(IReadOnlyList<Step> steps, StepContext parent, string pathPrefix, string labelPrefix)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Disabled) continue;

            string path = pathPrefix.Length == 0 ? $"/steps/{i}" : $"{pathPrefix}/{i}";
            if (parent.State.ShouldSkip(path)) continue;

            string label = labelPrefix.Length == 0 ? $"第 {i + 1} 行" : $"{labelPrefix} › 第 {i + 1} 行";

            parent.State.EnterStep();
            var unit = _registry.Get(step.Unit);
            var ctx = parent.ForStep(step);
            ctx.Path = path;
            ctx.Label = label;
            ctx.Depth = parent.Depth + 1;

            long started = Environment.TickCount64;
            try
            {
                var args = ResolveArgs(step, unit.Spec, ctx);
                unit.Execute(ctx, args);
            }
            catch (ActionAbortedException ex)
            {
                Trace(parent, step, path, label, ctx, StepTraceStatus.aborted, started, ex.Message);
                throw;
            }
            catch (StopSignal)
            {
                Trace(parent, step, path, label, ctx, StepTraceStatus.aborted, started, "已停止");
                throw;
            }
            catch (BreakSignal)
            {
                Trace(parent, step, path, label, ctx, StepTraceStatus.ok, started, null);
                throw;
            }
            catch (ContinueSignal)
            {
                Trace(parent, step, path, label, ctx, StepTraceStatus.ok, started, null);
                throw;
            }
            catch (Exception ex)
            {
                if (step.OnFail == FailMode.@continue)
                {
                    parent.State.RecordError(unit, ex);
                    parent.State.FailedStepLabel ??= label;
                    Trace(parent, step, path, label, ctx, StepTraceStatus.continued, started, ex.Message);
                    continue;
                }

                parent.State.FailedStepLabel ??= label;
                Trace(parent, step, path, label, ctx, StepTraceStatus.failed, started, ex.Message);

                // 内层步骤已经带了「第几行」前缀（例如循环体里那一步），外层别再套一层。
                if (ex is StepFailureException) throw;

                throw new StepFailureException(
                    $"{label}「{unit.Spec.Name}」失败：{ex.Message}", ex);
            }

            Trace(parent, step, path, label, ctx, StepTraceStatus.ok, started, null);

            if (step.DelayMs > 0 && !parent.State.Wait(step.DelayMs))
                throw new ActionAbortedException("已停止", userCancelled: true);
        }
    }

    /// <summary>发一条轨迹；<c>StepTrace</c> 回调抛异常时吞掉（调试信息不该毁掉动作）。</summary>
    private static void Trace(StepContext parent, Step step, string path, string label,
        StepContext ctx, StepTraceStatus status, long started, string? error)
    {
        var callback = parent.State.Options.StepTrace;
        if (callback == null) return;

        try
        {
            callback(new StepTrace(path, label, ctx.Depth, step.Unit,
                parent.Engine.Registry.Get(step.Unit).Spec.Name, status,
                Environment.TickCount64 - started, error, CaptureOutputs(step, ctx)));
        }
        catch
        {
            // 面板已经关掉 / 回调自己出错：不影响执行
        }
    }

    private static IReadOnlyDictionary<string, Value> CaptureOutputs(Step step, StepContext ctx)
    {
        if (step.Out.Count == 0) return EmptyOutputs;

        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in step.Out)
        {
            if (string.IsNullOrWhiteSpace(pair.Value)) continue;
            if (ctx.Scope.TryGet(pair.Value, out var value)) values[pair.Key] = value;
        }
        return values;
    }

    private static readonly IReadOnlyDictionary<string, Value> EmptyOutputs =
        new Dictionary<string, Value>();

    /// <summary>子动作递归深度上限。</summary>
    public const int MaxSubActionDepth = 8;

    /// <summary>按 id 找子动作：动作内（含嵌套）优先，其次全局库。</summary>
    internal SubActionDef? ResolveSubAction(ActionRunState state, string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return null;

        if (FindSubAction(state.Program.SubActions, id) is { } local) return local;

        var globals = state.Options.SubActionResolver ?? SubActionStore.Load;
        return globals(id);
    }

    /// <summary>调用一个子动作：新作用域、按输入变量名传值、输出按步骤的 out 映射写回。</summary>
    internal void RunSubAction(StepContext caller, string id, StepArgs args)
    {
        var def = ResolveSubAction(caller.State, id)
            ?? throw new ActionException($"找不到子动作：{id}");

        string label = string.IsNullOrWhiteSpace(def.Name) ? id : def.Name!;
        if (caller.State.SubActionDepth >= MaxSubActionDepth)
            throw new ActionException(
                $"子动作调用层数超过上限（{MaxSubActionDepth}）：{label}");

        var declared = new HashSet<string>(
            def.Variables.Select(v => v.Key?.Trim() ?? ""), StringComparer.OrdinalIgnoreCase);
        foreach (string key in args.Values.Keys)
        {
            if (string.Equals(key, "subAction", StringComparison.OrdinalIgnoreCase)) continue;
            if (!declared.Contains(key))
                throw new ActionException($"子动作「{label}」没有输入「{key}」");
        }

        caller.State.SubActionDepth++;
        try
        {
            var scope = new VariableScope();
            foreach (var variable in def.Variables)
            {
                string key = variable.Key?.Trim() ?? "";
                if (key.Length == 0) continue;
                scope.Set(key, args.Has(key) ? args.Get(key) : variable.Seed());
            }

            var ctx = new StepContext(this, caller.State, scope, new Step { Unit = "<subaction>" })
            {
                Path = $"{caller.Path}/sub/{id}",
                Label = caller.Label.Length == 0 ? $"子动作「{label}」" : $"{caller.Label} › 子动作「{label}」",
                Depth = caller.Depth + 1,
            };
            RunSteps(def.Steps, ctx);

            foreach (var variable in def.Variables)
            {
                if (variable.Direction != VariableDirection.output) continue;
                string key = variable.Key?.Trim() ?? "";
                if (key.Length == 0 || !scope.TryGetLocal(key, out var value) || value == null) continue;
                caller.SetOutput(key, value);
            }
        }
        catch (ActionAbortedException) { throw; }
        catch (StopSignal) { throw; }
        catch (BreakSignal) { throw; }
        catch (ContinueSignal) { throw; }
        catch (StepFailureException) { throw; }   // 里面已经带好位置（含「子动作「x」」）
        catch (ActionException ex)
        {
            throw new ActionException($"子动作「{label}」失败：{ex.Message}", ex);
        }
        finally
        {
            caller.State.SubActionDepth--;
        }
    }

    private static SubActionDef? FindSubAction(IEnumerable<SubActionDef> defs, string id)
    {
        foreach (var def in defs)
        {
            if (string.Equals((def.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase))
                return def;
            if (FindSubAction(def.SubActions, id) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>求值一个内联取值步骤（Pure 单元）。</summary>
    internal Value EvaluateInline(Step step, StepContext parent)
    {
        var unit = _registry.Get(step.Unit);
        if (unit.Spec.Kind != UnitKind.pure)
            throw new ActionException(
                $"「{unit.Spec.Name}」不是取值单元，不能内联在值的位置");

        var collected = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        var ctx = parent.ForStep(step, step.Out, (key, value) => collected[key] = value);
        var args = ResolveArgs(step, unit.Spec, ctx);

        parent.State.EnterStep();
        try
        {
            unit.Execute(ctx, args);
        }
        catch (Exception ex) when (ex is not ActionAbortedException)
        {
            throw new ActionException($"取值「{unit.Spec.Name}」失败：{ex.Message}", ex);
        }

        if (collected.TryGetValue("value", out var structured)) return structured;

        if (unit.Spec.Outputs.Count > 0 &&
            collected.TryGetValue(unit.Spec.Outputs[0].Key, out var first))
            return first;

        if (collected.Count > 0) return collected.Values.First();

        throw new ActionException($"取值单元「{unit.Spec.Name}」没有返回结果");
    }

    internal StepArgs ResolveArgs(Step step, UnitSpec spec, StepContext ctx)
    {
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in spec.Inputs)
        {
            Value value;
            if (step.In.TryGetValue(input.Key, out var arg))
            {
                provided.Add(input.Key);
                try { value = arg.Resolve(ctx, renderLiteral: !input.Raw); }
                catch (ActionException ex)
                {
                    throw new ActionException($"参数「{input.Label}」求值失败：{ex.Message}", ex);
                }
            }
            else if (input.Default != null)
            {
                provided.Add(input.Key);
                value = input.Default;
            }
            else if (input.Required)
            {
                throw new ActionException($"「{spec.Name}」缺少必填参数「{input.Label}」");
            }
            else
            {
                value = Value.Empty;
            }

            try { values[input.Key] = provided.Contains(input.Key) ? value.Coerce(input.Type) : Value.Empty; }
            catch (ActionException ex)
            {
                throw new ActionException($"参数「{input.Label}」类型不对：{ex.Message}", ex);
            }
        }

        foreach (var extra in step.In)
        {
            if (spec.Inputs.Any(input => string.Equals(input.Key, extra.Key, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (!spec.DynamicInputs)
                throw new ActionException($"「{spec.Name}」没有参数「{extra.Key}」（检查拼写）");

            // 动态参数（如子动作的输入变量）：保留原始类型，不做类型转换。
            provided.Add(extra.Key);
            try { values[extra.Key] = extra.Value.Resolve(ctx); }
            catch (ActionException ex)
            {
                throw new ActionException($"参数「{extra.Key}」求值失败：{ex.Message}", ex);
            }
        }

        return new StepArgs(values, provided);
    }

    private void ValidateProgram(ActionProgram program)
    {
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in program.Variables)
        {
            string key = variable.Key?.Trim() ?? "";
            if (key.Length == 0) throw new ActionException("变量声明缺少 key");
            if (!variables.Add(key))
                throw new ActionException($"变量「{key}」重复声明");
        }

        ValidateSteps(program.Steps);
    }

    private void ValidateSteps(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            var unit = _registry.Get(step.Unit);

            foreach (var arg in step.In.Values)
                if (arg.Kind == StepArgKind.inline)
                    ValidateInline(arg.InlineStep!);

            ValidateSteps(step.Body);
            ValidateSteps(step.Then);
            ValidateSteps(step.Else);
        }
    }

    private void ValidateInline(Step step)
    {
        var unit = _registry.Get(step.Unit);
        if (unit.Spec.Kind != UnitKind.pure)
            throw new ActionException(
                $"「{unit.Spec.Name}」不是取值单元，不能内联在值的位置");

        foreach (var arg in step.In.Values)
            if (arg.Kind == StepArgKind.inline)
                ValidateInline(arg.InlineStep!);
    }

    private static void SeedVariables(IEnumerable<VariableDef> variables, VariableScope scope)
    {
        foreach (var variable in variables)
            scope.Set(variable.Key, variable.Seed());
    }
}

/// <summary>运行状态：取消、步数 / 时间预算、错误记录。</summary>
internal sealed class ActionRunState
{
    private readonly ActionRunOptions _options;
    private readonly DateTime _deadline;
    private int _steps;
    private int _iterations;

    public ActionRunState(ActionRunOptions options, ActionProgram program)
    {
        _options = options;
        Program = program;
        _deadline = DateTime.UtcNow + options.MaxDuration;
    }

    /// <summary>本次运行的动作（子动作解析用）。</summary>
    public ActionProgram Program { get; }

    public ActionRunOptions Options => _options;

    /// <summary>当前子动作调用深度。</summary>
    public int SubActionDepth { get; set; }

    public CancellationToken Token => _options.Token;

    public int CursorX => _options.CursorX;

    public int CursorY => _options.CursorY;

    public int MaxLoopIterations => _options.MaxLoopIterations;

    public int StepCount => _steps;

    public string LastError { get; private set; } = "";

    /// <summary>最近一次失败所在的步骤位置（<c>第 3 行</c>），由 <see cref="ActionEngine"/> 写入。</summary>
    public string? FailedStepLabel { get; set; }

    /// <summary>「从选中行开始」：还没走到起点时返回 true（跳过这一步）。只认第一个命中的路径。</summary>
    public bool ShouldSkip(string path)
    {
        string? start = _options.StartPath;
        if (Started || string.IsNullOrEmpty(start)) return false;
        if (!string.Equals(path, start, StringComparison.Ordinal)) return true;
        Started = true;
        return false;
    }

    /// <summary>是否已经越过 <see cref="ActionRunOptions.StartPath"/>。</summary>
    public bool Started { get; private set; }

    public void EnterStep()
    {
        if (Token.IsCancellationRequested)
            throw new ActionAbortedException("已停止", userCancelled: true);

        _steps++;
        if (_steps > _options.MaxSteps)
            throw new ActionAbortedException($"执行步数超过上限（{_options.MaxSteps} 步），已自动停止");

        // 每 32 步检查一次时钟，避免热循环里频繁取时间。
        if ((_steps & 31) == 0 && DateTime.UtcNow > _deadline)
            throw new ActionAbortedException(
                $"执行时间超过上限（{_options.MaxDuration.TotalMinutes:0} 分钟），已自动停止");

        _options.StepStarted?.Invoke(_steps);
    }

    /// <summary>进入循环的一轮：只做取消与时间检查，不计步骤数。</summary>
    public void EnterIteration()
    {
        if (Token.IsCancellationRequested)
            throw new ActionAbortedException("已停止", userCancelled: true);

        if ((++_iterations & 31) == 0 && DateTime.UtcNow > _deadline)
            throw new ActionAbortedException(
                $"执行时间超过上限（{_options.MaxDuration.TotalMinutes:0} 分钟），已自动停止");
    }

    public bool Wait(int milliseconds)
    {
        if (milliseconds <= 0) return !Token.IsCancellationRequested;
        return !Token.WaitHandle.WaitOne(milliseconds);
    }

    public void RecordError(IStepUnit unit, Exception ex)
    {
        LastError = ex.Message;
        _options.Log?.Invoke($"「{unit.Spec.Name}」失败但继续：{ex.Message}");
    }
}

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

    /// <summary>本次运行最近一次被「失败继续」吞掉的错误文本。</summary>
    public string LastError => State.LastError;

    internal ActionRunState State { get; }

    /// <summary>写出一个输出；如果步骤没有把它映射到变量，则什么也不做（内联取值时收集起来）。</summary>
    public void SetOutput(string key, Value value) => _emit(key, value ?? Value.Empty);

    /// <summary>该输出是否映射到了变量（循环单元可用它避免无意义的赋值）。</summary>
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

/// <summary>
/// 步骤失败的内部信号：消息里已经带好「第 3 行 › 否则 › 第 1 行」前缀。
/// </summary>
/// <remarks>
/// 单独一个类型是为了让外层步骤区分「自己这一步失败」和「子步骤已经报过位置」——
/// 否则循环 / 分支会把前缀套成「第 2 行「如果」失败：第 2 行 › 满足时 › 第 1 行「提示弹窗」失败：…」。
/// 引擎对外的结果仍然把内层异常放在 <see cref="Exception.InnerException"/> 上。
/// </remarks>
internal sealed class StepFailureException : Exception
{
    public StepFailureException(string message, Exception inner) : base(message, inner) { }
}

