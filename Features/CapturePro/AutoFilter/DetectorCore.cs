using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>半开区间像素矩形（对应上游 <c>visual-region-detector</c> 的 <c>Rect</c>）。</summary>
internal readonly record struct RRect(int X, int Y, int W, int H)
{
    public int X2 => X + W;
    public int Y2 => Y + H;
    public long Area => (long)Math.Max(W, 0) * Math.Max(H, 0);
    public override string ToString() => $"{X},{Y} {W}x{H}";
}

/// <summary>矩形关系（逐行照抄上游 <c>geometry.rs</c>：半开区间、面积按非负宽高算）。</summary>
internal static class Geo
{
    public static RRect? Intersect(RRect a, RRect b)
    {
        int x = Math.Max(a.X, b.X);
        int y = Math.Max(a.Y, b.Y);
        int x2 = Math.Min(a.X2, b.X2);
        int y2 = Math.Min(a.Y2, b.Y2);
        return x2 > x && y2 > y ? new RRect(x, y, x2 - x, y2 - y) : null;
    }

    public static RRect Union(RRect a, RRect b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new RRect(x, y, Math.Max(a.X2, b.X2) - x, Math.Max(a.Y2, b.Y2) - y);
    }

    public static double IoU(RRect a, RRect b)
    {
        var hit = Intersect(a, b);
        if (hit == null) return 0;
        double inter = hit.Value.Area;
        return inter / (a.Area + b.Area - inter);
    }

    /// <summary><paramref name="a"/> 有多大比例落在 <paramref name="b"/> 里。</summary>
    public static double Containment(RRect a, RRect b)
    {
        if (a.Area == 0) return 0;
        var hit = Intersect(a, b);
        return hit == null ? 0 : hit.Value.Area / (double)a.Area;
    }

    public static RRect? Clip(RRect r, int w, int h)
    {
        var c = Intersect(r, new RRect(0, 0, w, h));
        return c is { W: >= 2, H: >= 2 } ? c : null;
    }

    public static RRect Expand(RRect r, int p, int w, int h) =>
        Clip(new RRect(r.X - p, r.Y - p, r.W + 2 * p, r.H + 2 * p), w, h) ?? r;
}

/// <summary>单通道 8 位掩码（上游 <c>Grid&lt;u8&gt;</c> 的 <c>Mask</c>）。</summary>
internal sealed class MaskGrid
{
    public MaskGrid(int w, int h, byte value = 0)
        : this(w, h, Fill(w, h, value))
    {
    }

    private MaskGrid(int w, int h, byte[] data)
    {
        W = w;
        H = h;
        Data = data;
    }

    public int W { get; }
    public int H { get; }
    public byte[] Data { get; }

    private static byte[] Fill(int w, int h, byte value)
    {
        var data = new byte[Math.Max(0, w) * Math.Max(0, h)];
        if (value != 0) Array.Fill(data, value);
        return data;
    }

    public byte At(int x, int y) => Data[y * W + x];

    public void Set(int x, int y, byte value) => Data[y * W + x] = value;

    public MaskGrid Clone() => new(W, H, (byte[])Data.Clone());

    public MaskGrid Crop(RRect r)
    {
        var data = new byte[Math.Max(0, r.W) * Math.Max(0, r.H)];
        for (int y = 0; y < r.H; y++)
            Array.Copy(Data, (r.Y + y) * W + r.X, data, y * r.W, r.W);
        return new MaskGrid(r.W, r.H, data);
    }
}

/// <summary>
/// 8 位三通道 BGR 图（上游 <c>BgrImage</c>）：通道顺序与 OpenCV 的 <c>CV_8UC3</c> 一致，
/// 灰度换算的系数就按这个顺序写。
/// </summary>
internal sealed class BgrImg
{
    public BgrImg(int w, int h)
    {
        W = w;
        H = h;
        Data = new byte[Math.Max(0, w) * Math.Max(0, h) * 3];
    }

    private BgrImg(int w, int h, byte[] data)
    {
        W = w;
        H = h;
        Data = data;
    }

