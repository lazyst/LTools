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
/// 元数据（名称 / 时长 / 事件数 / 大小 / 回放参数）存 <c>configs/macros.json</c> 的 <c>Macros.Items</c>，
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

    /// <summary>生成不与现有文件冲突的新 Id（ULID，见 <c>Core/Ids.cs</c>）。</summary>
    public static string NewId() => IdFactory.NewId(id => File.Exists(PathOf(id)));

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

        MacroFileStore.Modify(cfgPath, file =>
        {
            var macros = file.Macros;
            macros.Items ??= new List<MacroMeta>();
            var meta = macros.Items.FirstOrDefault(m => m.Id == macro.Id);
            if (meta == null)
            {
                meta = new MacroMeta { Id = macro.Id };
                macros.Items.Add(meta);
            }
            meta.Name = macro.Name;
            meta.DurationMs = macro.DurationMs;
            meta.EventCount = macro.Events.Count;
            meta.SizeBytes = size;
            meta.UpdatedAt = now;
            macros.LastMacroId = macro.Id;
        });

        // 让钩子 / 托盘立即读到新值（LastMacroId 等），无需重启。
        MacroSettings.Load(AppConfig.Load(cfgPath));
        Invalidate();
        ConfigStore.NotifyChanged();   // 录制完成：已打开的设置页要看到新宏
    }

    /// <summary>某个宏的元数据；不存在返回 null。</summary>
    public static MacroMeta? MetaOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return Items().FirstOrDefault(m => m.Id == id);
    }

    /// <summary>全部可用宏的元数据（最近录的在前），供「键鼠宏」页与宏下拉使用。</summary>
    /// <remarks>事件文件不在的元数据不进列表：删动作会回收宏文件，外部手工删 / 写盘中断时
    /// 也不该留下「能点但点不动」的幽灵行。孤儿元数据仍由 <see cref="CollectGarbage"/> 清理。</remarks>
    public static List<MacroMeta> List() => Items()
        .Where(m => !string.IsNullOrWhiteSpace(m.Id))
        .Where(m => Exists(m.Id))
        .OrderByDescending(m => m.UpdatedAt, StringComparer.Ordinal)
        .ThenByDescending(m => m.Id, StringComparer.Ordinal)
        .ToList();

    // 元数据缓存：编辑器每渲染一行摘要都要查一次名字，不能每次都读整份配置。
    private static List<MacroMeta>? _cache;

    /// <summary>丢弃元数据缓存（本类之外改了 <c>configs/macros.json</c> 后调用）。</summary>
    public static void Invalidate() => _cache = null;

    private static List<MacroMeta> Items()
    {
        if (_cache != null) return _cache;
        try
        {
            _cache = AppConfig.Load(CurrentConfigPath()).Macros?.Items ?? new List<MacroMeta>();
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.Items", ex);
            _cache = new List<MacroMeta>();
        }
        return _cache;
    }

    /// <summary>改名：只动元数据（界面显示的是元数据里的名字），引用它的动作名一起改。</summary>
    public static void Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) return;

        MacroFileStore.Modify(CurrentConfigPath(), file =>
        {
            var item = file.Macros.Items?.FirstOrDefault(m => m.Id == id);
            if (item != null) item.Name = trimmed;
        });
        Invalidate();
        MacroAction.Attach(id, trimmed);
    }

    /// <summary>删除宏：事件文件 + 元数据 + 引用它的动作（一宏一动作）。</summary>
    /// <returns>被一起删掉的动作数。</returns>
    public static int Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;

        int removedActions = 0;
        foreach (var action in ActionRegistry.All
                     .Where(a => string.Equals(MacroAction.MacroIdOf(a), id, StringComparison.Ordinal))
                     .ToList())
        {
            if (ConfigStore.RemoveAction(action.Id)) removedActions++;
        }

        try
        {
            string path = PathOf(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.Delete", ex);
        }

        MacroFileStore.Modify(CurrentConfigPath(), file =>
        {
            file.Macros.Items?.RemoveAll(m => m.Id == id);
            if (file.Macros.LastMacroId == id) file.Macros.LastMacroId = null;
        });
        Invalidate();
        ConfigStore.NotifyChanged();   // 没有引用它的动作时，上面不会经 RemoveAction 发广播
        return removedActions;
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
    /// 某个宏的回放参数覆盖值：字段为 <c>null</c> = 该字段跟随全局默认。
    /// <see cref="Loops"/> = -1 表示无限循环，1–99 表示次数。
    /// </summary>
    public readonly record struct MacroPlaybackOverride(double? Speed, int? Loops, int? CountdownMs)
    {
        /// <summary>全部跟随默认（「恢复默认」）。</summary>
        public static MacroPlaybackOverride None => new(null, null, null);

        /// <summary>有没有任何字段是单独设过的。</summary>
        public bool Any => Speed != null || Loops != null || CountdownMs != null;
    }

    /// <summary>
    /// 保存某个宏的回放参数覆盖值（「回放设置…」对话框写这里）；字段为 null 表示该字段跟随全局默认，
    /// <paramref name="name"/> 为 null 表示不改名。
    /// </summary>
    public static void SavePlaybackSettings(string id, string? name, MacroPlaybackOverride value)
    {
        if (string.IsNullOrWhiteSpace(id)) return;

        MacroFileStore.Modify(CurrentConfigPath(), file =>
        {
            var item = file.Macros.Items?.FirstOrDefault(m => m.Id == id);
            if (item == null) return;
            item.Speed = value.Speed is double s ? Math.Clamp(s, 0.1, 5.0) : 0;
            item.Loops = value.Loops is int l ? (l < 0 ? -1 : Math.Clamp(l, 1, 99)) : null;
            item.CountdownMs = value.CountdownMs is int c ? Math.Clamp(c, 0, 10_000) : null;
            if (!string.IsNullOrWhiteSpace(name)) item.Name = name.Trim();
        });
        Invalidate();
        ConfigStore.NotifyChanged();   // 列表行的回放参数后缀要跟着变
    }

    // ============ 旧数据 ============

    /// <summary>
    /// 把旧版的「宏动作」（<c>"Type": "macro"</c> + <c>MacroId</c>）连同它的宏文件移进 <c>trash/</c>。
    /// </summary>
    /// <remarks>
    /// 这个形状已经没有对应类型了：读进来是一个没有动作体的空动作，任何入口都跑不动，
    /// 而 <see cref="Core.Storage.ActionStore"/> 又会把它当成正常动作写回去。所以启动时（读配置之前）
    /// 先整体搬走——不迁移、不猜语义，用户重录一遍即可。搬完 <see cref="CollectGarbage"/> 会顺带清掉
    /// 不再被引用的元数据，<c>ActionRemoval.RepairReferences</c> 会清掉面板 / 快捷键里的悬空引用。
    /// </remarks>
    /// <returns>搬走的旧宏动作数。</returns>
    public static int RetireLegacyMacroActions(string anchorPath)
    {
        try
        {
            string root = Core.Storage.ActionStore.Root(anchorPath);
            if (!Directory.Exists(root)) return 0;

            var actionDirs = new List<string>();
            var macroIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (string dir in Directory.GetDirectories(root))
            {
                string file = Path.Combine(dir, "action.json");
                if (!File.Exists(file)) continue;
                if (!IsLegacyMacroFile(file, out string? macroId)) continue;

                actionDirs.Add(dir);
                if (!string.IsNullOrWhiteSpace(macroId)) macroIds.Add(macroId!);
            }

            if (actionDirs.Count == 0) return 0;

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(Core.Storage.ActionStore.TrashDir(anchorPath), stamp, "legacy-macro");

            foreach (string dir in actionDirs)
            {
                string to = Path.Combine(target, "actions", Path.GetFileName(dir));
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    Directory.Move(dir, to);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("MacroStore.RetireLegacyMacroActions", ex);
                }
            }

            foreach (string macroId in macroIds)
            {
                string from = PathOf(macroId);
                if (!File.Exists(from)) continue;
                try
                {
                    string to = Path.Combine(target, "macros", macroId + ".json");
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                }
                catch (Exception ex)
                {
                    CrashLog.Write("MacroStore.RetireLegacyMacroActions", ex);
                }
            }

            AppLog.Warn("Macro", $"旧版宏动作 {actionDirs.Count} 个已移进 trash/{stamp}/legacy-macro（请重新录制）");
            return actionDirs.Count;
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroStore.RetireLegacyMacroActions", ex);
            return 0;
        }
    }

    /// <summary>旧版宏动作文件判定（只读 <c>Type</c> / <c>MacroId</c> 两个字段，不按新模型反序列化）。</summary>
    private static bool IsLegacyMacroFile(string path, out string? macroId)
    {
        macroId = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("Type", out var type)) return false;
            if (type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "macro", StringComparison.OrdinalIgnoreCase))
                return false;

            if (doc.RootElement.TryGetProperty("MacroId", out var id) && id.ValueKind == JsonValueKind.String)
                macroId = id.GetString();
            return true;
        }
        catch
        {
            return false;   // 读不出来就别动它（可能是别的问题，交给 ActionStore 记录）
        }
    }

    // ============ 引用回收 ============

    /// <summary>
    /// 按引用回收宏文件：删除「不被任何动作步骤引用」的 <c>macros/*.json</c> 与元数据。
    /// 录制即生成一个「回放宏」动作，删除该动作后它的宏文件自动消失，
    /// 用户不需要（也没有入口）手工管理宏文件。
    /// </summary>
    /// <returns>删除的宏文件数。</returns>
    public static int CollectGarbage()
    {
        try
        {
            Invalidate();
            var live = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in ActionRegistry.All)
                foreach (var step in ProgramWalker.AllSteps(a.Program))
                    if (ProgramWalker.LiteralText(step, "macroId") is { } id && id.Trim().Length > 0)
                        live.Add(id.Trim());

            // 全局子动作里也可能回放宏：只扫动作会把它们引用的宏文件删掉。
            foreach (var def in SubActionStore.LoadAll())
                foreach (var step in ProgramWalker.AllSteps(def))
                    if (ProgramWalker.LiteralText(step, "macroId") is { } id && id.Trim().Length > 0)
                        live.Add(id.Trim());

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

            MacroFileStore.Modify(cfgPath, file =>
            {
                var macros = file.Macros;
                macros.Items ??= new List<MacroMeta>();
                macros.Items.RemoveAll(m => string.IsNullOrEmpty(m.Id) || !live.Contains(m.Id));
                if (!string.IsNullOrEmpty(macros.LastMacroId) && !live.Contains(macros.LastMacroId))
                    macros.LastMacroId = null;
            });
            Invalidate();
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
