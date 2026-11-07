using LTools.Core.Actions;

namespace LTools.Features.Units.Screen;

/// <summary>屏幕类单元共用的区域参数解析（前台窗口 / 整个屏幕 / 固定矩形）。</summary>
internal static class ScreenArgs
{
    public static readonly string[] Regions = { "foreground", "screen", "rect" };

    public static ImageRegion ParseRegion(string text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "foreground" => ImageRegion.foreground,
        "screen" => ImageRegion.screen,
        "rect" => ImageRegion.rect,
        _ => throw new ActionException($"区域只能是 foreground / screen / rect（当前 {text}）"),
    };

    /// <summary>固定区域的 <c>x,y,w,h</c>；没填 / 填错时返回 null。</summary>
    public static int[]? ParseRect(StepArgs args)
    {
        string text = args.Text("rect").Trim();
        if (text.Length == 0) return null;
        return UnitValues.ParseRect(Value.FromText(text));
    }

    /// <summary>
    /// 取图片参数：整段是变量且带 <c>file</c> 成员时用文件路径（截图的 <c>${截图}</c>），
    /// 否则直接当路径文本用。
    /// </summary>
    public static string ImagePath(StepArgs args, string key = "image")
    {
        var value = args.Get(key);
        if (value.Kind == ValueKind.@object &&
            UnitValues.Member(value, "file") is { } file &&
            file.AsText().Length > 0)
            return file.AsText();

        return value.AsText().Trim();
    }

    /// <summary>按参数构造找图 / 找字用的 <see cref="Probe"/>。</summary>
    public static Probe BuildProbe(StepArgs args, ImageRegion region, int[]? rect)
    {
        return new Probe
        {
            Kind = ProbeKind.image,
            Region = region,
            Rect = rect,
            TimeoutMs = (int)Math.Clamp(args.Has("timeoutMs") ? args.Number("timeoutMs") : 0, 0, 600_000),
            IntervalMs = (int)Math.Clamp(
                args.Has("intervalMs") ? args.Number("intervalMs") : 200, 50, 5_000),
            MinScore = Math.Clamp(args.Has("minScore") ? args.Number("minScore") : 0.85, 0.30, 0.99),
            OcrEngine = args.Has("ocrEngine") ? args.Text("ocrEngine").Trim() : null,
        };
    }
}
