using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>标注工具。工具只决定「按下鼠标会发生什么」，不持有文档状态。</summary>
internal enum CaptureTool
{
    Select,
    Pen,
    Highlighter,
    HighlightRect,
    Rect,
    Ellipse,
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
    Shape,

    /// <summary>序号的数字写法。</summary>
    Numbering,

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

    /// <summary>水印文字。</summary>
    WatermarkText,

    /// <summary>水印倾斜角度。</summary>
    WatermarkAngle,

    /// <summary>水印间距。</summary>
    WatermarkGap,
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

    public static readonly ToolInfo[] All =
    {
        new(CaptureTool.Select, "选择", "Select", "点选标注后可拖动移动，Delete 删除", None, Key.Q),
        new(CaptureTool.Rect, "矩形", "Rect", "拖出矩形框", Stroke, Key.R),
        new(CaptureTool.Ellipse, "椭圆", "Ellipse", "拖出椭圆", Stroke, Key.E),
        new(CaptureTool.Arrow, "箭头", "Arrow", "从起点拖到目标", Stroke, Key.A),
        new(CaptureTool.Line, "直线", "Line", "从起点拖到终点，不带箭头", Stroke, Key.I),
        new(CaptureTool.Polyline, "折线", "Polyline", "逐点单击，双击 / Enter 结束", Stroke, Key.L),
        new(CaptureTool.Distance, "测距", "Distance", "拖出线段，中点显示长度（像素）", Stroke, Key.D),
        new(CaptureTool.Angle, "测角", "Angle", "先拖一条臂，再拖第二条臂；选中后滚轮改角度", Stroke, Key.G),
        new(CaptureTool.Bracket, "括号", "Bracket", "拖出带两端刻度的区间", Stroke, Key.B),
        new(CaptureTool.SpeechBubble, "气泡", "SpeechBubble", "拖出气泡框，左下角带指向外的尾巴", Stroke, Key.O),
        new(CaptureTool.Pen, "画笔", "Pen", "按住拖动自由绘制", Stroke, Key.P),
        new(CaptureTool.Picker, "取色", "Picker", "鼠标下的颜色，单击复制 #RRGGBB", None, Key.C),
        new(CaptureTool.Spotlight, "聚光", "Spotlight", "拖出保持原样的矩形，其余压暗", new[] { ToolParam.Opacity }, Key.S),
        new(CaptureTool.Highlighter, "画笔荧光", "Highlighter", "半透明粗线，给文字打底", new[] { ToolParam.Color, ToolParam.Width, ToolParam.Opacity }, Key.H),
        new(CaptureTool.HighlightRect, "矩形荧光", "HighlightRect", "把一块区域铺上半透明色", new[] { ToolParam.Color, ToolParam.Opacity }, Key.J),
        new(CaptureTool.Text, "文字", "Text", "单击输入文字，Enter 结束（Shift+Enter 换行）", new[] { ToolParam.Color, ToolParam.FontSize }, Key.T),
        new(CaptureTool.Serial, "序号", "Serial", "单击添加序号，自动递增（删除不重排）",
            new[] { ToolParam.Color, ToolParam.Shape, ToolParam.Numbering }, Key.N),
        new(CaptureTool.Magnifier, "放大镜", "Magnifier", "拖出镜头框，框内的画面按倍率放大",
            new[] { ToolParam.Color, ToolParam.Width, ToolParam.Factor }, Key.Z),
        new(CaptureTool.Watermark, "水印", "Watermark", "整张图斜向平铺水印文字，单击落到图上",
            new[] { ToolParam.Color, ToolParam.FontSize, ToolParam.WatermarkText, ToolParam.WatermarkAngle, ToolParam.WatermarkGap, ToolParam.Opacity }, Key.W),
        new(CaptureTool.Mosaic, "马赛克", "Mosaic", "拖出要打码的矩形", new[] { ToolParam.Block }, Key.M),
        new(CaptureTool.Filter, "矩形滤镜", "Filter", "拖出矩形，按参数行选的类型处理这一块（模糊 / 灰度 / 反相 / 浮雕 / 亮度 / 智能擦除）",
            new[] { ToolParam.FilterKind, ToolParam.FilterStrength }, Key.F),
        new(CaptureTool.BrushFilter, "画笔滤镜", "BrushFilter", "按住涂抹，把经过的像素按滤镜类型处理",
            new[] { ToolParam.Width, ToolParam.FilterKind, ToolParam.FilterStrength }, Key.Y),
        new(CaptureTool.AutoFilter, "自动打码", "AutoFilter",
            "自动识别文字 / 图片 / 头像 / 图标…：点一下给那一块打码，拖动可框选多块；「填充区域」按类型一次填满",
            new[] { ToolParam.AutoFilterKind, ToolParam.AutoFilterStrength, ToolParam.FillRegions }, Key.V),
        new(CaptureTool.Eraser, "橡皮", "Eraser", "点一下去掉整条标注", None, Key.X),
        new(CaptureTool.RectEraser, "矩形橡皮", "RectEraser", "拖出矩形，把这块还原成截图原样（盖掉下面的标注 / 滤镜）", None, Key.K),
        new(CaptureTool.BrushEraser, "画笔橡皮", "BrushEraser", "按住涂抹，把经过的地方还原成截图原样（盖掉下面的标注 / 滤镜）",
            new[] { ToolParam.Width }, Key.U),
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

    public Color Color { get; set; } = Palette[0];
    public double Width { get; set; } = 4;
    public double FontSize { get; set; } = 20;
    public double Block { get; set; } = 8;

    /// <summary>荧光笔的不透明度；聚光用它换算压暗程度。</summary>
    public double Opacity { get; set; } = 0.35;

    /// <summary>序号徽标的形状。</summary>
    public SerialShape Shape { get; set; } = SerialShape.Circle;

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
        CaptureTool.Magnifier => Math.Max(Width, 3),
        _ => Width,
    };

    /// <summary>放大镜倍率。</summary>
    public double Factor { get; set; } = 2;

    /// <summary>水印文字。</summary>
    public string WatermarkText { get; set; } = "LTools";

    /// <summary>水印倾斜角度（度，逆时针为正）。</summary>
    public double WatermarkAngle { get; set; } = -30;

    /// <summary>水印间距（DIP）。</summary>
    public double WatermarkGap { get; set; } = 120;

    /// <summary>水印不透明度（比荧光笔低得多，否则盖住整张图）。</summary>
    public double WatermarkOpacity { get; set; } = 0.15;

    /// <summary>聚光的压暗程度：透明度越低，框外压得越暗。</summary>
    public double Dim => Math.Clamp(1 - Opacity, 0.15, 0.85);
}
