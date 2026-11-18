namespace LTools.Config;

/// <summary>
/// 贴图文字样式的默认值（<c>configs/settings.json</c> 的 <c>PinText</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>pin_to_screen/text_*</c>（<c>src/storage/configurationschema.cpp:1549</c>）：
/// 最大文字宽 100–2000（默认 1024 px）、字体族（空 = 系统默认）、字号 8–96（默认 16）、
/// 文字颜色与背景颜色（<c>system</c> = 跟随主题）。这些只作用于**新贴上去的文字**，
/// 已经在屏幕上的贴图用记录里存下的那份样式（<see cref="Features.CapturePro.PinTextSnapshot"/>）。
/// </para>
/// <para>
/// 归一（夹范围 / 认颜色写法）全在 <c>Features/CapturePro/PinTextRules</c> 一处，
/// 读的时候按归一后的值用，写的时候也只写归一后的值。
/// </para>
/// </remarks>
public sealed class PinTextFile
{
    /// <summary>最大文字宽（px，含两侧内边距）。缺省 = 1024。</summary>
    public int? MaximumWidth { get; set; }

    /// <summary>默认字体族；空 = 系统界面字体。</summary>
    public string? FontFamily { get; set; }

    /// <summary>默认字号（px）。缺省 = 16。</summary>
    public int? FontSize { get; set; }

    /// <summary>文字颜色（<c>#RRGGBB</c> / <c>#AARRGGBB</c>）；空或 <c>system</c> = 跟随主题。</summary>
    public string? TextColor { get; set; }

    /// <summary>背景颜色；空或 <c>system</c> = 跟随主题。</summary>
    public string? BackgroundColor { get; set; }
}
