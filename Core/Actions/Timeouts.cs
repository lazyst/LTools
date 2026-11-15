using System;

namespace LTools.Core.Actions;

/// <summary>
/// 「最多等待毫秒」（<c>timeoutMs</c>）这类参数的统一口径。
/// </summary>
/// <remarks>
/// <para>
/// 三种语义：<c>0</c> = 只做一次、<c>-1</c> = 一直做（直到成功 / 按停止热键）、其余夹到 1–<see cref="MaxMs"/>。
/// 等待类与命中类动作单元都走这里，别再各自 <c>Math.Clamp(…, 0, 600_000)</c>——
/// 那样写不出「一直等」，各处上限也会慢慢分叉。
/// </para>
/// <para>
/// 等待循环里用 <see cref="Loop"/>：不限时给一个不会先到的期限，每轮仍走 <c>ctx.Wait</c>，
/// 所以按停止热键照样立刻返回。
/// </para>
/// </remarks>
public static class Timeouts
{
    /// <summary>参数里「一直等」的写法。</summary>
    public const int Unlimited = -1;

    /// <summary>有限等待的上限（10 分钟）。</summary>
    public const int MaxMs = 600_000;

    /// <summary>把参数值归一成「0 / -1 / 1–600000」。</summary>
    public static int Normalize(double ms)
        => ms < 0 ? Unlimited : (int)Math.Clamp(Math.Round(ms), 0, MaxMs);

    /// <summary>等待循环里的期限：负数（= 不限）= 不会先到。</summary>
    public static int Loop(int timeoutMs)
        => timeoutMs < 0 ? int.MaxValue : Math.Clamp(timeoutMs, 0, MaxMs);

    /// <summary>失败提示里那句「等了多久」（不限 / 0 都有专门的说法）。</summary>
    public static string DescribeWait(int timeoutMs)
        => timeoutMs < 0 ? "，一直等到找到为止"
            : timeoutMs > 0 ? $"，等了 {timeoutMs} 毫秒"
            : "";
}
