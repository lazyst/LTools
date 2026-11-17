using System;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>一帧的处理结论（诊断用）。</summary>
internal sealed class StitchDecision
{
    public int InputIndex { get; init; }

    /// <summary>和上一帧输入逐像素一样（滚轮没打到目标 / 到底了）。</summary>
    public bool ExactDuplicate { get; init; }

    public StitchBranch Branch { get; init; }

    /// <summary>被接受的位移（没接受就是 <c>null</c>）。</summary>
    public int? AcceptedOffset { get; init; }

    public float? Confidence { get; init; }

    /// <summary>这一帧让画布变长了多少。</summary>
    public int Growth { get; init; }

    public MotionEstimate? Motion { get; init; }
}

/// <summary>
/// 增量拼接累加器：估计位移 → 换算视口位置 → 接 / 补画布 → 合成下一帧的参考画面。
/// </summary>
/// <remarks>
/// <para>
/// 移植自 <c>snow-stitch-images/src/stitcher.rs</c> 的 <c>StitchAccumulator</c>。位移语义与
/// <see cref="ViewportState"/> 一致（负数 = 往下滚），候选位置是 <c>position - offset</c>：
/// 超出右边 = <see cref="StitchBranch.Append"/>，掉到 0 以下 = <see cref="StitchBranch.Prepend"/>，
/// 落在中间 = <see cref="StitchBranch.Contained"/>（往回滚了，内容已经在画布里）。
/// </para>
/// <para>
/// 三种分支各自怎么补画布、怎么合成参考帧与原实现一致：
/// </para>
/// <list type="bullet">
/// <item>Append：画布先截掉与重画带重叠的那 <c>band - growth</c> 段，再接上这一帧末尾 <c>band</c> 段；
/// 参考帧 = 旧参考帧往上挪 <c>shift</c>、底部换成这一帧的重画带。</item>
/// <item>Prepend：画布先截掉头部 <c>band - growth</c> 段，再在前面补这一帧开头 <c>band</c> 段；
/// 参考帧 = 这一帧顶部的重画带盖住旧的，旧参考帧下面那截接在后面。</item>
/// <item>Contained：画布不动，参考帧改成「画布窗口」（下一次比较前从画布里取）。</item>
/// </list>
/// <para>
/// <c>band = max(一屏 / 2, 一屏 / 4 + growth)</c>：重画带不能窄于位移，也不能只有一小条。
/// </para>
/// </remarks>
internal sealed class StitchStitcher
{
    private readonly StitchAxis _axis;
    private readonly MotionEstimatorOptions _options;
    private readonly StitchEstimator _estimator;
    private readonly EstimationCache _cache = new();
    private readonly TiledCanvas _canvas;
    private readonly int _viewportExtent;
    private readonly int _crossExtent;

    private StitchFrame _previousRaw;
    private StitchFrame _syntheticReference;
    private bool _canvasWindow;
    private long? _loadedCanvasWindow;
    private ViewportState _state;

    public StitchStitcher(StitchFrame first, StitchAxis axis, MotionEstimatorOptions? options = null)
    {
        if (first.Width <= 4 || first.Height <= 4)
            throw new ArgumentException("第一帧太小，长截图至少要 5 × 5 像素", nameof(first));

        _axis = axis;
        _options = options ?? MotionEstimatorOptions.Default;
        _viewportExtent = first.AxisExtent(axis);
        _crossExtent = first.CrossExtent(axis);
        _canvas = TiledCanvas.FromFrame(first, axis);
        _previousRaw = first;
        _syntheticReference = first;
        _estimator = new StitchEstimator(axis, first.Width, first.Height, _options);
    }

    public StitchAxis Axis => _axis;

    /// <summary>一屏沿轴的长度。</summary>
    public int ViewportExtent => _viewportExtent;

    /// <summary>视口顶边在画布里的位置。</summary>
    public long Position => _state.Position;

    /// <summary>滚到过的最大位置（= 画布比一屏多出来的长度）。</summary>
    public long MaxPosition => _state.MaxPosition;

    /// <summary>画布沿轴的总长（= 一屏 + <see cref="MaxPosition"/>）。</summary>
    public int CanvasExtent => _state.CanvasExtent(_viewportExtent);

    public int CrossExtent => _crossExtent;

    public int ProcessedCount { get; private set; }

    public int AcceptedCount { get; private set; }

