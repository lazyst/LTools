using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Config;

/// <summary>
/// 配置分域读写（见 <c>docs/ARCHITECTURE.md</c> P1 / P2）：
/// 全局配置拆成 <c>configs/</c> 下的 7 个域文件，动作各自一个目录 <c>actions/&lt;id&gt;/</c>。
/// （目录名用复数的 <c>configs</c>：Windows 大小写不敏感，单数会撞上源码目录 <c>Config/</c>。）
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppConfig"/> 仍是内存里的聚合视图，<see cref="AppConfig.Load"/> / <see cref="AppConfig.Save"/>
/// 转到这里：读 = 合并域文件 + 动作目录；写 = 逐域序列化，**内容没变就不碰磁盘**
/// （因此改导航页只会动 <c>ui.json</c>，改一个动作只重写它自己的 <c>action.json</c>）。
/// </para>
/// <para>
/// 首次启动：<c>configs/</c> 不存在时从同目录的 <c>LTools.example.json</c> 读种子，补齐七个域；
/// 第一次保存就把域文件与 <c>actions/</c> 动作目录一起写出来，之后各域各自读写。
/// </para>
/// <para>
/// 读取失败按域隔离：坏掉的那个文件备份成 <c>&lt;file&gt;.bad-时间戳</c>（每域只留 3 份）并用默认值顶上，
/// <see cref="AppConfig.LoadFailed"/> 置位；写配置前的 <see cref="AppConfig.LoadStrict"/> 仍然直接抛错，
/// 绝不拿默认值覆盖用户数据。
/// </para>
/// </remarks>
internal static class ConfigBundle
{
    /// <summary>分域格式的版本号（写入每个域文件；以后改结构就 +1 并同步读取逻辑）。</summary>
    public const int SchemaVersion = 4;

    /// <summary>读失败（含被占用 / 共享冲突）时的重试次数与间隔——开机自启瞬间配置常被 AV / 索引器短暂占用。</summary>
    private const int ReadRetries = 15;
    private const int ReadRetryDelayMs = 200;

    /// <summary>域文件的序列化选项（可读、不转义中文、枚举写字符串）。</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ============ 路径 ============

    /// <summary>配置目录：与锚点文件（旧 <c>LTools.json</c> 的位置）同级的 <c>configs/</c>。</summary>
    public static string ConfigDir(string anchorPath) => Path.Combine(BaseDir(anchorPath), "configs");

    private static string SettingsPath(string a) => Path.Combine(ConfigDir(a), "settings.json");
    private static string UiPath(string a) => Path.Combine(ConfigDir(a), "ui.json");
    private static string HotkeysPath(string a) => Path.Combine(ConfigDir(a), "hotkeys.json");
    private static string PanelPath(string a) => Path.Combine(ConfigDir(a), "panel.json");
    private static string FloatingPath(string a) => Path.Combine(ConfigDir(a), "floating.json");
    private static string MacrosPath(string a) => Path.Combine(ConfigDir(a), "macros.json");
    private static string TriggersPath(string a) => Path.Combine(ConfigDir(a), "triggers.json");

    /// <summary>7 个域文件（顺序固定，便于诊断）。</summary>
    public static IReadOnlyList<string> DomainPaths(string anchorPath) => new[]
    {
        SettingsPath(anchorPath),
        UiPath(anchorPath),
        HotkeysPath(anchorPath),
        PanelPath(anchorPath),
        FloatingPath(anchorPath),
        MacrosPath(anchorPath),
        TriggersPath(anchorPath),
    };

    /// <summary>7 个域文件都在（首装 / 备份导入缺文件时用来决定是否整份补齐）。</summary>
    public static bool IsFullySplit(string anchorPath) => DomainPaths(anchorPath).All(File.Exists);

    private static string BaseDir(string anchorPath)
    {
        string full = Path.GetFullPath(anchorPath);
        return Path.GetDirectoryName(full) ?? Directory.GetCurrentDirectory();
    }

    // ============ 读 ============

    /// <summary>合并读取；坏文件按域回退默认值并置 <see cref="AppConfig.LoadFailed"/>。</summary>
    public static AppConfig Load(string anchorPath) => LoadCore(anchorPath, strict: false);

    /// <summary>写配置前使用：**已存在**的域文件解析失败时抛出，不回退默认值。</summary>
    public static AppConfig LoadStrict(string anchorPath) => LoadCore(anchorPath, strict: true);

