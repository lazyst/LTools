using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Collage;

/// <summary>导出进度（照上游 <c>ImageStitchingPdfExportProgress</c>）。</summary>
internal readonly record struct CollagePdfExportProgress(int CompletedPages, int TotalPages,
    int PageNumber, double PageFraction);

/// <summary>导出结果（照上游 <c>ImageStitchingPdfExportResult</c>）。</summary>
internal sealed class CollagePdfExportResult
{
    public int TotalCount { get; set; }

    public List<int> CompletedPages { get; } = new();

    public List<string> Paths { get; } = new();

    public bool Cancelled { get; set; }

    public string Error { get; set; } = "";

    public bool Succeeded =>
        !Cancelled && Error.Length == 0 && TotalCount > 0 &&
        CompletedPages.Count == TotalCount && Paths.Count > 0;
}

/// <summary>
/// PDF 导出（照上游 <c>collagepdfexport.cpp</c> + <c>screenshotpdfexport.cpp</c>）：按合成页
/// 写 PDF 1.7，图片要么走无损 FlateDecode（质量 100），要么走 DCTDecode JPEG（其余质量），
/// 透明用灰阶 SMask；合并导出写一个文件，逐页导出按 <c>NNN_名称.pdf</c> 命名并在重名时追加
/// <c>_2</c>、<c>_3</c>……
/// </summary>
/// <remarks>
/// <para>
/// 所有坐标先换成 PDF 点（<c>mm × 72 / 25.4</c>）再写；PDF 的 y 轴向上，所以每个左上角矩形都要
/// 翻成 <c>页高 − (y + h)</c>。页面超过 PDF 的 14400 点上限时按上游写 <c>/UserUnit</c> 缩放。
/// </para>
/// <para>
/// **先写临时文件再替换**：失败不留半个 PDF；逐页导出时同名文件冲突自动加序号，不覆盖别人的文件。
/// 取消 = 立即返回 <see cref="CollagePdfExportResult.Cancelled"/>，已经写好的页照常记进结果
/// （上游的「第 N 页未保存」提示就是靠它）。
/// </para>
/// </remarks>
internal static class CollagePdfExport
{
    private const double PointsPerMillimeter = 72.0 / 25.4;

    private const double MaximumMediaBox = 14400;

    private const double MaximumUserUnit = 75000;

    /// <summary>默认文件名（不含扩展名），照上游 <c>SnowShot_yyyy-MM-dd_HH-mm-ss</c>。</summary>
    public static string DefaultName => $"SnowShot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

    /// <summary>导出；<paramref name="cancelled"/> 返回 true 时立刻停下。</summary>
    public static CollagePdfExportResult Export(string destination,
        IReadOnlyList<CollageItem> items, CollagePdfSettings settings, Func<bool>? cancelled = null,
        Action<CollagePdfExportProgress>? progress = null)
    {
        var result = new CollagePdfExportResult();
        try
        {
            return ExportCore(destination, items, settings, cancelled, progress, result);
        }
        catch (OutOfMemoryException)
        {
            result.Error = "内存不足，无法导出 PDF";
            return result;
        }
        catch (Exception exception)
        {
            result.Error = $"无法保存 PDF：{exception.Message}";
            return result;
        }
    }

    private static CollagePdfExportResult ExportCore(string destination,
        IReadOnlyList<CollageItem> items, CollagePdfSettings settings, Func<bool>? cancelled,
        Action<CollagePdfExportProgress>? progress, CollagePdfExportResult result)
    {
        int pageCount = CollagePdfGeometry.PageCount(items.Count, settings);
        result.TotalCount = pageCount;

        if (destination.Length == 0)
        {
            result.Error = "未选择输出位置";
            return result;
        }

        if (items.Count == 0 || pageCount <= 0)
        {
            result.Error = "PDF 设置或图像无效";
            return result;
        }

        var errors = CollagePdfGeometry.Validate(settings);
        if (errors.Count > 0)
        {
            result.Error = "PDF 页面设置无效";
            return result;
        }

        var snapshot = CollagePdfRender.Snapshot(items);
        for (int page = 0; page < pageCount; page++)
        {
            if (cancelled?.Invoke() == true)
            {
                result.Cancelled = true;
                return result;
            }

            var composed = CollagePdfGeometry.ComposedPage(snapshot, settings, page);
            if (!composed.IsValid)
            {
                result.Error = $"PDF 页面设置无效";
                return result;
            }
        }

        if (settings.ExportMode == CollagePdfExportMode.Combined)
            return ExportCombined(destination, items, settings, pageCount, cancelled, progress, result);

        return ExportPages(destination, items, settings, pageCount, cancelled, progress, result);
    }

