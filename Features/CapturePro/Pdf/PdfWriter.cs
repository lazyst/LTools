using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Pdf;

/// <summary>
/// 顺序写 PDF 对象并记偏移（含 xref / trailer）：拼接导出与截图导出共用的唯一一份。
/// </summary>
/// <remarks>
/// 照上游 <c>collagepdfexport.cpp</c> / <c>screenshotpdfexport.cpp</c> 各自的 <c>Writer</c>：
/// 都是「对象号 = 偏移表长度」的顺序写法。**别在别处再抄一份**——xref 偏移错位只会
/// 得到一个打不开的 PDF，而且错在哪里很难看出来。
/// </remarks>
internal sealed class PdfWriter
{
    private readonly Stream _output;
    private readonly List<long> _offsets = new() { 0 };
    private long _position;

    public PdfWriter(Stream output) => _output = output;

    public int NextId => _offsets.Count;

    public bool Put(string text) => Put(Encoding.Latin1.GetBytes(text));

    public bool Put(byte[] bytes)
    {
        try
        {
            _output.Write(bytes, 0, bytes.Length);
            _position += bytes.Length;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public int Object(string contents)
    {
        int id = NextId;
        _offsets.Add(_position);
        Put(Encoding.ASCII.GetBytes(id + " 0 obj\n" + contents + "\nendobj\n"));
        return id;
    }

    public int Stream(string dictionary, byte[] data)
    {
        int id = NextId;
        _offsets.Add(_position);
        Put(Encoding.ASCII.GetBytes(id + " 0 obj\n<< " + dictionary + " /Length " + data.Length +
                                    " >>\nstream\n"));
        Put(data);
        Put("\nendstream\nendobj\n");
        return id;
    }

    public bool Finish(int root, int info)
    {
        long start = _position;
        var builder = new StringBuilder();
        builder.Append("xref\n0 ").Append(_offsets.Count).Append("\n0000000000 65535 f \n");
        for (int index = 1; index < _offsets.Count; index++)
            builder.Append(_offsets[index].ToString("D10", CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(_offsets.Count).Append(" /Root ").Append(root)
            .Append(" 0 R /Info ").Append(info).Append(" 0 R >>\nstartxref\n").Append(start)
            .Append("\n%%EOF\n");
        return Put(builder.ToString());
    }
}

/// <summary>PDF 里出现的两种文本写法：数字（不变区域小数点）与 UTF-16BE 字面串。</summary>
internal static class PdfText
{
    public static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string PdfString(string value)
    {
        var builder = new StringBuilder("<FEFF");
        foreach (char character in value)
            builder.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        builder.Append('>');
        return builder.ToString();
    }
}

/// <summary>PDF 图片流：质量 100 走 FlateDecode（RGB + 可选 SMask），其余走 DCTDecode JPEG。</summary>
internal static class PdfImages
{
    /// <summary>zlib 压缩（PDF 的 <c>FlateDecode</c> 要的是 zlib 头，不是裸 deflate）。</summary>
    public static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    /// <summary>位图 → 直通色 RGB（Bgra32 / Pbgra32 都按颜色通道取，忽略 alpha）。</summary>
    public static byte[] RgbBytes(BitmapSource image)
    {
        var pixels = Raw(image);
        var rgb = new byte[(long)image.PixelWidth * image.PixelHeight * 3];
        long to = 0;
        for (long from = 0; from < pixels.Length; from += 4)
        {
            rgb[to++] = pixels[from + 2];
            rgb[to++] = pixels[from + 1];
            rgb[to++] = pixels[from];
        }

        return rgb;
    }

    /// <summary>取 alpha 通道，<paramref name="translucent"/> = 存在非不透明像素。</summary>
    public static byte[] AlphaChannel(BitmapSource image, out bool translucent)
    {
        var pixels = Raw(image);
        var alpha = new byte[(long)image.PixelWidth * image.PixelHeight];
        translucent = false;
        long to = 0;
        for (long from = 3; from < pixels.Length; from += 4)
        {
            byte value = pixels[from];
            alpha[to++] = value;
            if (value != 255) translucent = true;
        }

        return alpha;
    }

    /// <summary>JPEG：先把 alpha 铺到白底，再按质量编码。</summary>
    public static byte[] Jpeg(BitmapSource image, int quality)
    {
        var pixels = Raw(image);
        var rgb = new byte[(long)image.PixelWidth * image.PixelHeight * 3];
        long to = 0;
        for (long from = 0; from < pixels.Length; from += 4)
        {
            int alpha = pixels[from + 3];
            int red = (pixels[from + 2] * alpha + 255 * (255 - alpha) + 127) / 255;
            int green = (pixels[from + 1] * alpha + 255 * (255 - alpha) + 127) / 255;
            int blue = (pixels[from] * alpha + 255 * (255 - alpha) + 127) / 255;
            rgb[to++] = (byte)red;
            rgb[to++] = (byte)green;
            rgb[to++] = (byte)blue;
        }

        var bitmap = BitmapSource.Create(image.PixelWidth, image.PixelHeight, 96, 96,
            PixelFormats.Bgr24, null, rgb, image.PixelWidth * 3);
        bitmap.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>统一取 Bgra32 原始像素（Pbgra32 也转过来，alpha 语义不变）。</summary>
    public static byte[] Raw(BitmapSource image)
    {
        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }
}
