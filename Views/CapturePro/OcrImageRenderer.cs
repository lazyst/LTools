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
    /// <param name="fillStyle">
    /// 文字区域填充样式（<c>background_fill</c> / <c>blur</c>，照上游 <c>text_recognition/fill_style</c>）；
    /// 空 = 背景填充。<c>blur</c> 需要 <paramref name="source"/>，没有就退回底色。
    /// </param>
    /// <param name="highlight">是否叠反向文字遮罩（整块压暗、字形区域留空）。</param>
    public static BitmapSource? Render(BitmapSource? source, IReadOnlyList<OcrTextLine> layer,
        IReadOnlyList<string> texts, bool includeSource, bool fillBackground,
        string? fillStyle = null, bool highlight = false)
    {
        if (layer.Count == 0) return null;

        int width = includeSource && source != null ? source.PixelWidth : Extent(layer, true);
        int height = includeSource && source != null ? source.PixelHeight : Extent(layer, false);
        if (width <= 0 || height <= 0) return null;

        bool blur = fillBackground && OcrFillStyle.IsBlur(fillStyle) && source != null;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (includeSource && source != null)
                dc.DrawImage(source, new Rect(0, 0, width, height));

            // 1) 文字区域的底：模糊块 + 50% 容器底色，或逐行估出来的底色
            if (fillBackground)
            {
                Brush? blend = blur ? BlendBrush() : null;
                for (int i = 0; i < layer.Count; i++)
                {
                    OcrTextLine line = layer[i];
                    var box = new Rect(line.Box.X, line.Box.Y, Math.Max(1, line.Box.Width),
                        Math.Max(1, line.Box.Height));

                    BitmapSource? patch = blur
                        ? OcrBlur.BlurRegion(source, line.Box, OcrFillStyle.DeviceSigma(1))
                        : null;
                    if (patch != null)
                    {
                        dc.DrawImage(patch, box);
                        if (blend != null) dc.DrawRectangle(blend, null, box);
                    }
                    else
                    {
                        dc.DrawRectangle(OcrTextPaint.Brush(line.BackgroundColor), null, box);
                    }
                }
            }

            // 2) 反向文字遮罩（导出图与屏幕上同一形状）
            if (highlight) DrawHighlight(dc, layer, texts, width, height);

            // 3) 文字
            for (int i = 0; i < layer.Count; i++)
            {
                OcrTextLine line = layer[i];
                string content = i < texts.Count ? texts[i] : line.Text;
                // 导出图是 1 源像素 = 1 像素，PixelsPerDip 固定 1（与屏幕缩放无关）
                OcrTextPaint.Draw(dc, line, content, fillBackground: false, 1.0);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>反向文字遮罩：整块铺遮罩色、字形矩形挖空（与画布同一个形状）。</summary>
    private static void DrawHighlight(DrawingContext dc, IReadOnlyList<OcrTextLine> layer,
        IReadOnlyList<string> texts, int width, int height)
    {
        Color mask = OverlayUiSettings.SelectionMaskColor;
        if (mask.A == 0) return;

        var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));

        for (int i = 0; i < layer.Count; i++)
        {
            OcrTextLine line = layer[i];
            string content = i < texts.Count ? texts[i] : line.Text;
            var box = new Rect(line.Box.X, line.Box.Y, Math.Max(1, line.Box.Width),
                Math.Max(1, line.Box.Height));

            Rect hole;
            if (content.Length == 0)
            {
                hole = new Rect(box.X, box.Y, Math.Max(1, box.Width * 0.1), box.Height);
            }
            else
            {
                (FormattedText text, _) = OcrTextPaint.Fit(content, line, 1.0);
                double top = box.Y + (box.Height - text.Height) / 2;
                double w = Math.Min(box.Width, Math.Max(1, text.WidthIncludingTrailingWhitespace));
                hole = new Rect(box.X, top, w, Math.Max(1, text.Height));
            }

            geometry.Children.Add(new RectangleGeometry(hole));
        }

        dc.DrawGeometry(new SolidColorBrush(mask), null, geometry);
    }

    /// <summary>模糊块上面叠的 50% 容器底色（上游 <c>kBackgroundBlendAmount = 0.5</c>）。</summary>
    private static Brush BlendBrush()
    {
        Color color = (Application.Current?.TryFindResource("SurfaceBrush") as SolidColorBrush)?.Color
                      ?? Colors.White;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(OcrFillStyle.BackgroundBlendAmount * 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
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
