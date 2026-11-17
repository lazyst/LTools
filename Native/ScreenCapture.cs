using System.Drawing;
using System.Drawing.Imaging;

namespace LTools.Native;

/// <summary>
/// 屏幕截图 / 像素取色（System.Drawing 随 WindowsDesktop 框架提供，无需额外 NuGet）。
/// </summary>
/// <remarks>
/// 本应用是 PerMonitorV2 DPI 感知，<see cref="Graphics.CopyFromScreen"/> 传虚拟桌面物理像素坐标，
/// 与 <see cref="InputHelper.MoveAbsolute"/> 的坐标系一致——截图匹配出的坐标可直接用于点击。
/// </remarks>
internal static class ScreenCapture
{
    /// <summary>虚拟桌面左上角 X（多显示器时可能为负）。</summary>
    public static int VirtualLeft => Win32.GetSystemMetrics(Win32.SmXVirtualscreen);

    /// <summary>虚拟桌面左上角 Y。</summary>
    public static int VirtualTop => Win32.GetSystemMetrics(Win32.SmYVirtualscreen);

    /// <summary>虚拟桌面宽度。</summary>
    public static int VirtualWidth => Win32.GetSystemMetrics(Win32.SmCxVirtualscreen);

    /// <summary>虚拟桌面高度。</summary>
    public static int VirtualHeight => Win32.GetSystemMetrics(Win32.SmCyVirtualscreen);

    /// <summary>截取屏幕区域（物理像素）。调用方负责 Dispose。</summary>
    public static Bitmap Capture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentException("截图区域尺寸必须大于 0");

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 把一个窗口**整窗**画到位图（<c>PrintWindow</c> + <c>PW_RENDERFULLCONTENT</c>），
    /// 尺寸 = <c>GetWindowRect</c>（含标题栏 / 边框）。实时贴图拿它按屏幕坐标裁。
    /// 调用方负责 Dispose；窗口不在了 / 没有可见尺寸时抛异常。
    /// </summary>
    public static Bitmap CaptureWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
            throw new InvalidOperationException("源窗口已经关闭");
        if (!Win32.GetWindowRect(hwnd, out var rect))
            throw new InvalidOperationException("读不到源窗口的位置");

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("源窗口没有可见尺寸");

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            IntPtr hdc = g.GetHdc();
            try { Win32.PrintWindow(hwnd, hdc, Win32.PwRenderFullContent); }
            finally { g.ReleaseHdc(hdc); }

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    /// <summary>读取屏幕某点颜色（#RRGGBB）；失败抛异常。</summary>
    public static string GetPixel(int x, int y)
    {
        using var bmp = Capture(x, y, 1, 1);
        var c = bmp.GetPixel(0, 0);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
