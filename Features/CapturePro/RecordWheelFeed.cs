using System;
using System.Collections.Concurrent;
using System.Threading;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏期间把系统滚轮事件喂给特效层（「记录鼠标滚动」那一项要它）。
/// </summary>
/// <remarks>
/// <para>
/// 鼠标钩子跑在 <c>HookDispatcher</c> 专用线程上，不可能去碰录制线程的任何对象，
/// 所以这里只做一个无锁队列：钩子 <see cref="Push"/> 一个 delta 就走，录制线程每帧
/// <see cref="TryTake"/> 把它变成「向上滚动 / 向下滚动」的按键浮层。
/// </para>
/// <para>
/// 不在录制时钩子**不进来**（<see cref="Active"/> 为 false 时 <see cref="Push"/> 直接返回），
/// 所以平时滚轮不会往队列里堆东西。
/// </para>
/// </remarks>
internal static class RecordWheelFeed
{
    private static readonly ConcurrentQueue<int> Queue = new();
    private static int _active;

    /// <summary>正在录屏（<c>RecordSession</c> 开录 / 收尾时切）。</summary>
    public static bool Active => Volatile.Read(ref _active) != 0;

    /// <summary>录屏开始 / 结束：开始前清空，避免把上一轮的滚轮读出来。</summary>
    public static void SetActive(bool active)
    {
        if (active)
        {
            while (Queue.TryDequeue(out _)) { }
        }

        Volatile.Write(ref _active, active ? 1 : 0);
    }

    /// <summary>滚轮 delta（120 = 一格）；只记方向。</summary>
    public static void Push(int delta)
    {
        if (delta == 0 || !Active) return;

        // 队列是保险丝不是日志：疯狂滚动时也别让它无限长
        if (Queue.Count > 64) return;
        Queue.Enqueue(delta);
    }

    /// <summary>取出一条（没有就 false）。</summary>
    public static bool TryTake(out int delta)
    {
        if (Queue.TryDequeue(out delta)) return true;

        delta = 0;
        return false;
    }
}
