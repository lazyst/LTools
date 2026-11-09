using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;
using LTools.Features;

namespace LTools.Config;

/// <summary>
/// 应用配置的强类型模型 + System.Text.Json 读写（内存里的聚合视图）。
/// 磁盘上是 <c>config/</c> 下的 6 个域文件（settings / ui / actions / panel / floating / macros），
/// 读写细节见 <see cref="ConfigBundle"/>；分域前是单文件 <c>LTools.json</c>（首次保存后归档为
/// <c>.migrated</c>）。发布包只携带 <c>LTools.example.json</c>，升级解压不会覆盖用户配置。
/// </summary>
public sealed class MenuGroupDto
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";

    /// <summary>菜单项为动作 Id 引用列表（对应 <see cref="AppConfig.Actions"/>）。</summary>
    public List<string> Items { get; set; } = new();
}

/// <summary>超级面板配置（计划 §5）。</summary>
public sealed class SuperPanelConfig
{
    /// <summary>独立全局开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>长按阈值（ms，可调 100–800）。</summary>
    public int LongPressThresholdMs { get; set; } = 250;

    /// <summary>每页 16 槽（4×4），元素为动作 Id 或 null（空格）。</summary>
    public List<List<string?>> Pages { get; set; } = new();

    /// <summary>上次关闭时的页下标（0-based，运行时自动记忆，下次唤起停在该页）。</summary>
    public int LastPage { get; set; } = 0;
}

/// <summary>键鼠宏录制配置（见 <c>Features/MacroRecorder.cs</c>）。</summary>
public sealed class MacroConfig
{
    /// <summary>总开关：关闭后录制热键完全不介入。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>停止录制热键（如 <c>ctrl+f2</c>，仅在录制期间临时注册）。</summary>
    public string RecordHotkey { get; set; } = "ctrl+f2";

    /// <summary>停止宏回放热键（如 <c>ctrl+f3</c>，仅在回放期间注册）。</summary>
    public string PlaybackStopHotkey { get; set; } = "ctrl+f3";

    /// <summary>回放前倒计时的全局默认值（ms，0 = 立即回放；宏可单独覆盖）。</summary>
    public int CountdownMs { get; set; } = 0;

    /// <summary>鼠标移动最小采样间隔（ms）。</summary>
    public int MouseSampleMs { get; set; } = 8;

    /// <summary>鼠标移动最小位移（px）。</summary>
    public int MouseSamplePx { get; set; } = 1;

    /// <summary>单个宏最大事件数。</summary>
    public int MaxEvents { get; set; } = 200_000;

    /// <summary>回放速度倍率的全局默认值（宏可单独覆盖）。</summary>
    public double DefaultSpeed { get; set; } = 1.0;

    /// <summary>回放循环次数的全局默认值（1 = 只回放一次，1–99；宏可单独覆盖或设为无限）。</summary>
    public int Loops { get; set; } = 1;

    /// <summary>上次录制的宏 Id。</summary>
    public string? LastMacroId { get; set; }

    /// <summary>宏元数据清单（事件本体在 <c>macros/&lt;id&gt;.json</c>）。</summary>
    public List<MacroMeta> Items { get; set; } = new();
}

/// <summary>运行日志配置（见 <see cref="AppLog"/>）：写入数据根下 <c>logs/</c>，按天分文件。</summary>
public sealed class LogConfig
{
    /// <summary>是否记录运行日志（默认开启，便于用户 / agent 排查问题）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>记录级别：error / warn / info / debug（默认 info）。</summary>
    public LogLevel Level { get; set; } = LogLevel.info;

    /// <summary>日志保留天数（按文件修改时间清理；0 = 不自动清理）。</summary>
    public int RetentionDays { get; set; } = 14;
}

public sealed class AppConfig
{
    /// <summary>
    /// 3 及更早版本的「CapsLock 快捷菜单」组（只读：启动迁移会把它并成 hotkeys 里的
    /// 一个「旧菜单」预设组，之后不再写回）。保留字段只是为了让旧 <c>LTools.json</c> 还能当迁移种子。
    /// </summary>
    public List<MenuGroupDto?> MenuGroups { get; set; } = new();

