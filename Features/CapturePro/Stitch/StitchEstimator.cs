using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>运动估计的调参；默认值与 Snow Shot 的 <c>MotionEstimatorOptions::default</c> 一致。</summary>
internal sealed class MotionEstimatorOptions
{
    /// <summary>相似度分块边长（像素）。</summary>
    public int TileSize { get; init; } = 32;

    /// <summary>每帧最多用多少个特征点。</summary>
    public int MaxFeatures { get; init; } = 2500;

    /// <summary>允许的最大位移占一屏的比例（超过就当「换页了」）。</summary>
    public float MaxMotionRatio { get; init; } = 0.6f;

    /// <summary>接受一次位移所需的最低置信度。</summary>
    public float MinConfidence { get; init; } = 0.65f;

    public static MotionEstimatorOptions Default { get; } = new();
}

/// <summary>运动估计的结论。</summary>
internal enum MotionOutcomeKind
{
    /// <summary>认出了位移（<see cref="MotionEstimate.Offset"/> 有效，且不为 0）。</summary>
    Motion,

    /// <summary>画面没动。</summary>
    NoMotion,

    /// <summary>认不出（特征太少 / 置信度不够 / 像换了一页）。</summary>
    Indeterminate,
}

/// <summary>运动估计走到哪一步（诊断用；<c>SceneCut</c> 是连续三次被拒后的结论）。</summary>
internal enum MotionStage
{
    InputTooSmall,
    IdenticalInterior,
    EmptyDescriptors,
    NoMatches,
    NoCandidates,
    SelectedNoMotion,
    LowConfidence,
    SceneCut,
    Selected,
}

/// <summary>一个候选位移的打分明细。</summary>
internal sealed class CandidateDiagnostics
{
    public int Offset { get; init; }

    public int RawInliers { get; init; }

    public int InlierTiles { get; init; }

    public float WeightedSupport { get; init; }

    public float WeightedInlierShare { get; init; }

    public float SpatialCoverage { get; init; }

    public float AlignmentError { get; init; }

    /// <summary>亚像素细化后的配准误差（只有最强候选才有）。</summary>
    public float? PreciseAlignmentError { get; set; }

    public float ResidualGain { get; init; }

    public float Score { get; init; }
}

/// <summary>一次运动估计的完整诊断。</summary>
internal sealed class MotionDiagnostics
{
    public MotionStage Stage { get; set; }

    public int ReferenceKeypoints { get; set; }

    public int IncomingKeypoints { get; set; }

    public int MutualMatches { get; set; }

    public float DirectSimilarity { get; set; }

    public int? SelectedOffset { get; set; }

    public List<CandidateDiagnostics> Candidates { get; } = new();
}

/// <summary>运动估计的结果。</summary>
internal sealed class MotionEstimate
{
    public MotionOutcomeKind Kind { get; init; }

    public int Offset { get; init; }

    public float Confidence { get; init; }

    public MotionDiagnostics Diagnostics { get; init; } = new();

    public bool IsMotion => Kind == MotionOutcomeKind.Motion;

    public static MotionEstimate Motion(int offset, float confidence, MotionDiagnostics diagnostics) =>
        new() { Kind = MotionOutcomeKind.Motion, Offset = offset, Confidence = confidence, Diagnostics = diagnostics };

    public static MotionEstimate NoMotion(float confidence, MotionDiagnostics diagnostics) =>
        new() { Kind = MotionOutcomeKind.NoMotion, Confidence = confidence, Diagnostics = diagnostics };

    public static MotionEstimate Indeterminate(float confidence, MotionDiagnostics diagnostics) =>
        new() { Kind = MotionOutcomeKind.Indeterminate, Confidence = confidence, Diagnostics = diagnostics };
}

/// <summary>估计器跨帧复用的准备数据（保留上一帧灰度 / 特征，参考帧没变就不重算）。</summary>
internal sealed class EstimationCache
{
    public GrayImage? ReferenceGray { get; set; }

    public List<BinaryFeature>? ReferenceFeatures { get; set; }

    public List<BinaryFeature>? FullReferenceFeatures { get; set; }

    public GrayImage? PreviousGray { get; set; }

    public List<BinaryFeature>? PreviousFeatures { get; set; }

    public List<BinaryFeature>? PreviousFullFeatures { get; set; }

    /// <summary>参考帧的像素变了：它相关的准备数据全部作废。</summary>
    public void InvalidateReference()
    {
        ReferenceGray = null;
        ReferenceFeatures = null;
        FullReferenceFeatures = null;
    }
}

/// <summary>一个二值特征点（坐标已映回源图，描述子是 32 字节）。</summary>
internal readonly struct BinaryFeature
{
    public BinaryFeature(float x, float y, byte[] descriptor)
    {
        X = x;
        Y = y;
        Descriptor = descriptor;
    }

    public float X { get; }

    public float Y { get; }

    public byte[] Descriptor { get; }
}

internal readonly struct DescriptorMatch
{
    public DescriptorMatch(int trainIndex, uint distance)
    {
        TrainIndex = trainIndex;
        Distance = distance;
    }

    public int TrainIndex { get; }

    public uint Distance { get; }
}

