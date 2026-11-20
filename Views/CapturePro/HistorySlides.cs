using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>一张历史预览：图 + 这张图是什么（卡片角标 / 大图标题用）。</summary>
internal sealed record HistorySlide(BitmapSource? Image, string Label);

/// <summary>
/// 采集历史的预览轮播（照上游 <c>HistoryEntryWidget</c> 的 <c>AdCarousel</c>）：
/// **第一张是成品图，后面每块屏幕一张**（原始屏幕，不压暗、不画选框）。
/// </summary>
/// <remarks>
/// 卡片和查看器共用这一份：<paramref name="maxSide"/> 给缩略图的小尺寸，给大图就传大尺寸。
/// 每块屏的预览是从那张整屏底图上按记录下来的屏幕矩形裁的（<see cref="CaptureEntry.Screens"/>），
/// 所以多屏时是「屏幕 1 / 屏幕 2」，单屏时只有一张「整个屏幕」。
/// </remarks>
internal static class HistorySlides
{
    public const string ResultLabel = "成品图";

    public static List<HistorySlide> Load(CaptureEntry entry, int maxSide, BitmapSource? result = null)
    {
        var slides = new List<HistorySlide>();

        // 成品图（录屏没有静态成品，给一张空位让卡片照旧显示「没有封面帧」）
        result ??= entry.IsRecord
            ? null
            : CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id),
                DecodeWidth(entry.Width, maxSide));
        slides.Add(new HistorySlide(result, ResultLabel));

        if (!entry.HasDisplay) return slides;

        var display = CaptureCatalog.LoadImage(CaptureCatalog.DisplayFilePath(entry),
            DecodeWidth(entry.DisplayW, maxSide));
        if (display == null) return slides;

        var screens = entry.Screens.Count > 0
            ? entry.Screens
            : new List<CaptureScreenDto> { CaptureScreenDto.Of(entry.DisplayRect) };

        double scale = display.PixelWidth / (double)Math.Max(1, entry.DisplayW);
        int index = 0;

        foreach (var screen in screens)
        {
            index++;
            var crop = Crop(display, screen.Rect, entry.DisplayX, entry.DisplayY, scale);
            if (crop == null) continue;

            string label = screens.Count > 1 ? $"屏幕 {index}" : "整个屏幕";
            slides.Add(new HistorySlide(crop, label));
        }

        return slides;
    }

    /// <summary>
    /// 解码宽度：0 = 原分辨率；否则取「上限」与「原图宽」的较小值。
    /// <c>DecodePixelWidth</c> 会**放大**小图（卡片 260 的框解码 520），这里不让它发生——
    /// 小图放大只是多占内存，看起来还糊。
    /// </summary>
    private static int DecodeWidth(int natural, int maxSide) =>
        maxSide <= 0 ? 0 : natural > 0 ? Math.Min(maxSide, natural) : maxSide;

    /// <summary>把一块屏幕的矩形从整屏图上裁下来（屏幕物理像素 → 底图像素）。</summary>
    internal static BitmapSource? Crop(BitmapSource display, Int32Rect screen, int originX, int originY,
        double scale)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return null;

        double s = scale > 0 ? scale : 1;
        int x = (int)Math.Round((screen.X - originX) * s);
        int y = (int)Math.Round((screen.Y - originY) * s);
        int w = (int)Math.Round(screen.Width * s);
        int h = (int)Math.Round(screen.Height * s);

        x = Math.Clamp(x, 0, Math.Max(0, display.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, display.PixelHeight - 1));
        w = Math.Clamp(w, 1, display.PixelWidth - x);
        h = Math.Clamp(h, 1, display.PixelHeight - y);

        try
        {
            var crop = new CroppedBitmap(display, new Int32Rect(x, y, w, h));
            crop.Freeze();
            return crop;
        }
        catch (Exception ex)
        {
            AppLog.Warn("HistorySlides.Crop", "裁屏幕预览失败：" + ex.Message);
            return null;
        }
    }
}
