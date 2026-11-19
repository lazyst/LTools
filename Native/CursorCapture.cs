using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;

namespace LTools.Native;

/// <summary>
/// 抓帧时把鼠标指针画进成品（照上游 <c>snow-cursor</c> + <c>cursor_compositor</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 光标形状由 User32 现成渲染：<c>GetCursorInfo</c> 取当前 <c>HCURSOR</c>，
/// <c>GetIconInfo</c> 取热点，再用 <c>DrawIconEx</c> 画进一块 32 位 DIB 区
/// （<c>CreateDIBSection</c>）。这是上游 <c>render_alpha_cursor_rgba</c> 的同一条路：
/// 应用自绘光标只有 User32 自己知道怎么合成，别自己解码 AND/XOR 掩码。
/// </para>
/// <para>
/// 用户态存的是**直通 alpha**（WPF <c>Bgra32</c>），而画进透明黑背景得到的是
/// **预乘 alpha**，所以取回来时按 <c>c * 255 / a</c> 还原（上游同样处理）。
/// 合成只有一处 <see cref="Composite(byte[], int, int, int, CursorFrame, int, int)"/>，
/// 是纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class CursorCapture
{
    private const int CursorShowing = 0x00000001;
    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;
    private const uint DiNormal = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point32 ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapObject
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public short Planes;
        public short BitsPixel;
        public IntPtr Bits;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr icon, ref IconInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon,
        int width, int height, int step, IntPtr flickerFreeBrush, uint flags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr reference);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", SetLastError = true)]
    private static extern int GetObject(IntPtr obj, int size, out BitmapObject value);

    /// <summary>一份已解码的光标形状（直通 alpha，BGRA 顺序，与 <c>BitmapSource</c> 的 Bgra32 一致）。</summary>
    internal sealed class CursorFrame
    {
        public int Width;
        public int Height;
        public int HotspotX;
        public int HotspotY;

        /// <summary>直通 alpha 的 BGRA 像素，长度 = <c>Width * Height * 4</c>。</summary>
        public byte[] Pixels = Array.Empty<byte>();
    }

    /// <summary>当前光标位置与形状；不可见 / 取不到形状时返回 <c>null</c>。</summary>
    public static CursorFrame? TryRead(out Point position)
    {
        position = default;

        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0) return null;
        if (info.Cursor == IntPtr.Zero) return null;

        position = new Point(info.ScreenPosition.X, info.ScreenPosition.Y);
        return ReadShape(info.Cursor);
    }

    /// <summary>把 <paramref name="shape"/> 叠到 <paramref name="image"/> 上（按屏幕物理矩形换算位置）。</summary>
    /// <remarks>
    /// 返回**原图**（同一实例）当光标不可见 / 完全落在区域外 / 取不到形状——调用方不必各自判空。
    /// </remarks>
    public static BitmapSource Apply(BitmapSource image, Int32Rect area, CursorFrame? shape, Point screen)
    {
        if (shape is not { Width: > 0, Height: > 0 } || shape.Pixels.Length != shape.Width * shape.Height * 4)
            return image;
        if (area.Width <= 0 || area.Height <= 0) return image;

        int cursorX = (int)Math.Round(screen.X) - shape.HotspotX;
        int cursorY = (int)Math.Round(screen.Y) - shape.HotspotY;

        // 区域外的光标不画：光标可能与冻结画面差几毫秒，别把画到边上的半个光标剪进来
        if (cursorX + shape.Width <= area.X || cursorX >= area.X + area.Width) return image;
        if (cursorY + shape.Height <= area.Y || cursorY >= area.Y + area.Height) return image;

        BitmapSource source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        source.CopyPixels(pixels, stride, 0);

        if (!Composite(pixels, stride, area.Width, area.Height, shape,
                cursorX - area.X, cursorY - area.Y))
            return image;

        var composed = BitmapSource.Create(area.Width, area.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        composed.Freeze();
        return composed;
    }

    /// <summary>
    /// 把光标形状叠进一块 Bgra32 画布（<paramref name="x"/> / <paramref name="y"/> = 形状左上角）。
    /// 返回是否真的画上了点东西。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>composite_pixel</c> 的 <c>AlphaBlend</c> 分支：alpha 0 跳过、255 直接覆盖、
    /// 其余按 <c>(src * a + dst * (255 − a)) / 255</c> 混合。这里只实现 AlphaBlend——
    /// User32 已经把 AND/XOR 掩码合成进 DIB 了，拿到的就是带 alpha 的图。
    /// </remarks>
    public static bool Composite(byte[] canvas, int stride, int width, int height,
        CursorFrame shape, int x, int y)
    {
        if (shape.Width <= 0 || shape.Height <= 0) return false;
        if (shape.Pixels.Length != shape.Width * shape.Height * 4) return false;

        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(width, x + shape.Width);
        int bottom = Math.Min(height, y + shape.Height);
        if (left >= right || top >= bottom) return false;

        bool touched = false;
        for (int py = top; py < bottom; py++)
        {
            int sourceRow = (py - y) * shape.Width;
            int targetRow = py * stride;
            for (int px = left; px < right; px++)
            {
                int s = (sourceRow + (px - x)) * 4;
                int d = targetRow + px * 4;
                byte alpha = shape.Pixels[s + 3];
                if (alpha == 0) continue;

                if (alpha == 255)
                {
                    canvas[d] = shape.Pixels[s];
                    canvas[d + 1] = shape.Pixels[s + 1];
                    canvas[d + 2] = shape.Pixels[s + 2];
                }
                else
                {
                    int inverse = 255 - alpha;
                    canvas[d] = (byte)((shape.Pixels[s] * alpha + canvas[d] * inverse) / 255);
                    canvas[d + 1] = (byte)((shape.Pixels[s + 1] * alpha + canvas[d + 1] * inverse) / 255);
                    canvas[d + 2] = (byte)((shape.Pixels[s + 2] * alpha + canvas[d + 2] * inverse) / 255);
                }

                canvas[d + 3] = 255;
                touched = true;
            }
        }

        return touched;
    }

    /// <summary>读取 <c>HCURSOR</c> 的形状：热点 + 由 User32 渲染的 32 位 DIB 像素。</summary>
    private static CursorFrame? ReadShape(IntPtr cursor)
    {
        var icon = default(IconInfo);
        if (!GetIconInfo(cursor, ref icon)) return null;

        try
        {
            if (!TryMeasure(icon, out int width, out int height)) return null;
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024) return null;

            var pixels = Render(cursor, width, height);
            if (pixels is null) return null;

            return new CursorFrame
            {
                Width = width,
                Height = height,
                HotspotX = icon.HotspotX,
                HotspotY = icon.HotspotY,
                Pixels = pixels,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn("CursorCapture", "读取光标形状失败：" + ex.Message);
            return null;
        }
        finally
        {
            // GetIconInfo 分配的两张位图由调用方释放；HCURSOR 本身是借来的，不能删
            if (icon.MaskBitmap != IntPtr.Zero) DeleteObject(icon.MaskBitmap);
            if (icon.ColorBitmap != IntPtr.Zero) DeleteObject(icon.ColorBitmap);
        }
    }

    /// <summary>光标位图尺寸：彩色位图优先，单色光标用掩码（上下两半叠着，高度减半）。</summary>
    private static bool TryMeasure(IconInfo icon, out int width, out int height)
    {
        width = 0;
        height = 0;

        bool measured = false;
        if (icon.ColorBitmap != IntPtr.Zero &&
            GetObject(icon.ColorBitmap, Marshal.SizeOf<BitmapObject>(), out var color) > 0)
        {
            width = color.Width;
            height = color.Height;
            measured = width > 0 && height > 0;
        }

        if (!measured && icon.MaskBitmap != IntPtr.Zero &&
            GetObject(icon.MaskBitmap, Marshal.SizeOf<BitmapObject>(), out var mask) > 0)
        {
            width = mask.Width;
            height = mask.Height / 2;
            measured = width > 0 && height > 0;
        }

        return measured;
    }

    /// <summary>把光标画进 32 位 DIB，返回直通 alpha 的 BGRA。</summary>
    private static byte[]? Render(IntPtr cursor, int width, int height)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;

        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,       // 负数 = 自上而下，与 WPF 的行序一致
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                },
            };

            bitmap = CreateDIBSection(dc, ref info, DibRgbColors, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) return null;

            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == (IntPtr)(-1)) return null;

            int length = width * height * 4;
            Marshal.Copy(new byte[length], 0, bits, length);

            if (!DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, DiNormal))
                return null;

            var rgba = new byte[length];
            Marshal.Copy(bits, rgba, 0, length);

            // 画进透明黑背景 = 预乘 alpha，换回直通 alpha
            for (int i = 0; i < length; i += 4)
            {
                byte alpha = rgba[i + 3];
                if (alpha is 0 or 255) continue;
                rgba[i] = (byte)Math.Min(255, (rgba[i] * 255 + alpha / 2) / alpha);
                rgba[i + 1] = (byte)Math.Min(255, (rgba[i + 1] * 255 + alpha / 2) / alpha);
                rgba[i + 2] = (byte)Math.Min(255, (rgba[i + 2] * 255 + alpha / 2) / alpha);
            }

            return rgba;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != (IntPtr)(-1)) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }
}
