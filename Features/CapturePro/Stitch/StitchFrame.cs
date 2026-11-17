using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>
/// 一帧画面：Bgra32、行紧密排列（没有 padding）。
/// </summary>
/// <remarks>
/// 移植自 Snow Shot 的 <c>snow-stitch-images/src/frame.rs</c>。原实现有 Gray8 / Rgb8 / Rgba8 三种像素格式，
/// LTools 只吃 Bgra32，所以只保留这一种：通道数固定 4，可见像素比较一律忽略 alpha。
/// </remarks>
internal sealed class StitchFrame
{
    /// <summary>每像素字节数（Bgra32）。</summary>
    public const int Channels = 4;

    public StitchFrame(int width, int height, byte[] pixels)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width), "帧宽必须大于 0");
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height), "帧高必须大于 0");
        if (pixels.Length != (long)width * height * Channels)
            throw new ArgumentException("像素缓冲区长度不对", nameof(pixels));

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>一行多少字节。</summary>
    public int RowBytes => Width * Channels;

    public static StitchFrame Create(int width, int height) =>
        new(width, height, new byte[(long)width * height * Channels]);

    public static StitchFrame FromBitmap(BitmapSource source)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        int stride = width * Channels;
        var pixels = new byte[(long)stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        return new StitchFrame(width, height, pixels);
    }

    public BitmapSource ToBitmap()
    {
        var image = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, Pixels, RowBytes);
        image.Freeze();
        return image;
    }

    /// <summary>只比可见颜色（RGB），alpha 不参与——与 Snow Shot 的 <c>visible_pixels_equal</c> 一致。</summary>
    public bool VisiblePixelsEqual(StitchFrame other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Width != other.Width || Height != other.Height) return false;

        byte[] left = Pixels;
        byte[] right = other.Pixels;
        for (int index = 0; index < left.Length; index += Channels)
        {
            if (left[index] != right[index] ||
                left[index + 1] != right[index + 1] ||
                left[index + 2] != right[index + 2])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 只比「内部」（去掉最外一圈像素）的可见颜色。参考帧合成后可能逐像素等于上一帧输入，
    /// 这条捷径就是靠它判定的。
    /// </summary>
    public bool VisibleInteriorPixelsEqual(StitchFrame other)
    {
        if (Width != other.Width || Height != other.Height || Width < 3 || Height < 3) return false;

        byte[] left = Pixels;
        byte[] right = other.Pixels;
        int rowBytes = RowBytes;
        for (int y = 1; y < Height - 1; y++)
        {
            int start = y * rowBytes + Channels;
            int end = y * rowBytes + rowBytes - Channels;
            for (int index = start; index < end; index += Channels)
            {
                if (left[index] != right[index] ||
                    left[index + 1] != right[index + 1] ||
                    left[index + 2] != right[index + 2])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>沿轴的长度（纵向 = 高，横向 = 宽）。</summary>
    public int AxisExtent(StitchAxis axis) => axis == StitchAxis.Vertical ? Height : Width;

    /// <summary>垂直轴的长度（横向 = 宽）。</summary>
    public int CrossExtent(StitchAxis axis) => axis == StitchAxis.Vertical ? Width : Height;
}
