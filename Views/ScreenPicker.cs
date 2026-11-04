using System;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 屏幕取点的入口：先把 LTools 自己的窗口藏起来、拍下光标所在显示器，
/// 再弹 <see cref="ScreenPickerWindow"/> 让用户框选 / 单击。
/// </summary>
/// <remarks>
/// 截图 / 框选期间本进程的窗口全部隐藏，所以模板图与取色结果都不含 LTools 自己；
/// 结束后原样恢复（模态对话框重新显示并回到前台）。
/// </remarks>
internal static class ScreenPicker
{
    /// <summary>框选一块屏幕区域。成功时 <paramref name="area"/> 是物理像素矩形，<paramref name="image"/> 是裁剪图。</summary>
    public static bool TryPickRegion(Window? owner, out Int32Rect area, out BitmapSource image)
    {
        area = default;
        image = null!;

        var restore = TransientHide.HideAll(owner);
        try
        {
            Thread.Sleep(160);   // 等 DWM 把「窗口已隐藏」的桌面画出来
            var monitor = MonitorUnderCursor();
            using var bmp = ScreenCapture.Capture(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            var shot = ToBitmapSource(bmp);

            var picker = new ScreenPickerWindow(shot, monitor, pointMode: false, wantColor: false);
            picker.ShowDialog();
            if (!picker.Confirmed || picker.Crop == null) return false;

            area = picker.Area;
            image = picker.Crop;
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ScreenPicker.Region", ex);
            TrayService.Notify("框选截图失败：" + ex.Message);
            return false;
        }
        finally
        {
            restore();
        }
    }

    /// <summary>在屏幕上单击取点（可同时取该点颜色）。</summary>
    public static bool TryPickPoint(Window? owner, out int x, out int y, out string hex)
    {
        x = y = 0;
        hex = "";

        var restore = TransientHide.HideAll(owner);
        try
        {
            Thread.Sleep(160);
            var monitor = MonitorUnderCursor();
            using var bmp = ScreenCapture.Capture(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            var shot = ToBitmapSource(bmp);

            var picker = new ScreenPickerWindow(shot, monitor, pointMode: true, wantColor: false);
            picker.ShowDialog();
            if (!picker.Confirmed) return false;

            x = picker.PointX;
            y = picker.PointY;
            hex = picker.Hex;
            return true;
        }
        catch (Exception ex)
        {
            CrashLog.Write("ScreenPicker.Point", ex);
            TrayService.Notify("屏幕取点失败：" + ex.Message);
            return false;
        }
        finally
        {
            restore();
        }
    }

    /// <summary>光标所在的显示器（物理像素）；取不到时退回整个虚拟桌面。</summary>
    private static System.Drawing.Rectangle MonitorUnderCursor()
    {
        if (Win32.GetCursorPos(out var pt))
        {
            var hmon = Win32.MonitorFromPoint(pt, Win32.MonitorDefaultToNearest);
            var mi = new Win32.MonitorInfo { cbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
            if (hmon != IntPtr.Zero && Win32.GetMonitorInfo(hmon, ref mi))
            {
                var r = mi.rcMonitor;
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w > 0 && h > 0) return new System.Drawing.Rectangle(r.Left, r.Top, w, h);
            }
        }

        return new System.Drawing.Rectangle(
            ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
            ScreenCapture.VirtualWidth, ScreenCapture.VirtualHeight);
    }

    /// <summary>GDI 位图 → 96 DPI 的 BitmapSource（1 像素 = 1 DIP，放大镜与坐标换算都按像素来）。</summary>
    private static BitmapSource ToBitmapSource(System.Drawing.Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var bytes = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var source = BitmapSource.Create(
                bitmap.Width, bitmap.Height, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
