using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把系统光标画进画面（Snow Shot 第二行「光标」按钮对应的那一项）。
/// </summary>
/// <remarks>
/// <para>
/// 抓屏（<c>CopyFromScreen</c>）**不含**光标，所以录屏要自己补：每帧问一次
/// <c>GetCursorInfo</c> 拿当前光标句柄，再用 <c>DrawIconEx</c> 按热点偏移画到帧上。
/// 指针形状（箭头 / 手 / I 型 / 拖动中）因此自然跟着变，和用户看到的一致。
/// </para>
/// <para>
/// 句柄用完必须删：<c>GetIconInfo</c> 会把掩码 / 彩色位图复制一份交给我们（
/// <c>hbmMask</c> / <c>hbmColor</c>），不删就是每帧泄漏两个 GDI 对象。
/// </para>
/// </remarks>
internal static class RecordCursor
{
    private const int CursorShowing = 0x00000001;
    private const int DiNormal = 0x0003;
    private const int SmCxCursor = 13;

    /// <summary>
    /// 在（输出像素的）<paramref name="x"/>, <paramref name="y"/> 处画光标。
    /// <paramref name="scale"/> = 输出像素 / 屏幕物理像素（跟随鼠标换视口时不是 1）。
    /// </summary>
    public static void Draw(Graphics graphics, double x, double y, double scale)
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info)) return;
        if (info.Handle == IntPtr.Zero || (info.Flags & CursorShowing) == 0) return;

        var icon = new IconInfo();
        if (!GetIconInfo(info.Handle, ref icon)) return;

        IntPtr screen = IntPtr.Zero;
        try
        {
            int side = Math.Max(8, GetSystemMetrics(SmCxCursor));
            int width = Math.Max(1, (int)Math.Round(side * scale));

            int left = (int)Math.Round(x - icon.HotspotX * scale);
            int top = (int)Math.Round(y - icon.HotspotY * scale);

            screen = graphics.GetHdc();
            DrawIconEx(screen, left, top, info.Handle, width, width, 0, IntPtr.Zero, DiNormal);
        }
        catch (Exception)
        {
            // 画不出来只影响这一帧的光标，别让整轮录制失败
        }
        finally
        {
            if (screen != IntPtr.Zero) graphics.ReleaseHdc(screen);
            if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask);
            if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Handle;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr Mask;
        public IntPtr Color;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr icon, ref IconInfo info);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon,
        int width, int height, int step, IntPtr brush, int flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
