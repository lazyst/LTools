using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Config;

namespace LTools.Features.CapturePro;

/// <summary>标注工具。工具只决定「按下鼠标会发生什么」，不持有文档状态。</summary>
internal enum CaptureTool
{
    Select,
    Pen,
    Highlighter,
    HighlightRect,
    Shape,
    Arrow,
    Line,
    Polyline,
    Distance,
    Angle,
    Bracket,
    SpeechBubble,
    Text,
    Serial,
    Magnifier,
    Watermark,
    Mosaic,
    Filter,
    BrushFilter,
    AutoFilter,
    Spotlight,
    Eraser,
    RectEraser,
    BrushEraser,
    Picker,
}

/// <summary>工具参数条里的一格。</summary>
internal enum ToolParam
{
    Color,
    Width,
    FontSize,
    Block,
    Opacity,

    /// <summary>序号的徽标形状。</summary>
    BadgeShape,

    /// <summary>形状族的形状（矩形 / 椭圆 / 菱形 / 思考气泡）。</summary>
    ShapeKind,

    /// <summary>描边线型（实线 / 虚线 / 点线）。</summary>
    StrokeStyle,

    /// <summary>笔触风格（干净 / 手绘 / 涂鸦）。</summary>
    Rendering,

    /// <summary>填充方式（无 / 实心 / 斜线 / 交叉斜线）。</summary>
    FillStyle,

    /// <summary>填充色（与描边色分开）。</summary>
    FillColor,

    /// <summary>圆角半径。</summary>
    CornerRadius,

    /// <summary>直线 / 曲线。</summary>
    LineType,

    /// <summary>画笔模式（常规 / 压感）。</summary>
    BrushMode,

    /// <summary>序号的数字写法。</summary>
    Numbering,

    /// <summary>下一个序号（上游参数行的序号输入框，滚轮微调）。</summary>
    SerialNumber,

    /// <summary>序号的字号（与水印 / 文字的字号分开：它的默认是 24）。</summary>
    SerialFontSize,

    /// <summary>滤镜的类型。</summary>
    FilterKind,

    /// <summary>滤镜的强度。</summary>
    FilterStrength,

    /// <summary>自动打码的滤镜类型（不含智能擦除）。</summary>
    AutoFilterKind,

    /// <summary>自动打码的滤镜强度。</summary>
    AutoFilterStrength,

    /// <summary>自动打码的「填充区域」下拉（按识别到的类型一次填满）。</summary>
    FillRegions,

    /// <summary>放大镜的倍率。</summary>
    Factor,

    /// <summary>放大镜与源区域的连接方式（无 / 引导线 / 渐变）。</summary>
    Connection,

    /// <summary>放大镜的源区域轮廓开关。</summary>
    SourceOutline,

    /// <summary>箭头比例（引导线 / 箭头族的箭头缩放，0.5–3）。</summary>
    ArrowRatio,

    /// <summary>引导线端点样式（14 种，与测距端点同一套 <see cref="Arrowhead"/>）。</summary>
    LeaderArrowhead,

    /// <summary>水印文字。</summary>
    WatermarkText,

    /// <summary>水印模板（可编辑下拉 + 增删，存 <c>ui.json</c>）。</summary>
    WatermarkTemplate,

    /// <summary>水印倾斜角度。</summary>
    WatermarkAngle,

    /// <summary>水印间距。</summary>
    WatermarkGap,

    /// <summary>水印不透明度（与荧光笔的透明度分开）。</summary>
    WatermarkOpacity,

    /// <summary>文字字号（与水印的「字号」分开：范围 6–256，上游 text 族）。</summary>
    TextSize,

    /// <summary>字体族（可搜索的字体选择）。</summary>
    FontFamily,

    /// <summary>粗体 / 斜体。</summary>
    TextEmphasis,

    /// <summary>文字水平对齐（左 / 中 / 右）。</summary>
    TextAlign,

    /// <summary>文字描边宽度。</summary>
    TextStrokeWidth,

    /// <summary>文字描边色。</summary>
    TextStrokeColor,

    // ============ 测距族（上游 buildDistanceFamily） ============

    /// <summary>测距实测值（只对选中的一条测距生效，写回系数）。</summary>
    DistanceValue,

    /// <summary>测距单位。</summary>
    DistanceUnit,

    /// <summary>测距小数位。</summary>
    DistanceDecimals,

    /// <summary>端点比例（0.5–3.0）。</summary>
    EndpointScale,

    /// <summary>端点样式（14 种）。</summary>
    EndpointStyle,

    // ============ 测角族（上游 buildAngleFamily） ============

    /// <summary>角度的类型（有向 360° / 内角 180°）。</summary>
    AngleKind,

    /// <summary>角度单位（度 / 弧度）。</summary>
    AngleUnit,

    /// <summary>角度小数位。</summary>
    AngleDecimals,

    /// <summary>橡皮的三种模式（元素 / 矩形 / 画笔）。</summary>
    EraserMode,

    // ============ 箭头族（上游 buildArrowFamily） ============

