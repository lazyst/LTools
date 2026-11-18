namespace LTools.Features.CapturePro;

/// <summary>
/// 「选中的这条标注」与「它那一族的创建样式」之间的翻译。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>style.rs</c> 的 <c>style_toolbar_source</c> / <c>set_shape_style_patch</c>：
/// 选中一条标注时参数行切到**它自己的样式族**，显示的是**这一条的值**（<c>SelectedArrow</c> /
/// <c>SelectedRectangle</c> 这些来源）；改参数先写回这一条，再把变过的字段合并进该族的创建样式。
/// </para>
/// <para>
/// 这里只做「值 ↔ 标注」的搬运，**不碰文档**：写回由覆盖层调 <c>CaptureDocument.ReplaceAt</c>，
/// 合并进创建样式由 <see cref="ToolStyle.CopyChangedTo"/> 负责。
/// </para>
/// <para>
/// 几何一律保留（点列 / 圆心 / 顶点 / 源区域 / 抖动种子），只换样式——所以「选一条旧箭头改颜色」
/// 不会把它挪位置，也不会让手绘抖动的形状换个花纹。
/// </para>
/// </remarks>
internal static class MarkStyler
{
    /// <summary>
    /// 这条标注属于哪个样式族、参数行按哪个工具排。
    /// 返回 false 表示它不参与样式编辑（水印是整张图的效果、恢复块不可选，上游同样不归任何族）。
    /// </summary>
    public static bool TryFamily(Mark mark, out string family, out CaptureTool tool)
    {
        switch (mark)
        {
            case Mark.Stroke stroke:
                family = stroke.Highlighter ? StyleFamilies.Highlighter : StyleFamilies.Pen;
                tool = stroke.Highlighter ? CaptureTool.Highlighter : CaptureTool.Pen;
                return true;

            case Mark.Shape:
                family = StyleFamilies.Shape;
                tool = CaptureTool.Shape;
                return true;

            case Mark.SpeechBubble:
                family = StyleFamilies.SpeechBubble;
                tool = CaptureTool.SpeechBubble;
                return true;

            case Mark.Arrow:
                family = StyleFamilies.Arrow;
                tool = CaptureTool.Arrow;
                return true;

            case Mark.Line:
                family = StyleFamilies.Line;
                tool = CaptureTool.Line;
                return true;

            case Mark.Polyline:
                family = StyleFamilies.Polyline;
                tool = CaptureTool.Polyline;
                return true;

            case Mark.Text:
                family = StyleFamilies.Text;
                tool = CaptureTool.Text;
                return true;

            case Mark.Serial:
                family = StyleFamilies.Serial;
                tool = CaptureTool.Serial;
                return true;

            case Mark.Mosaic:
                family = StyleFamilies.Mosaic;
                tool = CaptureTool.Mosaic;
                return true;

            case Mark.Filter:
                family = StyleFamilies.Filter;
                tool = CaptureTool.Filter;
                return true;

            case Mark.BrushFilter:
                family = StyleFamilies.BrushFilter;
                tool = CaptureTool.BrushFilter;
                return true;

            case Mark.Spotlight:
                family = StyleFamilies.Spotlight;
                tool = CaptureTool.Spotlight;
                return true;

            case Mark.Highlight:
                family = StyleFamilies.HighlightRect;
                tool = CaptureTool.HighlightRect;
                return true;

            case Mark.Distance:
                family = StyleFamilies.Distance;
                tool = CaptureTool.Distance;
                return true;

            case Mark.Angle:
                family = StyleFamilies.Angle;
                tool = CaptureTool.Angle;
                return true;

            case Mark.Bracket:
                family = StyleFamilies.Bracket;
                tool = CaptureTool.Bracket;
                return true;

            case Mark.Magnifier:
                family = StyleFamilies.Magnifier;
                tool = CaptureTool.Magnifier;
                return true;

            default:
                family = string.Empty;
                tool = CaptureTool.Select;
                return false;
        }
    }

