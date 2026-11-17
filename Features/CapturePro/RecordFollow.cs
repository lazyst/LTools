using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>跟随鼠标录屏的两个独立开关（照 Snow Shot：各自记忆，默认都开）。</summary>
internal readonly record struct RecordFollowOptions(bool SmoothMovement, bool ReduceJitter)
{
    public static RecordFollowOptions Default => new(SmoothMovement: true, ReduceJitter: true);
}

/// <summary>
/// 跟随鼠标的录制视口：选区决定**成品尺寸**，视口跟着光标在显示器里平移。
/// </summary>
/// <remarks>
/// <para>
/// 一比一移植 Snow Shot 的 <c>snow-crates/crates/snow-media/src/follow_mouse.rs</c>
/// （<c>FollowMouseMotion</c>），语义逐条对齐：
/// ① 视口尺寸 = 选区等比缩到不超过显示器（<see cref="Fit"/>），输出仍按选区尺寸编码；
/// ② 视口始终夹在显示器里，光标跳到边上也不会跑出去；
/// ③ **抑制抖动**：光标待在目标视口中央 5/6 之内就不动，进入外侧 1/12 才重新居中
///    （判定拿「目标」当基准，不是当前位置）；
/// ④ **平滑移动**：按 50 ms 半衰期指数逼近目标，0.5 像素内直接吸附，
///    但每帧都保证光标仍在视口里（快跳那一帧先追上，再慢慢缓过去）；
/// ⑤ 暂停恢复时不把暂停时长算进缓动（<see cref="RestartClock"/>）。
/// </para>
/// <para>
/// 纯数据 + 纯函数（时间由调用方以毫秒传入），<c>--selftest</c> 直接断言上游那几个用例。
/// </para>
/// </remarks>
internal sealed class RecordFollowMotion
{
    /// <summary>缓动半衰期（与上游 HALF_LIFE_SECONDS 一致）。</summary>
    public const double HalfLifeSeconds = 0.05;

    private readonly RecordFollowOptions _options;
    private readonly Axis _x;
    private readonly Axis _y;
    private long? _sampledAt;

    public RecordFollowMotion(Int32Rect viewport, Int32Rect bounds, RecordFollowOptions options, long nowMs)
    {
        _options = options;
        _x = new Axis(viewport.X, viewport.Width, bounds.X, bounds.X + bounds.Width);
        _y = new Axis(viewport.Y, viewport.Height, bounds.Y, bounds.Y + bounds.Height);
        _sampledAt = nowMs;
    }

    public bool IsMoving => _x.Position != _x.Target || _y.Position != _y.Target;

    /// <summary>当前视口（物理像素）。</summary>
    public Int32Rect Viewport => new(
        (int)_x.Origin(), (int)_y.Origin(), (int)_x.Extent, (int)_y.Extent);

    /// <summary>暂停恢复：从当前位置接着缓动，不把暂停的那段时间算成「已经走完」。</summary>
    public void RestartClock() => _sampledAt = null;

    /// <summary>把还没发布的视口直接对到光标中央（开录第一帧用）。</summary>
    public Int32Rect Center(int cursorX, int cursorY, long nowMs)
    {
        _sampledAt = nowMs;
        CenterTarget(cursorX, cursorY);
        ApplyTarget();
        return Viewport;
    }

    /// <summary>推进一帧：按光标位置更新目标 / 缓动，返回这一帧该抓的视口。</summary>
    public Int32Rect Advance(int cursorX, int cursorY, long nowMs)
    {
        long? previous = _sampledAt;
        _sampledAt = nowMs;

        if (!_options.ReduceJitter || _x.EntersMargin(cursorX) || _y.EntersMargin(cursorY))
            CenterTarget(cursorX, cursorY);

        if (!_options.SmoothMovement)
        {
            ApplyTarget();
        }
        else
        {
            if (IsMoving && previous is long before)
            {
                double elapsed = Math.Max(0, nowMs - before) / 1000.0;
                double fraction = 1.0 - Math.Pow(2.0, -elapsed / HalfLifeSeconds);
                _x.Ease(fraction, SettleDistance);
                _y.Ease(fraction, SettleDistance);
            }

            _x.KeepCursorInView(cursorX);
            _y.KeepCursorInView(cursorY);
        }

        return Viewport;
    }

    // ============ 内部 ============