    /// <summary>箭头类型（直线 / 曲线 / 折线）。</summary>
    ArrowKind,

    /// <summary>箭头外描边宽度（0 = 不描）。</summary>
    ArrowOutlineWidth,

    /// <summary>箭头外描边色。</summary>
    ArrowOutlineColor,

    /// <summary>起点端点样式。</summary>
    StartArrowhead,

    /// <summary>杆型（等宽 / 渐宽）。</summary>
    ArrowShaftType,

    /// <summary>终点端点样式。</summary>
    EndArrowhead,
}

/// <summary>一个工具的名字 / 图标 / 说明 / 用到哪几格参数 / 快捷键（工具栏与自检共用）。</summary>
internal sealed record ToolInfo(CaptureTool Tool, string Name, string Icon, string Hint, ToolParam[] Params,
    Key Shortcut);

/// <summary>
/// 工具目录：工具栏按它生成按钮，参数条按它决定显示哪几格。
/// **新增工具只改这一张表 + 一个绘制分支**，别在 UI 里另写一份清单。
/// </summary>
internal static class ToolCatalog
{
    private static readonly ToolParam[] None = Array.Empty<ToolParam>();
    private static readonly ToolParam[] Stroke = { ToolParam.Color, ToolParam.Width };

    /// <summary>
    /// 橡皮族（上游 <c>buildEraserFamily</c>）：三种模式用一个选择器切换，
    /// 画笔橡皮多一格宽度。
    /// </summary>
    private static readonly ToolParam[] EraserFamily = { ToolParam.EraserMode };

    private static readonly ToolParam[] BrushEraserFamily =
    {
        ToolParam.EraserMode, ToolParam.Width,
    };

    /// <summary>
    /// 测距族（上游 <c>buildDistanceFamily</c>）：描边（色 / 线宽）→ 实测值 → 单位 → 小数位
    /// → 端点比例 → 端点样式。
    /// </summary>
    private static readonly ToolParam[] DistanceFamily =
    {
        ToolParam.Color, ToolParam.Width, ToolParam.DistanceValue, ToolParam.DistanceUnit,
        ToolParam.DistanceDecimals, ToolParam.EndpointScale, ToolParam.EndpointStyle,
    };

    /// <summary>测角族（上游 <c>buildAngleFamily</c>）：描边（色 / 线宽）→ 类型 → 单位 → 小数位。</summary>
    private static readonly ToolParam[] AngleFamily =
    {
        ToolParam.Color, ToolParam.Width, ToolParam.AngleKind, ToolParam.AngleUnit,
        ToolParam.AngleDecimals,
    };

    /// <summary>
    /// 形状族（上游 <c>buildShapeFamily</c>）：形状选择 + 描边（色 / 线宽 / 线型 / 笔触风格）
    /// + 填充（色 / 方式）+ 圆角。气泡共用同一套，只是形状里多一个「思考气泡」。
    /// </summary>
    private static readonly ToolParam[] ShapeFamily =
    {
        ToolParam.Color, ToolParam.Width, ToolParam.ShapeKind, ToolParam.StrokeStyle,
        ToolParam.Rendering, ToolParam.FillColor, ToolParam.FillStyle, ToolParam.CornerRadius,
    };

    /// <summary>
    /// 箭头族（上游 <c>buildArrowFamily</c>）：描边（色 / 线宽 / 线型 / 笔触风格）→ 类型（3）→
    /// 外描边（宽 + 色）→ 箭头比例 → 起点端点 → 杆型 → 终点端点。
    /// </summary>
    private static readonly ToolParam[] ArrowFamily =
    {
        ToolParam.Color, ToolParam.Width, ToolParam.StrokeStyle, ToolParam.Rendering,
        ToolParam.ArrowKind, ToolParam.ArrowOutlineWidth, ToolParam.ArrowOutlineColor,
        ToolParam.ArrowRatio, ToolParam.StartArrowhead, ToolParam.ArrowShaftType,
        ToolParam.EndArrowhead,
    };

    /// <summary>直线：箭头那套再加「直线 / 曲线」。</summary>
    private static readonly ToolParam[] LineStroke =
    {
        ToolParam.Color, ToolParam.Width, ToolParam.StrokeStyle, ToolParam.Rendering, ToolParam.LineType,
    };

    /// <summary>
    /// 文字族（上游 <c>buildTextFamily</c>）：字色 → 字号 + 字体族 → 粗 / 斜 → 水平对齐 →
    /// 描边宽度 + 描边色 → 背景填充色 + 填充方式 → 背景圆角。
    /// </summary>
    private static readonly ToolParam[] TextFamily =
    {
        ToolParam.Color, ToolParam.TextSize, ToolParam.FontFamily, ToolParam.TextEmphasis,
        ToolParam.TextAlign, ToolParam.TextStrokeWidth, ToolParam.TextStrokeColor,
        ToolParam.FillColor, ToolParam.FillStyle, ToolParam.CornerRadius,
    };

