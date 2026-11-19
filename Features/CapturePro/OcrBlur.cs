using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro;

/// <summary>
/// 文字区域的模糊底（照上游 <c>fill_style = blur</c>）：三次盒式模糊逼近高斯。
/// </summary>
/// <remarks>
/// <para>
/// 上游用 <c>snowCanvasRegionFilter</c> 做降采样 + 高斯（<c>logicalSigma = 8</c>，再乘设备像素比）。
/// LTools 只模糊每行的小块，用三次盒式模糊逼近同一个 sigma：误差在 ±3% 以内，
/// 但代价是 O(像素 × 3 趟)，不需要降采样网格。
/// </para>
/// <para>
/// 算法本身是纯函数（<see cref="BoxRadii"/> / <see cref="BlurBgra"/> 直接吃字节数组），
/// <c>--selftest</c> 可以断言核大小与「模糊后比原来更平」；
/// 只有 <see cref="BlurRegion"/> 碰 WPF 位图。
/// </para>
/// </remarks>
internal static class OcrBlur
{
    /// <summary>
    /// 三次盒式模糊的核边长（上游 / Kutskir 的 fast-gaussian 推导）。
    /// </summary>
    /// <returns>三趟的盒半径（已按「核边长 = 2r + 1」折算）。</returns>
    public static (int First, int Second, int Third) BoxRadii(double sigma)
    {
        if (sigma <= 0 || double.IsNaN(sigma) || double.IsInfinity(sigma)) return (0, 0, 0);

        const int passes = 3;
        double ideal = Math.Sqrt(12.0 * sigma * sigma / passes + 1.0);
        int lower = (int)Math.Floor(ideal);
        if (lower % 2 == 0) lower--;
        if (lower < 1) lower = 1;
        int upper = lower + 2;

        double mIdeal = (12.0 * sigma * sigma - passes * lower * lower - 4.0 * passes * lower -
                         3.0 * passes) / (-4.0 * lower - 4.0);
        int m = (int)Math.Round(mIdeal);

        int Radius(int size) => Math.Max(0, (size - 1) / 2);
        return (Radius(m > 0 ? lower : upper), Radius(m > 1 ? lower : upper), Radius(upper));
    }

    /// <summary>
    /// 对 BGRA32 像素做三次盒式模糊（就地，alpha 不动）。
    /// </summary>
    /// <param name="pixels">BGRA32 像素（长度至少 <paramref name="stride"/> × <paramref name="height"/>）。</param>
    /// <param name="width">宽（像素）。</param>
    /// <param name="height">高（像素）。</param>
    /// <param name="stride">行字节数（通常 = 宽 × 4）。</param>
    /// <param name="sigma">高斯 sigma（设备像素）。</param>
    public static void BlurBgra(byte[] pixels, int width, int height, int stride, double sigma)
    {
        if (pixels == null || width < 1 || height < 1 || stride < width * 4) return;
        if (pixels.Length < (long)stride * height) return;

        var (first, second, third) = BoxRadii(sigma);
        foreach (int radius in new[] { first, second, third })
        {
            if (radius <= 0) continue;
            Horizontal(pixels, width, height, stride, radius);
            Vertical(pixels, width, height, stride, radius);
        }
    }

    /// <summary>
    /// 从源图裁一块并模糊，返回可直接 <c>DrawImage</c> 的冻结位图；
    /// 没有可模糊的像素时返回 <c>null</c>（调用方退回纯色底）。
    /// </summary>
    public static BitmapSource? BlurRegion(BitmapSource? source, Int32Rect rect, double sigma)
    {
        if (source == null) return null;

        int left = Math.Max(0, rect.X);
        int top = Math.Max(0, rect.Y);
        int right = Math.Min(source.PixelWidth, rect.X + Math.Max(0, rect.Width));
        int bottom = Math.Min(source.PixelHeight, rect.Y + Math.Max(0, rect.Height));
        if (right - left < 1 || bottom - top < 1) return null;

        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = right - left, height = bottom - top;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);

        BlurBgra(pixels, width, height, stride, sigma);

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels,
            stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Horizontal(byte[] pixels, int width, int height, int stride, int radius)
    {
        int window = radius * 2 + 1;
        byte[] row = new byte[width * 4];

        for (int y = 0; y < height; y++)
        {
            int offset = y * stride;
            Buffer.BlockCopy(pixels, offset, row, 0, width * 4);

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int i = -radius; i <= radius; i++) sum += row[Clamp(i, width) * 4 + channel];

                for (int x = 0; x < width; x++)
                {
                    pixels[offset + x * 4 + channel] = (byte)(sum / window);
                    sum += row[Clamp(x + radius + 1, width) * 4 + channel];
                    sum -= row[Clamp(x - radius, width) * 4 + channel];
                }
            }
        }
    }

    private static void Vertical(byte[] pixels, int width, int height, int stride, int radius)
    {
        int window = radius * 2 + 1;
        byte[] column = new byte[height * 4];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride + x * 4, column, y * 4, 4);

            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0;
                for (int i = -radius; i <= radius; i++) sum += column[Clamp(i, height) * 4 + channel];

                for (int y = 0; y < height; y++)
                {
                    pixels[y * stride + x * 4 + channel] = (byte)(sum / window);
                    sum += column[Clamp(y + radius + 1, height) * 4 + channel];
                    sum -= column[Clamp(y - radius, height) * 4 + channel];
                }
            }
        }
    }

    private static int Clamp(int value, int length) =>
        value < 0 ? 0 : value >= length ? length - 1 : value;
}
