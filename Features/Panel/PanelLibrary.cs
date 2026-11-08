using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.Panel;

/// <summary>
/// 动作面板配置的加载 / 迁移 / 保存与结构操作（设计见 <c>docs/PANEL_REDESIGN.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 面板窗口与设置「面板管理」页共用同一个 <see cref="Current"/> 实例：编辑它之后调用
/// <see cref="Save"/>，整份 <c>Panel</c> 经 <see cref="PanelStore"/> 落盘（只写 <c>configs/panel.json</c>）。动作本体不在
/// 这里，仍由 <see cref="ConfigStore"/> / <see cref="ActionRegistry"/> 管理。
/// </para>
/// <para>
/// <see cref="PanelStore"/> 的读改写用 <see cref="AppConfig.LoadStrict"/> 读盘，用户配置不可解析时
/// 会抛错并放弃写入，因此迁移 / 保存都不会覆盖坏掉的配置文件。
/// </para>
/// </remarks>
internal static class PanelLibrary
{
    private const string LogSource = "Panel";
    private static string _path = "";

    /// <summary>当前内存配置（永远非 null）。</summary>
    public static PanelConfig Current { get; private set; } = new();

    /// <summary>绑定配置路径并加载（启动时由 <see cref="ConfigStore.Initialize"/> 调用）。</summary>
    public static void Initialize(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>重新从磁盘加载；旧结构（<see cref="AppConfig.Panel"/> 为 null）就地迁移并落盘。</summary>
    public static void Reload()
    {
        AppConfig cfg;
        TryLoad(out cfg);

        // 读取失败已回退默认值：保留当前内存态，别把默认面板写回磁盘
        if (cfg.LoadFailed)
        {
            AppLog.Warn(LogSource, "配置读取失败，面板保留当前内存状态");
            return;
        }

        if (cfg.Panel == null)
        {
            cfg.Panel = MigrateFromLegacy(cfg);
            Current = cfg.Panel;
            AppLog.Info(LogSource, "已把旧的超级面板页迁移为全局场景分组");
            Persist();
        }
        else
        {
            Current = cfg.Panel;
            Normalize(Current);
        }
    }

    /// <summary>把内存配置整份写回磁盘。失败只记日志，不阻塞 UI。</summary>
    public static bool Save() => Persist();

    /// <summary>前台进程是否在「禁止弹出」列表里。</summary>
    public static bool IsBlocked(string? exeName, string? exePath)
    {
        foreach (string raw in Current.Blocked)
        {
            string v = raw.Trim();
            if (v.Length == 0) continue;
            if (!string.IsNullOrEmpty(exeName) && v.Equals(exeName, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(exePath) && v.Equals(exePath, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // —— 场景 / 分组查找 ——

    /// <summary>全局场景列表。</summary>
    public static IReadOnlyList<PanelSceneDto> GlobalScenes => Current.Global.Scenes;

    /// <summary>上下文场景列表。</summary>
    public static IReadOnlyList<PanelSceneDto> ContextScenes => Current.Context.Scenes;

    /// <summary>按 Id 在全局 / 上下文里找场景。</summary>
    public static PanelSceneDto? FindScene(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return Current.Global.Scenes.FirstOrDefault(s => s.Id == id)
               ?? Current.Context.Scenes.FirstOrDefault(s => s.Id == id);
    }

    /// <summary>某场景的直接子场景。</summary>
    public static IEnumerable<PanelSceneDto> ChildrenOf(string sceneId) =>
        Current.Context.Scenes.Where(s => s.ParentId == sceneId);

    /// <summary>当前选中的全局场景（失效则退回第一个）。</summary>
    public static PanelSceneDto? ActiveGlobalScene()
    {
        var scenes = Current.Global.Scenes;
        var s = scenes.FirstOrDefault(x => x.Id == Current.Global.ActiveSceneId);
        if (s == null && scenes.Count > 0) s = scenes[0];
        return s;
    }

    /// <summary>新建一个空场景 Id。</summary>
    public static string NewSceneId() => "s" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>新建一个空分组 Id。</summary>
    public static string NewGroupId() => "g" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>创建一个空分组。</summary>
    public static PanelGroupDto NewGroup(string name = "未分组") =>
        new() { Id = NewGroupId(), Name = name, Actions = new List<string>() };

    /// <summary>创建一个空场景。</summary>
    public static PanelSceneDto NewScene(string name, string icon = "app") => new()
    {
        Id = NewSceneId(),
        Name = string.IsNullOrWhiteSpace(name) ? "未命名场景" : name.Trim(),
        Icon = string.IsNullOrEmpty(icon) ? "app" : icon,
        Groups = new List<PanelGroupDto> { NewGroup() },
    };

    /// <summary>为前台程序创建一个上下文场景（含 exe / 完整路径匹配与一个空分组）。</summary>
    public static PanelSceneDto CreateContextScene(string name, string? exeName, string? exePath)
    {
        var scene = NewScene(name);
        scene.Match.Exe = string.IsNullOrWhiteSpace(exeName) ? null : exeName.Trim();
        scene.Match.Path = string.IsNullOrWhiteSpace(exePath) ? null : exePath.Trim();
        Current.Context.Scenes.Add(scene);
        Current.Context.ActiveSceneId = scene.Id;
        Save();
        return scene;
    }

    /// <summary>删除场景；父场景会连带删除子场景。同时修掉失效的选中项。</summary>
    public static void DeleteScene(PanelSceneDto scene)
    {
        Current.Context.Scenes.RemoveAll(s => s.Id == scene.Id || s.ParentId == scene.Id);
        Current.Global.Scenes.RemoveAll(s => s.Id == scene.Id);
        if (Current.Global.ActiveSceneId == scene.Id) Current.Global.ActiveSceneId = null;
        if (Current.Context.ActiveSceneId == scene.Id) Current.Context.ActiveSceneId = null;
        Normalize(Current);
        Save();
    }

    /// <summary>把动作从所有面板分组里移除（删除动作时调用）。</summary>
    public static void RemoveActionReferences(string actionId)
    {
        bool changed = false;
        foreach (var group in AllGroups())
        {
            while (group.Actions.Remove(actionId)) changed = true;
        }
        if (changed) Save();
    }

    /// <summary>
    /// 启动时清理指向不存在动作的槽位（外部编辑 / 备份导入 / 中途崩溃留下的悬空引用）。
    /// 没有变化就不写盘；返回是否改动过。
    /// </summary>
    public static bool RemoveMissingActions(ISet<string> liveIds)
    {
        if (!PruneMissing(Current, liveIds)) return false;
        Save();
        AppLog.Info(LogSource, "已清掉超级面板里指向不存在动作的槽位");
        return true;
    }

    /// <summary>就地删掉某个面板配置里指向不存在动作的槽位；返回是否有改动（不写盘）。</summary>
    public static bool PruneMissing(PanelConfig panel, ISet<string> liveIds)
    {
        bool changed = false;
        foreach (var group in GroupsOf(panel))
        {
            if (group.Actions.RemoveAll(id => !liveIds.Contains(id)) > 0) changed = true;
        }
        return changed;
    }

    /// <summary>所有分组（全局 + 上下文场景 + 上下文兜底）。</summary>
    public static IEnumerable<PanelGroupDto> AllGroups() => GroupsOf(Current);

    /// <summary>某个面板配置里的所有分组（全局 + 上下文场景 + 上下文兜底）。</summary>
    public static IEnumerable<PanelGroupDto> GroupsOf(PanelConfig panel)
    {
        foreach (var s in panel.Global.Scenes)
            foreach (var g in s.Groups) yield return g;
        foreach (var s in panel.Context.Scenes)
            foreach (var g in s.Groups) yield return g;
        foreach (var g in panel.Context.Common) yield return g;
    }

    /// <summary>某个动作被哪些面板位置引用。</summary>
    public sealed record PanelReference(string Area, string? SceneId, string SceneName, string GroupId, string GroupName);

    /// <summary>查出动作的所有引用位置。</summary>
    public static List<PanelReference> FindReferences(string actionId)
    {
        var list = new List<PanelReference>();
        foreach (var s in Current.Global.Scenes)
            foreach (var g in s.Groups)
                if (g.Actions.Contains(actionId))
                    list.Add(new PanelReference("全局", s.Id, s.Name, g.Id, g.Name));
        foreach (var s in Current.Context.Scenes)
            foreach (var g in s.Groups)
                if (g.Actions.Contains(actionId))
                    list.Add(new PanelReference("上下文", s.Id, s.Name, g.Id, g.Name));
        foreach (var g in Current.Context.Common)
            if (g.Actions.Contains(actionId))
                list.Add(new PanelReference("上下文", null, "通用", g.Id, g.Name));
        return list;
    }

    /// <summary>把引用位置拼成一行（用于动作表格 / 删除确认框）。</summary>
    public static string DescribeReferences(string actionId)
    {
        var refs = FindReferences(actionId);
        if (refs.Count == 0) return "未使用";
        return string.Join("；", refs.Select(r => r.SceneId == null
            ? $"{r.Area} · {r.SceneName} · {r.GroupName}"
            : $"{r.Area} · {r.SceneName} · {r.GroupName}"));
    }

    /// <summary>移动磁贴（同一分组内重排 / 跨分组移动）。</summary>
    public static void MoveAction(PanelGroupDto from, int fromIndex, PanelGroupDto to, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= from.Actions.Count) return;
        string id = from.Actions[fromIndex];
        int insert = Math.Clamp(toIndex, 0, to.Actions.Count);

        if (ReferenceEquals(from, to))
        {
            if (fromIndex == insert || fromIndex + 1 == insert) return;
            from.Actions.RemoveAt(fromIndex);
            if (insert > fromIndex) insert--;
            from.Actions.Insert(Math.Clamp(insert, 0, from.Actions.Count), id);
        }
        else
        {
            from.Actions.RemoveAt(fromIndex);
            to.Actions.Insert(insert, id);
        }
        Save();
    }

    // —— 内部 ——

    private static void TryLoad(out AppConfig cfg)
    {
        try
        {
            cfg = AppConfig.Load(_path);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogSource, "读取配置失败，面板使用内存默认值：" + ex.Message);
            cfg = new AppConfig();
        }
    }

    private static bool Persist()
    {
        try
        {
            PanelStore.Modify(_path, p => p.Panel = Current);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogSource, "保存面板配置失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>从旧的「页 + 槽位」生成全局场景。见文档 §6。</summary>
    internal static PanelConfig MigrateFromLegacy(AppConfig cfg)
    {
        var panel = new PanelConfig();
        var scene = new PanelSceneDto
        {
            Id = NewSceneId(),
            Name = "默认",
            Icon = "app",
            Groups = new List<PanelGroupDto>(),
        };

        var pages = cfg.SuperPanel?.Pages ?? new List<List<string?>>();
        if (pages.Count == 0) pages.Add(new List<string?>());

        for (int i = 0; i < pages.Count; i++)
        {
            var ids = (pages[i] ?? new List<string?>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct()
                .ToList();
            if (ids.Count == 0 && pages.Count > 1) continue;   // 多页时空页跳过

            scene.Groups.Add(new PanelGroupDto
            {
                Id = NewGroupId(),
                Name = pages.Count <= 1 ? "未分组" : $"第 {i + 1} 页",
                Actions = ids,
            });
        }

        if (scene.Groups.Count == 0)
            scene.Groups.Add(new PanelGroupDto { Id = NewGroupId(), Name = "未分组" });

        panel.Global.Scenes.Add(scene);
        panel.Global.ActiveSceneId = scene.Id;
        panel.Context.Common.Add(new PanelGroupDto { Id = NewGroupId(), Name = "常用" });
        return panel;
    }

    /// <summary>补齐 Id / 名称 / 分组，去掉悬空的 ParentId 与重复动作。幂等。</summary>
    internal static void Normalize(PanelConfig panel)
    {
        panel.Preferences ??= new PanelPreferencesDto();
        panel.Global ??= new PanelAreaDto();
        panel.Context ??= new PanelAreaDto();
        panel.Blocked ??= new List<string>();

        if (panel.Preferences.Width < 360) panel.Preferences.Width = 560;
        if (panel.Preferences.Height < 340) panel.Preferences.Height = 620;
        if (panel.Preferences.Width > 2400) panel.Preferences.Width = 2400;
        if (panel.Preferences.Height > 2000) panel.Preferences.Height = 2000;

        NormalizeScenes(panel.Global);
        NormalizeScenes(panel.Context);

        // 上下文兜底至少一个分组，否则用户没有地方放动作
        if (panel.Context.Common.Count == 0)
            panel.Context.Common.Add(NewGroup("常用"));
        NormalizeGroups(panel.Context.Common);

        // 修掉悬空的父场景引用
        var ids = panel.Context.Scenes.Select(s => s.Id).ToHashSet();
        foreach (var s in panel.Context.Scenes)
            if (!string.IsNullOrEmpty(s.ParentId) && !ids.Contains(s.ParentId))
                s.ParentId = null;

        if (panel.Global.Scenes.Count > 0
            && panel.Global.Scenes.All(s => s.Id != panel.Global.ActiveSceneId))
            panel.Global.ActiveSceneId = panel.Global.Scenes[0].Id;
    }

    private static void NormalizeScenes(PanelAreaDto area)
    {
        area.Scenes ??= new List<PanelSceneDto>();
        area.Common ??= new List<PanelGroupDto>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in area.Scenes)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !seen.Add(s.Id)) s.Id = NewSceneId();
            if (string.IsNullOrWhiteSpace(s.Name)) s.Name = "未命名场景";
            if (string.IsNullOrWhiteSpace(s.Icon)) s.Icon = "app";
            s.Match ??= new PanelMatchDto();
            s.Groups ??= new List<PanelGroupDto>();
            if (s.Groups.Count == 0) s.Groups.Add(NewGroup());
            NormalizeGroups(s.Groups);
        }
    }

    private static void NormalizeGroups(List<PanelGroupDto> groups)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || !seen.Add(g.Id)) g.Id = NewGroupId();
            if (string.IsNullOrWhiteSpace(g.Name)) g.Name = "未分组";
            g.Actions ??= new List<string>();

            var unique = new List<string>(g.Actions.Count);
            var actionSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in g.Actions)
                if (!string.IsNullOrWhiteSpace(id) && actionSeen.Add(id))
                    unique.Add(id);
            g.Actions = unique;
        }
    }
}