    /// <summary>
    /// 序号族（上游 <c>buildSequenceNumberFamily</c>）：色 → 徽标类型（5）→ 数字写法（5）→
    /// 下一个序号 → 字号 + 字体族 → 线宽 → 填充色 + 填充方式。
    /// </summary>
    private static readonly ToolParam[] SerialFamily =
    {
        ToolParam.Color, ToolParam.BadgeShape, ToolParam.Numbering, ToolParam.SerialNumber,
        ToolParam.SerialFontSize, ToolParam.FontFamily, ToolParam.Width, ToolParam.FillColor,
        ToolParam.FillStyle,
    };

    /// <summary>
    /// 放大镜族（上游 <c>buildMagnifierFamily</c>）：形状（3，没有思考气泡）→ 色 → 线宽 →
    /// 倍率 → 连接方式 → 源轮廓 → 箭头比例 → 引导线端点 → 圆角。
    /// </summary>
    private static readonly ToolParam[] MagnifierFamily =
    {
        ToolParam.ShapeKind, ToolParam.Color, ToolParam.Width, ToolParam.Factor, ToolParam.Connection,
        ToolParam.SourceOutline, ToolParam.ArrowRatio, ToolParam.LeaderArrowhead,
        ToolParam.CornerRadius,
    };

    /// <summary>
    /// 水印族（上游 <c>buildWatermarkFamily</c>）：色 → 文字 → 字号 + 字体族 → 模板 →
    /// 角度 → 间距 → 不透明度。
    /// </summary>
    private static readonly ToolParam[] WatermarkFamily =
    {
        ToolParam.Color, ToolParam.WatermarkText, ToolParam.FontSize, ToolParam.FontFamily,
        ToolParam.WatermarkTemplate, ToolParam.WatermarkAngle, ToolParam.WatermarkGap,
        ToolParam.WatermarkOpacity,
    };

    public static readonly ToolInfo[] All =
    {
        new(CaptureTool.Select, "选择", "Select", "点选标注后可拖动移动，Delete 删除", None, Key.Q),
        new(CaptureTool.Shape, "形状", "Rect", "拖出形状（矩形 / 椭圆 / 菱形，气泡工具里还能选思考气泡），填充与圆角在参数行选",
            ShapeFamily, Key.R),
        new(CaptureTool.Arrow, "箭头", "Arrow", "从起点拖到目标（类型 / 起止端点 / 杆型 / 外描边在参数行选）",
            ArrowFamily, Key.A),
        new(CaptureTool.Line, "直线", "Line", "从起点拖到终点，不带箭头", LineStroke, Key.I),
        new(CaptureTool.Polyline, "折线", "Polyline", "逐点单击，双击 / Enter 结束", Stroke, Key.L),
        new(CaptureTool.Distance, "测距", "Distance", "拖出线段，中点显示长度（单位 / 小数位 / 端点样式在参数行选；选中一条后还能直接填实测值）",
            DistanceFamily, Key.D),
        new(CaptureTool.Angle, "测角", "Angle", "先拖一条臂，再拖第二条臂；选中后滚轮改角度",
            AngleFamily, Key.G),
        new(CaptureTool.Bracket, "括号", "Bracket", "拖出花括号，开口朝拖动的方向", Stroke, Key.B),
        new(CaptureTool.SpeechBubble, "气泡", "SpeechBubble", "拖出气泡框，左下角带指向外的尾巴", ShapeFamily, Key.O),
        new(CaptureTool.Pen, "画笔", "Pen", "按住拖动自由绘制", new[] { ToolParam.Color, ToolParam.Width, ToolParam.BrushMode }, Key.P),
        new(CaptureTool.Picker, "取色", "Picker", "鼠标下的颜色，单击复制 #RRGGBB", None, Key.C),
        new(CaptureTool.Spotlight, "聚光", "Spotlight", "拖出保持原样的矩形，其余压暗", new[] { ToolParam.Opacity }, Key.S),
        new(CaptureTool.Highlighter, "画笔荧光", "Highlighter", "半透明粗线，给文字打底", new[] { ToolParam.Color, ToolParam.Width, ToolParam.Opacity }, Key.H),
        new(CaptureTool.HighlightRect, "矩形荧光", "HighlightRect", "把一块区域铺上半透明色", new[] { ToolParam.Color, ToolParam.Opacity }, Key.J),
        new(CaptureTool.Text, "文字", "Text", "单击输入文字，Enter 结束（Shift+Enter 换行）；字色 / 字号 / 字体 / 粗斜 / 对齐 / 描边 / 背景在参数行选",
            TextFamily, Key.T),
        new(CaptureTool.Serial, "序号", "Serial", "单击添加序号，自动递增（删除不重排）；徽标 / 写法 / 下一个序号 / 字号 / 填充在参数行选",
            SerialFamily, Key.N),
        new(CaptureTool.Magnifier, "放大镜", "Magnifier", "拖出镜头框，框内的画面按倍率放大；源区域可以单独拖开，连接方式在参数行选",
            MagnifierFamily, Key.Z),
        new(CaptureTool.Watermark, "水印", "Watermark", "整张图斜向平铺水印文字，单击落到图上；模板 / 角度 / 间距 / 不透明度在参数行选",
            WatermarkFamily, Key.W),
        new(CaptureTool.Mosaic, "马赛克", "Mosaic", "拖出要打码的矩形", new[] { ToolParam.Block }, Key.M),
        new(CaptureTool.Filter, "矩形滤镜", "Filter", "拖出矩形，按参数行选的类型处理这一块（模糊 / 灰度 / 反相 / 浮雕 / 亮度 / 智能擦除）",
            new[] { ToolParam.FilterKind, ToolParam.FilterStrength }, Key.F),
        new(CaptureTool.BrushFilter, "画笔滤镜", "BrushFilter", "按住涂抹，把经过的像素按滤镜类型处理",
            new[] { ToolParam.Width, ToolParam.FilterKind, ToolParam.FilterStrength }, Key.Y),
        new(CaptureTool.AutoFilter, "自动打码", "AutoFilter",
            "自动识别文字 / 图片 / 头像 / 图标…：点一下给那一块打码，拖动可框选多块；「填充区域」按类型一次填满",
            new[] { ToolParam.AutoFilterKind, ToolParam.AutoFilterStrength, ToolParam.FillRegions }, Key.V),
        new(CaptureTool.Eraser, "橡皮", "Eraser", "点一下去掉整条标注；参数行的模式能切成矩形 / 画笔橡皮", EraserFamily, Key.X),
        new(CaptureTool.RectEraser, "矩形橡皮", "RectEraser", "拖出矩形，把这块还原成截图原样（盖掉下面的标注 / 滤镜）", EraserFamily, Key.K),
        new(CaptureTool.BrushEraser, "画笔橡皮", "BrushEraser", "按住涂抹，把经过的地方还原成截图原样（盖掉下面的标注 / 滤镜）",
            BrushEraserFamily, Key.U),
    };

