using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>点击穿透时贴在贴图旁边的三个控件（透明度胶囊 / 移动 / 退出）的屏幕位置（DIP）。</summary>
/// <remarks>
/// 三个矩形紧挨着排成一行，<see cref="OpacityEditor"/> 的左上角就是整条控件栏的原点。
/// 空布局（<see cref="IsEmpty"/>）= 当前屏幕放不下，调用方要退出穿透——
/// 退出按钮都摆不出来时用户会被困在一张点不到的图后面。
/// </remarks>
internal readonly record struct PinClickThroughLayout(Rect OpacityEditor, Rect MoveButton, Rect ExitButton)
{
    public static PinClickThroughLayout Empty => new(Rect.Empty, Rect.Empty, Rect.Empty);

    public bool IsEmpty => OpacityEditor.IsEmpty || MoveButton.IsEmpty || ExitButton.IsEmpty;

    /// <summary>整条控件栏的左上角（= 透明度胶囊的左上角）。</summary>
    public Point Origin => OpacityEditor.TopLeft;
}

/// <summary>
/// 点击穿透的控件几何与不透明度口径。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshotpinnedclickthroughgeometry.cpp</c> 的 <c>controlsGeometry</c>：
/// 优先贴内容右上角内侧（内缩 16），上方放得下就翻到上方、否则翻到下方，整体再夹进工作区；
/// 屏幕宽度装不下 152 + 8 + 32 + 8 + 32 = 232 时返回空布局。
/// </para>
/// <para>
/// 上游按**物理像素**算（<c>qRound(kControlHeight * dpr)</c>）再换成逻辑坐标；这里全程 DIP，
/// WPF 自己按窗口 DPI 落到设备像素，所以不再有那层 round —— 尺寸与间距保持常量不变。
/// </para>
/// </remarks>
internal static class PinClickThrough
{
    /// <summary>每个控件的高度（也是两个圆按钮的边长）。</summary>
    public const double ControlHeight = 32;

    /// <summary>透明度胶囊的宽度（18 图标 + 8 + 96 滑条 + 左右各 12 内边距）。</summary>
    public const double OpacityEditorWidth = 152;

    /// <summary>控件之间的间隙。</summary>
    public const double ControlSpacing = 8;

    /// <summary>控件栏与贴图内容边缘的距离。</summary>
    public const double Inset = 16;

    /// <summary>移动按钮相对控件栏原点的左偏移（152 + 8 = 160）。</summary>
    public const double MoveLeft = OpacityEditorWidth + ControlSpacing;

    /// <summary>退出按钮相对控件栏原点的左偏移（160 + 32 + 8 = 200）。</summary>
    public const double ExitLeft = MoveLeft + ControlHeight + ControlSpacing;

    /// <summary>整条控件栏的宽度（200 + 32 = 232）。</summary>
    public const double TotalWidth = ExitLeft + ControlHeight;

    /// <summary>穿透不透明度的出厂值（上游 <c>click_through_opacity_percent</c> 默认 50）。</summary>
    public const int DefaultOpacity = 50;

    /// <summary>
    /// 算三个控件的屏幕位置（DIP）。<paramref name="content"/> 是贴图**内容**矩形
    /// （不含阴影 / 描边留白），<paramref name="workArea"/> 是它所在显示器的工作区。
    /// </summary>
    public static PinClickThroughLayout Layout(Rect content, Rect workArea)
    {
        if (content.IsEmpty || workArea.IsEmpty || content.Width <= 0 || content.Height <= 0) return PinClickThroughLayout.Empty;
        if (workArea.Width < TotalWidth || workArea.Height < ControlHeight) return PinClickThroughLayout.Empty;

        double preferredLeft = content.X + content.Width - Inset - TotalWidth;
        double maximumLeft = workArea.X + workArea.Width - TotalWidth;
        double maximumTop = workArea.Y + workArea.Height - ControlHeight;

        double aboveTop = content.Y - Inset - ControlHeight;
        double belowTop = content.Y + content.Height + Inset;
        double top = content.Y + Inset;

        if (aboveTop >= workArea.Y && aboveTop <= maximumTop) top = aboveTop;
        else if (belowTop >= workArea.Y && belowTop <= maximumTop) top = belowTop;

        double left = Clamp(preferredLeft, workArea.X, maximumLeft);
        double y = Clamp(top, workArea.Y, maximumTop);

        var origin = new Point(left, y);
        return new PinClickThroughLayout(
            new Rect(origin, new Size(OpacityEditorWidth, ControlHeight)),
            new Rect(new Point(origin.X + MoveLeft, origin.Y), new Size(ControlHeight, ControlHeight)),
            new Rect(new Point(origin.X + ExitLeft, origin.Y), new Size(ControlHeight, ControlHeight)));
    }

    /// <summary>穿透不透明度夹到 0–100（上游越界直接丢弃，这里夹住更好用）。</summary>
    public static int NormalizeOpacity(int percent) => percent < 0 ? 0 : percent > 100 ? 100 : percent;

    /// <summary>
    /// 实际生效的窗口不透明度：穿透态用穿透不透明度，否则用用户自己调的不透明度。
    /// </summary>
    public static double EffectiveOpacity(bool clickThrough, int userPercent, int clickThroughPercent) =>
        NormalizeOpacity(clickThrough ? clickThroughPercent : userPercent) / 100.0;

    private static double Clamp(double value, double min, double max)
    {
        if (max < min) return min;
        return value < min ? min : value > max ? max : value;
    }
}
