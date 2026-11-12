using System.Collections.Generic;
using System.Linq;
using LTools.Core;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Features;

/// <summary>
/// 动作管理「引用位置」筛选的唯一索引：一次把面板 / 快捷键 / 悬浮项 / 触发规则里的引用收成
/// 「动作 Id → 筛选键集合」，搜索与下拉切换只在内存里投影，不重新扫配置。
/// <see cref="ConfigStore.NotifyChanged"/> 会让缓存失效，别处改了数据下次自然重算。
/// </summary>
internal static class ActionReferenceIndex
{
    /// <summary>下拉的一行；Key 用于筛选，Label 给人看。</summary>
    public sealed record Option(string Key, string Label);

    public const string All = "all";
    public const string Unused = "unused";
    public const string Hotkey = "hotkey";
    public const string Floating = "floating";
    public const string Trigger = "trigger";

    private const string PanelPrefix = "panel:";

    private static readonly object Gate = new();
    private static Dictionary<string, HashSet<string>>? _cache;

    public static void Invalidate()
    {
        lock (Gate) _cache = null;
    }

    /// <summary>下拉选项：全部 / 未使用 / 每个面板场景·分组 / 三类其它引用。</summary>
    public static IReadOnlyList<Option> Options()
    {
        var list = new List<Option>
        {
            new(All, "全部"),
            new(Unused, "未使用"),
        };

        var panel = PanelLibrary.Current;
        foreach (var s in panel.Global.Scenes)
            foreach (var g in s.Groups)
                list.Add(new Option(PanelKey("全局", s.Id, g.Id), $"全局 · {s.Name} · {g.Name}"));
        foreach (var s in panel.Context.Scenes)
            foreach (var g in s.Groups)
                list.Add(new Option(PanelKey("上下文", s.Id, g.Id), $"上下文 · {s.Name} · {g.Name}"));
        foreach (var g in panel.Context.Common)
            list.Add(new Option(PanelKey("上下文", null, g.Id), $"上下文 · 通用 · {g.Name}"));

        list.Add(new Option(Hotkey, "快捷键"));
        list.Add(new Option(Floating, "悬浮项"));
        list.Add(new Option(Trigger, "触发规则"));
        return list;
    }

    /// <summary>动作 Id → 筛选键集合；没有任何引用的动作带 <see cref="Unused"/>。</summary>
    public static IReadOnlyDictionary<string, HashSet<string>> Map()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;

            var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var hotkeyRefs = HotkeyRegistry.File.Groups
                .SelectMany(g => g.Bindings)
                .Select(b => FeatureRef.IdOf(b.ActionId))
                .ToHashSet(StringComparer.Ordinal);
            var floatRefs = FloatingLibrary.Items
                .SelectMany(i => i.Members)
                .Select(m => FeatureRef.IdOf(m.ActionId))
                .ToHashSet(StringComparer.Ordinal);
            var triggerRefs = TriggerRegistry.Active
                .Select(r => FeatureRef.IdOf(r.ActionRef))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var action in ActionRegistry.All)
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in PanelLibrary.FindReferences(action.Id))
                    keys.Add(PanelKey(r.Area, r.SceneId, r.GroupId));
                if (hotkeyRefs.Contains(action.Id)) keys.Add(Hotkey);
                if (floatRefs.Contains(action.Id)) keys.Add(Floating);
                if (triggerRefs.Contains(action.Id)) keys.Add(Trigger);
                if (keys.Count == 0) keys.Add(Unused);
                map[action.Id] = keys;
            }

            _cache = map;
            return map;
        }
    }

    private static string PanelKey(string area, string? sceneId, string groupId) =>
        PanelPrefix + area + ":" + (sceneId ?? "common") + ":" + groupId;
}
