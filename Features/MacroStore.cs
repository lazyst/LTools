using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 宏文件存储：事件序列存 <c>&lt;数据根&gt;/macros/&lt;id&gt;.json</c>，
/// 元数据（名称 / 时长 / 事件数 / 大小 / 回放参数）存 <c>LTools.json</c> 的 <c>Macros.Items</c>，
/// 动作编辑器只读元数据，不必每次解析大文件。
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

    /// <summary>某个宏的元数据；不存在返回 null。</summary>
    public static MacroMeta? MetaOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return AppConfig.Load(CurrentConfigPath()).Macros?.Items?
                .FirstOrDefault(m => m.Id == id);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.MetaOf", ex);
            return null;
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

    // ============ 回放参数（每宏独立，缺省回退全局） ============

    /// <summary>某个宏实际生效的回放参数；<see cref="Loops"/> = -1 表示无限循环。</summary>
    public readonly record struct MacroPlayback(double Speed, int Loops, int CountdownMs);

    /// <summary>
    /// 解析某宏的回放参数：元数据里的专属值优先，缺失时回退 <see cref="MacroSettings"/> 的全局默认。
    /// </summary>
    public static MacroPlayback PlaybackOf(string? macroId)
    {
        var meta = MetaOf(macroId ?? "");
        double speed = meta is { Speed: > 0 } ? Math.Clamp(meta.Speed, 0.1, 5.0) : MacroSettings.DefaultSpeed;
        int loops = meta?.Loops is int l && l != 0
            ? (l < 0 ? -1 : Math.Clamp(l, 1, 99))
            : Math.Clamp(MacroSettings.PlayLoops, 1, 99);
        int countdown = meta?.CountdownMs is int c && c >= 0
            ? Math.Clamp(c, 0, 10_000)
            : MacroSettings.PlaybackCountdownMs;
        return new MacroPlayback(speed, loops, countdown);
    }

    /// <summary>
    /// 保存某个宏的回放参数（速度 / 循环 / 倒计时）与显示名；<paramref name="loops"/> = -1 表示无限循环，
    /// <paramref name="name"/> 为 null 表示不改名。
    /// </summary>
    public static void SavePlaybackSettings(string id, string? name, double speed, int loops, int countdownMs)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        double s = Math.Clamp(speed, 0.1, 5.0);
        int l = loops < 0 ? -1 : Math.Clamp(loops, 1, 99);
        int c = Math.Clamp(countdownMs, 0, 10_000);

        ConfigIO.Modify(CurrentConfigPath(), cfg =>
        {
            var item = cfg.Macros?.Items?.FirstOrDefault(m => m.Id == id);
            if (item == null) return;
            item.Speed = s;
            item.Loops = l;
            item.CountdownMs = c;
            if (!string.IsNullOrWhiteSpace(name)) item.Name = name.Trim();
        });
    }

    // ============ 引用回收 ============

    /// <summary>
    /// 按引用回收宏文件：删除「不被任何键鼠宏动作 / 组合步骤引用」的 <c>macros/*.json</c> 与元数据。
    /// 一宏一动作——删除宏动作后它的宏文件自动消失，用户不需要（也没有入口）手工管理宏文件。
    /// </summary>
    /// <returns>删除的宏文件数。</returns>
    public static int CollectGarbage()
    {
        try
        {
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ActionRegistry.All) CollectRefs(a);

            void CollectNode(FlowNode n)
            {
                if (n.Action != null) CollectRefs(n.Action);
                if (n.If != null)
                {
                    foreach (var c in n.If.Then) CollectNode(c);
                    if (n.If.Else != null)
                        foreach (var c in n.If.Else) CollectNode(c);
                }
                if (n.Loop != null)
                    foreach (var c in n.Loop.Body) CollectNode(c);
            }

            void CollectRefs(ActionDto a)
            {
                if (a.Type == ActionType.macro && !string.IsNullOrWhiteSpace(a.MacroId))
                    live.Add(a.MacroId!);

                // 新动作体：所有单元的 macroId 参数（含动作内子动作与内联取值步骤）
                foreach (var step in ProgramWalker.AllSteps(a.Program))
                    if (ProgramWalker.LiteralText(step, "macroId") is { } id && id.Trim().Length > 0)
                        live.Add(id.Trim());

                // —— 以下只服务还没迁移过的旧动作（迁移后这些字段为 null）——
                // 新流程（含旧 Steps 迁移）与残留 Steps 都算引用，避免误删仍被使用的宏文件。
                if (a.Type == ActionType.composite)
                    foreach (var n in FlowMigration.Ensure(a)) CollectNode(n);
                if (a.Steps == null) return;
                foreach (var s in a.Steps) CollectRefs(s.Action);
            }

            Ensure();
            int removed = 0;
            foreach (var file in Directory.GetFiles(Dir, "*.json"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(id) || live.Contains(id)) continue;
                try { File.Delete(file); removed++; }
                catch (Exception ex) { CrashLog.Write("MacroStore.CollectGarbage", ex); }
            }
            // 写盘中断留下的临时文件一并清掉。
            foreach (var tmp in Directory.GetFiles(Dir, "*.json.tmp"))
            {
                try { File.Delete(tmp); } catch { /* 忽略 */ }
            }

            string cfgPath = CurrentConfigPath();
            var cfg = AppConfig.Load(cfgPath);
            var items = cfg.Macros?.Items ?? new List<MacroMeta>();
            bool hasDeadMeta = items.Any(m => string.IsNullOrEmpty(m.Id) || !live.Contains(m.Id));
            bool lastDead = !string.IsNullOrEmpty(cfg.Macros?.LastMacroId) &&
                            !live.Contains(cfg.Macros!.LastMacroId!);
            if (!hasDeadMeta && !lastDead) return removed;

            ConfigIO.Modify(cfgPath, c =>
            {
                c.Macros ??= new MacroConfig();
                c.Macros.Items ??= new List<MacroMeta>();
                c.Macros.Items.RemoveAll(m => string.IsNullOrEmpty(m.Id) || !live.Contains(m.Id));
                if (!string.IsNullOrEmpty(c.Macros.LastMacroId) && !live.Contains(c.Macros.LastMacroId))
                    c.Macros.LastMacroId = null;
            });
            return removed;
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.CollectGarbage", ex);
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

    private static string CurrentConfigPath() =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;
}
