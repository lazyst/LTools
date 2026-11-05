using System;
using System.Collections.Generic;
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
/// 一次自动化运行的上下文：变量、取消、步数 / 时间预算。
/// </summary>
/// <remarks>
/// 变量按运行隔离、不落盘。模板（<c>${name}</c>）只解析「设置变量 / 取值」创建的用户变量；
/// 未定义变量直接抛 <see cref="FlowException"/>，避免静默空串造成误操作。
/// </remarks>
internal sealed class RunContext
{
    /// <summary>单次运行最多执行的节点数（防 while/goto 型失控）。</summary>
    public const int MaxSteps = 20_000;

    /// <summary>单次运行最长时长。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    private readonly CancellationToken _token;
    private readonly DateTime _deadline;

    public RunContext(CancellationToken token, int cursorX, int cursorY)
    {
        _token = token;
        CursorX = cursorX;
        CursorY = cursorY;
        _deadline = DateTime.UtcNow + MaxDuration;
    }

    /// <summary>面板唤起时记录的光标坐标；0 表示运行时取当前位置。</summary>
    public int CursorX { get; }
    public int CursorY { get; }

    /// <summary>变量表（大小写不敏感）。</summary>
    public Dictionary<string, VarValue> Vars { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已执行的节点数。</summary>
    public int StepCount { get; private set; }

    /// <summary>最近一次被吞掉的异常文本（「上一步的错误」取值器用）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>是否有取消请求。</summary>
    public bool IsCancelled => _token.IsCancellationRequested;

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

    /// <summary>解析 <c>${...}</c> 里的变量路径；未定义抛 <see cref="FlowException"/>。</summary>
    /// <remarks>只有流程里「设置变量 / 取值」创建的用户变量可以被解析，没有隐藏的内置值。</remarks>
    public VarValue ResolvePath(string path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) throw new FlowException("模板里的变量名为空");

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

        throw new FlowException($"未定义变量：{path}（先添加「设置变量」或左侧「取值」）");
    }
}
