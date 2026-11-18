using System;
using System.Runtime.InteropServices;
using System.Windows;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 「贴剪贴板」：把剪贴板里的图片 / 文件 / 文字贴到屏幕上（照上游 `pinClipboardContentToScreen`）。
/// </summary>
/// <remarks>
/// <para>
/// 顺序照上游：先看位图，再看**文件列表**（资源管理器里复制的图片 / 文本文件，见
/// <see cref="PinFiles"/>），最后才是文字。
/// 文字能解析成颜色就贴成**颜色取样板**（色块 + HEX / RGB / HSL 三行 + 三枚复制按钮），
/// 否则贴成普通文字卡片；两者都是按屏幕缩放渲染成位图，之后与图片贴图走同一条路径。
/// </para>
/// <para>
/// 上游还会渲染剪贴板里的 HTML（<c>text/html</c>）；本地没有 HTML 排版引擎，
/// 取 `<c>text/plain</c>` 作为平台差异（见 <c>docs/CAPTURE_PARITY.md</c>）。
/// 落点照上游 <c>fitPinnedImageOnScreen</c>：光标所在显示器的工作区居中，装不下等比缩小。
/// </para>
/// </remarks>
internal static class PinClipboard
{
    /// <summary>入口：贴剪贴板里的内容（必须在 UI 线程上调，由内置功能投递）。</summary>
    public static void Pin()
    {
        double scale = ScreenScale();
        Rect workArea = WorkAreaDip(scale);

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            CapturePinManager.PinClipboardImage(image, scale, workArea);
            return;
        }

        if (NativeClipboard.TryGetFileList(out var files))
        {
            var usable = PinFiles.Filter(files, out _);
            if (usable.Count > 0)
            {
                PinFiles.PinPaths(usable);
                return;
            }
        }

        if (NativeClipboard.TryGetText(out string text) && !string.IsNullOrWhiteSpace(text))
        {
            CapturePinManager.PinText(text, scale, workArea);
            return;
        }

        ToastService.Show("剪贴板里没有可以贴的内容（图片、文件或文字）", "贴剪贴板", 2800,
            ToastKind.warning, key: "pin-clipboard");
    }

    /// <summary>光标所在显示器的缩放（物理像素 ÷ DIP）。</summary>
    internal static double ScreenScale()
    {
        int x = 0, y = 0;
        if (Win32.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
        }
        return ScreenShot.ScaleOf(new Int32Rect(x, y, 1, 1));
    }

    /// <summary>光标所在显示器的工作区（DIP，物理像素按 <paramref name="scale"/> 换算）。</summary>
    internal static Rect WorkAreaDip(double scale)
    {
        double s = scale > 0 ? scale : 1;

        var probe = new Win32.Point();
        Win32.GetCursorPos(out probe);
        IntPtr monitor = Win32.MonitorFromPoint(probe, Win32.MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new Win32.MonitorInfo { cbSize = Marshal.SizeOf<Win32.MonitorInfo>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                var work = info.rcWork;
                return new Rect(work.Left / s, work.Top / s,
                    Math.Max(1, (work.Right - work.Left) / s),
                    Math.Max(1, (work.Bottom - work.Top) / s));
            }
        }

        return new Rect(0, 0,
            Math.Max(1, SystemParameters.WorkArea.Width),
            Math.Max(1, SystemParameters.WorkArea.Height));
    }
}
