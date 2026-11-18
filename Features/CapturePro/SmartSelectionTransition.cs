using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 智能选区换层级时的过渡插值（照上游 <c>ScreenshotSmartSelectionTransition</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游**没有自己的定时器**：presentation 每帧带着「现在几点」进来 <see cref="Advance"/>，
/// 所以帧率不规则也不会改变轨迹（按绝对时间算进度）。这里同口径：
/// 覆盖层用一个只在动画期间跑的 8ms <c>DispatcherTimer</c> 喂时间。
/// </para>
/// <para>
/// 三条固定参数：柔和 101ms、快速 67ms、边缘全部落在 0.4 DIP 以内直接落位（不做动画）；
/// 插值曲线是 <c>p * (2 - p)</c>（先快后慢的二次缓出），起点是**当前显示中的几何**
/// （不是上一个目标），所以中途改目标不会跳。
/// </para>
/// </remarks>
internal sealed class SmartSelectionTransition
{
    /// <summary>柔和过渡的时长（毫秒）。</summary>
    public const int SoftDurationMs = 101;

    /// <summary>快速过渡的时长（毫秒）。</summary>
    public const int FastDurationMs = 67;

    /// <summary>四条边都在这个距离内 = 直接落位（DIP）。</summary>
    public const double SnapTolerance = 0.4;

    private readonly Action<Rect>? _update;

    private Rect _displayed = Rect.Empty;
    private Rect _start = Rect.Empty;
    private Rect _target = Rect.Empty;
    private long _startedAt;
    private long _lastAdvancedAt;
    private double _progressAtStart;
    private bool _running;
    private bool _hasPresentedSmartSelection;
    private SelectionTransition _animation = SelectionTransition.Soft;

    public SmartSelectionTransition(Action<Rect>? update = null) => _update = update;

    public SelectionTransition Animation => _animation;

    public bool IsRunning => _running;

    /// <summary>当前真正显示出来的几何。</summary>
    public Rect Displayed => _displayed;

    /// <summary>当前动画时长：只有 <see cref="SelectionTransition.Fast"/> 是 67ms，其余按 101ms 算。</summary>
    public int DurationMs => _animation == SelectionTransition.Fast ? FastDurationMs : SoftDurationMs;

    /// <summary>上游 <c>QRectF::isValid() &amp;&amp; !isEmpty()</c>：宽高都大于 0 才算有选区。</summary>
    public static bool HasSelection(Rect rect) => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0;

    /// <summary>
    /// 换过渡档位。运行中改档只影响「剩下这段」：已经走掉的进度保留，剩余时间用新时长。
    /// 切到「无」时立刻跳到目标。
    /// </summary>
    public void SetAnimation(SelectionTransition animation, long nowMs)
    {
        // 非法档位回落到柔和（上游 normalizedSelectionTransitionAnimation）
        if (animation is not (SelectionTransition.Soft or SelectionTransition.Fast or SelectionTransition.None))
            animation = SelectionTransition.Soft;

        if (_animation == animation) return;

        Advance(nowMs);
        nowMs = Math.Max(nowMs, _lastAdvancedAt);

        if (_running)
        {
            _progressAtStart = ProgressAt(nowMs);
            _startedAt = nowMs;
        }

        _lastAdvancedAt = nowMs;
        _animation = animation;

        if (_animation == SelectionTransition.None && _running)
            PresentDirectly(_target);
    }

    /// <summary>
    /// 交来一个新目标。<paramref name="smartFraming"/> 为 false（手动框选 / 没有选区）时直接落位。
    /// 返回显示几何是否发生了变化。
    /// </summary>
    public bool Update(Rect selection, bool smartFraming, long nowMs)
    {
        bool hasSelection = HasSelection(selection);
        if (!smartFraming || !hasSelection)
        {
            _hasPresentedSmartSelection = false;
            return PresentDirectly(selection);
        }

        if (_animation == SelectionTransition.None || !_hasPresentedSmartSelection)
        {
            _hasPresentedSmartSelection = true;
            return PresentDirectly(selection);
        }

        if (selection == _target) return false;

        // 先把上一段曲线采样到「现在」，再从画面上真正显示的那块起步
        Advance(nowMs);
        _lastAdvancedAt = Math.Max(nowMs, _lastAdvancedAt);
        _startedAt = _lastAdvancedAt;
        _progressAtStart = 0;
        _start = _displayed;
        _target = selection;

        if (WithinSnapTolerance(_displayed, _target)) PresentDirectly(selection);
        else _running = true;

        return true;
    }

    /// <summary>喂一帧时间（毫秒）。返回显示几何是否变了。</summary>
    public bool Advance(long nowMs)
    {
        if (!_running || nowMs <= _lastAdvancedAt) return false;
        _lastAdvancedAt = nowMs;

        double progress = ProgressAt(nowMs);
        Rect selection;
        if (progress >= 1.0)
        {
            _running = false;
            selection = _target;
        }
        else
        {
            double eased = progress * (2.0 - progress);
            selection = new Rect(
                _start.X + (_target.X - _start.X) * eased,
                _start.Y + (_target.Y - _start.Y) * eased,
                _start.Width + (_target.Width - _start.Width) * eased,
                _start.Height + (_target.Height - _start.Height) * eased);
        }

        if (selection == _displayed) return false;
        _displayed = selection;
        NotifyUpdate();
        return true;
    }

    private double ProgressAt(long nowMs) =>
        _progressAtStart + (double)(nowMs - _startedAt) / DurationMs;

    private bool PresentDirectly(Rect selection)
    {
        _running = false;
        _target = selection;
        if (_displayed == selection) return false;
        _displayed = selection;
        NotifyUpdate();
        return true;
    }

    private static bool WithinSnapTolerance(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) <= SnapTolerance &&
        Math.Abs(a.Top - b.Top) <= SnapTolerance &&
        Math.Abs(a.Right - b.Right) <= SnapTolerance &&
        Math.Abs(a.Bottom - b.Bottom) <= SnapTolerance;

    private void NotifyUpdate() => _update?.Invoke(_displayed);
}
