using System.Threading;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏面板「摆出来 → 等用户点录制」这一次握手。
/// </summary>
/// <remarks>
/// 面板在 UI 线程上写，会话在后台线程上等，所以只用两个 int + <see cref="Volatile"/>，
/// 不需要锁；<see cref="Started"/> / <see cref="Cancelled"/> 都可能一直不成立（用户没理它），
/// 调用方必须自己带超时 / 由关窗来取消。
/// </remarks>
internal sealed class RecordStartGate
{
    private int _started;
    private int _cancelled;

    public bool Started => Volatile.Read(ref _started) != 0;

    public bool Cancelled => Volatile.Read(ref _cancelled) != 0;

    /// <summary>用户点了录制（面板上的红色圆点）。</summary>
    public void Start() => Interlocked.Exchange(ref _started, 1);

    /// <summary>用户关掉了面板（红色 ✕ / 空白处右键）。</summary>
    public void Cancel() => Interlocked.Exchange(ref _cancelled, 1);
}
