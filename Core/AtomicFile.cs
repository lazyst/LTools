using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace LTools.Core;

/// <summary>
/// 原子写文件：先写 <c>&lt;路径&gt;.tmp</c>、再整体替换目标。
/// </summary>
/// <remarks>
/// <para>
/// 数据根下的 JSON / 文本统一走这里。写到一半被结束进程，目标文件仍是旧的完整内容，
/// 不会留下半个 JSON；失败时顺手把 <c>.tmp</c> 删掉，不把垃圾留在数据根里。
/// </para>
/// <para>
/// **别再手写「tmp = path + ".tmp" / WriteAllText / File.Move」这三行**：
/// 以前数据根里有十几处各写一份，有的漏了建目录、有的失败后 <c>.tmp</c> 一直留着。
/// </para>
/// </remarks>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>写文本并替换目标（目标已存在就覆盖）。</summary>
    public static void WriteAllText(string path, string text) => WriteAllText(path, text, overwrite: true);

    /// <summary>
    /// 写文本并替换目标。
    /// </summary>
    /// <param name="overwrite">
    /// false = 目标已存在就抛 <see cref="IOException"/>（「只创建一次」的写入口靠这个语义）。
    /// </param>
    public static void WriteAllText(string path, string text, bool overwrite)
    {
        string dir = Path.GetDirectoryName(path) ?? "";
        if (dir.Length > 0) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text, Utf8NoBom);
            File.Move(tmp, path, overwrite);
        }
        catch
        {
            try
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
                /* 删不掉也不影响把原来的异常抛出去 */
            }
            throw;
        }
    }

    /// <summary>序列化成 JSON 再原子写。</summary>
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null,
        bool overwrite = true) =>
        WriteAllText(path, JsonSerializer.Serialize(value, options), overwrite);

    /// <summary>一行一条 JSON（运行历史 / 触发记录那种 jsonl），整份重写。</summary>
    public static void WriteJsonLines<T>(string path, IEnumerable<T> items)
    {
        var sb = new StringBuilder();
        foreach (T item in items) sb.AppendLine(JsonSerializer.Serialize(item));
        WriteAllText(path, sb.ToString());
    }
}
