using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 把文字层（原文或译文）渲染成一张图，供「复制译文为图片」使用。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>renderScreenshotRecognitionImage</c>（<c>screenshotrecognitionimage.cpp</c>）：
/// 底图 + 逐行文字 → 成品图。<c>includeSource = true</c> 就是上游的
/// <c>originalImageTranslation</c>（译文画进原图：底图是截图，原字被行底色盖掉，再画译文），
/// <c>false</c> 是上游的 <c>translationDocumentImage()</c>（只把文字排版渲成一张图）。
/// </para>
/// <para>
/// 画法与画布共用 <see cref="OcrTextPaint"/>：同一份字号拟合 / 颜色 / 折行，
/// 所以导出图和屏幕上看到的一致。渲染在调用线程（结果窗的 UI 线程）上跑，
/// <see cref="RenderTargetBitmap"/> 只在这一处用。
/// </para>
/// </remarks>
internal static class OcrImageRenderer
{
    /// <summary>
    /// 渲染一张图；没有可画的内容时返回 <c>null</c>（调用方据此退回复制文本）。
    /// </summary>
    /// <param name="source">原图（<paramref name="includeSource"/> 为真时作底图）。</param>
    /// <param name="layer">行框与颜色（原文层或译文层）。</param>
    /// <param name="texts">每一行要画的文字（与 <paramref name="layer"/> 对齐）。</param>
    /// <param name="includeSource">true = 译文画进原图；false = 只画文字（白底）。</param>
    /// <param name="fillBackground">是否铺每行估出来的底色（关掉只剩文字）。</param>
    public static BitmapSource? Render(BitmapSource? source, IReadOnlyList<OcrTextLine> layer,
        IReadOnlyList<string> texts, bool includeSource, bool fillBackground)
    {
        if (layer.Count == 0) return null;

        int width = includeSource && source != null ? source.PixelWidth : Extent(layer, true);
        int height = includeSource && source != null ? source.PixelHeight : Extent(layer, false);
        if (width <= 0 || height <= 0) return null;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (includeSource && source != null)
                dc.DrawImage(source, new Rect(0, 0, width, height));

            for (int i = 0; i < layer.Count; i++)
            {
                OcrTextLine line = layer[i];
                string content = i < texts.Count ? texts[i] : line.Text;
                // 导出图是 1 源像素 = 1 像素，PixelsPerDip 固定 1（与屏幕缩放无关）
                OcrTextPaint.Draw(dc, line, content, fillBackground, 1.0);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static int Extent(IReadOnlyList<OcrTextLine> layer, bool horizontal)
    {
        int max = 1;
        foreach (OcrTextLine line in layer)
        {
            int value = horizontal
                ? line.Box.X + Math.Max(1, line.Box.Width)
                : line.Box.Y + Math.Max(1, line.Box.Height);
            if (value > max) max = value;
        }

        return max;
    }
}
