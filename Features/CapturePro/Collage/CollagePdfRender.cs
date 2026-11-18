using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// PDF 页面的绘制与像素准备（照上游 <c>collagepdfexport.cpp</c> 的 <c>pageRows</c> +
/// <c>collagetransformedrows_p.h</c>）：预览与导出共用同一份几何，像素按 2×2 整数矩阵
/// **一一对应**搬过去，不做二次重采样。
/// </summary>
/// <remarks>
/// <para>
/// 模板拼接里一张图可能比格子大、也可能被挪出格子：绘制时一律先按格子裁剪再摆图，和导出
/// 写进内容流的 <c>re W n</c> 完全同一条规则，所以预览看到什么导出就是什么。
/// </para>
/// <para>
/// 单张图变换后的像素上限照项目既有口径压到 <see cref="MaximumPixels"/>（远小于上游的 8 GB）：
/// WPF 位图与导出缓冲都吃不下更大的，超了直接报「无法准备图像以供导出为 PDF」而不是把内存打爆。
/// </para>
/// </remarks>
internal static class CollagePdfRender
{
    /// <summary>单张图变换后的最大像素数（RGBA ≈ 1.6 GB）。</summary>
    public const long MaximumPixels = 400_000_000L;

    /// <summary>把文档里的图折成纯逻辑能用的描述（像素尺寸取变换后的尺寸）。</summary>
    public static IReadOnlyList<CollagePdfItem> Snapshot(IReadOnlyList<CollageItem> items)
    {
        var result = new List<CollagePdfItem>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var (width, height) = item.TransformedSize;
            result.Add(new CollagePdfItem(item.Id, i, width, height, item.PdfPlacement));
        }

        return result;
    }

    /// <summary>
    /// 按旋转 / 翻转把原图重排成 <see cref="PixelFormats.Pbgra32"/> 像素；失败返回 null。
    /// </summary>
    /// <remarks>
    /// 矩阵只有 0 / 1 / -1，目标像素与源像素是一一对应的整数坐标，所以这里直接逐像素搬
    /// （画布预览用 <see cref="DrawPage"/> 走 WPF 绘制，导出要的是原分辨率字节）。
    /// </remarks>
    public static CollagePdfRaster? TransformedPixels(CollageItem item)
    {
        var source = item.Source;
        int sourceWidth = source.PixelWidth;
        int sourceHeight = source.PixelHeight;
        if (sourceWidth <= 0 || sourceHeight <= 0) return null;

        var (width, height) = item.TransformedSize;
        if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels) return null;

        PixelFormat format = source.Format;
        int sourceStride = (sourceWidth * format.BitsPerPixel + 7) / 8;
        long sourceBytes = (long)sourceStride * sourceHeight;
        long targetBytes = (long)width * height * 4;
        byte[] input;
        byte[] output;
        try
        {
            input = new byte[sourceBytes];
            output = new byte[targetBytes];
        }
        catch (OutOfMemoryException)
        {
            return null;
        }

        try
        {
            source.CopyPixels(input, sourceStride, 0);
        }
        catch (Exception)
        {
            return null;
        }

        if (format != PixelFormats.Bgra32)
        {
            // 统一到 **直通 alpha** 的 Bgra32 再搬：索引色 / 灰度 / Bgr24 / 预乘 Pbgra32
            // 都走 WPF 的转换，导出写 RGB + SMask 时不用再反预乘。
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            sourceStride = converted.PixelWidth * 4;
            input = new byte[(long)sourceStride * converted.PixelHeight];
            converted.CopyPixels(input, sourceStride, 0);
        }

        var transform = item.Transform;
        var (originX, originY) = transform.BoundsOrigin(sourceWidth, sourceHeight);
        int stride = width * 4;
        for (int y = 0; y < sourceHeight; y++)
        {
            long row = (long)y * sourceStride;
            double centerY = y + 0.5;
            for (int x = 0; x < sourceWidth; x++)
            {
                double centerX = x + 0.5;
                int targetX = (int)Math.Floor(transform.M11 * centerX + transform.M21 * centerY -
                                              originX);
                int targetY = (int)Math.Floor(transform.M12 * centerX + transform.M22 * centerY -
                                              originY);
                if (targetX < 0 || targetY < 0 || targetX >= width || targetY >= height) continue;

                long from = row + (long)x * 4;
                long to = (long)targetY * stride + (long)targetX * 4;
                output[to] = input[from];
                output[to + 1] = input[from + 1];
                output[to + 2] = input[from + 2];
                output[to + 3] = input[from + 3];
            }
        }

        return new CollagePdfRaster(output, width, height);
    }

    /// <summary>
    /// 预览绘制：白纸铺满，再逐格裁剪 + 按目标毫米矩形摆图（缩放由 <paramref name="scale"/>
    /// 把毫米换成当前画布的 DIP）。坐标原点 = 纸张左上角。
    /// </summary>
    public static void DrawPage(DrawingContext dc, CollagePdfComposedPage page,
        IReadOnlyList<CollageItem> items, double scale)
    {
        if (!page.IsValid) return;

        double pageWidth = page.PageMillimeters.Width * scale;
        double pageHeight = page.PageMillimeters.Height * scale;
        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, pageWidth, pageHeight));

        foreach (var cell in page.Cells)
        {
            if (cell.ItemIndex < 0 || cell.ItemIndex >= items.Count) continue;

            var item = items[cell.ItemIndex];
            var target = new Rect(cell.ImageMillimeters.X * scale, cell.ImageMillimeters.Y * scale,
                cell.ImageMillimeters.Width * scale, cell.ImageMillimeters.Height * scale);
            if (target.Width <= 0 || target.Height <= 0) continue;

            var clip = new Rect(cell.ClipMillimeters.X * scale, cell.ClipMillimeters.Y * scale,
                cell.ClipMillimeters.Width * scale, cell.ClipMillimeters.Height * scale);
            if (clip.Width <= 0 || clip.Height <= 0) continue;

            var preview = item.Preview;
            dc.PushClip(new RectangleGeometry(clip));
            dc.PushTransform(new MatrixTransform(MatrixOf(item.Transform, preview.PixelWidth,
                preview.PixelHeight, target)));
            dc.DrawImage(preview, new Rect(0, 0, preview.PixelWidth, preview.PixelHeight));
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>源图像素 → 目标矩形的矩阵（与 <see cref="CollageRender.MatrixOf"/> 同一条规则）。</summary>
    public static Matrix MatrixOf(CollageTransform transform, int sourceWidth, int sourceHeight,
        Rect target)
    {
        var (transformedWidth, transformedHeight) = transform.SizeOf(sourceWidth, sourceHeight);
        double scaleX = transformedWidth > 0 ? target.Width / transformedWidth : 1;
        double scaleY = transformedHeight > 0 ? target.Height / transformedHeight : 1;

        var matrix = new Matrix(transform.M11, transform.M12, transform.M21, transform.M22, 0, 0);
        matrix.Scale(scaleX, scaleY);

        var (originX, originY) = transform.BoundsOrigin(sourceWidth, sourceHeight);
        matrix.Translate(target.X - originX * scaleX, target.Y - originY * scaleY);
        return matrix;
    }
}

/// <summary>变换后的像素缓冲（Pbgra32）。</summary>
internal sealed class CollagePdfRaster
{
    public CollagePdfRaster(byte[] pixels, int width, int height)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
    }

    public byte[] Pixels { get; }

    public int Width { get; }

    public int Height { get; }
}
