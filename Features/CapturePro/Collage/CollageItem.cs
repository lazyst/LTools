using System;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 一张图的摆放（照上游 <c>ImageStitchingStaticPlacement</c>）：相对所在格中心的偏移（输出像素）
/// + 相对变换后原图像素的缩放；<see cref="Scale"/> 为 null = 用自动布局算出来的尺寸。
/// </summary>
internal readonly record struct CollagePlacement(double OffsetX, double OffsetY, double? Scale)
{
    /// <summary>没手摆过（上游用 <c>placement == default</c> 判）。</summary>
    public bool IsDefault => OffsetX == 0 && OffsetY == 0 && Scale is null;
}

/// <summary>
/// 2×2 整数变换（照上游 <c>QTransform</c> 的 m11/m12/m21/m22 + <c>canonicalTransform</c>）：
/// 只由 90° 旋转与水平 / 垂直翻转叠出来，所以四个元素永远是 0 / 1 / -1，像素是**一一对应**
/// 的重排，不会重采样。
/// </summary>
internal readonly record struct CollageTransform(int M11, int M12, int M21, int M22)
{
    public static readonly CollageTransform Identity = new(1, 0, 0, 1);

    /// <summary>顺时针 90°：<c>QTransform(0, 1, -1, 0, 0, 0)</c>。</summary>
    public static readonly CollageTransform Clockwise = new(0, 1, -1, 0);

    /// <summary>逆时针 90°：<c>QTransform(0, -1, 1, 0, 0, 0)</c>。</summary>
    public static readonly CollageTransform CounterClockwise = new(0, -1, 1, 0);

    public static readonly CollageTransform MirrorHorizontal = new(-1, 0, 0, 1);
    public static readonly CollageTransform MirrorVertical = new(1, 0, 0, -1);

    public bool IsIdentity => M11 == 1 && M12 == 0 && M21 == 0 && M22 == 1;

    /// <summary>行向量约定下 <c>this * other</c>（先做 this，再做 other，与 Qt 一致）。</summary>
    public CollageTransform Multiply(CollageTransform other) => new(
        M11 * other.M11 + M12 * other.M21,
        M11 * other.M12 + M12 * other.M22,
        M21 * other.M11 + M22 * other.M21,
        M21 * other.M12 + M22 * other.M22);

    /// <summary>把 <paramref name="x"/> / <paramref name="y"/> 映射后的结果。</summary>
    public (double X, double Y) Map(double x, double y) => (M11 * x + M21 * y, M12 * x + M22 * y);

    /// <summary>源图 <paramref name="width"/>×<paramref name="height"/> 变换后的外接尺寸。</summary>
    public (int Width, int Height) SizeOf(int width, int height)
    {
        int w = (int)(Math.Abs(M11) * (long)width + Math.Abs(M21) * (long)height);
        int h = (int)(Math.Abs(M12) * (long)width + Math.Abs(M22) * (long)height);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>变换后外接矩形的左上角（旋转会让它变成负值，照上游用 min 平移回 0）。</summary>
    public (double X, double Y) BoundsOrigin(int width, int height)
    {
        var (x0, y0) = Map(0, 0);
        var (x1, y1) = Map(width, 0);
        var (x2, y2) = Map(0, height);
        var (x3, y3) = Map(width, height);
        return (Math.Min(Math.Min(x0, x1), Math.Min(x2, x3)),
            Math.Min(Math.Min(y0, y1), Math.Min(y2, y3)));
    }
}

/// <summary>
/// 拼图里的一张图：原图 + 缩小后的预览 + 旋转 / 翻转 + 手摆位置。
/// </summary>
/// <remarks>
/// 预览只服务画布（长边不超过 <see cref="PreviewExtent"/>），导出永远用原图像素——照上游
/// <c>boundedPreview</c>。两套都冻住，跨线程只读。
/// </remarks>
internal sealed class CollageItem
{
    private const int PreviewExtent = 1600;

    public CollageItem(long id, BitmapSource source, string label)
    {
        Id = id;
        Label = label ?? "";
        Source = Freeze(source);
        Preview = PreviewOf(Source);
    }

    public long Id { get; }

    public string Label { get; }

    /// <summary>原图（分辨率就是导出用的那一份）。</summary>
    public BitmapSource Source { get; }

    /// <summary>画布用的缩小预览（不缩放时就是原图本身）。</summary>
    public BitmapSource Preview { get; }

    public CollageTransform Transform { get; internal set; } = CollageTransform.Identity;

    public CollagePlacement Placement { get; internal set; }

    /// <summary>PDF 模板拼接里的手摆（相对格中心的毫米偏移 + 毫米 / 像素比例）。</summary>
    public CollagePdfPlacement PdfPlacement { get; internal set; }

    public (int Width, int Height) SourceSize => (Source.PixelWidth, Source.PixelHeight);

    /// <summary>变换后的像素尺寸（布局按这个算）。</summary>
    public (int Width, int Height) TransformedSize => Transform.SizeOf(Source.PixelWidth, Source.PixelHeight);

    /// <summary>
    /// 缩略图用的布局变换：把 2×2 矩阵直接给 WPF（矩阵约定与 Qt 一致）。
    /// </summary>
    /// <remarks>
    /// 用 <c>LayoutTransform</c> 而不是 <c>RenderTransform</c>：90° 旋转后外接框的高宽会互换，
    /// 布局变换能让缩略图容器自己按旋转后的尺寸排，画布与列表里看到的就是同一张方向。
    /// 平移项在布局变换里本来就被忽略，所以这里只给 2×2。
    /// </remarks>
    public System.Windows.Media.Transform PreviewLayoutTransform
    {
        get
        {
            var t = Transform;
            if (t.IsIdentity) return System.Windows.Media.Transform.Identity;

            var matrix = new System.Windows.Media.MatrixTransform(new System.Windows.Media.Matrix(
                t.M11, t.M12, t.M21, t.M22, 0, 0));
            matrix.Freeze();
            return matrix;
        }
    }

    private static BitmapSource Freeze(BitmapSource image)
    {
        if (image.CanFreeze) image.Freeze();
        return image;
    }

    private static BitmapSource PreviewOf(BitmapSource image)
    {
        int width = image.PixelWidth;
        int height = image.PixelHeight;
        if (width <= PreviewExtent && height <= PreviewExtent) return image;

        double scale = Math.Min((double)PreviewExtent / width, (double)PreviewExtent / height);
        var scaled = new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