/// <summary>一对互为最佳匹配的观测。</summary>
internal readonly struct MatchObservation
{
    public MatchObservation(uint distance, float referenceX, float referenceY, float incomingX, float incomingY, int referenceTile, int dx, int dy)
    {
        Distance = distance;
        ReferenceX = referenceX;
        ReferenceY = referenceY;
        IncomingX = incomingX;
        IncomingY = incomingY;
        ReferenceTile = referenceTile;
        Dx = dx;
        Dy = dy;
    }

    public uint Distance { get; }

    public float ReferenceX { get; }

    public float ReferenceY { get; }

    public float IncomingX { get; }

    public float IncomingY { get; }

    public int ReferenceTile { get; }

    public int Dx { get; }

    public int Dy { get; }
}

/// <summary>特征证据：两边各检测到多少点、互为最佳的有哪些。</summary>
internal sealed class FeatureEvidence
{
    public FeatureEvidence(int referenceFeatures, int incomingFeatures, List<MatchObservation> observations)
    {
        ReferenceFeatures = referenceFeatures;
        IncomingFeatures = incomingFeatures;
        Observations = observations;
    }

    public int ReferenceFeatures { get; }

    public int IncomingFeatures { get; }

    public List<MatchObservation> Observations { get; }
}

/// <summary>
/// 滚动位移估计：ORB 特征互匹配给出候选位移，SSIM 图给每个候选打分，
/// 再用亮度 / 梯度补丁把最强的那个细化到 ±2 像素。
/// </summary>
/// <remarks>
/// <para>
/// 移植自 <c>snow-stitch-images/src/estimator.rs</c>（常量、公式、判定顺序逐条对齐）：
/// <c>LOWE_RATIO = 0.8</c>、<c>MAX_HAMMING_DISTANCE = 64</c>、<c>MAX_CROSS_AXIS_DELTA = 4</c>、
/// <c>INLIER_TOLERANCE = 2</c>、<c>MAX_CANDIDATES = 8</c>、<c>MIN_INLIER_MATCHES = 8</c>、
/// <c>MIN_INLIER_TILES = 4</c>、<c>MIN_RESIDUAL_GAIN = 0.15</c>；候选打分
/// <c>0.6 × 加权内点占比 + 0.25 × 残差增益 + 0.15 × 空间覆盖</c>，
/// 置信度 <c>0.40 × 内点占比 + 0.25 × 空间覆盖 + 0.20 × 残差增益 + 0.15 × 领先幅度</c>。
/// </para>
/// <para>
/// 「位移」的方向与 <see cref="ViewportState"/> 一致：负数 = 内容朝上走（往下滚），正数 = 往回滚。
/// </para>
/// </remarks>
internal sealed class StitchEstimator
{
    private const float LoweRatio = 0.8f;
    private const float MaxHammingDistance = 64f;
    private const int MaxCrossAxisDelta = 4;
    private const int InlierTolerance = 2;
    private const int MaxCandidates = 8;
    private const int MaxFeaturesPerTile = 8;
    private const int MinInlierMatches = 8;
    private const int MinInlierTiles = 4;
    private const float MinResidualGain = 0.15f;

    private const int RefinementPatchRadius = 4;
    private const int RefinementOffsetCount = 2 * InlierTolerance + 1;
    private const int MaxRefinementPatchesPerTile = 2;

    private readonly StitchAxis _axis;
    private readonly MotionEstimatorOptions _options;
    private readonly TileLayout _layout;
    private readonly int _width;
    private readonly int _height;
    private readonly StitchSampling _sampling;
    private readonly OrbPyramidPlan _pyramid;
    private readonly int _maximumShift;

    private OrbPyramidPlan? _fullPyramid;
    private int _sceneCutStreak;

    public StitchEstimator(StitchAxis axis, int width, int height, MotionEstimatorOptions? options = null)
    {
        _axis = axis;
        _options = options ?? MotionEstimatorOptions.Default;
        _width = width;
        _height = height;
        _layout = new TileLayout(width, height, _options.TileSize);

        _sampling = StitchSampling.Create(axis, width, height);
        (int scaledWidth, int scaledHeight) = _sampling.Dimensions(width, height);
        _pyramid = OrbPyramidPlan.Create(scaledWidth, scaledHeight);
        _maximumShift = Math.Max(0, (int)MathF.Floor(PrimaryExtent * _options.MaxMotionRatio));
    }

    private int PrimaryExtent => _axis == StitchAxis.Vertical ? _height : _width;

    /// <summary>
    /// 估计 <paramref name="incoming"/> 相对 <paramref name="previousRaw"/> 的位移，
    /// <paramref name="reference"/> 是「合成参考帧」（应该是上一帧滚过之后屏幕上该看到的样子）。
    /// </summary>
    /// <remarks>
    /// 直接调用（每对帧都是新的一对）走这一版：每次新建缓存。参照 <c>estimator.rs</c> 的
    /// <c>estimate</c>——只有拼接累加器那种能保证「参考帧只在被接受后才变」的调用方
    /// 才该复用 <see cref="EstimationCache"/>，否则上一对的参考帧会被当成本对的（实测会把
    /// 连续滚动的第二帧认成两帧累计的位移）。
    /// </remarks>
    public MotionEstimate Estimate(StitchFrame reference, StitchFrame previousRaw, StitchFrame incoming) =>
        Estimate(reference, previousRaw, incoming, new EstimationCache());

