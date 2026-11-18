using System;
using System.Collections.Generic;
using System.Threading;

namespace LTools.Features.CapturePro;

/// <summary>
/// 像素级「智能擦除」（对齐 Snow Shot 的 <c>SnowCanvasFilterType::SmartErase</c>）：
/// 把选区当成「洞」，从周围完好的像素里取样重建，而不是把整块涂成马赛克或删掉整条标注。
/// </summary>
/// <remarks>
/// <para>
/// 上游实现在 <c>snow-draw-engine-gpui/src/smart_erase</c>（<c>patch.rs</c> / <c>region.rs</c>），
/// 分三级求解，命中哪一级就用哪一级，越往后越贵：
/// </para>
/// <list type="number">
/// <item><b>仿射曲面</b>（<c>surface</c>）：洞周围一圈已知像素拟合一个平面，5 轮 IRLS 去掉离群点。
/// 纯色 / 渐变背景（截图里最常见）在这里就结束，O(像素)。</item>
/// <item><b>周期纹理</b>（<c>periodic</c>）：沿 X / Y 找 2–64 像素的重复周期，按周期把洞抄回来。</item>
/// <item><b>多尺度块匹配</b>（<c>multiscale</c> / <c>fill</c>）：图像金字塔（最多 6 层），
/// 每层在 Lab 空间给洞算引导色 + 局部方差，再从「侵蚀后的可用素材」里随机撒点找最像的补丁，
/// 最后按邻域一致性投票（coherence）平掉接缝。种子固定，所以同一输入永远得到同一结果。</item>
/// </list>
/// <para>
/// 与上游的**唯一差异**：上游大洞会交给 GPU；这里没有 GPU，改用「盒式降采样到洞的边长 ≤ 96 再重建」，
/// 结果再双线性放大回洞区（<see cref="WorkScale"/>）。小洞（≤ 96 像素）与上游逐像素一致。
/// </para>
/// <para>
/// 纯计算、不碰 WPF：<c>--selftest</c> 直接喂假的 BGRA 缓冲断言结果。
/// </para>
/// </remarks>
internal static class SmartErase
{
    /// <summary>颜色（分量 0–255；Lab 也借用这个结构，L 0–100、a/b ±128）。</summary>
    private struct Rgb
    {
        public float R;
        public float G;
        public float B;

        public Rgb(float r, float g, float b)
        {
            R = r;
            G = g;
            B = b;
        }

        public static Rgb Zero => default;
    }

    /// <summary>一层金字塔的素材（对应上游 <c>Level</c>）。</summary>
    private sealed class Level
    {
        public int Width;
        public int Height;
        public int SearchSpan;
        public Rgb[] Source = Array.Empty<Rgb>();
        public bool[] Hole = Array.Empty<bool>();
        public bool[] Covered = Array.Empty<bool>();
        public bool[] Domain = Array.Empty<bool>();

        /// <summary>越界返回 -1（对应上游的 <c>Option</c>）。</summary>
        public int At(int x, int y) =>
            x >= 0 && y >= 0 && x < Width && y < Height ? y * Width + x : -1;

        public bool Known(int i) => Covered[i] && !Hole[i];

        public List<int> Holes()
        {
            var list = new List<int>();
            for (int i = 0; i < Hole.Length; i++)
                if (Hole[i]) list.Add(i);
            return list;
        }

        public Level Clone()
        {
            var copy = new Level
            {
                Width = Width,
                Height = Height,
                SearchSpan = SearchSpan,
                Source = (Rgb[])Source.Clone(),
                Hole = (bool[])Hole.Clone(),
                Covered = (bool[])Covered.Clone(),
                Domain = (bool[])Domain.Clone(),
            };
            return copy;
        }
    }

    /// <summary>一层重建结果（对应上游 <c>ResultLevel</c>）：RGB 成品 + 每个洞像素取的素材下标。</summary>
    private readonly struct Filled
    {
        public Filled(Rgb[] image, int[] matches, int width, int height)
        {
            Image = image;
            Matches = matches;
            Width = width;
            Height = height;
        }

        public Rgb[] Image { get; }
        public int[] Matches { get; }
        public int Width { get; }
        public int Height { get; }
    }

    /// <summary>固定种子的 xorshift（上游 <c>Random(0x534e4f57)</c>，保证结果可复现）。</summary>
    private struct Dice
    {
        private uint _state;

        public Dice(uint seed) => _state = seed;

        public uint Next()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x;
            return x;
        }

