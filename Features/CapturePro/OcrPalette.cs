using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>识别文字层用的像素颜色（0–255，无 alpha）。</summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>给 WPF 画刷用。</summary>
    public System.Windows.Media.Color ToColor() =>
        System.Windows.Media.Color.FromRgb(R, G, B);

    /// <summary>三通道最大差（上游 <c>rgbDistance</c>）。</summary>
    public int MaxDistance(Rgb other) => Math.Max(Math.Abs(R - other.R),
        Math.Max(Math.Abs(G - other.G), Math.Abs(B - other.B)));

    /// <summary>4 bit / 通道的分箱号（上游 <c>rgbBin</c>）。</summary>
    public int Bin => ((R >> 4) << 8) | ((G >> 4) << 4) | (B >> 4);

    /// <summary>感知亮度（只用来在估不出字色时挑黑 / 白）。</summary>
    public double Luma => 0.299 * R + 0.587 * G + 0.114 * B;

    /// <summary>确定性排序键：上游用 <c>QRgb</c> 的大小做并列裁决。</summary>
    public int Key => (R << 16) | (G << 8) | B;
}

/// <summary>
/// 一张解好的 BGRA 位图：识别文字层要逐行采样像素估色，<see cref="Bitmap.GetPixel"/> 太慢，
/// 所以整张一次性摊平成数组（1920×1080 ≈ 8 MB）。
/// </summary>
internal sealed class OcrImage
{
    private readonly uint[] _pixels;

    private OcrImage(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    public static OcrImage From(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new uint[bitmap.Width * bitmap.Height];
            var row = new int[bitmap.Width];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, bitmap.Width);
                int at = y * bitmap.Width;
                for (int x = 0; x < bitmap.Width; x++) pixels[at + x] = unchecked((uint)row[x]);
            }

            return new OcrImage(bitmap.Width, bitmap.Height, pixels);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public uint Raw(int x, int y) => _pixels[y * Width + x];

    public int Alpha(int x, int y) => (int)(_pixels[y * Width + x] >> 24);

    public Rgb Pixel(int x, int y)
    {
        uint p = _pixels[y * Width + x];
        return new Rgb((byte)(p >> 16), (byte)(p >> 8), (byte)p);
    }
}

/// <summary>
/// 识别文字层的配色估算：从原图像素里估「这一行的背景色 / 字色」。
/// </summary>
/// <remarks>
/// <para>
/// 逐行照上游 <c>screenshotocrvisuals.cpp</c> 移植（OKLab 感知色 + 分箱 + 均值漂移）：
/// 背景 = 越靠近边缘的平坦像素权重越高（行内文字笔画被 <c>textRegions</c> 排除在外），
/// 字色 = 先把样本按「相对背景的方向」聚成少数几族，取证据最多的一族再做对比度提纯。
/// </para>
/// <para>
/// 为什么不能「取平均色」：ClearType 的彩边、压缩噪点、双色标题都会把平均值拉成中间色，
/// 于是白字黑底会变成灰糊糊一片。上游的做法是**先分族再取代表色**，这里同样照做。
/// </para>
/// </remarks>
internal static class OcrPalette
{
    private const int SamplesPerSide = 24;
    private const int InteriorGrid = 8;
    private const float ColorRadiusSquared = 0.025f * 0.025f;

    private const int TextColorSamples = 2048;
    private const int TextColorBins = 4096;

    internal readonly record struct Lab(double L, double A, double B)
    {
        public double DistanceSquared(Lab other)
        {
            double l = L - other.L, a = A - other.A, b = B - other.B;
            return l * l + a * a + b * b;
        }

        public static bool SameDirection(Lab a, Lab b) => Math.Max(Math.Abs(a.L - b.L),
            Math.Max(Math.Abs(a.A - b.A), Math.Abs(a.B - b.B))) <= 56;
    }

    private readonly record struct Sample(Rgb Rgb, Lab Lab, int Weight);

    /// <summary>四邻域：平坦判定与字色采样共用。</summary>
    private static readonly (int X, int Y)[] Neighbors = { (-1, 0), (1, 0), (0, -1), (0, 1) };