    /// <summary>
    /// 多选时参数行用哪一族：照上游 <c>style_toolbar_source</c> 的顺序在**整个选区**里找
    /// （气泡 → 画笔滤镜 → 矩形滤镜 → 马赛克 → 文字 → 序号 → 聚光 → 形状 → 矩形荧光 →
    /// 荧光笔 → 直线 → 折线 → 画笔 → 测距 → 括号 → 测角 → 箭头 → 放大镜）。
    /// </summary>
    /// <remarks>
    /// 上游一律「先看主选中项，再看选区里第一个属于这一族的」：所以点选第二条同类元素不会把
    /// 参数行换成别人的值。<paramref name="representative"/> 就是参数行显示值的那一条。
    /// </remarks>
    public static bool TrySelectionFamily(IReadOnlyList<Mark> marks, IReadOnlyList<int> indices, int primary,
        out string family, out CaptureTool tool, out int representative)
    {
        foreach (string candidate in Priority)
        {
            int found = -1;
            CaptureTool foundTool = CaptureTool.Select;

            foreach (int index in indices)
            {
                if (index < 0 || index >= marks.Count) continue;
                if (!TryFamily(marks[index], out string itemFamily, out CaptureTool itemTool)) continue;
                if (!string.Equals(itemFamily, candidate, StringComparison.Ordinal)) continue;

                if (index == primary)
                {
                    found = index;
                    foundTool = itemTool;
                    break;
                }

                if (found < 0)
                {
                    found = index;
                    foundTool = itemTool;
                }
            }

            if (found >= 0)
            {
                family = candidate;
                tool = foundTool;
                representative = found;
                return true;
            }
        }

        family = string.Empty;
        tool = CaptureTool.Select;
        representative = -1;
        return false;
    }

    /// <summary>这一条是不是属于这个样式族（多选时「只改同一族的那些」用）。</summary>
    public static bool InFamily(Mark mark, string family) =>
        TryFamily(mark, out string itemFamily, out _) &&
        string.Equals(itemFamily, family, StringComparison.Ordinal);

    /// <summary>选区族优先级（与 <see cref="TrySelectionFamily"/> 的顺序逐字一致）。</summary>
    private static readonly string[] Priority =
    {
        StyleFamilies.SpeechBubble,
        StyleFamilies.BrushFilter,
        StyleFamilies.Filter,
        StyleFamilies.Mosaic,
        StyleFamilies.Text,
        StyleFamilies.Serial,
        StyleFamilies.Spotlight,
        StyleFamilies.Shape,
        StyleFamilies.HighlightRect,
        StyleFamilies.Highlighter,
        StyleFamilies.Line,
        StyleFamilies.Polyline,
        StyleFamilies.Pen,
        StyleFamilies.Distance,
        StyleFamilies.Bracket,
        StyleFamilies.Angle,
        StyleFamilies.Arrow,
        StyleFamilies.Magnifier,
    };

