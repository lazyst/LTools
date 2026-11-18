using System;
using System.Windows;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 顶部选区信息条里可以滚轮 / 点击的那几个字段（照上游 <c>ScreenshotSelectionToolbarWidget::Field</c>）。
/// </summary>
internal enum SelectionField
{
    X,
    Y,
    Width,
    Height,
    Radius,
    EffectWidth,
}

/// <summary>
/// 选区参数：**几何是屏幕物理像素 + 顺时针左上角**，外观长度也是物理像素。
/// </summary>
/// <remarks>
/// <para>
/// 这是「界面那一层」的形状：顶部信息条显示、滚轮改值、选区参数对话框、快速集都用它，
/// 与用户看到的数字一一对应（界面上写 1280 就是 1280 物理像素）。
/// 覆盖层落进 <see cref="SelectionStyle"/> 时再换回 DIP（外观）与窗口 DIP（几何）。
/// </para>
/// <para>
/// 上游把这些叫 <c>ScreenshotSelectionParams</c>（几何 + 圆角 + 边框类型 / 宽 / 色 + 比例锁）。
/// </para>
/// </remarks>
internal sealed record SelectionParams
{
    /// <summary>屏幕物理像素矩形（左上角已含显示器偏移）。</summary>
    public Int32Rect Area { get; init; }

    /// <summary>锁住宽高比（对话框里的那把锁 / 信息条上的锁）。</summary>
    public bool LockAspect { get; init; }

    /// <summary>圆角半径（物理像素，上限 <see cref="SelectionStyle.CornerRadiusMax"/>）。</summary>
    public int RadiusPixels { get; init; }

    /// <summary>效果类型：阴影 / 边框。</summary>
    public SelectionBorderType BorderType { get; init; }

    /// <summary>阴影宽度（物理像素）；切到边框时这一份也留着（照上游两份各存）。</summary>
    public int ShadowWidthPixels { get; init; }

    /// <summary>边框宽度（物理像素）。</summary>
    public int BorderWidthPixels { get; init; }

    /// <summary>阴影色。</summary>
    public Color ShadowColor { get; init; } = SelectionStyle.DefaultShadowColor;

    /// <summary>边框色。</summary>
    public Color BorderColor { get; init; } = SelectionStyle.DefaultBorderColor;

    /// <summary>当前生效的效果宽度（物理像素，上限 <see cref="SelectionStyle.EffectWidthMax"/>）。</summary>
    public int EffectWidthPixels => BorderType == SelectionBorderType.Border
        ? BorderWidthPixels
        : ShadowWidthPixels;

    /// <summary>当前生效的效果颜色。</summary>
    public Color EffectColor => BorderType == SelectionBorderType.Border ? BorderColor : ShadowColor;

    public int X => Area.X;
    public int Y => Area.Y;
    public int Width => Area.Width;
    public int Height => Area.Height;

    /// <summary>
    /// 按选区外观 + 屏幕比例造一份参数（信息条 / 对话框打开时的初始值）。
    /// </summary>
    public static SelectionParams From(Int32Rect area, bool lockAspect, SelectionStyle style, double scale)
    {
        var clamped = style.Clamp();
        return new SelectionParams
        {
            Area = area,
            LockAspect = lockAspect,
            RadiusPixels = clamped.RadiusPixels(scale),
            BorderType = clamped.BorderType,
            ShadowWidthPixels = Math.Max(0, (int)Math.Round(clamped.ShadowWidth * (scale > 0 ? scale : 1))),
            BorderWidthPixels = Math.Max(0, (int)Math.Round(clamped.BorderWidth * (scale > 0 ? scale : 1))),
            ShadowColor = clamped.ShadowColor,
            BorderColor = clamped.BorderColor,
        };
    }

    /// <summary>换当前生效的效果宽度（不动另一种）。</summary>
    public SelectionParams WithEffectWidth(int pixels) => BorderType == SelectionBorderType.Border
        ? this with { BorderWidthPixels = Math.Clamp(pixels, 0, SelectionStyle.EffectWidthMax) }
        : this with { ShadowWidthPixels = Math.Clamp(pixels, 0, SelectionStyle.EffectWidthMax) };

