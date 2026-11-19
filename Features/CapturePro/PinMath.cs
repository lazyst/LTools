using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图窗口的纯计算：缩放 / 不透明度步进、滚轮缩放时的锚点换算、初始落点、
/// 局部显示的内容偏移、成组移动的位移夹取。
/// </summary>
/// <remarks>
/// 放在这里而不是窗口里，<c>--selftest</c> 才能直接断言（不用开窗口）：
/// 滚轮缩放要「光标下的那个点不动」，这条最容易写成「窗口左上角不动」。
/// </remarks>
internal static class PinMath
{
    public const double ZoomMin = 0.1;
    public const double ZoomMax = 4.0;

    public const double OpacityMin = 0.2;
    public const double OpacityMax = 1.0;

    /// <summary>滚轮一格（120）走几档；按整数倍取，免得来回滚一次回不到原值。</summary>
    private static int Steps(int delta) => delta == 0 ? 0 : Math.Max(1, (int)Math.Round(Math.Abs(delta) / 120.0));

    /// <summary>
    /// 缩放一步（<paramref name="delta"/> 为滚轮增量，正 = 放大）。
    /// </summary>
    /// <remarks>
    /// 照上游 <c>applyWheelScaleSteps</c>：滚轮在「步长的整数倍」这排档位之间跳，
    /// 不是往当前值上加减——拖边框得到 137% 时滚一格是 140%，不是 147%。
    /// <paramref name="stepPercent"/> 来自 <see cref="PinSettings.ZoomStepPercent"/>（默认 10）。
    /// </remarks>
    public static double NextZoom(double zoom, int delta, int stepPercent = PinSettings.DefaultZoomStep)
    {
        int steps = Steps(delta);
        if (steps == 0) return ClampZoom(zoom);

        int step = PinSettings.NormalizeStep(stepPercent, PinSettings.DefaultZoomStep);
        int displayed = (int)Math.Round(ClampZoom(zoom) * 100);
        bool up = delta > 0;
        double currentLevel = (double)displayed / step;
        double level = up ? Math.Floor(currentLevel) : Math.Ceiling(currentLevel);
        int target = (int)Math.Round(level * step + (up ? steps : -steps) * step);
        return ClampZoom(Math.Round(target / 100.0, 4));
    }

