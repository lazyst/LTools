using System.Text;

namespace LTools.Config;

/// <summary>
/// 轻量 INI 读写（对应原版 Utils.ahk 的 IniRead/IniWrite）。
/// <para>仅用于旧版 CapsLock++.ini 的一次性迁移（见 <see cref="AppConfig.MigrateFromIni"/>）；
/// 迁移完成后运行时配置统一走 AppConfig(System.Text.Json)，不再使用本类。</para>
/// 采用逐行编辑而非整文件重写，最大限度保留用户原 INI 的注释/顺序/格式。
/// - 读：定位 [section] 块 → 块内找首个 key=... 取值。
/// - 写：定位 [section] 块 → 块内找 key= 行原地替换；块存在无该键 → 块尾插入；
///        块不存在 → 追加 [section] + key=value。
/// 文件 UTF-8（读时剥 BOM，写时不加 BOM）。
/// </summary>
internal static class IniFile
{
    /// <summary>读取 [section] 下 key 的值，找不到返回 null。</summary>
    public static string? ReadValue(string file, string section, string key)
    {
        if (!File.Exists(file)) return null;
        var content = ReadText(file);
        bool inSection = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection && TrySplitKv(line, out var k, out var v) &&
                k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }
        return null;
    }

    /// <summary>写入（覆盖或新增）[section] 下 key=value。文件/目录不存在则创建。</summary>
    public static void WriteValue(string file, string section, string key, string value)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var content = File.Exists(file) ? ReadText(file) : "";
        var lines = new List<string>(content.Split('\n').Select(l => l.TrimEnd('\r')));
        // 去掉末尾空行
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);

        int secStart = -1, secEnd = lines.Count; // secEnd 是块后第一个非块行的索引
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (secStart >= 0) { secEnd = i; break; }
                if (t[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase)) secStart = i;
            }
        }
        if (secStart < 0)
        {
            // 块不存在：追加
            if (lines.Count > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
        }
        else
        {
            // 块内找 key=
            int keyLine = -1;
            for (int i = secStart + 1; i < secEnd; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith('[') && t.EndsWith(']')) break;
                if (TrySplitKv(t, out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                { keyLine = i; break; }
            }
            if (keyLine >= 0)
            {
                lines[keyLine] = $"{key}={value}";
            }
            else
            {
                // 块尾插入（secEnd 之前）
                lines.Insert(secEnd, $"{key}={value}");
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Count - 1) sb.Append("\r\n");
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }

    private static bool TrySplitKv(string line, out string key, out string value)
    {
        key = ""; value = "";
        var idx = line.IndexOf('=');
        if (idx <= 0) return false;
        key = line[..idx].Trim();
        value = line[(idx + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>删除 [section] 下的 key（若存在）。块不存在或无该键静默。</summary>
    public static void DeleteKey(string file, string section, string key)
    {
        if (!File.Exists(file)) return;
        var content = ReadText(file);
        var lines = new List<string>(content.Split('\n').Select(l => l.TrimEnd('\r')));
        bool inSection = false;
        int removedAt = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                inSection = t[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (inSection && TrySplitKv(t, out var k, out _) &&
                k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                removedAt = i;
                break;
            }
        }
        if (removedAt >= 0)
        {
            lines.RemoveAt(removedAt);
            WriteAllLines(file, lines);
        }
    }

    /// <summary>删除整个 [section] 块（含块内所有行）。块不存在静默。</summary>
    public static void DeleteSection(string file, string section)
    {
        if (!File.Exists(file)) return;
        var content = ReadText(file);
        var lines = new List<string>(content.Split('\n').Select(l => l.TrimEnd('\r')));
        var result = new List<string>();
        bool inTarget = false;
        foreach (var raw in lines)
        {
            var t = raw.Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                inTarget = t[1..^1].Equals(section, StringComparison.OrdinalIgnoreCase);
                result.Add(raw);
                continue;
            }
            if (!inTarget) result.Add(raw);
        }
        // 去掉因删除块而残留的前后空行（块后紧跟的空行）
        WriteAllLines(file, result);
    }

    private static void WriteAllLines(string file, List<string> lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Count - 1) sb.Append("\r\n");
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }

    private static string ReadText(string file)
    {
        using var sr = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }
}
