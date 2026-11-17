using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>一张 8 位灰度图（特征检测 / 描述子都在这上面做）。</summary>
internal sealed class OrbImage
{
    public OrbImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height)
            throw new ArgumentException("灰度缓冲区长度不对", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>把一帧转成灰度（0.299 R + 0.587 G + 0.114 B，四舍五入）。</summary>
    public static OrbImage Grayscale(StitchFrame frame) =>
        new(frame.Width, frame.Height, GrayscaleBytes(frame));

    public static byte[] GrayscaleBytes(StitchFrame frame)
    {
        byte[] source = frame.Pixels;
        var gray = new byte[frame.Width * frame.Height];
        Parallel.For(0, frame.Height, y =>
        {
            int offset = y * frame.Width * StitchFrame.Channels;
            int target = y * frame.Width;
            for (int x = 0; x < frame.Width; x++, offset += StitchFrame.Channels)
            {
                // Bgra32：byte0 = B、1 = G、2 = R。
                float value = 0.299f * source[offset + 2] + 0.587f * source[offset + 1] + 0.114f * source[offset];
                gray[target + x] = (byte)Math.Clamp((int)(value + 0.5f), 0, 255);
            }
        });
        return gray;
    }
}

internal struct OrbKeypoint
{
    public float X;
    public float Y;
    public float Response;
    public float Angle;
    public int Octave;
}

internal sealed class OrbFeature
{
    public required OrbKeypoint Keypoint { get; init; }

    public required byte[] Descriptor { get; init; }
}

/// <summary>
/// 图像金字塔的降采样计划：每层用整数权重做精确双线性缩放（与 OpenCV 的线性缩放一致）。
/// </summary>
/// <remarks>移植自 <c>snow-stitch-images/src/orb.rs</c> 的 <c>PyramidPlan</c> / <c>resize_linear_exact</c>。</remarks>
internal sealed class OrbPyramidPlan
{
    public const int Levels = 8;
    public const float ScaleFactor = 1.2f;

    private readonly List<ResizePlan> _levels = new();

    private OrbPyramidPlan(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<ResizePlan> Resizes => _levels;

    public static OrbPyramidPlan Create(int width, int height)
    {
        var plan = new OrbPyramidPlan(width, height);
        int sourceWidth = width;
        int sourceHeight = height;
        for (int level = 1; level < Levels; level++)
        {
            var (targetWidth, targetHeight) = LevelSize(width, height, level);
            plan._levels.Add(new ResizePlan(
                sourceWidth, sourceHeight, targetWidth, targetHeight,
                StitchOrb.ResizeCoefficients(sourceWidth, targetWidth),
                StitchOrb.ResizeCoefficients(sourceHeight, targetHeight)));
            sourceWidth = targetWidth;
            sourceHeight = targetHeight;
        }

        return plan;
    }

    public static float ScaleForLevel(int level) => (float)Math.Pow(ScaleFactor, level);

    private static (int Width, int Height) LevelSize(int width, int height, int level)
    {
        float inverseScale = 1f / ScaleForLevel(level);
        return (StitchOrb.CvRound(width * inverseScale), StitchOrb.CvRound(height * inverseScale));
    }
}

internal readonly struct ResizeCoefficient
{
    public ResizeCoefficient(int first, int second, int firstWeight, int secondWeight)
    {
        First = first;
        Second = second;
        FirstWeight = firstWeight;
        SecondWeight = secondWeight;
    }

    public int First { get; }

    public int Second { get; }

    public int FirstWeight { get; }

    public int SecondWeight { get; }
}

internal sealed class ResizePlan
{
    public ResizePlan(int sourceWidth, int sourceHeight, int width, int height, ResizeCoefficient[] horizontal, ResizeCoefficient[] vertical)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Width = width;
        Height = height;
        Horizontal = horizontal;
        Vertical = vertical;
    }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    public int Width { get; }

    public int Height { get; }

    public ResizeCoefficient[] Horizontal { get; }

    public ResizeCoefficient[] Vertical { get; }
}

/// <summary>ORB 检测结果：多尺度图像 + 选出的关键点。</summary>
internal sealed class OrbDetection
{
    public OrbDetection(List<OrbImage> levels, List<OrbKeypoint> keypoints)
    {
        Levels = levels;
        Keypoints = keypoints;
    }

