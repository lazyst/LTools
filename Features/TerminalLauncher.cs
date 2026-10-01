using System.Diagnostics;
using System.IO;
using System.Text;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>终端路由器：把「终端 + keepWindow + 命令 + workdir」解析成 <see cref="ProcessStartInfo"/>
/// （对应原版 lib/Utils.ahk BuildCommandString 的执行侧，但终端信息不再 bake 进命令串）。
/// 路径探测：pwsh7/gitbash 探测常见安装位置 + PATH；pwsh5/cmd/wsl 走系统目录；
/// 缺失 shell → 返回错误（由调用方弹 MessageBox + 记日志）；wt 缺失 → 降级到 shell 自身控制台窗口。
/// workdir 为空默认桌面（对齐 AHK RunCommand）。钩子回调在 UI 线程，执行 spawn 到后台线程。</summary>
internal static class TerminalLauncher
{
    private static string? _gitBashOverride; // 配置中的 git bash 路径；空=自动探测

    /// <summary>从 AppConfig 加载终端路径覆盖（启动时调用一次）。</summary>
    public static void LoadFromConfig(AppConfig cfg)
    {
        if (cfg.TerminalPaths.TryGetValue("gitbash", out var v) && !string.IsNullOrWhiteSpace(v))
            _gitBashOverride = v.Trim();
        else
            _gitBashOverride = null;
    }

    /// <summary>写回 Git Bash 路径到配置（终端路径对话框保存调用）。</summary>
    public static void SaveGitBashPath(string path)
    {
        _gitBashOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        ConfigIO.Modify(cfg =>
        {
            if (_gitBashOverride == null) cfg.TerminalPaths.Remove("gitbash");
            else cfg.TerminalPaths["gitbash"] = _gitBashOverride;
        });
    }

    /// <summary>当前 Git Bash 路径（配置或探测；探测不到返回 null）。</summary>
    public static string? GetGitBashPath() => ResolveGitBash();

    /// <summary>各终端探测状态（终端路径对话框展示用）。键=terminal key，值=路径或 null。</summary>
    public static readonly string[] ResolvableTerminals = { "pwsh7", "pwsh5", "cmd", "gitbash", "wslbash", "wt" };

    public static string? Resolve(string terminal) => terminal switch
    {
        "pwsh7" => ResolvePwsh7(),
        "pwsh5" => ResolvePwsh5(),
        "cmd" => ResolveCmd(),
        "gitbash" => ResolveGitBash(),
        "wslbash" => ResolveWsl(),
        "wt" => ResolveWt(),
        _ => null,
    };

    public static string DisplayLabel(string terminal) => terminal switch
    {
        "pwsh7" => "PowerShell 7",
        "pwsh5" => "PowerShell 5",
        "cmd" => "CMD",
        "gitbash" => "Git Bash",
        "wslbash" => "WSL Bash",
        "wt" => "Windows Terminal",
        _ => terminal,
    };

    /// <summary>构造启动信息。terminal=direct 且 cmd 为空 → no-op（Error=null）；
    /// 非 direct 且 cmd 为空 → 仅打开 shell 交互窗口（keepWindow 语义）。</summary>
    public static LaunchResult TryBuildLaunch(string terminal, bool keepWindow, string cmd, string workdir)
    {
        string workdir2 = string.IsNullOrWhiteSpace(workdir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : workdir.Trim();

        if (terminal == "direct" || terminal.Length == 0)
        {
            if (string.IsNullOrWhiteSpace(cmd))
                return new LaunchResult(false, null, null, workdir2, null); // direct 空命令=no-op
            return new LaunchResult(false, null, null, workdir2, null); // direct 由 RunCommand ShellExecute 处理
        }

        string? shell = Resolve(terminal);
        if (shell == null || !File.Exists(shell))
            return new LaunchResult(false, null, null, workdir2,
                $"未安装 {DisplayLabel(terminal)}，请在「终端路径」中配置或安装后重试。");

        string shellToken = QuoteIfNeeded(shell);
        bool empty = string.IsNullOrWhiteSpace(cmd);

        // 空命令 → 仅打开 shell 交互（优先 wt 包装，wt 可用时始终走 wt）
        if (empty)
        {
            string? wt0 = ResolveWt();
            if (wt0 != null && File.Exists(wt0))
            {
                if (string.IsNullOrWhiteSpace(workdir))
                    return new LaunchResult(true, wt0, shellToken, workdir2, null);
                // wt 语法：new-tab 子命令 + --startingDirectory（驼峰，是 new-tab 的参数不是全局选项）
                return new LaunchResult(true, wt0, $"new-tab --startingDirectory \"{workdir2.Replace("\"", "")}\" {shellToken}", workdir2, null);
            }
            return new LaunchResult(true, shell, "", workdir2, null);
        }

        if (!TryBuildShellArgs(terminal, keepWindow, cmd, shell, out string shellArgs))
            return new LaunchResult(false, null, null, workdir2, $"不支持的终端: {terminal}");

