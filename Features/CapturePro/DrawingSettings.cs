using System;
using System.Collections.Generic;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 绘制行为设置（<c>configs/settings.json</c> 的 <c>Drawing</c> 段）的读写与默认值。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>drawing/*</c>：默认值逐条对齐 <c>storage/configurationschema.cpp</c>
/// （自动选中关、排除画笔 / 画笔滤镜 / 文字 / 气泡；快速选择默认只禁用画笔 / 画笔滤镜；
/// Delete 删后一个字符默认开；记住上次工具与「每组固定显示默认工具」默认关）。
/// </para>
/// <para>
/// 读盘只发生一次（覆盖层在鼠标钩子线程上也要问），写盘只写 settings 域。
/// 「上次用过的工具」是界面状态，落 <c>ui.json</c>，见 <see cref="CaptureToolMemory"/>。
/// </para>
/// </remarks>
internal static class DrawingSettings
{
    public const bool DefaultAutoSelectNew = false;
    public const bool DefaultDeleteTextAfterCursor = true;
    public const bool DefaultRememberLastTool = false;
    public const bool DefaultAlwaysShowFirstGroup = false;

    /// <summary>上游 <c>kSchemaStrings33</c>（自动选中的排除工具）。</summary>
    public static readonly IReadOnlyList<string> DefaultAutoSelectExcluded =
        new[] { "free-draw", "pen-filter", "text", "speech-bubble" };

    /// <summary>上游 <c>kSchemaStrings35</c>（禁用快速选择的工具）。</summary>
    public static readonly IReadOnlyList<string> DefaultQuickSelectionDisabled =
        new[] { "free-draw", "pen-filter" };

    private static readonly object Gate = new();

    private static DrawingFile? _cached;

    public static bool AutoSelectNew => Snapshot().AutomaticallySelectNewlyCreatedElements ?? DefaultAutoSelectNew;

    public static bool DeleteTextAfterCursor =>
        Snapshot().DeleteTextAfterCursor ?? DefaultDeleteTextAfterCursor;

    public static bool RememberLastTool => Snapshot().RememberLastUsedTool ?? DefaultRememberLastTool;

    public static bool AlwaysShowFirstGroup =>
        Snapshot().AlwaysShowFirstToolbarGroupButton ?? DefaultAlwaysShowFirstGroup;

    public static IReadOnlyList<string> AutoSelectExcluded => Snapshot().AutomaticSelectionExcludedTools!;

    public static IReadOnlyList<string> QuickSelectionDisabled =>
        Snapshot().QuickSelectionDisabledTools!;

    /// <summary>这个工具新画的元素参不参与自动选中。</summary>
    public static bool IsAutoSelectExcluded(CaptureTool tool) =>
        CaptureToolIds.Of(tool) is { } id && AutoSelectExcluded.Contains(id);

    /// <summary>这个工具上还允不允许「左键点已有同类元素 = 快速选中」。</summary>
    public static bool IsQuickSelectionDisabled(CaptureTool tool) =>
        CaptureToolIds.Of(tool) is { } id && QuickSelectionDisabled.Contains(id);

    public static void SetAutoSelectNew(bool on) => Save(file => file.AutomaticallySelectNewlyCreatedElements = on);

    public static void SetDeleteTextAfterCursor(bool on) => Save(file => file.DeleteTextAfterCursor = on);

    public static void SetRememberLastTool(bool on) => Save(file => file.RememberLastUsedTool = on);

    public static void SetAlwaysShowFirstGroup(bool on) =>
        Save(file => file.AlwaysShowFirstToolbarGroupButton = on);

    public static void SetAutoSelectExcluded(IEnumerable<string> ids) =>
        Save(file => file.AutomaticSelectionExcludedTools = CaptureToolIds.Normalize(ids));

    public static void SetQuickSelectionDisabled(IEnumerable<string> ids) =>
        Save(file => file.QuickSelectionDisabledTools = CaptureToolIds.Normalize(ids));

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>当前设置（缓存里的副本已归一化：列表只留认得出的键、写成上游顺序）。</summary>
    private static DrawingFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        DrawingFile loaded;
        try
        {
            loaded = SettingsStore.Read(ConfigStore.Anchor).Drawing ?? new DrawingFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.DrawingSettings", "读取绘制设置失败：" + ex.Message);
            loaded = new DrawingFile();
        }

        loaded.AutomaticSelectionExcludedTools = CaptureToolIds.Normalize(
            loaded.AutomaticSelectionExcludedTools ?? DefaultAutoSelectExcluded);
        loaded.QuickSelectionDisabledTools = CaptureToolIds.Normalize(
            loaded.QuickSelectionDisabledTools ?? DefaultQuickSelectionDisabled);

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<DrawingFile> mutate)
    {
        DrawingFile next;
        lock (Gate)
        {
            next = Clone(Snapshot());
            mutate(next);
            next.AutomaticSelectionExcludedTools =
                CaptureToolIds.Normalize(next.AutomaticSelectionExcludedTools);
            next.QuickSelectionDisabledTools =
                CaptureToolIds.Normalize(next.QuickSelectionDisabledTools);
            _cached = Clone(next);
        }

        try
        {
            SettingsStore.Modify(ConfigStore.Anchor, file =>
            {
                file.Drawing = Clone(next);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.DrawingSettings", "保存绘制设置失败：" + ex.Message);
        }
    }

    private static DrawingFile Clone(DrawingFile file) => new()
    {
        AutomaticallySelectNewlyCreatedElements = file.AutomaticallySelectNewlyCreatedElements,
        AutomaticSelectionExcludedTools =
            new List<string>(file.AutomaticSelectionExcludedTools ?? new List<string>()),
        QuickSelectionDisabledTools =
            new List<string>(file.QuickSelectionDisabledTools ?? new List<string>()),
        DeleteTextAfterCursor = file.DeleteTextAfterCursor,
        RememberLastUsedTool = file.RememberLastUsedTool,
        AlwaysShowFirstToolbarGroupButton = file.AlwaysShowFirstToolbarGroupButton,
    };
}
