using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro;

/// <summary>
/// 取色：从冻结画面读一个像素，并给出可复制的文本（<c>#RRGGBB</c> / <c>rgb(r, g, b)</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 读的是**底图**（<see cref="CaptureDocument.Base"/>），不是画好的成品：标注、压暗、聚光都不该影响取到的颜色。
/// </para>
/// <para>
/// 纯函数（<c>--selftest</c> 直接断言）：坐标是画面的物理像素，越界返回 <c>null</c>，不抛异常——
/// 光标在窗口边缘时 ToImage 可能算出边界外的值，那是正常情况。
/// </para>
/// </remarks>
internal static class PickerColor
{
    /// <summary>光标旁那块预览格取几乘几的源像素（照上游 <c>kPreviewPickerSize = 11</c>）。</summary>
    public const int PatchSize = 11;

    /// <summary>预览格里一个源像素放大到多少 DIP（照上游 <c>kPreviewScale = 12</c>）。</summary>
    public const int PatchScale = 12;

    /// <summary>预览格边长（DIP）= 11 × 12 = 132。</summary>
    public const double PatchCanvas = PatchSize * PatchScale;

    /// <summary>读画面像素颜色（物理像素坐标）。越界 / 没有底图 = <c>null</c>。</summary>
    public static Color? Read(BitmapSource? image, int x, int y)
    {
        if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;
        if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) return null;

        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        var pixel = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);   // BGRA
    }

    /// <summary>十六进制文本（大写，复制的就是这一段）。</summary>
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// 光标周围 <see cref="PatchSize"/> × <see cref="PatchSize"/> 的像素块（BGRA32，行优先）。
    /// 越界时**边缘像素重复**（逐点 clamp，照上游 <c>updatePreview</c>）；没有底图 = <c>null</c>。
    /// </summary>
    public static byte[]? ReadPatch(BitmapSource? image, int x, int y)
    {
        if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0) return null;

        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int w = source.PixelWidth, h = source.PixelHeight;
        int size = PatchSize, half = size / 2;

        // 先把「光标 ± half」夹成一块真实存在的矩形，一次拷出来
        int left = Math.Clamp(x - half, 0, w - 1);
        int top = Math.Clamp(y - half, 0, h - 1);
        int right = Math.Clamp(x + half, 0, w - 1);
        int bottom = Math.Clamp(y + half, 0, h - 1);
        int rw = right - left + 1, rh = bottom - top + 1;

        var raw = new byte[rw * rh * 4];
        source.CopyPixels(new Int32Rect(left, top, rw, rh), raw, rw * 4, 0);

        var block = new byte[size * size * 4];
        for (int row = 0; row < size; row++)
        {
            int sy = Math.Clamp(y + row - half, top, bottom) - top;
            for (int col = 0; col < size; col++)
            {
                int sx = Math.Clamp(x + col - half, left, right) - left;
                int from = (sy * rw + sx) * 4;
                int to = (row * size + col) * 4;
                block[to] = raw[from];
                block[to + 1] = raw[from + 1];
                block[to + 2] = raw[from + 2];
                block[to + 3] = raw[from + 3];
            }
        }

        return block;
    }

    /// <summary>
    /// 压在这一格颜色上的字该用黑还是白（照上游 <c>readableTextColor</c>：
    /// 任一分量 &gt; 128 用黑，否则用白）。
    /// </summary>
    public static Color ReadableTextColor(Color color) =>
        color.R > 128 || color.G > 128 || color.B > 128
            ? Color.FromArgb(224, 0, 0, 0)
            : Color.FromArgb(217, 255, 255, 255);

    /// <summary>给 CSS 用的 rgb 文本（提示条里一起显示，方便看清分量）。</summary>
    public static string Rgb(Color color) => $"rgb({color.R}, {color.G}, {color.B})";

    /// <summary>光标旁那一条提示的正文。</summary>
    public static string Describe(Color color) => $"{Hex(color)} · {Rgb(color)}";

    /// <summary>
    /// 按取色器格式（<c>screenshot_ui/color_picker_format</c>）出文本：<c>#RRGGBB</c> /
    /// <c>RRGGBB</c> / <c>rgb(r, g, b)</c> / <c>hsl(h, s%, l%)</c> / <c>hsb(h, s%, b%)</c>。
    /// 写法与解析都在 <see cref="ColorText"/>（取色弹窗共用同一份）。
    /// </summary>
    public static string Format(Color color, string format) => ColorText.Format(color, format);

    /// <summary>RGB → HSL（色相 0–360 取整，饱和 / 亮度 0–100 小数），照 Qt 的 <c>QColor::toHsl</c>。</summary>
    public static (int Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2.0;
        double delta = max - min;

        if (delta <= 0) return (0, 0, lightness * 100.0);

        double saturation = delta / (1.0 - Math.Abs(2.0 * lightness - 1.0));
        double hue = max == r ? (g - b) / delta + (g < b ? 6 : 0)
            : max == g ? (b - r) / delta + 2
            : (r - g) / delta + 4;
        hue *= 60;

        return ((int)Math.Round(hue) % 360, saturation * 100.0, lightness * 100.0);
    }

    /// <summary>取色器上那一行坐标（照上游 <c>X: %1 Y: %2</c>，按四舍五入取整）。</summary>
    public static string PositionText(double x, double y) =>
        $"X: {Math.Round(x, MidpointRounding.AwayFromZero)} Y: {Math.Round(y, MidpointRounding.AwayFromZero)}";

    /// <summary>当前格式的「长什么样」（提示条里写一句，用户才知道单击会复制什么）。</summary>
    public static string FormatHint(string format) => ColorText.Hint(ColorText.KindOf(format));
}
