using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;
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

    /// <summary>
    /// 只记路径，不加载动作 / 快捷键 / 面板、也不做修复（给共享助手进程用）。
    /// </summary>
    /// <remarks>
    /// 助手只读写 settings 域里的 Share 段（SettingsStore 每次现读现写）。它要是也跑一遍
    /// Initialize，就会在主程序之外再扫一遍动作目录、修引用、播种快捷键——两个进程同时改同一批
    /// 文件是没必要的风险。
    /// </remarks>
    internal static void UsePathOnly(string configPath) => ConfigPath = configPath;

    /// <summary>
    /// 只读初始化（普通权限宿主用）：加载动作库 / 面板 / 快捷键 / 宏元数据，**不修引用、不播种快捷键、
    /// 不清 trash、不写任何文件**；之后的 <see cref="Save"/> 会被拒绝（写入归主程序）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="UsePathOnly"/> 的区别：那个只记路径（共享助手只需要 settings 域）；
    /// 这个要真把面板 / 动作 / 快捷键读进内存（宿主显示面板要用），但仍然一个字都不写。
    /// 见 <c>docs/PROCESS_SPLIT.md</c> §7 的「一个文件一个写者」。
    /// </remarks>
    internal static void InitializeReadOnly(string configPath)
    {
        ConfigPath = configPath;
        ActionStore.Anchor = configPath;

        AppConfig cfg = AppConfig.Load(configPath);
        ActionLibrary.LoadFrom(cfg);
        HotkeyRegistry.Load(cfg);
        // 宿主也跑截图 / 录屏：延时秒数、录屏默认开关、导出场景这些「运行期缓存」要跟提权进程同源；
        // 宿主自己写完 settings.json 后再调一次（见 DataHost）。
        AppState.ApplySettings(cfg);
        PanelLibrary.LoadReadOnly(configPath);
        MacroStore.Invalidate();   // 下一次访问按 ConfigPath 现读（只读）
        SubActionStore.Invalidate();   // 导入备份会整份换掉 subactions/（编辑器将来在宿主里读）
        ReadOnly = true;
    }

    /// <summary>本进程是不是只读（宿主）：<see cref="Save"/> 直接拒绝，避免两个进程抢着写。</summary>
    public static bool ReadOnly { get; private set; }

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
        ApplyRuntime(cfg);
        PanelLibrary.Reload();
        NotifyChanged();

        // 谁是写者，就通知另一边重读：宿主写完让执行器刷新运行期状态，
        // 执行器整份换过（导入备份 / 重新加载）让宿主的只读副本作废。
        if (AppRole.IsUiHost) ConfigChangedBridge.Notify("reload");
        else ConfigReloadBridge.Notify();
    }

    /// <summary>
    /// 宿主写过 <c>configs/</c>（<c>config.changed</c>）：执行器从磁盘重读运行期状态。
    /// </summary>
    /// <remarks>
    /// 只刷「执行器自己那份运行期状态」，**不碰面板**（那走 <c>panel.replace</c>）也不再回头通知宿主
    /// （否则就是两个进程互相叫对方重读）。读盘在调它的 UI 线程上，几十毫秒，不影响输入。
    /// </remarks>
    internal static void ReloadRuntime()
    {
        ApplyRuntime(AppConfig.Load(ConfigPath));
        NotifyChanged();
    }

    /// <summary>把一份配置灌进运行期状态（读盘 / 首装 / 只读初始化共用的那几步）。</summary>
    private static void ApplyRuntime(AppConfig cfg)
    {
        ActionLibrary.LoadFrom(cfg);
        HotkeyRegistry.Load(cfg);          // 钩子线程下一次按键就用新组合键
        AppState.ApplySettings(cfg);       // 延时 / 录屏默认 / 导出场景 / 托盘双击…
        TriggerService.Reload(cfg.Triggers ?? new TriggersFile());
        MacroStore.Invalidate();           // 宏元数据（macros.json）可能整个换过
        MacroSettings.Load(cfg);
    }

    /// <summary>加载当前磁盘配置（不回写）。</summary>
    public static AppConfig LoadConfig() => AppConfig.Load(ConfigPath);

    /// <summary>保存动作清单（只写 actions/）。只读进程（宿主）里直接拒绝。</summary>
    public static void Save()
    {
        if (ReadOnly)
        {
            AppLog.Warn("Config", "宿主进程试图写动作清单，已拒绝（写入归主程序）");
            return;
        }

        ActionLibrary.Save(ConfigPath);
    }

    // —— 动作 CRUD 与变更广播 ——

    /// <summary>
    /// 动作 / 宏数据在设置窗口之外被改动（面板删除、录制完成、AI 采纳、删宏…）。
    /// 一次业务操作只发一次，别在每次落盘 / 切页时发（否则又回到「切页整表重建」）。
    /// 订阅者自行切到 UI 线程；本事件可能从后台线程触发。
    /// </summary>
    internal static event Action? Changed;

    /// <summary>业务操作完成后发一次变更通知（可能在后台线程，订阅端必须走 <c>Ui.Post</c>）。</summary>
    public static void NotifyChanged()
    {
        ActionReferenceIndex.Invalidate();
        Changed?.Invoke();
    }

    /// <summary>新建一个动作（自动生成 Id、图标默认为 app）。</summary>
    public static bool AddAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) action.Id = ActionRegistry.NextId();
        if (string.IsNullOrEmpty(action.Name)) return false;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        ActionLibrary.Add(action);
        NotifyChanged();
        return true;
    }

    /// <summary>用传入的 ActionDto 替换清单中 Id 相同的动作（保留 Id）。</summary>
    public static void UpdateAction(ActionDto action)
    {
        if (string.IsNullOrEmpty(action.Id)) return;
        if (string.IsNullOrEmpty(action.Icon)) action.Icon = IconCatalog.Default;
        ActionLibrary.Replace(action);
        NotifyChanged();
    }

    /// <summary>从清单删除动作（同时清掉快捷键 / 面板 / 触发规则 / 悬浮引用，并回收宏与模板图）。</summary>
    /// <remarks>用 finally 保证通知在 <see cref="ActionRemoval.Remove"/> 全部收尾（含宏回收）之后发出，
    /// 订阅者读到的才是新数据；回收中途抛异常也别让已建设置页停在旧快照上。</remarks>
    public static bool RemoveAction(string id)
    {
        try { return ActionRemoval.Remove(id); }
        finally { NotifyChanged(); }
    }

    /// <summary>复制动作（新 Id + 名称加「副本」，模板图 / 脚本一并带走）。</summary>
    /// <remarks>
    /// 只复制动作本体：快捷键绑定、悬浮项、触发规则都不跟过去——副本是一份新动作，
    /// 跟着绑会让一个键触发两个动作、桌面上冒出两个一模一样的悬浮按钮。
    /// </remarks>
    public static ActionDto? DuplicateAction(string id)
    {
        var copy = ActionDuplicator.Duplicate(ConfigPath, id, ActionRegistry.All.Select(a => a.Name));
        if (copy == null) return null;
        ActionLibrary.Add(copy);
        NotifyChanged();
        return copy;
    }
}
