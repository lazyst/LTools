using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using LTools.Core.Actions;
using LTools.Native;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace LTools.Features;

/// <summary>一次模板匹配的命中：中心点 + 命中的框（模板尺寸按命中缩放）+ 相似度。</summary>
internal sealed class ImageHit
{
    /// <summary>命中中心 X（虚拟桌面物理像素）。</summary>
    public int X { get; init; }

    /// <summary>命中中心 Y（虚拟桌面物理像素）。</summary>
    public int Y { get; init; }

    /// <summary>命中框宽（= 模板宽 × 命中缩放）。</summary>
    public int W { get; init; }

    /// <summary>命中框高。</summary>
    public int H { get; init; }

    /// <summary>相似度（0–1）。</summary>
    public double Score { get; init; }
}

/// <summary>一次找图的结果：命中列表（按分数降序）+ 看过的最高分（没找到时报错用）。</summary>
internal sealed class ImageProbeResult
{
    public IReadOnlyList<ImageHit> Hits { get; init; } = Array.Empty<ImageHit>();

    public double BestScore { get; init; }
}

/// <summary>
/// 屏幕图像匹配（OpenCV <c>MatchTemplate</c>），支持等待、区域限制与多缩放。
/// </summary>
/// <remarks>
/// <para>
/// 模板匹配的坐标系是虚拟桌面物理像素（与鼠标注入一致）。匹配成功返回模板中心点，
/// 后续「鼠标点击」可直接用 <c>${m.x}</c> / <c>${m.y}</c>。
/// 多缩放候选用于适配 Windows 缩放比例变化（模板在 100% 截取、运行在 125% 时仍能命中）。
/// </para>
/// <para>
/// 两张入口：<see cref="RunSingle"/>（只求最高分那个，行为与以前一致）与
/// <see cref="RunAll"/>（同一张模板出现多次时全部列出来）。多命中不额外截图、也不额外匹配——
/// <c>MatchTemplate</c> 本来就一次产出整张分数图，多取几个峰值只是后处理。
/// </para>
/// </remarks>
internal static class ImageProbe
{
    /// <summary>常见 Windows 缩放对应的模板缩放候选；命中过的比例下次优先尝试。</summary>
    private static readonly double[] Scales = { 1.0, 1.25, 1.5, 0.8, 1.75 };

    /// <summary>一次最多收集多少个命中（防止纯色区域刷出成千上万个点）。</summary>
    internal const int MaxHits = 200;

    /// <summary>执行一次（可带等待的）图像探测，只返回最高分的那个命中。</summary>
    public static VarValue Run(Probe probe, RunContext? ctx)
    {
        var result = RunSingle(probe, ctx);
        if (result.Hits.Count == 0)
            return new VarValue { Text = "", Flag = false, Score = result.BestScore };

        ImageHit hit = result.Hits[0];
        return VarValue.FromMatch(hit.X, hit.Y, hit.Score);
    }

    /// <summary>只求最高分的那个命中（与 <see cref="Run"/> 同一条路径，结果保持结构化）。</summary>
    public static ImageProbeResult RunSingle(Probe probe, RunContext? ctx)
    {
        using Mat template = LoadTemplate(probe, ctx);

        var (rx, ry, rw, rh) = ScreenRegion.Resolve(probe);
        int timeout = Timeouts.Loop(probe.TimeoutMs);
        int interval = Math.Clamp(probe.IntervalMs, 50, 5000);
        double minScore = Math.Clamp(probe.MinScore, 0.30, 0.99);
        long deadline = Environment.TickCount64 + timeout;
        double? preferredScale = null;
        double bestScore = 0;

        while (true)
        {
            var (found, x, y, w, h, score, _) =
                MatchOnce(rx, ry, rw, rh, template, minScore, preferredScale, ctx);
            if (score > bestScore) bestScore = score;
            if (found)
            {
                return new ImageProbeResult
                {
                    Hits = new[] { new ImageHit { X = x, Y = y, W = w, H = h, Score = score } },
                    BestScore = bestScore,
                };
            }

            if (timeout == 0) break;
            long remain = deadline - Environment.TickCount64;
            if (remain <= 0) break;

            int wait = (int)Math.Min(interval, remain);
            if (ctx != null)
            {
                if (!ctx.Wait(wait)) throw new FlowAbortException("已停止", userCancelled: true);
            }
            else
            {
                System.Threading.Thread.Sleep(wait);
            }
        }

        return new ImageProbeResult { BestScore = bestScore };
    }