    public int W { get; }
    public int H { get; }
    public byte[] Data { get; }

    /// <summary>WPF 的 <c>Bgra32</c> 缓冲 → BGR（丢 alpha，换通道序）。</summary>
    public static BgrImg FromBgra(byte[] bgra, int w, int h)
    {
        var image = new BgrImg(w, h);
        for (int i = 0, j = 0; i < image.Data.Length; i += 3, j += 4)
        {
            image.Data[i] = bgra[j];
            image.Data[i + 1] = bgra[j + 1];
            image.Data[i + 2] = bgra[j + 2];
        }
        return image;
    }

    public byte Ch(int x, int y, int c) => Data[(y * W + x) * 3 + c];

    public (byte B, byte G, byte R) At(int x, int y)
    {
        int i = (y * W + x) * 3;
        return (Data[i], Data[i + 1], Data[i + 2]);
    }

    public void Set(int x, int y, byte b, byte g, byte r)
    {
        int i = (y * W + x) * 3;
        Data[i] = b;
        Data[i + 1] = g;
        Data[i + 2] = r;
    }

    public BgrImg Clone() => new(W, H, (byte[])Data.Clone());

    public BgrImg Crop(RRect r)
    {
        var image = new BgrImg(r.W, r.H);
        for (int y = 0; y < r.H; y++)
            Array.Copy(Data, ((r.Y + y) * W + r.X) * 3, image.Data, y * r.W * 3, r.W * 3);
        return image;
    }
}