    public MotionEstimate Estimate(StitchFrame reference, StitchFrame previousRaw, StitchFrame incoming, EstimationCache cache)
    {
        if (reference.Width != _width || reference.Height != _height ||
            previousRaw.Width != _width || previousRaw.Height != _height ||
            incoming.Width != _width || incoming.Height != _height)
        {
            throw new ArgumentException("帧尺寸与估计器不一致");
        }

        GrayImage? previousGray = cache.PreviousGray;
        List<BinaryFeature>? previousFeatures = cache.PreviousFeatures;
        List<BinaryFeature>? previousFullFeatures = cache.PreviousFullFeatures;
        cache.PreviousGray = null;
        cache.PreviousFeatures = null;
        cache.PreviousFullFeatures = null;

        if (_width < 5 || _height < 5)
            return MotionEstimate.NoMotion(1f, new MotionDiagnostics { Stage = MotionStage.InputTooSmall });

        if (reference.VisibleInteriorPixelsEqual(incoming))
        {
            _sceneCutStreak = 0;
            return MotionEstimate.NoMotion(1f, new MotionDiagnostics
            {
                Stage = MotionStage.IdenticalInterior,
                DirectSimilarity = 1f,
            });
        }

        // 合成参考帧可能逐像素等于上一帧输入：那份准备数据照样能用，省一次 ORB。
        if (cache.ReferenceGray == null && previousGray != null && reference.VisiblePixelsEqual(previousRaw))
        {
            cache.ReferenceGray = previousGray;
            cache.ReferenceFeatures = previousFeatures;
            cache.FullReferenceFeatures = previousFullFeatures;
        }

        GrayImage previous = previousGray ?? GrayImage.FromFrame(previousRaw);
        GrayImage incomingGray = GrayImage.FromFrame(incoming);
        SimilarityMap direct = SimilarityMap.Between(previous, incomingGray, _layout, _axis, 0);

        cache.ReferenceGray ??= GrayImage.FromFrame(reference);
        GrayImage referenceGray = cache.ReferenceGray;
        SimilarityMap zeroAlignment = SimilarityMap.Between(referenceGray, incomingGray, _layout, _axis, 0);

        FeatureEvidence evidence = BuildEvidence(reference, incoming, cache, null, _sampling, out List<BinaryFeature> incomingFeatures);
        cache.PreviousFeatures = incomingFeatures;

        var images = new EvaluationImages(reference, incoming, referenceGray, incomingGray, direct, zeroAlignment);
        EvaluatedMotion evaluated = Evaluate(images, evidence);

        if (_sampling.Reduced && evaluated.NeedsFullResolution())
        {
            _fullPyramid ??= OrbPyramidPlan.Create(_width, _height);
            FeatureEvidence full = BuildEvidence(reference, incoming, cache, _fullPyramid, null, out List<BinaryFeature> fullFeatures);
            cache.PreviousFullFeatures = fullFeatures;
            evaluated = Evaluate(images, full);
        }

        cache.PreviousGray = incomingGray;
        return Commit(evaluated);
    }

    // ============ 特征 ============

    private FeatureEvidence BuildEvidence(
        StitchFrame reference,
        StitchFrame incoming,
        EstimationCache cache,
        OrbPyramidPlan? fullPyramid,
        StitchSampling? sampling,
        out List<BinaryFeature> incomingFeatures)
    {
        bool full = fullPyramid != null;
        OrbPyramidPlan pyramid = fullPyramid ?? _pyramid;
        List<BinaryFeature>? cached = full ? cache.FullReferenceFeatures : cache.ReferenceFeatures;

        List<BinaryFeature> incomingSet;
        List<BinaryFeature> referenceFeatures;
        if (cached != null)
        {
            incomingSet = Detect(incoming, pyramid, sampling);
            referenceFeatures = cached;
        }
        else
        {
            List<BinaryFeature>? referenceResult = null;
            List<BinaryFeature>? incomingResult = null;
            Parallel.Invoke(
                () => referenceResult = Detect(reference, pyramid, sampling),
                () => incomingResult = Detect(incoming, pyramid, sampling));
            referenceFeatures = referenceResult ?? new List<BinaryFeature>();
            incomingSet = incomingResult ?? new List<BinaryFeature>();
            if (full)
                cache.FullReferenceFeatures = referenceFeatures;
            else
                cache.ReferenceFeatures = referenceFeatures;
        }

        incomingFeatures = incomingSet;
        List<MatchObservation> observations = MutualObservations(referenceFeatures, incomingSet);
        return new FeatureEvidence(referenceFeatures.Count, incomingSet.Count, observations);
    }

