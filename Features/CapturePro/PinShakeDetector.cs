using System;

namespace LTools.Features.CapturePro;

/// <summary>
/// 拖动贴图时的「抖动」识别：左右快速来回甩六下就触发一次（用来隐藏 / 显示同组其他贴图）。
/// </summary>
/// <remarks>
/// <para>
/// 逐条照上游 <c>screenshotpinnedshakedetector.h</c> 移植：位置是**逻辑像素**，
/// 不含任何窗口查询、计时器或分配——时间由调用方给（<c>Stopwatch</c> 的毫秒）。
/// 状态机按 X / Y 两根轴各记一份，任一轴转够六次就算一次抖动；同一段拖动里只触发一次。
/// </para>
/// <para>
/// 四个门槛与原版一致：每一「腿」至少 48、至少 6 腿、单腿不超过 250ms、整串不超过 700ms。
/// 边界是**闭区间**（48 与 250 / 700 都算达标），上游 <c>tests/screenshot_pinned_shake_detector_tests.cpp</c>
/// 的八个用例在 <c>--selftest</c> 里逐条守着。
/// </para>
/// </remarks>
internal sealed class PinShakeDetector
{
    /// <summary>单腿的最小位移（逻辑像素）。</summary>
    public const double MinimumLeg = 48;

    /// <summary>至少几腿才算抖动（六腿 = 三次完整来回）。</summary>
    public const int MinimumLegCount = 6;

    /// <summary>单腿的最长时长（毫秒，含）。</summary>
    public const long MaximumLegMs = 250;

    /// <summary>整串的最长时长（毫秒，含）。</summary>
    public const long MaximumSequenceMs = 700;

    private Axis _x;
    private Axis _y;
    private long _lastTime;
    private bool _active;
    private bool _triggered;

    /// <summary>本次拖动里已经触发过了。</summary>
    public bool Triggered => _triggered;

    /// <summary>清空状态（拖动结束 / 取消时调）。</summary>
    public void Reset()
    {
        _x = default;
        _y = default;
        _lastTime = 0;
        _active = false;
        _triggered = false;
    }

    /// <summary>开始一段拖动；坐标非有限数或时间倒流时不武装。</summary>
    public void Begin(double x, double y, long timeMs = 0)
    {
        Reset();
        if (!Valid(x, y) || timeMs < 0) return;

        _x.Begin(x, timeMs);
        _y.Begin(y, timeMs);
        _lastTime = timeMs;
        _active = true;
    }

    /// <summary>
    /// 采一个点；识别到抖动时**只返回一次 true**（之后直到 <see cref="Begin"/> / <see cref="Reset"/> 都是 false）。
    /// </summary>
    public bool Sample(double x, double y, long timeMs)
    {
        if (!_active || _triggered) return false;

        if (!Valid(x, y) || timeMs < _lastTime)
        {
            Begin(x, y, timeMs);
            return false;
        }

        _lastTime = timeMs;
        bool hitX = _x.Sample(x, timeMs);
        bool hitY = _y.Sample(y, timeMs);
        _triggered = hitX || hitY;
        return _triggered;
    }

    private static bool Valid(double x, double y) => !double.IsNaN(x) && !double.IsInfinity(x) &&
                                                      !double.IsNaN(y) && !double.IsInfinity(y);

    /// <summary>一根轴上的状态机：等第一腿 → 记极值 → 反向够长才算一腿。</summary>
    private struct Axis
    {
        private double _origin;
        private double _extreme;
        private long _sequenceStart;
        private long _legStart;
        private long _extremeTime;
        private int _direction;
        private int _legs;

        public void Begin(double position, long timeMs)
        {
            _origin = _extreme = position;
            _sequenceStart = _legStart = _extremeTime = timeMs;
            _direction = _legs = 0;
        }

        public bool Sample(double position, long timeMs)
        {
            if (timeMs - _sequenceStart > MaximumSequenceMs)
            {
                Begin(position, timeMs);
                return false;
            }

            if (_direction == 0)
            {
                if (timeMs - _legStart > MaximumLegMs)
                {
                    Begin(position, timeMs);
                    return false;
                }

                if (Math.Abs(position - _origin) >= MinimumLeg)
                {
                    _direction = position > _origin ? 1 : -1;
                    _extreme = position;
                    _extremeTime = timeMs;
                    _legs = 1;
                }

                return false;
            }

            double advance = (position - _extreme) * _direction;
            if (advance > 0)
            {
                // 拉长正在走的那一腿不能把「一次很长的普通拖动」算成很多快腿。
                if (timeMs - _legStart > MaximumLegMs)
                {
                    Begin(position, timeMs);
                    return false;
                }

                _extreme = position;
                _extremeTime = timeMs;
            }
            else
            {
                if (timeMs - _extremeTime > MaximumLegMs)
                {
                    Begin(position, timeMs);
                    return false;
                }

                if (-advance >= MinimumLeg)
                {
                    _legStart = _extremeTime;
                    _extreme = position;
                    _extremeTime = timeMs;
                    _direction = -_direction;
                    return ++_legs == MinimumLegCount;
                }
            }

            return false;
        }
    }
}
