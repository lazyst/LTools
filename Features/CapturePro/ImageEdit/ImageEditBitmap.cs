using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.ImageEdit;

/// <summary>
/// 图片编辑窗口的像素操作（剪掉一条行 / 列条带）。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageEditingDocument::prepareCrop</c> 里的 <c>removeStrip</c>：按格式原样搬像素，
/// 不改编码、不重采样，所以剪完的图与「不剪那一块」逐像素相同。
/// 纯函数，<c>--selftest</c> 直接断言尺寸与像素。
/// </remarks>
internal static class ImageEditBitmap
{
    /// <summary>
    /// 剪掉 <paramref name="span"/> 这一条（行 = 横带、列 = 纵带），返回新位图。
    /// 条带不合法（长度 0 / 越界 / 等于整条边长）时返回原图，调用方先自己校验。
    /// </summary>
    public static BitmapSource RemoveStrip(BitmapSource image, CropAxis axis, CropSpan span)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        int width = image.PixelWidth;
        int height = image.PixelHeight;
        int extent = axis == CropAxis.Rows ? height : width;
        if (!span.IsValid(extent)) return image;

        int bytesPerPixel = (image.Format.BitsPerPixel + 7) / 8;
        int stride = width * bytesPerPixel;
        var source = new byte[stride * height];
        image.CopyPixels(source, stride, 0);

        int newWidth = axis == CropAxis.Columns ? width - span.Length : width;
        int newHeight = axis == CropAxis.Rows ? height - span.Length : height;
        int newStride = newWidth * bytesPerPixel;
        var target = new byte[newStride * newHeight];

        if (axis == CropAxis.Rows)
        {
            int above = span.Start * stride;
            int below = (height - span.End) * stride;
            Buffer.BlockCopy(source, 0, target, 0, above);
            if (below > 0) Buffer.BlockCopy(source, span.End * stride, target, above, below);
        }
        else
        {
            int leftBytes = span.Start * bytesPerPixel;
            int rightBytes = (width - span.End) * bytesPerPixel;
            for (int y = 0; y < height; y++)
            {
                int from = y * stride;
                int to = y * newStride;
                if (leftBytes > 0) Buffer.BlockCopy(source, from, target, to, leftBytes);
                if (rightBytes > 0)
                    Buffer.BlockCopy(source, from + span.End * bytesPerPixel, target, to + leftBytes, rightBytes);
            }
        }

        var result = BitmapSource.Create(newWidth, newHeight, image.DpiX, image.DpiY,
            image.Format, image.Palette, target, newStride);
        result.Freeze();
        return result;
    }
}