    /// <summary>检测并按「块均衡」挑出特征点（每块最多 8 个，整体按响应排序）。</summary>
    private List<BinaryFeature> Detect(StitchFrame frame, OrbPyramidPlan pyramid, StitchSampling? sampling)
    {
        int candidateLimit = Math.Max(_options.MaxFeatures * 2, _options.MaxFeatures);
        OrbImage image = sampling != null ? sampling.ApplyFrame(frame) : OrbImage.Grayscale(frame);
        OrbDetection detection = StitchOrb.Detect(image, candidateLimit, pyramid);

        var ranked = new List<RankedKeypoint>(detection.Keypoints.Count);
        foreach (OrbKeypoint keypoint in detection.Keypoints)
        {
            (float x, float y) = sampling != null
                ? sampling.SourceCoordinates(keypoint.X, keypoint.Y)
                : (keypoint.X, keypoint.Y);
            int? tile = _layout.Index(x, y);
            if (tile == null) continue;
            ranked.Add(new RankedKeypoint(MathF.Max(keypoint.Response, 0.001f), tile.Value, keypoint));
        }

        ranked.Sort(static (left, right) =>
        {
            int compare = right.Score.CompareTo(left.Score);
            if (compare != 0) return compare;
            compare = left.Keypoint.X.CompareTo(right.Keypoint.X);
            if (compare != 0) return compare;
            compare = left.Keypoint.Y.CompareTo(right.Keypoint.Y);
            if (compare != 0) return compare;
            compare = left.Keypoint.Octave.CompareTo(right.Keypoint.Octave);
            if (compare != 0) return compare;
            compare = left.Keypoint.Response.CompareTo(right.Keypoint.Response);
            if (compare != 0) return compare;
            return left.Keypoint.Angle.CompareTo(right.Keypoint.Angle);
        });

        var tileCounts = new int[_layout.Count];
        var selected = new List<OrbKeypoint>(Math.Min(_options.MaxFeatures, ranked.Count));
        foreach (RankedKeypoint candidate in ranked)
        {
            if (selected.Count >= _options.MaxFeatures) break;
            if (tileCounts[candidate.Tile] >= MaxFeaturesPerTile) continue;
            tileCounts[candidate.Tile]++;
            selected.Add(candidate.Keypoint);
        }

        List<OrbFeature> features = detection.Compute(selected);
        var result = new List<BinaryFeature>(features.Count);
        foreach (OrbFeature feature in features)
        {
            (float x, float y) = sampling != null
                ? sampling.SourceCoordinates(feature.Keypoint.X, feature.Keypoint.Y)
                : (feature.Keypoint.X, feature.Keypoint.Y);
            result.Add(new BinaryFeature(x, y, feature.Descriptor));
        }

        return result;
    }

    private List<MatchObservation> MutualObservations(List<BinaryFeature> reference, List<BinaryFeature> incoming)
    {
        DescriptorMatch?[] forward = MatchDescriptors(reference, incoming);
        DescriptorMatch?[] reverse = MatchDescriptors(incoming, reference);

        var observations = new List<MatchObservation>();
        for (int index = 0; index < forward.Length; index++)
        {
            DescriptorMatch? matched = forward[index];
            if (matched is not DescriptorMatch forwardMatch) continue;

            int incomingIndex = forwardMatch.TrainIndex;
            if (incomingIndex < 0 || incomingIndex >= reverse.Length) continue;
            DescriptorMatch? reverseMatch = reverse[incomingIndex];
            if (reverseMatch is not DescriptorMatch back || back.TrainIndex != index) continue;

            BinaryFeature feature = reference[index];
            BinaryFeature incomingFeature = incoming[incomingIndex];
            int dx = RoundCoordinate(incomingFeature.X) - RoundCoordinate(feature.X);
            int dy = RoundCoordinate(incomingFeature.Y) - RoundCoordinate(feature.Y);
            if (Math.Abs(CrossDelta(dx, dy)) > MaxCrossAxisDelta) continue;

            int? tile = _layout.Index(feature.X, feature.Y);
            if (tile == null) continue;

            observations.Add(new MatchObservation(
                forwardMatch.Distance,
                feature.X, feature.Y,
                incomingFeature.X, incomingFeature.Y,
                tile.Value, dx, dy));
        }

        return observations;
    }

    /// <summary>每个 query 在 train 里找最近两个，再按 Lowe 比例与最大距离筛。</summary>
    private static DescriptorMatch?[] MatchDescriptors(List<BinaryFeature> queries, List<BinaryFeature> train)
    {
        var result = new DescriptorMatch?[queries.Count];
        if (train.Count == 0) return result;

        Parallel.For(0, queries.Count, index =>
        {
            BinaryFeature query = queries[index];
            int firstIndex = -1;
            int secondIndex = -1;
            uint firstDistance = uint.MaxValue;
            uint secondDistance = uint.MaxValue;

            for (int candidate = 0; candidate < train.Count; candidate++)
            {
                uint distance = HammingDistance(query.Descriptor, train[candidate].Descriptor);
                if (firstIndex < 0 || distance < firstDistance || (distance == firstDistance && candidate < firstIndex))
                {
                    secondIndex = firstIndex;
                    secondDistance = firstDistance;
                    firstIndex = candidate;
                    firstDistance = distance;
                }
                else if (secondIndex < 0 || distance < secondDistance || (distance == secondDistance && candidate < secondIndex))
                {
                    secondIndex = candidate;
                    secondDistance = distance;
                }
            }

            if (secondIndex < 0) return;
            if (firstDistance > MaxHammingDistance) return;
            if (firstDistance >= LoweRatio * secondDistance) return;
            result[index] = new DescriptorMatch(firstIndex, firstDistance);
        });

        return result;
    }

