using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 文字层与「译文渲进原图」共用的行绘制：字号拟合、底色、字色、框内裁剪。
/// </summary>
/// <remarks>
/// <para>
/// 画布（<see cref="CaptureOcrCanvas"/>）与导出位图（<see cref="OcrImageRenderer"/>）必须用同一套
/// 度量，否则屏幕上看到的字号 / 位置与复制出去的图片对不上。字号规则见
/// <see cref="OcrTextLayer.FitFontSize"/>：槽高的 82% 起、超宽缩到 98% 槽宽、下限 6 px；
/// 合并出来的段（<see cref="OcrTextLine.FontSize"/> 有值）按那个字号排版并在框里折行。
/// </para>
/// <para>
/// 字色 / 底色都来自 <see cref="OcrTextLayer"/> 的逐行估色（上游 <c>screenshotocrvisuals.cpp</c>）：
/// 上游按估出来的字色画文字，所以这里也不能写死黑字——深底截图那样会看不见。
/// </para>
/// </remarks>
internal static class OcrTextPaint
{
    private static readonly Typeface Face = new(new FontFamily("Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>量一行文字（<paramref name="pixelsPerDip"/>：屏幕取 DPI 缩放，导出图取 1）。</summary>
    public static FormattedText Create(string content, double size, double pixelsPerDip) =>
        new(content ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
            Math.Max(1, size), Brushes.Black, pixelsPerDip <= 0 ? 1 : pixelsPerDip);

    /// <summary>按行框拟合这一行的字号（源图像素）。</summary>
    public static (FormattedText Text, double Size) Fit(string content, OcrTextLine line,
        double pixelsPerDip)
    {
        Int32Rect box = line.Box;
        double baseSize = line.FontSize is > 0
            ? line.FontSize.Value
            : Math.Clamp(box.Height * 0.82, 6, 4096);

        FormattedText probe = Create(content, baseSize, pixelsPerDip);

        // 合并出来的段按成员行字号排版并折行（上游 fontSizeCapRatio），不再按单行宽度缩，
        // 否则一段话会被压成 6 px；单行仍按 98% 槽宽收字号。
        double fitted = line.FontSize is > 0
            ? baseSize
            : OcrTextLayer.FitFontSize(baseSize, probe.WidthIncludingTrailingWhitespace,
                Math.Max(1, box.Width));

        FormattedText text = Math.Abs(fitted - baseSize) < 0.01
            ? probe
            : Create(content, fitted, pixelsPerDip);
        text.MaxTextWidth = Math.Max(1, box.Width);
        text.MaxTextHeight = Math.Max(1, box.Height * 1.6);
        text.Trimming = TextTrimming.None;
        return (text, fitted);
    }

    /// <summary>画一行：铺底色（可选）+ 框内裁剪画文字（垂直居中）。</summary>
    public static void Draw(DrawingContext dc, OcrTextLine line, string content, bool fillBackground,
        double pixelsPerDip)
    {
        var box = new Rect(line.Box.X, line.Box.Y, Math.Max(1, line.Box.Width),
            Math.Max(1, line.Box.Height));

        if (fillBackground) dc.DrawRectangle(Brush(line.BackgroundColor), null, box);
        if (string.IsNullOrEmpty(content)) return;

        (FormattedText text, _) = Fit(content, line, pixelsPerDip);
        text.SetForegroundBrush(Brush(line.TextColor));

        dc.PushClip(new RectangleGeometry(box));
        dc.DrawText(text, new Point(box.X, box.Y + (box.Height - text.Height) / 2));
        dc.Pop();
    }

    /// <summary>字色 / 底色用的画刷（冻结，绘制时不必再复制）。</summary>
    public static Brush Brush(Rgb color)
    {
        var brush = new SolidColorBrush(color.ToColor());
        brush.Freeze();
        return brush;
    }
}
