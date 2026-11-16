using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.CapturePro;

/// <summary>
/// 冻结画面的取景：覆盖**所有显示器**（跨屏选区），并把逐块抓到的画面拼成一张。
/// </summary>
/// <remarks>
/// <para>
/// 抓帧一律**逐块显示器**做：混合 DPI 下一次 <c>BitBlt</c> 跨屏可能拿到被系统缩放过的内容，
/// 逐块抓再按物理坐标贴回去才不会（每块都比自己那块，1:1，不做任何缩放）。
/// </para>
/// <para>
/// 坐标一律物理像素，与截图 / 找图 / 鼠标注入同一套。<see cref="Union"/> / <see cref="Intersect"/>
/// 是纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class ScreenShot
{
    /// <summary>所有显示器的物理矩形（按系统枚举顺序；枚举不到时退回虚拟桌面，至少一块）。</summary>
    public static List<Int32Rect> Monitors()
    {
        var list = new List<Int32Rect>();
        foreach (var screen in Screens.All())
            list.Add(new Int32Rect(screen.Left, screen.Top, Math.Max(1, screen.Width), Math.Max(1, screen.Height)));

        if (list.Count == 0)
            list.Add(new Int32Rect(ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                Math.Max(1, ScreenCapture.VirtualWidth), Math.Max(1, ScreenCapture.VirtualHeight)));

        return list;
    }

    /// <summary>所有显示器的并集 = 跨屏选区的取景范围。</summary>
    public static Int32Rect Bounds() => Union(Monitors());

    /// <summary>并集；空表 / 全是空矩形时返回空矩形（宽高为 0）。</summary>
    public static Int32Rect Union(IEnumerable<Int32Rect> rects)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        bool any = false;

        foreach (var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            any = true;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.X + r.Width);
            bottom = Math.Max(bottom, r.Y + r.Height);
        }

        return any ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty;
    }

    /// <summary>交集；不相交时返回空矩形（宽高为 0）。</summary>
    public static Int32Rect Intersect(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);

        return right - left > 0 && bottom - top > 0
            ? new Int32Rect(left, top, right - left, bottom - top)
            : Int32Rect.Empty;
    }

    /// <summary>拍下整块区域（物理像素）。区域不在任何显示器上时抛异常（调用方给提示）。</summary>
    public static BitmapSource Capture(Int32Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0)
            throw new ArgumentException("截图区域尺寸必须大于 0");

        var pieces = new List<(Int32Rect Rect, BitmapSource Image)>();
        foreach (var monitor in Monitors())
        {
            var part = Intersect(monitor, area);
            if (part.Width <= 0 || part.Height <= 0) continue;

            using var bitmap = ScreenCapture.Capture(part.X, part.Y, part.Width, part.Height);
            pieces.Add((part, ScreenPicker.ToBitmapSource(bitmap)));
        }

        if (pieces.Count == 0) throw new InvalidOperationException("截图区域不在任何显示器上");
        if (pieces.Count == 1 && Same(pieces[0].Rect, area)) return pieces[0].Image;   // 单屏：不复制一遍

        return Compose(area, pieces);
    }

    /// <summary>把逐块画面按物理坐标贴进一张 Bgra32 位图（纯像素搬运，不走渲染栈）。</summary>
    private static BitmapSource Compose(Int32Rect area, List<(Int32Rect Rect, BitmapSource Image)> pieces)
    {
        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];

        foreach (var (rect, image) in pieces)
        {
            // 只认 Bgra32：ToBitmapSource 给的就是它，万一换了抓帧实现就在这里转一次
            BitmapSource source = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

            int pieceStride = rect.Width * 4;
            var buffer = new byte[pieceStride * rect.Height];
            source.CopyPixels(buffer, pieceStride, 0);

            int offsetX = (rect.X - area.X) * 4;
            int offsetY = rect.Y - area.Y;
            for (int y = 0; y < rect.Height; y++)
                Buffer.BlockCopy(buffer, y * pieceStride, pixels, (offsetY + y) * stride + offsetX, pieceStride);
        }

        var composed = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        composed.Freeze();
        return composed;
    }

    /// <summary>区域中心所在显示器的 DPI 缩放比（窗口 / 贴图换算 DIP 用）；取不到按 100%。</summary>
    public static double ScaleOf(Int32Rect area)
    {
        var rect = ToRectangle(area);
        var (sx, _) = ScreenPickerWindow.MonitorScale(rect);
        return sx > 0 ? sx : 1;
    }

    /// <summary>物理矩形 → <see cref="System.Drawing.Rectangle"/>（窗口探测那套 API 用它）。</summary>
    public static System.Drawing.Rectangle ToRectangle(Int32Rect rect) =>
        new(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height));

    private static bool Same(Int32Rect a, Int32Rect b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
}