    public static ToolInfo Of(CaptureTool tool) => All.First(t => t.Tool == tool);

    /// <summary>字母快捷键 → 工具（没绑 = null）。打字时由调用方先早退。</summary>
    public static CaptureTool? ForKey(Key key)
    {
        foreach (var info in All)
            if (info.Shortcut == key) return info.Tool;
        return null;
    }

    /// <summary>这个工具用不用得到某一格参数（参数条据此显隐）。</summary>
    public static bool Uses(CaptureTool tool, ToolParam param) => Of(tool).Params.Contains(param);

    /// <summary>工具默认光标：文字是 I 形，其余是十字（选择是箭头，由覆盖层另判）。</summary>
    public static bool WantsCrossCursor(CaptureTool tool) => tool is not (CaptureTool.Select or CaptureTool.Text);

    /// <summary>拖出来的东西太小时不落盘（点一下不该留一个 0 像素的框）。</summary>
    public static double MinimumSize(CaptureTool tool) => IsFreehand(tool) ? 1 : 4;

    /// <summary>这几个工具靠笔迹（自由手绘）而不是拖矩形。</summary>
    public static bool IsFreehand(CaptureTool tool) =>
        tool is CaptureTool.Pen or CaptureTool.Highlighter or CaptureTool.BrushFilter or CaptureTool.BrushEraser;

    /// <summary>
    /// 一维工具（只有长度有意义）：横着拉、竖着拉都是合法的一条，
    /// 别按「宽和高都得够大」判（否则水平直线 / 测距 / 量水平距离根本画不出来）。
    /// </summary>
    public static bool IsLinear(CaptureTool tool) =>
        tool is CaptureTool.Line or CaptureTool.Distance or CaptureTool.Bracket or CaptureTool.Arrow;

    /// <summary>橡皮类：不改文档内容之外的东西，松手时一次性提交。</summary>
    public static bool IsEraser(CaptureTool tool) =>
        tool is CaptureTool.Eraser or CaptureTool.RectEraser or CaptureTool.BrushEraser;
}

/// <summary>
/// 橡皮族的参数文案：模式选择器上的三个名字（与工具栏格子的名字同一套叫法）。
/// </summary>
internal static class EraserLabel
{
    public static string Chip(CaptureTool tool) => tool switch
    {
        CaptureTool.RectEraser => "矩形橡皮",
        CaptureTool.BrushEraser => "画笔橡皮",
        _ => "橡皮",
    };

    public static string Name(CaptureTool tool) => tool switch
    {
        CaptureTool.RectEraser => "橡皮模式：拖矩形还原这块",
        CaptureTool.BrushEraser => "橡皮模式：按住涂抹还原",
        _ => "橡皮模式：点一下去掉整条标注",
    };
}

/// <summary>
/// 当前工具样式（颜色 / 线宽 / 字号 / 块大小 / 透明度）。
/// 工具栏改这里，标注创建时读这里——**只有一份状态**，不给每个工具各存一套。
/// </summary>
internal sealed class ToolStyle
{
    public static readonly Color[] Palette =
    {
        Color.FromRgb(0xE1, 0x5B, 0x4E),   // 红
        Color.FromRgb(0xF5, 0x9E, 0x0B),   // 琥珀
        Color.FromRgb(0x2E, 0xB8, 0x72),   // 绿
        Color.FromRgb(0x3B, 0x82, 0xF6),   // 蓝
        Color.FromRgb(0x8B, 0x5C, 0xF6),   // 紫
        Color.FromRgb(0xF4, 0xF4, 0xF5),   // 白
        Color.FromRgb(0x10, 0x10, 0x14),   // 近黑
    };