    /// <summary>全局快捷键（预设组 + 绑定 + 默认名单），落在 <c>configs/hotkeys.json</c>。</summary>
    public HotkeyFile Hotkeys { get; set; } = new();

    /// <summary>全局动作清单（计划 §2）。超级面板与菜单组都引用这里的 Id。</summary>
    public List<ActionDto> Actions { get; set; } = new();

    /// <summary>超级面板配置（旧：页 + 槽位；仅用于迁移，见 <see cref="Panel"/>）。</summary>
    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>
    /// 动作面板配置（全局区 + 上下文区）。为 null = 尚未从
    /// <see cref="SuperPanelConfig.Pages"/> 迁移；迁移由 <c>Features.Panel.PanelLibrary</c> 负责。
    /// </summary>
    public PanelConfig? Panel { get; set; }

    /// <summary>悬浮动作 / 悬浮分组（桌面上的置顶按钮，设计见 <c>docs/FLOATING.md</c>）。</summary>
    public FloatingConfig? Floating { get; set; }

    /// <summary>键鼠宏录制配置。</summary>
    public MacroConfig Macros { get; set; } = new();

    /// <summary>运行日志配置。</summary>
    public LogConfig Logging { get; set; } = new();

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>
    /// CapsLock 键功能总开关（CapsLock 修饰键 + 快捷键 / 鼠标手势等）。
    /// 与 <see cref="SuperPanel.Enabled"/> 相互独立，均可在设置「通用」页或托盘菜单切换。
    /// </summary>
    public bool CapsLockEnabled { get; set; } = true;

    /// <summary>设置窗口上次所在的导航页（general/actions/hotkeys/superpanel/terminals/logs/backup），下次打开恢复。</summary>
    public string LastNavPage { get; set; } = "actions";

    /// <summary>
    /// 设置左栏「二级下拉导航」中处于展开状态的父项（<c>superpanel</c> / <c>ai</c>）。
    /// 页面本身由 <see cref="LastNavPage"/> 记；启动时当前页所在组一定会展开（否则看不出在哪个子页）。
    /// </summary>
    public List<string>? NavExpandedGroups { get; set; }

    /// <summary>
    /// 上次选用的「文字识别」引擎（<c>windows</c> / <c>v6tiny</c> / <c>v6small</c>），
    /// 新建 OCR 取值默认用它；null = 还没选过（默认内置的 PP-OCRv6 快速）。见 <c>Features/OcrEnginePreference</c>。
    /// </summary>
    public string? LastOcrEngine { get; set; }

    /// <summary>
    /// 动作编辑器左栏「添加单元」里已折叠的分类名（如 <c>流程</c> / <c>剪贴板</c>）。
    /// **全局记忆**：换一个动作编辑、重开编辑器都沿用；只写 <c>configs/ui.json</c>。
    /// </summary>
    public List<string> ToolboxCollapsedGroups { get; set; } = new();

    /// <summary>
    /// 动作选择器（<c>ActionPoolPicker</c>）里「最近使用」的动作 Id，最新的在前，最多 6 条。
    /// 只在确定选择时写入（见选择器的 <c>SaveRecent</c>）。
    /// </summary>
    public List<string>? RecentActionIds { get; set; }

    /// <summary>
    /// 动作运行结束后的提示策略：不提示 / 只在失败时（默认）/ 成功也提示。
    /// 失败提示是常驻的（点一下关，下一次运行自动替换）。
    /// </summary>
    public RunNoticeMode RunNotice { get; set; } = RunNoticeMode.onFailure;

    /// <summary>「提示弹窗」没指定位置时的默认落点（右下角，与系统通知一致）。</summary>
    public ToastPosition ToastPosition { get; set; } = ToastPosition.bottomRight;

    /// <summary>
    /// 本次加载是否因用户配置不可读 / 损坏而回退到默认值。回退值只用于本次运行，
    /// 写盘前必须重新用 <see cref="LoadStrict"/> 读取，避免覆盖用户原文件。
    /// </summary>
    [JsonIgnore]
    public bool LoadFailed { get; internal set; }

