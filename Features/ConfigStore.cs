using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Storage;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Features;

/// <summary>
/// 设置面板的统一配置读写入口（阶段 3）：所有管理页（动作 / 菜单组 / 超级面板 / 终端路径）
/// 都经由此读写 <see cref="AppConfig"/> 与 <see cref="ActionRegistry"/>。
/// </summary>
/// <remarks>
/// 每次保存都做「Load 当前磁盘状态 → 修改 → Save 整体」，避免多个页 / 对话框并发编辑时
/// 互相覆盖（例：菜单组页保存只写 MenuGroups，会冲掉动作清单里用户新建的动作）。
/// 因此本类与 <see cref="MenuSystem.SaveToConfig"/> 都会把 MenuGroups + Actions 一起落盘。
/// </remarks>
internal static class ConfigStore
{
    /// <summary>当前配置路径（ConfigLocator 解析结果）。</summary>
    public static string ConfigPath { get; private set; } = "";

    /// <summary>加载配置路径并强制刷新 MenuSystem（动作清单 / 菜单组 / 终端路由）与面板库。</summary>
    public static void Initialize(string configPath)
    {
        ConfigPath = configPath;
        // 运行时找动作私有资源（actions/<id>/files）要这条锚点；顺手清理过期的 trash。
        ActionStore.Anchor = configPath;
        ActionStore.PruneTrash(configPath);
        // 外部编辑 / 备份导入 / 中途崩溃可能留下指向已删动作的菜单项与面板槽位：
        // 先按磁盘动作目录修好，再让两个库加载，内存里拿到的就是干净数据。
        ActionRemoval.RepairReferences(configPath);
        MenuSystem.ReloadFromConfig(configPath);
        PanelLibrary.Initialize(configPath);
        // 旧面板刚迁移出来时 RepairReferences 还看不到（Panel 为 null），按内存动作清单再收一次。
        PanelLibrary.RemoveMissingActions(
            new HashSet<string>(ActionRegistry.All.Select(a => a.Id), StringComparer.Ordinal));
        // 动作目录里没人引用的模板图移进 trash（动态引用的动作整个跳过，只搬不删）。
        OrphanFiles.Collect(configPath, ActionRegistry.All);
    }

    /// <summary>重新从磁盘加载（配置已被外部对话框改写后调用）。</summary>
    public static void Reload()
    {
        MenuSystem.ReloadFromConfig(ConfigPath);
        PanelLibrary.Reload();
    }

    /// <summary>加载当前磁盘配置（不回写）。</summary>
    public static AppConfig LoadConfig() => AppConfig.Load(ConfigPath);

    /// <summary>保存当前内存状态（菜单组 + 动作清单）到磁盘。</summary>
    public static void Save() => MenuSystem.SaveToConfig(ConfigPath);

    // —— 动作 CRUD ——

    /// <summary>新建一个动作（自动生成 Id、图标默认为 app）。</summary>
    public static bool AddAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) action.Id = ActionRegistry.NextId();
        if (string.IsNullOrEmpty(action.Name)) return false;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        MenuSystem.AddAction(action);
        return true;
    }

    /// <summary>用传入的 ActionDto 替换清单中 Id 相同的动作（保留 Id）。</summary>
    public static void UpdateAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) return;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        MenuSystem.ReplaceAction(action);
    }

    /// <summary>从清单删除动作（同时清掉菜单项 / 组合步骤 / 超级面板槽位三处引用）。</summary>
    public static bool RemoveAction(string id) => ActionRemoval.Remove(id);

    /// <summary>复制动作（新 Id + 名称追加「副本」）。</summary>
    public static ActionDto? DuplicateAction(string id)
    {
        var src = MenuSystem.FindAction(id);
        if (src == null) return null;
        // 用 Clone 深拷贝全部字段（含 Text / Mode / AppendEnter / Items / MacroId），
        // 避免新增字段时这里漏抄导致复制出的动作丢配置。
        var copy = src.Clone();
        copy.Id = ActionRegistry.NextId();
        copy.Name = src.Name + " 副本";
        if (string.IsNullOrEmpty(copy.Icon)) copy.Icon = IconCatalog.Default;
        MenuSystem.AddAction(copy);
        return copy;
    }

    // —— 终端路径 ——

    /// <summary>保存 Git Bash 路径覆盖（空字符串=恢复自动探测）。</summary>
    public static void SaveGitBashPath(string gitBashPath)
    {
        var p = string.IsNullOrWhiteSpace(gitBashPath) ? null : gitBashPath.Trim();
        SettingsStore.Modify(ConfigPath, s =>
        {
            if (p == null) s.TerminalPaths.Remove("gitbash");
            else s.TerminalPaths["gitbash"] = p;
        });
        TerminalLauncher.LoadFromConfig(AppConfig.Load(ConfigPath));
    }
}
