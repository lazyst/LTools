using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图自带的选区外观：圆角 / 阴影 / 边框**已经烘进成品图里**，这里只记窗口还要不要额外装饰。
/// </summary>
/// <remarks>
/// <para>
/// 对应上游 <c>storage::PinnedBorderAppearance</c> 里贴图窗口真正用到的几项：
/// <c>cornerRadius</c>、<c>hasShadow</c> / <c>hasBorder</c>，以及由 <c>contentRect</c> 表达的
/// 「图比内容大出多少」（这里用 <see cref="EffectPx"/>）。长度一律是**成品图的像素**——
/// 与截图同一个坐标空间，换显示器 / 改缩放都不用重算。
/// </para>
/// <para>
/// 上游把这套形容器化保存（<c>PinnedWindowRecord.borderAppearance</c>），贴图重开时按它恢复；
/// 本地同样落进 <c>pins/pins.json</c>（见 <see cref="PinRecord"/>）。
/// </para>
/// </remarks>
internal sealed record PinAppearance
{
    /// <summary>图里没有烘进任何效果。</summary>
    public const string DecorationNone = "";

    /// <summary>图里烘进的是阴影（<c>SelectionBorderType.Shadow</c>）。</summary>
    public const string DecorationShadow = "shadow";

    /// <summary>图里烘进的是外扩边框（<c>SelectionBorderType.Border</c>）。</summary>
    public const string DecorationBorder = "border";

    /// <summary>圆角半径（图像素；0 = 直角）。</summary>
    public int RadiusPx { get; init; }

    /// <summary>内容四周的效果内边距（图像素）：阴影 / 外扩边框的宽度。</summary>
    public int EffectPx { get; init; }

    /// <summary>烘进图里的效果：<see cref="DecorationNone"/> / <see cref="DecorationShadow"/> / <see cref="DecorationBorder"/>。</summary>
    public string Decoration { get; init; } = DecorationNone;

    /// <summary>没有外观（直角、无效果）：贴图与 P3 之前完全一样。</summary>
    public static PinAppearance None => new();

    /// <summary>
    /// 图本身带透明外形（圆角外 / 阴影外）：底下不能再铺那块不透明底，
    /// 否则透明的地方会被画成深灰（照上游「有形状就开棋盘格 / 不铺底」同口径）。
    /// </summary>
    public bool HasShape => RadiusPx > 0 || EffectPx > 0;

    /// <summary>
    /// 阴影 / 边框已经画在图里：窗口不要再叠自己那圈 1px 描边
    /// （描边会包住整块带内边距的窗口，看起来像套了个方框）。
    /// </summary>
    public bool SuppressesFrame => EffectPx > 0;

    /// <summary>效果名称规整（认不出的值按「没有」处理）。</summary>
    public static string NormalizeDecoration(string? decoration) =>
        decoration == DecorationShadow || decoration == DecorationBorder ? decoration : DecorationNone;

    /// <summary>按成品图 / 内容的尺寸夹一遍（圆角不超过短边的一半，与上游 <c>std::clamp</c> 同口径）。</summary>
    public PinAppearance Clamp(int width, int height)
    {
        int shortSide = Math.Max(0, Math.Min(width, height));
        int radius = Math.Max(0, RadiusPx);
        if (shortSide > 0) radius = Math.Min(radius, shortSide / 2);

        return this with
        {
            RadiusPx = radius,
            EffectPx = Math.Max(0, EffectPx),
            Decoration = NormalizeDecoration(Decoration),
        };
    }

    /// <summary>
    /// 从选区样式算出外观（导出前调一次）：长度是 DIP，按 <paramref name="scale"/> 换成图像素，
    /// <paramref name="contentWidth"/> / <paramref name="contentHeight"/> 是选区那块（物理像素）。
    /// </summary>
    public static PinAppearance FromStyle(SelectionStyle style, double scale,
        int contentWidth, int contentHeight)
    {
        var clamped = style.Clamp();
        double dpi = scale > 0 ? scale : 1;

        int effect = clamped.EffectPixels(dpi);
        int radius = clamped.RadiusPixels(dpi);
        int shortSide = Math.Max(0, Math.Min(contentWidth, contentHeight));
        if (shortSide > 0) radius = Math.Min(radius, shortSide / 2);

        return new PinAppearance
        {
            RadiusPx = Math.Max(0, radius),
            EffectPx = Math.Max(0, effect),
            Decoration = effect <= 0
                ? DecorationNone
                : clamped.BorderType == SelectionBorderType.Border
                    ? DecorationBorder
                    : DecorationShadow,
        };
    }

    /// <summary>
    /// 贴图窗口该占的屏幕矩形：把带效果内边距的图摆回去，**内容正好落在 <paramref name="content"/> 上**
    /// （图比选区大出来的那圈是透明/阴影，往四周让出去）。
    /// </summary>
    public Int32Rect WindowRect(Int32Rect content, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return content;

        // 允许负数：图比内容小（不该发生）时也保持两边等距，内容中心压在窗口中心上
        int padX = (imageWidth - content.Width) / 2;
        int padY = (imageHeight - content.Height) / 2;
        return new Int32Rect(content.X - padX, content.Y - padY, imageWidth, imageHeight);
    }

    /// <summary>圆角在窗口里的 DIP 半径（贴图缩放后跟着变，贴图窗口画描边时用）。</summary>
    public double RadiusDip(double scale, double zoom) =>
        RadiusPx <= 0 ? 0 : RadiusPx / (scale > 0 ? scale : 1) * (zoom > 0 ? zoom : 1);
}
