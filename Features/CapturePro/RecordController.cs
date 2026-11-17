using System;
using System.Diagnostics;
using System.Threading;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一次录屏的共享状态：录 / 暂停 / 完成 / 取消，以及「不含暂停」的时长。
/// </summary>
/// <remarks>
/// <para>
/// 控制条按钮（UI 线程）与抓帧循环（后台线程）只通过这个对象说话，
/// 双方都不碰对方的窗口 / 位图，所以不需要加锁去协调 UI。
/// </para>
/// <para>
/// 计时用 <see cref="Stopwatch"/> 的 tick 数：暂停期间「活动时长」冻住，恢复后接着走，
/// 所以暂停不会在成品里留一段空白，也不会让视频时间轴比实际长。
/// </para>
/// </remarks>
internal sealed class RecordController
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();

    /// <summary>已经结束的暂停总时长（tick）。</summary>
    private long _pausedTicks;

    /// <summary>正式开始录的时刻（tick）；-1 = 面板还在等用户点「录制」。</summary>
    private long _origin = -1;

    /// <summary>当前这段暂停的开始时刻（tick）；-1 = 没在暂停。</summary>
    private long _pauseStarted = -1;

    private volatile bool _stop;
    private volatile bool _cancel;

    public bool IsPaused
    {
        get { lock (_gate) return _pauseStarted >= 0; }
    }

    /// <summary>用户点了「取消」：把已经录到的丢掉。</summary>
    public bool Cancelled => _cancel;

    /// <summary>该收尾了（不管哪一种）。</summary>
    public bool Finished => _stop || _cancel;

    public void Pause()
    {
        lock (_gate)
        {
            if (_pauseStarted < 0) _pauseStarted = Now;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pauseStarted < 0) return;
            _pausedTicks += Now - _pauseStarted;
            _pauseStarted = -1;
        }
    }

    /// <summary>切一下暂停；返回切换之后是不是暂停态。</summary>
    public bool TogglePause()
    {
        if (IsPaused)
        {
            Resume();
            return false;
        }

        Pause();
        return true;
    }

    public void Stop() => _stop = true;

    public void Cancel() => _cancel = true;

    /// <summary>面板先摆出来时，这一刻才真正开始计时（之后 <see cref="ActiveTicks"/> 才有意义）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_origin < 0) _origin = _clock.ElapsedTicks;
        }
    }

    /// <summary>已经开始录了（面板据此决定「录制中」的外观）。</summary>
    public bool HasStarted
    {
        get { lock (_gate) return _origin >= 0; }
    }

    /// <summary>活动时长（已经扣掉暂停），单位是 <see cref="Stopwatch"/> tick。</summary>
    public long ActiveTicks
    {
        get
        {
            lock (_gate)
            {
                if (_origin < 0) return 0;

                long now = Now;
                long paused = _pausedTicks + (_pauseStarted >= 0 ? now - _pauseStarted : 0);
                return Math.Max(0, now - paused);
            }
        }
    }

    /// <summary>从「开始录」算起的 tick（没开始就是 0）。调用方不用持锁。</summary>
    private long Now
    {
        get
        {
            long ticks = _clock.ElapsedTicks;
            return _origin < 0 ? 0 : ticks - _origin;
        }
    }

    public long ElapsedMs => ActiveTicks * 1000 / Stopwatch.Frequency;

    /// <summary>控制条上的 <c>mm:ss</c>。</summary>
    public string ClockText => RecordPlan.FormatClock((int)ElapsedMs);
}
