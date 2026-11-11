using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;

namespace LTools.Core.Ai;

/// <summary>
/// MCP 工具调用的审计日志（<c>ai/audit.log</c>）：每行「时间 · 工具 · 草稿 · 结果 · 耗时」。
/// </summary>
/// <remarks>
/// 只记元数据，**不记 program 内容、不记令牌**；超过 2 MB 时轮转一份 <c>audit.log.1</c>。
/// 写入失败绝不影响工具调用本身。
/// </remarks>
internal static class AiAudit
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataRoot, "ai", "audit.log");

    /// <summary>追加一行。</summary>
    public static void Write(string tool, string? draftId, string result, long elapsedMs)
    {
        try
        {
            lock (Gate)
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    string old = path + ".1";
                    try
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    catch { /* 轮转失败就继续往原文件追加 */ }
                }

                string draft = string.IsNullOrWhiteSpace(draftId) ? "-" : draftId;
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {tool,-28}  {draft,-14}  {result,-8}  {elapsedMs,6} ms";
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 审计失败不影响调用
        }
    }

    /// <summary>最近 <paramref name="count"/> 行（设置页展示用）。</summary>
    public static IReadOnlyList<string> Tail(int count)
    {
        count = Math.Clamp(count, 1, 500);
        try
        {
            string path = FilePath;
            if (!File.Exists(path)) return Array.Empty<string>();

            var lines = new Queue<string>();
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                lines.Enqueue(line);
                if (lines.Count > count) lines.Dequeue();
            }
            return lines.ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>最近 <paramref name="count"/> 行解析成结构化记录（设置页 5 列表格用）。</summary>
    public static IReadOnlyList<AuditEntry> TailEntries(int count)
    {
        var list = new List<AuditEntry>();
        foreach (string line in Tail(count)) list.Add(Parse(line));
        return list;
    }

    // 写入格式固定：时间(19) + 2 空格 + tool(-28) + 2 空格 + draft(-14) + 2 空格 +
    // result(-8) + 2 空格 + elapsedMs(6) + " ms"。各字段都不含空格，用「2+ 空格」切最稳。
    private static readonly Regex EntryRe = new(
        @"^(?<t>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\s{2,}(?<tool>\S+)\s{2,}(?<draft>\S+)\s{2,}(?<result>\S+)\s{2,}(?<ms>\d+)\s+ms\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 解析一行审计日志。旧格式 / 手改过、解析不了的行整条塞进 <see cref="AuditEntry.Tool"/>，
    /// 保证表格不丢数据（也不因为一行坏日志整页空掉）。
    /// </summary>
    public static AuditEntry Parse(string? line)
    {
        string raw = line ?? "";
        var m = EntryRe.Match(raw);
        if (!m.Success) return new AuditEntry { Tool = raw, Raw = raw };
        return new AuditEntry
        {
            Time = m.Groups["t"].Value,
            Tool = m.Groups["tool"].Value,
            Draft = m.Groups["draft"].Value,
            Result = m.Groups["result"].Value,
            Elapsed = m.Groups["ms"].Value + " ms",
            Raw = raw,
        };
    }
}

/// <summary>一行审计记录（<see cref="AiAudit.TailEntries"/> 的行模型，供设置页表格绑定）。</summary>
internal sealed class AuditEntry
{
    public string Time { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Draft { get; init; } = "";
    public string Result { get; init; } = "";
    public string Elapsed { get; init; } = "";
    public string Raw { get; init; } = "";
}
