using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>文字的水平对齐（上游 <c>SnowCanvasTextHorizontalAlign</c>）。</summary>
internal enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 文字的一整套画法（上游 <c>SnowCanvasTextStyle</c>）。**不可变**：改参数 = 换一份
/// <see cref="TextSpec"/>，标注本身只读。
/// </summary>
/// <param name="FontFamily">字体族；空 = 项目默认（微软雅黑 UI，与其它文字一致）。</param>
/// <param name="Color">字色。</param>
/// <param name="Stroke">描边色（<paramref name="StrokeWidth"/> 为 0 时不画）。</param>
/// <param name="Fill">背景填充色（透明 = 默认没有背景）。</param>
internal readonly record struct TextSpec(
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    TextAlign Align,
    Color Color,
    double StrokeWidth,
    Color Stroke,
    Color Fill,
    FillKind FillStyle,
    double CornerRadius);

/// <summary>
/// 文字的排版与上色（上游 <c>snow_canvas_text_render.cpp</c> + <c>text_fill_outset</c> 的移植）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把每一行文字画成一块**背景胶囊**：行框向外扩「行高 × 0.32（横）/ 0.1（纵）」，
/// 圆角钳到 <c>min(宽, 高) / 2</c>，填充方式复用 <see cref="FillKind"/>（实心 / 斜线 / 交叉）；
/// 斜线的参考线宽是 <c>字号 / 24</c>（不是文字的描边宽），见 <c>drawTextBackgroundFill</c>。
/// </para>
/// <para>
/// 描边是**单独一遍**：把字形转成几何后只描边（上游用 <c>QTextCharFormat::setTextOutline</c>
/// 配「前景透明 + 描边笔」），所以描边不会盖住字色，字也不会被描边吃掉。
/// </para>
/// </remarks>
internal static class TextInk
{
    /// <summary>没有指定字体族时用项目统一的那一支。</summary>
    public const string FallbackFamily = "Microsoft YaHei UI";

    /// <summary>上游 <c>SnowCanvasTextStyle::fontSize</c> 的默认值。</summary>
    public const double DefaultFontSize = 30;

    /// <summary>字号范围（上游 <c>snow_canvas_style_limits</c>：6–256）。</summary>
    public const double MinFontSize = 6;
    public const double MaxFontSize = 256;

    /// <summary>文字描边宽度上限（上游 <c>kMaxTextStrokeWidth</c>）。</summary>
    public const double MaxStrokeWidth = 72;

    /// <summary>背景圆角上限（上游 <c>kMaxTextCornerRadius</c>）。</summary>
    public const double MaxCornerRadius = 83;

    /// <summary>上游描边 / 背景的默认色（<c>screenshotdefaultstyles.h</c> 的 <c>text.stroke</c>）。</summary>
    public static readonly Color DefaultStroke = Color.FromRgb(0xFF, 0xCC, 0xC7);

    /// <summary>行高（上游 <c>text_line_height</c> = 字号 × 1.2，是背景内边距的参考值）。</summary>
    public static double LineHeight(double fontSize) => Math.Max(1, fontSize) * 1.2;

    /// <summary>每行背景的横向内边距（行高 × 0.32）。</summary>
    public static double PadX(double fontSize) => LineHeight(fontSize) * 0.32;

    /// <summary>每行背景的纵向内边距（行高 × 0.1）。</summary>
    public static double PadY(double fontSize) => LineHeight(fontSize) * 0.1;

    /// <summary>背景看不看得见（上游 <c>fill_style.is_visible</c>：Border / 透明都算没有）。</summary>
    public static bool HasFill(TextSpec spec) => spec.FillStyle != FillKind.None && spec.Fill.A > 0;

    /// <summary>要不要画描边。</summary>
    public static bool HasStroke(TextSpec spec) => spec.StrokeWidth > 0.01 && spec.Stroke.A > 0;

    /// <summary>按 spec 的字体族 / 粗 / 斜造一份字体（不存在的族由 WPF 自己回退）。</summary>
    public static Typeface TypefaceOf(TextSpec spec) => TypefaceOf(spec.FontFamily, spec.Bold, spec.Italic);