    /// <summary>
    /// 把这条标注的值抄进显示样式（参数行显示它自己的值）。
    /// 只抄这一族参数行里摆得出来的字段，别的字段保持创建样式的值
    /// （序号计数器 <see cref="ToolStyle.SerialNumber"/> 就是故意的：选一条旧序号时仍显示下一个号）。
    /// </summary>
    public static void Capture(Mark mark, ToolStyle style)
    {
        switch (mark)
        {
            case Mark.Stroke s:
                style.Color = s.Color;
                style.Width = s.Width;
                if (s.Highlighter) style.Opacity = s.Opacity;
                break;

            case Mark.Shape shape:
                CaptureShape(shape.Spec, style);
                break;

            case Mark.SpeechBubble bubble:
                CaptureShape(bubble.Spec, style);
                break;

            case Mark.Arrow arrow:
                style.Color = arrow.Spec.Stroke;
                style.Width = arrow.Spec.Width;
                style.StrokeStyle = arrow.Spec.StrokeStyle;
                style.Rendering = arrow.Spec.Rendering;
                style.ArrowKind = arrow.Spec.Type;
                style.StartArrowhead = arrow.Spec.Start;
                style.EndArrowhead = arrow.Spec.End;
                style.ArrowRatio = arrow.Spec.Ratio;
                style.ArrowShaftType = arrow.Spec.Shaft;
                style.ArrowOutlineColor = arrow.Spec.OutlineColor;
                style.ArrowOutlineWidth = arrow.Spec.OutlineWidth;
                break;

            case Mark.Line line:
                style.Color = line.Color;
                style.Width = line.Width;
                style.StrokeStyle = line.StrokeStyle;
                style.Rendering = line.Rendering;
                style.LineType = line.Type;
                break;

            case Mark.Polyline polyline:
                style.Color = polyline.Color;
                style.Width = polyline.Width;
                break;

            case Mark.Text text:
                style.Color = text.Style.Color;
                style.TextSize = text.Style.FontSize;
                style.FontFamily = text.Style.FontFamily;
                style.TextBold = text.Style.Bold;
                style.TextItalic = text.Style.Italic;
                style.TextAlign = text.Style.Align;
                style.TextStrokeWidth = text.Style.StrokeWidth;
                style.StrokeColor = text.Style.Stroke;
                style.FillColor = text.Style.Fill;
                style.FillStyle = text.Style.FillStyle;
                style.CornerRadius = text.Style.CornerRadius;
                break;

            case Mark.Serial serial:
                style.Badge = serial.Spec.Badge;
                style.Numbering = serial.Spec.Numbering;
                style.Color = serial.Spec.Stroke;
                style.FillColor = serial.Spec.Fill;
                style.FillStyle = serial.Spec.FillStyle;
                style.FontSize = serial.Spec.FontSize;
                style.FontFamily = serial.Spec.FontFamily;
                style.Width = serial.Spec.StrokeWidth;
                style.Opacity = serial.Spec.Opacity;
                break;

            case Mark.Mosaic mosaic:
                style.Block = mosaic.Block;
                break;

            case Mark.Filter filter:
                style.FilterMode = filter.Kind;
                style.FilterStrength = filter.Strength;
                break;

            case Mark.BrushFilter brush:
                style.Width = brush.Width;
                style.FilterMode = brush.Kind;
                style.FilterStrength = brush.Strength;
                break;

            case Mark.Spotlight spotlight:
                style.Opacity = Math.Clamp(1 - spotlight.Dim, 0.1, 0.9);
                break;

            case Mark.Highlight highlight:
                style.Color = highlight.Color;
                style.Opacity = Math.Clamp(highlight.Opacity, 0.1, 0.9);
                break;

            case Mark.Distance distance:
                style.Color = distance.Color;
                style.Width = distance.Width;
                style.DistanceUnit = distance.Spec.Unit;
                style.DistanceFactor = distance.Spec.Factor;
                style.DistanceDecimals = distance.Spec.Decimals;
                style.EndpointScale = distance.Spec.EndpointScale;
                style.EndpointStyle = distance.Spec.Endpoint;
                break;

            case Mark.Angle angle:
                style.Color = angle.Color;
                style.Width = angle.Width;
                style.AngleKind = angle.Spec.Kind;
                style.AngleUnit = angle.Spec.Unit;
                style.AngleDecimals = angle.Spec.Decimals;
                break;

            case Mark.Bracket bracket:
                style.Color = bracket.Color;
                style.Width = bracket.Width;
                break;

            case Mark.Magnifier magnifier:
                style.ShapeKind = magnifier.Spec.Shape;
                style.Color = magnifier.Spec.Stroke;
                style.Width = magnifier.Spec.StrokeWidth;
                style.Factor = magnifier.Spec.Factor;
                style.Connection = magnifier.Spec.Connection;
                style.SourceOutline = magnifier.Spec.SourceOutline;
                style.EndpointStyle = magnifier.Spec.LeaderArrowhead;
                style.ArrowRatio = magnifier.Spec.ArrowRatio;
                style.CornerRadius = magnifier.Spec.CornerRadius;
                style.Opacity = magnifier.Spec.Opacity;
                break;
        }
    }