    /// <summary>换当前生效的效果颜色（不动另一种）。</summary>
    public SelectionParams WithEffectColor(Color color) => BorderType == SelectionBorderType.Border
        ? this with { BorderColor = color }
        : this with { ShadowColor = color };

    /// <summary>套回外观（物理像素 → DIP，两种宽度 / 颜色各自保留）。</summary>
    public SelectionStyle ApplyTo(SelectionStyle style, double scale)
    {
        double s = scale > 0 ? scale : 1;
        return style with
        {
            BorderType = BorderType,
            CornerRadius = Dip(RadiusPixels, s),
            ShadowWidth = Dip(ShadowWidthPixels, s),
            BorderWidth = Dip(BorderWidthPixels, s),
            ShadowColor = ShadowColor,
            BorderColor = BorderColor,
        };
    }

    /// <summary>换一块几何（其余字段照旧）。</summary>
    public SelectionParams WithArea(Int32Rect area) => this with { Area = area };

    /// <summary>夹到给定屏幕范围里（至少 1×1）。</summary>
    public SelectionParams ClampTo(Int32Rect bounds)
    {
        int width = Math.Clamp(Width, 1, Math.Max(1, bounds.Width));
        int height = Math.Clamp(Height, 1, Math.Max(1, bounds.Height));
        int x = Math.Clamp(X, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - width));
        int y = Math.Clamp(Y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - height));
        return WithArea(new Int32Rect(x, y, width, height));
    }

    private static double Dip(int pixels, double scale) =>
        Math.Max(0, Math.Max(0, pixels) / (scale > 0 ? scale : 1));
}

/// <summary>
/// 顶部选区信息条的位置（照上游 <c>ScreenshotGeometryMapper::selectionToolbarContentPosition</c>）：
/// 优先放选区**上方**，放不下依次试右侧、左侧、下方，四处都放不下就夹回可见区。
/// </summary>
/// <remarks>
/// 上游是「第一个能完整放下的位置」，不是「挑一个最合适的」——顺序不能改：
/// 上方几乎总是首选，只有贴近屏幕顶边时才会跑到右边 / 左边 / 下边。
/// </remarks>
internal static class SelectionBarPlacement
{
    /// <summary>信息条与选区之间的间距（上游 <c>kSelectionToolbarGap</c>）。</summary>
    public const double Gap = 4;

    /// <summary>按上游顺序挑一个位置；返回的是信息条左上角。</summary>
    public static Point Place(Rect selection, Size bar, Rect bounds, double gap = Gap)
    {
        if (bar.Width <= 0 || bar.Height <= 0) return selection.TopLeft;

        Point[] candidates =
        {
            new(selection.Left, selection.Top - bar.Height - gap),           // 上方
            new(selection.Right + gap, selection.Top),                       // 右侧
            new(selection.Left - bar.Width - gap, selection.Top),            // 左侧
            new(selection.Left, selection.Bottom + gap),                     // 下方
        };

        foreach (var candidate in candidates)
            if (Fits(candidate, bar, bounds)) return candidate;

        return ClampInside(candidates[0], bar, bounds);
    }

    private static bool Fits(Point at, Size bar, Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        return at.X >= bounds.Left - 0.5 && at.Y >= bounds.Top - 0.5 &&
               at.X + bar.Width <= bounds.Right + 0.5 &&
               at.Y + bar.Height <= bounds.Bottom + 0.5;
    }

    private static Point ClampInside(Point at, Size bar, Rect bounds)
    {
        double maxX = Math.Max(bounds.Left, bounds.Right - bar.Width);
        double maxY = Math.Max(bounds.Top, bounds.Bottom - bar.Height);
        return new Point(
            Math.Clamp(at.X, bounds.Left, maxX),
            Math.Clamp(at.Y, bounds.Top, maxY));
    }
}
