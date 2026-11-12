using System.Diagnostics;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 启动进程的共用实现：构造 <see cref="ProcessStartInfo"/>、拆分命令行、直接运行。
/// </summary>
/// <remarks>
/// 「启动程序」动作单元、旧 <see cref="ActionExecutor"/> 的 launchApp / runCommand、
/// 以及新的「运行命令」动作单元共用这里，避免三处各写一套参数解析。
/// </remarks>
internal static class ProcessLauncher
{
    /// <summary>构造 ProcessStartInfo（工作目录仅在存在时设置）。</summary>
    public static ProcessStartInfo Build(string exe, string? args, string? workdir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        if (!string.IsNullOrEmpty(workdir) && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;
        return psi;
    }

    /// <summary>命令行拆分：首段（带引号或到空格）为 exe，其余为参数。</summary>
    public static bool TrySplitCommandLine(string cmd, out string exe, out string args)
    {
        cmd = cmd.Trim();
        exe = "";
        args = "";
        if (cmd.Length == 0) return false;

        if (cmd[0] == '"')
        {
            int end = cmd.IndexOf('"', 1);
            if (end < 0) return false;
            exe = cmd[1..end];
            args = end + 1 < cmd.Length ? cmd[(end + 1)..].Trim() : "";
            return exe.Length > 0;
        }

        int space = cmd.IndexOf(' ');
        if (space < 0)
        {
            exe = cmd;
            return true;
        }

        exe = cmd[..space];
        args = cmd[(space + 1)..].Trim();
        return exe.Length > 0;
    }

    /// <summary>
    /// 「直接运行」：拆 exe + args 后启动，失败回退 ShellExecute
    /// （URL / 文档 / 首段含空格但不是可执行文件）。
    /// </summary>
    public static void RunDirect(string cmd, string workdir)
    {
        string wd = AppPaths.ResolveWorkdir(workdir);
        try
        {
            if (!TrySplitCommandLine(cmd, out string exe, out string args))
                throw new InvalidOperationException("命令为空");

            Process.Start(Build(AppPaths.ResolveTarget(exe, wd), args, wd));
        }
        catch
        {
            Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true, WorkingDirectory = wd });
        }
    }

    /// <summary>启动一个程序（相对路径按数据根 / 工作目录解析），返回进程（可能为 null）。</summary>
    public static Process? StartApp(string target, string? args, string workdir)
    {
        string wd = AppPaths.ResolveWorkdir(workdir);
        string exe = AppPaths.ResolveTarget(target, wd);
        return Process.Start(Build(exe, args, wd));
    }
}
