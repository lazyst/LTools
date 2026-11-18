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
    /// <remarks>
    /// 用户改过的默认值（<c>settings.json</c> 的 <c>PinText</c> 段，见 <see cref="PinTextStore"/>）
    /// 盖在主题值上面：字体 / 字号 / 最大宽直接用，字色 / 底色选了「跟随主题」才用主题色。
    /// </remarks>
    public static PinTextStyle Current()
    {
        var style = new PinTextStyle
        {
            FontFamily = string.IsNullOrWhiteSpace(SystemFonts.MessageFontFamily?.Source)
                ? "Segoe UI"
                : SystemFonts.MessageFontFamily.Source,
            FontSize = PinTextRules.DefaultFontSize,
            Padding = DefaultPadding,
            MaximumWidth = PinTextRules.DefaultMaximumWidth,
        };

        if (BrushColor("TextBrush") is { } text) style.TextColor = text;
        if (BrushColor("SurfaceBrush") is { } background) style.BackgroundColor = background;
        if (BrushColor("SecondaryTextBrush") is { } secondary) style.SecondaryColor = secondary;

        var limits = PinTextStore.Limits();
        style.MaximumWidth = limits.MaximumWidth;
        style.FontSize = limits.FontSize;
        if (!string.IsNullOrWhiteSpace(limits.FontFamily)) style.FontFamily = limits.FontFamily;
        if (limits.TextColor is { } custom) style.TextColor = custom;
        if (limits.BackgroundColor is { } customBackground) style.BackgroundColor = customBackground;
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
/// 贴图文字样式默认值的归一规则：范围内的数字、能认的颜色写法、字体族名字。
/// </summary>
/// <remarks>
/// 照上游 <c>pin_to_screen/text_*</c> 的 schema（<c>configurationschema.cpp:1549</c>）：
/// 最大宽 100–2000（默认 1024）、字号 8–96（默认 16）、颜色 <c>system</c> = 跟随主题。
/// 全部是纯函数，<c>--selftest</c> 直接断言；读写盘在 <see cref="PinTextStore"/>。
/// </remarks>
internal static class PinTextRules
{
    /// <summary>最大文字宽的下限（px）。</summary>
    public const int MinMaximumWidth = 100;

    /// <summary>最大文字宽的上限（px）。</summary>
    public const int MaxMaximumWidth = 2000;

    /// <summary>最大文字宽的默认值（px，上游 <c>pin_to_screen/text_maximum_width</c>）。</summary>
    public const int DefaultMaximumWidth = 1024;

    /// <summary>字号的下限（px）。</summary>
    public const int MinFontSize = 8;

    /// <summary>字号的上限（px）。</summary>
    public const int MaxFontSize = 96;

    /// <summary>字号的默认值（px，上游 <c>pin_to_screen/text_font_size</c>）。</summary>
    public const int DefaultFontSize = 16;

    /// <summary>颜色的「跟随主题」写法（上游同名字符串）。</summary>
    public const string SystemColor = "system";

    /// <summary>字体族名字的长度上限（上游 1024）。</summary>
    public const int MaxFontFamilyLength = 1024;

    /// <summary>最大宽：缺省 / 越界夹到 100–2000。</summary>
    public static int NormalizeMaximumWidth(int? value) =>
        value is not { } v ? DefaultMaximumWidth : Math.Clamp(v, MinMaximumWidth, MaxMaximumWidth);

    /// <summary>字号：缺省 / 越界夹到 8–96。</summary>
    public static int NormalizeFontSize(int? value) =>
        value is not { } v ? DefaultFontSize : Math.Clamp(v, MinFontSize, MaxFontSize);

    /// <summary>字体族：去空白；空 / 超长（&gt; 1024）= 系统默认（空串）。</summary>
    public static string NormalizeFontFamily(string? value)
    {
        string text = (value ?? "").Trim();
        return text.Length > MaxFontFamilyLength ? "" : text;
    }

    /// <summary>
    /// 颜色：空 / 缺省 / <c>system</c> = 跟随主题（<c>null</c>）；
    /// 认 <c>#RRGGBB</c> 与 <c>#AARRGGBB</c>（可省 <c>#</c>），认不出 = 跟随主题。
    /// </summary>
    public static Color? ParseColor(string? text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0 || string.Equals(value, SystemColor, StringComparison.OrdinalIgnoreCase))
            return null;

        value = value.TrimStart('#');
        if (value.Length != 6 && value.Length != 8) return null;

        try
        {
            byte alpha = value.Length == 8 ? Convert.ToByte(value.Substring(0, 2), 16) : (byte)255;
            int offset = value.Length == 8 ? 2 : 0;
            return Color.FromArgb(alpha,
                Convert.ToByte(value.Substring(offset, 2), 16),
                Convert.ToByte(value.Substring(offset + 2, 2), 16),
                Convert.ToByte(value.Substring(offset + 4, 2), 16));
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>颜色的写法：<c>null</c> = <c>system</c>；不透明写 <c>#RRGGBB</c>、半透明写 <c>#AARRGGBB</c>。</summary>
    public static string FormatColor(Color? color) => color is not { } c
        ? SystemColor
        : c.A == 255
            ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>归一后的默认值（读盘一次，直接可用）。</summary>
    public static (int MaximumWidth, int FontSize, string FontFamily, Color? TextColor, Color? BackgroundColor)
        Normalize(int? width, int? fontSize, string? fontFamily, string? textColor, string? background) =>
        (NormalizeMaximumWidth(width), NormalizeFontSize(fontSize), NormalizeFontFamily(fontFamily),
            ParseColor(textColor), ParseColor(background));
}

/// <summary>
/// 一张文字 / 颜色贴图**当时用的**样式快照（进贴图历史，贴回时按原样重画）。
/// </summary>
/// <remarks>
/// 照上游 <c>PinnedWindowRepository</c> 的 <c>text_style</c> 对象（<c>pinnedwindowrepository.cpp:597</c>）：
/// 最大宽 / 字体族 / 字号 / 字色 / 底色都存**具体值**（主题色在贴出那一刻就解析成 <c>#RRGGBB</c>），
/// 所以之后改默认值不会让已经贴过的文字变样。
/// </remarks>
internal sealed class PinTextSnapshot
{
    public int MaximumWidth { get; set; } = PinTextRules.DefaultMaximumWidth;

    public string FontFamily { get; set; } = "";

    public double FontSize { get; set; } = PinTextRules.DefaultFontSize;

    public string TextColor { get; set; } = "#18181B";

    public string BackgroundColor { get; set; } = "#FFFFFF";

    /// <summary>把一份样式拍成快照（颜色固定成 <c>#RRGGBB</c> / <c>#AARRGGBB</c>）。</summary>
    public static PinTextSnapshot Of(PinTextStyle style) => new()
    {
        MaximumWidth = PinTextRules.NormalizeMaximumWidth((int)Math.Round(style.MaximumWidth)),
        FontFamily = PinTextRules.NormalizeFontFamily(style.FontFamily),
        FontSize = PinTextRules.NormalizeFontSize((int)Math.Round(style.FontSize)),
        TextColor = PinTextRules.FormatColor(style.TextColor),
        BackgroundColor = PinTextRules.FormatColor(style.BackgroundColor),
    };

    /// <summary>按快照还原样式：字体 / 字号 / 最大宽 / 字色 / 底色都取快照里的值。</summary>
    public PinTextStyle ToStyle()
    {
        var style = PinTextStyle.Current();
        style.MaximumWidth = PinTextRules.NormalizeMaximumWidth(MaximumWidth);
        style.FontSize = PinTextRules.NormalizeFontSize((int)Math.Round(FontSize));
        if (!string.IsNullOrWhiteSpace(FontFamily)) style.FontFamily = FontFamily;
        if (PinTextRules.ParseColor(TextColor) is { } text) style.TextColor = text;
        if (PinTextRules.ParseColor(BackgroundColor) is { } background) style.BackgroundColor = background;
        return style;
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
