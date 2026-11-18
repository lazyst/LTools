using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 截图 Pro 的样式记忆（<c>configs/ui.json</c> 的 <c>CaptureStyles</c>）：
/// **每个工具家族一套**，对齐上游 <c>screenshotcanvastoolstyles.cpp</c> 的
/// <c>drawing/*_style</c> 那组键，而不是全项目一份样式。
/// </summary>
/// <remarks>
/// 字段都可空：缺省 = 该家族用默认值（默认值只有一处，在 <c>ToolStyle</c>）。
/// 认不出的家族名读的时候直接丢掉（不做兼容、不留别名）。
/// </remarks>
public sealed class CaptureStylesFile
{
    /// <summary>改任一工具的颜色 = 所有工具一起变（上游「同步工具颜色」，默认关）。</summary>
    public bool SyncColors { get; set; }

    /// <summary>同步用的共享色（<c>#RRGGBB</c>）；空 = 默认红。</summary>
    public string? SharedColor { get; set; }

    /// <summary>家族名 → 该家族记住的样式。</summary>
    public Dictionary<string, CaptureToolStyleDto> Families { get; set; } = new();

    /// <summary>水印模板（上游 <c>WatermarkTemplate</c>：名字 + 内容，参数行下拉可增删）。</summary>
    public List<CaptureWatermarkTemplate> WatermarkTemplates { get; set; } = new();
}

/// <summary>一个水印模板：名字（下拉里显示的那一条）+ 内容（写进水印文字的值）。</summary>
public sealed class CaptureWatermarkTemplate
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>一个工具家族的样式（每个字段都可空，缺省 = 默认）。</summary>
public sealed class CaptureToolStyleDto
{
    /// <summary>主色 / 描边色（<c>#RRGGBB</c>）。</summary>
    public string? Color { get; set; }

    /// <summary>线宽（DIP）。</summary>
    public double? Width { get; set; }

    /// <summary>字号（DIP）。</summary>
    public double? FontSize { get; set; }

    /// <summary>马赛克块边长（DIP）。</summary>
    public double? Block { get; set; }

    /// <summary>荧光笔不透明度（0–1）。</summary>
    public double? Opacity { get; set; }

    /// <summary>序号徽标类型（<c>SerialBadge</c> 名字）。</summary>
    public string? Shape { get; set; }

    /// <summary>序号数字写法（<c>SerialNumbering</c> 名字）。</summary>
    public string? Numbering { get; set; }

    /// <summary>下一个序号（参数行里那个数字框）。</summary>
    public int? SerialNumber { get; set; }

    /// <summary>形状族的形状（<c>ShapeKind</c> 名字）。</summary>
    public string? ShapeKind { get; set; }

    /// <summary>描边线型（<c>StrokeKind</c> 名字）。</summary>
    public string? StrokeStyle { get; set; }

    /// <summary>笔触风格（<c>StrokeRendering</c> 名字）。</summary>
    public string? Rendering { get; set; }

    /// <summary>填充方式（<c>FillKind</c> 名字）。</summary>
    public string? FillStyle { get; set; }

    /// <summary>填充色（<c>#RRGGBB</c>）。</summary>
    public string? FillColor { get; set; }

    /// <summary>圆角半径（DIP）。</summary>
    public double? CornerRadius { get; set; }

    /// <summary>直线 / 曲线（<c>LineType</c> 名字）。</summary>
    public string? LineType { get; set; }

    /// <summary>画笔模式（<c>BrushMode</c> 名字）。</summary>
    public string? BrushMode { get; set; }

    /// <summary>滤镜类型（<c>FilterKind</c> 名字）。</summary>
    public string? FilterMode { get; set; }

    /// <summary>滤镜强度 0–100。</summary>
    public double? FilterStrength { get; set; }

    /// <summary>放大镜倍率。</summary>
    public double? Factor { get; set; }

    /// <summary>放大镜连接方式（<c>MagnifierConnection</c> 名字）。</summary>
    public string? Connection { get; set; }

    /// <summary>放大镜的源区域轮廓开关。</summary>
    public bool? SourceOutline { get; set; }

    /// <summary>箭头比例（0.5–3）。</summary>
    public double? ArrowRatio { get; set; }

    /// <summary>箭头类型（<c>ArrowType</c> 名字：直线 / 曲线 / 折线）。</summary>
    public string? ArrowKind { get; set; }

    /// <summary>起点端点（<c>Arrowhead</c> 名字）。</summary>
    public string? StartArrowhead { get; set; }

    /// <summary>终点端点（<c>Arrowhead</c> 名字）。</summary>
    public string? EndArrowhead { get; set; }

    /// <summary>杆型（<c>ArrowShaft</c> 名字：等宽 / 渐宽）。</summary>
    public string? ArrowShaftType { get; set; }

    /// <summary>箭头外描边色（<c>#RRGGBB</c>）。</summary>
    public string? ArrowOutlineColor { get; set; }

    /// <summary>箭头外描边宽（0–72，0 = 不描）。</summary>
    public double? ArrowOutlineWidth { get; set; }

    /// <summary>水印文字。</summary>
    public string? WatermarkText { get; set; }

    /// <summary>水印角度（度）。</summary>
    public double? WatermarkAngle { get; set; }

    /// <summary>水印间距（DIP）。</summary>
    public double? WatermarkGap { get; set; }

    /// <summary>水印不透明度（0–1）。</summary>
    public double? WatermarkOpacity { get; set; }

    /// <summary>文字字号（DIP，6–256）。</summary>
    public double? TextSize { get; set; }

    /// <summary>字体族；空 = 项目默认。</summary>
    public string? FontFamily { get; set; }

    /// <summary>文字粗体。</summary>
    public bool? TextBold { get; set; }

    /// <summary>文字斜体。</summary>
    public bool? TextItalic { get; set; }

    /// <summary>文字水平对齐（<c>TextAlign</c> 名字）。</summary>
    public string? TextAlign { get; set; }

    /// <summary>文字描边宽度（DIP，0–72）。</summary>
    public double? TextStrokeWidth { get; set; }

    /// <summary>文字描边色（<c>#RRGGBB</c>）。</summary>
    public string? StrokeColor { get; set; }

    /// <summary>测距单位（<c>DistanceUnit</c> 名字）。</summary>
    public string? DistanceUnit { get; set; }

    /// <summary>测距系数（0.01–1000）。</summary>
    public double? DistanceFactor { get; set; }

    /// <summary>测距小数位（0–3）。</summary>
    public int? DistanceDecimals { get; set; }

    /// <summary>测距端点比例（0.5–3）。</summary>
    public double? EndpointScale { get; set; }

    /// <summary>测距端点样式（<c>Arrowhead</c> 名字）。</summary>
    public string? EndpointStyle { get; set; }

    /// <summary>测角类型（<c>AngleKind</c> 名字：有向 / 内角）。</summary>
    public string? AngleKind { get; set; }

    /// <summary>测角单位（<c>AngleUnit</c> 名字：度 / 弧度）。</summary>
    public string? AngleUnit { get; set; }

    /// <summary>测角小数位（0–3）。</summary>
    public int? AngleDecimals { get; set; }
}
