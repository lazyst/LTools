using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 复制 / 保存截图时按显示器缩放降采样（照上游
/// <c>screenshothidpiimagescaling.h</c>：<c>screenshotHiDpiImageScale</c> /
/// <c>screenshotHiDpiImageSize</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <c>disabled</c> = 不缩；<c>auto</c> = 只看与选区相交的显示器，缩放比全部相同才
/// <c>1 / scale</c>（混合 DPI 的选区不缩，否则一半清晰一半模糊）；固定档位
/// <c>125…350</c>（每 25 一档）= <c>100 / percentage</c>。
/// </para>
/// <para>
/// 尺寸按上游 <c>qRound</c>（半数远离零）算，至少 1 像素。判定与尺寸都是纯函数，
/// <c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class HiDpiImageScaling
{
    /// <summary>一块显示器的物理矩形与缩放比（与选区同一套物理像素坐标）。</summary>
    public readonly record struct Display(Int32Rect Bounds, double Scale);

    /// <summary>缩放比（1.0 = 不缩）。</summary>
    public static double ScaleFor(string? mode, Int32Rect region, IReadOnlyList<Display> displays)
    {
        string key = ScreenshotBehavior.NormalizeHidpi(mode);

        if (key == ScreenshotBehavior.HidpiAuto)
        {
            double common = 0;
            foreach (var display in displays)
            {
                if (!Intersects(region, display.Bounds)) continue;
                if (!IsFinite(display.Scale) || display.Scale <= 0 ||
                    (common > 0 && !NearlyEqual(common, display.Scale)))
                    return 1.0;
                common = display.Scale;
            }

            return common > 1.0 ? 1.0 / common : 1.0;
        }

        if (!ScreenshotBehavior.TryParsePercentage(key, out int percentage)) return 1.0;
        return 100.0 / percentage;
    }

    /// <summary>按缩放比算输出像素尺寸（<c>qRound</c>：半数远离零，至少 1）。</summary>
    public static (int Width, int Height) OutputSize(int width, int height, double scale)
    {
        if (width <= 0 || height <= 0 || !IsFinite(scale) || scale <= 0 || scale > 1) return (0, 0);

        int w = Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero));
        int h = Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero));
        return (w, h);
    }

    /// <summary>按当前设置把成品图缩到输出尺寸；不需要缩时返回原实例。</summary>
    /// <param name="image">成品图（通常是选中区域 1:1 的抓帧）。</param>
    /// <param name="region">该图在屏幕上的物理矩形；空 = 不知道落点，按不缩。</param>
    public static BitmapSource Apply(BitmapSource image, Int32Rect? region)
    {
        if (region is not { Width: > 0, Height: > 0 } area) return image;

        double scale = ScaleFor(ScreenshotBehavior.HidpiImageScaling, area, Displays());
        if (scale >= 1.0 || scale <= 0) return image;

        var (width, height) = OutputSize(image.PixelWidth, image.PixelHeight, scale);
        if (width <= 0 || height <= 0 || (width == image.PixelWidth && height == image.PixelHeight))
            return image;

        return Resize(image, width, height);
    }

    /// <summary>高质量重采样到指定像素尺寸（<c>Pbgra32</c> 冻结位图）。</summary>
    public static BitmapSource Resize(BitmapSource image, int width, int height)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, width, height));
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>当前所有显示器（物理矩形 + 缩放比）。</summary>
    public static IReadOnlyList<Display> Displays()
    {
        var list = new List<Display>();
        foreach (var screen in Screens.All())
            list.Add(new Display(
                new Int32Rect(screen.Left, screen.Top, Math.Max(1, screen.Width), Math.Max(1, screen.Height)),
                screen.Scale));
        return list;
    }

    /// <summary>两个矩形是否有面积重叠（贴边不算，与 Qt 的 <c>QRectF::intersects</c> 一致）。</summary>
    public static bool Intersects(Int32Rect a, Int32Rect b) =>
        Math.Min(a.X + a.Width, b.X + b.Width) > Math.Max(a.X, b.X) &&
        Math.Min(a.Y + a.Height, b.Y + b.Height) > Math.Max(a.Y, b.Y);

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>上游 <c>qFuzzyCompare</c> 的同口径近似比较。</summary>
    private static bool NearlyEqual(double a, double b) =>
        Math.Abs(a - b) <= 1e-12 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
}
