using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace CapsLockPro.Config;

/// <summary>
/// 应用配置（CapsLock++.json）强类型模型 + System.Text.Json 读写。
/// 取代旧的 CapsLock++.ini：菜单/终端路径/鼠标速度统一为一个 JSON 对象，
/// 任何部分修改都「读-改-写」整个文件（配置小、无并发保存，比 ini 逐字段多次全文件 I/O 更优）。
/// 配置只由应用自身修改（设置面板/快捷键），不面向用户手改。
/// 发布包只携带 CapsLock++.example.json；用户首次改动保存后才在同目录生成
/// CapsLock++.json，故升级解压不会覆盖用户已有配置。
/// </summary>
public sealed class MenuGroupDto
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public List<MenuItemDto> Items { get; set; } = new();
}

public sealed class MenuItemDto
{
    public string Name { get; set; } = "";
    public string Cmd { get; set; } = "";
    public string Terminal { get; set; } = "direct";
    public bool KeepWindow { get; set; }
    public string Workdir { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>菜单组（长度固定 10，下标 0..9 对应 CapsLock+1..0；null=空组）。</summary>
    public List<MenuGroupDto?> MenuGroups { get; set; } = new();

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>鼠标模式速度 1..20。</summary>
    public int MouseModeSpeed { get; set; } = 1;

    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        // 中文/特殊字符不转义为 \uXXXX，保持配置可读（配置仅由应用修改，但调试时易读更好）
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load(string path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path, Encoding.UTF8));
                return cfg ?? new AppConfig();
            }
            catch { return new AppConfig(); }
        }

        // 用户配置缺失：回退读同目录示例作为默认模板。
        // 发布包只携带 CapsLock++.example.json（不携带 CapsLock++.json），
        // 用户首次保存才在同目录生成 CapsLock++.json，升级解压不会覆盖已有配置。
        var example = FindExample(path);
        if (example != null)
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(example, Encoding.UTF8));
                if (cfg != null) return cfg;
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
    /// 依赖 IniFile 的逐节读取；迁移完成后 IniFile 不再被运行时使用。
    /// </summary>
    public static AppConfig MigrateFromIni(string iniPath)
    {
        var cfg = new AppConfig();
        for (int i = 1; i <= 10; i++)
        {
            string enabled = IniFile.ReadValue(iniPath, "MenuGroupsEnable", "enableGroup" + i) ?? "true";
            if (!enabled.Equals("true", StringComparison.OrdinalIgnoreCase)) { cfg.MenuGroups.Add(null); continue; }

            string name = IniFile.ReadValue(iniPath, "MenuGroupName", "name" + i) ?? ("菜单组 " + i);
            int count = int.TryParse(IniFile.ReadValue(iniPath, "MenuGroupCount", "count" + i), out var c) ? c : 0;
            var items = new List<MenuItemDto>();
            string section = "MenuGroups" + i + "Items";
            for (int j = 1; j <= count; j++)
            {
                string iname = IniFile.ReadValue(iniPath, section, "name" + j) ?? "";
                if (iname.Length == 0) continue;
                items.Add(new MenuItemDto
                {
                    Name = iname,
                    Cmd = IniFile.ReadValue(iniPath, section, "action" + j) ?? "",
                    Terminal = IniFile.ReadValue(iniPath, section, "terminal" + j) ?? "direct",
                    KeepWindow = (IniFile.ReadValue(iniPath, section, "keepwindow" + j) ?? "false")
                        .Equals("true", StringComparison.OrdinalIgnoreCase),
                    Workdir = IniFile.ReadValue(iniPath, section, "workdir" + j) ?? "",
                });
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