    private void CenterTarget(double cursorX, double cursorY)
    {
        _x.CenterTarget(cursorX);
        _y.CenterTarget(cursorY);
    }

    /// <summary>不做缓动：目标就是这一帧的位置（两个开关都关掉时的行为）。</summary>
    private void ApplyTarget()
    {
        _x.Position = _x.Target;
        _y.Position = _y.Target;
    }

    // ============ 纯函数 ============

    /// <summary>吸附阈值：整数像素半个像素内就算到位。</summary>
    public const double SettleDistance = 0.5;

    /// <summary>
    /// 视口尺寸：选区等比缩到不超过 <paramref name="bounds"/>（放得下就原样），
    /// 再在 <paramref name="bounds"/> 里居中。
    /// </summary>
    public static Int32Rect Fit(Int32Rect selection, Int32Rect bounds)
    {
        int width = Math.Max(1, selection.Width);
        int height = Math.Max(1, selection.Height);

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            double fit = Math.Min(1.0, Math.Min(
                (double)bounds.Width / width, (double)bounds.Height / height));
            width = Math.Max(1, (int)Math.Round(width * fit));
            height = Math.Max(1, (int)Math.Round(height * fit));
        }

        int x = bounds.Width > 0 ? bounds.X + Math.Max(0, (bounds.Width - width) / 2) : selection.X;
        int y = bounds.Height > 0 ? bounds.Y + Math.Max(0, (bounds.Height - height) / 2) : selection.Y;

        return new Int32Rect(x, y, width, height);
    }

    /// <summary>最小录制尺寸（上游 <c>screenRecordingMinimumExtent</c>：10 物理像素）。</summary>
    public const int MinimumExtent = 10;

    /// <summary>把区域补到最小尺寸，并夹进 <paramref name="bounds"/>（夹不住就只补尺寸）。</summary>
    public static Int32Rect Normalize(Int32Rect region, Int32Rect bounds)
    {
        if (region.Width < 2 || region.Height < 2) return Int32Rect.Empty;

        int x = region.X, y = region.Y, w = region.Width, h = region.Height;

        if (w < MinimumExtent)
        {
            w = MinimumExtent;
            if (bounds.Width >= MinimumExtent)
                x = Math.Clamp(x, bounds.X, bounds.X + bounds.Width - MinimumExtent);
        }

        if (h < MinimumExtent)
        {
            h = MinimumExtent;
            if (bounds.Height >= MinimumExtent)
                y = Math.Clamp(y, bounds.Y, bounds.Y + bounds.Height - MinimumExtent);
        }

        return new Int32Rect(x, y, w, h);
    }

    // ============ 单轴 ============

    private sealed class Axis
    {
        public double Position;
        public double Target;
        public readonly double Extent;
        public readonly double MinOrigin;
        public readonly double MaxOrigin;

        public Axis(double origin, double extent, double minOrigin, double end)
        {
            Position = origin;
            Target = origin;
            Extent = extent;
            MinOrigin = minOrigin;
            MaxOrigin = end - extent;
        }

        /// <summary>居中：物理像素按整像素取半（与上游 <c>floor</c> 一致）。</summary>
        public void CenterTarget(double cursor) =>
            Target = Clamp(cursor - Math.Floor(Extent * 0.5), MinOrigin, MaxOrigin);

        /// <summary>光标是否进入「外侧 1/12」——进到这里才允许重新居中。</summary>
        public bool EntersMargin(double cursor)
        {
            double margin = Extent / 12.0;
            return (Target > MinOrigin && cursor <= Target + margin)
                || (Target < MaxOrigin && cursor >= Target + Extent - margin);
        }

        public void Ease(double fraction, double settle)
        {
            Position += (Target - Position) * fraction;
            if (Math.Abs(Target - Position) <= settle) Position = Target;
        }

        /// <summary>快跳那一帧：至少把光标拉进视口（远端留 1 像素）。</summary>
        public void KeepCursorInView(double cursor)
        {
            double minPosition = cursor - Math.Max(Extent - 1.0, 0.0);
            Position = Clamp(Clamp(Position, minPosition, cursor), MinOrigin, MaxOrigin);
        }

        public double Origin() => Math.Round(Position);

        /// <summary>边界可能倒挂（视口比范围还大）：倒挂时按最小值算，别抛。</summary>
        private static double Clamp(double value, double min, double max) =>
            min > max ? min : Math.Clamp(value, min, max);
    }
}