    private static uint HammingDistance(byte[] left, byte[] right)
    {
        ulong first = BinaryPrimitives.ReadUInt64LittleEndian(left) ^ BinaryPrimitives.ReadUInt64LittleEndian(right);
        ulong second = BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(8)) ^ BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(8));
        ulong third = BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(16)) ^ BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(16));
        ulong fourth = BinaryPrimitives.ReadUInt64LittleEndian(left.AsSpan(24)) ^ BinaryPrimitives.ReadUInt64LittleEndian(right.AsSpan(24));
        return (uint)(BitOperations.PopCount(first) + BitOperations.PopCount(second)
                      + BitOperations.PopCount(third) + BitOperations.PopCount(fourth));
    }

    // ============ 候选与打分 ============

    private static int RoundCoordinate(float value) => (int)(value + 0.5f);

    private int PrimaryDelta(int dx, int dy) => _axis == StitchAxis.Vertical ? dy : dx;

    private int CrossDelta(int dx, int dy) => _axis == StitchAxis.Vertical ? dx : dy;

    /// <summary>把互匹配观测按「沿轴位移」聚成模式，取支持度最高的几个（点 0 永远在候选里）。</summary>
    private List<int> CandidateOffsets(List<MatchObservation> observations)
    {
        var exact = new SortedDictionary<int, float>();
        foreach (MatchObservation observation in observations)
        {
            int primary = PrimaryDelta(observation.Dx, observation.Dy);
            int offset = Math.Abs(primary) < 2 ? 0 : primary;
            if (Math.Abs(offset) > _maximumShift) continue;

            float weight = Math.Clamp(1f - observation.Distance / MaxHammingDistance, 0.05f, 1f);
            exact.TryGetValue(offset, out float current);
            exact[offset] = current + weight;
        }

        var modes = new List<(int Offset, float Support)>(exact.Count);
        foreach ((int candidate, float _) in exact)
        {
            float support = 0f;
            foreach ((int offset, float weight) in exact)
            {
                if (offset >= candidate - InlierTolerance && offset <= candidate + InlierTolerance)
                    support += weight;
            }

            modes.Add((candidate, support));
        }

        modes.Sort(static (left, right) =>
        {
            int compare = right.Support.CompareTo(left.Support);
            if (compare != 0) return compare;
            compare = Math.Abs(left.Offset).CompareTo(Math.Abs(right.Offset));
            if (compare != 0) return compare;
            return left.Offset.CompareTo(right.Offset);
        });

        var selected = new List<int>(MaxCandidates) { 0 };
        foreach ((int candidate, float _) in modes)
        {
            if (selected.Count >= MaxCandidates) break;
            bool distinct = true;
            foreach (int existing in selected)
            {
                if (Math.Abs(existing - candidate) <= InlierTolerance)
                {
                    distinct = false;
                    break;
                }
            }

            if (distinct) selected.Add(candidate);
        }

        return selected;
    }

    private static float AverageSimilarity(SimilarityMap map, MatchObservation observation)
    {
        float reference = map.AtPoint(observation.ReferenceX, observation.ReferenceY) ?? 0.5f;
        float incoming = map.AtPoint(observation.IncomingX, observation.IncomingY) ?? reference;
        return 0.5f * (reference + incoming);
    }

    /// <summary>
    /// 配准误差：把每个块的重叠误差按纹理与配准证据加权，丢掉最差的那四分之一
    /// （动态贴片 / 视频块不该决定位移）。
    /// </summary>
    private static (float ZeroError, float CandidateError) AlignmentErrors(
        int offset,
        SimilarityMap direct,
        SimilarityMap zeroAlignment,
        SimilarityMap compensated)
    {
        var samples = new List<(float Candidate, float Zero, float Weight)>();
        for (int tile = 0; tile < direct.Layout.Count; tile++)
        {
            float? directSimilarity = direct.AtTile(tile);
            float? zeroSimilarity = zeroAlignment.AtTile(tile);
            float? candidateSimilarity = compensated.AtTile(tile);
            if (directSimilarity is not float directValue ||
                zeroSimilarity is not float zeroValue ||
                candidateSimilarity is not float candidateValue)
            {
                continue;
            }

            float pairEvidence = offset == 0 ? directValue : 1f - directValue;
            float texture = MathF.Max(direct.TextureAt(tile), compensated.TextureAt(tile));
            if (texture < 0.05f) continue;

            float weight = (0.1f + 0.9f * pairEvidence) * (0.1f + 0.9f * texture);
            samples.Add((1f - candidateValue, 1f - zeroValue, weight));
        }

        if (samples.Count == 0) return (1f, 1f);

        samples.Sort(static (left, right) => left.Candidate.CompareTo(right.Candidate));
        float retainedWeight = 0f;
        foreach ((float _, float _, float weight) in samples) retainedWeight += weight;
        retainedWeight *= 0.75f;

        float usedWeight = 0f;
        float zeroError = 0f;
        float candidateError = 0f;
        foreach ((float candidateSample, float zeroSample, float weight) in samples)
        {
            float used = MathF.Min(weight, MathF.Max(retainedWeight - usedWeight, 0f));
            if (used <= 0f) break;
            candidateError += candidateSample * used;
            zeroError += zeroSample * used;
            usedWeight += used;
        }

        return usedWeight > 0f ? (zeroError / usedWeight, candidateError / usedWeight) : (1f, 1f);
    }

    private CandidateDiagnostics ScoreCandidate(
        int offset,
        List<MatchObservation> observations,
        SimilarityMap direct,
        SimilarityMap zeroAlignment,
        SimilarityMap compensated)
    {
        var eligibleByTile = new float[direct.Layout.Count];
        var inlierByTile = new float[direct.Layout.Count];
        int rawInliers = 0;

        foreach (MatchObservation observation in observations)
        {
            float directSimilarity = AverageSimilarity(direct, observation);
            float pairEvidence = offset == 0 ? directSimilarity : 1f - directSimilarity;
            float descriptorQuality = Math.Clamp(1f - observation.Distance / MaxHammingDistance, 0.05f, 1f);
            float weight = descriptorQuality * (0.1f + 0.9f * pairEvidence);

            eligibleByTile[observation.ReferenceTile] += weight;
            if (Math.Abs(PrimaryDelta(observation.Dx, observation.Dy) - offset) <= InlierTolerance)
            {
                inlierByTile[observation.ReferenceTile] += weight;
                rawInliers = Math.Min(rawInliers + 1, int.MaxValue);
            }
        }

        float eligibleSupport = 0f;
        float weightedSupport = 0f;
        int inlierTiles = 0;
        for (int tile = 0; tile < eligibleByTile.Length; tile++)
        {
            eligibleSupport += MathF.Min(eligibleByTile[tile], 1f);
            weightedSupport += MathF.Min(inlierByTile[tile], 1f);
            if (inlierByTile[tile] >= 0.05f) inlierTiles++;
        }

        float weightedInlierShare = eligibleSupport > 0f
            ? Math.Clamp(weightedSupport / eligibleSupport, 0f, 1f)
            : 0f;
        float spatialCoverage = Math.Clamp(inlierTiles / 12f, 0f, 1f);
        (float zeroError, float alignmentError) = AlignmentErrors(offset, direct, zeroAlignment, compensated);
        float residualGain = offset == 0 || zeroError <= float.Epsilon
            ? 0f
            : Math.Clamp((zeroError - alignmentError) / zeroError, 0f, 1f);
        float score = 0.6f * weightedInlierShare + 0.25f * residualGain + 0.15f * spatialCoverage;

        return new CandidateDiagnostics
        {
            Offset = offset,
            RawInliers = rawInliers,
            InlierTiles = inlierTiles,
            WeightedSupport = weightedSupport,
            WeightedInlierShare = weightedInlierShare,
            SpatialCoverage = spatialCoverage,
            AlignmentError = alignmentError,
            ResidualGain = residualGain,
            Score = score,
        };
    }

    // ============ 亚像素细化 ============

    private static byte LuminanceAt(StitchFrame frame, int x, int y)
    {
        int offset = (y * frame.Width + x) * StitchFrame.Channels;
        byte[] pixels = frame.Pixels;
        // Bgra32：byte0 = B、1 = G、2 = R。
        return (byte)((77 * pixels[offset + 2] + 150 * pixels[offset + 1] + 29 * pixels[offset] + 128) >> 8);
    }

    private static int LuminanceGradient(StitchFrame frame, int x, int y)
    {
        int horizontal = Math.Abs(LuminanceAt(frame, x + 1, y) - LuminanceAt(frame, x - 1, y));
        int vertical = Math.Abs(LuminanceAt(frame, x, y + 1) - LuminanceAt(frame, x, y - 1));
        return horizontal + vertical;
    }

    /// <summary>一块观测在 ±2 像素候选上的平均误差（亮度 + 梯度，纹理点才计）。</summary>
    private float[]? RefinementTileErrors(
        StitchFrame reference,
        StitchFrame incoming,
        List<int> offsets,
        List<MatchObservation> group)
    {
        int width = incoming.Width;
        int height = incoming.Height;
        int first = offsets[0];
        int last = offsets[^1];
        var sums = new long[RefinementOffsetCount];
        int samples = 0;

        int taken = 0;
        foreach (MatchObservation observation in group)
        {
            if (taken >= MaxRefinementPatchesPerTile) break;
            taken++;

            int x = RoundCoordinate(observation.IncomingX);
            int y = RoundCoordinate(observation.IncomingY);
            int left, top, right, bottom;
            if (_axis == StitchAxis.Vertical)
            {
                (left, top, right, bottom) = (x, y - last, x, y - first);
            }
            else
            {
                (left, top, right, bottom) = (x - last, y, x - first, y);
            }

            const int Border = RefinementPatchRadius + 1;
            if (x < Border || y < Border || x >= width - Border || y >= height - Border ||
                left < Border || top < Border || right >= width - Border || bottom >= height - Border)
            {
                continue;
            }

            for (int dy = -RefinementPatchRadius; dy <= RefinementPatchRadius; dy += 2)
            {
                for (int dx = -RefinementPatchRadius; dx <= RefinementPatchRadius; dx += 2)
                {
                    int sampleX = x + dx;
                    int sampleY = y + dy;
                    int incomingGradient = LuminanceGradient(incoming, sampleX, sampleY);
                    // 纹理门槛不能随候选位移变：只按 incoming 的梯度筛点。
                    if (incomingGradient < 16) continue;

                    byte incomingValue = LuminanceAt(incoming, sampleX, sampleY);
                    for (int index = 0; index < offsets.Count; index++)
                    {
                        int offset = offsets[index];
                        int referenceX = _axis == StitchAxis.Vertical ? sampleX : sampleX - offset;
                        int referenceY = _axis == StitchAxis.Vertical ? sampleY - offset : sampleY;
                        byte value = LuminanceAt(reference, referenceX, referenceY);
                        int gradient = LuminanceGradient(reference, referenceX, referenceY);
                        int error = Math.Abs(value - incomingValue) + Math.Abs(gradient - incomingGradient) / 2;
                        sums[index] += Math.Min(error, 255);
                    }

                    samples++;
                }
            }
        }

        if (samples < 4) return null;

        var result = new float[RefinementOffsetCount];
        for (int index = 0; index < result.Length; index++)
            result[index] = sums[index] / (samples * 255f);
        return result;
    }

    /// <summary>在最强候选的 ±2 邻域里取误差最小的偏移；证据太散就保持粗估（并请求全分辨率重试）。</summary>
    private (int Offset, float Error) RefineMotionOffset(
        StitchFrame reference,
        StitchFrame incoming,
        int strongestMode,
        List<MatchObservation> observations)
    {
        var offsets = new List<int>(RefinementOffsetCount);
        for (int offset = strongestMode - InlierTolerance; offset <= strongestMode + InlierTolerance; offset++)
        {
            if (Math.Abs(offset) <= _maximumShift) offsets.Add(offset);
        }

        if (offsets.Count == 0) return (strongestMode, 1f);

        var inliers = new List<MatchObservation>();
        foreach (MatchObservation observation in observations)
        {
            if (Math.Abs(PrimaryDelta(observation.Dx, observation.Dy) - strongestMode) <= InlierTolerance)
                inliers.Add(observation);
        }

        // 保留空间分布：按块分组，每块只用描述子最近的两个补丁。
        inliers.Sort(static (left, right) =>
        {
            int compare = left.ReferenceTile.CompareTo(right.ReferenceTile);
            return compare != 0 ? compare : left.Distance.CompareTo(right.Distance);
        });

        var groups = new List<List<MatchObservation>>();
        foreach (MatchObservation observation in inliers)
        {
            if (groups.Count == 0 || groups[^1][0].ReferenceTile != observation.ReferenceTile)
                groups.Add(new List<MatchObservation>());
            groups[^1].Add(observation);
        }

        var tileErrors = new List<float[]>();
        foreach (List<MatchObservation> group in groups)
        {
            float[]? errors = RefinementTileErrors(reference, incoming, offsets, group);
            if (errors != null) tileErrors.Add(errors);
        }

        if (tileErrors.Count < MinInlierTiles) return (strongestMode, 1f);

        int retained = (tileErrors.Count * 3 + 3) / 4;
        int bestIndex = 0;
        float bestError = float.MaxValue;
        for (int index = 0; index < offsets.Count; index++)
        {
            var errors = new float[tileErrors.Count];
            for (int tile = 0; tile < tileErrors.Count; tile++)
                errors[tile] = tileErrors[tile][index];
            Array.Sort(errors);

            float sum = 0f;
            for (int i = 0; i < retained; i++) sum += errors[i];
            float error = sum / retained;

            if (error < bestError ||
                (error == bestError &&
                 Math.Abs(offsets[index] - strongestMode) < Math.Abs(offsets[bestIndex] - strongestMode)))
            {
                bestIndex = index;
                bestError = error;
            }
        }

        return (offsets[bestIndex], bestError);
    }

    // ============ 评估 ============

    private EvaluatedMotion Evaluate(EvaluationImages images, FeatureEvidence evidence)
    {
        float directSimilarity = images.Direct.Mean();
        var diagnostics = new MotionDiagnostics
        {
            Stage = MotionStage.EmptyDescriptors,
            ReferenceKeypoints = evidence.ReferenceFeatures,
            IncomingKeypoints = evidence.IncomingFeatures,
            DirectSimilarity = directSimilarity,
        };

        if (evidence.Observations.Count == 0)
            return EvaluatedMotion.Rejected(directSimilarity < 0.6f, 0f, diagnostics);

        List<MatchObservation> observations = evidence.Observations;
        diagnostics.MutualMatches = observations.Count;

        List<int> candidates = CandidateOffsets(observations);
        if (candidates.Count == 0)
        {
            diagnostics.Stage = MotionStage.NoCandidates;
            return EvaluatedMotion.Rejected(directSimilarity < 0.6f, 0f, diagnostics);
        }

        var scored = new List<CandidateDiagnostics>(candidates.Count);
        foreach (int offset in candidates)
        {
            SimilarityMap compensated = offset == 0
                ? images.ZeroAlignment
                : SimilarityMap.Between(images.ReferenceGray, images.IncomingGray, _layout, _axis, offset);
            scored.Add(ScoreCandidate(offset, observations, images.Direct, images.ZeroAlignment, compensated));
        }

        scored.Sort(static (left, right) =>
        {
            int compare = right.Score.CompareTo(left.Score);
            if (compare != 0) return compare;
            compare = Math.Abs(left.Offset).CompareTo(Math.Abs(right.Offset));
            if (compare != 0) return compare;
            return left.Offset.CompareTo(right.Offset);
        });

        int strongestMode = scored[0].Offset;
        if (strongestMode != 0)
        {
            (int refinedOffset, float preciseError) = RefineMotionOffset(
                images.Reference, images.Incoming, strongestMode, observations);

            CandidateDiagnostics refined;
            int found = scored.FindIndex(candidate => candidate.Offset == refinedOffset);
            if (found >= 0)
            {
                refined = scored[found];
                scored.RemoveAt(found);
            }
            else
            {
                SimilarityMap compensated = SimilarityMap.Between(
                    images.ReferenceGray, images.IncomingGray, _layout, _axis, refinedOffset);
                refined = ScoreCandidate(refinedOffset, observations, images.Direct, images.ZeroAlignment, compensated);
            }

            refined.PreciseAlignmentError = preciseError;
            scored.Insert(0, refined);
        }

        float bestScore = scored[0].Score;
        float secondScore = 0f;
        foreach (CandidateDiagnostics candidate in scored)
        {
            if (Math.Abs(candidate.Offset - scored[0].Offset) > InlierTolerance)
            {
                secondScore = candidate.Score;
                break;
            }
        }

        float margin = bestScore > float.Epsilon
            ? Math.Clamp((bestScore - secondScore) / bestScore, 0f, 1f)
            : 0f;

        CandidateDiagnostics best = scored[0];
        float confidence = Math.Clamp(
            0.40f * best.WeightedInlierShare +
            0.25f * best.SpatialCoverage +
            0.20f * best.ResidualGain +
            0.15f * margin,
            0f, 1f);

        diagnostics.SelectedOffset = best.Offset;
        diagnostics.Candidates.Clear();
        diagnostics.Candidates.AddRange(scored);

        if (best.Offset == 0)
        {
            diagnostics.Stage = MotionStage.SelectedNoMotion;
            return new EvaluatedMotion(MotionEstimate.NoMotion(confidence, diagnostics), false, margin);
        }

        bool accepted = best.RawInliers >= MinInlierMatches
                        && best.InlierTiles >= MinInlierTiles
                        && best.ResidualGain >= MinResidualGain
                        && confidence >= _options.MinConfidence;
        if (accepted)
        {
            diagnostics.Stage = MotionStage.Selected;
            return new EvaluatedMotion(MotionEstimate.Motion(best.Offset, confidence, diagnostics), false, margin);
        }

        bool sceneCut = directSimilarity < 0.5f &&
                        scored.TrueForAll(candidate => candidate.AlignmentError > 0.6f);
        diagnostics.Stage = MotionStage.LowConfidence;
        return EvaluatedMotion.Rejected(sceneCut, confidence, diagnostics);
    }

    /// <summary>连续三次被拒 = 换页（<c>SceneCut</c>）；中间只要有一次成功就清零。</summary>
    private MotionEstimate Commit(EvaluatedMotion evaluated)
    {
        if (evaluated.Estimate.Kind != MotionOutcomeKind.Indeterminate)
        {
            _sceneCutStreak = 0;
            return evaluated.Estimate;
        }

        if (evaluated.SceneCut)
            _sceneCutStreak = Math.Min(_sceneCutStreak + 1, 255);
        else
            _sceneCutStreak = 0;

        if (_sceneCutStreak >= 3)
        {
            _sceneCutStreak = 0;
            evaluated.Estimate.Diagnostics.Stage = MotionStage.SceneCut;
        }

        return evaluated.Estimate;
    }

    private readonly struct RankedKeypoint
    {
        public RankedKeypoint(float score, int tile, OrbKeypoint keypoint)
        {
            Score = score;
            Tile = tile;
            Keypoint = keypoint;
        }

        public float Score { get; }

        public int Tile { get; }

        public OrbKeypoint Keypoint { get; }
    }

    private readonly struct EvaluationImages
    {
        public EvaluationImages(
            StitchFrame reference,
            StitchFrame incoming,
            GrayImage referenceGray,
            GrayImage incomingGray,
            SimilarityMap direct,
            SimilarityMap zeroAlignment)
        {
            Reference = reference;
            Incoming = incoming;
            ReferenceGray = referenceGray;
            IncomingGray = incomingGray;
            Direct = direct;
            ZeroAlignment = zeroAlignment;
        }

        public StitchFrame Reference { get; }

        public StitchFrame Incoming { get; }

        public GrayImage ReferenceGray { get; }

        public GrayImage IncomingGray { get; }

        public SimilarityMap Direct { get; }

        public SimilarityMap ZeroAlignment { get; }
    }

    private sealed class EvaluatedMotion
    {
        public EvaluatedMotion(MotionEstimate estimate, bool sceneCut, float margin)
        {
            Estimate = estimate;
            SceneCut = sceneCut;
            Margin = margin;
        }

        public MotionEstimate Estimate { get; }

        public bool SceneCut { get; }

        public float Margin { get; }

        public static EvaluatedMotion Rejected(bool sceneCut, float confidence, MotionDiagnostics diagnostics) =>
            new(MotionEstimate.Indeterminate(confidence, diagnostics), sceneCut, 0f);

        /// <summary>缩略图上的粗估不够用：没运动、没有领先幅度，或配准误差还没降到 1 像素以下。</summary>
        public bool NeedsFullResolution() =>
            Estimate.Kind != MotionOutcomeKind.Motion ||
            Margin <= 0f ||
            Estimate.Diagnostics.Candidates.Count == 0 ||
            Estimate.Diagnostics.Candidates[0].PreciseAlignmentError is not float error ||
            error >= 1f;
    }
}
