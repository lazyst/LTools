namespace LTools.Core.Actions;

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

    /// <summary>最近一次被「失败继续」吞掉的错误：位置、动作单元名、性质（<c>flow.lastErrorStep</c> 用）。</summary>
    public string LastErrorLabel { get; private set; } = "";

    public string LastErrorUnit { get; private set; } = "";

    public ActionErrorKind LastErrorKind { get; private set; } = ActionErrorKind.unknown;

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

    // ============ 慢步骤看门狗 ============

    private readonly object _slowLock = new();
    private readonly List<StepFrame> _slowFrames = new();
    private readonly HashSet<string> _slowReported = new(StringComparer.Ordinal);

    private readonly record struct StepFrame(string Path, string Label, string UnitKey, string UnitName, long Started);

    /// <summary>
    /// 进入一步（用于慢步骤看门狗）；嵌套调用（子动作 / 内联取值）只报最里面那一步。
    /// 没开看门狗时不做任何记录。
    /// </summary>
    public void PushStep(string path, string label, IStepUnit unit)
    {
        if (_options.OnSlowStep == null || _options.SlowStepWarn <= TimeSpan.Zero) return;

        lock (_slowLock)
            _slowFrames.Add(new StepFrame(path, label, unit.Spec.Key, unit.Spec.Name, Environment.TickCount64));
    }

    /// <summary>离开一步。</summary>
    public void PopStep()
    {
        lock (_slowLock)
        {
            if (_slowFrames.Count > 0) _slowFrames.RemoveAt(_slowFrames.Count - 1);
        }
    }

    /// <summary>起一个 1 秒计时器查「这一步是不是太久了」；没开看门狗时返回 null。</summary>
    public IDisposable? StartSlowWatch()
    {
        if (_options.OnSlowStep == null || _options.SlowStepWarn <= TimeSpan.Zero) return null;

        // 心跳 1 秒：阈值默认 30 秒，所以发现延迟最多 1 秒左右，不会因为「每 32 步才看一次钟」而漏掉。
        return new Timer(_ => CheckSlowStep(), null, dueTime: 1000, period: 1000);
    }

    private void CheckSlowStep()
    {
        SlowStepInfo info;
        lock (_slowLock)
        {
            if (_slowFrames.Count == 0) return;

            StepFrame frame = _slowFrames[^1];
            TimeSpan elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - frame.Started);
            if (elapsed < _options.SlowStepWarn) return;

            // 同一路径（循环里的同一行）每次运行只报一次，别 30 秒后每秒刷一条。
            if (!_slowReported.Add(frame.Path)) return;

            info = new SlowStepInfo(frame.Path, frame.Label, frame.UnitKey, frame.UnitName, elapsed);
        }

        try { _options.OnSlowStep?.Invoke(info); }
        catch { /* 提示失败不该毁掉动作 */ }
    }

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

    public void RecordError(IStepUnit unit, Exception ex, string label = "")
    {
        LastError = ex.Message;
        LastErrorLabel = label;
        LastErrorUnit = unit.Spec.Name;
        LastErrorKind = ActionErrors.KindOf(ex);
        _options.Log?.Invoke($"「{unit.Spec.Name}」失败但继续：{ex.Message}");
    }
}
