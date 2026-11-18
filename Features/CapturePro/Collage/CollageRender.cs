using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 把一次拼接画成一张位图，**画布预览与导出共用这一份几何**（照上游 <c>prepareImageStitchingRows</c>
/// 的绘制规则：背景铺满、逐图裁剪到自己那一格、按矩阵变换后平滑缩放）。
/// </summary>
/// <remarks>
/// <para>
/// 导出按行分带渲染（每带一个 <see cref="RenderTargetBitmap"/>，再拷进最终缓冲）：一次性建出
/// 整张画布在长截图拼接这种尺寸下会直接吃掉上 GB 显存 / 内存，分带之后峰值就只有一个带。
/// 各带之间没有共享像素，合成结果与一次画完完全一致。
/// </para>
/// <para>
/// 变换矩阵是 2×2 整数（90° 旋转与翻转），用 <see cref="DrawingContext.PushTransform"/> 把
/// 源图像素直接映射到输出矩形——**不重采样两次**，与画布上看到的完全一样。
/// </para>
/// </remarks>
internal static class CollageRender
{
    private const int BandRows = 256;

    /// <summary>渲染成一张 <see cref="PixelFormats.Pbgra32"/> 位图；失败抛 <see cref="InvalidOperationException"/>。</summary>
    public static BitmapSource Render(IReadOnlyList<CollageItem> items, CollageSettings settings,
        CollageLayout layout)
    {
        if (!layout.IsValid)
            throw new InvalidOperationException(layout.Error.Length > 0 ? layout.Error : "没有可拼接的图片");

        int width = layout.Width;
        int height = layout.Height;
        int stride = width * 4;
        byte[] pixels;
        try
        {
            pixels = new byte[(long)stride * height];
        }
        catch (OutOfMemoryException)
        {
            throw new InvalidOperationException("拼接结果太大，内存放不下");
        }

        try
        {
            for (int top = 0; top < height; top += BandRows)
            {
                int rows = Math.Min(BandRows, height - top);
                var visual = new DrawingVisual();
                RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
                using (DrawingContext dc = visual.RenderOpen())
                {
                    dc.PushTransform(new TranslateTransform(0, -top));
                    dc.DrawRectangle(new SolidColorBrush(settings.Background), null,
                        new Rect(0, top, width, rows));

                    for (int i = 0; i < items.Count && i < layout.ImageRects.Count; i++)
                    {
                        var image = layout.ImageRects[i];
                        var clip = layout.Clips[i];
                        if (image.Width <= 0 || image.Height <= 0) continue;
                        if (image.Y >= top + rows || image.Y + image.Height <= top) continue;

                        var item = items[i];
                        dc.PushClip(new RectangleGeometry(new Rect(clip.X, clip.Y, clip.Width,
                            clip.Height)));
                        dc.PushTransform(new MatrixTransform(MatrixOf(item,
                            new Rect(image.X, image.Y, image.Width, image.Height))));
                        dc.DrawImage(item.Source, new Rect(0, 0, item.Source.PixelWidth,
                            item.Source.PixelHeight));
                        dc.Pop();
                        dc.Pop();
                    }

                    dc.Pop();
                }

                var band = new RenderTargetBitmap(width, rows, 96, 96, PixelFormats.Pbgra32);
                band.Render(visual);
                band.CopyPixels(new Int32Rect(0, 0, width, rows), pixels, stride, top * stride);
            }
        }
        catch (OutOfMemoryException)
        {
            throw new InvalidOperationException("拼接结果太大，内存放不下");
        }

        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels,
            stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// 源图像素 → 输出矩形的矩阵：先按图片的旋转 / 翻转，再平移到外接矩形原点，最后缩放到目标矩形。
    /// </summary>
    public static Matrix MatrixOf(CollageItem item, Rect target)
    {
        var (sourceWidth, sourceHeight) = item.SourceSize;
        var (transformedWidth, transformedHeight) = item.TransformedSize;
        double scaleX = transformedWidth > 0 ? target.Width / transformedWidth : 1;
        double scaleY = transformedHeight > 0 ? target.Height / transformedHeight : 1;

        var transform = item.Transform;
        var matrix = new Matrix(transform.M11, transform.M12, transform.M21, transform.M22, 0, 0);
        matrix.Scale(scaleX, scaleY);

        var (originX, originY) = transform.BoundsOrigin(sourceWidth, sourceHeight);
        matrix.Translate(target.X - originX * scaleX, target.Y - originY * scaleY);
        return matrix;
    }
}
