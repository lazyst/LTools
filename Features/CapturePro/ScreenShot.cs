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

    /// <summary>
    /// 实时贴图的一帧：抓源窗口**整窗**，裁到 <paramref name="area"/> 那块再贴进同尺寸的画布
    /// （窗口只露一半就只画一半，完全挪开就整张透明）。窗口关了 / 抓不到返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 画布尺寸永远是选区那块矩形，所以贴图窗口的尺寸 / 位置一拍都不变；
    /// 裁剪与偏移由 <see cref="PinLive.Intersect"/> 算（纯函数，<c>--selftest</c> 直接断言）。
    /// </remarks>
    public static BitmapSource? CaptureWindow(IntPtr hwnd, Int32Rect area)
    {
        if (hwnd == IntPtr.Zero || area.Width <= 0 || area.Height <= 0) return null;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return null;

        var windowRect = new Int32Rect(rect.Left, rect.Top,
            Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
        if (windowRect.Width <= 0 || windowRect.Height <= 0) return null;

        BitmapSource window;
        try
        {
            using var bitmap = ScreenCapture.CaptureWindow(hwnd);
            window = ScreenPicker.ToBitmapSource(bitmap);
        }
        catch
        {
            // 最小化 / 正被销毁 / 提权窗口：这一拍没有内容，调用方保留上一帧
            return null;
        }

        int stride = area.Width * 4;
        var canvas = new byte[stride * area.Height];          // 默认全透明（Bgra32）

        if (PinLive.Intersect(windowRect, area) is LiveCrop crop)
        {
            BitmapSource source = window.Format == PixelFormats.Bgra32
                ? window
                : new FormatConvertedBitmap(window, PixelFormats.Bgra32, null, 0);

            int sourceStride = crop.Source.Width * 4;
            var buffer = new byte[sourceStride * crop.Source.Height];
            source.CopyPixels(crop.Source, buffer, sourceStride, 0);

            for (int y = 0; y < crop.Source.Height; y++)
                Buffer.BlockCopy(buffer, y * sourceStride, canvas,
                    (crop.Target.Y + y) * stride + crop.Target.X * 4, sourceStride);
        }

        var composed = BitmapSource.Create(area.Width, area.Height, 96, 96,
            PixelFormats.Bgra32, null, canvas, stride);
        composed.Freeze();
        return composed;
    }

    /// <summary>
    /// 长截图的一帧：从**目标窗口**（<c>PrintWindow</c> 整窗）里裁出 <paramref name="area"/> 那块。
    /// 覆盖层这时盖满全屏，抓屏幕只会抓到自己，所以帧一律从窗口自己取（窗口被挡住也画得出来）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 选区必须**整块落在窗口里**（<see cref="PinLive.Intersect"/> 的 source 与 area 等宽等高），
    /// 否则说明框跨到了别的窗口 / 桌面上，这一帧不可信：返回 <c>null</c>，调用方不接。
    /// </para>
    /// <para>
    /// 窗口最小化 / 关掉 / 抓不到都返回 <c>null</c>（<c>PrintWindow</c> 在这种状态下给不出内容）。
    /// </para>
    /// </remarks>
    public static BitmapSource? CaptureRegion(IntPtr hwnd, Int32Rect area)
    {
        if (hwnd == IntPtr.Zero || area.Width <= 0 || area.Height <= 0) return null;
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd)) return null;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return null;

        var windowRect = new Int32Rect(rect.Left, rect.Top,
            Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
        if (windowRect.Width <= 0 || windowRect.Height <= 0) return null;

        if (PinLive.Intersect(windowRect, area) is not LiveCrop crop) return null;
        if (crop.Source.Width != area.Width || crop.Source.Height != area.Height) return null;

        try
        {
            using var bitmap = ScreenCapture.CaptureWindow(hwnd);
            var window = ScreenPicker.ToBitmapSource(bitmap);
            BitmapSource source = window.Format == PixelFormats.Bgra32
                ? window
                : new FormatConvertedBitmap(window, PixelFormats.Bgra32, null, 0);

            var frame = new CroppedBitmap(source, crop.Source);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;    // 正被销毁 / 提权窗口 / 没有可见尺寸：这一帧没有内容
        }
    }

    /// <summary>物理矩形 → <see cref="System.Drawing.Rectangle"/>（窗口探测那套 API 用它）。</summary>
    public static System.Drawing.Rectangle ToRectangle(Int32Rect rect) =>
        new(rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height));

    private static bool Same(Int32Rect a, Int32Rect b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
}
