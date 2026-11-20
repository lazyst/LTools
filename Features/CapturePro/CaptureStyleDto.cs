namespace LTools.Features.CapturePro;

/// <summary>
/// 选区外观的落盘形状（圆角 / 阴影 / 边框 / 背景不透明度），采集历史「重新编辑截图」照它还原。
/// </summary>
/// <remarks>
/// 字段与 <see cref="SelectionStyle"/> 一一对应，但颜色写成 <c>#RRGGBB</c>、边框方式写成
/// <c>shadow</c> / <c>border</c>——与 <c>configs/ui.json</c> 的 <c>CaptureSelection</c> 同一套写法，
/// 所以老配置、采集历史、预设三处说的都是同一件事。长度单位是 DIP。
/// </remarks>
internal sealed class CaptureStyleDto
{
    public double CornerRadius { get; set; }

    public double ShadowWidth { get; set; }

    public string? ShadowColor { get; set; }

    public double BorderWidth { get; set; }

    public string? BorderColor { get; set; }

    public string? BorderType { get; set; }

    public int BackgroundOpacity { get; set; }

    /// <summary>导出时压缩选区里的整段空白（照上游 <c>layoutCompaction.enabled</c>）。</summary>
    public bool CompactLayout { get; set; }

    /// <summary>压缩后每段之间留多少物理像素。</summary>
    public int CompactSpacing { get; set; }

    /// <summary>从文档里取（外观在 <see cref="CaptureDocument.Style"/>，压缩选项也挂在文档上）。</summary>
    public static CaptureStyleDto Of(CaptureDocument document) =>
        Of(document.Style, document.CompactLayout, document.CompactSpacing);

    /// <summary>只取外观（不关心压缩选项时用，例如自检里的纯换算）。</summary>
    public static CaptureStyleDto Of(SelectionStyle style) => Of(style, false, 0);

    public static CaptureStyleDto Of(SelectionStyle style, bool compactLayout, int compactSpacing)
    {
        var clamped = style.Clamp();
        return new CaptureStyleDto
        {
            CornerRadius = clamped.CornerRadius,
            ShadowWidth = clamped.ShadowWidth,
            ShadowColor = ToolStyleFormat.Hex(clamped.ShadowColor),
            BorderWidth = clamped.BorderWidth,
            BorderColor = ToolStyleFormat.Hex(clamped.BorderColor),
            BorderType = clamped.BorderType == SelectionBorderType.Border ? "border" : "shadow",
            BackgroundOpacity = clamped.BackgroundOpacity,
            CompactLayout = compactLayout,
            CompactSpacing = compactSpacing,
        };
    }

    public SelectionStyle ToStyle()
    {
        if (!ToolStyleFormat.TryParseColor(ShadowColor, out var shadow))
            shadow = SelectionStyle.DefaultShadowColor;
        if (!ToolStyleFormat.TryParseColor(BorderColor, out var border))
            border = SelectionStyle.DefaultBorderColor;

        return new SelectionStyle
        {
            CornerRadius = CornerRadius,
            ShadowWidth = ShadowWidth,
            ShadowColor = shadow,
            BorderWidth = BorderWidth,
            BorderColor = border,
            BorderType = string.Equals(BorderType, "border", System.StringComparison.OrdinalIgnoreCase)
                ? SelectionBorderType.Border
                : SelectionBorderType.Shadow,
            BackgroundOpacity = BackgroundOpacity,
        }.Clamp();
    }
}
