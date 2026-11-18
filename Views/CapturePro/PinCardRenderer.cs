using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>卡片上的一个复制按钮：点了就把 <see cref="Text"/> 放进剪贴板。</summary>
/// <remarks>
/// 两个矩形就是上游那枚「复制」图标的两笔（后一笔压在先一笔上）。命中判定照上游的
/// <c>Qt::OddEvenFill</c>：点在两笔的**重叠处**不算命中——这是上游的行为，别改成并集。
/// </remarks>
internal sealed class PinCardCopy
{
    public PinCardCopy(string text, string label, Rect back, Rect front)
    {
        Text = text;
        Label = label;
        Back = back;
        Front = front;
    }

    /// <summary>点击后进剪贴板的规范 CSS。</summary>
    public string Text { get; }

    /// <summary>悬停提示（「复制 HEX 颜色值」）。</summary>
    public string Label { get; }

    /// <summary>图标第一笔（后一层的框线）。</summary>
    public Rect Back { get; }

    /// <summary>图标第二笔（前一层的框，填底色后再描边）。</summary>
    public Rect Front { get; }

    /// <summary>这个点（卡片坐标，DIP）算不算按在这个复制按钮上。</summary>
    public bool Hit(Point point) => Back.Contains(point) ^ Front.Contains(point);
}

/// <summary>渲染好的一张卡片：像素图 + 逻辑尺寸 + 复制按钮。</summary>
internal sealed class PinCardRender
{
    public PinCardRender(BitmapSource image, Size logical, PinContentKind kind,
        IReadOnlyList<PinCardCopy> copies)
    {
        Image = image;
        Logical = logical;
        Kind = kind;
        Copies = copies;
    }

    /// <summary>按屏幕缩放渲染出来的位图（已 Freeze）。</summary>
    public BitmapSource Image { get; }

    /// <summary>卡片按 DIP 的尺寸（= 贴图窗口在 100% 时的大小）。</summary>
    public Size Logical { get; }

    public PinContentKind Kind { get; }

    /// <summary>复制按钮（普通文字卡片是空的）。</summary>
    public IReadOnlyList<PinCardCopy> Copies { get; }
}

/// <summary>
/// 把「一段文字」画成贴图卡片：普通文字卡片，或颜色取样板（色块 + HEX / RGB / HSL 三行 + 复制按钮）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>renderOriginalText</c> / <c>renderPinnedColorSwatch</c>：贴到屏幕上的是一张**渲染好的图**，
/// 上游也是这样（文字贴图不可在图上划选，只有一个复制按钮可点），所以这里按屏幕缩放渲染成位图，
/// 之后缩放 / 缩略图 / 拖动 / 标注全部复用图片贴图那一条路径。
/// </para>
/// <para>
/// 颜色取样板必须用上游的度量：342×210（或旧版 470×288）、内边距 28×缩放、行高 30×缩放、
/// 行后 36×缩放、标签列 78×缩放、色块 28×缩放落在第一行的值列上、复制图标紧跟在值文本之后 6×缩放。
/// 缩放来自 <see cref="ColorCardLayout"/>，别再这里另写一套数字。
/// </para>
/// </remarks>
internal static class PinCardRenderer
{
    /// <summary>普通文字 / 色卡的总入口：颜色文本走色卡，其余走文字卡片。</summary>
    public static PinCardRender Render(string text, PinTextStyle style, double rasterScale,
        int cardWidth = ColorSwatch.CompactWidth)
    {
        var values = ColorSwatch.Parse(text);
        return values != null
            ? RenderColor(values, style, rasterScale, cardWidth)
            : RenderText(text, style, rasterScale);
    }

    /// <summary>把一段颜色画成取样板。</summary>
    public static PinCardRender RenderColor(ColorSwatchValues values, PinTextStyle style,
        double rasterScale, int cardWidth = ColorSwatch.CompactWidth)
    {
        var layout = ColorCardLayout.Of(cardWidth);
        var visual = new DrawingVisual();
        var copies = new List<PinCardCopy>(3);

        var typeface = TypefaceFor(style);
        double pixelsPerDip = ScaleOf(rasterScale);

        using (DrawingContext dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(style.BackgroundColor);
            background.Freeze();
            dc.DrawRectangle(background, null, new Rect(0, 0, layout.Width, layout.Height));

            Text(dc, ColorSwatch.ColorLabel, layout.Margin, layout.RowTop(0), typeface,
                ColorSwatch.FontSize, style.SecondaryColor, pixelsPerDip);

            DrawSwatch(dc, layout, values.Color, style);

            for (int row = 0; row < 3; row++)
            {
                double top = layout.RowTop(row + 1);
                Text(dc, values.Labels[row], layout.Margin, top, typeface,
                    ColorSwatch.FontSize, style.SecondaryColor, pixelsPerDip);

                var value = Text(dc, values.Display[row], layout.ValueX, top, typeface,
                    ColorSwatch.FontSize, style.TextColor, pixelsPerDip);

                double x = layout.ValueX + value.Width + 6 * layout.Scale;
                var button = new Rect(x, top + layout.Scale, layout.CopySize, layout.CopySize);
                var back = Inset(button, 10 * layout.Scale, 4 * layout.Scale, 5 * layout.Scale, 8 * layout.Scale);
                var front = Inset(button, 6 * layout.Scale, 8 * layout.Scale, 9 * layout.Scale, 4 * layout.Scale);

                DrawCopyIcon(dc, back, front, style, layout.Scale);
                copies.Add(new PinCardCopy(values.Clipboard[row], ColorSwatch.CopyLabels[row], back, front));
            }
        }

        return new PinCardRender(Rasterize(visual, layout.Width, layout.Height, rasterScale),
            new Size(layout.Width, layout.Height), PinContentKind.Color, copies);
    }

