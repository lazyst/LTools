using System.IO;

namespace LTools.Core;

/// <summary>
/// 崩溃日志（追加写到 %TEMP%\LTools-crash.log）。
/// 用于常驻输入工具：吞掉单次未捕获异常时不静默丢失，留栈迹供溯源。
/// </summary>
internal static class CrashLog
{
    private static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LTools-crash.log");

    /// <summary>追加一条异常记录。自身永不抛出。</summary>
    public static void Write(string context, System.Exception ex)
    {
        try
        {
            var msg = $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}] "
                      + $"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n\n";
            File.AppendAllText(Path, msg);
        }
        catch
        {
            // 日志失败绝不再抛——避免在异常路径上引发二次崩溃
        }
    }
}
