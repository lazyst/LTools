using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CapsLockPro.Features;

namespace CapsLockPro.Config;

/// <summary>
/// 应用配置（CapsLock++.json）强类型模型 + System.Text.Json 读写。
/// 动作系统 schema（计划 §9）：全局动作清单 <see cref="Actions"/> + 超级面板 <see cref="SuperPanel"/> +
/// 菜单组 <see cref="MenuGroups"/>（项为动作 Id 引用）。无需兼容旧 schema（旧 json 废弃）。
/// 发布包只携带 CapsLock++.example.json；用户首次改动保存后才在同目录生成
/// CapsLock++.json，升级解压不会覆盖用户配置。
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

    /// <summary>每页 9 槽，元素为动作 Id 或 null（空格）。</summary>
    public List<List<string?>> Pages { get; set; } = new();
}

public sealed class AppConfig
{
    /// <summary>菜单组（长度固定 10，下标 0..9 对应 CapsLock+1~0；null=空组）。</summary>
    public List<MenuGroupDto?> MenuGroups { get; set; } = new();

    /// <summary>全局动作清单（计划 §2）。超级面板与菜单组都引用这里的 Id。</summary>
    public List<ActionDto> Actions { get; set; } = new();

    /// <summary>超级面板配置。</summary>
    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>鼠标模式速度 1..20。</summary>
    public int MouseModeSpeed { get; set; } = 1;

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

    /// <summary>
    /// 加载配置：优先读用户 json；不存在或反序列化失败（旧 schema 不兼容）时回退读同目录示例。
    /// </summary>
    public static AppConfig Load(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppConfig();
            }
            catch
            {
                // 旧 schema 反序列化失败 → 回退 example（不崩溃）
            }
        }
        return LoadExample(path);
    }

    private static AppConfig LoadExample(string? path)
    {
        // 用户配置缺失或不可读：回退读同目录示例作为默认模板。
        // 发布包只携带 CapsLock++.example.json（不携带 CapsLock++.json），
        // 用户首次保存才在同目录生成 CapsLock++.json，升级解压不会覆盖已有配置。
        var example = FindExample(path);
        if (example != null)
        {
            try
            {
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(example, Encoding.UTF8)) ?? new AppConfig();
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
        var example = Path.Combine(dir, "CapsLock++.example.json");
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
