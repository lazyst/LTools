using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>贴图内容是什么（进贴图历史、决定管理页怎么显示 / 怎么贴回）。</summary>
internal enum PinContentKind
{
    /// <summary>一张图（截图 / 剪贴板里的图 / 图片文件）。</summary>
    Image,

    /// <summary>一段普通文字（剪贴板里的文本 / HTML 的纯文本），贴成一张文字卡片。</summary>
    Text,

    /// <summary>一段颜色文本，贴成颜色取样板（色块 + HEX / RGB / HSL 三行 + 复制按钮）。</summary>
    Color,
}

/// <summary>
/// 文字贴图的样式：字体、字号、字色、底色、最大宽度、内边距。
/// </summary>
/// <remarks>
/// 照上游 <c>capturePinnedTextStyle()</c>（<c>src/presentation/services/pinnedtextstylecapture.cpp</c>）：
/// 字体取系统界面字体、字号 16、字色 / 底色取主题（取不到就用浅色主题的默认值），
/// 最大宽度默认 1024（上游 <c>pin_to_screen/text_maximum_width</c>），内边距 16（<c>kFormattedTextPadding</c>）。
/// 颜色取样板也用这里的字体与四个颜色，保证同一套观感。
/// </remarks>
internal sealed class PinTextStyle
{
    /// <summary>最大卡片宽度（含两侧内边距），照上游默认值。</summary>
    public const double DefaultMaximumWidth = 1024;

    /// <summary>文字与卡片边缘的距离（上游 <c>kFormattedTextPadding</c>）。</summary>
    public const double DefaultPadding = 16;

    public string FontFamily { get; set; } = "Segoe UI";

    public double FontSize { get; set; } = 16;

    /// <summary>正文字色（颜色取样板里是那三行值）。</summary>
    public Color TextColor { get; set; } = Color.FromRgb(0x18, 0x18, 0x1B);

    /// <summary>卡片底色。</summary>
    public Color BackgroundColor { get; set; } = Colors.White;

    /// <summary>次要字色（颜色取样板里的标签）。</summary>
    public Color SecondaryColor { get; set; } = Color.FromRgb(0x71, 0x71, 0x7A);

    /// <summary>链接色（颜色取样板里的复制图标）。</summary>
    public Color LinkColor { get; set; } = Color.FromRgb(0x16, 0x77, 0xFF);

    /// <summary>卡片最大宽度（含内边距；文字超宽会折行）。</summary>
    public double MaximumWidth { get; set; } = DefaultMaximumWidth;

    /// <summary>内边距。</summary>
    public double Padding { get; set; } = DefaultPadding;

    /// <summary>
    /// 取当前主题下的样式（在 UI 线程上调：要读 <c>Themes/Modern.xaml</c> 的笔刷）。
    /// 读不到资源（无头自检 / 启动早期）时退回浅色主题默认值，与上游 <c>QPalette</c> 同一口径。
    /// </summary>
    public static PinTextStyle Current()
    {
        var style = new PinTextStyle
        {
            FontFamily = string.IsNullOrWhiteSpace(SystemFonts.MessageFontFamily?.Source)
                ? "Segoe UI"
                : SystemFonts.MessageFontFamily.Source,
            FontSize = 16,
            Padding = DefaultPadding,
            MaximumWidth = DefaultMaximumWidth,
        };

        if (BrushColor("TextBrush") is { } text) style.TextColor = text;
        if (BrushColor("SurfaceBrush") is { } background) style.BackgroundColor = background;
        if (BrushColor("SecondaryTextBrush") is { } secondary) style.SecondaryColor = secondary;
        return style;
    }

    private static Color? BrushColor(string key)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush) return brush.Color;
        return null;
    }

    /// <summary>
    /// 文字卡片的尺寸（DIP）：宽度按「文字理想宽 + 两侧内边距」再夹到最大宽度，高度按折行后的高度。
    /// </summary>
    /// <param name="idealTextWidth">不折行时文字有多宽。</param>
    /// <param name="textHeight">在 <paramref name="wrapWidth"/> 下折行后的高度（由排版方回填）。</param>
    /// <param name="wrapWidth">真正用来排版的宽度（= 卡片宽 − 两侧内边距）。</param>
    public Size CardSize(double idealTextWidth, double textHeight, out double wrapWidth)
    {
        double width = Math.Ceiling(Math.Clamp(idealTextWidth + Padding * 2, 1, Math.Max(1, MaximumWidth)));
        wrapWidth = Math.Max(1, width - Padding * 2);
        return new Size(width, Math.Ceiling(Math.Max(1, textHeight + Padding * 2)));
    }
}

