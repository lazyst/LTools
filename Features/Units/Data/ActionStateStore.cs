using System.Text.Encodings.Web;
using System.Text.Json;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 跨动作持久状态：<c>&lt;数据根&gt;/state/values.json</c>（一个扁平的「键 → 值」表）。
/// </summary>
/// <remarks>
/// <para>
/// 变量只在一次运行里有效，子动作之间靠参数传递；「上次运行到哪儿」「今天第几次」这类
/// 要跨动作、跨重启记住的东西放这里。所有动作共享同一张表，键名自己定。
/// </para>
/// <para>
/// 进程内缓存 + 写穿：读一次文件后常驻内存，每次写入整份落盘（文件很小，换来的是
/// 任何时刻断电都不会出现半个 JSON）。写入先写 <c>.tmp</c> 再替换，读不到就当空表。
/// </para>
/// </remarks>
internal static class ActionStateStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, Value>? _values;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>状态目录（进备份）。</summary>
    public static string Dir => Path.Combine(AppPaths.DataRoot, "state");

    /// <summary>状态文件。</summary>
    public static string FilePath => Path.Combine(Dir, "values.json");

    public static bool TryGet(string key, out Value value)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new ActionException("状态键不能为空");

        lock (Gate)
        {
            var values = Load();
            if (values.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = Value.Empty;
            return false;
        }
    }

    /// <summary>写入（返回覆盖前的旧值，没有就是 null）。</summary>
    public static Value? Set(string key, Value value)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new ActionException("状态键不能为空");

        lock (Gate)
        {
            var values = Load();
            values.TryGetValue(key, out var old);
            values[key] = value ?? Value.Empty;
            Save(values);
            return old;
        }
    }

    public static bool Remove(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0) throw new ActionException("状态键不能为空");

        lock (Gate)
        {
            var values = Load();
            if (!values.Remove(key)) return false;
            Save(values);
            return true;
        }
    }

    private static Dictionary<string, Value> Load()
    {
        if (_values != null) return _values;

        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, Value>>(json, Json);
                if (parsed != null)
                {
                    _values = new Dictionary<string, Value>(parsed, StringComparer.OrdinalIgnoreCase);
                    return _values;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 文件坏了 / 读不了：当作空表继续，别让一个状态文件拖死整个动作。
            AppLog.Warn("State", $"状态文件读取失败，按空表处理：{ex.Message}");
        }

        _values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        return _values;
    }

    private static void Save(Dictionary<string, Value> values)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(values, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActionException($"写状态文件失败：{ex.Message}", ex);
        }
    }
}
