using System;
using System.Windows;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「原文预览」小窗摆位（照 Snow Shot 的 <c>ScreenshotOriginalImagePreviewWindow::placement</c>）：
/// 贴在识别窗右边，右边放不下翻左边，上下放不下翻下 / 上，最后夹进工作区。
/// </summary>
/// <remarks>
/// 纯函数，<c>--selftest</c> 直接断言（上游同一个函数也有测试）。工作区与尺寸都是 DIP。
/// </remarks>
internal static class OriginalPreviewPlacement
{
    /// <summary>和识别窗之间留的缝（上游 <c>gap</c>）。</summary>
    public const double Gap = 8;

    public static Rect Place(Rect host, Rect work, Size size, bool preferLeft = false)
    {
        if (size.Width <= 0 || size.Height <= 0) return Rect.Empty;

        double right = host.Right + Gap;
        double left = host.Left - Gap - size.Width;
        bool fitsRight = right + size.Width <= work.Right + 0.5;
        bool fitsLeft = left >= work.Left - 0.5;

        double x = preferLeft
            ? (fitsLeft ? left : fitsRight ? right : host.Left)
            : (fitsRight ? right : fitsLeft ? left : host.Left);

        // 上下优先贴识别窗的底边；放不下翻到上方；都放不下就对齐识别窗顶边再夹。
        double below = host.Bottom + Gap;
        double above = host.Top - Gap - size.Height;
        double y = below + size.Height <= work.Bottom + 0.5
            ? below
            : above >= work.Top - 0.5 ? above : host.Top;

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));
        return new Rect(x, y, size.Width, size.Height);
    }
}
