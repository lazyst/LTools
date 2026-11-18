using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using ZXing;
using ZXing.Common;

namespace LTools.Features.CapturePro;

/// <summary>识别到的一枚二维码：文本 + 在准备图里的外接矩形（像素）。</summary>
internal readonly record struct QrMark(string Text, double Left, double Top, double Right, double Bottom)
{
    public double CenterX => (Left + Right) / 2;

    public double CenterY => (Top + Bottom) / 2;

    public double Width => Right - Left;

    public double Height => Bottom - Top;
}

/// <summary>二维码气泡的落点：外框左上角 + 箭头在哪一侧 / 箭头中心相对气泡顶边的偏移。</summary>
internal readonly record struct QrBubbleLayout(double Left, double Top, bool ArrowOnLeft, double ArrowY);

/// <summary>
/// 截图 Pro 的自动二维码识别与气泡几何（纯计算，不碰 WPF 控件 / 配置）。
/// </summary>
/// <remarks>
/// <para>
/// 照 snow-shot 的 <c>screenshotqrcontroller.cpp</c>：识别前先按
/// <c>min(1, sqrt(1920*1080/(w*h)), 2560/max(w,h))</c> 降采样，多枚码用「解码一枚 → 白色掩码盖住 → 再解」
/// 的贪心重试（上游用的识别服务一次给全部，本地 ZXing 只给一枚，掩码循环等价）。
/// 标记只画在外接矩形**整个落在选区里**的码上，位置取外接矩形中心。
/// </para>
/// <para>
/// 气泡尺寸也照上游：总宽 ≤344、圆角 8、四周 12 阴影留白、箭头 8；文本高 = 量出来的高 + 8，
/// 夹在 48–240；再压到「可用高 − 140」。摆位优先放标记右侧、放不下翻到左侧。
/// </para>
/// </remarks>
internal static class QrScan
{
    /// <summary>一次最多认几枚（防止掩码循环在噪声图上跑太久）。</summary>
    public const int MaxCodes = 8;

    /// <summary>气泡总宽上限（上游 `std::min(344, available.width())`）。</summary>
    public const double MaxWidth = 344;

    /// <summary>气泡四周留给阴影 / 箭头的透明边（上游 `adjusted(12, 12, -12, -12)`）。</summary>
    public const double Pad = 12;

    /// <summary>气泡圆角。</summary>
    public const double Radius = 8;

    /// <summary>箭头高度的一半（整只箭头 16 高、8 宽）。</summary>
    public const double Arrow = 8;

    /// <summary>气泡内容内边距。</summary>
    public const double Margin = 24;

    /// <summary>内容块之间的间距。</summary>
    public const double Spacing = 12;

    /// <summary>文本区最小 / 最大高度。</summary>
    public const double TextMin = 48;
    public const double TextMax = 240;

    /// <summary>标记按钮边长（上游 `setFixedSize(24, 24)`）。</summary>
    public const int MarkerSize = 24;

    /// <summary>标记里的二维码字形边长（上游 14×14）。</summary>
    public const int MarkerGlyph = 14;

    /// <summary>识别图的降采样比例（上游 `prepareImage`）。</summary>
    public static double ScaleFor(int width, int height)
    {
        if (width <= 0 || height <= 0) return 1;
        double byArea = Math.Sqrt(1920.0 * 1080.0 / ((double)width * height));
        double byMax = 2560.0 / Math.Max(width, height);
        return Math.Min(1.0, Math.Min(byArea, byMax));
    }

    /// <summary>按比例缩一张位图（比例 ≥ 1 就复制一份，调用方负责 Dispose 返回的图）。</summary>
    public static Bitmap Prepare(Bitmap source, double scale)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (scale >= 0.999) return new Bitmap(source);

        int width = Math.Max(1, (int)Math.Floor(source.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(source.Height * scale));

        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(target))
        {
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        return target;
    }

    /// <summary>
    /// 识别图里的全部二维码：先解一枚，用白色盖住它再解下一枚（最多 <see cref="MaxCodes"/> 枚）。
    /// 坐标是这张位图的像素。解不出来返回空表，不抛。
    /// </summary>
    public static List<QrMark> Detect(Bitmap bitmap)
    {
        var marks = new List<QrMark>();
        if (bitmap == null || bitmap.Width < 16 || bitmap.Height < 16) return marks;

        using Bitmap work = ToArgb(bitmap);
        for (int i = 0; i < MaxCodes; i++)
        {
            var (text, box) = ReadOne(work);
            if (string.IsNullOrEmpty(text) || box is not { } area || area.Width < 2 || area.Height < 2) break;

            marks.Add(new QrMark(text!, area.Left, area.Top, area.Left + area.Width, area.Top + area.Height));
            Mask(work, area);
        }

        return marks;
    }

