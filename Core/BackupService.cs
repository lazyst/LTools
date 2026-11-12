using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Core;

/// <summary>
/// 配置备份（导出 / 导入 .ltbak）：一个 zip 同时打包 <c>configs/</c>（分域配置；旧备份是单文件
/// <c>LTools.json</c>）+ 动作目录 <c>actions/</c>（动作体与它自己的模板图）+ 用户资源目录
/// <c>resources/</c> + 速记目录 <c>速记/</c> + 宏事件目录 <c>macros/</c> + AI 暂存区 <c>staging/</c>。
/// 打包哪些目录由<b>目录白名单</b>（<see cref="BackupDomain"/>）决定，manifest 的 <c>includes</c>
/// 也只声明实际打进包里的域；<c>cache/</c>、<c>ai/</c>、<c>logs/</c> 这类运行数据永远不进备份。
/// 导入为整体替换（仅替换备份包含的项），旧数据先改名为 <c>.bak-时间戳</c>；
/// 导入前由 <see cref="Inspect"/> 扫描可执行文件并列出引用 <c>resources</c> 的动作，
/// 交给调用方弹警告——导入备份等于可能以管理员权限运行外来脚本。
/// 另有 <see cref="ExportAction"/>：只打包一个动作目录（<c>.ltact</c>）。
/// 所有方法同步执行且阻塞，调用方应放在后台线程。
/// </summary>
internal static class BackupService
{
    /// <summary>备份文件扩展名（本质 zip）。</summary>
    public const string Extension = ".ltbak";

    /// <summary>单个动作导出的扩展名（本质 zip：manifest + action.json + files/）。</summary>
    public const string ActionExtension = ".ltact";

    /// <summary>导出体积超过该值时调用方应先确认。</summary>
    public const long ExportWarnBytes = 50 * 1024 * 1024;

    private const string FormatId = "ltools-backup";
    private const string ActionFormatId = "ltools-action";
    private const int FormatVersion = 1;