    private static AppConfig LoadCore(string anchorPath, bool strict)
    {
        var ctx = new LoadContext { Anchor = anchorPath, Strict = strict };
        var cfg = new AppConfig();

        var settings = Read<SettingsFile>(SettingsPath(anchorPath), ctx);
        if (settings == null) settings = ToSettings(ctx.GetSeed());
        cfg.CapsLockModifier = settings.CapsLockModifier;
        cfg.EdgeWheelVolume = settings.EdgeWheelVolume;
        cfg.EdgeWheelVolumeEdge = settings.EdgeWheelVolumeEdge;
        cfg.TerminalPaths = settings.TerminalPaths ?? new();
        cfg.RunNotice = settings.RunNotice;
        cfg.ToastPosition = settings.ToastPosition;
        cfg.TrayDoubleClick = settings.TrayDoubleClick;
        cfg.Logging = settings.Logging ?? new LogConfig();

        var ui = Read<UiStateFile>(UiPath(anchorPath), ctx);
        if (ui == null) ui = ToUi(ctx.GetSeed());
        cfg.LastNavPage = string.IsNullOrWhiteSpace(ui.LastNavPage) ? "actions" : ui.LastNavPage;
        cfg.NavExpandedGroups = ui.NavExpandedGroups;
        cfg.LastOcrEngine = ui.LastOcrEngine;
        cfg.ToolboxCollapsedGroups = ui.ToolboxCollapsedGroups ?? new();
        cfg.RecentActionIds = ui.RecentActionIds;

        bool hasStore = ActionStore.HasStore(anchorPath);
        if (hasStore)
        {
            // actions/ 目录存在（含空）= 唯一来源；空目录表示用户把动作全删了。
            var (fromStore, failed) = ActionStore.LoadAll(anchorPath, ctx.Strict);
            cfg.Actions = fromStore;
            if (failed) ctx.Failed = true;
        }
        else
        {
            cfg.Actions = ctx.GetSeed().Actions ?? new();
        }

        var hotkeys = Read<HotkeyFile>(HotkeysPath(anchorPath), ctx) ?? ToHotkeys(ctx.GetSeed());
        cfg.Hotkeys = hotkeys;
        cfg.Hotkeys.Normalize();

        var panel = Read<PanelFile>(PanelPath(anchorPath), ctx);
        if (panel == null) panel = ToPanel(ctx.GetSeed());
        cfg.SuperPanel = panel.SuperPanel ?? new SuperPanelConfig();
        cfg.Panel = panel.Panel;

        var floating = Read<FloatingFile>(FloatingPath(anchorPath), ctx);
        if (floating == null) floating = ToFloating(ctx.GetSeed());
        cfg.Floating = floating.Floating;

        var macros = Read<MacroFile>(MacrosPath(anchorPath), ctx);
        if (macros == null) macros = ToMacros(ctx.GetSeed());
        cfg.Macros = macros.Macros ?? new MacroConfig();

        var triggers = Read<TriggersFile>(TriggersPath(anchorPath), ctx);
        if (triggers == null) triggers = ToTriggers(ctx.GetSeed());
        cfg.Triggers = triggers;
        cfg.Triggers.Normalize();

        cfg.LoadFailed = ctx.Failed;
        return cfg;
    }

    /// <summary>
    /// 只读 <c>configs/settings.json</c>（不读其它域、不扫 <c>actions/</c>）。
    /// 设置页切页回填「运行提示 / 日志设置」这类纯 settings 域控件时用：整份
    /// <see cref="AppConfig.Load"/> 会把动作目录也解析一遍，UI 线程与低级鼠标钩子共用，
    /// 每次切页都做会顿一下。缺文件时的回落与 <see cref="LoadCore"/> 一致。
    /// </summary>
    internal static SettingsFile ReadSettings(string anchorPath)
    {
        var ctx = new LoadContext { Anchor = anchorPath, Strict = false };
        return Read<SettingsFile>(SettingsPath(anchorPath), ctx) ?? ToSettings(ctx.GetSeed());
    }

    /// <summary>
    /// 只读 <c>configs/ui.json</c>（不读其它域、不扫 <c>actions/</c>）。设置窗口构造时恢复
    /// <c>LastNavPage</c> / <c>NavExpandedGroups</c> 用：整份 <see cref="AppConfig.Load"/> 会把动作
    /// 目录也解析一遍，窗口还没 Show 出来就压在 UI 线程上。缺文件回落与 <see cref="LoadCore"/> 一致。
    /// </summary>
    internal static UiStateFile ReadUi(string anchorPath)
    {
        var ctx = new LoadContext { Anchor = anchorPath, Strict = false };
        return Read<UiStateFile>(UiPath(anchorPath), ctx) ?? ToUi(ctx.GetSeed());
    }

