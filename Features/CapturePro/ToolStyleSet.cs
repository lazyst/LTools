using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using LTools.Config;

namespace LTools.Features.CapturePro;

/// <summary>
/// 样式族 = 一套样式的归属。对齐上游 <c>screenshotcanvastoolstyles.cpp</c> 的
/// <c>drawing/*_style</c>：**每个工具家族各记一套**，切工具不会把别的工具的参数带走。
/// </summary>
/// <remarks>
/// 名字直接当 <c>ui.json</c> 的键，**不做别名、不认旧名**（改名字就同步改配置与示例）。
/// <c>Rect</c> / <c>Ellipse</c> 共用 <c>shape</c>（上游只有一个 Shape 工具 + 形状选择）；
/// 自动打码与矩形滤镜共用 <c>filter</c>（上游强度是共享的）。
/// </remarks>
internal static class StyleFamilies
{
    public const string Shape = "shape";
    public const string SpeechBubble = "speechBubble";
    public const string Arrow = "arrow";
    public const string Line = "line";
    public const string Polyline = "polyline";
    public const string Pen = "pen";
    public const string Highlighter = "highlighter";
    public const string HighlightRect = "highlightRect";
    public const string Distance = "distance";
    public const string Angle = "angle";
    public const string Bracket = "bracket";
    public const string Magnifier = "magnifier";
    public const string Text = "text";
    public const string Serial = "serial";
    public const string Mosaic = "mosaic";
    public const string Filter = "filter";
    public const string BrushFilter = "brushFilter";
    public const string Spotlight = "spotlight";
    public const string Watermark = "watermark";
    public const string BrushEraser = "brushEraser";

    /// <summary>落盘顺序（只影响 JSON 里键的顺序；读的时候按名字，与顺序无关）。</summary>
    public static readonly string[] All =
    {
        Shape, SpeechBubble, Arrow, Line, Polyline, Pen, Highlighter, HighlightRect, Distance,
        Angle, Bracket, Magnifier, Text, Serial, Mosaic, Filter, BrushFilter, Spotlight,
        Watermark, BrushEraser,
    };

    /// <summary>当前工具用哪一族。选择 / 橡皮 / 取色不画东西，给一个兜底族（不会被用到）。</summary>
    public static string Of(CaptureTool tool) => tool switch
    {
        CaptureTool.Pen => Pen,
        CaptureTool.Highlighter => Highlighter,
        CaptureTool.HighlightRect => HighlightRect,
        CaptureTool.Shape => Shape,
        CaptureTool.Arrow => Arrow,
        CaptureTool.Line => Line,
        CaptureTool.Polyline => Polyline,
        CaptureTool.Distance => Distance,
        CaptureTool.Angle => Angle,
        CaptureTool.Bracket => Bracket,
        CaptureTool.SpeechBubble => SpeechBubble,
        CaptureTool.Text => Text,
        CaptureTool.Serial => Serial,
        CaptureTool.Magnifier => Magnifier,
        CaptureTool.Watermark => Watermark,
        CaptureTool.Mosaic => Mosaic,
        CaptureTool.Filter or CaptureTool.AutoFilter => Filter,
        CaptureTool.BrushFilter => BrushFilter,
        CaptureTool.Spotlight => Spotlight,
        CaptureTool.BrushEraser => BrushEraser,
        _ => Shape,
    };

    public static bool IsKnown(string family) => Array.IndexOf(All, family) >= 0;
}

/// <summary>
/// 全部工具样式（每族一份 <see cref="ToolStyle"/>）+ 共享颜色开关。
/// 覆盖层持有**一个实例**，按当前工具取自己那一份。
/// </summary>
internal sealed class ToolStyleSet
{
    /// <summary>共享色默认值（上游 <c>sharedToolColor()</c> 的兜底红）。</summary>
    public static readonly Color DefaultSharedColor = Color.FromRgb(0xF5, 0x22, 0x2D);

