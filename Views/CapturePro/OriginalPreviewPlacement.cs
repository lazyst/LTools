using System;
using System.Windows;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「原文」预览的摆位与尺寸（照 Snow Shot 的 <c>ScreenshotOriginalImagePreviewWindow::placement</c>，
/// 但**不再按图片原始像素开窗**）。
/// </summary>
/// <remarks>
/// <para>
/// 上游的识别窗正好是选区大小、预览贴在选区外面，所以「贴着放」就够。LTools 的识别窗是一张
/// 卡片（最宽 1000、最高 840），按图片原始像素开窗（比如 858×748）时两侧根本塞不下，
/// 只能盖在识别窗上——又大又丑（用户报过）。这里改成：
/// ① 尺寸固定一档（<see cref="DefaultSize"/>，工作区的 22%，300–420 宽）；
/// ② 依次试右 → 左 → 上 → 下，**要求整块落在工作区里**；
/// ③ 四周都不够时才退回「识别窗内侧右边、垂直居中」，夹进工作区。
/// </para>
/// <para>
/// 纯函数，<c>--selftest</c> 直接断言（上游同一个函数也有测试）。尺寸与工作区都是 DIP。
/// </para>
/// </remarks>
internal static class OriginalPreviewPlacement
{
    /// <summary>和识别窗之间留的缝（上游 <c>gap</c>）。</summary>
    public const double Gap = 8;

    /// <summary>退回「贴在识别窗里面」时离边框的间距。</summary>
    public const double Inset = 16;

    private enum Side
    {
        Right,
        Left,
        Above,
        Below,
    }

    /// <summary>
    /// 卡片默认尺寸：工作区宽的 22%，夹在 300–420；高 = 宽 × 0.75，不超过工作区高的 45%。
    /// **永远按「适应窗口」打开**——图片本身多大跟窗口无关，100% 是滚轮 / 双击的事。
    /// </summary>
    public static Size DefaultSize(Rect work)
    {
        if (work.Width <= 0 || work.Height <= 0) return new Size(360, 270);

        double width = Math.Clamp(work.Width * 0.22, 300, 420);
        double height = Math.Clamp(width * 0.75, 200, Math.Max(200, work.Height * 0.45));
        return new Size(Math.Round(width), Math.Round(height));
    }

    /// <summary>摆位：右 → 左 → 上 → 下，都不够就贴到识别窗内侧右边。返回空矩形表示尺寸不合法。</summary>
    public static Rect Place(Rect host, Rect work, Size size, bool preferLeft = false)
    {
        if (size.Width <= 0 || size.Height <= 0) return Rect.Empty;
        if (work.Width <= 0 || work.Height <= 0) return Rect.Empty;

        if (host.Width <= 0 || host.Height <= 0)
            return Clamp(new Rect(work.Right - size.Width - Inset,
                work.Bottom - size.Height - Inset, size.Width, size.Height), work);

        Side[] order = preferLeft
            ? new[] { Side.Left, Side.Right, Side.Above, Side.Below }
            : new[] { Side.Right, Side.Left, Side.Above, Side.Below };

        foreach (Side side in order)
        {
            Rect candidate = Beside(host, work, size, side);
            if (Fits(candidate, work)) return candidate;
        }

        return Inside(host, work, size);
    }

    /// <summary>摆完之后是不是压在识别窗上（压上就该让阴影更明显，读作「浮在上面的一张卡」）。</summary>
    public static bool Overlaps(Rect placed, Rect host) =>
        !placed.IsEmpty && !host.IsEmpty && placed.IntersectsWith(host);

    private static Rect Beside(Rect host, Rect work, Size size, Side side)
    {
        double x, y;
        switch (side)
        {
            case Side.Right:
                x = host.Right + Gap;
                y = host.Top;
                break;
            case Side.Left:
                x = host.Left - Gap - size.Width;
                y = host.Top;
                break;
            case Side.Above:
                x = host.Left;
                y = host.Top - Gap - size.Height;
                break;
            default:
                x = host.Left;
                y = host.Bottom + Gap;
                break;
        }

        // 上下摆放时横向对齐宿主再夹进工作区；左右摆放时纵向对齐宿主再夹
        if (side is Side.Above or Side.Below)
            x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - size.Width));
        else
            y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - size.Height));

        return new Rect(x, y, size.Width, size.Height);
    }

    /// <summary>四周都塞不下：贴识别窗内侧右边、垂直居中，像一张画中画。</summary>
    private static Rect Inside(Rect host, Rect work, Size size)
    {
        double x = Math.Max(host.Right - Inset - size.Width, host.Left + Inset);
        double y = host.Top + (host.Height - size.Height) / 2;
        return Clamp(new Rect(x, y, size.Width, size.Height), work);
    }

    private static Rect Clamp(Rect rect, Rect work) => new(
        Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.Width)),
        Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height)),
        rect.Width, rect.Height);

    private static bool Fits(Rect rect, Rect work) =>
        rect.Left >= work.Left - 0.5 && rect.Top >= work.Top - 0.5 &&
        rect.Right <= work.Right + 0.5 && rect.Bottom <= work.Bottom + 0.5;
}