    /// <summary>
    /// 载荷是不是一个能直接打开的网址（上游 <c>webUrl</c>）：整串就是一个绝对 http/https 地址，
    /// 含空白 / 控制字符一律不算（「正文里带着一个链接」不弹「打开链接」）。
    /// </summary>
    public static string? WebUrl(string? text)
    {
        string candidate = (text ?? "").Trim();
        if (candidate.Length == 0) return null;

        foreach (char c in candidate)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (string.IsNullOrEmpty(uri.Host)) return null;

        return candidate;
    }

    /// <summary>气泡总宽：可用宽与 344 取小的那个。</summary>
    public static double BubbleWidth(double availableWidth) =>
        Math.Max(1, Math.Min(MaxWidth, availableWidth));

    /// <summary>气泡里的文本区宽度（照上游 layout 的 24 内边距：扣左右各 24）。</summary>
    public static double ContentWidth(double width) =>
        Math.Max(1, width - Margin * 2);

    /// <summary>文本区高度：量出来的高 + 8，夹 48–240，再压到「可用高 − 140」。</summary>
    public static double TextHeight(double measured, double availableHeight)
    {
        double height = Math.Clamp(Math.Ceiling(measured) + 8, TextMin, TextMax);
        return Math.Min(height, Math.Max(24, availableHeight - 140));
    }

    /// <summary>
    /// 气泡落点（上游 <c>ScreenshotQrPopover::place</c>）：优先放在标记右侧（箭头朝左），
    /// 放不下翻到左侧（箭头朝右），最后夹进可用区域；纵向按标记中心居中。
    /// </summary>
    public static QrBubbleLayout PlaceBubble(Rect anchor, Rect available, double width, double height)
    {
        double x = anchor.Right + 1 + Arrow - Pad;
        bool arrowOnLeft = x + width <= available.Right + 1;
        if (!arrowOnLeft) x = anchor.Left - Arrow - width + Pad;

        double maxX = Math.Max(available.Left, available.Right + 1 - width);
        double maxY = Math.Max(available.Top, available.Bottom + 1 - height);
        double centerY = anchor.Top + anchor.Height / 2;

        double left = Math.Clamp(x, available.Left, maxX);
        double top = Math.Clamp(centerY - height / 2, available.Top, maxY);

        // 箭头中心夹在「圆角内 + 16」之间（上游 paint 里的 std::clamp）
        double arrowY = Math.Clamp(centerY - top, Pad + Radius + 8, Math.Max(Pad + Radius + 8, height - Pad - Radius - 8));
        return new QrBubbleLayout(left, top, arrowOnLeft, arrowY);
    }

    // ============ 解码 ============

    private static Bitmap ToArgb(Bitmap source)
    {
        if (source.PixelFormat == PixelFormat.Format32bppArgb) return new Bitmap(source);

        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height));
        return copy;
    }

    private static (string? Text, Rectangle? Box) ReadOne(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        byte[] pixels;
        int stride = data.Stride;
        try
        {
            pixels = new byte[stride * height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        // LuminanceSource 要每行紧挨着的 width*4 字节
        if (stride != width * 4)
        {
            byte[] tight = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Array.Copy(pixels, y * stride, tight, y * width * 4, width * 4);
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
            var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
            var binary = new BinaryBitmap(new HybridBinarizer(source));
            Result? result = new MultiFormatReader().decode(binary, hints);
            if (result?.Text is not { Length: > 0 } text) return (null, null);
            return (text, BoundsOf(result, width, height));
        }
        catch (Exception)
        {
            // 畸形图 / 库内部异常都算「这一轮没认到」
            return (null, null);
        }
    }

    /// <summary>结果点的外接矩形（认不出点时按整张图算，掩码后循环就结束）。</summary>
    private static Rectangle BoundsOf(Result result, int width, int height)
    {
        ResultPoint[]? points = result.ResultPoints;
        if (points == null || points.Length == 0) return new Rectangle(0, 0, width, height);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var point in points)
        {
            if (point == null || !IsFinite(point.X) || !IsFinite(point.Y)) continue;
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        if (minX > maxX || minY > maxY) return new Rectangle(0, 0, width, height);

        int left = Math.Clamp((int)Math.Floor(minX), 0, width - 1);
        int top = Math.Clamp((int)Math.Floor(minY), 0, height - 1);
        int right = Math.Clamp((int)Math.Ceiling(maxX), left + 1, width);
        int bottom = Math.Clamp((int)Math.Ceiling(maxY), top + 1, height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>把这一枚盖成白的，下一轮就不会再认出它（顺手把安静区一起盖掉）。</summary>
    private static void Mask(Bitmap bitmap, Rectangle area)
    {
        int grow = Math.Max(6, (int)Math.Ceiling(Math.Max(area.Width, area.Height) * 0.5));
        var rect = Rectangle.Intersect(Rectangle.Inflate(area, grow, grow),
            new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        using var graphics = Graphics.FromImage(bitmap);
        graphics.FillRectangle(Brushes.White, rect);
    }
}
