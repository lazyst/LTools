using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using LTools.Native;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace LTools.Features;

/// <summary>
/// 屏幕图像匹配（OpenCV <c>MatchTemplate</c>），支持等待、区域限制与多缩放。
/// </summary>
/// <remarks>
/// 模板匹配的坐标系是虚拟桌面物理像素（与鼠标注入一致）。匹配成功返回模板中心点，
/// 后续「鼠标点击」可直接用 <c>${m.x}</c> / <c>${m.y}</c>。
/// 多缩放候选用于适配 Windows 缩放比例变化（模板在 100% 截取、运行在 125% 时仍能命中）。
/// </remarks>
internal static class ImageProbe
{
    /// <summary>常见 Windows 缩放对应的模板缩放候选；命中过的比例下次优先尝试。</summary>
    private static readonly double[] Scales = { 1.0, 1.25, 1.5, 0.8, 1.75 };

    /// <summary>执行一次（可带等待的）图像探测。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        string path = ImageLibrary.Resolve(probe.Image);
        if (path.Length == 0 || !System.IO.File.Exists(path))
            throw new FlowException($"图像文件不存在：{probe.Image}");

        using var template = Cv2.ImRead(path, ImreadModes.Color);
        if (template.Empty())
            throw new FlowException($"图像无法读取（可能不是 PNG/JPG）：{path}");

        var (rx, ry, rw, rh) = ResolveRegion(probe);
        int timeout = Math.Clamp(probe.TimeoutMs, 0, 600_000);
        int interval = Math.Clamp(probe.IntervalMs, 50, 5000);
        double minScore = Math.Clamp(probe.MinScore, 0.30, 0.99);
        long deadline = Environment.TickCount64 + timeout;
        double? preferredScale = null;
        double bestScore = 0;

        while (true)
        {
            var (found, x, y, score, scale) = MatchOnce(rx, ry, rw, rh, template, minScore, preferredScale, ctx);
            if (score > bestScore) bestScore = score;
            if (found)
            {
                preferredScale = scale;   // 后续同一次运行里的多次探测直接命中同一比例
                return VarValue.FromMatch(x, y, score);
            }

            if (timeout == 0) break;
            long remain = deadline - Environment.TickCount64;
            if (remain <= 0) break;

            int wait = (int)Math.Min(interval, remain);
            if (!ctx.Wait(wait)) throw new FlowAbortException("已停止", userCancelled: true);
        }

        return new VarValue { Text = "", Flag = false, Score = bestScore };
    }

    private static (bool found, int x, int y, double score, double scale) MatchOnce(
        int rx, int ry, int rw, int rh, Mat template, double minScore, double? preferred, RunContext ctx)
    {
        using var screen = CaptureBgr(rx, ry, rw, rh);
        if (screen.Empty() || template.Width > screen.Width || template.Height > screen.Height)
            return (false, 0, 0, 0, 1.0);

        var order = new List<double>(Scales.Length + 1);
        if (preferred is double p) order.Add(p);
        foreach (double s in Scales)
            if (Math.Abs(s - (preferred ?? -1)) > 0.001) order.Add(s);

        double best = 0;
        int bestX = 0, bestY = 0;
        double bestScale = 1.0;

        foreach (double scale in order)
        {
            if (ctx.IsCancelled) throw new FlowAbortException("已停止", userCancelled: true);

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
            }

            if (best >= minScore) break;   // 已够用，不再试其它缩放
        }

        if (best < minScore) return (false, 0, 0, best, bestScale);

        return (true, bestX, bestY, best, bestScale);
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

    /// <summary>解析搜索区域；前台窗口无效时退回整个虚拟桌面。</summary>
    private static (int x, int y, int w, int h) ResolveRegion(Probe probe)
    {
        var full = (ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                    ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);

        if (probe.Region == ImageRegion.screen) return full;

        if (probe.Region == ImageRegion.rect)
        {
            int[]? r = probe.Rect;
            if (r == null || r.Length < 4 || r[2] <= 0 || r[3] <= 0) return full;
            return (r[0], r[1], r[2], r[3]);
        }

        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !Win32.GetWindowRect(hwnd, out var rect)) return full;
        int w = rect.Right - rect.Left;
        int h = rect.Bottom - rect.Top;
        if (w <= 0 || h <= 0) return full;
        return (rect.Left, rect.Top, w, h);
    }
}
