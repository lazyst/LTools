using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro;

/// <summary>
/// 上游绘图工具键（<c>shape</c> / <c>free-draw</c> / <c>pen-filter</c>…）与
/// <see cref="CaptureTool"/> 的唯一对照表。
/// </summary>
/// <remarks>
/// 照 <c>presentation/settings/settingscatalog.cpp</c> 的 <c>drawingSelectableToolOptions()</c>
/// 与 <c>storage/configurationschema.cpp</c> 的 <c>kSchemaStrings34</c>：设置里的两份工具清单
/// （自动选中排除 / 禁用快速选择）只收这 16 个键，**顺序也照上游**。
/// 认不出的键在归一化时直接丢掉（不做别名，也不留旧写法）。
/// </remarks>
internal static class CaptureToolIds
{
    /// <summary>可列进设置的 16 个绘图工具（上游顺序）。</summary>
    public static readonly IReadOnlyList<(string Id, CaptureTool Tool)> Selectable = new[]
    {
        ("shape", CaptureTool.Shape),
        ("speech-bubble", CaptureTool.SpeechBubble),
        ("arrow", CaptureTool.Arrow),
        ("line", CaptureTool.Line),
        ("distance", CaptureTool.Distance),
        ("angle", CaptureTool.Angle),
        ("bracket", CaptureTool.Bracket),
        ("free-draw", CaptureTool.Pen),
        ("rectangle-highlight", CaptureTool.HighlightRect),
        ("pen-highlight", CaptureTool.Highlighter),
        ("spotlight", CaptureTool.Spotlight),
        ("magnifier", CaptureTool.Magnifier),
        ("rectangle-filter", CaptureTool.Filter),
        ("pen-filter", CaptureTool.BrushFilter),
        ("text", CaptureTool.Text),
        ("serial-number", CaptureTool.Serial),
    };

    private static readonly Dictionary<string, CaptureTool> ById =
        Selectable.ToDictionary(pair => pair.Id, pair => pair.Tool, StringComparer.Ordinal);

    private static readonly Dictionary<CaptureTool, string> ByTool =
        Selectable.ToDictionary(pair => pair.Tool, pair => pair.Id);

    /// <summary>工具 → 上游键；不在这 16 个里的工具（折线 / 橡皮 / 水印…）返回 null。</summary>
    public static string? Of(CaptureTool tool) => ByTool.TryGetValue(tool, out var id) ? id : null;

    /// <summary>上游键 → 工具；认不出返回 null。</summary>
    public static CaptureTool? Parse(string? id) =>
        id != null && ById.TryGetValue(id.Trim(), out var tool) ? tool : null;

    /// <summary>归一化：丢掉认不出的键、去重，保持上游顺序（不按用户保存顺序）。</summary>
    public static List<string> Normalize(IEnumerable<string>? ids)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids ?? Array.Empty<string>())
            if (ById.ContainsKey(id?.Trim() ?? string.Empty)) set.Add(id!.Trim());

        return Selectable.Select(pair => pair.Id).Where(set.Contains).ToList();
    }

    /// <summary>中文名（设置页摘要用，取 <see cref="ToolCatalog"/> 的名字），认不出返回 null。</summary>
    public static string? Name(string? id) =>
        Parse(id) is { } tool ? ToolCatalog.Of(tool).Name : null;

    /// <summary>一串键 → 「形状 / 画笔 / 文字」；空 = 空串。</summary>
    public static string Describe(IEnumerable<string>? ids) =>
        string.Join(" / ", Normalize(ids).Select(id => Name(id) ?? id));
}