    /// <summary>一次读取里的共享状态：严格模式、是否已回退过、惰性加载的种子。</summary>
    private sealed class LoadContext
    {
        public required string Anchor { get; init; }
        public required bool Strict { get; init; }
        public bool Failed { get; set; }

        private bool _seedLoaded;
        private AppConfig? _seed;

        /// <summary>
        /// 缺失域的回填来源：同目录示例 → 内置默认。只在真有域文件缺失时才读，
        /// 因此配置齐全之后启动不会再去碰示例文件。
        /// </summary>
        public AppConfig GetSeed()
        {
            if (_seedLoaded) return _seed ?? new AppConfig();
            _seedLoaded = true;
            _seed = LoadSeed(this);
            return _seed ?? new AppConfig();
        }
    }

    private static AppConfig LoadSeed(LoadContext ctx)
    {
        string? example = FindExample(ctx.Anchor);
        if (example != null)
        {
            try
            {
                return ReadWithRetry<AppConfig>(example) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                if (ctx.Strict) throw new InvalidOperationException($"示例配置无法解析：{example}", ex);
                CrashLog.Write("ConfigExample", ex);
                ctx.Failed = true;
            }
        }
        return new AppConfig();
    }

    /// <summary>读一个域文件；不存在返回 null（调用方回填种子），坏文件在非严格模式下备份并置位失败。</summary>
    private static T? Read<T>(string path, LoadContext ctx) where T : class, new()
    {
        if (!File.Exists(path)) return null;
        try
        {
            return ReadWithRetry<T>(path);
        }
        catch (Exception ex)
        {
            if (ctx.Strict)
                throw new InvalidOperationException($"配置文件无法解析：{path}", ex);
            CrashLog.Write("Config." + Path.GetFileName(path), ex);
            TryBackupUnreadable(path);
            ctx.Failed = true;
            return null;
        }
    }

