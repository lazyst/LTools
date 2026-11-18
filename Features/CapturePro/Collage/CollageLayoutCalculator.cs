using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼图几何的唯一实现（照上游 <c>collagedocument.cpp</c> 的三个函数：
/// <c>calculateAutomaticImageStitchingLayout</c> / <c>calculateCustomGridLayout</c> /
/// <c>resolveImageStitchingStaticImageRect</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 三处取样点必须和上游一致：**参考尺寸永远是首图**（统一尺寸的宽 / 高、宫格的格子尺寸都以它
/// 为准；原始尺寸的宫格取所有图的最大值），等比缩放用 <c>(分子 + 分母/2) / 分母</c> 四舍五入
/// （整数除法，别改成 <see cref="Math.Round(double)"/>），居中偏移用截断除。
/// </para>
/// <para>
/// 手摆矩形是**输出像素的整数**：中心 = 格中心 + 偏移，尺寸 = 变换后尺寸 × 缩放（或自动尺寸），
/// 左上角再四舍五入。预览和导出走同一份，所以画布上看到的就是导出的样子。
/// </para>
/// </remarks>
internal static class CollageLayoutCalculator
{
    /// <summary>输出图的最大边长（WPF 位图与导出缓冲都吃不下更大的）。</summary>
    public const int MaximumDimension = 32000;

    /// <summary>输出图的最大像素数（RGBA ≈ 800 MB）。</summary>
    public const long MaximumPixels = 200_000_000L;

    /// <summary>算一遍布局；空文档返回 <see cref="CollageLayout.Invalid"/>。</summary>
    public static CollageLayout Calculate(IReadOnlyList<CollageItem> items, CollageSettings settings)
    {
        if (items.Count == 0) return CollageLayout.Invalid;
        if (!settings.IsValid) return CollageLayout.Fail("拼接设置无效");

        var (referenceWidth, referenceHeight) = items[0].TransformedSize;
        if (referenceWidth <= 0 || referenceHeight <= 0) return CollageLayout.Fail("图片读不出来");

        var automatic = settings.Direction == CollageDirection.Custom
            ? CustomGrid(items, settings, referenceWidth, referenceHeight)
            : Automatic(items, settings, referenceWidth, referenceHeight);
        if (!automatic.IsValid) return automatic;

        // 手摆位置：只有摆过的图才重算，其余沿用自动矩形。
        var rects = new Int32Rect[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Placement.IsDefault)
            {
                rects[i] = automatic.AutomaticRects[i];
                continue;
            }

            var resolved = ResolveStaticRect(item, automatic.AutomaticRects[i], automatic.Clips[i]);
            if (resolved.Width <= 0 || resolved.Height <= 0)
                return CollageLayout.Fail("图片的摆放位置无效");
            rects[i] = resolved;
        }