    /// <summary>视为可执行 / 可被动作以管理员权限运行的扩展名。</summary>
    private static readonly HashSet<string> ExecExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".com", ".scr", ".lnk", ".wsf", ".jar",
    };

    // —— manifest ——
    private static readonly JsonSerializerOptions ManifestOpt = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public string AppVersion { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public List<string> Includes { get; set; } = new();

        /// <summary>单个动作导出（<see cref="ActionFormatId"/>）时的来源动作。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ActionId { get; set; }

        /// <summary>同上的动作名（导出时就近取，导入端不必再解析 action.json）。</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ActionName { get; set; }
    }

    /// <summary>
    /// 一个备份域 = 数据根下一个目录：manifest 键、zip 内目录名、源目录。
    /// </summary>
    /// <remarks>
    /// <b>目录白名单</b>：能进备份的目录只来自这张表（见 <see cref="BuildPlan"/>），
    /// 新增一类要备份的目录就在这里加一行——导出、体积估算、manifest 声明自动跟上，
    /// 不需要再改三处。表里没列的目录（<c>cache/</c>、<c>ai/</c>、<c>logs/</c>…）永远不进备份。
    /// </remarks>
    private sealed record BackupDomain(string Key, string Entry, string Dir);

    /// <summary>一次导出要打包的东西（配置单独处理：旧布局是单文件，新布局是 <c>configs/</c>）。</summary>
    private sealed class ExportPlan
    {
        public string ConfigDir { get; init; } = "";
        public List<BackupDomain> Domains { get; } = new();
    }

    /// <summary>备份清单信息（导入预检结果的一部分，供 UI 展示）。</summary>
    public sealed class BackupManifestInfo
    {
        public string AppVersion { get; init; } = "";
        public string CreatedAt { get; init; } = "";
        public List<string> Includes { get; init; } = new();
        public bool HasResources { get; init; }
        public bool HasNotes { get; init; }
        public bool HasMacros { get; init; }
        public bool HasStaging { get; init; }
        public bool HasActions { get; init; }
        public bool HasState { get; init; }

        /// <summary>备份里带了 <c>subactions/</c> 全局子动作库。</summary>
        public bool HasSubActions { get; init; }
    }

    /// <summary>导入预检结果：解压到临时目录后的扫描信息；调用方确认后由 <see cref="Import"/> 执行替换。</summary>
    public sealed class ImportPreview
    {
        public string StagingDir { get; init; } = "";
        public BackupManifestInfo Manifest { get; init; } = new();
        public bool IncludeResources { get; init; }
        public bool IncludeNotes { get; init; }
        public bool IncludeMacros { get; init; }
        public bool IncludeStaging { get; init; }

        /// <summary>备份里带了 <c>state/</c> 跨动作状态。</summary>
        public bool IncludeState { get; init; }

        /// <summary>备份里带了 <c>configs/</c> 配置目录。</summary>
        public bool IncludeConfig { get; init; }

        /// <summary>备份里带了 <c>actions/</c> 动作目录。</summary>
        public bool IncludeActions { get; init; }

        /// <summary>备份里带了 <c>subactions/</c> 全局子动作库。</summary>
        public bool IncludeSubActions { get; init; }
        public int ExecutableCount { get; init; }
        public List<string> ResourceActionRefs { get; init; } = new(); // "名称 — 片段"
        public long SizeBytes { get; init; }
    }

    // ============ 导出 ============

    /// <summary>估算将要打包的字节数（调用方用于体积提示）。</summary>
    public static long EstimateSize(bool includeResources, bool includeNotes)
    {
        var plan = BuildPlan(includeResources, includeNotes);
        long total = DirSize(plan.ConfigDir);
        foreach (var domain in plan.Domains) total += DirSize(domain.Dir);
        return total;
    }

    /// <summary>
    /// 按目录白名单决定这次打包什么：配置 / 动作总是带上；资源目录、速记按勾选；
    /// 宏事件与 AI 暂存区存在就带上（用户感受不到这两个选项）。
    /// </summary>
    private static ExportPlan BuildPlan(bool includeResources, bool includeNotes)
    {
        string cfgPath = ConfigLocator.FindPath();
        var plan = new ExportPlan { ConfigDir = ConfigBundle.ConfigDir(cfgPath) };

        if (ActionStore.HasStore(cfgPath))
            plan.Domains.Add(new BackupDomain("actions", "actions", ActionStore.Root(cfgPath)));
        if (includeResources)
            plan.Domains.Add(new BackupDomain("resources", "resources", AppPaths.ResourceDir));
        if (includeNotes)
            plan.Domains.Add(new BackupDomain("notes", "速记", AppPaths.NotesDir));
        if (Directory.Exists(MacroStore.Dir))
            plan.Domains.Add(new BackupDomain("macros", "macros", MacroStore.Dir));
        if (Directory.Exists(DraftStore.Root))
            plan.Domains.Add(new BackupDomain("staging", "staging", DraftStore.Root));
        if (Directory.Exists(AppPaths.StateDir))
            plan.Domains.Add(new BackupDomain("state", "state", AppPaths.StateDir));
        // 子动作库：动作里的 sub.run 只存 id，漏了这个目录换机后全部报「找不到子动作」。
        if (Directory.Exists(SubActionStore.DirectoryPath))
            plan.Domains.Add(new BackupDomain("subactions", "subactions", SubActionStore.DirectoryPath));
        return plan;
    }

    /// <summary>导出备份到 destPath；配置始终包含，资源目录 / 速记按参数决定。</summary>
    public static void Export(string destPath, bool includeResources, bool includeNotes)
    {
        // 配置总是打包 configs/；用户从未保存过配置时先按当前内存态补一份，否则备份里会缺配置。
        string cfgPath = ConfigLocator.FindPath();
        if (!ConfigBundle.IsFullySplit(cfgPath))
            ConfigBundle.SaveDomains(cfgPath, AppConfig.Load(cfgPath), ConfigDomain.all);

        var plan = BuildPlan(includeResources, includeNotes);
        WriteZip(destPath, zip =>
        {
            AddDir(zip, plan.ConfigDir, "configs");
            foreach (var domain in plan.Domains) AddDir(zip, domain.Dir, domain.Entry);

            AddText(zip, "manifest.json", BuildManifest(plan));
        });
    }

    /// <summary>
    /// 导出单个动作：<c>manifest.json</c> + <c>action.json</c> + <c>files/…</c>，
    /// 内容就是 <c>actions/&lt;id&gt;/</c> 这一份目录（动作体与它自己的模板图 / 脚本）。
    /// </summary>
    public static void ExportAction(string actionId, string destPath)
    {
        if (string.IsNullOrWhiteSpace(actionId)) throw new ArgumentException("动作 Id 不能为空", nameof(actionId));
        string cfgPath = ConfigLocator.FindPath();
        string json = ActionStore.ActionPath(cfgPath, actionId);
        if (!File.Exists(json)) throw new FileNotFoundException("动作不存在", json);

        string name = "";
        try
        {
            var action = JsonSerializer.Deserialize<ActionDto>(File.ReadAllText(json, Encoding.UTF8), ConfigBundle.Options);
            name = action?.Name ?? "";
        }
        catch { /* 名字只用于 manifest 展示，读不出来不影响导出 */ }

        var manifest = new Manifest
        {
            Format = ActionFormatId,
            FormatVersion = FormatVersion,
            AppVersion = AppVersion,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Includes = new List<string> { "action" },
            ActionId = actionId,
            ActionName = name,
        };
        WriteZip(destPath, zip =>
        {
            AddFile(zip, json, "action.json");
            AddDir(zip, ActionStore.FilesDir(cfgPath, actionId), "files");
            AddText(zip, "manifest.json", JsonSerializer.Serialize(manifest, ManifestOpt));
        });
    }

    // ============ 导入：预检 ============

    /// <summary>解压并校验备份，返回安全扫描信息；不改动任何现有数据。失败抛 <see cref="InvalidDataException"/>。</summary>
    public static ImportPreview Inspect(string srcPath)
    {
        if (!File.Exists(srcPath)) throw new FileNotFoundException("备份文件不存在", srcPath);
        string staging = Path.Combine(Path.GetTempPath(), "ltools-import-" + IdFactory.ShortId(4));
        try
        {
            // .NET 的 ExtractToDirectory 内置 zip-slip（路径越界）校验，越界条目会抛异常
            ZipFile.ExtractToDirectory(srcPath, staging);

            string manifestPath = Path.Combine(staging, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("不是有效的 LTools 备份：缺少 manifest.json");
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath, Encoding.UTF8), ManifestOpt)
                ?? throw new InvalidDataException("manifest.json 解析失败");
            if (!string.Equals(manifest.Format, FormatId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不是有效的 LTools 备份：格式标识不符");
            if (manifest.FormatVersion != FormatVersion)
                throw new InvalidDataException(manifest.FormatVersion > FormatVersion
                    ? "备份来自更新版本，请升级 LTools 后再导入"
                    : "备份格式版本过旧，无法导入");

            // 配置：备份里是 staging/configs/*.json，读不出来 = 备份损坏。
            string cfgPath = Path.Combine(staging, "LTools.json");
            string stagedCfgDir = ConfigBundle.ConfigDir(cfgPath);
            bool hasConfigDir = Directory.Exists(stagedCfgDir);
            if (hasConfigDir)
            {
                var stagedCfg = AppConfig.Load(cfgPath);
                if (stagedCfg.LoadFailed)
                    throw new InvalidDataException("备份里的配置无法解析（或文件已损坏）");
            }

            // includes 以 manifest 为准：即使目录为空（zip 无文件条目），导入也要整体替换成空目录
            var includes = manifest.Includes ?? new List<string>();
            bool hasResources = includes.Contains("resources", StringComparer.OrdinalIgnoreCase);
            bool hasNotes = includes.Contains("notes", StringComparer.OrdinalIgnoreCase);
            bool hasMacros = includes.Contains("macros", StringComparer.OrdinalIgnoreCase);
            bool hasStaging = includes.Contains("staging", StringComparer.OrdinalIgnoreCase);
            bool hasState = includes.Contains("state", StringComparer.OrdinalIgnoreCase);
            bool hasSubActions = includes.Contains("subactions", StringComparer.OrdinalIgnoreCase)
                                 || Directory.Exists(Path.Combine(staging, "subactions"));
            bool hasActions = includes.Contains("actions", StringComparer.OrdinalIgnoreCase)
                              || Directory.Exists(Path.Combine(staging, "actions"));

            var refs = new List<string>();
            int execCount = 0;
            if (hasConfigDir)
            {
                var cfg = AppConfig.Load(cfgPath);
                foreach (var a in cfg.Actions ?? new List<ActionDto>())
                {
                    foreach (string s in ActionReferences.ResourcePaths(a))
                    {
                        if (string.IsNullOrWhiteSpace(s)) continue;
                        if (s.Replace('\\', '/').Contains("resources", StringComparison.OrdinalIgnoreCase))
                            refs.Add($"{a.Name} — {Truncate(s, 60)}");
                    }
                }
            }
            // 可执行文件扫描：resources 与动作目录（actions/<id>/files 也可能放脚本）。
            foreach (string dirName in new[] { "resources", "actions" })
            {
                string scanDir = Path.Combine(staging, dirName);
                if (!Directory.Exists(scanDir)) continue;
                foreach (var f in Directory.EnumerateFiles(scanDir, "*", SearchOption.AllDirectories))
                    if (ExecExtensions.Contains(Path.GetExtension(f))) execCount++;
            }

            return new ImportPreview
            {
                StagingDir = staging,
                Manifest = new BackupManifestInfo
                {
                    AppVersion = manifest.AppVersion,
                    CreatedAt = manifest.CreatedAt,
                    Includes = includes,
                    HasResources = hasResources,
                    HasNotes = hasNotes,
                    HasMacros = hasMacros,
                    HasStaging = hasStaging,
                    HasActions = hasActions,
                    HasState = hasState,
                    HasSubActions = hasSubActions,
                },
                IncludeResources = hasResources,
                IncludeNotes = hasNotes,
                IncludeMacros = hasMacros,
                IncludeStaging = hasStaging,
                IncludeState = hasState,
                IncludeConfig = hasConfigDir,
                IncludeActions = hasActions,
                IncludeSubActions = hasSubActions,
                ExecutableCount = execCount,
                ResourceActionRefs = refs,
                SizeBytes = DirSize(staging),
            };
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* 清理失败忽略 */ }
            throw;
        }
    }

    // ============ 导入：执行替换 ============

    /// <summary>执行整体替换（仅替换备份包含的项）；完成后由 <see cref="Cleanup"/> 清理临时目录。失败抛出并回滚。</summary>
    public static void Import(ImportPreview preview)
    {
        if (preview == null) throw new ArgumentNullException(nameof(preview));
        string ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string cfgPath = ConfigLocator.FindPath();

        // (dest, backup, isDir)：失败时按反序回滚——删除已写入的 dest，把 backup 移回
        var restores = new List<(string dest, string? backup, bool isDir)>();
        try
        {
            // 1) 配置：整体替换 configs/
            string stagedConfigDir = ConfigBundle.ConfigDir(preview.StagingDir);
            string targetConfigDir = ConfigBundle.ConfigDir(cfgPath);

            if (preview.IncludeConfig && Directory.Exists(stagedConfigDir))
                ReplaceDir(stagedConfigDir, targetConfigDir, ts, restores);

            // 1.5) 动作目录（备份声明带了就整体替换；目录缺失 = 备份里是空的，替换为空目录）
            if (preview.IncludeActions)
            {
                string stagedActions = Path.Combine(preview.StagingDir, "actions");
                ReplaceDir(stagedActions, ActionStore.Root(cfgPath), ts, restores);
            }

            // 2) 资源目录（includes 以 manifest 为准；备份里目录缺失 = 备份是空目录，替换为空）
            if (preview.IncludeResources)
                ReplaceDir(Path.Combine(preview.StagingDir, "resources"), AppPaths.ResourceDir, ts, restores);

            // 3) 速记
            if (preview.IncludeNotes)
                ReplaceDir(Path.Combine(preview.StagingDir, "速记"), AppPaths.NotesDir, ts, restores);

            // 4) 宏事件文件
            if (preview.IncludeMacros)
                ReplaceDir(Path.Combine(preview.StagingDir, "macros"), MacroStore.Dir, ts, restores);

            // 5) AI 暂存区草稿（含 history；排除 ai/ 里的令牌）
            if (preview.IncludeStaging)
                ReplaceDir(Path.Combine(preview.StagingDir, "staging"), DraftStore.Root, ts, restores);

            // 6) 跨动作状态（「存状态 / 读状态」单元写的那张表）
            if (preview.IncludeState)
                ReplaceDir(Path.Combine(preview.StagingDir, "state"), AppPaths.StateDir, ts, restores);

            // 7) 全局子动作库（动作里的 sub.run 只存 id，缺了就是一片「找不到子动作」）
            if (preview.IncludeSubActions)
            {
                ReplaceDir(Path.Combine(preview.StagingDir, "subactions"),
                    SubActionStore.DirectoryPath, ts, restores);
                SubActionStore.Invalidate();
            }
        }
        catch
        {
            Rollback(restores);
            throw;
        }

        // 导入成功后再清理更早的残留备份：回滚还需要本次的 .bak，所以放在 try 之后。
        PruneOldBackups();
    }

    /// <summary>清理预检产生的临时目录（导入成功 / 失败 / 用户取消都应调用）。</summary>
    public static void Cleanup(ImportPreview? preview)
    {
        if (preview == null) return;
        try { if (Directory.Exists(preview.StagingDir)) Directory.Delete(preview.StagingDir, true); }
        catch { /* 清理失败忽略 */ }
    }

    // ============ 内部 ============

    private static string BuildManifest(ExportPlan plan)
    {
        var includes = new List<string> { "config" };
        foreach (var domain in plan.Domains) includes.Add(domain.Key);
        var m = new Manifest
        {
            Format = FormatId,
            FormatVersion = FormatVersion,
            AppVersion = AppVersion,
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Includes = includes,
        };
        return JsonSerializer.Serialize(m, ManifestOpt);
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// 先写 <c>.part-*</c> 临时文件，成功后再替换目标：导出到一半失败不会留下半个 zip。
    /// </summary>
    private static void WriteZip(string destPath, Action<ZipArchive> write)
    {
        string tmp = destPath + ".part-" + IdFactory.ShortId(4);
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create)) write(zip);
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败忽略 */ }
            throw;
        }
    }

    private static void AddFile(ZipArchive zip, string path, string entryName)
    {
        if (!File.Exists(path)) return;
        zip.CreateEntryFromFile(path, entryName);
    }

    private static void AddText(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static void AddDir(ZipArchive zip, string root, string entryRoot)
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (ShouldSkip(Path.GetFileName(file))) continue;
            string rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            zip.CreateEntryFromFile(file, entryRoot + "/" + rel);
        }
    }

    /// <summary>跳过现场备份（.bak- / .bad-）与写了一半的 .tmp，避免把旧副本嵌进新备份。</summary>
    private static bool ShouldSkip(string name)
        => name.Contains(".bak-", StringComparison.OrdinalIgnoreCase)
           || name.Contains(".bad-", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static long DirSize(string root)
    {
        if (!Directory.Exists(root)) return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            try { total += new FileInfo(f).Length; } catch { /* 忽略单个文件读取失败 */ }
        return total;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>替换目录：现有 dest 改名 .bak，再把 src 拷贝到 dest（用拷贝而非 Move，兼容跨卷）。
    /// src 不存在（空备份）时 dest 替换为空目录。</summary>
    private static void ReplaceDir(string src, string dest, string ts,
        List<(string dest, string? backup, bool isDir)> restores)
    {
        string? backup = null;
        if (Directory.Exists(dest))
        {
            backup = UniqueBackupPath(dest, ts);
            Directory.Move(dest, backup); // 同卷改名，原子
        }
        restores.Add((dest, backup, true));
        if (Directory.Exists(src)) CopyDir(src, dest);
        else Directory.CreateDirectory(dest);
    }

    /// <summary>
    /// 导入成功后的清理：每导入一次就多一份 <c>.bak-时间戳</c>（resources 可能有几十 MB），
    /// 按「被替换的原路径」分组只留最近 <paramref name="keep"/> 份，其余删除。
    /// </summary>
    private static void PruneOldBackups(int keep = 3)
    {
        try
        {
            var groups = new Dictionary<string, List<FileSystemInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in new DirectoryInfo(AppPaths.DataRoot).EnumerateFileSystemInfos())
            {
                int i = entry.Name.IndexOf(".bak-", StringComparison.OrdinalIgnoreCase);
                if (i <= 0) continue;
                string key = entry.Name[..i];
                if (!groups.TryGetValue(key, out var list))
                    groups[key] = list = new List<FileSystemInfo>();
                list.Add(entry);
            }

            foreach (var list in groups.Values)
            {
                // 名字里的时间戳是 yyyyMMdd-HHmmss，按名字倒序即「最新在前」
                foreach (var old in list.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase).Skip(keep))
                {
                    try
                    {
                        if (old is DirectoryInfo d) d.Delete(recursive: true);
                        else old.Delete();
                    }
                    catch { /* 被占用等：跳过 */ }
                }
            }
        }
        catch { /* 清理失败不影响导入结果 */ }
    }

    /// <summary>生成不与现有文件 / 目录冲突的 .bak-时间戳 路径。</summary>
    private static string UniqueBackupPath(string path, string ts)
    {
        string baseName = path + ".bak-" + ts;
        string candidate = baseName;
        int n = 0;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = baseName + "-" + (++n);
        return candidate;
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.EnumerateDirectories(src))
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    private static void Rollback(List<(string dest, string? backup, bool isDir)> restores)
    {
        for (int i = restores.Count - 1; i >= 0; i--)
        {
            var r = restores[i];
            try
            {
                if (r.isDir) { if (Directory.Exists(r.dest)) Directory.Delete(r.dest, true); }
                else if (File.Exists(r.dest)) File.Delete(r.dest);
                if (r.backup == null) continue;
                if (r.isDir && Directory.Exists(r.backup)) Directory.Move(r.backup, r.dest);
                else if (!r.isDir && File.Exists(r.backup)) File.Move(r.backup, r.dest);
            }
            catch { /* 回滚中单步失败不中断其余步骤 */ }
        }
    }
}