    /// <summary>把一段普通文字画成卡片（宽度按最大宽度折行，四周留内边距）。</summary>
    public static PinCardRender RenderText(string text, PinTextStyle style, double rasterScale)
    {
        string body = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        var typeface = TypefaceFor(style);
        double pixelsPerDip = ScaleOf(rasterScale);

        var measure = MakeText(body, typeface, style.FontSize, style.TextColor, pixelsPerDip);
        measure.MaxTextWidth = 100000;
        Size size = style.CardSize(measure.Width, measure.Height, out double wrapWidth);

        var wrapped = MakeText(body, typeface, style.FontSize, style.TextColor, pixelsPerDip);
        wrapped.MaxTextWidth = wrapWidth;
        size = style.CardSize(measure.Width, wrapped.Height, out wrapWidth);
        var final = MakeText(body, typeface, style.FontSize, style.TextColor, pixelsPerDip);
        final.MaxTextWidth = wrapWidth;

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(style.BackgroundColor);
            background.Freeze();
            dc.DrawRectangle(background, null, new Rect(0, 0, size.Width, size.Height));
            dc.DrawText(final, new Point(style.Padding, style.Padding));
        }

        return new PinCardRender(Rasterize(visual, size.Width, size.Height, rasterScale), size,
            PinContentKind.Text, Array.Empty<PinCardCopy>());
    }

    // ============ 画 ============

    private static void DrawSwatch(DrawingContext dc, ColorCardLayout layout, Color color, PinTextStyle style)
    {
        var swatch = new Rect(layout.SwatchX, layout.SwatchY, layout.SwatchSize, layout.SwatchSize);
        double radius = 3 * layout.Scale;

        dc.PushClip(new RectangleGeometry(swatch, radius, radius));

        // 半透明色下面的棋盘格（上游 7×7 一格，190 / 240 两色）
        double cell = 7 * layout.Scale;
        var dark = new SolidColorBrush(Color.FromRgb(190, 190, 190));
        var light = new SolidColorBrush(Color.FromRgb(240, 240, 240));
        dark.Freeze();
        light.Freeze();
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                dc.DrawRectangle((x + y) % 2 == 0 ? light : dark, null,
                    new Rect(swatch.X + x * cell, swatch.Y + y * cell, cell, cell));
            }
        }

        var fill = new SolidColorBrush(color);
        fill.Freeze();
        dc.DrawRectangle(fill, null, swatch);
        dc.Pop();

        var borderColor = style.SecondaryColor;
        var border = new SolidColorBrush(Color.FromArgb(100, borderColor.R, borderColor.G, borderColor.B));
        border.Freeze();
        var pen = new Pen(border, layout.Scale);
        pen.Freeze();
        dc.DrawRoundedRectangle(null, pen,
            new Rect(swatch.X + 0.5 * layout.Scale, swatch.Y + 0.5 * layout.Scale,
                swatch.Width - layout.Scale, swatch.Height - layout.Scale), radius, radius);
    }

    /// <summary>上游那枚复制图标：两笔错开的方框，后一笔先用底色盖住再描边。</summary>
    private static void DrawCopyIcon(DrawingContext dc, Rect back, Rect front, PinTextStyle style, double scale)
    {
        var link = new SolidColorBrush(style.LinkColor);
        link.Freeze();
        var pen = new Pen(link, 1.6 * scale);
        pen.Freeze();

        dc.DrawRectangle(null, pen, back);

        var background = new SolidColorBrush(style.BackgroundColor);
        background.Freeze();
        dc.DrawRectangle(background, null, front);
        dc.DrawRectangle(null, pen, front);
    }

    private static Rect Inset(Rect rect, double left, double top, double right, double bottom) =>
        new(rect.X + left, rect.Y + top, Math.Max(0, rect.Width - left - right),
            Math.Max(0, rect.Height - top - bottom));

    private static FormattedText Text(DrawingContext dc, string text, double x, double y,
        Typeface typeface, double size, Color color, double pixelsPerDip)
    {
        var formatted = MakeText(text, typeface, size, color, pixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
        return formatted;
    }

    private static FormattedText MakeText(string text, Typeface typeface, double size, Color color,
        double pixelsPerDip)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, pixelsPerDip);
    }

    private static Typeface TypefaceFor(PinTextStyle style)
    {
        var family = string.IsNullOrWhiteSpace(style.FontFamily)
            ? SystemFonts.MessageFontFamily
            : new FontFamily(style.FontFamily);
        return new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    private static double ScaleOf(double rasterScale) =>
        double.IsFinite(rasterScale) && rasterScale > 0 ? rasterScale : 1;

    /// <summary>把矢量卡片按屏幕缩放渲染成位图（96×缩放 DPI，逻辑坐标正好是 DIP）。</summary>
    private static BitmapSource Rasterize(Visual visual, double width, double height, double rasterScale)
    {
        double scale = ScaleOf(rasterScale);
        int pixelWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        int pixelHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
