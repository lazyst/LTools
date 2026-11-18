using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// <see cref="ToolStyle"/> 的复制与「只搬运改过的那几项」。
/// </summary>
/// <remarks>
/// <para>
/// 选中一条标注时，参数行显示的是**那一条的值**（照上游 <c>SelectedArrow</c> / <c>SelectedRectangle</c> 这些
/// 样式来源），不是这一族的创建默认值；所以工具栏改的是一份**临时的显示样式**。
/// </para>
/// <para>
/// 改完要落回两处：① 选中的那条标注（整份写回）；② 这一族的创建样式——**只写真正变过的那几项**
/// （上游 <c>set_shape_style_patch</c> 只 merge <c>properties</c> 里的位）。不这么分开的话，
/// 「选一条旧箭头看看它的颜色」会把创建样式整份替换成那条旧箭头的值。
/// </para>
/// <para>
/// <see cref="CopyChangedTo"/> 是逐字段手写的：多一个样式字段就在这三处一起加（属性 / <c>ToDto</c> / 这里），
/// 不做反射，免得打包裁剪后悄悄漏字段。
/// </para>
/// </remarks>
internal sealed partial class ToolStyle
{
    /// <summary>整份复制（显示样式从创建样式拷一份出来改）。</summary>
    public ToolStyle Clone() => (ToolStyle)MemberwiseClone();

    /// <summary>
    /// 把「相对 <paramref name="baseline"/> 变过的字段」写进 <paramref name="target"/>。
    /// <paramref name="baseline"/> 是开始编辑那一刻的快照，<c>this</c> 是编辑后的显示样式。
    /// </summary>
    /// <remarks>
    /// <see cref="SerialNumber"/> 刻意不搬：它是「下一枚序号」的计数器，选中一条旧序号时
    /// 参数行照旧显示下一个要落的号（上游同样把 number 位排除在创建样式合并之外）。
    /// </remarks>
    public void CopyChangedTo(ToolStyle target, ToolStyle baseline)
    {
        if (Color != baseline.Color) target.Color = Color;
        if (Width != baseline.Width) target.Width = Width;
        if (FontSize != baseline.FontSize) target.FontSize = FontSize;
        if (Block != baseline.Block) target.Block = Block;
        if (Opacity != baseline.Opacity) target.Opacity = Opacity;
        if (Badge != baseline.Badge) target.Badge = Badge;
        if (ShapeKind != baseline.ShapeKind) target.ShapeKind = ShapeKind;
        if (StrokeStyle != baseline.StrokeStyle) target.StrokeStyle = StrokeStyle;
        if (Rendering != baseline.Rendering) target.Rendering = Rendering;
        if (FillStyle != baseline.FillStyle) target.FillStyle = FillStyle;
        if (FillColor != baseline.FillColor) target.FillColor = FillColor;
        if (CornerRadius != baseline.CornerRadius) target.CornerRadius = CornerRadius;
        if (LineType != baseline.LineType) target.LineType = LineType;
        if (BrushMode != baseline.BrushMode) target.BrushMode = BrushMode;
        if (Numbering != baseline.Numbering) target.Numbering = Numbering;
        if (FilterMode != baseline.FilterMode) target.FilterMode = FilterMode;
        if (FilterStrength != baseline.FilterStrength) target.FilterStrength = FilterStrength;
        if (Factor != baseline.Factor) target.Factor = Factor;
        if (Connection != baseline.Connection) target.Connection = Connection;
        if (SourceOutline != baseline.SourceOutline) target.SourceOutline = SourceOutline;
        if (ArrowRatio != baseline.ArrowRatio) target.ArrowRatio = ArrowRatio;
        if (ArrowKind != baseline.ArrowKind) target.ArrowKind = ArrowKind;
        if (StartArrowhead != baseline.StartArrowhead) target.StartArrowhead = StartArrowhead;
        if (EndArrowhead != baseline.EndArrowhead) target.EndArrowhead = EndArrowhead;
        if (ArrowShaftType != baseline.ArrowShaftType) target.ArrowShaftType = ArrowShaftType;
        if (ArrowOutlineColor != baseline.ArrowOutlineColor) target.ArrowOutlineColor = ArrowOutlineColor;
        if (ArrowOutlineWidth != baseline.ArrowOutlineWidth) target.ArrowOutlineWidth = ArrowOutlineWidth;
        if (WatermarkText != baseline.WatermarkText) target.WatermarkText = WatermarkText;
        if (WatermarkAngle != baseline.WatermarkAngle) target.WatermarkAngle = WatermarkAngle;
        if (WatermarkGap != baseline.WatermarkGap) target.WatermarkGap = WatermarkGap;
        if (WatermarkOpacity != baseline.WatermarkOpacity) target.WatermarkOpacity = WatermarkOpacity;
        if (TextSize != baseline.TextSize) target.TextSize = TextSize;
        if (FontFamily != baseline.FontFamily) target.FontFamily = FontFamily;
        if (TextBold != baseline.TextBold) target.TextBold = TextBold;
        if (TextItalic != baseline.TextItalic) target.TextItalic = TextItalic;
        if (TextAlign != baseline.TextAlign) target.TextAlign = TextAlign;
        if (TextStrokeWidth != baseline.TextStrokeWidth) target.TextStrokeWidth = TextStrokeWidth;
        if (StrokeColor != baseline.StrokeColor) target.StrokeColor = StrokeColor;
        if (DistanceUnit != baseline.DistanceUnit) target.DistanceUnit = DistanceUnit;
        if (DistanceFactor != baseline.DistanceFactor) target.DistanceFactor = DistanceFactor;
        if (DistanceDecimals != baseline.DistanceDecimals) target.DistanceDecimals = DistanceDecimals;
        if (EndpointScale != baseline.EndpointScale) target.EndpointScale = EndpointScale;
        if (EndpointStyle != baseline.EndpointStyle) target.EndpointStyle = EndpointStyle;
        if (AngleKind != baseline.AngleKind) target.AngleKind = AngleKind;
        if (AngleUnit != baseline.AngleUnit) target.AngleUnit = AngleUnit;
        if (AngleDecimals != baseline.AngleDecimals) target.AngleDecimals = AngleDecimals;
    }

