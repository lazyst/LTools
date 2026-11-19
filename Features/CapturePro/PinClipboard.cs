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
/// 顺序照上游 <c>pinClipboardContent</c>：先看**文件列表**（资源管理器里复制的图片 / 文本文件，
/// 见 <see cref="PinFiles"/>），再看位图，最后才是文字。
/// 文字能解析成颜色就贴成**颜色取样板**（色块 + HEX / RGB / HSL 三行 + 三枚复制按钮），
/// 否则贴成普通文字卡片；两者都是按屏幕缩放渲染成位图，之后与图片贴图走同一条路径。
/// </para>
/// <para>
/// 剪贴板里若带着 <see cref="CaptureClipboard"/> 写的**外观 / 落位**元数据（LTools 自己复制过截图 /
/// 贴图），就按原位置 + 原外观贴回；文件被改过、桌面变了、图像素尺寸对不上都退回常规的「居中贴」。
/// 上游还会渲染剪贴板里的 HTML（<c>text/html</c>）；本地没有 HTML 排版引擎，
/// 取 <c>text/plain</c> 作为平台差异（见 <c>docs/CAPTURE_PARITY.md</c>）。
/// </para>
/// </remarks>
internal static class PinClipboard
{
    /// <summary>入口：贴剪贴板里的内容（必须在 UI 线程上调，由内置功能投递）。</summary>
    public static void Pin()
    {
        double scale = ScreenScale();
        Rect workArea = WorkAreaDip(scale);

        // 先在 UI 线程把元数据读一次（复制的截图会带外观 / 落位，见 CaptureClipboard）；
        // 后面按每张图的实际像素尺寸各自解码，解码本身不碰剪贴板，可以在后台线程做
        var meta = CaptureClipboard.Read();

        // 照上游 pinClipboardContent 的顺序：**先文件列表**，再图片，最后文字。
        // 「复制图片文件到剪贴板」时会同时有 CF_DIB 与 CF_HDROP，文件那条路能按文件签名
        // 认出「还是当初那一份」，从而贴回原位置；纯图片才走后面的图片分支。
        if (NativeClipboard.TryGetFileList(out var files))
        {
            var usable = PinFiles.Filter(files, out _);
            if (usable.Count > 0)
            {
                PinFiles.PinPaths(usable, PinStore.SourceFiles, meta);
                return;
            }
        }

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            // 图片没有文件签名（纯位图）时才按位图尺寸匹配元数据
            var appearance = meta.AppearanceFor(image.PixelWidth, image.PixelHeight);
            var placement = meta.PlacementFor(image.PixelWidth, image.PixelHeight);
            var look = appearance?.Look ?? PinAppearance.None;

            // 同一次复制（剪贴板序号没变）已经贴过：按「重复内容」设置处理
            string key = PinContentIdentity.ClipboardKey(Win32.GetClipboardSequenceNumber());
            if (CapturePinManager.HandleDuplicate(key) == PinDuplicateOutcome.Handled) return;

            if (placement != null &&
                CaptureClipboard.ResolveRect(placement, PinSettings.AutoResizeWindow) is Int32Rect at)
            {
                CapturePinManager.PinRestored(image, at, look,
                    appearance?.Checkerboard ?? look.HasShape, sourceKey: key);
                return;
            }

            CapturePinManager.PinClipboardImage(image, scale, workArea, PinStore.SourceClipboard, look, key);
            return;
        }

        if (NativeClipboard.TryGetText(out string text) && !string.IsNullOrWhiteSpace(text))
        {
            string key = PinContentIdentity.ClipboardKey(Win32.GetClipboardSequenceNumber());
            if (CapturePinManager.HandleDuplicate(key) == PinDuplicateOutcome.Handled) return;

            CapturePinManager.PinText(text, scale, workArea, sourceKey: key);
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
