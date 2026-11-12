using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.Ai;

/// <summary>草稿的宿主状态（存在 <c>staging/&lt;id&gt;/draft.json</c>，agent 不要改它）。</summary>
internal sealed class DraftMeta
{
    /// <summary>26 位小写 ULID，同时是目录名（见 <c>Core/Ids.cs</c>）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示标题（可以随时被 staging_write 里的 program.name 覆盖）。</summary>
    public string? Title { get; set; }

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";

    /// <summary>最近一次 <see cref="DraftStore.Save"/> 冻结的 revision；null = 还没保存过。</summary>
    public string? SavedRevision { get; set; }

    /// <summary>保存版本号，只在 save / reset 时自增。</summary>
    public int EditVersion { get; set; }

    /// <summary>最近一次保存的 history 目录名（reset 用它回滚）。</summary>
    public string? SavedSnapshot { get; set; }

    /// <summary>复制来源的正式动作 Id。</summary>
    public string? BaseActionId { get; set; }

    /// <summary>复制时来源动作 Program 的内容哈希（promote replace 用它判断用户是否改过）。</summary>
    public string? BaseSourceHash { get; set; }

    /// <summary>来源：<c>mcp</c> / <c>user</c>（只用于展示）。</summary>
    public string Origin { get; set; } = "mcp";

    /// <summary>最近一次保存时 agent / 用户写的摘要。</summary>
    public string? Summary { get; set; }

    /// <summary>采纳后的正式动作 Id（允许再次采纳，用最新值覆盖）。</summary>
    public string? PromotedActionId { get; set; }

    public string? PromotedAt { get; set; }

    /// <summary>草稿此刻的状态：已采纳 / 已保存 / 草稿。</summary>
    [JsonIgnore]
    public DraftStatus Status => PromotedActionId is { Length: > 0 }
        ? DraftStatus.promoted
        : SavedRevision is { Length: > 0 }
            ? DraftStatus.saved
            : DraftStatus.draft;
}

/// <summary>草稿状态（列表徽标用）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum DraftStatus
{
    draft,
    saved,
    promoted,
}

/// <summary>草稿里一个资源文件的描述。</summary>
internal sealed record DraftFile(string Name, long Bytes);

/// <summary>一次读取的完整草稿：宿主状态 + 工作副本 + 文件清单 + 当前内容修订号。</summary>
internal sealed class DraftSnapshot
{
    public required DraftMeta Meta { get; init; }

    public ActionProgram? Program { get; init; }

    /// <summary>当前 <c>program.json</c> + <c>files/</c> 的内容哈希；读不出来时为 null。</summary>
    public string? Revision { get; init; }

    public string Directory { get; init; } = "";

    public IReadOnlyList<DraftFile> Files { get; init; } = Array.Empty<DraftFile>();

    /// <summary>program.json 无法解析时的错误文本。</summary>
    public string? Error { get; init; }
}

/// <summary>revision 不匹配（agent 手里的版本过期）。</summary>
internal sealed class DraftConflictException : Exception
{
    public DraftConflictException(string message) : base(message) { }
}

/// <summary>
/// 暂存区：AI 写出来的动作草稿。目录布局见 <c>docs/AI_AGENT.md</c> §5.1。
/// </summary>
/// <remarks>
/// <para>
/// 草稿不进 <c>LTools.json</c>，一个草稿一个目录：
/// <code>
/// staging/&lt;draftId&gt;/
///   draft.json      宿主状态（本类维护，agent 不要改）
///   program.json    工作副本：ActionProgram（agent 改这个）
///   files/          草稿资源（v1 只接受图片）
///   history/&lt;时间戳-rev&gt;/program.json + files/  最近 3 份已保存版本
/// </code>
/// </para>
/// <para>
/// <b>revision 必须能从文件内容重算</b>：agent 既可以用 MCP 的写工具，也可以用自己的 fs 工具直接改
/// <c>program.json</c>；只要保存 / 检查时重新算一遍，两种通道的结果一致。CAS 只比较内容哈希，
/// 不依赖任何内存计数。
/// </para>
/// <para>本类不碰 WPF，可在后台线程调用；唯一需要串行的是同一草稿的写操作（用按草稿的锁兜住）。</para>
/// </remarks>
internal static class DraftStore
{
    /// <summary>草稿根目录（数据根下）。</summary>
    public static string Root => Path.Combine(AppPaths.DataRoot, "staging");

