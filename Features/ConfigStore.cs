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
/// 设置面板的统一配置读写入口：动作管理 / 快捷键 / 面板 / 终端路径都经由此读写
/// <see cref="AppConfig"/>、<see cref="ActionRegistry"/> 与 <see cref="HotkeyRegistry"/>。
/// </summary>
/// <remarks>
/// 动作清单与快捷键各自有属主（<see cref="ActionLibrary"/> 写 <c>actions/</c>，
/// <see cref="HotkeyStore"/> 写 <c>hotkeys.json</c>），这里不再出现「读整份 → 改 → 整份写」。
/// </remarks>
internal static class ConfigStore
{
    /// <summary>当前配置路径（ConfigLocator 解析结果）。</summary>
    public static string ConfigPath { get; private set; } = "";

    /// <summary>加载配置路径并刷新动作清单 / 快捷键 / 面板库。</summary>
    public static void Initialize(string configPath)
    {
        ConfigPath = configPath;
        // 运行时找动作私有资源（actions/<id>/files）要这条锚点；顺手清理过期的 trash。
        ActionStore.Anchor = configPath;
        ActionStore.PruneTrash(configPath);
        // 外部编辑 / 备份导入 / 中途崩溃可能留下指向已删动作的面板槽位与快捷键：先按磁盘修好。
        ActionRemoval.RepairReferences(configPath);

        var cfg = AppConfig.Load(configPath);
        ActionLibrary.LoadFrom(cfg);
        HotkeyRegistry.Load(cfg);
        HotkeySeeding.Ensure(configPath, cfg);

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
        var cfg = AppConfig.Load(ConfigPath);
        ActionLibrary.LoadFrom(cfg);
        HotkeyRegistry.Load(cfg);
        PanelLibrary.Reload();
    }

    /// <summary>加载当前磁盘配置（不回写）。</summary>
    public static AppConfig LoadConfig() => AppConfig.Load(ConfigPath);

    /// <summary>保存动作清单（只写 actions/）。</summary>
    public static void Save() => ActionLibrary.Save(ConfigPath);

    // —— 动作 CRUD ——

    /// <summary>新建一个动作（自动生成 Id、图标默认为 app）。</summary>
    public static bool AddAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) action.Id = ActionRegistry.NextId();
        if (string.IsNullOrEmpty(action.Name)) return false;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        ActionLibrary.Add(action);
        return true;
    }

    /// <summary>用传入的 ActionDto 替换清单中 Id 相同的动作（保留 Id）。</summary>
    public static void UpdateAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) return;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        ActionLibrary.Replace(action);
    }

    /// <summary>从清单删除动作（同时清掉快捷键 / 面板 / 触发规则 / 悬浮引用，并回收宏与模板图）。</summary>
    public static bool RemoveAction(string id) => ActionRemoval.Remove(id);

    /// <summary>复制动作（新 Id + 名称追加「副本」）。</summary>
    public static ActionDto? DuplicateAction(string id)
    {
        var src = ActionLibrary.Find(id);
        if (src == null) return null;
        // 用 Clone 深拷贝全部字段，避免新增字段时这里漏抄导致复制出的动作丢配置。
        var copy = src.Clone();
        copy.Id = ActionRegistry.NextId();
        copy.Name = src.Name + " 副本";
        if (string.IsNullOrEmpty(copy.Icon)) copy.Icon = IconCatalog.Default;
        ActionLibrary.Add(copy);
        return copy;
    }
}