    /// <summary>
    /// 找出所有命中（同一张模板出现多次）。等待语义与 <see cref="RunSingle"/> 一致：
    /// 命中数达到 <paramref name="minCount"/> 就返回，超时返回最后一次看到的那些。
    /// </summary>
    /// <param name="limit">最多收集几个（1–200）。</param>
    /// <param name="minCount">等到至少几个才算「找到」（点第 N 个时传 N）。</param>
    public static ImageProbeResult RunAll(Probe probe, RunContext? ctx, int limit, int minCount = 1)
    {
        limit = Math.Clamp(limit, 1, MaxHits);
        minCount = Math.Clamp(minCount, 1, limit);

        using Mat template = LoadTemplate(probe, ctx);

        var (rx, ry, rw, rh) = ScreenRegion.Resolve(probe);
        int timeout = Timeouts.Loop(probe.TimeoutMs);
        int interval = Math.Clamp(probe.IntervalMs, 50, 5000);
        double minScore = Math.Clamp(probe.MinScore, 0.30, 0.99);
        long deadline = Environment.TickCount64 + timeout;
        double bestScore = 0;
        IReadOnlyList<ImageHit> last = Array.Empty<ImageHit>();

        while (true)
        {
            var found = new List<ImageHit>();
            using (Mat screen = CaptureBgr(rx, ry, rw, rh))
            {
                if (!screen.Empty())
                {
                    // 多命中时每个缩放都要试：不同 DPI 的元素可能只在某一个比例下命中。
                    foreach (double scale in Scales)
                    {
                        if (ctx?.IsCancelled == true)
                            throw new FlowAbortException("已停止", userCancelled: true);
                        CollectScale(screen, template, scale, rx, ry, minScore, limit, found, ctx);
                    }
                }
            }

            foreach (ImageHit hit in found)
                if (hit.Score > bestScore) bestScore = hit.Score;

            last = Dedupe(found, limit);
            if (last.Count >= minCount)
                return new ImageProbeResult { Hits = last, BestScore = bestScore };

            if (timeout == 0) break;
            long remain = deadline - Environment.TickCount64;
            if (remain <= 0) break;

            int wait = (int)Math.Min(interval, remain);
            if (ctx != null)
            {
                if (!ctx.Wait(wait)) throw new FlowAbortException("已停止", userCancelled: true);
            }
            else
            {
                System.Threading.Thread.Sleep(wait);
            }
        }

        return new ImageProbeResult { Hits = last, BestScore = bestScore };
    }

    private static (bool found, int x, int y, int w, int h, double score, double scale) MatchOnce(
        int rx, int ry, int rw, int rh, Mat template, double minScore, double? preferred, RunContext? ctx)
    {
        using var screen = CaptureBgr(rx, ry, rw, rh);
        if (screen.Empty() || template.Width > screen.Width || template.Height > screen.Height)
            return (false, 0, 0, 0, 0, 0, 1.0);

        var order = new List<double>(Scales.Length + 1);
        if (preferred is double p) order.Add(p);
        foreach (double s in Scales)
            if (Math.Abs(s - (preferred ?? -1)) > 0.001) order.Add(s);

        double best = 0;
        int bestX = 0, bestY = 0, bestW = 0, bestH = 0;
        double bestScale = 1.0;

        foreach (double scale in order)
        {
            if (ctx?.IsCancelled == true) throw new FlowAbortException("已停止", userCancelled: true);

            using var scaled = ResizeTemplate(template, scale);
            if (scaled == null || scaled.Width > screen.Width || scaled.Height > screen.Height)
                continue;

            using var result = new Mat();
            Cv2.MatchTemplate(screen, scaled, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out CvPoint maxLoc);

            double score = double.IsNaN(maxVal) ? 0 : maxVal;
            if (score > best)
            {
                best = score;
                bestScale = scale;
                // 模板中心（虚拟桌面坐标）
                bestX = rx + maxLoc.X + scaled.Width / 2;
                bestY = ry + maxLoc.Y + scaled.Height / 2;
                bestW = scaled.Width;
                bestH = scaled.Height;
            }

            if (best >= minScore) break;   // 已够用，不再试其它缩放
        }

        if (best < minScore) return (false, 0, 0, 0, 0, best, bestScale);

        return (true, bestX, bestY, bestW, bestH, best, bestScale);
    }

