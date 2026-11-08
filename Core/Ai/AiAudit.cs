using System.Text;
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
}