    /// <summary>读取并反序列化；对瞬时 IO 错误（被占用 / 共享冲突 / 权限）做固定间隔退避重试。</summary>
    private static T ReadWithRetry<T>(string path) where T : class, new()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Options) ?? new T();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < ReadRetries)
            {
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    /// <summary>瞬时 IO 错误：被占用 / 共享冲突 / 权限不足（区别于内容损坏的 JsonException）。</summary>
    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;

    private static string? FindExample(string anchorPath)
    {
        string example = Path.Combine(BaseDir(anchorPath), "LTools.example.json");
        if (File.Exists(example)) return example;
        string besideExe = Path.Combine(AppContext.BaseDirectory, "LTools.example.json");
        return File.Exists(besideExe) ? besideExe : null;
    }

    // ============ 写 ============

    /// <summary>整份写回（备份恢复用）。日常单域修改走 <c>DomainStores</c>。</summary>
    public static void Save(string anchorPath, AppConfig cfg)
        => SaveDomains(anchorPath, cfg, ConfigDomain.all);

    /// <summary>
    /// 按域写回：只写 <paramref name="domains"/> 选中的文件，内容没变的文件不碰
    /// （保留 mtime，也让 diff / 备份更干净）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 七个域文件还不齐（首装 / 备份导入只带回一部分）时，这一次写入自动扩成整份：
    /// 否则只改一个界面开关会留下半个配置目录，缺的域永远补不上。
    /// </para>
    /// <para>
    /// 动作目录同理：<c>actions/</c> 不存在时至少写一次（把种子里的动作落成目录），
    /// 否则「只改了一个开关」永远不会建立权威目录。
    /// </para>
    /// </remarks>
    public static void SaveDomains(string anchorPath, AppConfig cfg, ConfigDomain domains)
    {
        Directory.CreateDirectory(ConfigDir(anchorPath));
        // 首装 / 有域文件缺失：这次写全，之后每次只碰被点的那个文件。
        if (!IsFullySplit(anchorPath)) domains = ConfigDomain.all;

        if (domains.HasFlag(ConfigDomain.settings)) WriteIfChanged(SettingsPath(anchorPath), ToSettings(cfg));
        if (domains.HasFlag(ConfigDomain.ui)) WriteIfChanged(UiPath(anchorPath), ToUi(cfg));
        if (domains.HasFlag(ConfigDomain.hotkeys)) WriteIfChanged(HotkeysPath(anchorPath), ToHotkeys(cfg));
        if (domains.HasFlag(ConfigDomain.panel)) WriteIfChanged(PanelPath(anchorPath), ToPanel(cfg));
        if (domains.HasFlag(ConfigDomain.floating)) WriteIfChanged(FloatingPath(anchorPath), ToFloating(cfg));
        if (domains.HasFlag(ConfigDomain.macros)) WriteIfChanged(MacrosPath(anchorPath), ToMacros(cfg));
        if (domains.HasFlag(ConfigDomain.triggers)) WriteIfChanged(TriggersPath(anchorPath), ToTriggers(cfg));

        // 动作一个目录一个（内容没变的 action.json 不重写；清单里没有的目录移入 trash/）。
        if (domains.HasFlag(ConfigDomain.actions) || !ActionStore.HasStore(anchorPath))
            ActionStore.SaveAll(anchorPath, cfg.Actions ?? new List<ActionDto>());
    }

    // —— 单域写入（DomainStores 用） ——

    public static void WriteSettings(string anchorPath, SettingsFile file)
        => WriteDomain(SettingsPath(anchorPath), file);

    public static void WriteUi(string anchorPath, UiStateFile file)
        => WriteDomain(UiPath(anchorPath), file);

    public static void WriteHotkeys(string anchorPath, HotkeyFile file)
        => WriteDomain(HotkeysPath(anchorPath), file);

    public static void WritePanel(string anchorPath, PanelFile file)
        => WriteDomain(PanelPath(anchorPath), file);

    public static void WriteFloating(string anchorPath, FloatingFile file)
        => WriteDomain(FloatingPath(anchorPath), file);

    public static void WriteMacros(string anchorPath, MacroFile file)
        => WriteDomain(MacrosPath(anchorPath), file);

    public static void WriteTriggers(string anchorPath, TriggersFile file)
        => WriteDomain(TriggersPath(anchorPath), file);

    private static void WriteDomain<T>(string path, T file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteIfChanged(path, file);
    }

    /// <summary>先写临时文件再整体替换，避免写到一半崩溃留下半个 JSON。</summary>
    private static void WriteIfChanged<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        try
        {
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == json) return;
        }
        catch { /* 读不出来（被占用）就照常写 */ }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ============ 聚合 ↔ 域文件 ============

    internal static SettingsFile ToSettings(AppConfig cfg) => new()
    {
        CapsLockModifier = cfg.CapsLockModifier,
        EdgeWheelVolume = cfg.EdgeWheelVolume,
        EdgeWheelVolumeEdge = cfg.EdgeWheelVolumeEdge,
        TerminalPaths = cfg.TerminalPaths ?? new(),
        RunNotice = cfg.RunNotice,
        ToastPosition = cfg.ToastPosition,
        TrayDoubleClick = cfg.TrayDoubleClick,
        Logging = cfg.Logging ?? new LogConfig(),
    };

    internal static UiStateFile ToUi(AppConfig cfg) => new()
    {
        LastNavPage = cfg.LastNavPage,
        NavExpandedGroups = cfg.NavExpandedGroups,
        LastOcrEngine = cfg.LastOcrEngine,
        ToolboxCollapsedGroups = cfg.ToolboxCollapsedGroups ?? new(),
        RecentActionIds = cfg.RecentActionIds,
    };

    internal static HotkeyFile ToHotkeys(AppConfig cfg) => cfg.Hotkeys ?? new HotkeyFile();

    internal static PanelFile ToPanel(AppConfig cfg) => new()
    {
        SuperPanel = cfg.SuperPanel ?? new SuperPanelConfig(),
        Panel = cfg.Panel,
    };

    internal static FloatingFile ToFloating(AppConfig cfg) => new() { Floating = cfg.Floating };

    internal static MacroFile ToMacros(AppConfig cfg) => new() { Macros = cfg.Macros ?? new MacroConfig() };

    internal static TriggersFile ToTriggers(AppConfig cfg) => cfg.Triggers ?? new TriggersFile();

    // ============ 坏文件备份 ============

    /// <summary>把不可读 / 损坏的文件复制为 <c>.bad-时间戳</c>（复制而非改名：万一随后被覆盖，备份仍可恢复）。</summary>
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
    /// 配置一直读不出来的话每次启动都会复制一份，不清就会越积越多。
    /// </summary>
    public static void PruneBrokenBackups(string path, int keep = 3)
    {
        try
        {
            string dir = Path.GetDirectoryName(path)!;
            string prefix = Path.GetFileName(path) + ".bad-";
            foreach (var old in Directory.GetFiles(dir, prefix + "*")
                                         .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                                         .Skip(keep))
            {
                try { File.Delete(old); } catch { /* 单个文件被占用时跳过 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    /// <summary>启动时清理：每个域文件旁边的 <c>.bad-*</c> 只留最近几份。</summary>
    public static void PruneAllBrokenBackups(string anchorPath, int keep = 3)
    {
        foreach (string path in DomainPaths(anchorPath)) PruneBrokenBackups(path, keep);
    }
}