    /// <summary>与 <c>LTools.json</c> 一致的 JSON 选项：缩进、中文不转义、枚举字符串。</summary>
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>v1 只接受图片进 <c>files/</c>，避免出现没人回收的孤儿资源。</summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
    };

    /// <summary>同一草稿的写操作串行化（MCP 回调线程与设置页可能同时动）。</summary>
    private static readonly Dictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    private static readonly object LocksGate = new();

    // ============ 路径 ============

    public static string DirectoryOf(string id) => Path.Combine(Root, NormalizeId(id));

    public static string ProgramPath(string id) => Path.Combine(DirectoryOf(id), "program.json");

    public static string MetaPath(string id) => Path.Combine(DirectoryOf(id), "draft.json");

    public static string FilesDir(string id) => Path.Combine(DirectoryOf(id), "files");

    public static string HistoryDir(string id) => Path.Combine(DirectoryOf(id), "history");

    /// <summary>确保草稿根目录存在。</summary>
    public static void EnsureRoot()
    {
        try { System.IO.Directory.CreateDirectory(Root); }
        catch (Exception ex) { AppLog.Warn("Ai", $"创建暂存区目录失败：{ex.Message}"); }
    }

    // ============ 查询 ============

    /// <summary>全部草稿（按更新时间倒序；坏目录跳过）。</summary>
    public static IReadOnlyList<DraftMeta> List()
    {
        if (!System.IO.Directory.Exists(Root)) return Array.Empty<DraftMeta>();

        var list = new List<DraftMeta>();
        foreach (string dir in System.IO.Directory.EnumerateDirectories(Root))
        {
            // 不做向后兼容：旧 12 位 hex 草稿目录不是新格式，不进暂存区列表。
            if (!IsValidId(Path.GetFileName(dir))) continue;
            string metaPath = Path.Combine(dir, "draft.json");
            if (!File.Exists(metaPath)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<DraftMeta>(File.ReadAllText(metaPath, Encoding.UTF8), Json);
                if (meta == null) continue;
                if (string.IsNullOrWhiteSpace(meta.Id)) meta.Id = Path.GetFileName(dir);
                list.Add(meta);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ai", $"草稿元数据损坏，已跳过：{Path.GetFileName(dir)}（{ex.Message}）");
            }
        }

        return list
            .OrderByDescending(m => m.UpdatedAt, StringComparer.Ordinal)
            .ThenByDescending(m => m.CreatedAt, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>按 id 读宿主状态；不存在 / 损坏时返回 null。</summary>
    public static DraftMeta? Find(string id)
    {
        if (!IsValidId(id)) return null;
        string metaPath = MetaPath(id);
        if (!File.Exists(metaPath)) return null;
        try
        {
            var meta = JsonSerializer.Deserialize<DraftMeta>(File.ReadAllText(metaPath, Encoding.UTF8), Json);
            if (meta == null) return null;
            if (string.IsNullOrWhiteSpace(meta.Id)) meta.Id = NormalizeId(id);
            return meta;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读整份草稿（宿主状态 + program + 文件清单 + 当前 revision）。</summary>
    public static DraftSnapshot Read(string id)
    {
        id = NormalizeId(id);
        var meta = Find(id) ?? throw new ActionException($"草稿不存在：{id}");

        ActionProgram? program = null;
        string? error = null;
        string programPath = ProgramPath(id);
        if (File.Exists(programPath))
        {
            try
            {
                program = JsonSerializer.Deserialize<ActionProgram>(File.ReadAllText(programPath, Encoding.UTF8), Json);
                if (program == null) error = "program.json 内容为空";
            }
            catch (Exception ex)
            {
                error = "program.json 解析失败：" + ex.Message;
            }
        }
        else
        {
            error = "program.json 不存在";
        }

        return new DraftSnapshot
        {
            Meta = meta,
            Program = program,
            Error = error,
            Revision = error == null ? ComputeRevision(id) : null,
            Directory = Path.GetFullPath(DirectoryOf(id)),
            Files = ListFiles(id),
        };
    }

    /// <summary>草稿 <c>files/</c> 下的文件清单（相对路径用 /）。</summary>
    public static IReadOnlyList<DraftFile> ListFiles(string id)
    {
        string filesDir = FilesDir(id);
        if (!System.IO.Directory.Exists(filesDir)) return Array.Empty<DraftFile>();

        var list = new List<DraftFile>();
        foreach (string file in System.IO.Directory.EnumerateFiles(filesDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                list.Add(new DraftFile(RelativeOf(filesDir, file), new FileInfo(file).Length));
            }
            catch { /* 单个文件读不到时跳过 */ }
        }

        return list.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ============ 新建 / 删除 ============

    /// <summary>
    /// 新建草稿。给了 <paramref name="copyFromActionId"/> 时复制那个正式动作的 Program
    /// （正式动作只读，复制之后才可改）。
    /// </summary>
    public static DraftMeta Create(string title, string? description = null, string? copyFromActionId = null,
        string origin = "mcp")
    {
        EnsureRoot();

        title = (title ?? "").Trim();
        if (title.Length == 0) throw new ActionException("草稿标题不能为空");

        ActionDto? source = null;
        if (!string.IsNullOrWhiteSpace(copyFromActionId))
        {
            source = ActionRegistry.FindById(copyFromActionId.Trim())
                ?? throw new ActionException($"要复制的动作不存在：{copyFromActionId}");
        }

        ActionProgram program;
        string? baseHash = null;
        if (source != null)
        {
            program = source.Program?.Clone() ?? new ActionProgram();
            baseHash = HashProgram(program);
            if (string.IsNullOrWhiteSpace(program.Name)) program.Name = source.Name;
        }
        else
        {
            program = new ActionProgram { Name = title };
        }

        if (string.IsNullOrWhiteSpace(program.Name)) program.Name = title;
        if (!string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(program.Description))
            program.Description = description.Trim();

        string id = NewId();
        string dir = DirectoryOf(id);
        System.IO.Directory.CreateDirectory(dir);
        System.IO.Directory.CreateDirectory(FilesDir(id));

        string now = Now();
        var meta = new DraftMeta
        {
            Id = id,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
            BaseActionId = source?.Id,
            BaseSourceHash = baseHash,
            Origin = origin,
        };

        WriteJson(MetaPath(id), meta, createNew: true);
        WriteJson(ProgramPath(id), program, createNew: true);
        AppLog.Info("Ai", $"新建草稿 {id}：{title}{(source != null ? $"（复制自 {source.Id}）" : "")}");
        return meta;
    }

    /// <summary>删掉草稿目录（不可恢复）。调用方负责人工确认。</summary>
    public static void Delete(string id)
    {
        id = NormalizeId(id);
        string dir = DirectoryOf(id);
        if (!System.IO.Directory.Exists(dir)) return;

        lock (LockOf(id))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }
        AppLog.Info("Ai", $"删除草稿 {id}");
    }

    // ============ 工作副本写入 ============

    /// <summary>
    /// 整份写入 <c>program.json</c>（CAS）。返回写入后的 revision。
    /// <paramref name="expectedRevision"/> 为 null 时不校验（新建后第一次写）。
    /// </summary>
    public static string WriteProgram(string id, ActionProgram program, string? expectedRevision = null)
    {
        if (program == null) throw new ArgumentNullException(nameof(program));
        id = NormalizeId(id);
        var meta = Find(id) ?? throw new ActionException($"草稿不存在：{id}");

        lock (LockOf(id))
        {
            string? current = ComputeRevision(id);
            RequireRevision(id, current, expectedRevision);

            WriteJson(ProgramPath(id), program);

            if (!string.IsNullOrWhiteSpace(program.Name))
                meta.Title = program.Name.Trim();
            meta.UpdatedAt = Now();

            string? revision = ComputeRevision(id);
            WriteJson(MetaPath(id), meta);
            return revision ?? "";
        }
    }

    /// <summary>
    /// 冻结当前内容为「已保存版本」（CAS），并在 history 里留一份快照。
    /// </summary>
    public static DraftMeta Save(string id, string? expectedRevision = null, string? summary = null)
    {
        id = NormalizeId(id);
        var meta = Find(id) ?? throw new ActionException($"草稿不存在：{id}");

        lock (LockOf(id))
        {
            string? current = ComputeRevision(id)
                ?? throw new ActionException("草稿内容不完整，无法保存（先修好 program.json）");
            RequireRevision(id, current, expectedRevision);

            string snapshot = $"{DateTime.Now:yyyyMMdd-HHmmss}-{current[..12]}";
            Snapshot(id, snapshot);

            meta.SavedRevision = current;
            meta.SavedSnapshot = snapshot;
            meta.EditVersion++;
            meta.UpdatedAt = Now();
            if (!string.IsNullOrWhiteSpace(summary)) meta.Summary = summary.Trim();

            PruneHistory(id);
            WriteJson(MetaPath(id), meta);
            AppLog.Info("Ai", $"保存草稿 {id}：rev {current[..12]}（第 {meta.EditVersion} 版）");
            return meta;
        }
    }

    /// <summary>回到最近一次保存的版本（没有保存过时报错）。</summary>
    public static DraftMeta Reset(string id)
    {
        id = NormalizeId(id);
        var meta = Find(id) ?? throw new ActionException($"草稿不存在：{id}");

        lock (LockOf(id))
        {
            if (string.IsNullOrWhiteSpace(meta.SavedSnapshot))
                throw new ActionException("这个草稿还没有保存过版本，无法还原");

            string snapshotDir = Path.Combine(HistoryDir(id), meta.SavedSnapshot);
            if (!System.IO.Directory.Exists(snapshotDir))
                throw new ActionException($"保存版本的快照已丢失：{meta.SavedSnapshot}");

            string programPath = Path.Combine(snapshotDir, "program.json");
            if (!File.Exists(programPath))
                throw new ActionException($"保存版本的快照已丢失：{meta.SavedSnapshot}/program.json");

            File.Copy(programPath, ProgramPath(id), overwrite: true);

            string filesDir = FilesDir(id);
            if (System.IO.Directory.Exists(filesDir)) System.IO.Directory.Delete(filesDir, recursive: true);
            string snapshotFiles = Path.Combine(snapshotDir, "files");
            if (System.IO.Directory.Exists(snapshotFiles)) CopyDir(snapshotFiles, filesDir);
            else System.IO.Directory.CreateDirectory(filesDir);

            meta.UpdatedAt = Now();
            WriteJson(MetaPath(id), meta);
            AppLog.Info("Ai", $"草稿 {id} 已还原到第 {meta.EditVersion} 版");
            return meta;
        }
    }

    /// <summary>记下采纳结果（草稿本身保留，允许再次采纳）。</summary>
    public static void MarkPromoted(string id, string actionId)
    {
        id = NormalizeId(id);
        var meta = Find(id) ?? throw new ActionException($"草稿不存在：{id}");
        meta.PromotedActionId = actionId;
        meta.PromotedAt = Now();
        meta.UpdatedAt = Now();
        WriteJson(MetaPath(id), meta);
    }

    // ============ 草稿文件（files/） ============

    /// <summary>读草稿文件（相对 <c>files/</c> 的路径）。</summary>
    public static byte[] ReadFile(string id, string relativePath)
    {
        string full = ResolveDraftFile(id, relativePath, mustExist: true);
        return File.ReadAllBytes(full);
    }

    /// <summary>写草稿文件（v1 只允许图片扩展名）。返回写后的绝对路径。</summary>
    public static string WriteFile(string id, string relativePath, byte[] content)
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        id = NormalizeId(id);
        if (Find(id) == null) throw new ActionException($"草稿不存在：{id}");

        string full = ResolveDraftFile(id, relativePath, mustExist: false);
        string ext = Path.GetExtension(full);
        if (!ImageExtensions.Contains(ext))
            throw new ActionException($"草稿资源 v1 只接受图片（{string.Join(" / ", ImageExtensions)}），收到「{ext}」");

        lock (LockOf(id))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, content);

            var meta = Find(id);
            if (meta != null)
            {
                meta.UpdatedAt = Now();
                WriteJson(MetaPath(id), meta);
            }
        }
        return full;
    }

    /// <summary>删草稿文件；不存在时什么也不做。</summary>
    public static bool DeleteFile(string id, string relativePath)
    {
        string full = ResolveDraftFile(id, relativePath, mustExist: false);
        if (!File.Exists(full)) return false;

        lock (LockOf(id))
        {
            File.Delete(full);
            var meta = Find(id);
            if (meta != null)
            {
                meta.UpdatedAt = Now();
                WriteJson(MetaPath(id), meta);
            }
        }
        return true;
    }

    /// <summary>把相对 <c>files/</c> 的路径解析成绝对路径（拒绝越界路径）。</summary>
    public static string ResolveDraftFile(string id, string relativePath, bool mustExist)
    {
        id = NormalizeId(id);
        string rel = NormalizeRelative(relativePath);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(FilesDir(id)));
        string full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ActionException($"草稿文件路径越界：{relativePath}");

        if (mustExist && !File.Exists(full))
            throw new ActionException($"草稿文件不存在：{relativePath}");

        return full;
    }

    // ============ revision ============

    /// <summary>
    /// 从文件内容重算 revision：<c>sha256(规范化 program.json + 排序后的 files 清单与内容哈希)</c>。
    /// program.json 无法解析时返回 null。
    /// </summary>
    public static string? ComputeRevision(string id)
    {
        id = NormalizeId(id);
        string programPath = ProgramPath(id);
        if (!File.Exists(programPath)) return null;

        byte[] canonical;
        try
        {
            var program = JsonSerializer.Deserialize<ActionProgram>(File.ReadAllText(programPath, Encoding.UTF8), Json);
            if (program == null) return null;
            canonical = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(program, Json));
        }
        catch
        {
            return null;
        }

        using var sha = SHA256.Create();
        HashSegment(sha, "program.json", canonical);

        string filesDir = FilesDir(id);
        if (System.IO.Directory.Exists(filesDir))
        {
            var names = System.IO.Directory
                .EnumerateFiles(filesDir, "*", SearchOption.AllDirectories)
                .Select(f => RelativeOf(filesDir, f))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                string full = Path.Combine(filesDir, name.Replace('/', Path.DirectorySeparatorChar));
                using var stream = File.OpenRead(full);
                byte[] hash = SHA256.HashData(stream);
                HashSegment(sha, name.ToLowerInvariant(), hash);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>一个 Program 的内容哈希（promote 判断来源动作是否被用户改过）。</summary>
    public static string HashProgram(ActionProgram program)
    {
        byte[] canonical = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(program, Json));
        return Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant();
    }

    // ============ 内部 ============

    private static object LockOf(string id)
    {
        lock (LocksGate)
        {
            if (!Locks.TryGetValue(id, out var gate))
                Locks[id] = gate = new object();
            return gate;
        }
    }

    private static void RequireRevision(string id, string? current, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return;
        if (current == null)
            throw new DraftConflictException($"草稿 {id} 还没有可用的内容，先重新读取再写");
        if (!string.Equals(expected.Trim(), current, StringComparison.OrdinalIgnoreCase))
            throw new DraftConflictException(
                $"草稿 {id} 的内容已被改动（当前 revision {current[..12]}，期望 {expected.Trim()[..Math.Min(12, expected.Trim().Length)]}）。先重新读取，再基于最新内容修改");
    }

    private static void Snapshot(string id, string name)
    {
        string dir = Path.Combine(HistoryDir(id), name);
        if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true);
        System.IO.Directory.CreateDirectory(dir);

        File.Copy(ProgramPath(id), Path.Combine(dir, "program.json"), overwrite: true);
        string filesDir = FilesDir(id);
        if (System.IO.Directory.Exists(filesDir))
            CopyDir(filesDir, Path.Combine(dir, "files"));
    }

    /// <summary>history 只留最近 3 份（与 <c>.bak-*</c> 的既有约定一致）。</summary>
    private static void PruneHistory(string id, int keep = 3)
    {
        string dir = HistoryDir(id);
        if (!System.IO.Directory.Exists(dir)) return;

        foreach (string old in System.IO.Directory.EnumerateDirectories(dir)
                     .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                     .Skip(keep))
        {
            try { System.IO.Directory.Delete(old, recursive: true); }
            catch { /* 被占用时留着，下次再清 */ }
        }
    }

    private static void WriteJson<T>(string path, T value, bool createNew = false)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) System.IO.Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: !createNew);
    }

    private static void HashSegment(SHA256 sha, string name, byte[] content)
    {
        byte[] head = Encoding.UTF8.GetBytes($"{name}\0{content.Length}\0");
        sha.TransformBlock(head, 0, head.Length, null, 0);
        sha.TransformBlock(content, 0, content.Length, null, 0);
    }

    private static void CopyDir(string src, string dest)
    {
        System.IO.Directory.CreateDirectory(dest);
        foreach (string file in System.IO.Directory.EnumerateFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (string dir in System.IO.Directory.EnumerateDirectories(src))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static string RelativeOf(string root, string file) =>
        Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');

    private static string NormalizeRelative(string? path)
    {
        string rel = (path ?? "").Trim().Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        rel = rel.Trim('/');

        if (rel.Length == 0)
            throw new ActionException("草稿文件路径为空");
        if (rel.Contains(':'))
            throw new ActionException($"草稿文件路径不能是绝对路径：{path}");
        if (rel.Split('/').Any(part => part == ".." || part == "." || part.Length == 0))
            throw new ActionException($"草稿文件路径不合法：{path}");
        return rel;
    }

    /// <summary>新草稿 Id：ULID（不做向后兼容，不再用 12 位 hex）。</summary>
    public static string NewId() => IdFactory.NewId(id => System.IO.Directory.Exists(DirectoryOf(id)));

    /// <summary>id 只认 26 位小写 ULID（目录名，绝不能带路径字符）。</summary>
    public static bool IsValidId(string? id) => IdFactory.IsValid(id);

    private static string NormalizeId(string? id)
    {
        string value = (id ?? "").Trim().ToLowerInvariant();
        if (!IsValidId(value)) throw new ActionException($"草稿 id 不合法：{id}");
        return value;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