    private static CollagePdfExportResult ExportCombined(string destination,
        IReadOnlyList<CollageItem> items, CollagePdfSettings settings, int pageCount,
        Func<bool>? cancelled, Action<CollagePdfExportProgress>? progress,
        CollagePdfExportResult result)
    {
        string path = Path.GetFullPath(destination);
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
        {
            result.Error = "无法创建输出文件夹";
            return result;
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            result.Error = "无法创建输出文件夹";
            return result;
        }

        string temporary = Path.Combine(directory, $".ltools-pdf-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None))
            {
                var write = WriteDocument(stream, items, settings, 0, pageCount, cancelled,
                    progress, out string error);
                if (!write)
                {
                    result.Cancelled = cancelled?.Invoke() == true;
                    if (!result.Cancelled) result.Error = error;
                    return result;
                }
            }

            if (cancelled?.Invoke() == true)
            {
                result.Cancelled = true;
                return result;
            }

            File.Move(temporary, path, true);
        }
        catch (Exception exception)
        {
            result.Error = $"无法保存 PDF：{exception.Message}";
            return result;
        }
        finally
        {
            TryDelete(temporary);
        }

        result.Paths.Add(path);
        for (int page = 0; page < pageCount; page++) result.CompletedPages.Add(page + 1);
        progress?.Invoke(new CollagePdfExportProgress(pageCount, pageCount, pageCount, 1));
        return result;
    }

    private static CollagePdfExportResult ExportPages(string destination,
        IReadOnlyList<CollageItem> items, CollagePdfSettings settings, int pageCount,
        Func<bool>? cancelled, Action<CollagePdfExportProgress>? progress,
        CollagePdfExportResult result)
    {
        string directory = Path.GetFullPath(destination);
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception)
        {
            result.Error = "无法创建输出文件夹";
            return result;
        }

