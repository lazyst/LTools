using System;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>贴图文字样式的默认值（归一后）：读盘一次就能直接用。</summary>
/// <param name="MaximumWidth">最大文字宽（px，含内边距）。</param>
/// <param name="FontSize">字号（px）。</param>
/// <param name="FontFamily">字体族；空 = 系统界面字体。</param>
/// <param name="TextColor">字色；<c>null</c> = 跟随主题。</param>
/// <param name="BackgroundColor">底色；<c>null</c> = 跟随主题。</param>
internal readonly record struct PinTextLimits(
    int MaximumWidth, double FontSize, string FontFamily, Color? TextColor, Color? BackgroundColor)
{
    /// <summary>上游默认值：1024 / 16 / 系统字体 / 跟随主题。</summary>
    public static PinTextLimits Default => new(PinTextRules.DefaultMaximumWidth, PinTextRules.DefaultFontSize,
        "", null, null);

    /// <summary>把内存里的配置对象按归一规则读成可用值。</summary>
    public static PinTextLimits From(PinTextFile? file)
    {
        if (file == null) return Default;
        var (width, size, font, text, background) = PinTextRules.Normalize(
            file.MaximumWidth, file.FontSize, file.FontFamily, file.TextColor, file.BackgroundColor);
        return new PinTextLimits(width, size, font, text, background);
    }

    /// <summary>写回配置对象（只写归一后的值，颜色 <c>null</c> 写成 <c>system</c>）。</summary>
    public PinTextFile ToFile() => new()
    {
        MaximumWidth = MaximumWidth,
        FontSize = (int)Math.Round(FontSize),
        FontFamily = FontFamily.Length == 0 ? null : FontFamily,
        TextColor = PinTextRules.FormatColor(TextColor),
        BackgroundColor = PinTextRules.FormatColor(BackgroundColor),
    };
}

/// <summary>
/// 贴图文字样式默认值的读写（<c>configs/settings.json</c> 的 <c>PinText</c> 段）。
/// </summary>
/// <remarks>
/// 照上游 <c>PinToScreenSettings</c> 的 <c>text_maximum_width</c> / <c>text_font_family</c> /
/// <c>text_font_size</c> / <c>text_color</c> / <c>text_background_color</c>。
/// 这些只作用于**新贴的文字**；已经贴出去的用记录里的 <see cref="PinTextSnapshot"/>。
/// 读写锚点走 <c>ConfigStore.ConfigPath</c>（与 <c>TranslationStore</c> / <c>TextRecognitionStore</c> 同一个写法），
/// 否则导入备份 / 冒烟把配置指到临时目录后这里会偷偷写另一份。
/// </remarks>
internal static class PinTextStore
{
    /// <summary>
    /// 读写的锚点：优先 <c>ConfigStore.ConfigPath</c>（导入备份 / 冒烟指到临时目录时跟着走），
    /// 没初始化才回落 <see cref="ConfigLocator.FindPath"/>——读与写必须用同一个，否则会写入一处、读到另一处。
    /// </summary>
    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    /// <summary>当前生效的默认值（读不到按上游默认值）。</summary>
    public static PinTextLimits Limits()
    {
        try
        {
            return PinTextLimits.From(SettingsStore.Read(Anchor).PinText);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinText", "读取贴图文字样式默认值失败：" + ex.Message);
            return PinTextLimits.Default;
        }
    }

    /// <summary>写一份新的默认值（归一后再落盘，UI 传什么都不会写出越界值）。</summary>
    public static void Save(PinTextLimits limits)
    {
        PinTextFile normalized = limits.ToFile();
        try
        {
            SettingsStore.Modify(Anchor, file => file.PinText = normalized);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinText", "保存贴图文字样式默认值失败：" + ex.Message);
        }
    }

    /// <summary>恢复默认（= 上游设置里「重置文字样式」那一节）。</summary>
    public static void Reset() => Save(PinTextLimits.Default);
}