    /// <summary>填充色默认值（上游 <c>screenshotCanvasStyleDefaults()</c> 的 <c>#FFCCC7</c>）。</summary>
    public static readonly Color DefaultFillColor = Color.FromRgb(0xFF, 0xCC, 0xC7);

    public Color Color { get; set; } = Palette[0];
    public double Width { get; set; } = 4;
    public double FontSize { get; set; } = 20;
    public double Block { get; set; } = 8;

    /// <summary>荧光笔的不透明度；聚光用它换算压暗程度。</summary>
    public double Opacity { get; set; } = 0.35;

    /// <summary>序号徽标的类型（空心 / 实心 × 圆 / 方，外加不写数字的圆圈）。</summary>
    public SerialBadge Badge { get; set; } = SerialBadge.OutlinedCircle;

    /// <summary>下一个序号（上游参数行那个数字输入框，默认 1）。</summary>
    public int SerialNumber { get; set; } = 1;

    /// <summary>形状族的形状（矩形 / 椭圆 / 菱形 / 思考气泡）。</summary>
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;

    /// <summary>描边线型（实线 / 虚线 / 点线）。</summary>
    public StrokeKind StrokeStyle { get; set; } = StrokeKind.Solid;

    /// <summary>笔触风格（干净 / 手绘 / 涂鸦）。</summary>
    public StrokeRendering Rendering { get; set; } = StrokeRendering.Architect;

    /// <summary>填充方式（无 = 上游的 Border）。</summary>
    public FillKind FillStyle { get; set; } = FillKind.None;

    /// <summary>填充色（上游默认 <c>#FFCCC7</c>，与描边色分开）。</summary>
    public Color FillColor { get; set; } = DefaultFillColor;

    /// <summary>圆角半径（DIP，上游默认 6）。</summary>
    public double CornerRadius { get; set; } = 6;

    /// <summary>直线 / 曲线（上游默认曲线；两点之间两者视觉一致）。</summary>
    public LineType LineType { get; set; } = LineType.Curve;

    /// <summary>画笔模式（常规 / 压感）。</summary>
    public BrushMode BrushMode { get; set; } = BrushMode.Regular;

    /// <summary>序号的数字写法。</summary>
    public SerialNumbering Numbering { get; set; } = SerialNumbering.Arabic;

    /// <summary>矩形滤镜的处理方式（上游默认马赛克）。</summary>
    public FilterKind FilterMode { get; set; } = FilterKind.Mosaic;

    /// <summary>
    /// 滤镜强度 0–100：模糊按它算半径、亮度按它算倍数（50 = 原样），
    /// 灰度 / 反相 / 浮雕不看它。
    /// </summary>
    public double FilterStrength { get; set; } = 50;

    /// <summary>
    /// 自动打码实际用的滤镜类型：类型下拉不给「智能擦除」，所以停在它上面时
    /// 回落成马赛克（上游 <c>filter_style()</c> 的同一条规则）。
    /// </summary>
    public FilterKind FilterForAutoFill =>
        FilterMode == FilterKind.SmartErase ? FilterKind.Mosaic : FilterMode;

    /// <summary>荧光笔的笔头比普通画笔粗，否则看着就是一条普通线。</summary>
    public double WidthFor(CaptureTool tool) => tool switch
    {
        CaptureTool.Highlighter => Math.Max(Width, 10),
        _ => Width,
    };

    /// <summary>放大镜倍率。</summary>
    public double Factor { get; set; } = 2;

    /// <summary>放大镜与源区域的连接方式（上游默认渐变桥）。</summary>
    public MagnifierConnection Connection { get; set; } = MagnifierConnection.Gradient;

    /// <summary>放大镜的源区域轮廓（上游默认开）。</summary>
    public bool SourceOutline { get; set; } = true;

    /// <summary>箭头比例（0.5–3，上游 <c>arrow_ratio</c> 默认 1）。</summary>
    public double ArrowRatio { get; set; } = 1;

    /// <summary>箭头类型（上游默认曲线）。</summary>
    public ArrowType ArrowKind { get; set; } = ArrowType.Curve;

    /// <summary>起点端点（上游默认无）。</summary>
    public Arrowhead StartArrowhead { get; set; } = Arrowhead.None;

    /// <summary>终点端点（上游默认三角箭头）。</summary>
    public Arrowhead EndArrowhead { get; set; } = Arrowhead.Arrow;

    /// <summary>杆型（上游默认等宽）。</summary>
    public ArrowShaft ArrowShaftType { get; set; } = ArrowShaft.Plain;

    /// <summary>箭头外描边色（上游默认 <c>#FFCCC7</c>）。</summary>
    public Color ArrowOutlineColor { get; set; } = ArrowSpec.DefaultOutline;

