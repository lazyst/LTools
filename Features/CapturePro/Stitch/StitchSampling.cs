using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>
/// 把帧沿拼接方向按面积分箱降采样的计划。
/// </summary>
/// <remarks>
/// 移植自 <c>snow-stitch-images/src/sampling.rs</c>。沿轴超过 <see cref="FullResolutionExtent"/>
/// 时把一维长度压到约 512（面积守恒），特征点检测在这张缩小的图上做，把坐标再映回原图。
/// 权重用整数重叠长度表示、除以同一个分母，所以每个输出像素拿到的源权重恰好一样，
/// 不会有浮点边界误差。
/// </remarks>
internal sealed class StitchSampling
{
    /// <summary>沿轴不超过这个长度就保持全分辨率。</summary>
    public const int FullResolutionExtent = 512;

    private readonly int[] _first;
    private readonly int[] _start;
    private readonly int[] _count;
    private readonly long[] _weights;

    private StitchSampling(StitchAxis axis, int source, int target, int[] first, int[] start, int[] count, long[] weights)
    {
        Axis = axis;
        Source = source;
        Target = target;
        _first = first;
        _start = start;
        _count = count;
        _weights = weights;
    }

    public StitchAxis Axis { get; }

    /// <summary>沿轴的源长度（纵向 = 宽，横向 = 高）。</summary>
    public int Source { get; }

    /// <summary>沿轴的目标长度。</summary>
    public int Target { get; }

    public bool Reduced => Target < Source;

    public static StitchSampling Create(StitchFrame frame, StitchAxis axis) =>
        Create(axis, frame.Width, frame.Height);

    public static StitchSampling Create(StitchAxis axis, int width, int height)
    {
        int source = axis == StitchAxis.Vertical ? width : height;
        return Build(axis, source);
    }

    private static StitchSampling Build(StitchAxis axis, int source)
    {
        int target = RetainedExtent(source);
        if (target >= source)
            return new StitchSampling(axis, source, target, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<long>());

        long sourceL = source;
        long targetL = target;
        var first = new int[target];
        var start = new int[target];
        var count = new int[target];
        var weights = new List<long>();
        for (int index = 0; index < target; index++)
        {
            long begin = index * sourceL;
            long end = begin + sourceL;
            int binFirst = (int)(begin / targetL);
            first[index] = binFirst;
            start[index] = weights.Count;
            long lastPixel = (end + targetL - 1) / targetL;
            for (long pixel = binFirst; pixel < lastPixel; pixel++)
            {
                long lo = Math.Max(begin, pixel * targetL);
                long hi = Math.Min(end, (pixel + 1) * targetL);
                weights.Add(hi - lo);
            }

            count[index] = weights.Count - start[index];
        }

        return new StitchSampling(axis, source, target, first, start, count, weights.ToArray());
    }

    private static int RetainedExtent(int extent)
    {
        if (extent <= FullResolutionExtent) return extent;

        long area = (long)FullResolutionExtent * extent;
        long root = (long)Math.Sqrt(area);
        while (root * root > area) root--;
        while ((root + 1) * (root + 1) <= area) root++;
        return (int)(root + (root * root != area ? 1 : 0));
    }

    /// <summary>降采样后这张图的宽高。</summary>
    public (int Width, int Height) Dimensions(StitchFrame frame) => Dimensions(frame.Width, frame.Height);

    public (int Width, int Height) Dimensions(int width, int height) =>
        Axis == StitchAxis.Vertical ? (Target, height) : (width, Target);

    /// <summary>把降采样图上的坐标映回源图坐标。</summary>
    public (float X, float Y) SourceCoordinates(float x, float y)
    {
        if (!Reduced) return (x, y);

        float ratio = (float)Source / Target;
        float Map(float value) => (value + 0.5f) * ratio - 0.5f;
        return Axis == StitchAxis.Vertical ? (Map(x), y) : (x, Map(y));
    }

    /// <summary>把一帧降采样成灰度图（先转灰度再按面积分箱，等于对灰度做平均）。</summary>
    public OrbImage ApplyFrame(StitchFrame frame)
    {
        if (!Reduced) return OrbImage.Grayscale(frame);

        byte[] gray = OrbImage.GrayscaleBytes(frame);
        long source = Source;
        if (Axis == StitchAxis.Vertical)
        {
            int width = Target;
            int height = frame.Height;
            var output = new byte[width * height];
            int frameWidth = frame.Width;
            Parallel.For(0, height, y =>
            {
                int row = y * frameWidth;
                int target = y * width;
                for (int x = 0; x < width; x++)
                {
                    long sum = 0;
                    int binFirst = _first[x];
                    int weightStart = _start[x];
                    int weightCount = _count[x];
                    for (int k = 0; k < weightCount; k++)
                        sum += gray[row + binFirst + k] * _weights[weightStart + k];
                    output[target + x] = RoundAverage(sum, source);
                }
            });
            return new OrbImage(width, height, output);
        }
        else
        {
            int width = frame.Width;
            int height = Target;
            var output = new byte[width * height];
            Parallel.For(0, height, y =>
            {
                int binFirst = _first[y];
                int weightStart = _start[y];
                int weightCount = _count[y];
                int target = y * width;
                for (int x = 0; x < width; x++)
                {
                    long sum = 0;
                    for (int k = 0; k < weightCount; k++)
                        sum += gray[(binFirst + k) * width + x] * _weights[weightStart + k];
                    output[target + x] = RoundAverage(sum, source);
                }
            });
            return new OrbImage(width, height, output);
        }
    }

    private static byte RoundAverage(long sum, long source) => (byte)((sum + source / 2) / source);
}