    /// <summary>接下一帧，返回这一帧的处理结论。</summary>
    public StitchDecision Process(StitchFrame incoming)
    {
        if (incoming.Width != _syntheticReference.Width || incoming.Height != _syntheticReference.Height)
            throw new ArgumentException("帧尺寸和第一帧不一致", nameof(incoming));

        int index = ProcessedCount + 1;

        // 逐像素一模一样：说明画面根本没动（到底 / 滚轮没打到目标窗口）。
        if (_previousRaw.VisiblePixelsEqual(incoming))
        {
            return new StitchDecision
            {
                InputIndex = index,
                ExactDuplicate = true,
                Branch = StitchBranch.Skip,
            };
        }

        ProcessedCount++;

        if (_canvasWindow)
        {
            long position = _state.Position;
            if (_loadedCanvasWindow != position)
            {
                _canvas.CopyAxisTo((int)position, (int)position + _viewportExtent, _syntheticReference);
                _loadedCanvasWindow = position;
            }
        }

        MotionEstimate estimate = _estimator.Estimate(_syntheticReference, _previousRaw, incoming, _cache);

        int maximumShift = Math.Max(0, (int)MathF.Floor(_viewportExtent * _options.MaxMotionRatio));
        int? accepted = estimate.IsMotion && estimate.Offset != 0 && Math.Abs(estimate.Offset) <= maximumShift
            ? estimate.Offset
            : null;

        if (accepted == null)
        {
            _previousRaw = incoming;
            return new StitchDecision
            {
                InputIndex = index,
                Branch = StitchBranch.NoMovement,
                Confidence = estimate.Confidence,
                Motion = estimate,
            };
        }

        AcceptedCount++;

        // 参考帧的像素（也就是它的准备数据）马上要变。
        _cache.InvalidateReference();
        _previousRaw = incoming;

        ViewportTransition transition = _state.Transition(accepted.Value);
        _loadedCanvasWindow = null;
        int shift = Math.Abs(accepted.Value);

        switch (transition.Branch)
        {
            case StitchBranch.Append:
            {
                int band = StitchCompositor.BandHeight(_viewportExtent, transition.Growth);
                int overlap = band - transition.Growth;
                _canvas.TruncateEnd(_canvas.Extent - overlap);
                _canvas.AppendAxis(_previousRaw, _viewportExtent - band, _viewportExtent);
                _syntheticReference = StitchCompositor.SynthesizeAppend(
                    _syntheticReference, _previousRaw, _axis, shift);
                _canvasWindow = false;
                break;
            }

            case StitchBranch.Prepend:
            {
                int band = StitchCompositor.BandHeight(_viewportExtent, transition.Growth);
                _canvas.TruncateStart(band - transition.Growth);
                _canvas.PrependAxis(_previousRaw, 0, band);
                _syntheticReference = StitchCompositor.SynthesizePrepend(
                    _syntheticReference, _previousRaw, _axis, shift);
                _canvasWindow = false;
                break;
            }

            case StitchBranch.Contained:
                _canvasWindow = true;
                break;

            default:
                throw new InvalidOperationException("过渡分支不该是 Skip / NoMovement");
        }

        _state = transition.Next;
        AssertInvariants();

        return new StitchDecision
        {
            InputIndex = index,
            Branch = transition.Branch,
            AcceptedOffset = accepted,
            Confidence = estimate.Confidence,
            Growth = transition.Growth,
            Motion = estimate,
        };
    }

    /// <summary>把画布 [<paramref name="start"/>, <paramref name="end"/>) 这一段拷进 <paramref name="target"/> 的开头。</summary>
    public void CopyAxisTo(int start, int end, StitchFrame target) => _canvas.CopyAxisTo(start, end, target);

    /// <summary>把画布的第 <paramref name="index"/> 条线（一行或一列）拷进 <paramref name="destination"/>。</summary>
    public void CopyLine(int index, byte[] destination) => _canvas.CopyLine(index, destination);

    /// <summary>整张画布物化成一张图。</summary>
    public StitchFrame Materialize() => _canvas.Materialize();

    private void AssertInvariants()
    {
        int expectedExtent = _state.CanvasExtent(_viewportExtent);
        if (_canvas.Extent != expectedExtent ||
            _canvas.Cross != _crossExtent ||
            _state.Position < 0 ||
            _state.Position > _state.MaxPosition)
        {
            throw new InvalidOperationException("画布 / 视口状态不变量被破坏");
        }
    }
}