    /// <summary>
    /// 按显示样式重造这条标注：几何与抖动种子保持，只换样式字段。
    /// 不认识 / 不参与样式的类型原样返回（水印、恢复块）。
    /// </summary>
    public static Mark Apply(Mark mark, ToolStyle style)
    {
        switch (mark)
        {
            case Mark.Stroke s:
                return new Mark.Stroke(s.Points, style.Color,
                    s.Highlighter ? style.WidthFor(CaptureTool.Highlighter) : style.Width,
                    s.Highlighter, s.Highlighter ? style.Opacity : s.Opacity);

            case Mark.Shape shape:
                return new Mark.Shape(shape.A, shape.B, style.ShapeSpecOf(shape.Spec.Seed));

            case Mark.SpeechBubble bubble:
                return new Mark.SpeechBubble(bubble.Body, style.ShapeSpecOf(bubble.Spec.Seed));

            case Mark.Arrow arrow:
                // ArrowSpecOf 没有不透明度参数（箭头族不上透明度），保留这条自己的
                return new Mark.Arrow(arrow.Points,
                    style.ArrowSpecOf(arrow.Spec.Seed) with { Opacity = arrow.Spec.Opacity });

            case Mark.Line line:
                return new Mark.Line(line.A, line.B, style.Color, style.Width, style.StrokeStyle,
                    style.LineType, style.Rendering, line.Seed);

            case Mark.Polyline polyline:
                return new Mark.Polyline(polyline.Points, style.Color, style.Width, polyline.Dashed);

            case Mark.Text text:
                return new Mark.Text(text.Origin, text.Content, style.TextSpecOf());

            case Mark.Serial serial:
                return new Mark.Serial(serial.Center, serial.Number, style.SerialSpecOf());

            case Mark.Mosaic mosaic:
                return new Mark.Mosaic(mosaic.A, mosaic.B, style.Block);

            case Mark.Filter filter:
                return new Mark.Filter(filter.A, filter.B, style.FilterMode, style.FilterStrength,
                    filter.AutoRegionId);

            case Mark.BrushFilter brush:
                return new Mark.BrushFilter(brush.Points, style.Width, style.FilterMode, style.FilterStrength);

            case Mark.Spotlight spotlight:
                return new Mark.Spotlight(spotlight.A, spotlight.B, style.Dim);

            case Mark.Highlight highlight:
                return new Mark.Highlight(highlight.A, highlight.B, style.Color, style.Opacity);

            case Mark.Distance distance:
                return new Mark.Distance(distance.A, distance.B, style.Color, style.Width,
                    style.DistanceSpecOf());

            case Mark.Angle angle:
                return new Mark.Angle(angle.Vertex, angle.Radius, angle.Bisector, angle.Degrees,
                    style.Color, style.Width, style.AngleSpecOf());

            case Mark.Bracket bracket:
                return new Mark.Bracket(bracket.A, bracket.Apex, bracket.B, style.Color, style.Width);

            case Mark.Magnifier magnifier:
                return magnifier.WithSpec(style.MagnifierSpecOf());

            default:
                return mark;
        }
    }

    private static void CaptureShape(ShapeSpec spec, ToolStyle style)
    {
        style.Color = spec.Stroke;
        style.Width = spec.Width;
        style.ShapeKind = spec.Kind;
        style.StrokeStyle = spec.StrokeStyle;
        style.Rendering = spec.Rendering;
        style.FillColor = spec.Fill;
        style.FillStyle = spec.FillStyle;
        style.CornerRadius = spec.Radius;
    }
}
