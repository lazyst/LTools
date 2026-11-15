using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using LTools.Core.Actions;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace LTools.Features.Units.Screen;

/// <summary>
/// <c>image.qrcodeGen</c> / <c>image.qrcodeRead</c> 共用的纯计算（不碰 WPF / 配置 / 键鼠）。
/// </summary>
/// <remarks>
/// <para>
/// ZXing.Net 0.16.11 的 net5.0+ 主包**只含核心类**：<c>BitMatrix</c> 要自己画成位图、读图要自己
/// 解成 <c>RGBLuminanceSource</c>。所以不引 <c>ZXing.Net.Bindings.*</c>（那些绑定包拖 System.Drawing /
/// SkiaSharp），用项目里已有的 GDI+ <c>Bitmap</c> 就够，发布包只多核心 dll。
/// </para>
/// <para>
/// 像素按 <c>PixelFormat.Format32bppArgb</c> 读写，内存顺序是 <b>BGRA</b>；识别时必须显式传
/// <c>RGBLuminanceSource.BitmapFormat.BGRA32</c>，不带格式的 3 参构造按 24bpp 解释，会认不出。
/// </para>
/// </remarks>
internal static class QrCodeAlgorithms
{
    public static readonly string[] Levels = { "L", "M", "Q", "H" };

    internal readonly record struct DecodeResult(string? Text, string Type);

    /// <summary>把文本编成二维码位图（含安静区）；位图由调用方负责 Dispose。</summary>
    public static Bitmap Generate(string text, int size, int margin, string level)
    {
        size = (int)Math.Clamp(size, 64, 2048);
        margin = (int)Math.Clamp(margin, 0, 32);

        var hints = new Dictionary<EncodeHintType, object>
        {
            { EncodeHintType.CHARACTER_SET, "UTF-8" },
            { EncodeHintType.MARGIN, margin },
            { EncodeHintType.ERROR_CORRECTION, LevelOf(level) },
        };

        BitMatrix matrix = new QRCodeWriter().encode(text ?? "", BarcodeFormat.QR_CODE, size, size, hints);
        var bitmap = new Bitmap(matrix.Width, matrix.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, matrix.Width, matrix.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[data.Stride];
            for (int y = 0; y < matrix.Height; y++)
            {
                Array.Clear(row, 0, row.Length);
                for (int x = 0; x < matrix.Width; x++)
                {
                    byte v = (byte)(matrix[x, y] ? 0 : 255);
                    int i = x * 4;
                    row[i] = v; row[i + 1] = v; row[i + 2] = v; row[i + 3] = 255;
                }
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    /// <summary>识别位图里的二维码；没找到 / 解码异常一律返回 <c>Text = null</c>，不抛。</summary>
    public static DecodeResult Read(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] pixels;
        try
        {
            pixels = new byte[data.Stride * h];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // LockBits 的行可能有 padding；LuminanceSource 要的是每行紧挨着的 width*4 字节。
        if (data.Stride != w * 4)
        {
            byte[] tight = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Array.Copy(pixels, y * data.Stride, tight, y * w * 4, w * 4);
            pixels = tight;
        }

        var hints = new Dictionary<DecodeHintType, object>
        {
            { DecodeHintType.CHARACTER_SET, "UTF-8" },
            { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE } },
            { DecodeHintType.TRY_HARDER, true },
        };

        try
        {
            var source = new RGBLuminanceSource(pixels, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);
            var binary = new BinaryBitmap(new HybridBinarizer(source));
            Result? result = new MultiFormatReader().decode(binary, hints);
            return result == null
                ? new DecodeResult(null, "")
                : new DecodeResult(result.Text, result.BarcodeFormat.ToString());
        }
        catch (Exception)
        {
            // 畸形图 / 太糊 / 库内部异常都算「没识别到」，不该让整个动作失败。
            return new DecodeResult(null, "");
        }
    }

    /// <summary>纠错等级：认不出按 M（最常用）。</summary>
    internal static ErrorCorrectionLevel LevelOf(string? level) =>
        (level ?? "").Trim().ToUpperInvariant() switch
        {
            "L" => ErrorCorrectionLevel.L,
            "Q" => ErrorCorrectionLevel.Q,
            "H" => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M,
        };
}