    public List<OrbImage> Levels { get; }

    public List<OrbKeypoint> Keypoints { get; }

    /// <summary>给选中的关键点算 256 位描述子（只模糊用得到的层）。</summary>
    public List<OrbFeature> Compute(List<OrbKeypoint> keypoints)
    {
        var required = new bool[Levels.Count];
        foreach (var keypoint in keypoints)
            required[keypoint.Octave] = true;

        var blurred = new OrbImage?[Levels.Count];
        Parallel.For(0, Levels.Count, level =>
        {
            if (required[level]) blurred[level] = StitchOrb.Blur(Levels[level]);
        });

        var features = new List<OrbFeature>(keypoints.Count);
        foreach (var keypoint in keypoints)
        {
            var image = blurred[keypoint.Octave] ?? throw new InvalidOperationException("选中的层没有模糊图");
            features.Add(new OrbFeature
            {
                Keypoint = keypoint,
                Descriptor = StitchOrb.Describe(image, keypoint),
            });
        }

        return features;
    }
}

/// <summary>
/// ORB 特征检测：FAST-9-16 角点 + Harris 排序 + 强度质心方向 + 旋转 BRIEF 描述子。
/// </summary>
/// <remarks>
/// 移植自 <c>snow-stitch-images/src/orb.rs</c>（常量逐一对齐：<c>SCALE_FACTOR = 1.2</c>、<c>LEVELS = 8</c>、
/// <c>EDGE_THRESHOLD = 31</c>、<c>PATCH_SIZE = 31</c>、<c>FAST_THRESHOLD = 20</c>、<c>HARRIS_K = 0.04</c>、
/// <c>HARRIS_BLOCK_SIZE = 7</c>）。这里只保留标量实现；原文件的 AVX2/NEON 分支是同一算法的并行版本，不搬。
/// </remarks>
internal static class StitchOrb
{
    public const int EdgeThreshold = 31;
    private const int PatchSize = 31;
    private const byte FastThreshold = 20;
    private const float HarrisK = 0.04f;
    private const int HarrisBlockSize = 7;

    private static readonly (int X, int Y)[] Circle =
    {
        (0, 3), (1, 3), (2, 2), (3, 1), (3, 0), (3, -1), (2, -2), (1, -3),
        (0, -3), (-1, -3), (-2, -2), (-3, -1), (-3, 0), (-3, 1), (-2, 2), (-1, 3),
    };