    private readonly Dictionary<string, ToolStyle> _byFamily = new(StringComparer.Ordinal);

    /// <summary>改任一工具的颜色 = 所有工具一起变（上游「同步工具颜色」）。</summary>
    public bool SyncColors { get; private set; }

    /// <summary>同步用的共享色。</summary>
    public Color SharedColor { get; private set; } = DefaultSharedColor;

    /// <summary>
    /// 当前工具的样式（不存在的族按默认建一份）。
    /// </summary>
    public ToolStyle For(CaptureTool tool) => ForFamily(StyleFamilies.Of(tool));

    /// <summary>
    /// 某个族的样式；共享颜色开着时颜色恒等于共享色（关掉开关也保持统一，与上游一致）。
    /// 各族默认值只在 <see cref="ApplyFamilyDefaults"/>（别散到工具栏里）。
    /// </summary>
    public ToolStyle ForFamily(string family)
    {
        if (!_byFamily.TryGetValue(family, out var style))
        {
            style = new ToolStyle();
            ApplyFamilyDefaults(family, style);
            _byFamily[family] = style;
        }
        if (SyncColors) style.Color = SharedColor;
        return style;
    }

    /// <summary>
    /// 每族的出厂默认（上游 <c>screenshotCanvasStyleDefaults()</c> 与
    /// <c>SnowCanvas*Style</c> 的默认构造）。只有这里一处，自检直接断言。
    /// </summary>
    public static void ApplyFamilyDefaults(string family, ToolStyle style)
    {
        switch (family)
        {
            case StyleFamilies.BrushEraser:
                // 画笔橡皮默认笔头很粗（上游 brushEraserStyle.strokeWidth = 30），细笔头擦起来像没反应
                style.Width = 30;
                break;

            case StyleFamilies.Serial:
                style.Color = SerialSpec.DefaultStroke;
                style.FillColor = SerialSpec.DefaultFill;
                style.Badge = SerialBadge.OutlinedCircle;
                style.Numbering = SerialNumbering.Arabic;
                style.SerialNumber = 1;
                style.FontSize = 24;
                style.Width = 2;
                style.Opacity = 1;
                style.FillStyle = FillKind.None;
                break;

            case StyleFamilies.Watermark:
                style.Color = Colors.Black;
                style.FontSize = 16;
                style.WatermarkText = string.Empty;
                style.WatermarkAngle = 30;
                style.WatermarkGap = 56;
                style.WatermarkOpacity = 0.16;
                break;

            case StyleFamilies.Magnifier:
                style.Color = MagnifierSpec.DefaultStroke;
                style.ShapeKind = ShapeKind.Rectangle;
                style.Width = 2;
                style.Factor = 2;
                style.Connection = MagnifierConnection.Gradient;
                style.SourceOutline = true;
                style.EndpointStyle = Arrowhead.None;
                style.ArrowRatio = 1;
                style.CornerRadius = 6;
                style.Opacity = 1;
                break;

            case StyleFamilies.Arrow:
                // 上游 defaults.arrow = defaults.rectangle + 终点 Arrow + 类型 Curve
                style.Color = Color.FromRgb(0xF5, 0x22, 0x2D);
                style.Width = 2;
                style.StrokeStyle = StrokeKind.Solid;
                style.Rendering = StrokeRendering.Architect;
                style.ArrowKind = ArrowType.Curve;
                style.StartArrowhead = Arrowhead.None;
                style.EndArrowhead = Arrowhead.Arrow;
                style.ArrowRatio = 1;
                style.ArrowShaftType = ArrowShaft.Plain;
                style.ArrowOutlineColor = ArrowSpec.DefaultOutline;
                style.ArrowOutlineWidth = 0;
                break;
        }
    }

    /// <summary>开着共享颜色时改一处颜色 = 全改；关着时只改这一族。</summary>
    public void SetColor(string family, Color color)
    {
        ForFamily(family).Color = color;
        if (!SyncColors) return;
        SharedColor = color;
        ApplySharedColor();
    }