    private struct Bin
    {
        public int Weight;
        public int R;
        public int G;
        public int B;
    }

    // ============ 背景色 ============

    /// <summary>估这一行的背景色（拿不到采样时返回白色，保证预览一定有个实底）。</summary>
    public static Rgb EstimateBackground(OcrImage image, Int32Rect box,
        IReadOnlyList<Int32Rect> textRegions)
    {
        if (box.Width <= 0 || box.Height <= 0) return Rgb.White;

        var samples = new List<Sample>(SamplesPerSide * 12 + InteriorGrid * InteriorGrid);
        var visited = new HashSet<long>();
        double left = box.X, top = box.Y;
        double right = box.X + box.Width, bottom = box.Y + box.Height;

        void Sample(double fx, double fy, bool exterior)
        {
            int x = (int)fx, y = (int)fy;
            if (!image.InBounds(x, y)) return;
            if (exterior && InAny(textRegions, x, y)) return;
            long key = ((long)y << 32) | (uint)x;
            if (!visited.Add(key)) return;

            int alpha = image.Alpha(x, y);
            if (alpha == 0) return;

            Rgb rgb = image.Pixel(x, y);
            bool flat = FlatPixel(image, x, y, rgb);
            samples.Add(new Sample(rgb, Perceptual(rgb), flat ? 4 : 1));
        }

        double shortEdge = Math.Min(box.Width, box.Height);
        double bandWidth = Math.Clamp(shortEdge * 0.12, 2.0, 6.0);

        // 四条边朝内采样：法线一律指向矩形中心
        for (int side = 0; side < 4; side++)
        {
            (double sx, double sy, double ex, double ey, double nx, double ny) edge = side switch
            {
                0 => (left, top, right, top, 0.0, 1.0),
                1 => (right, top, right, bottom, -1.0, 0.0),
                2 => (right, bottom, left, bottom, 0.0, -1.0),
                _ => (left, bottom, left, top, 1.0, 0.0),
            };

            double dx = edge.ex - edge.sx, dy = edge.ey - edge.sy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) continue;

            int positions = (int)Math.Clamp(length / 2.0, 4.0, SamplesPerSide);
            for (int band = 0; band < 3; band++)
            {
                double distance = 1.5 + band * bandWidth / 2.0;
                for (int i = 0; i < positions; i++)
                {
                    double t = (i + 0.5) / positions;
                    Sample(edge.sx + dx * t + edge.nx * distance,
                        edge.sy + dy * t + edge.ny * distance, true);
                }
            }
        }

        int exteriorCount = samples.Count;

        // 行内网格：行列相位错开，别让采样点整条压在字形笔画上
        for (int gy = 0; gy < InteriorGrid; gy++)
        {
            for (int gx = 0; gx < InteriorGrid; gx++)
            {
                double xPhase = (Math.Min(gy, InteriorGrid - 1 - gy) + 0.5) / (InteriorGrid / 2.0);
                double yPhase = (Math.Min(gx, InteriorGrid - 1 - gx) + 0.5) / (InteriorGrid / 2.0);
                double u = (gx + (gx < InteriorGrid / 2 ? xPhase : 1 - xPhase)) / InteriorGrid;
                double v = (gy + (gy < InteriorGrid / 2 ? yPhase : 1 - yPhase)) / InteriorGrid;
                Sample(left + (right - left) * u, top + (bottom - top) * v, false);
            }
        }

        if (samples.Count > exteriorCount)
        {
            var interior = samples.GetRange(exteriorCount, samples.Count - exteriorCount);
            var exterior = samples.GetRange(0, exteriorCount);
            return Dominant(interior, exterior);
        }