/// <summary>颜色统计（逐行照抄上游 <c>color.rs</c>）。</summary>
internal static class ColorStats
{
    /// <summary>中位数：偶数取中间两个的平均（与上游一致，返回浮点）。</summary>
    public static double Median(IReadOnlyList<byte> values)
    {
        if (values.Count == 0) return 0;
        var copy = new byte[values.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
        Array.Sort(copy);
        int n = copy.Length;
        return n % 2 == 0 ? (copy[n / 2 - 1] + (double)copy[n / 2]) / 2.0 : copy[n / 2];
    }

    public static double[] MedianColor(IEnumerable<(byte B, byte G, byte R)> pixels)
    {
        var b = new List<byte>();
        var g = new List<byte>();
        var r = new List<byte>();
        foreach (var (pb, pg, pr) in pixels)
        {
            b.Add(pb);
            g.Add(pg);
            r.Add(pr);
        }
        return new[] { Median(b), Median(g), Median(r) };
    }

    public static int QuantKey(byte b, byte g, byte r, int step)
    {
        int n = 256 / step;
        return (b / step * n + g / step) * n + r / step;
    }

    public static int[] Histogram(BgrImg image, RRect rect, int step)
    {
        int n = 256 / step;
        var hist = new int[n * n * n];
        for (int y = rect.Y; y < rect.Y2; y++)
            for (int x = rect.X; x < rect.X2; x++)
            {
                int i = (y * image.W + x) * 3;
                hist[QuantKey(image.Data[i], image.Data[i + 1], image.Data[i + 2], step)]++;
            }
        return hist;
    }

    public static double SurfaceFraction(BgrImg image, RRect rect)
    {
        var hist = Histogram(image, rect, 16);
        int best = 0;
        foreach (int value in hist) if (value > best) best = value;
        return best / (double)rect.Area;
    }

    /// <summary>四条边上出现最多的颜色（量化到 8）：整张图的背景色基准。</summary>
    public static double[] Background(BgrImg image)
    {
        int w = image.W, h = image.H;
        int t = Math.Max(4, Math.Min(w, h) / 80);
        var hist = new int[32768];
        var bands = new[]
        {
            new RRect(0, 0, w, Math.Min(t, h)),
            new RRect(0, Math.Max(h - t, 0), w, Math.Min(t, h)),
            new RRect(0, 0, Math.Min(t, w), h),
            new RRect(Math.Max(w - t, 0), 0, Math.Min(t, w), h),
        };
        foreach (var band in bands)
            for (int y = band.Y; y < band.Y2; y++)
                for (int x = band.X; x < band.X2; x++)
                {
                    int i = (y * w + x) * 3;
                    hist[QuantKey(image.Data[i], image.Data[i + 1], image.Data[i + 2], 8)]++;
                }

        int best = 0;
        for (int k = 1; k < hist.Length; k++)
            if (hist[k] > hist[best]) best = k;

        return new double[]
        {
            best / 1024 * 8 + 4,
            best / 32 % 32 * 8 + 4,
            best % 32 * 8 + 4,
        };
    }

    public static MaskGrid Foreground(BgrImg image, double[] bg, double threshold)
    {
        var mask = new MaskGrid(image.W, image.H);
        for (int i = 0, p = 0; i < mask.Data.Length; i++, p += 3)
        {
            byte b = image.Data[p], g = image.Data[p + 1], r = image.Data[p + 2];
            bool far = Math.Abs(b - bg[0]) > threshold
                       || Math.Abs(g - bg[1]) > threshold
                       || Math.Abs(r - bg[2]) > threshold;
            mask.Data[i] = far ? (byte)255 : (byte)0;
        }
        return mask;
    }

    public static MaskGrid LocalForeground(BgrImg image, BgrImg bg, int threshold)
    {
        var mask = new MaskGrid(image.W, image.H);
        for (int i = 0, p = 0; i < mask.Data.Length; i++, p += 3)
        {
            bool far = Math.Abs(image.Data[p] - bg.Data[p]) > threshold
                       || Math.Abs(image.Data[p + 1] - bg.Data[p + 1]) > threshold
                       || Math.Abs(image.Data[p + 2] - bg.Data[p + 2]) > threshold;
            mask.Data[i] = far ? (byte)255 : (byte)0;
        }
        return mask;
    }

    /// <summary>框内 / 框外（外扩 4 像素那一圈）中位色的最大单通道差。</summary>
    public static double BoundaryContrast(BgrImg image, RRect rect)
    {
        var outer = Geo.Expand(rect, 4, image.W, image.H);
        var ring = new List<(byte, byte, byte)>();
        for (int y = outer.Y; y < outer.Y2; y++)
            for (int x = outer.X; x < outer.X2; x++)
            {
                if (x < rect.X || x >= rect.X2 || y < rect.Y || y >= rect.Y2)
                    ring.Add(image.At(x, y));
            }
        if (ring.Count == 0) return 0;

        var inside = new List<(byte, byte, byte)>();
        for (int y = rect.Y; y < rect.Y2; y++)
            for (int x = rect.X; x < rect.X2; x++)
                inside.Add(image.At(x, y));

        double[] a = MedianColor(inside);
        double[] b = MedianColor(ring);
        double best = 0;
        for (int i = 0; i < 3; i++) best = Math.Max(best, Math.Abs(a[i] - b[i]));
        return best;
    }

    /// <summary>某个连通域内三通道标准差的平均（照片 / logo 用它跟纯色块区分开）。</summary>
    public static double RoiStd(BgrImg image, RRect rect, int[] labels, int id)
    {
        var sum = new double[3];
        double n = 0;
        for (int y = rect.Y; y < rect.Y2; y++)
            for (int x = rect.X; x < rect.X2; x++)
            {
                if (labels[y * image.W + x] != id) continue;
                n += 1;
                int i = (y * image.W + x) * 3;
                sum[0] += image.Data[i];
                sum[1] += image.Data[i + 1];
                sum[2] += image.Data[i + 2];
            }
        if (n == 0) return 0;

        var mean = new[] { sum[0] / n, sum[1] / n, sum[2] / n };
        var variance = new double[3];
        for (int y = rect.Y; y < rect.Y2; y++)
            for (int x = rect.X; x < rect.X2; x++)
            {
                if (labels[y * image.W + x] != id) continue;
                int i = (y * image.W + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    double d = image.Data[i + c] - mean[c];
                    variance[c] += d * d;
                }
            }
        double total = 0;
        for (int c = 0; c < 3; c++) total += Math.Sqrt(variance[c] / n);
        return total / 3;
    }
}
