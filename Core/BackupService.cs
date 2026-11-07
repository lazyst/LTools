using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Config;
using LTools.Features;
using LTools.Features.Migration.Legacy;

namespace LTools.Core;

/// <summary>
/// 配置备份（导出 / 导入 .ltbak）：一个 zip 同时打包 <c>LTools.json</c> + 用户资源目录
/// <c>resources/</c> + 速记目录 <c>速记/</c> + 宏事件目录 <c>macros/</c>。
/// 导入为整体替换（仅替换备份包含的项），旧数据先改名为 <c>.bak-时间戳</c>；
/// 导入前由 <see cref="Inspect"/> 扫描可执行文件并列出引用 <c>resources</c> 的动作，
/// 交给调用方弹警告——导入备份等于可能以管理员权限运行外来脚本。
/// 所有方法同步执行且阻塞，调用方应放在后台线程。
/// </summary>
internal static class BackupService
{
    /// <summary>备份文件扩展名（本质 zip）。</summary>
    public const string Extension = ".ltbak";

    /// <summary>导出体积超过该值时调用方应先确认。</summary>
    public const long ExportWarnBytes = 50 * 1024 * 1024;

    private const string FormatId = "ltools-backup";
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

    // —— AppConfig 反序列化复用 AppConfig.Opt 同款转换器 ——
    private static readonly JsonSerializerOptions ConfigOpt = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Manifest
    {
        public string Format { get; set; } = "";
        public int FormatVersion { get; set; }
        public string AppVersion { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public List<string> Includes { get; set; } = new();
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
    }

    /// <summary>导入预检结果：解压到临时目录后的扫描信息；调用方确认后由 <see cref="Import"/> 执行替换。</summary>
    public sealed class ImportPreview
    {
        public string StagingDir { get; init; } = "";
        public BackupManifestInfo Manifest { get; init; } = new();
        public bool IncludeResources { get; init; }
        public bool IncludeNotes { get; init; }
        public bool IncludeMacros { get; init; }
        public int ExecutableCount { get; init; }
        public List<string> ResourceActionRefs { get; init; } = new(); // "名称 — 片段"
        public long SizeBytes { get; init; }
    }

    // ============ 导出 ============

    /// <summary>估算将要打包的字节数（调用方用于体积提示）。</summary>
    public static long EstimateSize(bool includeResources, bool includeNotes)
    {
        long total = FileSize(ConfigLocator.FindPath());
        if (includeResources) total += DirSize(AppPaths.ResourceDir);
        if (includeNotes) total += DirSize(AppPaths.NotesDir);
        total += DirSize(MacroStore.Dir);
        return total;
    }

    /// <summary>导出备份到 destPath；配置始终包含，资源目录 / 速记按参数决定。</summary>
    public static void Export(string destPath, bool includeResources, bool includeNotes)
    {
        string tmp = destPath + ".part-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                // 配置必须总是写入：manifest 已声明包含 config，若用户从未保存过（LTools.json 尚未生成），
                // AddFile 会静默跳过，导致备份「声称含配置、实际没有」，导入时又跳过替换配置。
                // 此处回退读同目录示例 / 内置默认，与应用此刻的内存态一致。
                string cfgPath = ConfigLocator.FindPath();
                if (File.Exists(cfgPath)) AddFile(zip, cfgPath, "LTools.json");
                else AddText(zip, "LTools.json",
                    JsonSerializer.Serialize(AppConfig.Load(cfgPath), ConfigOpt));
                if (includeResources) AddDir(zip, AppPaths.ResourceDir, "resources");
                if (includeNotes) AddDir(zip, AppPaths.NotesDir, "速记");
                // 宏事件文件：存在才打包，旧备份不含 macros 时导入不会清空现有宏。
                bool includeMacros = Directory.Exists(MacroStore.Dir);
                if (includeMacros) AddDir(zip, MacroStore.Dir, "macros");
                AddText(zip, "manifest.json", BuildManifest(includeResources, includeNotes, includeMacros));
            }
            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(tmp, destPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理失败忽略 */ }
            throw;
        }
    }

    // ============ 导入：预检 ============

    /// <summary>解压并校验备份，返回安全扫描信息；不改动任何现有数据。失败抛 <see cref="InvalidDataException"/>。</summary>
    public static ImportPreview Inspect(string srcPath)
    {
        if (!File.Exists(srcPath)) throw new FileNotFoundException("备份文件不存在", srcPath);
        string staging = Path.Combine(Path.GetTempPath(), "ltools-import-" + Guid.NewGuid().ToString("N")[..8]);
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

            string cfgPath = Path.Combine(staging, "LTools.json");
            if (File.Exists(cfgPath))
            {
                try { JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath, Encoding.UTF8), ConfigOpt); }
                catch (Exception ex) { throw new InvalidDataException("LTools.json 无法解析：" + ex.Message); }
            }

            // includes 以 manifest 为准：即使目录为空（zip 无文件条目），导入也要整体替换成空目录
            var includes = manifest.Includes ?? new List<string>();
            bool hasResources = includes.Contains("resources", StringComparer.OrdinalIgnoreCase);
            bool hasNotes = includes.Contains("notes", StringComparer.OrdinalIgnoreCase);
            bool hasMacros = includes.Contains("macros", StringComparer.OrdinalIgnoreCase);

            var refs = new List<string>();
            int execCount = 0;
            if (File.Exists(cfgPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(cfgPath, Encoding.UTF8), ConfigOpt) ?? new AppConfig();
                foreach (var a in cfg.Actions ?? new List<ActionDto>())
                {
                    string? s = LegacyActionSummary.ResourceReference(a);
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (s.Replace('\\', '/').Contains("resources", StringComparison.OrdinalIgnoreCase))
                        refs.Add($"{a.Name} — {Truncate(s, 60)}");
                }
            }
            string resDir = Path.Combine(staging, "resources");
            if (Directory.Exists(resDir))
                foreach (var f in Directory.EnumerateFiles(resDir, "*", SearchOption.AllDirectories))
                    if (ExecExtensions.Contains(Path.GetExtension(f))) execCount++;

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
                },
                IncludeResources = hasResources,
                IncludeNotes = hasNotes,
                IncludeMacros = hasMacros,
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
            // 1) 配置
            string stagedCfg = Path.Combine(preview.StagingDir, "LTools.json");
            if (File.Exists(stagedCfg))
            {
                string? bak = BackupFile(cfgPath, ts);
                restores.Add((cfgPath, bak, false));
                Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);
                File.Copy(stagedCfg, cfgPath, overwrite: true);
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

    private static string BuildManifest(bool includeResources, bool includeNotes, bool includeMacros)
    {
        var includes = new List<string> { "config" };
        if (includeResources) includes.Add("resources");
        if (includeNotes) includes.Add("notes");
        if (includeMacros) includes.Add("macros");
        var m = new Manifest
        {
            Format = FormatId,
            FormatVersion = FormatVersion,
            AppVersion = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0",
            CreatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
            Includes = includes,
        };
        return JsonSerializer.Serialize(m, ManifestOpt);
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

    /// <summary>跳过 .bak- 命名（避免把旧备份嵌进新备份）与写了一半的 .tmp。</summary>
    private static bool ShouldSkip(string name)
        => name.Contains(".bak-", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

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

    private static string? BackupFile(string path, string ts)
    {
        if (!File.Exists(path)) return null;
        string bak = UniqueBackupPath(path, ts);
        File.Copy(path, bak, overwrite: true);
        return bak;
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