    /// <summary>箭头外描边宽（上游默认 0 = 不描；上限 72）。</summary>
    public double ArrowOutlineWidth { get; set; }

    /// <summary>造一份箭头画法给新标注（点列由拖动产生，这里只管样式）。</summary>
    public ArrowSpec ArrowSpecOf(uint seed) => new ArrowSpec(
        Color, Width, StrokeStyle, Rendering, ArrowKind, StartArrowhead, EndArrowhead,
        ArrowRatio, ArrowShaftType, ArrowOutlineColor, ArrowOutlineWidth, 1, seed).Clamped();

    /// <summary>水印文字（上游默认空：没填就什么都不画）。</summary>
    public string WatermarkText { get; set; } = string.Empty;

    /// <summary>水印倾斜角度（度，逆时针为正；上游默认 30）。</summary>
    public double WatermarkAngle { get; set; } = 30;

    /// <summary>水印间距（DIP；上游默认 56）。</summary>
    public double WatermarkGap { get; set; } = 56;

    /// <summary>水印不透明度（上游默认 0.16，比荧光笔低得多）。</summary>
    public double WatermarkOpacity { get; set; } = 0.16;

    /// <summary>聚光的压暗程度：透明度越低，框外压得越暗。</summary>
    public double Dim => Math.Clamp(1 - Opacity, 0.15, 0.85);

    /// <summary>
    /// 造一份形状样式给新标注（形状族与气泡族共用）。
    /// <paramref name="seed"/> 是手绘抖动种子：同一条标注重绘多次结果一致。
    /// </summary>
    public ShapeSpec ShapeSpecOf(uint seed) => new(
        ShapeKind, Color, Width, StrokeStyle, Rendering, FillColor, FillStyle, CornerRadius, seed);

    // ============ 测距 / 测角 / 橡皮（上游 distance_style / angle_style / brush_eraser_style） ============

    /// <summary>测距单位（上游默认厘米）。</summary>
    public DistanceUnit DistanceUnit { get; set; } = DistanceUnit.Cm;

    /// <summary>测距系数：数值 = 长度 × 屏幕缩放 × 系数（上游默认 1）。</summary>
    public double DistanceFactor { get; set; } = 1;

    /// <summary>测距小数位（0–3，上游默认 0）。</summary>
    public int DistanceDecimals { get; set; }

    /// <summary>测距端点比例（0.5–3.0，上游默认 1）。</summary>
    public double EndpointScale { get; set; } = 1;

    /// <summary>测距端点样式（上游默认横杠）。</summary>
    public Arrowhead EndpointStyle { get; set; } = Arrowhead.Bar;

    /// <summary>角度类型（上游默认有向 360°）。</summary>
    public AngleKind AngleKind { get; set; } = AngleKind.Directed;

    /// <summary>角度单位（上游默认度）。</summary>
    public AngleUnit AngleUnit { get; set; } = AngleUnit.Degrees;

    /// <summary>角度小数位（0–3，上游默认 0）。</summary>
    public int AngleDecimals { get; set; }

    /// <summary>造一份测距参数给新标注。</summary>
    public DistanceSpec DistanceSpecOf() => new DistanceSpec(
        DistanceUnit, DistanceFactor, DistanceDecimals, EndpointScale, EndpointStyle).Clamped();

    /// <summary>造一份测角参数给新标注。</summary>
    public AngleSpec AngleSpecOf() => new AngleSpec(AngleKind, AngleUnit, AngleDecimals).Clamped();

    // ============ 文字族（上游 SnowCanvasTextStyle） ============

    /// <summary>文字字号（与 <see cref="FontSize"/> 分开：那个是水印 / 序号的）。</summary>
    public double TextSize { get; set; } = TextInk.DefaultFontSize;

    /// <summary>字体族；空 = 项目默认（微软雅黑 UI）。</summary>
    public string FontFamily { get; set; } = string.Empty;

    public bool TextBold { get; set; }
    public bool TextItalic { get; set; }

    /// <summary>文字水平对齐（上游只有水平，垂直留给固定框、这里文字自适应所以用不到）。</summary>
    public TextAlign TextAlign { get; set; } = TextAlign.Left;

    /// <summary>文字描边宽度（0 = 不描边，上游默认 0）。</summary>
    public double TextStrokeWidth { get; set; }

    /// <summary>文字描边色（上游默认 <c>#FFCCC7</c>）。</summary>
    public Color StrokeColor { get; set; } = TextInk.DefaultStroke;

    /// <summary>造一份文字样式给新标注。</summary>
    public TextSpec TextSpecOf() => new(
        FontFamily, TextSize, TextBold, TextItalic, TextAlign,
        Color, TextStrokeWidth, StrokeColor, FillColor, FillStyle, CornerRadius);

    // ============ 序号 / 水印 / 放大镜（上游 serial_number_style / watermark / magnifier） ============

