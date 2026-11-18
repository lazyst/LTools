namespace LTools.Features.CapturePro;

/// <summary>
/// 滤镜的类型（顺序照上游 <c>CanvasFilterType</c>）。
/// 马赛克既是一个单独的工具（<see cref="Mark.Mosaic"/>，块大小由用户调），
/// 也是这里的滤镜类型（强度换算块边长）——自动打码「填充区域」默认用它。
/// </summary>
internal enum FilterKind
{
    Mosaic,
    Blur,
    Grayscale,
    Invert,
    Emboss,
    Brighten,

    /// <summary>
    /// 智能擦除：把这块当洞，从周围完好的像素重建（对应上游 <c>FilterType.SmartErase</c>）。
    /// 算法在 <see cref="SmartErase"/>。
    /// </summary>
    SmartErase,
}

/// <summary>
/// 形状族的参数文案：**只做枚举 → 中文**，参数行胶囊 / tooltip / 自检共用一份
/// （别在工具栏里另写一套 switch）。
/// </summary>
internal static class ShapeLabel
{
    /// <summary>参数行胶囊上的短标签（三个字以内，别把参数条撑开）。</summary>
    public static string Chip(ShapeKind kind) => kind switch
    {
        ShapeKind.Ellipse => "椭圆",
        ShapeKind.Diamond => "菱形",
        ShapeKind.ThoughtBubble => "思考",
        _ => "矩形",
    };

    public static string Name(ShapeKind kind) => "形状：" + Chip(kind);

    public static string Chip(StrokeKind kind) => kind switch
    {
        StrokeKind.Dashed => "虚线",
        StrokeKind.Dotted => "点线",
        _ => "实线",
    };

    public static string Name(StrokeKind kind) => "描边：" + Chip(kind);

    public static string Chip(StrokeRendering style) => style switch
    {
        StrokeRendering.Artist => "手绘",
        StrokeRendering.Cartoonist => "涂鸦",
        _ => "干净",
    };

    public static string Name(StrokeRendering style) => "笔触：" + Chip(style);

    public static string Chip(FillKind kind) => kind switch
    {
        FillKind.Solid => "实心",
        FillKind.Line => "斜线",
        FillKind.CrossLine => "交叉",
        _ => "无",
    };

    public static string Name(FillKind kind) => "填充：" + Chip(kind);

    public static string Chip(LineType type) => type == LineType.Curve ? "曲线" : "直线";

    public static string Name(LineType type) => "线型：" + Chip(type);

    public static string Chip(BrushMode mode) => mode == BrushMode.PressureSensitive ? "压感" : "常规";

    public static string Name(BrushMode mode) => "画笔：" + Chip(mode);

    public static string Chip(TextAlign align) => align switch
    {
        TextAlign.Center => "中",
        TextAlign.Right => "右",
        _ => "左",
    };

    public static string Name(TextAlign align) => "对齐：" + Chip(align);
}
