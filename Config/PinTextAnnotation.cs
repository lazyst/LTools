namespace LTools.Config;

/// <summary>
/// 一种快速文字标注工具的样式（<c>configs/settings.json</c> 的
/// <c>PinToScreen.QuickTextAnnotationStyles.&lt;工具&gt;</c> 那一项）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>pin_to_screen/quick_text_annotation_styles</c>（<c>{}</c> 默认空）：
/// 四个工具键 <c>highlight</c> / <c>wavy_underline</c> / <c>underline</c> / <c>strikethrough</c>，
/// 每项只存 <c>color</c> / <c>width</c> / <c>stroke_style</c> / <c>rendering_style</c>，
/// 缺哪一项就用该工具的默认值（照 <c>screenshotCanvasStyleDefaults()</c>：
/// 高亮 <c>#FF7875</c>、其余描边 <c>#F5222D</c>、宽 2、实线、<c>Architect</c>）。
/// </para>
/// <para>
/// **四项都可空**：读的时候缺项回落默认值、写的时候四项都写全（与上游 <c>persistPreference</c>
/// 的整项替换一致），所以配置文件里不会出现「半套样式」。
/// </para>
/// </remarks>
public sealed class PinTextAnnotationStyleFile
{
    /// <summary>颜色（<c>#RRGGBBAA</c>）；空 = 该工具的默认色。</summary>
    public string? Color { get; set; }

    /// <summary>线宽（0 &lt; w ≤ 72）；空 / 越界 = 默认 2。</summary>
    public double? Width { get; set; }

    /// <summary>线型（0 实线 / 1 虚线 / 2 点线）；空 / 越界 = 实线。</summary>
    public int? StrokeStyle { get; set; }

    /// <summary>笔触风格（0 干净 / 1 手绘 / 2 涂鸦）；空 / 越界 = 干净。</summary>
    public int? RenderingStyle { get; set; }
}