    /// <summary>造一份序号画法（徽标 / 写法 / 色 / 填充 / 字号 / 字体 / 描边 / 不透明度）。</summary>
    public SerialSpec SerialSpecOf() => new SerialSpec(
        Badge, Numbering, Color, FillColor, FillStyle, FontSize, FontFamily, Width, Opacity).Clamped();

    /// <summary>造一份水印画法（文字 / 色 / 字号 / 字体 / 角度 / 间距 / 不透明度）。</summary>
    public WatermarkSpec WatermarkSpecOf() => new WatermarkSpec(
        WatermarkText, Color, FontSize, FontFamily, WatermarkAngle, WatermarkGap,
        WatermarkOpacity).Clamped();

    /// <summary>造一份放大镜画法（形状 / 色 / 线宽 / 倍率 / 连接 / 源轮廓 / 箭头 / 圆角）。</summary>
    public MagnifierSpec MagnifierSpecOf() => new MagnifierSpec(
        ShapeKind, Color, Width, Factor, Connection, SourceOutline, EndpointStyle, ArrowRatio,
        CornerRadius, Opacity).Clamped();

    // ============ 落盘（ui.json 的 CaptureStyles） ============

    /// <summary>写进配置文件的一份（字段全给，读回来等价）。</summary>
    public CaptureToolStyleDto ToDto() => new()
    {
        Color = ToolStyleFormat.Hex(Color),
        Width = Width,
        FontSize = FontSize,
        Block = Block,
        Opacity = Opacity,
        Shape = ToolStyleFormat.Name(Badge),
        Numbering = ToolStyleFormat.Name(Numbering),
        SerialNumber = SerialNumber,
        ShapeKind = ToolStyleFormat.Name(ShapeKind),
        StrokeStyle = ToolStyleFormat.Name(StrokeStyle),
        Rendering = ToolStyleFormat.Name(Rendering),
        FillStyle = ToolStyleFormat.Name(FillStyle),
        FillColor = ToolStyleFormat.Hex(FillColor),
        CornerRadius = CornerRadius,
        LineType = ToolStyleFormat.Name(LineType),
        BrushMode = ToolStyleFormat.Name(BrushMode),
        FilterMode = ToolStyleFormat.Name(FilterMode),
        FilterStrength = FilterStrength,
        Factor = Factor,
        Connection = ToolStyleFormat.Name(Connection),
        SourceOutline = SourceOutline,
        ArrowRatio = ArrowRatio,
        ArrowKind = ToolStyleFormat.Name(ArrowKind),
        StartArrowhead = ToolStyleFormat.Name(StartArrowhead),
        EndArrowhead = ToolStyleFormat.Name(EndArrowhead),
        ArrowShaftType = ToolStyleFormat.Name(ArrowShaftType),
        ArrowOutlineColor = ToolStyleFormat.Hex(ArrowOutlineColor),
        ArrowOutlineWidth = ArrowOutlineWidth,
        WatermarkText = WatermarkText,
        WatermarkAngle = WatermarkAngle,
        WatermarkGap = WatermarkGap,
        WatermarkOpacity = WatermarkOpacity,
        TextSize = TextSize,
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? null : FontFamily,
        TextBold = TextBold,
        TextItalic = TextItalic,
        TextAlign = ToolStyleFormat.Name(TextAlign),
        TextStrokeWidth = TextStrokeWidth,
        StrokeColor = ToolStyleFormat.Hex(StrokeColor),
        DistanceUnit = ToolStyleFormat.Name(DistanceUnit),
        DistanceFactor = DistanceFactor,
        DistanceDecimals = DistanceDecimals,
        EndpointScale = EndpointScale,
        EndpointStyle = ToolStyleFormat.Name(EndpointStyle),
        AngleKind = ToolStyleFormat.Name(AngleKind),
        AngleUnit = ToolStyleFormat.Name(AngleUnit),
        AngleDecimals = AngleDecimals,
    };

