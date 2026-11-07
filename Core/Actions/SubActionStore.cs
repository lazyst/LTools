using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;

namespace LTools.Core.Actions;

/// <summary>
/// 全局子动作库：一个子动作一个 JSON 文件，放在数据根 <c>subactions/&lt;id&gt;.json</c>。
/// </summary>
/// <remarks>
/// 与 <c>macros/*.json</c> 同一套约定：文件名就是 id，元数据都在文件内。
/// 动作内子动作不在这里（它们随动作保存在 <see cref="ActionProgram.SubActions"/>），
/// 但解析 id 时可以跨层查（动作内优先，见 <see cref="ActionEngine"/>）。
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

    private static readonly char[] InvalidIdChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>按 id 读取；文件不存在时扫描全部文件找嵌套子动作（大小写不敏感）。</summary>
    public static SubActionDef? Load(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.IndexOfAny(InvalidIdChars) >= 0) return null;

        string file = PathFor(id);
        if (File.Exists(file))
        {
            try
            {
                var def = JsonSerializer.Deserialize<SubActionDef>(
                    File.ReadAllText(file, Encoding.UTF8), Opt);
                if (def != null)
                {
                    if (string.IsNullOrWhiteSpace(def.Id)) def.Id = id;
                    return def;
                }
            }
            catch
            {
                // 坏文件不应让所有调用方崩掉；调用方会按「找不到子动作」报错。
            }
        }

        foreach (var root in LoadAll())
        {
            if (Find(root, id) is { } nested) return nested;
        }

        return null;
    }

    /// <summary>读取全部全局子动作（坏文件跳过）。</summary>
    public static IReadOnlyList<SubActionDef> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath)) return Array.Empty<SubActionDef>();

        var list = new List<SubActionDef>();
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                var def = JsonSerializer.Deserialize<SubActionDef>(
                    File.ReadAllText(file, Encoding.UTF8), Opt);
                if (def == null) continue;
                if (string.IsNullOrWhiteSpace(def.Id)) def.Id = Path.GetFileNameWithoutExtension(file);
                list.Add(def);
            }
            catch
            {
                // 坏文件跳过：其它子动作仍可正常使用。
            }
        }

        return list;
    }

    /// <summary>写入全局子动作（id 为空 / 含路径分隔符时报错）。</summary>
    public static void Save(SubActionDef def)
    {
        if (def == null) throw new ArgumentNullException(nameof(def));

        string id = (def.Id ?? "").Trim();
        if (id.Length == 0)
            throw new ActionException("全局子动作缺少 id");
        if (id.IndexOfAny(InvalidIdChars) >= 0)
            throw new ActionException($"子动作 id 不能包含路径字符：{id}");

        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(PathFor(id), JsonSerializer.Serialize(def, Opt), new UTF8Encoding(false));
    }

    /// <summary>删除全局子动作；不存在时什么也不做。</summary>
    public static void Delete(string id)
    {
        string file = PathFor(id);
        if (File.Exists(file)) File.Delete(file);
    }

    private static string PathFor(string id) =>
        Path.Combine(DirectoryPath, (id ?? "").Trim() + ".json");

    private static SubActionDef? Find(SubActionDef def, string id)
    {
        if (string.Equals((def.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase))
            return def;

        foreach (var child in def.SubActions)
            if (Find(child, id) is { } nested) return nested;

        return null;
    }
}