        public int Pick(int n) => n <= 0 ? 0 : (int)(Next() % (uint)n);
    }

    // ============ 入口 ============

    /// <summary>
    /// 把 <paramref name="mask"/> 标出的像素重建掉。返回 false = 没有可用素材（整块都被遮住），
    /// 调用方按「这一步没做成」处理（别假装擦掉了）。
    /// </summary>
    /// <param name="pixels">BGRA（非预乘），步长 = <paramref name="width"/> × 4。就地修改。</param>
    /// <param name="mask">要重建的像素（true = 洞）。</param>
    public static bool Reconstruct(byte[] pixels, int width, int height, bool[] mask, CancellationToken token)
    {
        if (width <= 0 || height <= 0) return false;
        if (mask.Length != width * height) return false;

        int holes = 0;
        for (int i = 0; i < mask.Length; i++)
            if (mask[i]) holes++;
        if (holes == 0) return true;

        int scale = WorkScale(mask, width, height);
        var level = Build(pixels, width, height, mask, scale);

        try
        {
            var filled = Solve(level, token);
            if (filled == null) return false;
            WriteBack(pixels, width, height, mask, level, filled, scale);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 工作倍率：洞的边长超过 96 就整块降采样（上游这时会用 GPU）。
    /// 小洞返回 1 = 与上游逐像素一致。
    /// </summary>
    private static int WorkScale(bool[] mask, int width, int height)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (!mask[row + x]) continue;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                if (y > y1) y1 = y;
            }
        }

        if (x1 < 0) return 1;
        int longest = Math.Max(x1 - x0 + 1, y1 - y0 + 1);
        return Math.Clamp((longest + 95) / 96, 1, 8);
    }

    /// <summary>把原图（可带倍率）摊成一层素材。</summary>
    private static Level Build(byte[] pixels, int width, int height, bool[] mask, int scale)
    {
        int w = (width + scale - 1) / scale;
        int h = (height + scale - 1) / scale;
        var level = new Level
        {
            Width = w,
            Height = h,
            SearchSpan = Math.Max(w, h),
            Source = new Rgb[w * h],
            Hole = new bool[w * h],
            Covered = new bool[w * h],
            Domain = new bool[w * h],
        };

        for (int cy = 0; cy < h; cy++)
        {
            for (int cx = 0; cx < w; cx++)
            {
                bool anyHole = false;
                bool allCovered = true;
                float r = 0, g = 0, b = 0;
                int count = 0;

                for (int dy = 0; dy < scale; dy++)
                {
                    int y = cy * scale + dy;
                    if (y >= height)
                    {
                        allCovered = false;
                        continue;
                    }

                    int row = y * width;
                    for (int dx = 0; dx < scale; dx++)
                    {
                        int x = cx * scale + dx;
                        if (x >= width)
                        {
                            allCovered = false;
                            continue;
                        }

                        int i = row + x;
                        bool covered = pixels[i * 4 + 3] != 0;
                        bool hole = covered && mask[i];
                        allCovered &= covered;
                        anyHole |= hole;
                        if (!covered || hole) continue;

                        b += pixels[i * 4];
                        g += pixels[i * 4 + 1];
                        r += pixels[i * 4 + 2];
                        count++;
                    }
                }

                int j = cy * w + cx;
                level.Covered[j] = allCovered;
                level.Hole[j] = anyHole;
                if (count > 0) level.Source[j] = new Rgb(r / count, g / count, b / count);
            }
        }

        return level;
    }

    /// <summary>把重建结果写回原缓冲：只动洞里的像素，其它字节一个都不碰。</summary>
    private static void WriteBack(byte[] pixels, int width, int height, bool[] mask, Level level, Rgb[] filled, int scale)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int i = row + x;
                if (!mask[i]) continue;

                Rgb c;
                if (scale == 1)
                {
                    c = filled[y * level.Width + x];
                }
                else
                {
                    double u = (x + 0.5) / scale - 0.5;
                    double v = (y + 0.5) / scale - 0.5;
                    c = Sample(filled, level.Width, level.Height, u, v);
                }

                pixels[i * 4] = Clamp(c.B);
                pixels[i * 4 + 1] = Clamp(c.G);
                pixels[i * 4 + 2] = Clamp(c.R);
            }
        }
    }

    /// <summary>双线性取样（降采样重建之后放大用）。</summary>
    private static Rgb Sample(Rgb[] image, int width, int height, double u, double v)
    {
        double x = Math.Clamp(u, 0, width - 1);
        double y = Math.Clamp(v, 0, height - 1);
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, width - 1);
        int y1 = Math.Min(y0 + 1, height - 1);
        double fx = x - x0;
        double fy = y - y0;

        var a = Mix(image[y0 * width + x0], image[y0 * width + x1], (float)fx);
        var b = Mix(image[y1 * width + x0], image[y1 * width + x1], (float)fx);
        return Mix(a, b, (float)fy);
    }

    private static byte Clamp(float value) =>
        (byte)Math.Clamp(Math.Round(value), 0, 255);

    // ============ 三级求解 ============

    /// <summary>依次尝试仿射曲面 → 周期纹理 → 多尺度块匹配；都不行返回 null。</summary>
    private static Rgb[]? Solve(Level level, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        bool anyKnown = false;
        for (int i = 0; i < level.Hole.Length; i++)
        {
            if (!level.Known(i)) continue;
            anyKnown = true;
            break;
        }

        if (!anyKnown) return null;

        var surface = Surface(level, token);
        if (surface != null) return surface;

        level.MakeDomain(token);

        var repeated = Periodic(level, token);
        if (repeated != null) return repeated;

        return Multiscale(level, token)?.Image;
    }

    /// <summary>可用素材的「够用半径」：洞越大，允许的素材离洞越远（16–512，按 2 倍递增）。</summary>
    private static void MakeDomain(this Level level, CancellationToken token)
    {
        var holes = level.Holes();
        if (holes.Count == 0) return;

        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        foreach (int i in holes)
        {
            int x = i % level.Width;
            int y = i / level.Width;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }

        var nearest = Nearest(level.Width, level.Height, level.Hole, token)!;
        int reach = Math.Clamp(Math.Min(x1 - x0 + 1, y1 - y0 + 1) / 2, 16, 96);

        while (true)
        {
            token.ThrowIfCancellationRequested();
            for (int i = 0; i < nearest.Length; i++)
            {
                int p = nearest[i];
                if (p < 0 || !level.Known(i))
                {
                    level.Domain[i] = false;
                    continue;
                }

                int dx = Math.Abs(i % level.Width - p % level.Width);
                int dy = Math.Abs(i / level.Width - p / level.Width);
                level.Domain[i] = dx * dx + dy * dy <= reach * reach;
            }

            if (Count(Erode(level.Domain, level.Width, level.Height, 3)) >= 64 || reach >= 512) break;
            reach = Math.Min(reach * 2, 512);
        }
    }

    // ============ 第一级：仿射曲面 ============

    /// <summary>
    /// 洞周围一圈已知像素拟合一个平面（<c>1, u, v</c> 三个基）。5 轮 IRLS 压掉离群点，
    /// 至少 97% 的点要落在色差 4/255 以内才算「这就是一块纯色 / 渐变」，否则交给下一级。
    /// </summary>
    private static Rgb[]? Surface(Level level, CancellationToken token)
    {
        int w = level.Width;
        var holes = level.Holes();
        if (holes.Count == 0) return (Rgb[])level.Source.Clone();

        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        foreach (int i in holes)
        {
            int x = i % w;
            int y = i / w;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }

        x1++;
        y1++;

        var ring = new bool[level.Source.Length];
        for (int n = 0; n < holes.Count; n++)
        {
            if (n % 64 == 0) token.ThrowIfCancellationRequested();
            int i = holes[n];
            int hx = i % w;
            int hy = i / w;
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    if (dx * dx + dy * dy > 20) continue;
                    int j = level.At(hx + dx, hy + dy);
                    if (j >= 0 && level.Known(j)) ring[j] = true;
                }
            }
        }

        var points = new List<int>();
        for (int i = 0; i < ring.Length; i++)
            if (ring[i]) points.Add(i);
        if (points.Count < 12) return null;

        int stride = Math.Max(points.Count / 4096, 1);
        double width = Math.Max(1, x1 - x0);
        double height = Math.Max(1, y1 - y0);

        double[] Basis(int i) => new[]
        {
            1.0,
            (i % w - (double)x0) / width,
            (i / w - (double)y0) / height,
        };

        double[] Predict(double[] basis, double[][] coefficients)
        {
            var result = new double[3];
            for (int channel = 0; channel < 3; channel++)
            {
                double sum = 0;
                for (int j = 0; j < 3; j++) sum += basis[j] * coefficients[j][channel];
                result[channel] = sum;
            }
            return result;
        }

        double[][] coefficients = { new double[3], new double[3], new double[3] };

        for (int iteration = 0; iteration < 5; iteration++)
        {
            token.ThrowIfCancellationRequested();
            var normal = new double[3][];
            var rhs = new double[3][];
            for (int a = 0; a < 3; a++)
            {
                normal[a] = new double[3];
                rhs[a] = new double[3];
            }

            for (int n = 0; n < points.Count; n += stride)
            {
                int i = points[n];
                var basis = Basis(i);
                var value = new[] { level.Source[i].R / 255.0, level.Source[i].G / 255.0, level.Source[i].B / 255.0 };
                var p = Predict(basis, coefficients);

                double residual = 0;
                for (int c = 0; c < 3; c++) residual += (p[c] - value[c]) * (p[c] - value[c]);
                residual = Math.Sqrt(residual);

                double weight = iteration == 0 ? 1.0 : Math.Min(0.008 / Math.Max(residual, 1e-8), 1.0);
                for (int a = 0; a < 3; a++)
                    for (int c = 0; c < 3; c++)
                    {
                        normal[a][c] += weight * basis[a] * basis[c];
                        rhs[a][c] += weight * basis[a] * value[c];
                    }
            }

            double[][]? solution = Solve3(normal, rhs);
            if (solution == null) return null;
            coefficients = solution;
        }

        int inliers = 0, count = 0;
        double error = 0;
        for (int n = 0; n < points.Count; n += stride)
        {
            int i = points[n];
            count++;
            var p = Predict(Basis(i), coefficients!);
            double e = 0;
            for (int c = 0; c < 3; c++)
            {
                double value = c switch
                {
                    0 => level.Source[i].R,
                    1 => level.Source[i].G,
                    _ => level.Source[i].B,
                } / 255.0;
                e += (p[c] - value) * (p[c] - value);
            }

            if (e >= (4.0 / 255.0) * (4.0 / 255.0)) continue;
            inliers++;
            error += e;
        }

        if (inliers * 100 < count * 97 || error > inliers * 6.0 / (255.0 * 255.0)) return null;

        var output = (Rgb[])level.Source.Clone();
        foreach (int i in holes)
        {
            var p = Predict(Basis(i), coefficients!);
            output[i] = new Rgb(
                (float)(p[0] * 255.0), (float)(p[1] * 255.0), (float)(p[2] * 255.0));
        }

        return output;
    }

    /// <summary>3×3 高斯消元（列主元）。奇异返回 null。</summary>
    private static double[][]? Solve3(double[][] a, double[][] b)
    {
        var m = new double[3][];
        var v = new double[3][];
        for (int i = 0; i < 3; i++)
        {
            m[i] = (double[])a[i].Clone();
            v[i] = (double[])b[i].Clone();
        }

        for (int k = 0; k < 3; k++)
        {
            int pivot = k;
            for (int i = k + 1; i < 3; i++)
                if (Math.Abs(m[i][k]) > Math.Abs(m[pivot][k])) pivot = i;
            if (Math.Abs(m[pivot][k]) < 1e-12) return null;

            (m[k], m[pivot]) = (m[pivot], m[k]);
            (v[k], v[pivot]) = (v[pivot], v[k]);

            double scale = m[k][k];
            for (int c = 0; c < 3; c++)
            {
                m[k][c] /= scale;
                v[k][c] /= scale;
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == k) continue;
                double factor = m[r][k];
                for (int c = 0; c < 3; c++)
                {
                    m[r][c] -= factor * m[k][c];
                    v[r][c] -= factor * v[k][c];
                }
            }
        }

        return v;
    }

    // ============ 第二级：周期纹理 ============

    /// <summary>沿 X / Y 找 2–64 像素的重复周期；整块洞都能按周期抄回来才采用。</summary>
    private static Rgb[]? Periodic(Level level, CancellationToken token)
    {
        var points = new List<int>();
        for (int i = 0; i < level.Domain.Length; i++)
            if (level.Domain[i]) points.Add(i);

        foreach (var (dx, dy) in new[] { (1, 0), (0, 1) })
        {
            for (int period = 2; period <= 64; period++)
            {
                if (!PeriodMatches(level, points, dx, dy, period, token)) continue;

                var output = (Rgb[])level.Source.Clone();
                bool complete = true;
                var holes = level.Holes();

                for (int n = 0; n < holes.Count; n++)
                {
                    if (n % 64 == 0) token.ThrowIfCancellationRequested();
                    int i = holes[n];
                    int found = -1;
                    int limit = Math.Max(level.Width, level.Height) / period + 1;

                    for (int step = 1; step < limit && found < 0; step++)
                    {
                        foreach (int sign in new[] { -1, 1 })
                        {
                            int j = level.At(
                                i % level.Width + dx * period * step * sign,
                                i / level.Width + dy * period * step * sign);
                            if (j < 0 || !level.Domain[j]) continue;
                            found = j;
                            break;
                        }
                    }

                    if (found < 0)
                    {
                        complete = false;
                        break;
                    }

                    output[i] = level.Source[found];
                }

                if (complete) return output;
            }
        }

        return null;
    }

    private static bool PeriodMatches(Level level, List<int> points, int dx, int dy, int period, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int count = 0;
        float error = 0;

        for (int n = 0; n < points.Count; n++)
        {
            if (n % 4096 == 0) token.ThrowIfCancellationRequested();
            int i = points[n];
            int j = level.At(i % level.Width + dx * period, i / level.Width + dy * period);
            if (j < 0 || !level.Domain[j]) continue;

            count++;
            error += Distance(level.Source[i], level.Source[j]);
            if (error > points.Count * 0.5f) break;
        }

        return count >= 64 && error <= count * 0.5f;
    }

    // ============ 第三级：多尺度块匹配 ============

    /// <summary>金字塔：粗层一路填下来，每层用上一层的匹配结果当先验（对应上游 <c>multiscale</c>）。</summary>
    private static Filled? Multiscale(Level level, CancellationToken token)
    {
        var levels = new List<Level> { level };

        while (levels.Count < 6)
        {
            var fine = levels[^1];
            if (Math.Min(fine.Width, fine.Height) < 40) break;

            var coarse = Downsample(fine, token);
            bool anyHole = false;
            for (int i = 0; i < coarse.Hole.Length; i++)
                if (coarse.Hole[i])
                {
                    anyHole = true;
                    break;
                }

            if (!anyHole || Count(Erode(coarse.Domain, coarse.Width, coarse.Height, 3)) < 16) break;
            levels.Add(coarse);
        }

        int count = levels.Count;
        Filled? previous = null;

        for (int n = 0; n < count; n++)
        {
            token.ThrowIfCancellationRequested();
            var current = levels[count - 1 - n];
            int passes = n == 0 ? 5 : n + 1 == count ? 2 : 3;
            previous = Fill(current, previous, passes, token);
        }

        return previous;
    }

    /// <summary>2:1 降采样（5×5 权重）；只要有一块不完整 / 有洞，粗层就不当素材用。</summary>
    private static Level Downsample(Level fine, CancellationToken token)
    {
        var known = new bool[fine.Source.Length];
        for (int i = 0; i < known.Length; i++) known[i] = fine.Known(i);

        var safe = Erode(known, fine.Width, fine.Height, 2);
        var domain = Erode(fine.Domain, fine.Width, fine.Height, 2);

        var coarse = new Level
        {
            Width = (fine.Width + 1) / 2,
            Height = (fine.Height + 1) / 2,
            SearchSpan = Math.Max(1, fine.SearchSpan / 2),
            Source = new Rgb[(fine.Width + 1) / 2 * ((fine.Height + 1) / 2)],
            Hole = new bool[(fine.Width + 1) / 2 * ((fine.Height + 1) / 2)],
            Covered = new bool[(fine.Width + 1) / 2 * ((fine.Height + 1) / 2)],
            Domain = new bool[(fine.Width + 1) / 2 * ((fine.Height + 1) / 2)],
        };

        var weights = new[] { 1f, 4f, 6f, 4f, 1f };

        for (int y = 0; y < coarse.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < coarse.Width; x++)
            {
                int i = y * coarse.Width + x;
                int fx = x * 2;
                int fy = y * 2;

                bool allCovered = true;
                bool anyHole = false;
                bool allDomain = true;
                float r = 0, g = 0, b = 0, total = 0;

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int j = fine.At(fx + dx, fy + dy);
                        if (j < 0)
                        {
                            allCovered = false;
                            allDomain = false;
                            continue;
                        }

                        allCovered &= fine.Covered[j];
                        anyHole |= fine.Hole[j];
                        allDomain &= domain[j];
                        if (!fine.Known(j)) continue;

                        float weight = weights[dx + 2] * weights[dy + 2];
                        r += fine.Source[j].R * weight;
                        g += fine.Source[j].G * weight;
                        b += fine.Source[j].B * weight;
                        total += weight;
                    }
                }

                coarse.Hole[i] = anyHole && allCovered;
                coarse.Covered[i] = allCovered;
                coarse.Domain[i] = allDomain && safe[fy * fine.Width + fx] && !anyHole;
                if (total > 0 && !anyHole) coarse.Source[i] = new Rgb(r / total, g / total, b / total);
            }
        }

        return coarse;
    }

    /// <summary>一层里的「初始化 + 多轮块匹配 + 一致性投票」。</summary>
    private static Filled Fill(Level level, Filled? previous, int passes, CancellationToken token)
    {
        int width = level.Width;
        int height = level.Height;

        var colors = new Rgb[level.Source.Length];
        for (int i = 0; i < colors.Length; i++) colors[i] = Lab(level.Source[i]);

        var (guideColor, guideVariation) = Guides(level, colors, token);

        var holes = level.Holes();
        var donors = Erode(level.Domain, width, height, 3);
        int radius = 3;
        while (Count(donors) == 0 && radius > 0)
        {
            radius--;
            donors = Erode(level.Domain, width, height, radius);
        }

        var candidates = new List<int>();
        for (int i = 0; i < donors.Length; i++)
            if (donors[i]) candidates.Add(i);
        if (candidates.Count == 0) throw new InvalidOperationException("智能擦除没有可用素材");

        var nearest = Nearest(width, height, donors, token)!;
        var dice = new Dice(0x534e4f57);

        var output = (Rgb[])colors.Clone();
        var matches = new int[colors.Length];
        Array.Fill(matches, -1);

        foreach (int i in holes)
        {
            token.ThrowIfCancellationRequested();
            int best = nearest[i];
            if (best < 0) best = candidates[0];

            float error = Distance(guideColor[i], guideColor[best])
                          + 4f * Sq(guideVariation[i] - guideVariation[best]);

            for (int n = 0; n < 32; n++)
            {
                int p = candidates[dice.Pick(candidates.Count)];
                float e = Distance(guideColor[i], guideColor[p])
                          + 4f * Sq(guideVariation[i] - guideVariation[p]);
                if (e >= error) continue;
                best = p;
                error = e;
            }

            matches[i] = best;
            output[i] = colors[best];

            if (previous == null) continue;

            var prev = previous.Value;
            int px = i % width * prev.Width / width;
            int py = i / width * prev.Height / height;
            int p2 = prev.Matches[py * prev.Width + px];
            if (p2 < 0) continue;

            int sx = p2 % prev.Width * width / prev.Width;
            int sy = p2 / prev.Width * height / prev.Height;
            int j = level.At(sx, sy);
            if (j >= 0 && donors[j]) matches[i] = j;
            output[i] = Lab(prev.Image[py * prev.Width + px]);
        }

        var costs = new float[colors.Length];
        Array.Fill(costs, float.MaxValue);
        var reuse = new float[colors.Length];
        float expected = Math.Max(1f, holes.Count / (float)candidates.Count);
        float searchScale = Math.Max(1, level.SearchSpan);
        var next = (Rgb[])output.Clone();
        var rgb = (Rgb[])level.Source.Clone();

        for (int pass = 0; pass < passes; pass++)
        {
            token.ThrowIfCancellationRequested();
            float synthesized = 0.15f + 0.20f * pass / Math.Max(1, passes - 1);
            Array.Clear(reuse);

            foreach (int i in holes) reuse[matches[i]] += 1f;
            foreach (int i in candidates) reuse[i] = 25f * MathF.Log(1f + reuse[i] / expected);

            int step = pass % 2 == 0 ? 1 : -1;

            for (int n = 0; n < holes.Count; n++)
            {
                if (n % 64 == 0) token.ThrowIfCancellationRequested();
                int i = holes[step > 0 ? n : holes.Count - 1 - n];
                int x = i % width;
                int y = i / width;

                int best = matches[i];
                float bestCost = float.MaxValue;

                void Try(int p)
                {
                    if (p < 0 || !donors[p]) return;

                    int sx = p % width;
                    int sy = p / width;

                    float fixedCost = 8f / (4f + guideVariation[i]) * Distance(guideColor[i], guideColor[p])
                                      + 4f * Sq(guideVariation[i] - guideVariation[p])
                                      + reuse[p]
                                      + 8f * ((sx - x) * (sx - x) + (sy - y) * (sy - y)) / (searchScale * searchScale);
                    if (fixedCost > bestCost) return;

                    float sum = 0;
                    float norm = 0;
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int t = level.At(x + dx, y + dy);
                            if (t < 0 || !level.Covered[t]) continue;

                            int s = level.At(sx + dx, sy + dy);
                            if (s < 0) continue;

                            float weight = level.Hole[t] ? synthesized : 1f;
                            sum += Distance(output[t], colors[s]) * weight;
                            norm += weight;
                        }
                    }

                    float score = sum / Math.Max(norm, 1f) + fixedCost;
                    if (score >= bestCost) return;
                    best = p;
                    bestCost = score;
                }

                Try(matches[i]);
                Try(nearest[i]);
                Try(candidates[dice.Pick(candidates.Count)]);

                foreach (var (nx, ny, ox, oy) in new[] { (x - step, y, step, 0), (x, y - step, 0, step) })
                {
                    int n2 = level.At(nx, ny);
                    if (n2 < 0) continue;
                    int p = matches[n2];
                    if (p < 0) continue;
                    Try(level.At(p % width + ox, p / width + oy));
                }

                int center = matches[i];
                int window = level.SearchSpan;
                while (window > 0)
                {
                    int sx = center % width + dice.Pick(window * 2 + 1) - window;
                    int sy = center / width + dice.Pick(window * 2 + 1) - window;
                    Try(level.At(sx, sy));
                    window /= 2;
                }

                matches[i] = best;
                costs[i] = bestCost;
            }

            foreach (int i in holes)
            {
                if (i % 64 == 0) token.ThrowIfCancellationRequested();
                int x = i % width;
                int y = i / width;
                var anchor = colors[matches[i]];
                float tolerance = Math.Clamp(guideVariation[i] * 0.25f, 1.5f, 10f);

                float sumR = 0, sumG = 0, sumB = 0;
                float sumRgbR = 0, sumRgbG = 0, sumRgbB = 0;
                float total = 0;

                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        int n = level.At(x + dx, y + dy);
                        if (n < 0 || !level.Hole[n]) continue;

                        int p = matches[n];
                        if (p < 0) continue;

                        int j = level.At(p % width - dx, p / width - dy);
                        if (j < 0) continue;

                        float weight = 1f
                                       / ((1f + costs[n])
                                          * (1f + Distance(guideColor[j], guideColor[i]) / 100f)
                                          * (1f + Distance(colors[j], anchor) / (tolerance * tolerance)));

                        var src = level.Source[j];
                        sumR += colors[j].R * weight;
                        sumG += colors[j].G * weight;
                        sumB += colors[j].B * weight;
                        sumRgbR += src.R * weight;
                        sumRgbG += src.G * weight;
                        sumRgbB += src.B * weight;
                        total += weight;
                    }
                }

                if (total > 0)
                {
                    next[i] = new Rgb(sumR / total, sumG / total, sumB / total);
                    rgb[i] = new Rgb(sumRgbR / total, sumRgbG / total, sumRgbB / total);
                }
                else
                {
                    next[i] = output[i];
                    rgb[i] = level.Source[Math.Max(0, matches[i])];
                }
            }

            (output, next) = (next, output);
        }

        return new Filled(rgb, matches, width, height);
    }

    /// <summary>
    /// 引导色 / 局部方差：先做一次已知掩码与颜色的可分离高斯（半径按洞的短边 /12，夹 4–16），
    /// 再把每个洞像素沿四个方向找左右最近的已知像素，按「最近的那侧」或「两侧插值」给引导色。
    /// </summary>
    private static (Rgb[] Color, float[] Variation) Guides(Level level, Rgb[] colors, CancellationToken token)
    {
        int width = level.Width;
        int height = level.Height;
        var holes = level.Holes();

        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        foreach (int i in holes)
        {
            int x = i % width;
            int y = i / width;
            if (x < x0) x0 = x;
            if (x > x1) x1 = x;
            if (y < y0) y0 = y;
            if (y > y1) y1 = y;
        }

        int radius = Math.Clamp(Math.Min(x1 - x0 + 1, y1 - y0 + 1) / 12, 4, 16);

        var weights = new Rgb[colors.Length];
        var observed = new Rgb[colors.Length];
        var squared = new Rgb[colors.Length];

        for (int i = 0; i < colors.Length; i++)
        {
            if (!level.Known(i)) continue;
            weights[i] = new Rgb(1, 1, 1);
            observed[i] = colors[i];
            float norm = colors[i].R * colors[i].R + colors[i].G * colors[i].G + colors[i].B * colors[i].B;
            squared[i] = new Rgb(norm, norm, norm);
        }

        weights = Gaussian(weights, width, height, radius, token);
        var mean = Gaussian(observed, width, height, radius, token);
        squared = Gaussian(squared, width, height, radius, token);

        var variation = new float[colors.Length];
        for (int i = 0; i < mean.Length; i++)
        {
            float wv = Math.Max(weights[i].R, 1e-6f);
            mean[i] = new Rgb(mean[i].R / wv, mean[i].G / wv, mean[i].B / wv);
            float second = squared[i].R / wv;
            float first = mean[i].R * mean[i].R + mean[i].G * mean[i].G + mean[i].B * mean[i].B;
            variation[i] = (float)Math.Sqrt(Math.Max(0, second - first));
        }

        var guideColor = (Rgb[])mean.Clone();
        var guideVariation = (float[])variation.Clone();
        var costs = new float[colors.Length];
        Array.Fill(costs, float.MaxValue);

        foreach (var (dx, dy) in new[] { (1, 0), (0, 1), (1, 1), (-1, 1) })
        {
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    if (level.At(x - dx, y - dy) >= 0) continue;

                    int before = -1;
                    var pending = new List<int>();
                    int xx = x;
                    int yy = y;

                    void Flush(int after)
                    {
                        int a = before >= 0 ? before : after;
                        int b = after >= 0 ? after : before;
                        if (a < 0 || b < 0) return;

                        float difference = Distance(mean[a], mean[b]);
                        float span = MathF.Sqrt(
                            Sq(Math.Abs(a % width - b % width)) + Sq(Math.Abs(a / width - b / width)));
                        float cost = difference + 0.02f * span + (before < 0 || after < 0 ? 1000f : 0f);

                        foreach (int i in pending)
                        {
                            if (cost >= costs[i]) continue;

                            float d = MathF.Sqrt(
                                Sq(Math.Abs(i % width - a % width)) + Sq(Math.Abs(i / width - a / width)));
                            float t = span > 0 ? d / span : 0;

                            guideColor[i] = difference < 900f ? Mix(mean[a], mean[b], t)
                                : t < 0.5f ? mean[a] : mean[b];
                            guideVariation[i] = variation[a] * (1 - t) + variation[b] * t;
                            costs[i] = cost;
                        }
                    }

                    while (true)
                    {
                        int i = level.At(xx, yy);
                        if (i < 0) break;

                        if (level.Known(i))
                        {
                            Flush(i);
                            pending.Clear();
                            before = i;
                        }
                        else if (level.Hole[i])
                        {
                            pending.Add(i);
                        }
                        else
                        {
                            Flush(-1);
                            pending.Clear();
                            before = -1;
                        }

                        xx += dx;
                        yy += dy;
                    }

                    Flush(-1);
                }
            }
        }

        return (guideColor, guideVariation);
    }

    /// <summary>可分离高斯（σ = 半径 / 2，边界夹取）。</summary>
    private static Rgb[] Gaussian(Rgb[] values, int width, int height, int radius, CancellationToken token)
    {
        float sigma = radius / 2f;
        var kernel = new float[radius * 2 + 1];
        float norm = 0;
        for (int i = -radius; i <= radius; i++)
        {
            float weight = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = weight;
            norm += weight;
        }

        var temp = new Rgb[values.Length];
        var output = new Rgb[values.Length];

        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = 0; k <= radius * 2; k++)
                {
                    int xx = Math.Clamp(x + k - radius, 0, width - 1);
                    var c = values[y * width + xx];
                    float w = kernel[k] / norm;
                    r += c.R * w;
                    g += c.G * w;
                    b += c.B * w;
                }

                temp[y * width + x] = new Rgb(r, g, b);
            }
        }

        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (int k = 0; k <= radius * 2; k++)
                {
                    int yy = Math.Clamp(y + k - radius, 0, height - 1);
                    var c = temp[yy * width + x];
                    float w = kernel[k] / norm;
                    r += c.R * w;
                    g += c.G * w;
                    b += c.B * w;
                }

                output[y * width + x] = new Rgb(r, g, b);
            }
        }

        return output;
    }

    // ============ 小工具 ============

    /// <summary>多源广度优先：每个像素到最近「种子」的距离（种子 = true 的像素）。</summary>
    private static int[]? Nearest(int width, int height, bool[] seeds, CancellationToken token)
    {
        var result = new int[seeds.Length];
        Array.Fill(result, -1);

        var queue = new Queue<int>();
        for (int i = 0; i < seeds.Length; i++)
        {
            if (!seeds[i]) continue;
            result[i] = i;
            queue.Enqueue(i);
        }

        if (queue.Count == 0) return null;

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (i % 4096 == 0) token.ThrowIfCancellationRequested();

            int x = i % width;
            int y = i / width;

            if (x > 0) Spread(i - 1);
            if (x + 1 < width) Spread(i + 1);
            if (y > 0) Spread(i - width);
            if (y + 1 < height) Spread(i + width);

            void Spread(int j)
            {
                if (result[j] >= 0) return;
                result[j] = result[i];
                queue.Enqueue(j);
            }
        }

        return result;
    }

    /// <summary>方形侵蚀（积分图，O(像素)）：窗口 (2r+1)² 全是 true 才留。</summary>
    private static bool[] Erode(bool[] mask, int width, int height, int radius)
    {
        var output = new bool[mask.Length];
        if (radius <= 0)
        {
            Array.Copy(mask, output, mask.Length);
            return output;
        }

        int stride = width + 1;
        var sums = new int[stride * (height + 1)];

        for (int y = 0; y < height; y++)
        {
            int row = 0;
            for (int x = 0; x < width; x++)
            {
                if (mask[y * width + x]) row++;
                sums[(y + 1) * stride + x + 1] = sums[y * stride + x + 1] + row;
            }
        }

        int side = radius * 2 + 1;
        for (int y = radius; y < height - radius; y++)
        {
            for (int x = radius; x < width - radius; x++)
            {
                int a = (y - radius) * stride + x - radius;
                int b = (y - radius) * stride + x + radius + 1;
                int c = (y + radius + 1) * stride + x - radius;
                int d = (y + radius + 1) * stride + x + radius + 1;
                output[y * width + x] = sums[d] + sums[a] - sums[b] - sums[c] == side * side;
            }
        }

        return output;
    }

    private static int Count(bool[] values)
    {
        int n = 0;
        for (int i = 0; i < values.Length; i++)
            if (values[i]) n++;
        return n;
    }

    /// <summary>sRGB → CIE Lab（借用 <see cref="Rgb"/> 装 L / a / b）。</summary>
    private static Rgb Lab(Rgb rgb)
    {
        static double Linear(float channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        double r = Linear(rgb.R);
        double g = Linear(rgb.G);
        double b = Linear(rgb.B);

        double x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
        double y = r * 0.2126729 + g * 0.7151522 + b * 0.0721750;
        double z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

        static double Curve(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;

        double fx = Curve(x);
        double fy = Curve(y);
        double fz = Curve(z);

        return new Rgb(
            (float)(116.0 * fy - 16.0),
            (float)(500.0 * (fx - fy)),
            (float)(200.0 * (fy - fz)));
    }

    private static float Distance(Rgb a, Rgb b) => Sq(a.R - b.R) + Sq(a.G - b.G) + Sq(a.B - b.B);

    private static float Sq(float value) => value * value;

    private static Rgb Mix(Rgb a, Rgb b, float t) => new(
        a.R * (1 - t) + b.R * t,
        a.G * (1 - t) + b.G * t,
        a.B * (1 - t) + b.B * t);
}
