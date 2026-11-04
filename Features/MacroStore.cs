using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 宏文件存储：事件序列存 <c>&lt;数据根&gt;/macros/&lt;id&gt;.json</c>，
/// 元数据（名称 / 时长 / 事件数 / 大小）存 <c>LTools.json</c> 的 <c>Macros.Items</c>，
/// 设置页列表只读元数据，不必每次解析大文件。
/// </summary>
/// <remarks>
/// 事件文件用紧凑 JSON（短字段名、不缩进）；写入走「临时文件 → 覆盖移动」，
/// 避免写到一半崩溃留下损坏的宏文件。所有方法可从后台线程调用。
/// </remarks>
internal static class MacroStore
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>宏目录（数据根下）。</summary>
    public static string Dir => Path.Combine(AppPaths.DataRoot, "macros");

    /// <summary>确保宏目录存在。</summary>
    public static void Ensure()
    {
        try { Directory.CreateDirectory(Dir); } catch { /* 目录不可用时保存会报错，由调用方记录 */ }
    }

    /// <summary>宏文件完整路径。</summary>
    public static string PathOf(string id) => Path.Combine(Dir, id + ".json");

    /// <summary>宏事件文件是否存在（不解析内容，供列表展示 / 失效检查）。</summary>
    public static bool Exists(string id) =>
        !string.IsNullOrWhiteSpace(id) && File.Exists(PathOf(id));

    /// <summary>生成不与现有文件冲突的新 Id。</summary>
    public static string NewId()
    {
        string id;
        do
        {
            id = "m" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
        } while (File.Exists(PathOf(id)));
        return id;
    }

    /// <summary>读取宏；不存在 / 解析失败返回 null。</summary>
    public static MacroDto? Load(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string path = PathOf(id);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<MacroDto>(File.ReadAllText(path, Encoding.UTF8), Opt);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.Load", ex);
            return null;
        }
    }

    /// <summary>保存宏（事件文件 + 元数据），并把 <c>LastMacroId</c> 指向它。</summary>
    public static void Save(MacroDto macro)
    {
        if (macro == null || string.IsNullOrEmpty(macro.Id)) return;
        macro.Events ??= new List<MacroEvent>();

        Ensure();
        string path = PathOf(macro.Id);
        string tmp = path + ".tmp";

        try
        {
            WriteMacroFile(tmp, macro);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败不影响上层报错 */ }
            throw;
        }

        long size = new FileInfo(path).Length;
        string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string cfgPath = CurrentConfigPath();

        ConfigIO.Modify(cfgPath, cfg =>
        {
            cfg.Macros ??= new MacroConfig();
            cfg.Macros.Items ??= new List<MacroMeta>();
            var meta = cfg.Macros.Items.FirstOrDefault(m => m.Id == macro.Id);
            if (meta == null)
            {
                meta = new MacroMeta { Id = macro.Id };
                cfg.Macros.Items.Add(meta);
            }
            meta.Name = macro.Name;
            meta.DurationMs = macro.DurationMs;
            meta.EventCount = macro.Events.Count;
            meta.SizeBytes = size;
            meta.UpdatedAt = now;
            cfg.Macros.LastMacroId = macro.Id;
        });

        // 让钩子 / 托盘立即读到新值（LastMacroId 等），无需重启。
        MacroSettings.Load(AppConfig.Load(cfgPath));
    }

    /// <summary>全部宏元数据（按更新时间倒序）。</summary>
    public static IReadOnlyList<MacroMeta> List()
    {
        try
        {
            var cfg = AppConfig.Load(CurrentConfigPath());
            return (cfg.Macros?.Items ?? new List<MacroMeta>())
                .OrderByDescending(m => m.UpdatedAt)
                .ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.List", ex);
            return Array.Empty<MacroMeta>();
        }
    }

    /// <summary>宏 Id → 显示名（供动作列表一次查出，避免逐行动作读配置）。</summary>
    public static IReadOnlyDictionary<string, string> NameMap()
    {
        try
        {
            var items = AppConfig.Load(CurrentConfigPath()).Macros?.Items;
            if (items == null || items.Count == 0)
                return new Dictionary<string, string>();
            return items
                .Where(m => !string.IsNullOrEmpty(m.Id))
                .GroupBy(m => m.Id)
                .ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.NameMap", ex);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>删除宏文件、元数据，以及引用它的宏动作（一宏一动作）。</summary>
    public static void Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        try
        {
            string path = PathOf(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.Delete", ex);
        }

        string cfgPath = CurrentConfigPath();
        ConfigIO.Modify(cfgPath, cfg =>
        {
            cfg.Macros ??= new MacroConfig();
            cfg.Macros.Items ??= new List<MacroMeta>();
            cfg.Macros.Items.RemoveAll(m => m.Id == id);
            if (cfg.Macros.LastMacroId == id) cfg.Macros.LastMacroId = null;
        });
        MacroSettings.Load(AppConfig.Load(cfgPath));

        // 一宏一动作：连带删除引用该宏的动作，避免动作池 / 菜单 / 超级面板留下死链。
        var actionIds = ActionRegistry.All
            .Where(a => a.Type == ActionType.macro && a.MacroId == id)
            .Select(a => a.Id)
            .ToList();
        if (actionIds.Count > 0)
        {
            foreach (string actionId in actionIds) ConfigStore.RemoveAction(actionId);
            ConfigStore.Save();
        }
    }

    /// <summary>
    /// 重命名宏（只改显示名，Id 不变）。若动作池里仍有沿用旧名、且引用该宏的动作，
    /// 一并改成新名（用户自定义过动作名则保持不动）。
    /// </summary>
    public static MacroDto? Rename(string id, string name)
    {
        var macro = Load(id);
        if (macro == null) return null;
        string oldName = macro.Name;
        macro.Name = string.IsNullOrWhiteSpace(name) ? macro.Name : name.Trim();
        Save(macro);

        if (!string.Equals(oldName, macro.Name, StringComparison.Ordinal))
        {
            var actions = ActionRegistry.All
                .Where(a => a.Type == ActionType.macro && a.MacroId == id &&
                            string.Equals(a.Name, oldName, StringComparison.Ordinal))
                .ToList();
            if (actions.Count > 0)
            {
                foreach (var a in actions) a.Name = macro.Name;
                ConfigStore.Save();
            }
        }
        return macro;
    }

    /// <summary>复制宏（新 Id + 名称追加「副本」）。</summary>
    public static MacroDto? Duplicate(string id)
    {
        var src = Load(id);
        if (src == null) return null;

        var copy = new MacroDto
        {
            Id = NewId(),
            Name = src.Name + " 副本",
            CreatedAt = DateTime.Now.ToString("O"),
            DurationMs = src.DurationMs,
            DesktopLeft = src.DesktopLeft,
            DesktopTop = src.DesktopTop,
            DesktopWidth = src.DesktopWidth,
            DesktopHeight = src.DesktopHeight,
            Events = src.Events != null ? new List<MacroEvent>(src.Events) : new List<MacroEvent>(),
        };
        Save(copy);

        // 专属速度存在元数据里，Save 不写它，复制后单独带上。
        double speed = SpeedOf(id);
        if (speed > 0) SetSpeed(copy.Id, speed);

        // 一宏一动作：副本也生成同名宏动作，否则这份宏在动作系统里无法使用。
        ConfigStore.AddAction(new ActionDto
        {
            Name = copy.Name,
            Type = ActionType.macro,
            MacroId = copy.Id,
            Icon = "record",
        });
        ConfigStore.Save();
        return copy;
    }

    // ============ 专属回放速度 ============

    /// <summary>取某个宏的专属回放速度；0 表示未设置（跟随全局默认）。</summary>
    public static double SpeedOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;
        try
        {
            var item = AppConfig.Load(CurrentConfigPath()).Macros?.Items?.FirstOrDefault(m => m.Id == id);
            return item == null ? 0 : Math.Clamp(item.Speed, 0, 5.0);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.SpeedOf", ex);
            return 0;
        }
    }

    /// <summary>设置某个宏的专属回放速度（0 = 跟随全局默认）。</summary>
    public static void SetSpeed(string id, double speed)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        double v = speed < 0.1 ? 0 : Math.Clamp(speed, 0.1, 5.0);

        string cfgPath = CurrentConfigPath();
        ConfigIO.Modify(cfgPath, cfg =>
        {
            var item = cfg.Macros?.Items?.FirstOrDefault(m => m.Id == id);
            if (item != null) item.Speed = v;
        });
        MacroSettings.Load(AppConfig.Load(cfgPath));
    }

    // ============ 元数据自愈 ============

    /// <summary>
    /// 让 <c>Macros.Items</c> 与 <c>macros/</c> 目录重新对齐：
    /// 删掉指向已不存在文件的条目、为落单的宏文件补元数据、纠正过期的大小、清掉失效的 <c>LastMacroId</c>。
    /// </summary>
    /// <returns>修正的条目数；0 表示无需改动（此时不会写配置文件）。</returns>
    /// <remarks>
    /// 触发场景：手动删 / 拷宏文件、从旧备份导入、直接改 <c>LTools.json</c>。
    /// 无宏文件且无元数据时直接返回，避免首次启动仅因自愈就生成 <c>LTools.json</c>。
    /// </remarks>
    public static int Reconcile()
    {
        try
        {
            Ensure();
            var byId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(id)) byId[id] = file;
            }

            string cfgPath = CurrentConfigPath();
            var cfg = AppConfig.Load(cfgPath);
            var items = cfg.Macros?.Items ?? new List<MacroMeta>();
            if (byId.Count == 0 && items.Count == 0) return 0;

            var staleIds = items
                .Where(m => string.IsNullOrEmpty(m.Id) || !byId.ContainsKey(m.Id))
                .Select(m => m.Id)
                .ToHashSet(StringComparer.Ordinal);

            var additions = new List<MacroMeta>();
            foreach (var (id, path) in byId)
            {
                if (items.Any(m => m.Id == id)) continue;
                var macro = Load(id);
                if (macro == null) continue;   // 损坏文件不登记，交由用户手动处理
                additions.Add(new MacroMeta
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(macro.Name) ? id : macro.Name,
                    DurationMs = macro.DurationMs,
                    EventCount = macro.Events?.Count ?? 0,
                    SizeBytes = SafeLength(path),
                    UpdatedAt = SafeWriteTime(path),
                });
            }

            bool sizeDirty = items.Any(m =>
                !string.IsNullOrEmpty(m.Id) && byId.TryGetValue(m.Id, out var p) && SafeLength(p) != m.SizeBytes);
            string? last = cfg.Macros?.LastMacroId;
            bool lastDirty = !string.IsNullOrEmpty(last) && !byId.ContainsKey(last!);

            if (additions.Count == 0 && staleIds.Count == 0 && !sizeDirty && !lastDirty) return 0;

            int changed = additions.Count + staleIds.Count;
            ConfigIO.Modify(cfgPath, c =>
            {
                c.Macros ??= new MacroConfig();
                var list = c.Macros.Items ??= new List<MacroMeta>();
                list.RemoveAll(m => string.IsNullOrEmpty(m.Id) || !byId.ContainsKey(m.Id));
                foreach (var add in additions) list.Add(add);
                foreach (var meta in list)
                {
                    if (byId.TryGetValue(meta.Id, out var p)) meta.SizeBytes = SafeLength(p);
                }
                if (!string.IsNullOrEmpty(c.Macros.LastMacroId) && !byId.ContainsKey(c.Macros.LastMacroId))
                    c.Macros.LastMacroId = null;
            });
            MacroSettings.Load(AppConfig.Load(cfgPath));
            return changed;
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.Reconcile", ex);
            return 0;
        }
    }

    /// <summary>
    /// 流式写出宏文件：事件逐条写入，避免把 20 万事件先拼成一个巨大的 JSON 字符串
    /// （长宏时峰值内存约为文件大小的 2–3 倍）。字段名与 <see cref="MacroDto"/> 的
    /// <c>JsonPropertyName</c> 一致，读取仍走 <see cref="Load"/> 的普通反序列化。
    /// </summary>
    private static void WriteMacroFile(string path, MacroDto macro)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

        w.WriteStartObject();
        w.WriteNumber("ver", macro.Version);
        w.WriteString("id", macro.Id ?? "");
        w.WriteString("name", macro.Name ?? "");
        w.WriteString("created", macro.CreatedAt ?? "");
        w.WriteNumber("dur", macro.DurationMs);
        w.WriteNumber("dl", macro.DesktopLeft);
        w.WriteNumber("dt", macro.DesktopTop);
        w.WriteNumber("dw", macro.DesktopWidth);
        w.WriteNumber("dh", macro.DesktopHeight);

        w.WriteStartArray("events");
        foreach (var e in macro.Events)
        {
            w.WriteStartObject();
            w.WriteNumber("t", e.TimeMs);
            w.WriteNumber("k", (byte)e.Kind);
            w.WriteNumber("v", e.Vk);
            w.WriteNumber("x", e.X);
            w.WriteNumber("y", e.Y);
            w.WriteNumber("d", e.Data);
            w.WriteNumber("f", e.Flags);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
        w.Flush();
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    private static string SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"); } catch { return ""; }
    }

    private static string CurrentConfigPath() =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;
}