/// <summary>
/// 颜色取样板的版面度量（照上游 <c>renderPinnedColorSwatch</c> 的 342×210 / 470×288 与 16/22 缩放）。
/// </summary>
/// <remarks>
/// 全部是 DIP，卡片自己再按屏幕缩放渲染成像素。上游的四行文字：<c>颜色：</c> + 色块，
/// 然后 HEX / RGB / HSL 各一行；每行高 30（×缩放）、行后留 36（×缩放），
/// 标签列宽 78（×缩放），内边距 28（×缩放），色块 28（×缩放）落在第一行的值列上。
/// </remarks>
internal readonly record struct ColorCardLayout(
    int Width, int Height, double Scale, double Margin, double LineHeight, double RowGap, double Tab,
    double SwatchX, double SwatchY, double SwatchSize)
{
    /// <summary>按卡片宽度取度量（只认 342 / 470，其余按 342）。</summary>
    public static ColorCardLayout Of(int cardWidth)
    {
        var (width, height) = ColorSwatch.SizeOf(cardWidth);
        double scale = ColorSwatch.ScaleFor(width);
        return new ColorCardLayout(
            width, height, scale,
            Margin: 28 * scale,
            LineHeight: 30 * scale,
            RowGap: 36 * scale,
            Tab: 78 * scale,
            SwatchX: 106 * scale,
            SwatchY: 31 * scale,
            SwatchSize: 28 * scale);
    }

    /// <summary>第 <paramref name="row"/> 行（0–3）的顶端。</summary>
    public double RowTop(int row) => Margin + row * (LineHeight + RowGap);

    /// <summary>值列（HEX / RGB / HSL 三行的值、第一行的色块）的左边缘。</summary>
    public double ValueX => Margin + Tab;

    /// <summary>复制图标边长。</summary>
    public double CopySize => SwatchSize;
}

/// <summary>
/// 贴图出场时放在哪：照上游 <c>fitPinnedImageOnScreen</c> —— 光标所在显示器的工作区**居中**，
/// 装不下就等比缩到四周留 16 DIP 边距（<see cref="Margin"/>）。纯函数，<c>--selftest</c> 直接断言。
/// </summary>
/// <remarks>
/// 缩放同时是窗口的初始缩放（<c>Zoom</c>）：内容仍按 100% 渲染，贴出来时整体按这个比例缩，
/// 与上游「窗口原生尺寸 × 缩放百分比」同一口径。
/// </remarks>
internal static class PinPlacement
{
    /// <summary>装不下时四周留的边距（DIP，上游 <c>fitImageToAvailableGeometry</c> 的 16）。</summary>
    public const double Margin = 16;

    /// <summary>算出贴图的窗口矩形（DIP）与初始缩放。</summary>
    public static (Rect Box, double Zoom) Center(Size logical, Rect workArea, double margin = Margin)
    {
        double width = Math.Max(1, logical.Width);
        double height = Math.Max(1, logical.Height);
        double usableWidth = Math.Max(1, workArea.Width - Math.Max(0, margin) * 2);
        double usableHeight = Math.Max(1, workArea.Height - Math.Max(0, margin) * 2);

        double zoom = Math.Min(1, Math.Min(usableWidth / width, usableHeight / height));
        if (!(zoom > 0) || !double.IsFinite(zoom)) zoom = 1;

        double boxWidth = Math.Max(1, Math.Round(width * zoom));
        double boxHeight = Math.Max(1, Math.Round(height * zoom));
        double left = workArea.X + (workArea.Width - boxWidth) / 2;
        double top = workArea.Y + (workArea.Height - boxHeight) / 2;

        return (new Rect(left, top, boxWidth, boxHeight), zoom);
    }
}
