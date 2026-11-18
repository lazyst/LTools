using System;

namespace LTools.Features.CapturePro;

/// <summary>鼠标的三个键（与 WPF 的 <c>MouseButton</c> 解耦，纯逻辑直接可测）。</summary>
internal enum PointerButton
{
    Left,
    Middle,
    Right,
}

/// <summary>
/// 「按下武装、抬起才执行」的鼠标释放动作（照 Snow Shot 的
/// <c>MouseReleaseActionController</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游为什么要绕这一圈：右键取消截图、双击关贴图这类动作如果是**按下就做**，
/// 用户按下去那一下窗口就没了，抬起还没发出去——下一次点击就会落到别的窗口上。
/// 所以按下的那一下只 <see cref="Arm"/>，真正的动作等按钮抬起之后**再排一拍**执行，
/// 抬起的那个事件本身也被吞掉。
/// </para>
/// <para>
/// 生命周期（与上游逐条对应）：
/// <list type="bullet">
/// <item>武装条件：范围窗口可见、按钮不是 <c>NoButton</c>、有动作、当前没有别的窗口抢着鼠标捕获。
/// 不满足就返回 false，调用方按上游的写法**什么都不做**（不是退回「按下就做」）。</item>
/// <item><see cref="Release"/>：按钮抬起 → 给出这一轮的 revision，等一拍再来取。</item>
/// <item><see cref="Take"/>：revision 没变、范围窗口还可见 → 动作交给调用方执行。</item>
/// <item><see cref="Cancel"/>：revision 递增，已经排队的 <see cref="Take"/> 全部作废
/// （窗口藏了 / 关了 / 失去激活 / 捕获被抢走 / 按钮已经不再按下）。</item>
/// </list>
/// 这个类**不碰 WPF**，窗口那一层（鼠标捕获、事件吞掉、<c>Dispatcher</c> 排一拍）在
/// <c>Views/CapturePro/MouseReleaseGate.cs</c>。
/// </para>
/// </remarks>
internal sealed class MouseReleaseGesture
{
    private long _revision;
    private long _released;              // 抬起那一轮给的 revision（0 = 还没抬）
    private PointerButton? _button;
    private Action? _pending;

    /// <summary>已经武装、还没执行（当作「这一下别再做别的事」的门面）。</summary>
    public bool Pending => _pending != null;

    /// <summary>武装的是哪个键（没武装时 null）。</summary>
    public PointerButton? Button => _button;

    /// <summary>当前轮次（每次 <see cref="Arm"/> / <see cref="Cancel"/> 都会变）。</summary>
    public long Revision => _revision;

    /// <summary>
    /// 武装一次。已经有待执行的动作 / 范围窗口不可见 / 没给动作 → false。
    /// </summary>
    public bool Arm(PointerButton button, Action? action, bool scopeVisible)
    {
        if (_pending != null || !scopeVisible || action == null) return false;

        _revision++;
        _released = 0;
        _button = button;
        _pending = action;
        return true;
    }

    /// <summary>
    /// 按钮抬起：返回这一轮的 revision（0 = 没武装 / 不是这个键 / 已经取过）。
    /// **不**递增 revision——递增只发生在 <see cref="Cancel"/>。
    /// </summary>
    public long Release(PointerButton button)
    {
        if (_pending == null || _button != button || _released != 0) return 0;
        _released = _revision;
        return _released;
    }

    /// <summary>
    /// 抬起之后一拍来取动作：revision 对不上（中途被取消 / 又武装了一次）或范围窗口
    /// 已经不可见 → 丢弃。取走之后本轮结束。
    /// </summary>
    public bool Take(long revision, bool scopeVisible, out Action? action)
    {
        action = null;
        if (_pending == null || _released == 0 || revision != _released || revision != _revision)
        {
            Cancel();
            return false;
        }

        if (!scopeVisible)
        {
            Cancel();
            return false;
        }

        action = _pending;
        _pending = null;
        _button = null;
        _released = 0;
        return true;
    }

    /// <summary>作废这一次（并递增 revision，让已经排队的 <see cref="Take"/> 什么都取不到）。</summary>
    public void Cancel()
    {
        _revision++;
        _released = 0;
        _pending = null;
        _button = null;
    }
}
