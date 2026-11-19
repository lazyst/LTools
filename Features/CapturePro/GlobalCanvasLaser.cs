using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 全屏画布的激光笔拖尾（纯数据 + 纯几何，<c>--selftest</c> 直接断言）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>snow-recording-effects/src/laser_trail.rs</c>：寿命 500ms、最多 50 个点、
/// 半径 2、流线平滑 0.6（新点 = 旧点 + (新点 − 旧点) × 0.6）。上游的淡出**不是透明度**，
/// 而是**线宽**收细：
/// </para>
/// <list type="bullet">
/// <item><c>time_width = 1 − (age / lifetime)²</c></item>
/// <item><c>length_width = 1 − (1 − length)⁴</c>，<c>length = 1 − (剩余点数 / 50)</c></item>
/// <item><c>radius = 2 × min(time_width, length_width)</c></item>
/// </list>
/// <para>
/// 颜色就是纯红 <c>#FF0000</c> 不透明（上游 <c>trail_rgba = 0xff0000ff</c> 解出来是
/// R=255 G=0 B=0 A=255）。所以画出来的是一条**端头收细的红线**，不是淡出的痕。
/// </para>
/// </remarks>
internal sealed class GlobalCanvasLaser
{
    /// <summary>拖尾寿命（上游 <c>DEFAULT_LIFETIME_MS</c>）。</summary>
    public const int LifetimeMs = 500;

    /// <summary>点数上限（上游 <c>MAX_POINTS</c>）。</summary>
    public const int MaxPoints = 50;

    /// <summary>笔头半径（上游 <c>RADIUS</c>，单位是输出像素；画布就是 DIP，所以直接当 DIP）。</summary>
    public const double Radius = 2.0;

    /// <summary>流线平滑系数（新点往旧点收 40%）。</summary>
    public const double Streamline = 0.6;

    /// <summary>拖尾颜色（上游纯红）。</summary>
    public static readonly Color Ink = Color.FromRgb(0xFF, 0x00, 0x00);

    /// <summary>一段拖尾（点是画布坐标，宽度是直径）。</summary>
    public readonly record struct Segment(Point From, Point To, double Width);

    private sealed class Sample
    {
        public Point At;
        public long Stamp;
        public bool Connected;
    }

    private readonly List<Sample> _samples = new();

    public int Count => _samples.Count;

    /// <summary>拖尾点（画布坐标，按加入顺序）。</summary>
    public IReadOnlyList<Point> Points
    {
        get
        {
            var list = new List<Point>(_samples.Count);
            foreach (var sample in _samples) list.Add(sample.At);
            return list;
        }
    }

    public void Clear() => _samples.Clear();

    /// <summary>
    /// 采样一次指针位置。位置没变就不加点（上游同样只在 <c>position != previous</c> 时记录）。
    /// </summary>
    /// <returns>真的加了一个点。</returns>
    public bool Observe(Point at, long now)
    {
        // 先淘汰过期的：留一个过期点当收细的起点，整段都过期就整段清掉
        while (_samples.Count > 1 && now - _samples[1].Stamp >= LifetimeMs) _samples.RemoveAt(0);
        if (_samples.Count > 0 && now - _samples[^1].Stamp >= LifetimeMs) _samples.Clear();

        if (_samples.Count > 0)
        {
            var last = _samples[^1].At;
            if (Math.Abs(last.X - at.X) < 0.01 && Math.Abs(last.Y - at.Y) < 0.01) return false;
        }

        bool connected = _samples.Count > 0;
        if (connected)
        {
            var previous = _samples[^1].At;
            at = new Point(
                previous.X + (at.X - previous.X) * Streamline,
                previous.Y + (at.Y - previous.Y) * Streamline);
        }

        if (_samples.Count == MaxPoints) _samples.RemoveAt(0);
        _samples.Add(new Sample { At = at, Stamp = now, Connected = connected });
        return true;
    }

    /// <summary>现在还有没有看得见的线（上游 <c>has_active_animation</c> 的同口径）。</summary>
    public bool HasActive(long now)
    {
        for (int i = 1; i < _samples.Count; i++)
        {
            if (!_samples[i].Connected) continue;
            if (now - _samples[i].Stamp < LifetimeMs) return true;
        }
        return false;
    }

    /// <summary>这一刻要画的线段（宽度已经按时间和长度收细）。</summary>
    public IReadOnlyList<Segment> Segments(long now)
    {
        var result = new List<Segment>();
        int count = _samples.Count;
        if (count < 2) return result;

        for (int i = 1; i < count; i++)
        {
            if (!_samples[i].Connected) continue;
            double fromRadius = Radius * Taper(i - 1, now, count);
            double toRadius = Radius * Taper(i, now, count);
            if (fromRadius <= 0.05 && toRadius <= 0.05) continue;
            result.Add(new Segment(_samples[i - 1].At, _samples[i].At, fromRadius + toRadius));
        }
        return result;
    }

    private double Taper(int index, long now, int count)
    {
        var sample = _samples[index];
        long age = now - sample.Stamp;
        double elapsed = Math.Clamp(age / (double)LifetimeMs, 0, 1);
        double length = 1 - (count - index) / (double)MaxPoints;
        double timeWidth = 1 - elapsed * elapsed;
        double lengthWidth = 1 - Math.Pow(1 - length, 4);
        return Math.Clamp(Math.Min(timeWidth, lengthWidth), 0, 1);
    }
}
