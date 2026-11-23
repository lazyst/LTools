using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace LTools.Core.Link;

/// <summary>
/// 链路的管道名与握手令牌：执行器启动时确保令牌存在，界面进程从同一个文件读。
/// </summary>
/// <remarks>
/// <para>
/// 令牌落在 <c>state/worker.token</c>（32 位小写十六进制）。**已有合法令牌就复用**——主程序重启后
/// 界面进程还能用它重连；只有文件缺失 / 内容不合法时才重新生成。
/// </para>
/// <para>
/// 安全边界见 <c>docs/PROCESS_SPLIT.md</c> §8：文件只给当前用户读，能挡别的用户与误连，
/// **挡不住同用户的注入**。真正收窄命令面 / 审计在后续步骤做。
/// </para>
/// </remarks>
internal static class LinkToken
{
    /// <summary>令牌文件（执行器写、界面读）。</summary>
    public static string FilePath => System.IO.Path.Combine(AppPaths.StateDir, "worker.token");

    /// <summary>当前登录会话的管道名（两个进程用同一个算法，天然对齐）。</summary>
    public static string PipeName => LinkNames.Pipe(System.Diagnostics.Process.GetCurrentProcess().SessionId);

    /// <summary>读令牌；不存在 / 格式不对返回 null（不抛异常）。</summary>
    public static string? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string text = File.ReadAllText(FilePath).Trim();
            return IsValid(text) ? text : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"读令牌失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>确保令牌存在（执行器启动时调用）：合法就复用，否则生成并写盘。</summary>
    public static string Ensure()
    {
        string? existing = Read();
        if (existing != null) return existing;

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        try
        {
            Directory.CreateDirectory(AppPaths.StateDir);
            File.WriteAllText(FilePath, token);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Link", $"写令牌失败（界面进程将连不上）：{ex.Message}");
        }

        return token;
    }

    private static bool IsValid(string text) => text.Length == 32 && text.All(Uri.IsHexDigit);
}
