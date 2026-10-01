using System.Diagnostics;
using System.IO;

namespace LTools.Core;

/// <summary>
/// 开机自启管理（任务计划程序 + highest privileges）。
///
/// 为什么用任务计划而非注册表 Run 键：本应用是 requireAdministrator 提权程序，
/// 注册表 Run 启动提权程序会弹 UAC 确认；任务计划 <c>/sc ONLOGON /rl HIGHEST</c>
/// 可静默以最高权限启动，开机无 UAC 弹窗，体验更好。
///
/// 任务名固定 <c>LTools</c>，<c>/tr</c> 指向当前 exe 全路径。
/// </summary>
internal static class AutoStartService
{
    private const string TaskName = "LTools";

    /// <summary>当前 exe 全路径（用于任务计划 /tr）。</summary>
    private static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    private static bool _legacyCleaned;

    /// <summary>
    /// 是否已启用自启（查询任务计划是否存在）。
    /// 首次查询时顺带清理改名前的旧任务 <c>CapsLockPro</c>——其 /tr 指向已不存在的旧 exe。
    /// </summary>
    public static bool IsEnabled()
    {
        CleanupLegacyTask();
        var psi = new ProcessStartInfo("schtasks", $"/query /tn {TaskName} /fo LIST")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(3000);
            return p.ExitCode == 0;   // 0 = 任务存在
        }
        catch { return false; }
    }

    /// <summary>启用自启：创建 ONLOGON HIGHEST 任务（覆盖已有）。</summary>
    public static bool Enable()
    {
        var exe = ExePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
        // /tr 路径含空格需引号，schtasks 参数本身用引号包裹整个 /tr 值
        var args = $"/create /tn {TaskName} /tr \"\\\"{exe}\\\"\" /sc ONLOGON /rl HIGHEST /f";
        return RunSchtasks(args);
    }

    /// <summary>禁用自启：删除任务。</summary>
    public static bool Disable() => RunSchtasks($"/delete /tn {TaskName} /f");

    /// <summary>一次性删除改名前遗留的自启任务（改名 LTools 前任务名为 CapsLockPro）。</summary>
    private static void CleanupLegacyTask()
    {
        if (_legacyCleaned) return;
        _legacyCleaned = true;
        RunSchtasks("/delete /tn CapsLockPro /f");   // 任务不存在/无权限时静默失败
    }

    private static bool RunSchtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