    /// <summary>这份样式里有没有跟 <paramref name="baseline"/> 不一样的字段（合并完判断要不要重绘）。</summary>
    public bool DiffersFrom(ToolStyle baseline) => !SameAs(baseline);

    private bool SameAs(ToolStyle other) =>
        Color == other.Color && Width == other.Width && FontSize == other.FontSize &&
        Block == other.Block && Opacity == other.Opacity && Badge == other.Badge &&
        ShapeKind == other.ShapeKind && StrokeStyle == other.StrokeStyle &&
        Rendering == other.Rendering && FillStyle == other.FillStyle &&
        FillColor == other.FillColor && CornerRadius == other.CornerRadius &&
        LineType == other.LineType && BrushMode == other.BrushMode &&
        Numbering == other.Numbering && FilterMode == other.FilterMode &&
        FilterStrength == other.FilterStrength && Factor == other.Factor &&
        Connection == other.Connection && SourceOutline == other.SourceOutline &&
        ArrowRatio == other.ArrowRatio && ArrowKind == other.ArrowKind &&
        StartArrowhead == other.StartArrowhead && EndArrowhead == other.EndArrowhead &&
        ArrowShaftType == other.ArrowShaftType && ArrowOutlineColor == other.ArrowOutlineColor &&
        ArrowOutlineWidth == other.ArrowOutlineWidth && WatermarkText == other.WatermarkText &&
        WatermarkAngle == other.WatermarkAngle && WatermarkGap == other.WatermarkGap &&
        WatermarkOpacity == other.WatermarkOpacity && TextSize == other.TextSize &&
        FontFamily == other.FontFamily && TextBold == other.TextBold &&
        TextItalic == other.TextItalic && TextAlign == other.TextAlign &&
        TextStrokeWidth == other.TextStrokeWidth && StrokeColor == other.StrokeColor &&
        DistanceUnit == other.DistanceUnit && DistanceFactor == other.DistanceFactor &&
        DistanceDecimals == other.DistanceDecimals && EndpointScale == other.EndpointScale &&
        EndpointStyle == other.EndpointStyle && AngleKind == other.AngleKind &&
        AngleUnit == other.AngleUnit && AngleDecimals == other.AngleDecimals;
}
