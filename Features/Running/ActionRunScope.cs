using System.Threading;
using LTools.Core;

namespace LTools.Features.Running;

/// <summary>
/// 一次运行在后台线程上的「当前上下文」：取消令牌与「按住不放」的登记表。
/// </summary>
/// <remarks>
/// <para>
/// 用 <see cref="AsyncLocal{T}"/> 而不是静态字段：引擎是同步阻塞的，一个运行一条专用线程，
/// 线程自己的 ExecutionContext 天然隔离；<see cref="InputGate"/> 等待锁时也从这里拿令牌，
/// 所以按停止热键能立刻打断「排队等注入」。
/// </para>
/// <para>
/// 不在运行里的调用（内置功能的单次注入、辅助工具等）拿不到上下文，
/// 由 <see cref="InputHoldState.Ambient"/> 退回进程级的兜底实例。
/// </para>
/// </remarks>
internal static class ActionRunScope
{
    internal sealed class Scope
    {
        public required CancellationToken Token { get; init; }
        public required InputHoldState Hold { get; init; }
        public required string Label { get; init; }
    }

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>注入闸等待锁时读当前运行的取消令牌（按停止热键能立刻打断「排队等注入」）。</summary>
    static ActionRunScope() => InputGate.TokenProvider = () => Token;

    public static IDisposable Begin(Scope scope)
    {
        Scope? previous = Current.Value;
        Current.Value = scope;
        return new Restore(previous);
    }

    public static InputHoldState? Hold => Current.Value?.Hold;

    public static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;

    public static string Label => Current.Value?.Label ?? "";

    private sealed class Restore(Scope? previous) : System.IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