        int digits = Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);
        for (int page = 0; page < pageCount; page++)
        {
            if (cancelled?.Invoke() == true)
            {
                result.Cancelled = true;
                return result;
            }

            string label = settings.IsTemplateBased
                ? "page"
                : SafeImageName(items[page].Label);
            string baseName = (page + 1).ToString("D" + digits) + "_" + label;

            string temporary = Path.Combine(directory, $".ltools-pdf-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None))
                {
                    var write = WriteDocument(stream, items, settings, page, page + 1, cancelled,
                        progress, out string error);
                    if (!write)
                    {
                        result.Cancelled = cancelled?.Invoke() == true;
                        if (!result.Cancelled) result.Error = error;
                        return result;
                    }
                }

                string? published = PublishUnique(temporary, directory, baseName, cancelled);
                if (published == null)
                {
                    if (cancelled?.Invoke() == true) result.Cancelled = true;
                    else result.Error = "无法保存 PDF：目标文件写入失败";
                    return result;
                }

                result.CompletedPages.Add(page + 1);
                result.Paths.Add(published);
                progress?.Invoke(new CollagePdfExportProgress(result.CompletedPages.Count, pageCount,
                    page + 1, 0));
            }
            catch (Exception exception)
            {
                result.Error = $"无法导出第 {page + 1} 页：{exception.Message}";
                return result;
            }
            finally
            {
                TryDelete(temporary);
            }
        }

        progress?.Invoke(new CollagePdfExportProgress(pageCount, pageCount, pageCount, 1));
        return result;
    }

    // ============ PDF 文档 ============

    private readonly record struct WrittenPage(double Width, double Height, double Unit,
        string Resources, int Contents);

    private static bool WriteDocument(Stream output, IReadOnlyList<CollageItem> items,
        CollagePdfSettings settings, int firstPage, int endPage, Func<bool>? cancelled,
        Action<CollagePdfExportProgress>? progress, out string error)
    {
        error = "";
        var writer = new PdfWriter(output);
        if (!writer.Put("%PDF-1.7\n%\u00e2\u00e3\u00cf\u00d3\n"))
        {
            error = "无法保存 PDF：写入失败";
            return false;
        }

        int pageCount = endPage - firstPage;
        var snapshot = CollagePdfRender.Snapshot(items);
        var written = new List<WrittenPage>(pageCount);
        var imageCache = new Dictionary<long, (int Id, int Width, int Height)>();
        for (int index = firstPage; index < endPage; index++)
        {
            if (cancelled?.Invoke() == true) return false;

            var page = CollagePdfGeometry.ComposedPage(snapshot, settings, index);
            if (!page.IsValid)
            {
                error = "PDF 页面设置无效";
                return false;
            }

            double pageWidth = page.PageMillimeters.Width * PointsPerMillimeter;
            double pageHeight = page.PageMillimeters.Height * PointsPerMillimeter;
            if (!(pageWidth > 0) || !(pageHeight > 0))
            {
                error = "PDF 页面设置无效";
                return false;
            }

            double unit = Math.Max(1,
                Math.Ceiling(Math.Max(pageWidth, pageHeight) / MaximumMediaBox));
            if (unit > MaximumUserUnit)
            {
                error = "PDF 页面设置无效";
                return false;
            }

            double width = pageWidth / unit;
            double height = pageHeight / unit;
            var content = new StringBuilder();
            content.Append("1 1 1 rg\n0 0 ").Append(Number(width)).Append(' ').Append(Number(height))
                .Append(" re f\n");

            string resources = "";
            double pageFraction = 0;
            int cells = Math.Max(1, page.Cells.Count);
            for (int cellIndex = 0; cellIndex < page.Cells.Count; cellIndex++)
            {
                if (cancelled?.Invoke() == true) return false;

                var cell = page.Cells[cellIndex];
                if (cell.ItemIndex < 0) continue;

                var item = items[cell.ItemIndex];
                if (!imageCache.TryGetValue(item.Id, out var image))
                {
                    var raster = CollagePdfRender.TransformedPixels(item);
                    if (raster == null)
                    {
                        error = "无法准备图像以供导出为 PDF";
                        return false;
                    }

                    int mask = 0;
                    byte[] alpha = AlphaChannel(raster, out bool translucent);
                    if (translucent)
                    {
                        mask = writer.Stream(
                            "/Type /XObject /Subtype /Image /ColorSpace /DeviceGray /BitsPerComponent 8 " +
                            $"/Width {raster.Width} /Height {raster.Height} /Filter /FlateDecode",
                            Flate(alpha));
                    }

                    byte[] encoded = settings.ImageQuality >= 100
                        ? Flate(RgbBytes(raster))
                        : Jpeg(raster, settings.ImageQuality);
                    string filter = settings.ImageQuality >= 100 ? "/FlateDecode" : "/DCTDecode";
                    int id = writer.Stream(
                        "/Type /XObject /Subtype /Image /ColorSpace /DeviceRGB /Interpolate false " +
                        $"/Width {raster.Width} /Height {raster.Height} /BitsPerComponent 8 /Filter {filter}" +
                        (mask != 0 ? $" /SMask {mask} 0 R" : ""), encoded);
                    image = (id, raster.Width, raster.Height);
                    imageCache[item.Id] = image;
                }

                double clipX = cell.ClipMillimeters.X * PointsPerMillimeter / unit;
                double clipY = (pageHeight - cell.ClipMillimeters.Bottom * PointsPerMillimeter) / unit;
                double clipW = cell.ClipMillimeters.Width * PointsPerMillimeter / unit;
                double clipH = cell.ClipMillimeters.Height * PointsPerMillimeter / unit;
                double imageX = cell.ImageMillimeters.X * PointsPerMillimeter / unit;
                double imageY = (pageHeight - cell.ImageMillimeters.Bottom * PointsPerMillimeter) / unit;
                double imageW = cell.ImageMillimeters.Width * PointsPerMillimeter / unit;
                double imageH = cell.ImageMillimeters.Height * PointsPerMillimeter / unit;

                string name = $"/Im{image.Id}";
                if (!resources.Contains(name + " ", StringComparison.Ordinal))
                    resources += $"{name} {image.Id} 0 R ";

                content.Append("q\n").Append(Number(clipX)).Append(' ').Append(Number(clipY))
                    .Append(' ').Append(Number(clipW)).Append(' ').Append(Number(clipH))
                    .Append(" re W n\n").Append(Number(imageW)).Append(" 0 0 ")
                    .Append(Number(imageH)).Append(' ').Append(Number(imageX)).Append(' ')
                    .Append(Number(imageY)).Append(" cm\n").Append(name).Append(" Do\nQ\n");
                pageFraction = (cellIndex + 1.0) / cells;
            }

            int contents = writer.Stream("", Encoding.ASCII.GetBytes(content.ToString()));
            written.Add(new WrittenPage(width, height, unit, resources, contents));
            progress?.Invoke(new CollagePdfExportProgress(index - firstPage + 1, pageCount,
                index + 1, pageFraction));
        }

        int pagesId = writer.NextId + written.Count;
        var kids = new StringBuilder();
        foreach (var page in written)
        {
            if (cancelled?.Invoke() == true) return false;

            int id = writer.Object("<< /Type /Page /Parent " + pagesId + " 0 R /MediaBox [0 0 " +
                                   Number(page.Width) + " " + Number(page.Height) + "] /UserUnit " +
                                   Number(page.Unit) + " /Resources << /XObject << " +
                                   page.Resources + ">> >> /Contents " + page.Contents + " 0 R >>");
            kids.Append(id).Append(" 0 R ");
        }

        int pages = writer.Object($"<< /Type /Pages /Count {written.Count} /Kids [{kids}] >>");
        int root = writer.Object($"<< /Type /Catalog /Pages {pages} 0 R >>");
        string created = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        int info = writer.Object("<< /Title " + PdfString("图像拼接") + " /Creator " +
                                 PdfString("LTools") + " /CreationDate (D:" + created + "Z) >>");
        if (!writer.Finish(root, info))
        {
            error = "无法保存 PDF：写入失败";
            return false;
        }

        return true;
    }

    // ============ 图片编码 ============

    /// <summary>RGB 字节（Bgra32 → 3 字节直通色）。</summary>
    private static byte[] RgbBytes(CollagePdfRaster raster)
    {
        var rgb = new byte[(long)raster.Width * raster.Height * 3];
        var pixels = raster.Pixels;
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
    private static byte[] AlphaChannel(CollagePdfRaster raster, out bool translucent)
    {
        var alpha = new byte[(long)raster.Width * raster.Height];
        var pixels = raster.Pixels;
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

    /// <summary>JPEG：先按上游口径把 alpha 铺到白底，再按质量编码。</summary>
    private static byte[] Jpeg(CollagePdfRaster raster, int quality)
    {
        var rgb = new byte[(long)raster.Width * raster.Height * 3];
        var pixels = raster.Pixels;
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

        int stride = raster.Width * 3;
        var bitmap = BitmapSource.Create(raster.Width, raster.Height, 96, 96, PixelFormats.Bgr24, null,
            rgb, stride);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, true))
        {
            zlib.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    // ============ 命名与文件 ============

    /// <summary>照上游 <c>safeImageName</c>：只留文件名部分，去掉 Windows 不允许的字符。</summary>
    private static string SafeImageName(string label)
    {
        string name = label ?? "";
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name[(slash + 1)..];
        string stem = Path.GetFileNameWithoutExtension(name);
        var builder = new StringBuilder(stem.Length);
        foreach (char character in stem)
        {
            builder.Append(character < 32 || "\\/:*?\"<>|".Contains(character) ? '_' : character);
        }

        string result = builder.ToString().Trim();
        if (result.Length == 0) result = "Image";
        return result.Length > 120 ? result[..120] : result;
    }

    /// <summary>照上游 <c>publishUnique</c>：先试原名，冲突就追加 <c>_2</c>、<c>_3</c>……</summary>
    private static string? PublishUnique(string temporary, string directory, string baseName,
        Func<bool>? cancelled)
    {
        for (int suffix = 1; suffix < int.MaxValue; suffix++)
        {
            if (cancelled?.Invoke() == true) return null;

            string name = suffix == 1 ? baseName + ".pdf" : $"{baseName}_{suffix}.pdf";
            string path = Path.Combine(directory, name);
            try
            {
                if (File.Exists(path)) continue;
                File.Move(temporary, path, false);
                return path;
            }
            catch (IOException)
            {
                if (File.Exists(path)) continue;
                throw;
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // 临时文件删不掉不影响结果（下次导出用新的 guid）。
        }
    }

    // ============ 字节工具 ============

    private static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string PdfString(string value)
    {
        var builder = new StringBuilder("<FEFF");
        foreach (char character in value)
            builder.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        builder.Append('>');
        return builder.ToString();
    }

    /// <summary>顺序写 PDF 对象并记偏移（照上游 <c>Writer</c>，含 xref / trailer）。</summary>
    private sealed class PdfWriter
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
}
