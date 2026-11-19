using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Pdf;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图存成 PDF（照上游 <c>screenshotpdfexport.cpp</c> 的 <c>pageSizeForKey</c> /
/// <c>layout</c>，写出复用 <see cref="PdfWriter"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <c>image_size</c> = 页面就是图片本身（像素 × 72/96）；<c>a4_portrait</c> /
/// <c>a4_landscape</c> = A4（210 × 297 mm，横向转置），图片按
/// <c>min(页宽/图宽, 页高/图高)</c> 等比缩放后居中。
/// </para>
/// <para>
/// 布局是纯函数，<c>--selftest</c> 直接断言页面尺寸与居中偏移。
/// </para>
/// </remarks>
internal static class ScreenshotPdf
{
    private const double PointsPerInch = 72.0;
    private const double PixelsPerInch = 96.0;
    private const double MillimetersPerInch = 25.4;
    private const double A4WidthMm = 210.0;
    private const double A4HeightMm = 297.0;

    /// <summary>页面尺寸、图片落点与绘制尺寸（PDF 点）。</summary>
    public readonly record struct Layout(
        double PageWidth, double PageHeight,
        double ImageX, double ImageY, double ImageWidth, double ImageHeight);

    /// <summary>设置键 → 纸张（只认三个键，其余按 A4 竖版）。</summary>
    public static string PageSizeForKey(string? key) => ScreenshotBehavior.NormalizePdfPageSize(key);

    /// <summary>算页面与图片的落点（图片为空 = 全 0，调用方据此报错）。</summary>
    public static Layout Compute(int pixelWidth, int pixelHeight, string? pageSizeKey)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return default;

        string key = PageSizeForKey(pageSizeKey);
        double pageWidth = pixelWidth * PointsPerInch / PixelsPerInch;
        double pageHeight = pixelHeight * PointsPerInch / PixelsPerInch;

        if (key != ScreenshotBehavior.PdfImageSize)
        {
            pageWidth = A4WidthMm * PointsPerInch / MillimetersPerInch;
            pageHeight = A4HeightMm * PointsPerInch / MillimetersPerInch;
            if (key == ScreenshotBehavior.PdfA4Landscape) (pageWidth, pageHeight) = (pageHeight, pageWidth);
        }

        double scale = Math.Min(pageWidth / pixelWidth, pageHeight / pixelHeight);
        double imageWidth = pixelWidth * scale;
        double imageHeight = pixelHeight * scale;

        return new Layout(pageWidth, pageHeight,
            (pageWidth - imageWidth) / 2, (pageHeight - imageHeight) / 2,
            imageWidth, imageHeight);
    }

    /// <summary>把一张图写成单页 PDF（页面按当前设置）。</summary>
    public static void Save(Stream stream, BitmapSource image, int quality)
    {
        var layout = Compute(image.PixelWidth, image.PixelHeight, ScreenshotBehavior.PdfPageSize);
        if (layout.PageWidth <= 0 || layout.PageHeight <= 0)
            throw new InvalidOperationException("图片没有可以放进 PDF 的尺寸");

        var writer = new PdfWriter(stream);
        if (!writer.Put("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n")) throw new IOException("无法写入 PDF");

        int imageId = WriteImage(writer, image, quality);

        string content = "q\n" + PdfText.Number(layout.ImageWidth) + " 0 0 " +
                         PdfText.Number(layout.ImageHeight) + " " + PdfText.Number(layout.ImageX) +
                         " " + PdfText.Number(layout.ImageY) + " cm\n/Im0 Do\nQ\n";
        int contents = writer.Stream("", Encoding.ASCII.GetBytes(content));

        int pagesId = writer.NextId + 1;
        int page = writer.Object("<< /Type /Page /Parent " + pagesId + " 0 R /MediaBox [0 0 " +
                                 PdfText.Number(layout.PageWidth) + " " +
                                 PdfText.Number(layout.PageHeight) + "] /Resources << /XObject << /Im0 " +
                                 imageId + " 0 R >> >> /Contents " + contents + " 0 R >>");
        int pages = writer.Object($"<< /Type /Pages /Count 1 /Kids [{page} 0 R] >>");
        int root = writer.Object($"<< /Type /Catalog /Pages {pages} 0 R >>");
        string created = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        int info = writer.Object("<< /Title " + PdfText.PdfString("截图") + " /Creator " +
                                 PdfText.PdfString("LTools") + " /CreationDate (D:" + created + "Z) >>");
        if (!writer.Finish(root, info)) throw new IOException("无法写入 PDF");
    }

    /// <summary>按当前设置把图写成 PDF 文件。</summary>
    public static void SaveFile(string path, BitmapSource image, int quality = 100)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = File.Create(path);
        Save(stream, image, quality);
    }

    /// <summary>图片 XObject：质量 100 走 FlateDecode（带可选 SMask），其余走 DCTDecode。</summary>
    private static int WriteImage(PdfWriter writer, BitmapSource image, int quality)
    {
        if (Math.Clamp(quality, 1, 100) < 100)
        {
            byte[] jpeg = PdfImages.Jpeg(image, quality);
            return writer.Stream("/Type /XObject /Subtype /Image /Width " + image.PixelWidth +
                                 " /Height " + image.PixelHeight +
                                 " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode",
                jpeg);
        }

        byte[] rgb = PdfImages.RgbBytes(image);
        byte[] alpha = PdfImages.AlphaChannel(image, out bool translucent);
        string dictionary = "/Type /XObject /Subtype /Image /Width " + image.PixelWidth +
                            " /Height " + image.PixelHeight +
                            " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode";

        int mask = 0;
        if (translucent)
        {
            mask = writer.Stream("/Type /XObject /Subtype /Image /Width " + image.PixelWidth +
                                 " /Height " + image.PixelHeight +
                                 " /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode",
                PdfImages.Flate(alpha));
        }

        if (mask > 0) dictionary += " /SMask " + mask + " 0 R";
        return writer.Stream(dictionary, PdfImages.Flate(rgb));
    }
}