    public void SetSyncColors(bool enabled)
    {
        SyncColors = enabled;
        if (enabled) ApplySharedColor();
    }

    public void SetSharedColor(Color color)
    {
        SharedColor = color;
        if (SyncColors) ApplySharedColor();
    }

    private void ApplySharedColor()
    {
        foreach (var style in _byFamily.Values) style.Color = SharedColor;
    }

    // ============ 水印模板（上游 WatermarkTemplateSettings） ============

    private readonly List<WatermarkTemplate> _templates = new();

    /// <summary>水印模板（顺序就是参数行下拉的顺序）。</summary>
    public IReadOnlyList<WatermarkTemplate> WatermarkTemplates => _templates;

    /// <summary>加一个模板：同名就替换内容（上游「添加模板」同名不允许，这里按覆盖处理）。</summary>
    public void AddWatermarkTemplate(string name, string value)
    {
        string key = (name ?? string.Empty).Trim();
        if (key.Length == 0) return;

        int index = _templates.FindIndex(t => t.Name == key);
        var template = new WatermarkTemplate(key, (value ?? string.Empty).Trim());
        if (index >= 0) _templates[index] = template;
        else _templates.Add(template);
    }

    /// <summary>删一个模板；返回是否删到了。</summary>
    public bool RemoveWatermarkTemplate(string name) =>
        _templates.RemoveAll(t => t.Name == (name ?? string.Empty).Trim()) > 0;

    // ============ 落盘 ============

    /// <summary>写进 <c>ui.json</c>；只写已经创建过的族（没碰过的工具不留垃圾）。</summary>
    public CaptureStylesFile ToFile() => new()
    {
        SyncColors = SyncColors,
        SharedColor = SyncColors ? ToolStyleFormat.Hex(SharedColor) : null,
        Families = _byFamily.ToDictionary(pair => pair.Key, pair => pair.Value.ToDto()),
        WatermarkTemplates = _templates
            .Select(t => new CaptureWatermarkTemplate { Name = t.Name, Value = t.Value })
            .ToList(),
    };

    /// <summary>从 <c>ui.json</c> 读；认不出的族名直接丢掉。</summary>
    public static ToolStyleSet FromFile(CaptureStylesFile? file)
    {
        var set = new ToolStyleSet();
        if (file == null) return set;

        if (!string.IsNullOrWhiteSpace(file.SharedColor) &&
            ToolStyleFormat.TryParseColor(file.SharedColor, out var shared))
            set.SharedColor = shared;

        set.SyncColors = file.SyncColors;
        foreach (var (family, dto) in file.Families ?? new())
        {
            if (dto == null || !StyleFamilies.IsKnown(family)) continue;
            set._byFamily[family] = ToolStyle.FromDto(dto);
        }

        foreach (var template in file.WatermarkTemplates ?? new())
        {
            if (template == null || string.IsNullOrWhiteSpace(template.Name)) continue;
            set.AddWatermarkTemplate(template.Name, template.Value);
        }

        if (set.SyncColors) set.ApplySharedColor();
        return set;
    }

    /// <summary>自检用：清掉某个族的记忆（下次按默认）。</summary>
    internal void Forget(string family) => _byFamily.Remove(family);
}

/// <summary><see cref="ToolStyle"/> 与配置文件之间的转换（颜色一律 <c>#RRGGBB</c>）。</summary>
internal static class ToolStyleFormat
{
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.StartsWith("#", StringComparison.Ordinal)) s = s[1..];
        if (s.Length != 6 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
            return false;

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>枚举按名字存（改顺序不会改老配置的含义）。认不出就用默认。</summary>
    public static string Name<T>(T value) where T : struct, Enum => value.ToString();

    public static T ParseEnum<T>(string? text, T fallback) where T : struct, Enum =>
        !string.IsNullOrWhiteSpace(text) && Enum.TryParse<T>(text, out var value) ? value : fallback;
}