        string? wt = ResolveWt();
        if (wt != null && File.Exists(wt))
        {
            // wt 包装：exe=wt，args="<shell> <shellArgs>"。
            // wt 以 ';' 作为子命令分隔符（且不尊重引号），shellArgs 里的 ';'（含 keepWindow 的 "; exec bash"
            // 及用户命令里的 ';'）必须转义为 '\\;' 让 wt 传字面分号给 shell，否则会被拆成多个 tab。
            if (string.IsNullOrWhiteSpace(workdir))
                return new LaunchResult(true, wt, $"{shellToken} {WtEscapeSemicolon(shellArgs)}", workdir2, null);
            // 设了工作目录：用 new-tab 子命令 + --startingDirectory（驼峰，是 new-tab 的参数而非全局选项）
            // 让 wt 在指定目录启动。必须放在 new-tab 之后、命令之前。
            return new LaunchResult(true, wt, $"new-tab --startingDirectory \"{workdir2.Replace("\"", "")}\" {shellToken} {WtEscapeSemicolon(shellArgs)}", workdir2, null);
        }
            return new LaunchResult(true, shell, shellArgs, workdir2, null);
    }

    /// <summary>预览：返回实际将启动的 exe + args 串（direct 返回原始 cmd；非 direct 空命令返回“仅打开 shell”）。</summary>
    public static string BuildPreview(string terminal, bool keepWindow, string cmd, string workdir)
    {
        if (terminal == "direct" || terminal.Length == 0)
            return string.IsNullOrWhiteSpace(cmd) ? "" : cmd;
        var r = TryBuildLaunch(terminal, keepWindow, cmd, workdir);
        if (!r.Ok || r.Exe == null) return $"（无法解析: {r.Error ?? terminal}）";
        var args = r.Args ?? "";
        return args.Length > 0 ? $"{r.Exe} {args}" : $"{r.Exe}（仅打开 {DisplayLabel(terminal)}）";
    }

    // —— shell args 构造 ——
    private static bool TryBuildShellArgs(string terminal, bool keepWindow, string cmd, string shell, out string args)
    {
        args = "";
        // 命令体的引号转义：pwsh/cmd 内嵌 " 双写；bash 内嵌 " 用 \"（bash -c "..." 内）
        switch (terminal)
        {
            case "pwsh7":
                args = keepWindow ? $@"-NoExit -c ""{EscapeDouble(cmd)}""" : $@"-c ""{EscapeDouble(cmd)}""";
                return true;
            case "pwsh5":
                args = keepWindow ? $@"-NoExit -Command ""{EscapeDouble(cmd)}""" : $@"-Command ""{EscapeDouble(cmd)}""";
                return true;
            case "cmd":
                args = keepWindow ? $@"/k ""{EscapeDouble(cmd)}""" : $@"/c ""{EscapeDouble(cmd)}""";
                return true;
            case "gitbash":
                // bash -l -c "<cmd>; exec bash"（keepWindow 执行完落回交互 shell）
                args = keepWindow ? $@"-l -c ""{EscapeBash(cmd)}; exec bash""" : $@"-c ""{EscapeBash(cmd)}""";
                return true;
            case "wslbash":
                // exe=wsl.exe，args="bash <flags>"
                args = keepWindow ? $@"bash -l -c ""{EscapeBash(cmd)}; exec bash""" : $@"bash -c ""{EscapeBash(cmd)}""";
                return true;
            default:
                return false;
        }
    }

    // pwsh/cmd 内嵌双引号双写（与 AHK 行为对齐，简单覆盖大多数场景）
    private static string EscapeDouble(string s) => s.Replace("\"", "\"\"");

    // bash -c "..." 内嵌双引号转义为 \"
    private static string EscapeBash(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string QuoteIfNeeded(string path)
        => path.IndexOf(' ') >= 0 && !(path.StartsWith('"') && path.EndsWith('"')) ? $"\"{path}\"" : path;

    /// <summary>wt 把 ';' 当子命令分隔符（不尊重引号）；把 shellArgs 里的 ';' 转义为 '\\;'
    /// 让 wt 传字面分号给 shell。已转义的 '\\;' 不重复转义。</summary>
    private static string WtEscapeSemicolon(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf(';') < 0) return s;
        var sb = new StringBuilder(s.Length + 4);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ';' && (i == 0 || s[i - 1] != '\\'))
                sb.Append('\\');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // —— 路径探测 ——
    private static string? ResolvePwsh7()
    {
        // 1) PATH 上的 pwsh
        if (FindOnPath("pwsh.exe") is { } p) return p;
        // 2) 常见安装位置
        string? prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(prog))
        {
            var p2 = Path.Combine(prog, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(p2)) return p2;
        }
        var prog86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(prog86))
        {
            var p2 = Path.Combine(prog86, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(p2)) return p2;
        }
        return null;
    }

    private static string? ResolvePwsh5()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var p = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(p) ? p : FindOnPath("powershell.exe");
    }

    private static string? ResolveCmd()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var p = Path.Combine(sys, "cmd.exe");
        return File.Exists(p) ? p : FindOnPath("cmd.exe");
    }

    private static string? ResolveWsl()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var p = Path.Combine(sys, "wsl.exe");
        return File.Exists(p) ? p : FindOnPath("wsl.exe");
    }

    private static string? ResolveGitBash()
    {
        // 1) INI 配置覆盖
        if (!string.IsNullOrWhiteSpace(_gitBashOverride) && File.Exists(_gitBashOverride))
            return _gitBashOverride;
        // 2) 常见安装位置
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "bash.exe"),
        };
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            candidates = candidates.Append(Path.Combine(local, "Programs", "Git", "bin", "bash.exe")).ToArray();
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        // 3) PATH
        return FindOnPath("bash.exe");
    }

    private static string? ResolveWt()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            var p = Path.Combine(local, "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(p)) return p;
        }
        return FindOnPath("wt.exe");
    }

    /// <summary>在 PATH 上查找可执行文件名（逐目录检查存在性），找不到返回 null。</summary>
    private static string? FindOnPath(string fileName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            string full;
            try { full = Path.Combine(dir.Trim('"'), fileName); }
            catch { continue; }
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>启动结果。Ok=false 且 Error!=null 表示不可启动（缺失 shell）；Error=null 表示 direct/空命令，调用方另处理。</summary>
    internal sealed record LaunchResult(bool Ok, string? Exe, string? Args, string? Workdir, string? Error);
}
