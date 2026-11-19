using System;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.Units.Files;

namespace LTools.Features.CapturePro;

/// <summary>
/// 自己写的 PNG 编码器：只有它能按「压缩力度」真的改变输出大小。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 <see cref="PngBitmapEncoder"/>：WPF 的编码器内部写死 zlib 的压缩级别，
/// 界面上给一个「压缩力度：低 / 中 / 高」却完全不起作用，那是假选项。
/// 这里走标准 PNG（8 bit RGBA / color type 6），逐行做「自适应过滤」
/// （None / Sub / Up / Average / Paeth 五选一，按 libpng 的启发式取绝对值和最小的那个），
/// 再用 <see cref="ZLibStream"/> 按力度压（低 = Fastest、中 = Optimal、高 = SmallestSize）。
/// </para>
/// <para>
/// 只输出 RGBA：截图原本就带 alpha（圆角 / 阴影），统一成 4 字节一像素最省心；
/// 输入是 Pbgra32 时先转成 Bgra32（PNG 要的是直通 alpha，不是预乘）。
/// </para>
/// </remarks>
internal static class PngWriter
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>编码成 PNG 字节（<paramref name="compression"/> 决定 deflate 力度）。</summary>
    public static byte[] Encode(BitmapSource image, ExportCompression compression)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        var (pixels, width, height) = Pixels(image);
        byte[] filtered = Filter(pixels, width, height);
        byte[] compressed = Deflate(filtered, compression);

        using var output = new MemoryStream(compressed.Length + 128);
        output.Write(Signature, 0, Signature.Length);

        var header = new byte[13];
        WriteInt32(header, 0, width);
        WriteInt32(header, 4, height);
        header[8] = 8;      // bit depth
        header[9] = 6;      // color type: RGBA
        header[10] = 0;     // compression: deflate
        header[11] = 0;     // filter: adaptive
        header[12] = 0;     // interlace: none
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", Array.Empty<byte>());

        return output.ToArray();
    }

    /// <summary>把图片读成 **RGBA** 的连续字节（stride = 宽 × 4）。</summary>
    /// <remarks>
    /// PNG 的 color type 6 按 R,G,B,A 排，而 WPF 的 <see cref="PixelFormats.Bgra32"/>
    /// 是 B,G,R,A —— 不交换红蓝，导出出来的图颜色是反的（浅蓝会变橙）。
    /// </remarks>
    internal static (byte[] Pixels, int Width, int Height) Pixels(BitmapSource image)
    {
        BitmapSource source = image;
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

        return (pixels, width, height);
    }

    // ============ 过滤 ============

    /// <summary>逐行挑一个过滤方式（返回 每行 1 字节过滤号 + stride 字节数据）。</summary>
    internal static byte[] Filter(byte[] pixels, int width, int height)
    {
        const int bpp = 4;
        int stride = width * 4;
        var result = new byte[(stride + 1) * height];

        var candidate = new byte[stride];
        var best = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            int previous = (y - 1) * stride;

            int bestType = 0;
            long bestScore = long.MaxValue;

            for (int type = 0; type < 5; type++)
            {
                long score = 0;
                for (int x = 0; x < stride; x++)
                {
                    byte value = pixels[row + x];
                    byte left = x >= bpp ? pixels[row + x - bpp] : (byte)0;
                    byte up = y > 0 ? pixels[previous + x] : (byte)0;
                    byte upLeft = x >= bpp && y > 0 ? pixels[previous + x - bpp] : (byte)0;

                    byte filtered = type switch
                    {
                        0 => value,
                        1 => (byte)(value - left),
                        2 => (byte)(value - up),
                        3 => (byte)(value - (byte)((left + up) >> 1)),
                        _ => (byte)(value - Paeth(left, up, upLeft)),
                    };

                    candidate[x] = filtered;
                    score += filtered < 128 ? filtered : 256 - filtered;
                }

                if (score >= bestScore) continue;
                bestScore = score;
                bestType = type;
                Buffer.BlockCopy(candidate, 0, best, 0, stride);
            }

            int at = y * (stride + 1);
            result[at] = (byte)bestType;
            Buffer.BlockCopy(best, 0, result, at + 1, stride);
        }

        return result;
    }

    private static byte Paeth(byte left, byte up, byte upLeft)
    {
        int p = left + up - upLeft;
        int da = Math.Abs(p - left);
        int db = Math.Abs(p - up);
        int dc = Math.Abs(p - upLeft);
        if (da <= db && da <= dc) return left;
        return db <= dc ? up : upLeft;
    }

    private static byte[] Deflate(byte[] data, ExportCompression compression)
    {
        var level = compression switch
        {
            ExportCompression.Low => CompressionLevel.Fastest,
            ExportCompression.High => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal,
        };

        using var output = new MemoryStream(data.Length / 2 + 64);
        using (var zlib = new ZLibStream(output, level, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    // ============ chunk ============

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt32(length, 0, data.Length);
        output.Write(length, 0, 4);

        var name = new byte[4];
        for (int i = 0; i < 4; i++) name[i] = (byte)type[i];
        output.Write(name, 0, 4);
        output.Write(data, 0, data.Length);

        var crcInput = new byte[4 + data.Length];
        Buffer.BlockCopy(name, 0, crcInput, 0, 4);
        Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);

        var crc = new byte[4];
        WriteInt32(crc, 0, unchecked((int)Crc32.Compute(crcInput)));
        output.Write(crc, 0, 4);
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