    /// <summary>
    /// 不透明度一步（<paramref name="delta"/> 为滚轮增量，正 = 更不透明）。
    /// 照上游 <c>handleOpacityWheel</c>：直接加减步长（百分点），夹在 20–100。
    /// </summary>
    public static double NextOpacity(double opacity, int delta, int stepPercent = PinSettings.DefaultOpacityStep)
    {
        int steps = Steps(delta);
        if (steps == 0) return ClampOpacity(opacity);

        int step = PinSettings.NormalizeStep(stepPercent, PinSettings.DefaultOpacityStep);
        int displayed = (int)Math.Round(ClampOpacity(opacity) * 100);
        int target = displayed + (delta > 0 ? steps : -steps) * step;
        return ClampOpacity(Math.Round(target / 100.0, 4));
    }

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, ZoomMin, ZoomMax);

    public static double ClampOpacity(double opacity) => Math.Clamp(opacity, OpacityMin, OpacityMax);

    /// <summary>
    /// 缩放后窗口左上角该放哪：让窗口里 <paramref name="at"/> 那个点（DIP）缩放前后**落在同一处屏幕位置**。
    /// 没有它的话滚轮缩放会「窗口左上角不动」，光标下的内容跟着跑。
    /// </summary>
    public static Point AnchoredOrigin(Point origin, Size oldSize, Size newSize, Point at) =>
        AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomMousePosition);

    /// <summary>
    /// 按滚轮锚点设置缩放：<c>mouse_position</c> 用光标在窗口里的比例，
    /// 四角 / 中心用固定比例（照上游 <c>scaleAnchorFromSetting</c>）。
    /// </summary>
    public static Point AnchoredOrigin(Point origin, Size oldSize, Size newSize, Point at, string? mode)
    {
        var (rx, ry) = Ratio(oldSize, mode, at);
        return new Point(
            origin.X + rx * (oldSize.Width - newSize.Width),
            origin.Y + ry * (oldSize.Height - newSize.Height));
    }

    /// <summary>某个滚轮锚点在窗口里的实际位置（取景框里当缩放锚点用）。</summary>
    public static Point AnchorPoint(Size size, string? mode, Point at)
    {
        var (rx, ry) = Ratio(size, mode, at);
        return new Point(rx * size.Width, ry * size.Height);
    }

    private static (double X, double Y) Ratio(Size size, string? mode, Point at)
    {
        var (rx, ry) = PinSettings.AnchorRatio(mode);
        if (!double.IsNaN(rx)) return (rx, ry);

        rx = size.Width > 0.5 ? Math.Clamp(at.X / size.Width, 0, 1) : 0.5;
        ry = size.Height > 0.5 ? Math.Clamp(at.Y / size.Height, 0, 1) : 0.5;
        return (rx, ry);
    }

    /// <summary>
    /// 贴图刚出现时放在哪：正好盖住截图的那块区域（屏幕物理像素 → DIP）。
    /// 100% 缩放时用户看到的和刚才截下来的一模一样。
    /// </summary>
    public static Rect PlaceAt(Rect physicalArea, double scale)
    {
        double s = scale > 0 ? scale : 1;
        return new Rect(physicalArea.X / s, physicalArea.Y / s,
            Math.Max(1, physicalArea.Width / s), Math.Max(1, physicalArea.Height / s));
    }

    // ============ 局部显示（放大后只露一块） ============

    /// <summary>拖动 / 成组移动时至少留多少 DIP 在桌面里（拖出去就找不回来了）。</summary>
    public const double KeepVisible = 40;

    /// <summary>虚拟桌面（DIP）：贴图拖动的边界（多屏时 <c>Left</c> / <c>Top</c> 可能为负）。</summary>
    public static Rect VirtualDesktop => new(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

    /// <summary>
    /// 局部显示里内容的偏移夹紧：内容比取景框大就限制在「始终盖住取景框」的范围里（不露出空白），
    /// 比取景框小就居中（否则缩小时内容贴在左上角，看着像画歪了）。
    /// </summary>
    public static Vector ClampPan(Vector pan, Size content, Size view)
        => new(ClampAxis(pan.X, content.Width, view.Width), ClampAxis(pan.Y, content.Height, view.Height));

    private static double ClampAxis(double pan, double content, double view)
    {
        if (content <= 0) return 0;
        if (content <= view) return (view - content) / 2;
        return Math.Clamp(pan, view - content, 0);
    }

    /// <summary>刚进局部显示时看哪一块：内容比取景框大就先居中。</summary>
    public static Vector CenterPan(Size content, Size view)
        => ClampPan(new Vector((view.Width - content.Width) / 2, (view.Height - content.Height) / 2), content, view);

    /// <summary>
    /// 局部显示里换缩放：让窗口里 <paramref name="anchor"/> 那个点（取景框坐标）下的内容不跑。
    /// 与 <see cref="AnchoredOrigin"/> 是同一件事的两个坐标空间——那边挪窗口，这边挪内容。
    /// </summary>
    public static Vector ZoomAnchoredPan(Vector pan, Point anchor, double oldZoom, double newZoom)
    {
        if (oldZoom <= 0.0001) return pan;
        double px = (anchor.X - pan.X) / oldZoom;      // 锚点下的内容坐标（未缩放）
        double py = (anchor.Y - pan.Y) / oldZoom;
        return new Vector(anchor.X - px * newZoom, anchor.Y - py * newZoom);
    }

    /// <summary>局部显示的取景框尺寸：不超过内容，也不超过桌面（内容只有一小块时窗口不该比图还大）。</summary>
    public static Size ViewportSize(Size content, Rect desktop)
        => new(Math.Max(1, Math.Min(content.Width, Math.Max(1, desktop.Width))),
               Math.Max(1, Math.Min(content.Height, Math.Max(1, desktop.Height))));

    // ============ 缩略图模式 ============

    /// <summary>缩略图模式的窗口边长（DIP）：一个固定的小方块，内容等比缩进去（照 Snow Shot 的 83px）。</summary>
    public const double ThumbnailSide = 83;

    /// <summary>
    /// 悬停按钮（✕）在小窗口里会盖住内容：窗口小于这个尺寸就干脆不显示，
    /// 出口仍留着（双击 / 右键菜单）。
    /// </summary>
    public const double HoverButtonMinSide = 56;

    /// <summary>这个尺寸的贴图要不要在悬停时显示右上角的按钮（缩略图里一律不显示：会盖住内容）。</summary>
    public static bool ShowsHoverButtons(double width, double height, bool thumbnail) =>
        !thumbnail && width >= HoverButtonMinSide && height >= HoverButtonMinSide;


    // ============ 抖动提醒（贴到重复内容时） ============

    /// <summary>抖动一次的时长（毫秒，照上游 <c>shakeForAttention</c> 的 300）。</summary>
    public const int ShakeDurationMs = 300;

    /// <summary>抖动幅度（DIP）：上游是 6 × 屏幕缩放，DIP 下就是 6。</summary>
    public const double ShakeAmplitude = 6;

    /// <summary>
    /// 抖动的关键帧（照上游）：1/6 拍 −1、2/6 拍 +1、3/6 拍 −0.75、4/6 拍 +0.5、5/6 拍 −0.25，
    /// 两端都是 0。返回的是**幅度倍数**（−1…1），乘 <see cref="ShakeAmplitude"/> 才是 DIP。
    /// </summary>
    private static readonly (double At, double Value)[] ShakeKeys =
    {
        (0.0, 0.0),
        (1.0 / 6.0, -1.0),
        (2.0 / 6.0, 1.0),
        (3.0 / 6.0, -0.75),
        (4.0 / 6.0, 0.5),
        (5.0 / 6.0, -0.25),
        (1.0, 0.0),
    };

    /// <summary>
    /// 抖动在 <paramref name="progress"/>（0…1）时的水平偏移（DIP）。
    /// 纯函数，<c>--selftest</c> 直接断言曲线与端点。
    /// </summary>
    public static double ShakeOffset(double progress)
    {
        if (!double.IsFinite(progress) || progress <= 0) return 0;
        if (progress >= 1) return 0;

        for (int i = 1; i < ShakeKeys.Length; i++)
        {
            var (at, value) = ShakeKeys[i];
            if (progress > at) continue;

            var (prevAt, prevValue) = ShakeKeys[i - 1];
            double span = at - prevAt;
            if (span <= 0) return value * ShakeAmplitude;
            double t = (progress - prevAt) / span;
            return (prevValue + (value - prevValue) * t) * ShakeAmplitude;
        }

        return 0;
    }

    // ============ 成组移动 ============

    /// <summary>
    /// 成组移动的位移：整组共用一个，且没有一张被推出桌面。
    /// **各自夹会把相对位置压扁**——拖到边上时有的先停、有的还在走，组就散了。
    /// 单张贴图也走这里（<paramref name="boxes"/> 只有自己那一张），夹法只有一份。
    /// </summary>
    public static Vector ConstrainGroupMove(Vector delta, IReadOnlyList<Rect> boxes, Rect desktop, double margin)
    {
        double minX = double.NegativeInfinity, maxX = double.PositiveInfinity;
        double minY = double.NegativeInfinity, maxY = double.PositiveInfinity;

        foreach (var box in boxes)
        {
            minX = Math.Max(minX, desktop.Left - box.Width + margin - box.Left);
            maxX = Math.Min(maxX, desktop.Right - margin - box.Left);
            minY = Math.Max(minY, desktop.Top - box.Height + margin - box.Top);
            maxY = Math.Min(maxY, desktop.Bottom - margin - box.Top);
        }

        // 一张都没有 → ±∞ 范围（原样放行）；某一轴比桌面还大 → 那个轴别动
        double x = minX <= maxX ? Math.Clamp(delta.X, minX, maxX) : 0;
        double y = minY <= maxY ? Math.Clamp(delta.Y, minY, maxY) : 0;
        return new Vector(x, y);
    }
}