    /// <summary>读失败（含被占用 / 共享冲突）时的重试次数与间隔——开机自启瞬间配置常被 AV / 索引器短暂占用。</summary>
    private const int ReadRetries = 15;
    private const int ReadRetryDelayMs = 200;

    /// <summary>
    /// 加载配置：合并 <c>configs/</c> 下的 6 个域文件（路径按 <paramref name="path"/> 同级的 <c>configs/</c> 解析）。
    /// 域文件不存在时从旧 <c>LTools.json</c> / 同目录示例回填；已有文件读失败（占用）会退避重试，
    /// 仍失败则按域回退默认值并置 <see cref="LoadFailed"/>——**写盘必须走 <see cref="LoadStrict"/>**
    /// （<see cref="ConfigIO"/> 与各域 store 都已如此），绝不把回退值写回磁盘。
    /// </summary>
    public static AppConfig Load(string path) => ConfigBundle.Load(path);

    /// <summary>
    /// 严格加载（写配置前使用）：**已存在**的域文件无法解析时抛出，不回退默认值。
    /// 这样一次读取失败不会让后续「读-改-写」把用户的动作清单覆盖成默认值。
    /// </summary>
    public static AppConfig LoadStrict(string path) => ConfigBundle.LoadStrict(path);

    /// <summary>
    /// 清理不可读配置留下的 <c>.bad-*</c> 现场副本（旧单文件 + 每个域文件，各留最近 3 份）。
    /// </summary>
    internal static void PruneBrokenBackups(string path, int keep = 3) =>
        ConfigBundle.PruneAllBrokenBackups(path, keep);

    /// <summary>保存配置：逐域写回，内容没变的文件不动；6 个域文件齐全后把旧单文件归档。</summary>
    public void Save(string path) => ConfigBundle.Save(path, this);

    /// <summary>
    /// 从旧版 CapsLock++.ini 迁移（仅首次升级时由 <see cref="Core.ConfigLocator"/> 调用）。
    /// 把每个 ini 菜单项转为一个 runCommand 动作（保留 Terminal/Cmd/KeepWindow/Workdir），
    /// 菜单组 Items 存生成的动作 Id。依赖 IniFile 的逐节读取。
    /// </summary>
    public static AppConfig MigrateFromIni(string iniPath)
    {
        var cfg = new AppConfig();
        int seq = 0;
        for (int i = 1; i <= 10; i++)
        {
            string enabled = IniFile.ReadValue(iniPath, "MenuGroupsEnable", "enableGroup" + i) ?? "true";
            if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) { cfg.MenuGroups.Add(null); continue; }

            string name = IniFile.ReadValue(iniPath, "MenuGroupName", "name" + i) ?? ("菜单组 " + i);
            int count = int.TryParse(IniFile.ReadValue(iniPath, "MenuGroupCount", "count" + i), out var c) ? c : 0;
            var items = new List<string>();
            string section = "MenuGroups" + i + "Items";
            for (int j = 1; j <= count; j++)
            {
                string iname = IniFile.ReadValue(iniPath, section, "name" + j) ?? "";
                if (iname.Length == 0) continue;
                string id = "m" + (++seq);
                cfg.Actions.Add(new ActionDto
                {
                    Id = id,
                    Name = iname,
                    Type = ActionType.composite,
                    LegacyType = "runCommand",
                    Cmd = IniFile.ReadValue(iniPath, section, "action" + j) ?? "",
                    Terminal = IniFile.ReadValue(iniPath, section, "terminal" + j) ?? "direct",
                    KeepWindow = (IniFile.ReadValue(iniPath, section, "keepwindow" + j) ?? "false")
                        .Equals("true", StringComparison.OrdinalIgnoreCase),
                    Workdir = IniFile.ReadValue(iniPath, section, "workdir" + j) ?? "",
                });
                items.Add(id);
            }
            cfg.MenuGroups.Add(new MenuGroupDto { Enabled = true, Name = name, Items = items });
        }

        var gb = IniFile.ReadValue(iniPath, "TerminalPaths", "gitbash")?.Trim();
        if (!string.IsNullOrEmpty(gb)) cfg.TerminalPaths["gitbash"] = gb;

        return cfg;
    }
}