    public static Typeface TypefaceOf(string family, bool bold, bool italic) => new(
        new FontFamily(string.IsNullOrWhiteSpace(family) ? FallbackFamily : family),
        italic ? FontStyles.Italic : FontStyles.Normal,
        bold ? FontWeights.Bold : FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>统一换行符后按行切（上游按 <c>\n</c> 分段）。</summary>
    public static string[] Lines(string content) =>
        (content ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static double SizeOf(TextSpec spec) => Math.Clamp(spec.FontSize, MinFontSize, MaxFontSize);

    /// <summary>排版后的一行：左偏移（按对齐算好）与行顶（相对文字原点）。</summary>
    public sealed record LineBox(string Text, Size Size, double X, double Top);

    /// <summary>逐行排版：总宽取最长一行，每行按对齐在总宽里定位。</summary>
    public static IReadOnlyList<LineBox> Layout(string content, TextSpec spec)
    {
        var face = TypefaceOf(spec);
        double size = SizeOf(spec);

        var measured = new List<(string Text, Size Size)>();
        double width = 1;
        foreach (string line in Lines(content))
        {
            var format = Format(line, face, size);
            var extent = new Size(
                Math.Max(1, format.WidthIncludingTrailingWhitespace),
                Math.Max(1, format.Height));
            measured.Add((line, extent));
            width = Math.Max(width, extent.Width);
        }

        var boxes = new List<LineBox>(measured.Count);
        double top = 0;
        foreach (var (text, extent) in measured)
        {
            double x = spec.Align switch
            {
                TextAlign.Center => (width - extent.Width) / 2,
                TextAlign.Right => width - extent.Width,
                _ => 0,
            };
            boxes.Add(new LineBox(text, extent, x, top));
            top += extent.Height;
        }

        return boxes;
    }

    /// <summary>墨迹尺寸（不含背景内边距与描边外溢）。</summary>
    public static Size Measure(string content, TextSpec spec)
    {
        var boxes = Layout(content, spec);
        double width = 1, height = 0;
        foreach (var box in boxes)
        {
            width = Math.Max(width, box.Size.Width);
            height += box.Size.Height;
        }
        return new Size(width, Math.Max(height, 1));
    }

    /// <summary>
    /// 命中 / 选中框：墨迹 + 背景胶囊的内边距 + 描边外溢（三项取最大的那个，不叠加——
    /// 上游 <c>text_paint_outset</c> 就是 <c>max(fill padding, stroke halo)</c>）。
    /// </summary>
    public static Rect BoundsOf(Point origin, string content, TextSpec spec)
    {
        var size = Measure(content, spec);
        double padX = HasFill(spec) ? PadX(spec.FontSize) : 0;
        double padY = HasFill(spec) ? PadY(spec.FontSize) : 0;
        double halo = HasStroke(spec) ? spec.StrokeWidth / 2 + 1 : 0;
        double marginX = Math.Max(padX, halo);
        double marginY = Math.Max(padY, halo);
        return new Rect(origin.X - marginX, origin.Y - marginY,
            size.Width + marginX * 2, size.Height + marginY * 2);
    }

    /// <summary>画一条文字标注：先铺每行的背景胶囊，再描边、最后填字。</summary>
    public static void Draw(DrawingContext dc, Point origin, string content, TextSpec spec)
    {
        var boxes = Layout(content, spec);
        if (boxes.Count == 0) return;

        var face = TypefaceOf(spec);
        double size = SizeOf(spec);

        if (HasFill(spec))
        {
            double padX = PadX(size);
            double padY = PadY(size);
            foreach (var line in boxes)
            {
                var rect = new Rect(
                    origin.X + line.X - padX,
                    origin.Y + line.Top - padY,
                    line.Size.Width + padX * 2,
                    line.Size.Height + padY * 2);
                if (rect.Width <= 0.01 || rect.Height <= 0.01) continue;

                double radius = Math.Min(spec.CornerRadius, Math.Min(rect.Width, rect.Height) / 2);
                Geometry geometry = radius > 0.01
                    ? new RectangleGeometry(rect, radius, radius)
                    : new RectangleGeometry(rect);
                geometry.Freeze();

                // 上游文字背景的斜线参考线宽 = 字号 / 24（`kTextFillReferenceFontSize`）
                ShapeInk.PaintFillGeometry(dc, geometry, spec.Fill, spec.FillStyle, size / 24.0);
            }
        }

        Pen? stroke = null;
        if (HasStroke(spec))
        {
            var pen = new Pen(Frozen(spec.Stroke), spec.StrokeWidth)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                MiterLimit = 2,
            };
            pen.Freeze();
            stroke = pen;
        }

        var ink = Frozen(spec.Color);
        foreach (var line in boxes)
        {
            if (line.Text.Length == 0) continue;
            var format = Format(line.Text, face, size);
            var geometry = format.BuildGeometry(new Point(origin.X + line.X, origin.Y + line.Top));
            if (geometry == null) continue;
            geometry.Freeze();

            if (stroke != null) dc.DrawGeometry(null, stroke, geometry);
            dc.DrawGeometry(ink, null, geometry);
        }
    }

    private static FormattedText Format(string line, Typeface face, double size) => new(
        line.Length == 0 ? " " : line,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        face,
        size,
        Brushes.Black,
        1.0);

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