    /// <summary>从配置读一份：缺项用默认，超范围钳回界面能摆出来的区间（读坏不炸）。</summary>
    public static ToolStyle FromDto(CaptureToolStyleDto dto)
    {
        var style = new ToolStyle();
        if (ToolStyleFormat.TryParseColor(dto.Color, out var color)) style.Color = color;

        style.Width = Math.Clamp(dto.Width ?? style.Width, 1, 50);
        style.FontSize = Math.Clamp(dto.FontSize ?? style.FontSize, 6, 256);
        style.Block = Math.Clamp(dto.Block ?? style.Block, 4, 24);
        style.Opacity = Math.Clamp(dto.Opacity ?? style.Opacity, 0.1, 0.9);
        style.Badge = ToolStyleFormat.ParseEnum(dto.Shape, style.Badge);
        style.Numbering = ToolStyleFormat.ParseEnum(dto.Numbering, style.Numbering);
        style.SerialNumber = Math.Max(0, dto.SerialNumber ?? style.SerialNumber);
        style.ShapeKind = ToolStyleFormat.ParseEnum(dto.ShapeKind, style.ShapeKind);
        style.StrokeStyle = ToolStyleFormat.ParseEnum(dto.StrokeStyle, style.StrokeStyle);
        style.Rendering = ToolStyleFormat.ParseEnum(dto.Rendering, style.Rendering);
        style.FillStyle = ToolStyleFormat.ParseEnum(dto.FillStyle, style.FillStyle);
        if (ToolStyleFormat.TryParseColor(dto.FillColor, out var fill)) style.FillColor = fill;
        style.CornerRadius = Math.Clamp(dto.CornerRadius ?? style.CornerRadius, 0, TextInk.MaxCornerRadius);
        style.LineType = ToolStyleFormat.ParseEnum(dto.LineType, style.LineType);
        style.BrushMode = ToolStyleFormat.ParseEnum(dto.BrushMode, style.BrushMode);
        style.FilterMode = ToolStyleFormat.ParseEnum(dto.FilterMode, style.FilterMode);
        style.FilterStrength = Math.Clamp(dto.FilterStrength ?? style.FilterStrength, 0, 100);
        style.Factor = Math.Clamp(dto.Factor ?? style.Factor, MagnifierSpec.MinFactor, MagnifierSpec.MaxFactor);
        style.Connection = ToolStyleFormat.ParseEnum(dto.Connection, style.Connection);
        style.SourceOutline = dto.SourceOutline ?? style.SourceOutline;
        style.ArrowRatio = double.IsFinite(dto.ArrowRatio ?? style.ArrowRatio)
            ? Math.Clamp(dto.ArrowRatio ?? style.ArrowRatio, ArrowInk.MinRatio, ArrowInk.MaxRatio)
            : style.ArrowRatio;
        style.ArrowKind = ToolStyleFormat.ParseEnum(dto.ArrowKind, style.ArrowKind);
        style.StartArrowhead = ToolStyleFormat.ParseEnum(dto.StartArrowhead, style.StartArrowhead);
        style.EndArrowhead = ToolStyleFormat.ParseEnum(dto.EndArrowhead, style.EndArrowhead);
        style.ArrowShaftType = ToolStyleFormat.ParseEnum(dto.ArrowShaftType, style.ArrowShaftType);
        if (ToolStyleFormat.TryParseColor(dto.ArrowOutlineColor, out var arrowOutline))
            style.ArrowOutlineColor = arrowOutline;
        style.ArrowOutlineWidth = double.IsFinite(dto.ArrowOutlineWidth ?? style.ArrowOutlineWidth)
            ? Math.Clamp(dto.ArrowOutlineWidth ?? style.ArrowOutlineWidth, 0, ArrowSpec.MaxOutlineWidth)
            : style.ArrowOutlineWidth;
        if (dto.WatermarkText != null) style.WatermarkText = dto.WatermarkText;
        style.WatermarkAngle = Math.Clamp(dto.WatermarkAngle ?? style.WatermarkAngle, -90, 90);
        style.WatermarkGap = Math.Clamp(dto.WatermarkGap ?? style.WatermarkGap, 10, 200);
        style.WatermarkOpacity = Math.Clamp(dto.WatermarkOpacity ?? style.WatermarkOpacity, 0, 1);

        style.TextSize = Math.Clamp(dto.TextSize ?? style.TextSize, TextInk.MinFontSize, TextInk.MaxFontSize);
        if (dto.FontFamily != null) style.FontFamily = dto.FontFamily.Trim();
        style.TextBold = dto.TextBold ?? style.TextBold;
        style.TextItalic = dto.TextItalic ?? style.TextItalic;
        style.TextAlign = ToolStyleFormat.ParseEnum(dto.TextAlign, style.TextAlign);
        style.TextStrokeWidth = Math.Clamp(dto.TextStrokeWidth ?? style.TextStrokeWidth, 0, TextInk.MaxStrokeWidth);
        if (ToolStyleFormat.TryParseColor(dto.StrokeColor, out var stroke)) style.StrokeColor = stroke;

        style.DistanceUnit = ToolStyleFormat.ParseEnum(dto.DistanceUnit, style.DistanceUnit);
        style.DistanceFactor = double.IsFinite(dto.DistanceFactor ?? style.DistanceFactor)
            ? Math.Clamp(dto.DistanceFactor ?? style.DistanceFactor, 0.01, 1000)
            : style.DistanceFactor;
        style.DistanceDecimals = MeasureLabel.ClampDecimals(dto.DistanceDecimals ?? style.DistanceDecimals);
        style.EndpointScale = double.IsFinite(dto.EndpointScale ?? style.EndpointScale)
            ? Math.Clamp(dto.EndpointScale ?? style.EndpointScale, ArrowInk.MinRatio, ArrowInk.MaxRatio)
            : style.EndpointScale;
        style.EndpointStyle = ToolStyleFormat.ParseEnum(dto.EndpointStyle, style.EndpointStyle);
        style.AngleKind = ToolStyleFormat.ParseEnum(dto.AngleKind, style.AngleKind);
        style.AngleUnit = ToolStyleFormat.ParseEnum(dto.AngleUnit, style.AngleUnit);
        style.AngleDecimals = MeasureLabel.ClampDecimals(dto.AngleDecimals ?? style.AngleDecimals);
        return style;
    }
}