    /// <summary>
    /// 一个缩放下的多命中：反复取最高分，并把它的「模板尺寸」邻域清零。
    /// 邻域清零就是一次 NMS——同一个目标在模板范围内只保留分数最高的那一点。
    /// </summary>
    private static void CollectScale(Mat screen, Mat template, double scale,
        int rx, int ry, double minScore, int limit, List<ImageHit> into, RunContext? ctx)
    {
        using var scaled = ResizeTemplate(template, scale);
        if (scaled == null || scaled.Width > screen.Width || scaled.Height > screen.Height) return;

        using var result = new Mat();
        Cv2.MatchTemplate(screen, scaled, result, TemplateMatchModes.CCoeffNormed);

        for (int i = 0; i < limit; i++)
        {
            if (ctx?.IsCancelled == true) throw new FlowAbortException("已停止", userCancelled: true);

            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out CvPoint maxLoc);
            if (double.IsNaN(maxVal) || maxVal < minScore) return;

            into.Add(new ImageHit
            {
                X = rx + maxLoc.X + scaled.Width / 2,
                Y = ry + maxLoc.Y + scaled.Height / 2,
                W = scaled.Width,
                H = scaled.Height,
                Score = maxVal,
            });

            // 抹掉这一整块（负值 < minScore，下一轮自然跳过）
            Cv2.Rectangle(result,
                new OpenCvSharp.Rect(maxLoc.X, maxLoc.Y, scaled.Width, scaled.Height),
                Scalar.All(-1), -1);
        }
    }

    /// <summary>
    /// 跨缩放的命中去重：按分数降序，与已保留的框重叠超过「较小框一半面积」的丢掉。
    /// 同一目标会在 1.0 / 1.25 两个缩放里各命中一次，不去重就会变成两个目标。
    /// </summary>
    internal static IReadOnlyList<ImageHit> Dedupe(List<ImageHit> hits, int limit)
    {
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));

        var kept = new List<ImageHit>(Math.Min(limit, hits.Count));
        foreach (ImageHit hit in hits)
        {
            bool overlap = false;
            foreach (ImageHit other in kept)
            {
                if (!Overlaps(hit, other)) continue;
                overlap = true;
                break;
            }
            if (overlap) continue;

            kept.Add(hit);
            if (kept.Count >= limit) break;
        }
        return kept;
    }

    /// <summary>两个命中框是否「说的是同一个目标」：交叠面积超过较小框的一半。</summary>
    internal static bool Overlaps(ImageHit a, ImageHit b)
    {
        int aLeft = a.X - a.W / 2, aTop = a.Y - a.H / 2;
        int bLeft = b.X - b.W / 2, bTop = b.Y - b.H / 2;

        int left = Math.Max(aLeft, bLeft);
        int top = Math.Max(aTop, bTop);
        int right = Math.Min(aLeft + a.W, bLeft + b.W);
        int bottom = Math.Min(aTop + a.H, bTop + b.H);
        if (right <= left || bottom <= top) return false;

        long inter = (long)(right - left) * (bottom - top);
        long smaller = Math.Min((long)a.W * a.H, (long)b.W * b.H);
        return smaller > 0 && inter * 2 > smaller;
    }

    private static Mat LoadTemplate(Probe probe, RunContext? ctx)
    {
        string path = ImageLibrary.Resolve(probe.Image, ctx?.ResourceRoot);
        if (path.Length == 0 || !System.IO.File.Exists(path))
        {
            throw new FlowException(ctx?.ResourceRoot is { Length: > 0 } root
                ? $"图像文件不存在：{probe.Image}（草稿资源目录 {root} 里也没有；草稿图片请放在 files/ 下）"
                : $"图像文件不存在：{probe.Image}");
        }

        Mat template = Cv2.ImRead(path, ImreadModes.Color);
        if (template.Empty())
        {
            template.Dispose();
            throw new FlowException($"图像无法读取（可能不是 PNG/JPG）：{path}");
        }
        return template;
    }

    private static Mat? ResizeTemplate(Mat template, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001) return template.Clone();

        int w = (int)Math.Round(template.Width * scale);
        int h = (int)Math.Round(template.Height * scale);
        if (w < 2 || h < 2) return null;

        var dst = new Mat();
        Cv2.Resize(template, dst, new CvSize(w, h), 0, 0, InterpolationFlags.Area);
        return dst;
    }

    private static Mat CaptureBgr(int x, int y, int w, int h)
    {
        using Bitmap bmp = ScreenCapture.Capture(x, y, w, h);
        using Mat bgra = BitmapToMat(bmp);
        var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private static Mat BitmapToMat(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var view = Mat.FromPixelData(bmp.Height, bmp.Width, MatType.CV_8UC4, data.Scan0, data.Stride);
            return view.Clone();
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