    private static readonly float[] BlurKernel =
    {
        BitConverter.Int32BitsToSingle(unchecked((int)0x3d8fafb1)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3e06387e)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3e434a39)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3e5d4ae0)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3e434a39)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3e06387e)),
        BitConverter.Int32BitsToSingle(unchecked((int)0x3d8fafb1)),
    };

    private const string OrbPatternBase64 =
        "CP0JBQQCB/T1CfgCB/QM8wLzAgwB+QEG/vb+/PPz9fjz/fT3CgQLCfP4+Pf1B/cMBwcMBvz7/QDzAvT99wD5BQz6DP/9Bv4M+vP8+AvzDPgEBwUBBf0K/QP5Bgz4+fr+/gv/9vMM+Ar5A/v9/AL9B/b0+gsF9Ab5BfoH/wEABPsJCwvzBAcEDAL/BAT89P4H+Pv59gQLCQwA+AHz8/74Av3+/gP6Cfz3CAwKBwAJAQMH+wv28/r1AAoHDAH6/foMCvcM/PMI+PTzAPj8AwMHCAUHCvn/BwH0A/YFBgL8A/bzAPMF8/n0DPMD9Qj5DPwHBvYMCPf/+fr++wAM9AX5BQP2CPP5+fwF/f7/+QIJBfX18/vz/wYA/wX9BQL88/wM9/r3BvT2+PwKAgz9BwwMDPnz+gX8Cf0EB/8MAvkG+wHzC/QF/Qf++gf4DPnz+fX0Af0MDAL6AwD8A/7z//MBCQcBCPoB/wMMCQEMBv/3/wPz8/YFBwcKDAz7DAkGAwcLBfMGCgL0AgMDCAT6AgYM8wn0CgP4BPkJ9Qz8+gEMAvgG9wf8AgMD/gYDCwAD/Qj4BwgJA/X7+vz2C/sK+/j9DPYF9wAI/wz6BPoG9fYM+AcE/gYH/gD+DPv4+wIH+goM9/P4+Pvz+/4I+Anz9/X3AAH4Af4H/AkB/gH//Av6DPX09/oEAwcHDAUFCggA/AII9wz78wAHAgz/AgEHBQsH9wMFBvjz/PgJ+wn9/fz5/fQGBQgA+Qb6DPMG+/4B9gMKBAEI/P7+AvMC9AwM/vMA+gQBCQP69v37/fP/AQcFDPUE/gX58wn3+wcBCAYH+AcG+fz5AfgL+fjzBvT4AgQDCQr7DAP6+/oHCP0J+AL0Agj1/vYD9PP59/UA9vsF/QsI/vP/DP/4AAnz9fT79v72C/0J/vMC/QMC9/P8APwG/fb8DP75+vX8CQb9BgvzC/sFCwsMBgf7DP7/DAAH/Pj9/vkB+gfz9Pjz+f76+PgF+vf7//wF8wf4CgEFBfMBAArzCQwK/wX4Cvf/CwHz9/36Av/2AQzzAfj2CPUK+gLzA/oH8wz39vb7+fb4+PME+ggFAwwI8/wC/f0F8wr0BPMF//cJ/AMAAwP39AH6AQMCBPj29vYJCPMMDPj0+vsCAgMHCgYL+AYICPT5CvoF/ff9Cf/z/wX9+f0E+P74AwQCDAwC+wMLBvcL8wP/BwwL/wwE/QD9BgT1BAwC/AIB9vr4AfMH9QHzDPXzBgAL8wD/AQTzA/f+9wj6/fP6+P4F9wgKAgcD9//6//8JBQv+C/0M+AMAAwX/BAAKA/oEBfMA9gUFCAwLCAkJ+gf8CPT2BPYJBwMMBAn5Cv4HAAz+//oA9Q==";

    private static readonly (sbyte X, sbyte Y)[] Pattern = DecodePattern();

    private static readonly float DegreesPerRadian = (float)(180.0 / Math.PI);
    private static readonly float AtanP1 = BitConverter.Int32BitsToSingle(unchecked((int)0x3f7ff219)) * DegreesPerRadian;
    private static readonly float AtanP3 = BitConverter.Int32BitsToSingle(unchecked((int)0xbea6d05c)) * DegreesPerRadian;
    private static readonly float AtanP5 = BitConverter.Int32BitsToSingle(unchecked((int)0x3e1f5003)) * DegreesPerRadian;
    private static readonly float AtanP7 = BitConverter.Int32BitsToSingle(unchecked((int)0xbd358fc3)) * DegreesPerRadian;

    /// <summary>检测一帧的特征点（最多 <paramref name="maxFeatures"/> 个，按层配额分配）。</summary>
    public static OrbDetection Detect(OrbImage image, int maxFeatures, OrbPyramidPlan plan)
    {
        int[] quotas = FeatureQuota(maxFeatures);
        var levels = Pyramid(image, plan);
        int[] maximumX = Umax();

        var keypoints = new List<OrbKeypoint>();
        for (int level = 0; level < levels.Count && level < quotas.Length; level++)
        {
            if (quotas[level] == 0) continue;

            OrbImage levelImage = levels[level];
            var fast = Fast9x16WithBorder(levelImage.Width, levelImage.Height, levelImage.Pixels, FastThreshold, EdgeThreshold);
            if (fast.Count == 0) continue;

            var selected = new List<OrbKeypoint>(fast.Count);
            foreach (var item in fast)
            {
                selected.Add(new OrbKeypoint
                {
                    X = item.X,
                    Y = item.Y,
                    Response = item.Response,
                    Angle = -1f,
                    Octave = level,
                });
            }

            FilterBorder(selected, levelImage);
            RetainBest(selected, quotas[level] * 2);
            for (int i = 0; i < selected.Count; i++)
            {
                var keypoint = selected[i];
                HarrisResponse(levelImage, ref keypoint);
                selected[i] = keypoint;
            }

            RetainBest(selected, quotas[level]);
            for (int i = 0; i < selected.Count; i++)
            {
                var keypoint = selected[i];
                Orient(levelImage, ref keypoint, maximumX);
                float scale = OrbPyramidPlan.ScaleForLevel(level);
                keypoint.X *= scale;
                keypoint.Y *= scale;
                selected[i] = keypoint;
            }

            keypoints.AddRange(selected);
        }

        return new OrbDetection(levels, keypoints);
    }

    /// <summary>七抽头可分离模糊（反射边界），描述子用。</summary>
    public static OrbImage Blur(OrbImage image)
    {
        int width = image.Width;
        int height = image.Height;
        byte[] source = image.Pixels;

        var horizontal = new float[width * height];
        Parallel.For(0, height, y =>
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float value = 0f;
                for (int offset = 0; offset < 7; offset++)
                {
                    int sourceX = Reflect101(x + offset - 3, width);
                    value += source[row + sourceX] * BlurKernel[offset];
                }

                horizontal[row + x] = value;
            }
        });

        var output = new byte[width * height];
        Parallel.For(0, height, y =>
        {
            var sources = new int[7];
            for (int s = 0; s < 7; s++)
                sources[s] = Reflect101(y + s - 3, height);

            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                float value = 0f;
                for (int s = 0; s < 7; s++)
                    value += horizontal[sources[s] * width + x] * BlurKernel[s];

                output[row + x] = (byte)Math.Clamp(CvRound(value), 0, 255);
            }
        });

        return new OrbImage(width, height, output);
    }

    /// <summary>旋转 BRIEF 描述子（32 字节 = 256 位）。</summary>
    public static byte[] Describe(OrbImage image, OrbKeypoint keypoint)
    {
        const float radiansPerDegree = (float)(Math.PI / 180.0);
        float scale = 1f / OrbPyramidPlan.ScaleForLevel(keypoint.Octave);
        int centerX = CvRound(keypoint.X * scale);
        int centerY = CvRound(keypoint.Y * scale);
        float radians = keypoint.Angle * radiansPerDegree;
        float cosine = MathF.Cos(radians);
        float sine = MathF.Sin(radians);
        byte[] pixels = image.Pixels;
        int width = image.Width;

        int Sample(int index)
        {
            var point = Pattern[index];
            int x = CvRound(point.X * cosine - point.Y * sine);
            int y = CvRound(point.X * sine + point.Y * cosine);
            return pixels[(centerY + y) * width + centerX + x];
        }

        var bytes = new byte[32];
        for (int index = 0; index < 32; index++)
        {
            int baseIndex = index * 16;
            int value = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                if (Sample(baseIndex + bit * 2) < Sample(baseIndex + bit * 2 + 1))
                    value |= 1 << bit;
            }

            bytes[index] = (byte)value;
        }

        return bytes;
    }

    // ---- 内部实现 ----

    private static (sbyte X, sbyte Y)[] DecodePattern()
    {
        byte[] bytes = Convert.FromBase64String(OrbPatternBase64);
        if (bytes.Length != 1024) throw new InvalidOperationException("ORB 模式长度不对");
        var pattern = new (sbyte X, sbyte Y)[512];
        for (int i = 0; i < 512; i++)
            pattern[i] = ((sbyte)bytes[i * 2], (sbyte)bytes[i * 2 + 1]);
        return pattern;
    }

    private static List<OrbImage> Pyramid(OrbImage baseImage, OrbPyramidPlan plan)
    {
        var levels = new List<OrbImage>(OrbPyramidPlan.Levels) { baseImage };
        foreach (var resizePlan in plan.Resizes)
            levels.Add(ResizeLinearExact(levels[^1], resizePlan));
        return levels;
    }

    private static OrbImage ResizeLinearExact(OrbImage source, ResizePlan plan)
    {
        if (source.Width == plan.Width && source.Height == plan.Height) return source;

        int width = plan.Width;
        int height = plan.Height;
        byte[] sourcePixels = source.Pixels;
        var output = new byte[width * height];
        Parallel.For(0, height, targetY =>
        {
            ResizeCoefficient vertical = plan.Vertical[targetY];
            int sourceY = vertical.First;
            int nextY = vertical.Second;
            int row = targetY * width;
            for (int targetX = 0; targetX < width; targetX++)
            {
                ResizeCoefficient horizontal = plan.Horizontal[targetX];
                int sourceX = horizontal.First;
                int nextX = horizontal.Second;
                long top = (long)sourcePixels[sourceY * source.Width + sourceX] * horizontal.FirstWeight
                           + (long)sourcePixels[sourceY * source.Width + nextX] * horizontal.SecondWeight;
                long bottom = (long)sourcePixels[nextY * source.Width + sourceX] * horizontal.FirstWeight
                              + (long)sourcePixels[nextY * source.Width + nextX] * horizontal.SecondWeight;
                long value = top * vertical.FirstWeight + bottom * vertical.SecondWeight;
                output[row + targetX] = (byte)((value + (1 << 15)) >> 16);
            }
        });

        return new OrbImage(width, height, output);
    }

    internal static ResizeCoefficient[] ResizeCoefficients(int sourceLength, int targetLength)
    {
        var coefficients = new ResizeCoefficient[targetLength];
        for (int target = 0; target < targetLength; target++)
        {
            double value = (double)sourceLength / targetLength * (target + 0.5) - 0.5;
            if (value < 0.0)
                coefficients[target] = new ResizeCoefficient(0, 0, 256, 0);
            else if (value >= sourceLength - 1)
                coefficients[target] = new ResizeCoefficient(sourceLength - 1, sourceLength - 1, 256, 0);
            else
            {
                int offset = (int)Math.Floor(value);
                int right = (int)CvRound64((value - offset) * 256.0);
                coefficients[target] = new ResizeCoefficient(offset, offset + 1, 256 - right, right);
            }
        }

        return coefficients;
    }

    private static int[] FeatureQuota(int total)
    {
        float factor = 1f / OrbPyramidPlan.ScaleFactor;
        float desired = total * (1f - factor) / (1f - MathF.Pow(factor, OrbPyramidPlan.Levels));
        var quotas = new int[OrbPyramidPlan.Levels];
        int sum = 0;
        for (int level = 0; level < OrbPyramidPlan.Levels - 1; level++)
        {
            quotas[level] = CvRound(desired);
            sum += quotas[level];
            desired *= factor;
        }

        quotas[OrbPyramidPlan.Levels - 1] = Math.Max(0, total - sum);
        return quotas;
    }

    private static void RetainBest(List<OrbKeypoint> keypoints, int count)
    {
        if (keypoints.Count <= count) return;
        if (count <= 0)
        {
            keypoints.Clear();
            return;
        }

        var responses = new float[keypoints.Count];
        for (int i = 0; i < keypoints.Count; i++) responses[i] = keypoints[i].Response;
        Array.Sort(responses);
        Array.Reverse(responses);
        float threshold = responses[count - 1];
        keypoints.RemoveAll(keypoint => keypoint.Response < threshold);
    }

    private static void FilterBorder(List<OrbKeypoint> keypoints, OrbImage image)
    {
        if (image.Width <= EdgeThreshold * 2 || image.Height <= EdgeThreshold * 2)
        {
            keypoints.Clear();
            return;
        }

        keypoints.RemoveAll(keypoint =>
        {
            int x = (int)keypoint.X;
            int y = (int)keypoint.Y;
            return x < EdgeThreshold || y < EdgeThreshold ||
                   x >= image.Width - EdgeThreshold || y >= image.Height - EdgeThreshold;
        });
    }

    private static void HarrisResponse(OrbImage image, ref OrbKeypoint keypoint)
    {
        int x = CvRound(keypoint.X);
        int y = CvRound(keypoint.Y);
        HarrisMoments(image, x, y, out int horizontal, out int vertical, out int cross);
        float scale = 1f / (4f * HarrisBlockSize * 255f);
        float scaleSqSq = scale * scale * scale * scale;
        keypoint.Response = ((horizontal * (float)vertical - (float)cross * cross)
                             - HarrisK * (horizontal + vertical) * (horizontal + vertical)) * scaleSqSq;
    }

    private static void HarrisMoments(OrbImage image, int x, int y, out int horizontal, out int vertical, out int cross)
    {
        byte[] pixels = image.Pixels;
        int stride = image.Width;
        int radius = HarrisBlockSize / 2;
        horizontal = 0;
        vertical = 0;
        cross = 0;
        for (int offsetY = 0; offsetY < HarrisBlockSize; offsetY++)
        {
            for (int offsetX = 0; offsetX < HarrisBlockSize; offsetX++)
            {
                int center = (y + offsetY - radius) * stride + x + offsetX - radius;
                int intensityX = (pixels[center + 1] - pixels[center - 1]) * 2
                                 + pixels[center - stride + 1] - pixels[center - stride - 1]
                                 + pixels[center + stride + 1] - pixels[center + stride - 1];
                int intensityY = (pixels[center + stride] - pixels[center - stride]) * 2
                                 + pixels[center + stride - 1] - pixels[center - stride - 1]
                                 + pixels[center + stride + 1] - pixels[center - stride + 1];
                horizontal += intensityX * intensityX;
                vertical += intensityY * intensityY;
                cross += intensityX * intensityY;
            }
        }
    }

    private static int[] Umax()
    {
        int half = PatchSize / 2;
        var values = new int[PatchSize / 2 + 2];
        int maximum = (int)Math.Floor(half * MathF.Sqrt(2f) / 2f + 1f);
        int minimum = (int)Math.Ceiling(half * MathF.Sqrt(2f) / 2f);
        for (int offset = 0; offset <= maximum && offset < values.Length; offset++)
            values[offset] = CvRound(MathF.Sqrt(half * half - offset * offset));

        int lower = 0;
        for (int upper = half; upper >= minimum; upper--)
        {
            while (lower + 1 < values.Length && values[lower] == values[lower + 1]) lower++;
            values[upper] = lower;
            lower++;
        }

        return values;
    }

    private static void Orient(OrbImage image, ref OrbKeypoint keypoint, int[] maximumX)
    {
        byte[] pixels = image.Pixels;
        int width = image.Width;
        int x = CvRound(keypoint.X);
        int y = CvRound(keypoint.Y);
        int center = y * width + x;
        int half = PatchSize / 2;
        int momentY = 0;
        int momentX = 0;
        for (int horizontal = -half; horizontal <= half; horizontal++)
            momentX += horizontal * pixels[center + horizontal];

        for (int vertical = 1; vertical <= half; vertical++)
        {
            int horizontalMaximum = maximumX[vertical];
            int sum = 0;
            for (int horizontal = -horizontalMaximum; horizontal <= horizontalMaximum; horizontal++)
            {
                int above = pixels[center + horizontal + vertical * width];
                int below = pixels[center + horizontal - vertical * width];
                sum += above - below;
                momentX += horizontal * (above + below);
            }

            momentY += vertical * sum;
        }

        keypoint.Angle = FastAtan2(momentY, momentX);
    }

    private static float FastAtan2(float y, float x)
    {
        const float epsilon = 2.220446E-16f;
        float absoluteX = MathF.Abs(x);
        float absoluteY = MathF.Abs(y);
        float angle;
        if (absoluteX >= absoluteY)
        {
            float ratio = absoluteY / (absoluteX + epsilon);
            float squared = ratio * ratio;
            angle = (((AtanP7 * squared + AtanP5) * squared + AtanP3) * squared + AtanP1) * ratio;
        }
        else
        {
            float ratio = absoluteX / (absoluteY + epsilon);
            float squared = ratio * ratio;
            angle = 90f - (((AtanP7 * squared + AtanP5) * squared + AtanP3) * squared + AtanP1) * ratio;
        }

        if (x < 0f) angle = 180f - angle;
        if (y < 0f) angle = 360f - angle;
        return angle;
    }

    private readonly struct FastKeypoint
    {
        public FastKeypoint(int x, int y, byte response)
        {
            X = x;
            Y = y;
            Response = response;
        }

        public int X { get; }

        public int Y { get; }

        public byte Response { get; }
    }

    private static List<FastKeypoint> Fast9x16WithBorder(int width, int height, byte[] pixels, byte threshold, int border)
    {
        var keypoints = new List<FastKeypoint>();
        if (border < 3 || width <= border * 2 || height <= border * 2 || pixels.Length != width * height)
            return keypoints;

        int scoreBorder = Math.Max(border - 1, 3);
        byte[] scores = FastScores(width, height, pixels, threshold, scoreBorder);

        for (int y = border; y < height - border; y++)
        {
            for (int x = border; x < width - border; x++)
            {
                byte response = scores[y * width + x];
                if (response == 0) continue;

                bool best = true;
                for (int offsetY = -1; offsetY <= 1 && best; offsetY++)
                {
                    for (int offsetX = -1; offsetX <= 1; offsetX++)
                    {
                        if (offsetX == 0 && offsetY == 0) continue;
                        if (response <= scores[(y + offsetY) * width + (x + offsetX)])
                        {
                            best = false;
                            break;
                        }
                    }
                }

                if (best) keypoints.Add(new FastKeypoint(x, y, response));
            }
        }

        return keypoints;
    }

    private static byte[] FastScores(int width, int height, byte[] pixels, byte threshold, int border)
    {
        var scores = new byte[width * height];
        short limit = threshold;
        Parallel.For(border, height - border, y =>
        {
            int row = y * width;
            for (int x = border; x < width - border; x++)
            {
                if (!MayPassFast9x16(width, pixels, x, y, limit)) continue;
                int? score = Score(width, pixels, x, y, limit);
                if (score is int value) scores[row + x] = (byte)value;
            }
        });
        return scores;
    }

    private static bool MayPassFast9x16(int width, byte[] pixels, int x, int y, short threshold)
    {
        short center = pixels[y * width + x];
        int dark = 0;
        int bright = 0;
        foreach (int index in stackalloc int[] { 0, 4, 8, 12 })
        {
            var (offsetX, offsetY) = Circle[index];
            short sample = pixels[(y + offsetY) * width + x + offsetX];
            short difference = (short)(center - sample);
            if (difference > threshold) dark++;
            if (-difference > threshold) bright++;
        }

        return dark >= 2 || bright >= 2;
    }

    private static int? Score(int width, byte[] pixels, int x, int y, short threshold)
    {
        short center = pixels[y * width + x];
        Span<short> differences = stackalloc short[16];
        for (int i = 0; i < 16; i++)
        {
            var (offsetX, offsetY) = Circle[i];
            short sample = pixels[(y + offsetY) * width + x + offsetX];
            differences[i] = (short)(center - sample);
        }

        uint dark = 0;
        uint bright = 0;
        for (int i = 0; i < 16; i++)
        {
            if (differences[i] > threshold) dark |= 1u << i;
            if (-differences[i] > threshold) bright |= 1u << i;
        }

        if (!HasNinePixelRun(dark) && !HasNinePixelRun(bright)) return null;

        short strongest = StrongestFastArc(differences);
        return strongest > threshold ? (strongest - 1) & 0xFF : (int?)null;
    }

    private static bool HasNinePixelRun(uint mask)
    {
        uint doubled = mask | (mask << 16);
        uint runs = doubled & (doubled >> 1);
        runs &= runs >> 2;
        runs &= runs >> 4;
        runs &= doubled >> 8;
        return (runs & 0xffff) != 0;
    }

    private static short StrongestFastArc(ReadOnlySpan<short> differences)
    {
        short strongest = short.MinValue;
        for (int start = 0; start < 16; start++)
        {
            short dark = short.MaxValue;
            short bright = short.MinValue;
            for (int offset = 0; offset < 9; offset++)
            {
                short difference = differences[(start + offset) % 16];
                if (difference < dark) dark = difference;
                if (difference > bright) bright = difference;
            }

            short candidate = (short)Math.Max(dark, (short)-bright);
            if (candidate > strongest) strongest = candidate;
        }

        return strongest;
    }

    private static int Reflect101(int index, int length)
    {
        if (length <= 1) return 0;
        while (index < 0 || index >= length)
            index = index < 0 ? -index : length * 2 - index - 2;
        return index;
    }

    internal static int CvRound(float value)
    {
        float floor = MathF.Floor(value);
        float fraction = value - floor;
        if (fraction < 0.5f) return (int)floor;
        if (fraction > 0.5f) return (int)floor + 1;
        return ((int)floor % 2 == 0) ? (int)floor : (int)floor + 1;
    }

    private static long CvRound64(double value)
    {
        double floor = Math.Floor(value);
        double fraction = value - floor;
        if (fraction < 0.5) return (long)floor;
        if (fraction > 0.5) return (long)floor + 1;
        return ((long)floor % 2 == 0) ? (long)floor : (long)floor + 1;
    }
}
