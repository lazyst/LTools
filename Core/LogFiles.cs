using System.Text;

namespace LTools.Core;

/// <summary>
/// 日志文件的公共小工具：大小滚动、按尾读取、判断哪些文件算日志。
/// </summary>
/// <remarks>
/// <para>
/// 现在有两套日志文件——<c>logs/ltools-*.log</c>（<see cref="AppLog"/> 写的应用日志）与
/// <c>logs/action-*.log</c>（「输出日志」单元写的动作日志）。它们的**清理、滚动、预览**
/// 必须是同一套规则，否则就会出现「设置页写着保留 14 天，但动作日志从来不删」这种坑。
/// 所以规则都收在这里，两个写入方都调它。
/// </para>
/// <para>
/// 滚动规则：单个文件超过 <see cref="MaxBytes"/> 就把 <c>name.log</c> 改名成
/// <c>name.1.log</c>（再超就 <c>.2</c>、<c>.3</c> 顺延），然后继续写新的 <c>name.log</c>。
/// 这样「今天的日志」永远是 <c>name.log</c>，历史按序号排。
/// </para>
/// </remarks>
internal static class LogFiles
{
    /// <summary>单个日志文件的上限（20 MB）；超过就滚动。一天一个文件不设上限会写出几百 MB。</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>日志文件的前缀：<c>ltools-</c> 应用日志、<c>action-</c> 动作日志、<c>last-failure</c> 失败报告。</summary>
    private static readonly string[] Prefixes = { "ltools-", "action-", "last-failure" };

    /// <summary>这个文件名是不是日志（清理 / 预览只认这几种前缀，别把用户放进来的文件删了）。</summary>
    public static bool IsLogFile(string name) =>
        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) &&
        Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>日志行以 <c>yyyy-MM-dd HH:mm:ss.fff</c> 开头；异常栈 / 多行内容的续行不是。</summary>
    public static bool IsHeader(string line) =>
        line.Length >= 20 && line[4] == '-' && line[7] == '-' && line[10] == ' '
        && line[13] == ':' && line[16] == ':';

    /// <summary>
    /// 按「整条记录」过滤：<paramref name="match"/> 只看带时间戳的首行，续行（异常栈、多行内容）
    /// 跟着它上面那条记录一起留或一起丢。失败报告、日志预览、诊断信息共用这一份规则。
    /// </summary>
    public static List<string> KeepEntries(IEnumerable<string> lines, Func<string, bool> match)
    {
        var kept = new List<string>();
        bool keep = false;
        foreach (string line in lines)
        {
            if (IsHeader(line))
            {
                keep = match(line);
                if (keep) kept.Add(line);
            }
            else if (keep)
            {
                kept.Add(line);
            }
        }
        return kept;
    }

    /// <summary>
    /// 超过上限就把文件滚动成 <c>stem.N.log</c>；没超过、读不到、被占用都什么都不做。
    /// **调用前要确保没有打开的写入句柄**（<see cref="AppLog"/> 会先关再调）。
    /// </summary>
    public static void Roll(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;

            string dir = info.DirectoryName ?? ".";
            string stem = Path.GetFileNameWithoutExtension(info.Name);
            string ext = info.Extension;

            for (int n = 1; ; n++)
            {
                string target = Path.Combine(dir, $"{stem}.{n}{ext}");
                if (File.Exists(target)) continue;
                File.Move(path, target);
                return;
            }
        }
        catch
        {
            // 被别的进程占着 / 无权限：继续写原文件，下一次再试。
        }
    }

    /// <summary>
    /// 读文件末尾最多 <paramref name="maxLines"/> 行；文件正在被写也能读（<c>FileShare.ReadWrite</c>），
    /// 不存在 / 读不了返回空数组。
    /// </summary>
    /// <remarks>
    /// 单文件上限 20 MB，整份读一遍再取尾巴最简单也最不容易出错（预览是手动触发的，不是热路径）。
    /// </remarks>
    public static string[] ReadTail(string path, int maxLines)
    {
        if (maxLines <= 0) return Array.Empty<string>();

        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var tail = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == maxLines) tail.Dequeue();
                tail.Enqueue(line);
            }
            return tail.ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
