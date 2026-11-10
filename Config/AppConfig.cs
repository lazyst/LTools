using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;
using LTools.Features;

namespace LTools.Config;

/// <summary>超级面板配置（计划 §5）。</summary>
public sealed class SuperPanelConfig
{
    /// <summary>独立全局开关。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>长按阈值（ms，可调 100–800）。</summary>
    public int LongPressThresholdMs { get; set; } = 250;
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

/// <summary>
/// 应用配置的强类型模型（内存里的聚合视图 + <c>LTools.example.json</c> 的种子形状）。
/// 磁盘上按域分成 <c>configs/*.json</c>，读写细节见 <see cref="ConfigBundle"/>；
/// 发布包只携带示例文件，升级解压不会覆盖用户配置。
/// </summary>
public sealed class AppConfig
{
    /// <summary>全局快捷键（预设组 + 绑定 + 默认名单），落在 <c>configs/hotkeys.json</c>。</summary>
    public HotkeyFile Hotkeys { get; set; } = new();

    /// <summary>全局动作清单（计划 §2）。面板 / 快捷键 / 悬浮都引用这里的 Id。</summary>
    public List<ActionDto> Actions { get; set; } = new();

    /// <summary>超级面板开关与长按阈值（面板结构本身见 <see cref="Panel"/>）。</summary>
    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>动作面板配置（全局区 + 上下文区）。</summary>
    public PanelConfig? Panel { get; set; }

    /// <summary>悬浮动作 / 悬浮分组（桌面上的置顶按钮，设计见 <c>docs/FLOATING.md</c>）。</summary>
    public FloatingConfig? Floating { get; set; }

    /// <summary>键鼠宏录制配置。</summary>
    public MacroConfig Macros { get; set; } = new();

    /// <summary>自动触发规则（<c>configs/triggers.json</c>，见 <c>docs/AUTO_TRIGGER.md</c>）。</summary>
    public TriggersFile Triggers { get; set; } = new();

    /// <summary>运行日志配置。</summary>
    public LogConfig Logging { get; set; } = new();

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>
    /// CapsLock 当快捷键修饰键：轻按发 Esc，按住配合其它键（<c>CapsLock+字母</c> 等）触发快捷键。
    /// 关闭后 CapsLock 走原生大小写切换，带 CapsLock 前缀的绑定也随之不匹配。
    /// 与 <see cref="SuperPanel.Enabled"/> 相互独立。
    /// </summary>
    public bool CapsLockModifier { get; set; } = true;

    /// <summary>鼠标停在屏幕边缘（<see cref="EdgeWheelVolumeEdge"/>）时滚轮调系统音量。</summary>
    public bool EdgeWheelVolume { get; set; } = true;

    /// <summary>边缘滚轮音量算哪条边（底部 / 顶部 / 上下）。</summary>
    public WheelVolumeEdge EdgeWheelVolumeEdge { get; set; } = WheelVolumeEdge.bottom;

    /// <summary>设置窗口上次所在的导航页（general/actions/subactions/hotkeys/macros/triggers/superpanel/terminals/logs/ai/backup，或 superpanel-* / ai-* 具体子页），下次打开恢复。</summary>
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
    /// 加载配置：合并 <c>configs/</c> 下的 7 个域文件（路径按 <paramref name="path"/> 同级的 <c>configs/</c> 解析）。
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
    /// 清理不可读配置留下的 <c>.bad-*</c> 现场副本（每个域文件各留最近 3 份）。
    /// </summary>
    internal static void PruneBrokenBackups(string path, int keep = 3) =>
        ConfigBundle.PruneAllBrokenBackups(path, keep);

    /// <summary>保存配置：逐域写回，内容没变的文件不动。</summary>
    public void Save(string path) => ConfigBundle.Save(path, this);
}