        return exteriorCount > 0 ? Dominant(samples, Array.Empty<Sample>()) : Rgb.White;
    }

    /// <summary>相邻四像素都与自己相近（±18）才算「平坦」，权重 ×4。</summary>
    private static bool FlatPixel(OcrImage image, int x, int y, Rgb rgb)
    {
        foreach ((int dx, int dy) in Neighbors)
        {
            int nx = x + dx, ny = y + dy;
            if (!image.InBounds(nx, ny)) continue;
            if (image.Alpha(nx, ny) != 255) return false;
            Rgb other = image.Pixel(nx, ny);
            if (Math.Abs(rgb.R - other.R) > 18 || Math.Abs(rgb.G - other.G) > 18 ||
                Math.Abs(rgb.B - other.B) > 18)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>上游 <c>dominantColor</c>：4 个种子 + 2 步均值漂移 + 外部证据只破并列。</summary>
    private static Rgb Dominant(IReadOnlyList<Sample> samples, IReadOnlyList<Sample> exterior)
    {
        if (samples.Count == 0) return Rgb.White;

        Rgb first = samples[0].Rgb;
        bool allSame = true;
        foreach (Sample sample in samples)
        {
            if (sample.Rgb != first)
            {
                allSame = false;
                break;
            }
        }

        if (allSame) return first;

        var bins = new Bin[4096];
        foreach (Sample sample in samples)
        {
            ref Bin bin = ref bins[sample.Rgb.Bin];
            bin.Weight += sample.Weight;
            bin.R += sample.Rgb.R * sample.Weight;
            bin.G += sample.Rgb.G * sample.Weight;
            bin.B += sample.Rgb.B * sample.Weight;
        }

        var seeds = new[] { -1, -1, -1, -1 };
        for (int i = 0; i < bins.Length; i++)
        {
            if (bins[i].Weight == 0) continue;
            for (int rank = 0; rank < 4; rank++)
            {
                if (seeds[rank] < 0 || bins[i].Weight > bins[seeds[rank]].Weight)
                {
                    for (int move = 3; move > rank; move--) seeds[move] = seeds[move - 1];
                    seeds[rank] = i;
                    break;
                }
            }
        }

        int bestSupport = -1, bestExterior = -1;
        Rgb best = first;

        foreach (int seed in seeds)
        {
            if (seed < 0) break;

            Bin bin = bins[seed];
            Lab lab = Perceptual(new Rgb((byte)(bin.R / bin.Weight), (byte)(bin.G / bin.Weight),
                (byte)(bin.B / bin.Weight)));

            // 固定步数的均值漂移：把压缩噪点跨分箱并到同一个色团里
            for (int step = 0; step < 2; step++)
            {
                int weight = 0, r = 0, g = 0, b = 0;
                foreach (Sample sample in samples)
                {
                    if (sample.Lab.DistanceSquared(lab) > ColorRadiusSquared) continue;
                    weight += sample.Weight;
                    r += sample.Rgb.R * sample.Weight;
                    g += sample.Rgb.G * sample.Weight;
                    b += sample.Rgb.B * sample.Weight;
                }

                if (weight == 0) break;
                lab = Perceptual(new Rgb((byte)((r + weight / 2) / weight),
                    (byte)((g + weight / 2) / weight), (byte)((b + weight / 2) / weight)));
            }

            int support = 0, exteriorSupport = 0;
            double nearest = double.MaxValue;
            Rgb representative = first;

            foreach (Sample sample in samples)
            {
                double distance = sample.Lab.DistanceSquared(lab);
                if (distance > ColorRadiusSquared) continue;
                support += sample.Weight;
                if (distance < nearest ||
                    (distance == nearest && sample.Rgb.Key < representative.Key))
                {
                    nearest = distance;
                    representative = sample.Rgb;
                }
            }

            foreach (Sample sample in exterior)
            {
                if (sample.Lab.DistanceSquared(lab) <= ColorRadiusSquared)
                    exteriorSupport += sample.Weight;
            }

            if (support > bestSupport ||
                (support == bestSupport &&
                 (exteriorSupport > bestExterior ||
                  (exteriorSupport == bestExterior && representative.Key < best.Key))))
            {
                bestSupport = support;
                bestExterior = exteriorSupport;
                best = representative;
            }
        }

        return best;
    }

    // ============ 字色 ============

    private readonly record struct TextSample(Rgb Rgb, Lab Direction, int Distance, int Weight,
        int Band);

    /// <summary>估这一行的字色；一行的底色太花（噪点 &gt; 24）或证据不足时返回 null（保持主题字色）。</summary>
    public static Rgb? EstimateTextColor(OcrImage image, Int32Rect box, Rgb background,
        IReadOnlyList<Int32Rect> textRegions)
    {
        if (box.Width <= 0 || box.Height <= 0) return null;

        double left = box.X, top = box.Y;
        double right = box.X + box.Width, bottom = box.Y + box.Height;
        double centerX = (left + right) / 2, centerY = (top + bottom) / 2;

        // ---- 外圈 2 px 采样：定背景与「这套底子有多花」 ----
        var exterior = new List<Rgb>(64);
        var exteriorCounts = new int[TextColorBins];
        var exteriorRepresentative = new int[TextColorBins];
        int backgroundBin = 0;

        for (int side = 0; side < 4; side++)
        {
            (double sx, double sy, double ex, double ey, double nx, double ny) edge = side switch
            {
                0 => (left, top, right, top, 0.0, 1.0),
                1 => (right, top, right, bottom, -1.0, 0.0),
                2 => (right, bottom, left, bottom, 0.0, -1.0),
                _ => (left, bottom, left, top, 1.0, 0.0),
            };

            double dx = edge.ex - edge.sx, dy = edge.ey - edge.sy;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) continue;

            for (int i = 0; i < 16; i++)
            {
                double t = (i + 0.5) / 16;
                int x = (int)(edge.sx + dx * t + edge.nx * 2);
                int y = (int)(edge.sy + dy * t + edge.ny * 2);
                if (!image.InBounds(x, y)) continue;
                if (InAny(textRegions, x, y)) continue;
                if (image.Alpha(x, y) != 255) continue;

                Rgb rgb = image.Pixel(x, y);
                exterior.Add(rgb);
                int bin = rgb.Bin;
                exteriorCounts[bin]++;
                exteriorRepresentative[bin] = exterior.Count - 1;
                if (exteriorCounts[bin] > exteriorCounts[backgroundBin]) backgroundBin = bin;
            }
        }

        Rgb bg = exterior.Count >= 8
            ? exterior[exteriorRepresentative[backgroundBin]]
            : background;

        var noiseHistogram = new int[256];
        foreach (Rgb rgb in exterior) noiseHistogram[rgb.MaxDistance(bg)]++;

        int noise = exterior.Count >= 8
            ? Quantile(noiseHistogram, (exterior.Count * 3 + 3) / 4)
            : 0;

        // 一块底色模型扛不住花底：宁可退回主题字色，也别把纹理当成字
        if (noise > 24) return null;

        int threshold = Math.Max(12, noise * 2 + 4);

        int boxWidth = box.Width, boxHeight = box.Height;
        int columns = Math.Clamp(
            (int)Math.Min(Math.Ceiling((double)boxWidth),
                Math.Sqrt(TextColorSamples * (double)boxWidth / boxHeight)),
            1, TextColorSamples);
        int rows = Math.Clamp(
            (int)Math.Min(Math.Ceiling((double)boxHeight), (double)(TextColorSamples / columns)),
            1, TextColorSamples / columns);
        bool dense = columns >= Math.Ceiling((double)boxWidth) && rows >= Math.Ceiling((double)boxHeight);

        var samples = new TextSample[TextColorSamples];
        var counts = new int[TextColorBins];
        var representatives = new int[TextColorBins];
        var yPhases = new double[TextColorSamples];

        for (int x = 0; x < columns; x++)
        {
            yPhases[x] = dense ? 0.5 : 0.15 + 0.7 * ((x + 1) * 0.75487766625 % 1.0);
        }

        int count = 0, opaqueCount = 0, totalWeight = 0;

        for (int y = 0; y < rows; y++)
        {
            double xPhase = dense ? 0.5 : 0.15 + 0.7 * ((y + 1) * 0.61803398875 % 1.0);
            for (int x = 0; x < columns; x++)
            {
                double u = (x + xPhase) / columns;
                double v = (y + yPhases[x]) / rows;
                int px = (int)(left + (right - left) * u);
                int py = (int)(top + (bottom - top) * v);
                if (!image.InBounds(px, py)) continue;
                if (image.Alpha(px, py) != 255) continue;

                opaqueCount++;
                Rgb rgb = image.Pixel(px, py);
                int distance = rgb.MaxDistance(bg);
                if (distance <= threshold) continue;

                int neighbors = 0;
                Rgb leftRgb = rgb, rightRgb = rgb;
                foreach ((int dx, int dy) in Neighbors)
                {
                    int nx = px + dx, ny = py + dy;
                    if (!image.InBounds(nx, ny)) continue;
                    if (image.Alpha(nx, ny) != 255) continue;
                    Rgb other = image.Pixel(nx, ny);
                    if (dx < 0) leftRgb = other;
                    else if (dx > 0) rightRgb = other;
                    if (rgb.MaxDistance(other) <= 24) neighbors++;
                }

                // 一像素宽的笔画要活下来；孤立噪点与亚像素彩边降权；对比度权重封顶
                int weight = (neighbors >= 2 ? 2 : 1) * (distance >= 64 ? 2 : 1);

                // 三像素水平滤波只用于「分族」，输出与统计仍用原始像素
                Rgb directionRgb = new(
                    (byte)((leftRgb.R + 2 * rgb.R + rightRgb.R + 2) / 4),
                    (byte)((leftRgb.G + 2 * rgb.G + rightRgb.G + 2) / 4),
                    (byte)((leftRgb.B + 2 * rgb.B + rightRgb.B + 2) / 4));
                int directionDistance = directionRgb.MaxDistance(bg);
                if (directionDistance <= threshold / 2) continue;

                var direction = new Lab(
                    (directionRgb.R - bg.R) * 255.0 / directionDistance,
                    (directionRgb.G - bg.G) * 255.0 / directionDistance,
                    (directionRgb.B - bg.B) * 255.0 / directionDistance);

                int bin = (((int)(direction.L + 255) >> 5) << 8) |
                          (((int)(direction.A + 255) >> 5) << 4) |
                          ((int)(direction.B + 255) >> 5);

                counts[bin] += weight;
                representatives[bin] = count;
                samples[count++] = new TextSample(rgb, direction, distance, weight,
                    1 << Math.Min(7, (int)((boxWidth >= boxHeight ? u : v) * 8)));
                totalWeight += weight;
            }
        }

        // 采样点里绝大多数都「不像字」：说明这不是一格文字，或底色本身有纹理
        if (count < 8 || count * 5 > opaqueCount * 4) return null;

        // ---- 按「相对背景的方向」分族 ----
        var seeds = new int[8];
        Array.Fill(seeds, -1);
        for (int i = 0; i < TextColorBins; i++)
        {
            if (counts[i] == 0) continue;
            for (int rank = 0; rank < 8; rank++)
            {
                if (seeds[rank] < 0 || counts[i] > counts[seeds[rank]])
                {
                    for (int move = 7; move > rank; move--) seeds[move] = seeds[move - 1];
                    seeds[rank] = i;
                    break;
                }
            }
        }

        var directions = new Lab[4];
        int directionCount = 0, bestSupport = 0;
        int bestBands = 0;
        Lab bestDirection = default;

        foreach (int seed in seeds)
        {
            if (seed < 0 || directionCount == 4) break;

            Lab direction = samples[representatives[seed]].Direction;
            bool seen = false;
            for (int i = 0; i < directionCount; i++)
            {
                if (Lab.SameDirection(directions[i], direction))
                {
                    seen = true;
                    break;
                }
            }

            if (seen) continue;
            directions[directionCount++] = direction;

            int support = 0, bands = 0;
            for (int i = 0; i < count; i++)
            {
                if (!Lab.SameDirection(samples[i].Direction, direction)) continue;
                support += samples[i].Weight;
                bands |= samples[i].Band;
            }

            if (support > bestSupport)
            {
                bestSupport = support;
                bestDirection = direction;
                bestBands = bands;
            }
        }

        // 小字的彩边可能占掉大半像素并散成很多族：稳定核心存在时，五分之一证据就够
        if (bestSupport < 12 || bestSupport * 5 < totalWeight ||
            (Math.Max(boxWidth, boxHeight) >= 48 && BitOperations.PopCount((uint)bestBands) < 2))
        {
            return null;
        }

        // ---- 先选色族，再按对比度提纯（红字不能因为更亮就顶掉灰正文） ----
        var contrastHistogram = new int[256];
        int projectionScale = Math.Abs((int)bestDirection.L) + Math.Abs((int)bestDirection.A) +
                              Math.Abs((int)bestDirection.B);

        for (int i = 0; i < count; i++)
        {
            if (!Lab.SameDirection(samples[i].Direction, bestDirection)) continue;

            double projection = ((samples[i].Rgb.R - bg.R) * bestDirection.L +
                                 (samples[i].Rgb.G - bg.G) * bestDirection.A +
                                 (samples[i].Rgb.B - bg.B) * bestDirection.B) / projectionScale;

            int distance = (int)Math.Clamp(projection, 0, 255);
            samples[i] = samples[i] with { Distance = distance };
            contrastHistogram[distance] += samples[i].Weight;
        }

        int coreThreshold = Quantile(contrastHistogram, (bestSupport * 3 + 3) / 4);

        counts = new int[TextColorBins];
        int bestBin = 0;
        for (int i = 0; i < count; i++)
        {
            if (samples[i].Distance < coreThreshold ||
                !Lab.SameDirection(samples[i].Direction, bestDirection))
            {
                continue;
            }

            int bin = samples[i].Rgb.Bin;
            counts[bin] += samples[i].Weight;
            representatives[bin] = i;
            if (counts[bin] > counts[bestBin]) bestBin = bin;
        }

        Rgb seedRgb = samples[representatives[bestBin]].Rgb;

        int supportSum = 0, red = 0, green = 0, blue = 0;
        for (int i = 0; i < count; i++)
        {
            if (samples[i].Distance < coreThreshold ||
                samples[i].Rgb.MaxDistance(seedRgb) > 24 ||
                !Lab.SameDirection(samples[i].Direction, bestDirection))
            {
                continue;
            }

            supportSum += samples[i].Weight;
            red += samples[i].Rgb.R * samples[i].Weight;
            green += samples[i].Rgb.G * samples[i].Weight;
            blue += samples[i].Rgb.B * samples[i].Weight;
        }

        if (supportSum < 6) return null;

        var mean = new Rgb((byte)((red + supportSum / 2) / supportSum),
            (byte)((green + supportSum / 2) / supportSum),
            (byte)((blue + supportSum / 2) / supportSum));

        Rgb representative = seedRgb;
        int nearest = 256;
        for (int i = 0; i < count; i++)
        {
            if (samples[i].Distance < coreThreshold ||
                !Lab.SameDirection(samples[i].Direction, bestDirection))
            {
                continue;
            }

            int distance = samples[i].Rgb.MaxDistance(mean);
            if (distance < nearest ||
                (distance == nearest && samples[i].Rgb.Key < representative.Key))
            {
                nearest = distance;
                representative = samples[i].Rgb;
            }
        }

        return representative;
    }

    // ============ 小工具 ============

    private static bool InAny(IReadOnlyList<Int32Rect> regions, int x, int y)
    {
        for (int i = 0; i < regions.Count; i++)
        {
            Int32Rect region = regions[i];
            if (x < region.X || y < region.Y) continue;
            if (x >= region.X + region.Width || y >= region.Y + region.Height) continue;
            return true;
        }

        return false;
    }

    private static int Quantile(int[] histogram, int rank)
    {
        for (int i = 0; i < histogram.Length; i++)
        {
            rank -= histogram[i];
            if (rank <= 0) return i;
        }

        return histogram.Length - 1;
    }

    /// <summary>OKLab（上游 <c>perceptualColor</c>）：暗底与高饱和面板也能分开亮度 / 彩度。</summary>
    internal static Lab Perceptual(Rgb rgb)
    {
        static double Linear(int channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        double r = Linear(rgb.R), g = Linear(rgb.G), b = Linear(rgb.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new Lab(
            0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }
}
