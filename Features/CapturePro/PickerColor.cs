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

    /// <summary>给 CSS 用的 rgb 文本（提示条里一起显示，方便看清分量）。</summary>
    public static string Rgb(Color color) => $"rgb({color.R}, {color.G}, {color.B})";

    /// <summary>光标旁那一条提示的正文。</summary>
    public static string Describe(Color color) => $"{Hex(color)} · {Rgb(color)}";
}