        var clips = new Int32Rect[items.Count];
        for (int i = 0; i < items.Count; i++) clips[i] = automatic.Clips[i];
        return CollageLayout.Create(automatic.Width, automatic.Height, automatic.AutomaticRects,
            rects, clips);
    }

    /// <summary>
    /// 手摆矩形（照上游 <c>resolveImageStitchingStaticImageRect</c>）：没摆过 = 自动矩形。
    /// </summary>
    public static Int32Rect ResolveStaticRect(CollageItem item, Int32Rect automatic, Int32Rect clip)
    {
        if (item.Placement.IsDefault) return automatic;
        if (automatic.Width <= 0 || automatic.Height <= 0 || clip.Width <= 0 || clip.Height <= 0)
            return default;

        double scale = item.Placement.Scale ?? 1;
        if (!double.IsFinite(scale) || scale <= 0) return default;

        var (width, height) = item.TransformedSize;
        double sizeWidth = item.Placement.Scale.HasValue ? width * scale : automatic.Width;
        double sizeHeight = item.Placement.Scale.HasValue ? height * scale : automatic.Height;
        if (!double.IsFinite(sizeWidth) || !double.IsFinite(sizeHeight) ||
            sizeWidth <= 0 || sizeHeight <= 0) return default;

        // 照上游 qRound：半点远离零（Math.Round 默认是银行家舍入，2.5 会变 2）
        int rasterWidth = Math.Max(1, (int)Math.Round(sizeWidth, MidpointRounding.AwayFromZero));
        int rasterHeight = Math.Max(1, (int)Math.Round(sizeHeight, MidpointRounding.AwayFromZero));
        double centerX = clip.X + clip.Width / 2.0 + item.Placement.OffsetX;
        double centerY = clip.Y + clip.Height / 2.0 + item.Placement.OffsetY;
        double originX = centerX - rasterWidth / 2.0;
        double originY = centerY - rasterHeight / 2.0;
        if (!double.IsFinite(originX) || !double.IsFinite(originY)) return default;

        return new Int32Rect(
            (int)Math.Round(originX, MidpointRounding.AwayFromZero),
            (int)Math.Round(originY, MidpointRounding.AwayFromZero), rasterWidth, rasterHeight);
    }

    /// <summary>
    /// 宫格的行列会自动补齐（照上游 <c>normalizeCustomGrid</c>）：没定过就按图片数取平方根定一次，
    /// 之后行数固定的情况下列数不够就加列（只加不减）。
    /// </summary>
    public static CollageSettings NormalizeCustomGrid(CollageSettings settings, int imageCount,
        out bool adjusted)
    {
        adjusted = false;
        if (settings.Direction != CollageDirection.Custom || imageCount <= 0) return settings;

        var result = settings;
        if (!result.CustomGridInitialized)
        {
            int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(imageCount)));
            result = result with
            {
                Columns = columns,
                Rows = Math.Max(1, ColumnsForImages(imageCount, columns)),
                CustomGridInitialized = true,
            };
            adjusted = true;
        }

        int needed = ColumnsForImages(imageCount, result.Rows);
        if (needed > result.Columns)
        {
            result = result with { Columns = needed };
            adjusted = true;
        }

        return result;
    }

    /// <summary>把 <paramref name="count"/> 张图按 <paramref name="rows"/> 行排开需要几列。</summary>
    public static int ColumnsForImages(int count, int rows)
    {
        if (rows <= 0) return Math.Max(1, count);
        return count / rows + (count % rows != 0 ? 1 : 0);
    }

    /// <summary>纵向 / 横向：一条轴上首尾相接，交叉轴按最宽（或最高）居中对齐。</summary>
    private static CollageLayout Automatic(IReadOnlyList<CollageItem> items, CollageSettings settings,
        int referenceWidth, int referenceHeight)
    {
        bool vertical = settings.Direction == CollageDirection.Vertical;
        var sizes = new (int Width, int Height)[items.Count];
        long axis = 0;
        int cross = 0;
        for (int i = 0; i < items.Count; i++)
        {
            var (width, height) = items[i].TransformedSize;
            if (width <= 0 || height <= 0) return CollageLayout.Fail("图片读不出来");

            if (settings.Sizing == CollageSizing.Uniform)
            {
                long numerator = vertical ? (long)height * referenceWidth : (long)width * referenceHeight;
                int divisor = vertical ? width : height;
                long scaled = Math.Max(1, (numerator + divisor / 2) / divisor);
                if (scaled > int.MaxValue) return CollageLayout.Fail("图片太大，放不进拼接结果");
                if (vertical) (width, height) = (referenceWidth, (int)scaled);
                else (width, height) = ((int)scaled, referenceHeight);
            }

            sizes[i] = (width, height);
            axis += vertical ? height : width;
            if (i > 0) axis += settings.Spacing;
            cross = Math.Max(cross, vertical ? width : height);
            if (axis > int.MaxValue) return CollageLayout.Fail("拼接结果太大");
        }

        axis += (long)settings.Border * 2;
        long fullCross = (long)cross + (long)settings.Border * 2;
        if (axis > int.MaxValue || fullCross > int.MaxValue) return CollageLayout.Fail("拼接结果太大");

        int outputWidth = vertical ? (int)fullCross : (int)axis;
        int outputHeight = vertical ? (int)axis : (int)fullCross;
        if (TooLarge(outputWidth, outputHeight)) return CollageLayout.Fail(TooLargeMessage());

        var rects = new Int32Rect[items.Count];
        var clips = new Int32Rect[items.Count];
        long offset = settings.Border;
        for (int i = 0; i < items.Count; i++)
        {
            var (width, height) = sizes[i];
            int centered = settings.Border + (cross - (vertical ? width : height)) / 2;
            rects[i] = vertical
                ? new Int32Rect(centered, (int)offset, width, height)
                : new Int32Rect((int)offset, centered, width, height);
            clips[i] = vertical
                ? new Int32Rect(settings.Border, (int)offset, cross, height)
                : new Int32Rect((int)offset, settings.Border, width, cross);
            offset += (vertical ? height : width) + settings.Spacing;
        }

        return CollageLayout.Create(outputWidth, outputHeight, rects, rects, clips);
    }

    /// <summary>宫格：格子尺寸取首图（统一尺寸）或各图最大值（原始尺寸），图在格里居中。</summary>
    private static CollageLayout CustomGrid(IReadOnlyList<CollageItem> items, CollageSettings settings,
        int referenceWidth, int referenceHeight)
    {
        var sizes = new (int Width, int Height)[items.Count];
        int cellWidth = referenceWidth;
        int cellHeight = referenceHeight;
        for (int i = 0; i < items.Count; i++)
        {
            var (itemWidth, itemHeight) = items[i].TransformedSize;
            if (itemWidth <= 0 || itemHeight <= 0) return CollageLayout.Fail("图片读不出来");

            sizes[i] = (itemWidth, itemHeight);
            if (settings.Sizing == CollageSizing.Original)
            {
                cellWidth = Math.Max(cellWidth, itemWidth);
                cellHeight = Math.Max(cellHeight, itemHeight);
            }
        }

        long columns = Math.Max(settings.Columns, ColumnsForImages(items.Count, settings.Rows));
        if (columns > int.MaxValue) return CollageLayout.Fail("拼接结果太大");

        long width = columns * cellWidth + (columns - 1) * settings.Spacing +
                     (long)settings.Border * 2;
        long height = (long)settings.Rows * cellHeight + (long)(settings.Rows - 1) * settings.Spacing +
                      (long)settings.Border * 2;
        if (width > int.MaxValue || height > int.MaxValue) return CollageLayout.Fail("拼接结果太大");
        if (TooLarge((int)width, (int)height)) return CollageLayout.Fail(TooLargeMessage());

        var rects = new Int32Rect[items.Count];
        var clips = new Int32Rect[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            var (w, h) = sizes[i];
            if (settings.Sizing == CollageSizing.Uniform)
            {
                if ((long)w * cellHeight >= (long)h * cellWidth)
                {
                    long numerator = (long)h * cellWidth;
                    h = Math.Max(1, (int)((numerator + w / 2) / w));
                    w = cellWidth;
                }
                else
                {
                    long numerator = (long)w * cellHeight;
                    w = Math.Max(1, (int)((numerator + h / 2) / h));
                    h = cellHeight;
                }
            }

            long x = settings.Border + (long)(i % columns) * (cellWidth + settings.Spacing) +
                     (cellWidth - w) / 2;
            long y = settings.Border + (long)(i / columns) * (cellHeight + settings.Spacing) +
                     (cellHeight - h) / 2;
            rects[i] = new Int32Rect((int)x, (int)y, w, h);
            clips[i] = new Int32Rect((int)x - (cellWidth - w) / 2,
                (int)y - (cellHeight - h) / 2, cellWidth, cellHeight);
        }

        return CollageLayout.Create((int)width, (int)height, rects, rects, clips);
    }

    private static bool TooLarge(int width, int height) =>
        width <= 0 || height <= 0 || width > MaximumDimension || height > MaximumDimension ||
        (long)width * height > MaximumPixels;

    private static string TooLargeMessage() =>
        $"拼接结果太大（最多 {MaximumDimension} × {MaximumDimension} 像素）";
}
