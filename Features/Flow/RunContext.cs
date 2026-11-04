using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using LTools.Native;

namespace LTools.Features;

/// <summary>流程执行中的用户可读错误（配置问题 / 探测失败等）。</summary>
internal sealed class FlowException : Exception
{
    public FlowException(string message) : base(message) { }
    public FlowException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>中止整个流程（用户停止 / 步数或时间超预算）。不写崩溃日志。</summary>
internal sealed class FlowAbortException : Exception
{
    public FlowAbortException(string reason, bool userCancelled = false) : base(reason)
        => UserCancelled = userCancelled;

    /// <summary>是否由用户按停止热键触发。</summary>
    public bool UserCancelled { get; }
}

/// <summary>
/// 一次自动化运行的上下文：变量、取消、步数 / 时间预算、循环下标。
/// </summary>
/// <remarks>
/// 变量按运行隔离、不落盘。模板（<c>${name}</c>）与内置值（选中文本 / 剪贴板 / 光标等）
/// 都在此处解析；未定义变量直接抛 <see cref="FlowException"/>，避免静默空串造成误操作。
/// </remarks>
internal sealed class RunContext
{
    /// <summary>单次运行最多执行的节点数（防 while/goto 型失控）。</summary>
    public const int MaxSteps = 20_000;

    /// <summary>单次运行最长时长。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    private readonly CancellationToken _token;
    private readonly DateTime _deadline;
    private readonly Stack<int> _loopIndexes = new();

    public RunContext(string runName, CancellationToken token, int cursorX, int cursorY)
    {
        RunName = runName;
        _token = token;
        CursorX = cursorX;
        CursorY = cursorY;
        _deadline = DateTime.UtcNow + MaxDuration;
    }

    /// <summary>动作名（日志 / <c>${run.name}</c>）。</summary>
    public string RunName { get; }

    /// <summary>面板唤起时记录的光标坐标；0 表示运行时取当前位置。</summary>
    public int CursorX { get; }
    public int CursorY { get; }

    /// <summary>变量表（大小写不敏感）。</summary>
    public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已执行的节点数。</summary>
    public int StepCount { get; private set; }

    /// <summary>最近一次被吞掉的异常文本（<c>${lastError}</c>）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>当前循环下标（0-based；不在循环中为 null）。</summary>
    public int? LoopIndex => _loopIndexes.Count > 0 ? _loopIndexes.Peek() : null;

    /// <summary>是否有取消请求。</summary>
    public bool IsCancelled => _token.IsCancellationRequested;

    /// <summary>压入循环下标；调用方负责在 finally 中 <see cref="PopLoop"/>。</summary>
    public void PushLoop(int index) => _loopIndexes.Push(index);

    /// <summary>弹出循环下标。</summary>
    public void PopLoop()
    {
        if (_loopIndexes.Count > 0) _loopIndexes.Pop();
    }

    /// <summary>记录被 OnFail=继续 吞掉的异常。</summary>
    public void SetError(Exception ex) => LastError = ex.Message;

    /// <summary>进入一个节点：取消检查 + 步数 / 时间预算。</summary>
    public void EnterStep()
    {
        if (_token.IsCancellationRequested) throw new FlowAbortException("已停止", userCancelled: true);

        StepCount++;
        if (StepCount > MaxSteps)
            throw new FlowAbortException($"执行步数超过上限（{MaxSteps} 步），已自动停止");

        // 每 32 步检查一次时钟，避免热循环里频繁取时间。
        if ((StepCount & 31) == 0 && DateTime.UtcNow > _deadline)
            throw new FlowAbortException($"执行时间超过上限（{MaxDuration.TotalMinutes:0} 分钟），已自动停止");
    }

    /// <summary>可取消等待；返回 false 表示被用户停止。</summary>
    public bool Wait(int milliseconds)
    {
        if (milliseconds <= 0) return !_token.IsCancellationRequested;
        if (_token.WaitHandle.WaitOne(milliseconds)) return false;
        return true;
    }

    /// <summary>当前光标坐标（面板唤起时的坐标优先）。</summary>
    public (int x, int y) CurrentCursor()
    {
        if (CursorX != 0 || CursorY != 0) return (CursorX, CursorY);
        return Win32.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);
    }

    /// <summary>解析 <c>${...}</c> 里的路径；未定义抛 <see cref="FlowException"/>。</summary>
    public VarValue ResolvePath(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) throw new FlowException("模板里的变量名为空");

        // 日期 / 时间带格式：${date:yyyy-MM-dd}
        int colon = path.IndexOf(':');
        if (colon > 0)
        {
            string head = path[..colon];
            string format = path[(colon + 1)..];
            if (head.Equals("date", StringComparison.OrdinalIgnoreCase))
                return VarValue.FromText(DateTime.Now.ToString(format, CultureInfo.InvariantCulture));
            if (head.Equals("time", StringComparison.OrdinalIgnoreCase))
                return VarValue.FromText(DateTime.Now.ToString(format, CultureInfo.InvariantCulture));
        }

        // 用户变量（支持 m.x 这类属性访问）
        if (Vars.TryGetValue(path, out var exact)) return exact;
        int dot = path.IndexOf('.');
        if (dot > 0)
        {
            string baseName = path[..dot];
            string prop = path[(dot + 1)..];
            if (Vars.TryGetValue(baseName, out var v))
            {
                string? value = v.Property(prop);
                if (value == null) throw new FlowException($"变量「{baseName}」没有属性「{prop}」");
                return VarValue.FromText(value);
            }
        }

        switch (path.ToLowerInvariant())
        {
            case "cursor":
            {
                var (x, y) = CurrentCursor();
                return VarValue.FromPoint(x, y);
            }
            case "cursor.x":
                return VarValue.FromText(CurrentCursor().x.ToString(CultureInfo.InvariantCulture));
            case "cursor.y":
                return VarValue.FromText(CurrentCursor().y.ToString(CultureInfo.InvariantCulture));
            case "clipboard":
                return VarValue.FromText(NativeClipboard.TryGetText(out var text) ? text : "");
            case "selection":
                return VarValue.FromText(SelectionCapture.Capture(800));
            case "date":
                return VarValue.FromText(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case "time":
                return VarValue.FromText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            case "loop.i":
            case "loop.index":
            {
                int? i = LoopIndex;
                if (i == null) throw new FlowException("${loop.i} 只能在循环体内使用");
                return VarValue.FromNumber(i.Value);
            }
            case "run.name":
                return VarValue.FromText(RunName);
            case "lastError":
                return VarValue.FromText(LastError);
        }

        throw new FlowException($"未定义变量：{path}");
    }
}
