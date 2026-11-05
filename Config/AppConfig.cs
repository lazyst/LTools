using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;
using LTools.Features;

namespace LTools.Config;

/// <summary>
/// 应用配置（LTools.json）强类型模型 + System.Text.Json 读写。
/// 动作系统 schema（计划 §9）：全局动作清单 <see cref="Actions"/> + 超级面板 <see cref="SuperPanel"/> +
/// 菜单组 <see cref="MenuGroups"/>（项为动作 Id 引用）。无需兼容旧 schema（旧 json 废弃）。
/// 发布包只携带 LTools.example.json；用户首次改动保存后才在同目录生成
/// LTools.json，升级解压不会覆盖用户配置。
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
    /// <summary>菜单组（长度固定 10，下标 0..9 对应 CapsLock+1~0；null=空组）。</summary>
    public List<MenuGroupDto?> MenuGroups { get; set; } = new();

    /// <summary>全局动作清单（计划 §2）。超级面板与菜单组都引用这里的 Id。</summary>
    public List<ActionDto> Actions { get; set; } = new();

    /// <summary>超级面板配置。</summary>
    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>键鼠宏录制配置。</summary>
    public MacroConfig Macros { get; set; } = new();

    /// <summary>运行日志配置。</summary>
    public LogConfig Logging { get; set; } = new();

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>鼠标模式速度 1..20。</summary>
    public int MouseModeSpeed { get; set; } = 1;

    /// <summary>
    /// CapsLock 键功能总开关（CapsLock+Esc 手势 / CapsLock+数字菜单 / 鼠标模式等）。
    /// 与 <see cref="SuperPanel.Enabled"/> 相互独立，均可在设置「通用」页或托盘菜单切换。
    /// </summary>
    public bool CapsLockEnabled { get; set; } = true;

    /// <summary>设置窗口上次所在的导航页（general/actions/menus/superpanel/terminals/logs/backup），下次打开恢复。</summary>
    public string LastNavPage { get; set; } = "actions";

    /// <summary>
    /// 上次选用的「截图 OCR」引擎（<c>windows</c> / <c>v6tiny</c> / <c>v6small</c>），
    /// 新建 OCR 取值默认用它；null = 还没选过（默认内置的 PP-OCRv6 快速）。见 <c>Features/OcrEnginePreference</c>。
    /// </summary>
    public string? LastOcrEngine { get; set; }

    /// <summary>组合动作编辑器左栏已折叠的分组 key（basic / internal / pool），下次打开恢复。</summary>
    public List<string> CompositeCollapsedGroups { get; set; } = new();

    /// <summary>
    /// 本次加载是否因用户配置不可读 / 损坏而回退到示例。回退值只用于本次运行，
    /// 写盘前必须重新用 <see cref="LoadStrict"/> 读取，避免覆盖用户原文件。
    /// </summary>
    [JsonIgnore]
    public bool LoadFailed { get; private set; }

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        // 中文/特殊字符不转义为 \uXXXX，保持配置可读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // 枚举序列化为字符串（ActionType / OnFailStrategy）
        Converters = { new JsonStringEnumConverter() },
        // 省略 null 字段，减少 JSON 噪声（空 Steps、未用的类型特定字段等）
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读失败（含被占用 / 共享冲突）时的重试次数与间隔——开机自启瞬间配置常被 AV / 索引器短暂占用。</summary>
    private const int ReadRetries = 15;
    private const int ReadRetryDelayMs = 200;

    /// <summary>
    /// 加载配置：优先读用户 json；**不存在**时才回退读同目录示例作为默认模板。
    /// 用户配置已存在但读取失败（被占用等瞬时 IO 错误）时退避重试，绝不静默使用示例默认值——
    /// 否则开机自启瞬间的一次短暂占用就会让用户的全部设置"消失"（内存变默认值、界面显示默认），
    /// 且随后的保存还会用默认值覆盖真配置。仅当重试后仍不可读 / 内容损坏时，备份现场再回退示例。
    /// 回退时 <see cref="LoadFailed"/> 为 true：调用方据此提示用户，且写配置必须走
    /// <see cref="LoadStrict"/>（<see cref="ConfigIO.Modify"/> 已如此），绝不把回退值写回磁盘。
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                return ReadWithRetry(path);
            }
            catch (Exception ex)
            {
                // 仍不可读 / 内容损坏：记录 + 备份现场，再回退示例（不静默丢弃用户配置）
                CrashLog.Write("AppConfigLoad", ex);
                TryBackupUnreadable(path);
                var fallback = LoadExample(path);
                fallback.LoadFailed = true;
                return fallback;
            }
        }
        return LoadExample(path);
    }

    /// <summary>
    /// 严格加载（写配置前使用）：文件存在但无法解析时抛出，**不回退示例**。
    /// 这样一次读取失败不会让后续「读-改-写」把用户的动作清单覆盖成默认值。
    /// </summary>
    public static AppConfig LoadStrict(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Load(path);
        return ReadWithRetry(path);
    }

    /// <summary>读取并反序列化；对瞬时 IO 错误（被占用 / 共享冲突 / 权限）做固定间隔退避重试。</summary>
    private static AppConfig ReadWithRetry(string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8), Opt)
                       ?? new AppConfig();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < ReadRetries)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    /// <summary>瞬时 IO 错误：被占用 / 共享冲突 / 权限不足（区别于内容损坏的 JsonException）。</summary>
    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <summary>把不可读 / 损坏的用户配置复制为 <c>.bad-时间戳</c> 备份（复制失败则忽略）。
    /// 用复制而非改名：保留原文件，万一应用随后用默认值覆盖，备份仍可恢复。</summary>
    private static void TryBackupUnreadable(string path)
    {
        try
        {
            File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMddHHmmss}", overwrite: false);
            PruneBrokenBackups(path);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 只保留最近 <paramref name="keep"/> 份 <c>.bad-时间戳</c> 副本。
    /// 配置一直读不出来的话每次启动都会复制一份（同一秒内还会重名失败重试），不清就会越积越多。
    /// </summary>
    internal static void PruneBrokenBackups(string path, int keep = 3)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(path)!;
            string prefix = System.IO.Path.GetFileName(path) + ".bad-";
            foreach (var old in Directory.GetFiles(dir, prefix + "*")
                                         .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                                         .Skip(keep))
            {
                try { File.Delete(old); } catch { /* 单个文件被占用时跳过 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    private static AppConfig LoadExample(string? path)
    {
        // 用户配置缺失或不可读：回退读同目录示例作为默认模板。
        // 发布包只携带 LTools.example.json（不携带 LTools.json），
        // 用户首次保存才在同目录生成 LTools.json，升级解压不会覆盖已有配置。
        var example = FindExample(path);
        if (example != null)
        {
            try
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(example, Encoding.UTF8), Opt)
                       ?? new AppConfig();
            }
            catch { /* 示例不可读则用内置默认 */ }
        }
        return new AppConfig();
    }

    private static string? FindExample(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;
        var example = Path.Combine(dir, "LTools.example.json");
        return File.Exists(example) ? example : null;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Opt), new UTF8Encoding(false));
    }

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
                    Type = ActionType.runCommand,
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

        var sp = IniFile.ReadValue(iniPath, "MouseMode", "Speed");
        if (int.TryParse(sp, out var s)) cfg.MouseModeSpeed = s;

        return cfg;
    }
}
