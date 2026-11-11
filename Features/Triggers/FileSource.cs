using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 文件系统变化事件源（<c>file.change</c>，见 <c>docs/AUTO_TRIGGER.md</c> §13.4/13.5/13.7）。
/// 按 <c>(path, filter, includeSubdirectories)</c> 分组共用 <see cref="FileSystemWatcher"/>；
/// watcher 回调在后台线程，命中时构造一条 <see cref="TriggerEvent"/> 交给统一投递，
/// **不自己筛规则**（<c>events</c> / <c>pathMatch</c> 由 <see cref="EventMatcher"/> 判）。
/// </summary>
/// <remarks>
/// <para>
/// 一次保存常产生多条 <c>changed</c>，这里用 120ms 的同路径同类型合并窗口收一次；
/// 规则级洪泛仍由 <c>DebounceMs</c> 收（§13.7）。网络盘 / 回收站移动同理。
/// </para>
/// <para>
/// 监听目录不存在 / 无权限时**不静默失败**：不为该规则建 watcher（本次运行内等效停用），
/// 记日志并用常驻 toast 提示（<c>key</c> 去重，避免反复保存刷屏）。
/// </para>
/// </remarks>
internal sealed class FileSource : ITriggerSource
{
    /// <summary>同路径同变化类型的合并窗口。</summary>
    private const long MergeWindowMs = 120;

    private readonly object _gate = new();
    private Action<TriggerEvent>? _fire;
    private bool _hasRules;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _recent = new(StringComparer.OrdinalIgnoreCase);

    public void Start(Action<TriggerEvent> fire)
    {
        lock (_gate) _fire = fire;
        Reload();
    }

    /// <summary>按当前 <c>Active</c> 重建 watcher 集合（路径 / 筛选 / 递归变了都能重挂）。</summary>
    public void Reload()
    {
        var rules = TriggerRegistry.Active.Where(r =>
            r.Kind == TriggerKind.@event && r.Enabled
            && string.Equals(r.EventType, "file.change", StringComparison.OrdinalIgnoreCase)).ToList();

        var wanted = new Dictionary<string, (string Path, string Filter, bool Sub)>(StringComparer.OrdinalIgnoreCase);
        var bad = new List<string>();
        foreach (var rule in rules)
        {
            string path = Param(rule, "path");
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                bad.Add(string.IsNullOrWhiteSpace(path)
                    ? (string.IsNullOrWhiteSpace(rule.Note) ? "（未填监听目录）" : rule.Note)
                    : $"{rule.Note}（{path}）");
                continue;
            }
            string filter = Param(rule, "filter");
            if (string.IsNullOrWhiteSpace(filter)) filter = "*.*";
            bool sub = string.Equals(Param(rule, "includeSubdirectories"), "true", StringComparison.OrdinalIgnoreCase);
            string key = path.TrimEnd('\\', '/') + "|" + filter + "|" + sub;
            wanted[key] = (path, filter, sub);
        }

        var toDispose = new List<FileSystemWatcher>();
        lock (_gate)
        {
            _hasRules = rules.Count > 0;
            foreach (string key in _watchers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                toDispose.Add(_watchers[key]);
                _watchers.Remove(key);
            }
            foreach (var (key, spec) in wanted)
            {
                if (_watchers.ContainsKey(key)) continue;   // 没变就不重挂，避免每次保存都抖一下
                FileSystemWatcher? w = null;
                try
                {
                    w = new FileSystemWatcher(spec.Path, spec.Filter)
                    {
                        IncludeSubdirectories = spec.Sub,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                            | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    };
                    // 三类事件分别带 kind，重命名另外带旧路径；闭包由 watcher 持有，不会 GC。
                    w.Created += (_, e) => OnFile("created", e.FullPath, e.Name, "");
                    w.Changed += (_, e) => OnFile("changed", e.FullPath, e.Name, "");
                    w.Deleted += (_, e) => OnFile("deleted", e.FullPath, e.Name, "");
                    w.Renamed += (_, e) => OnFile("renamed", e.FullPath, e.Name, e.OldFullPath);
                    w.EnableRaisingEvents = true;   // 路径失效 / 无权限会在这里抛
                    _watchers[key] = w;
                    w = null;
                }
                catch (Exception ex)
                {
                    bad.Add($"{spec.Path}（{ex.Message}）");
                }
                finally { w?.Dispose(); }
            }
        }

        // Dispose 放在锁外：watcher 的回调可能在等 _gate，锁内 Dispose 会互等死锁。
        foreach (var w in toDispose) DisposeWatcher(w);
        ReportBadPaths(bad);
    }

    public void Stop()
    {
        List<FileSystemWatcher> all;
        lock (_gate)
        {
            _fire = null;
            _hasRules = false;
            all = _watchers.Values.ToList();
            _watchers.Clear();
            _recent.Clear();
        }
        foreach (var w in all) DisposeWatcher(w);   // 同样在锁外
    }

    private void OnFile(string kind, string fullPath, string? name, string oldPath)
    {
        if (!ShouldFire(kind, fullPath)) return;
        Action<TriggerEvent> fire;
        lock (_gate)
        {
            if (!_hasRules || _fire == null) return;
            fire = _fire;
        }

        // name 在递归监听时可能带相对子目录，取最后一段文件名。
        string fileName = string.IsNullOrEmpty(name) ? Path.GetFileName(fullPath) : Path.GetFileName(name);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["path"] = fullPath,
            ["pathMatch"] = fullPath,   // 对齐 Params["pathMatch"]
            ["events"] = kind,          // 对齐 Params["events"]（created;changed;...）
            ["name"] = fileName,
            ["oldPath"] = oldPath,
        };
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggerFilePath"] = Value.FromText(fullPath),
            ["triggerFileName"] = Value.FromText(fileName),
            ["triggerFileChange"] = Value.FromText(kind),
            ["triggerFileOldPath"] = Value.FromText(oldPath),
        };

        fire(new TriggerEvent
        {
            EventType = "file.change",
            Fields = fields,
            Values = values,
            Foreground = null,   // §13.3：用命中瞬间的 ForegroundAppCache.Current
            OwnEvent = false,
        });
    }

    /// <summary>同路径同类型在合并窗口内只投一次，并顺手清理过期项，别让字典无限增长。</summary>
    private bool ShouldFire(string kind, string fullPath)
    {
        string key = kind + "|" + fullPath;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_recent.TryGetValue(key, out long last) && now - last < MergeWindowMs) return false;
            _recent[key] = now;
            if (_recent.Count > 512)
                foreach (string k in _recent.Where(kv => now - kv.Value > 1000).Select(kv => kv.Key).ToList())
                    _recent.Remove(k);
            return true;
        }
    }

    private static string Param(TriggerRule rule, string key)
    {
        if (rule.Params == null) return "";
        foreach (var (k, v) in rule.Params)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v ?? "";
        return "";
    }

    private static void DisposeWatcher(FileSystemWatcher w)
    {
        try { w.EnableRaisingEvents = false; w.Dispose(); }
        catch (Exception ex) { AppLog.Warn("Triggers", "释放 FileSystemWatcher 失败：" + ex.Message); }
    }

    private static void ReportBadPaths(List<string> bad)
    {
        if (bad.Count == 0) return;
        string detail = string.Join("、", bad.Distinct());
        AppLog.Warn("Triggers", "文件系统变化规则监听目录无效，本次未生效：" + detail);
        ToastService.Show("文件系统变化规则监听目录无效（不存在或无权限），这些规则本次未生效：" + detail,
            "自动触发", ToastService.StickyMs, ToastKind.error, key: "trigger:badpath");
    }
}
