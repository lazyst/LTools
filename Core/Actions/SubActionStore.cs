using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;
using LTools.Features;

namespace LTools.Core.Actions;

/// <summary>一个子动作被哪个动作的哪一步调用（删除前给用户看的引用清单）。</summary>
public sealed record SubActionReference(string ActionId, string ActionName, string Where);

/// <summary>
/// 全局子动作库：一个子动作一个 JSON 文件，放在数据根 <c>subactions/&lt;id&gt;.json</c>。
/// </summary>
/// <remarks>
/// 与 <c>macros/*.json</c> 同一套约定：文件名就是 id，元数据都在文件内。
/// <b>一个文件 = 一个子动作</b>，没有嵌套；id 就是文件名，也是动作里 <c>sub.run</c> 的参数。
/// <para>
/// 整库在进程内缓存（<see cref="Invalidate"/>）：动作在循环里调上千次也只读一次盘。
/// 本类之外的改动（用户在资源管理器里改文件、导入备份）之后要调一次 <see cref="Invalidate"/>，
/// 设置页的「刷新」已经接了。
/// </para>
/// </remarks>
public static class SubActionStore
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>全局子动作目录。</summary>
    public static string DirectoryPath => AppPaths.SubActionsDir;

    /// <summary>新的子动作 id（就是文件名）：<c>sub-</c> + 8 位十六进制。</summary>
    public static string NewId() => "sub-" + Guid.NewGuid().ToString("N")[..8];

    private static readonly char[] InvalidIdChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>坏文件最多留几份（和配置的 <c>.bad-*</c> 一个规矩）。</summary>
    private const int KeepBadFiles = 3;

    private static readonly object Gate = new();
    private static Dictionary<string, SubActionDef>? _index;

    /// <summary>丢掉进程内缓存。外部改了 <c>subactions/</c>（资源管理器 / 导入备份）之后调用。</summary>
    public static void Invalidate()
    {
        lock (Gate) _index = null;
    }

    /// <summary>按 id 读取全局子动作（大小写不敏感）；不存在返回 null。</summary>
    public static SubActionDef? Load(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.IndexOfAny(InvalidIdChars) >= 0) return null;
        return Index().TryGetValue(id, out var def) ? def : null;
    }

    /// <summary>读取全部全局子动作（坏文件已隔离、跳过），按名称排序。</summary>
    public static IReadOnlyList<SubActionDef> LoadAll() =>
        Index().Values
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>写入全局子动作（id 为空 / 含路径分隔符时报错）。先写 <c>.tmp</c> 再替换，写一半崩了不会毁掉旧文件。</summary>
    public static void Save(SubActionDef def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));

        string id = (def.Id ?? "").Trim();
        if (id.Length == 0)
            throw new ActionException("全局子动作缺少 id");
        if (id.IndexOfAny(InvalidIdChars) >= 0)
            throw new ActionException($"子动作 id 不能包含路径字符：{id}");

        def.Id = id;
        Directory.CreateDirectory(DirectoryPath);

        string file = PathFor(id);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(def, Opt), new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);

        Invalidate();
    }

    /// <summary>删除全局子动作；不存在时什么也不做。</summary>
    public static void Delete(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.IndexOfAny(InvalidIdChars) >= 0) return;

        string file = PathFor(id);
        if (File.Exists(file)) File.Delete(file);

        string tmp = file + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);

        Invalidate();
    }

    /// <summary>
    /// 内容修订号 = 文件字节的 sha256（小写十六进制）；文件不存在返回 null。
    /// </summary>
    /// <remarks>
    /// AI 写入用它做 CAS：<c>expectedRevision</c> 与当前值不一致就拒绝，要求先重读——
    /// 和草稿的 revision 一样是「从磁盘内容重算」，不依赖是谁写的。
    /// </remarks>
    public static string? Revision(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.IndexOfAny(InvalidIdChars) >= 0) return null;

        string file = PathFor(id);
        if (!File.Exists(file)) return null;

        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>从 JSON 文本解析子动作（与磁盘格式同一套选项，MCP 写入用）。</summary>
    public static SubActionDef Deserialize(string json) =>
        JsonSerializer.Deserialize<SubActionDef>(json, Opt)
        ?? throw new ActionException("子动作 JSON 解析结果为空");

    /// <summary>磁盘格式的序列化选项（MCP 解析 <c>JsonElement</c> 时共用，别另写一套命名规则）。</summary>
    internal static JsonSerializerOptions JsonOptions => Opt;

    /// <summary>
    /// 哪些动作在调这个子动作（按动作名 · 步骤位置）。删除前给用户看，
    /// 别只弹一句「调用它的动作之后会提示找不到子动作」。
    /// </summary>
    public static IReadOnlyList<SubActionReference> FindReferences(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return Array.Empty<SubActionReference>();
        return ReferenceIndex().TryGetValue(id, out var list) ? list : Array.Empty<SubActionReference>();
    }

    /// <summary>
    /// 全部动作的引用索引（子动作 id → 引用清单）。设置页列表一次算完所有行，
    /// 不用每个子动作都把动作库扫一遍。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<SubActionReference>> ReferenceIndex()
    {
        var map = new Dictionary<string, List<SubActionReference>>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<(Step Step, string Path, string Label, int Depth)>();

        foreach (var action in ActionRegistry.All)
        {
            steps.Clear();
            ProgramWalker.CollectSteps(action.Program?.Steps ?? Enumerable.Empty<Step>(),
                "/steps", "", 1, steps);

            foreach (var (step, _, label, _) in steps)
            {
                if (!string.Equals(step.Unit, "sub.run", StringComparison.OrdinalIgnoreCase)) continue;
                string? target = ProgramWalker.LiteralText(step, "subAction")?.Trim();
                if (string.IsNullOrEmpty(target)) continue;

                if (!map.TryGetValue(target, out var list))
                    map[target] = list = new List<SubActionReference>();
                list.Add(new SubActionReference(action.Id, action.Name, label));
            }
        }

        return map.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SubActionReference>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>被隔离的坏文件（<c>*.json.bad-时间戳</c>），设置页据此提示「有子动作没读出来」。</summary>
    public static IReadOnlyList<string> BrokenFiles()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<string>();
            return Directory.GetFiles(DirectoryPath, "*.json.bad-*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string PathFor(string id) =>
        Path.Combine(DirectoryPath, (id ?? "").Trim() + ".json");

    // ============ 索引 ============

    private static Dictionary<string, SubActionDef> Index()
    {
        lock (Gate)
        {
            _index ??= Build();
            return _index;
        }
    }

    private static Dictionary<string, SubActionDef> Build()
    {
        var map = new Dictionary<string, SubActionDef>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(DirectoryPath)) return map;

        // GetFiles 先取快照：坏文件会在遍历过程中被改名，边枚举边移动在 Windows 上会炸。
        foreach (string file in Directory.GetFiles(DirectoryPath))
        {
            if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.OrdinalIgnoreCase)) continue;

            var def = ReadFile(file);
            if (def == null) continue;

            // 文件名就是 id：手改过 json 里的 id 时以文件名为准，否则保存会另建一个文件。
            string id = Path.GetFileNameWithoutExtension(file);
            if (!string.IsNullOrWhiteSpace(def.Id) &&
                !string.Equals(def.Id.Trim(), id, StringComparison.OrdinalIgnoreCase))
                AppLog.Warn("SubAction", $"子动作 {id}.json 里的 id 是「{def.Id}」，以文件名为准");

            def.Id = id;
            if (!map.TryAdd(id, def))
                AppLog.Warn("SubAction", $"子动作 id 重复：{id}（{Path.GetFileName(file)} 被忽略）");
        }

        return map;
    }

    private static SubActionDef? ReadFile(string file)
    {
        try
        {
            var def = JsonSerializer.Deserialize<SubActionDef>(File.ReadAllText(file, Encoding.UTF8), Opt);
            if (def == null) throw new JsonException("解析结果是 null");
            return def;
        }
        catch (Exception ex)
        {
            Isolate(file, ex);
            return null;
        }
    }

    /// <summary>坏文件改名成 <c>xxx.json.bad-时间戳</c>：不再参与加载，但内容还在，用户能找回。</summary>
    private static void Isolate(string file, Exception ex)
    {
        try
        {
            string bad = $"{file}.bad-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(file, bad, overwrite: true);
            AppLog.Warn("SubAction", $"子动作文件损坏，已隔离：{Path.GetFileName(bad)}（{ex.Message}）");
            PruneBadFiles();
        }
        catch (Exception moveError)
        {
            CrashLog.Write("SubActionStore.Isolate", moveError);
        }
    }

    private static void PruneBadFiles()
    {
        try
        {
            var bad = Directory.GetFiles(DirectoryPath, "*.json.bad-*")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int i = KeepBadFiles; i < bad.Count; i++) File.Delete(bad[i]);
        }
        catch
        {
            // 清理失败不影响使用
        }
    }
}
