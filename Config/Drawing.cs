using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 绘制行为设置（<c>configs/settings.json</c> 的 <c>Drawing</c> 段）。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>drawing/*</c>（见 <c>storage/configurationschema.cpp</c>）：
/// 「新画的元素自动选中」（<c>automatically_select_newly_created_elements</c>，默认关）、
/// 自动选中的排除工具（<c>automatic_selection_excluded_tools</c>，默认画笔 / 画笔滤镜 / 文字 / 气泡）、
/// 禁用快速选择的工具（<c>quick_selection_disabled_tools</c>，默认画笔 / 画笔滤镜）、
/// 「Delete 删光标后的文字」（<c>delete_text_after_cursor</c>，默认开）、
/// 「记住上次工具」（<c>remember_last_used_tool</c>，默认关）、
/// 「每组固定显示默认工具」（<c>always_show_first_toolbar_group_button</c>，默认关；键名沿用上游）。
/// 工具名一律是上游的英文键（<c>free-draw</c> / <c>pen-filter</c>…），映射见
/// <c>Features/CapturePro/CaptureToolIds.cs</c>。
/// </remarks>
public sealed class DrawingFile
{
    /// <summary>新画的元素自动选中（画完仍停在这个工具上），默认关。</summary>
    public bool? AutomaticallySelectNewlyCreatedElements { get; set; } = false;

    /// <summary>不参与自动选中的工具（默认 free-draw / pen-filter / text / speech-bubble）。</summary>
    public List<string>? AutomaticSelectionExcludedTools { get; set; } =
        new() { "free-draw", "pen-filter", "text", "speech-bubble" };

    /// <summary>禁用快速选择的工具（默认 free-draw / pen-filter）。</summary>
    public List<string>? QuickSelectionDisabledTools { get; set; } = new() { "free-draw", "pen-filter" };

    /// <summary>编辑文字时按 Delete 删选中 / 光标后的字符（默认开；关掉只有退格可用）。</summary>
    public bool? DeleteTextAfterCursor { get; set; } = true;

    /// <summary>新的截图 / 图片编辑从上次用过的绘图工具开始（默认关 = 从「选择」开始）。</summary>
    public bool? RememberLastUsedTool { get; set; } = false;

    /// <summary>工具条里每组固定显示第一个工具，而不是上次用过的那个（默认关）。</summary>
    public bool? AlwaysShowFirstToolbarGroupButton { get; set; } = false;
}
