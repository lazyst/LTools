using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using LTools.Core;

namespace LTools.Features;

/// <summary>一次命令执行的请求。</summary>
internal sealed class CommandRequest
{
    public string Command { get; init; } = "";

    /// <summary>direct / pwsh7 / pwsh5 / cmd / gitbash / wslbash / wt。</summary>
    public string Terminal { get; init; } = "direct";

    public bool KeepWindow { get; init; }

    public string Workdir { get; init; } = "";

    /// <summary>日志 / 报错里显示的名字。</summary>
    public string Label { get; init; } = "";

    /// <summary>true = 后台捕获输出（不打开终端）；false = 按终端路由打开。</summary>
    public bool Capture { get; init; }
}

/// <summary>一次命令执行的结果（只有 <see cref="Captured"/> 为真时输出才有效）。</summary>
internal sealed class CommandOutcome
{
    public bool Captured { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    public int ExitCode { get; init; }
}

/// <summary>
/// 运行命令的共用实现：终端路由（复用 <see cref="TerminalLauncher"/>）或后台捕获输出。
/// 旧 <see cref="ActionExecutor"/> 与新的「运行命令」动作单元共用。
/// </summary>
internal static class CommandRunner
{
    /// <summary>单条输出最多保留的字符数（防止命令刷屏把内存吃满）。</summary>
    public const int MaxCapturedChars = 200_000;

    /// <summary>没有流程上下文时，捕获型命令的最长等待时间。</summary>
    public const int MaxCaptureMs = 10 * 60 * 1000;

    private static readonly Encoding? OemEncoding = CreateOemEncoding();

    /// <summary>Git Bash / WSL 的管道输出是 UTF-8（无 BOM）。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static CommandOutcome Run(CommandRequest request, RunContext? ctx)
    {
        string cmd = (request.Command ?? "").Trim();
        if (cmd.Length == 0)
            throw new InvalidOperationException($"缺少要执行的命令: {request.Label}");

        if (request.Capture)
            return Capture(request, cmd, ctx);

        string terminal = request.Terminal ?? "direct";

        // direct / 空 terminal：走 ShellExecute（TerminalLauncher 的 direct 分支为 no-op）
        if (terminal == "direct" || terminal.Length == 0)
        {
            if (request.KeepWindow)
                AppLog.Info("Action", $"「{request.Label}」终端为「直接运行」，没有窗口可保持，已忽略「保持窗口」");
            ProcessLauncher.RunDirect(cmd, request.Workdir);
            return new CommandOutcome { Captured = false };
        }

        var r = TerminalLauncher.TryBuildLaunch(terminal, request.KeepWindow, cmd, request.Workdir);
        if (!r.Ok)
        {
            if (r.Error == null) return new CommandOutcome { Captured = false }; // 空命令仅开 shell 无 exe
            TrayService.Notify(r.Error);
            throw new InvalidOperationException(r.Error);
        }

        Process.Start(ProcessLauncher.Build(r.Exe!, r.Args, r.Workdir));
        return new CommandOutcome { Captured = false };
    }

    // —— 捕获模式 ——

    private static CommandOutcome Capture(CommandRequest request, string cmd, RunContext? ctx)
    {
        if (request.KeepWindow)
            AppLog.Info("Action", $"「{request.Label}」填了输出变量，改为后台捕获执行（不显示窗口），已忽略「保持窗口」");

        string wd = AppPaths.ResolveWorkdir(request.Workdir);
        var psi = BuildCapturePsi(request, cmd, wd);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"无法启动命令: {request.Label}");

        // 先异步把两条流读空，避免子进程写满管道缓冲区后卡住
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        long started = Environment.TickCount64;
        while (!process.HasExited)
        {
            if (ctx != null)
            {
                if (!ctx.Wait(100))
                {
                    TryKill(process);
                    throw new FlowAbortException("已停止", userCancelled: true);
                }
            }
            else
            {
                Thread.Sleep(100);
                if (Environment.TickCount64 - started > MaxCaptureMs)
                {
                    TryKill(process);
                    throw new InvalidOperationException(
                        $"命令运行超过 {MaxCaptureMs / 60000} 分钟，已中止: {request.Label}");
                }
            }
        }

        string stdout = Cap(stdoutTask.GetAwaiter().GetResult());
        string stderr = Cap(stderrTask.GetAwaiter().GetResult());

        return new CommandOutcome
        {
            Captured = true,
            Stdout = stdout.TrimEnd('\r', '\n'),
            Stderr = stderr.TrimEnd('\r', '\n'),
            ExitCode = process.ExitCode,
        };
    }

    /// <summary>
    /// 按所选终端构造捕获用的启动信息：不使用 keepWindow / Windows Terminal 包装，
    /// 直接跑 shell 的「执行一条命令」形式，才能从管道拿到输出。
    /// </summary>
    private static ProcessStartInfo BuildCapturePsi(CommandRequest request, string cmd, string wd)
    {
        string terminal = request.Terminal ?? "direct";

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (terminal is "pwsh7" or "pwsh5" or "gitbash" or "wslbash")
        {
            string? shell = TerminalLauncher.Resolve(terminal);
            if (!string.IsNullOrEmpty(shell) && File.Exists(shell))
            {
                psi.FileName = shell;
                switch (terminal)
                {
                    case "pwsh7":
                    case "pwsh5":
                        psi.ArgumentList.Add("-NoProfile");
                        psi.ArgumentList.Add("-NonInteractive");
                        psi.ArgumentList.Add(terminal == "pwsh7" ? "-c" : "-Command");
                        psi.ArgumentList.Add(cmd);
                        break;
                    case "gitbash":
                        psi.ArgumentList.Add("-c");
                        psi.ArgumentList.Add(cmd);
                        break;
                    case "wslbash":
                        psi.ArgumentList.Add("bash");
                        psi.ArgumentList.Add("-lc");
                        psi.ArgumentList.Add(cmd);
                        break;
                }

                // Git Bash / WSL 固定 UTF-8；cmd 与 PowerShell 的管道输出跟随系统 OEM 代码页。
                bool utf8 = terminal is "gitbash" or "wslbash";
                psi.StandardOutputEncoding = utf8 ? Utf8NoBom : OemEncoding;
                psi.StandardErrorEncoding = utf8 ? Utf8NoBom : OemEncoding;

                if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd))
                    psi.WorkingDirectory = wd;
                return psi;
            }

            AppLog.Warn("Action",
                $"未找到 {TerminalLauncher.DisplayLabel(terminal)}，捕获执行改用 cmd.exe：{request.Label}");
        }

        // direct / cmd / wt（wt 只是终端宿主，捕获时不需要它）
        psi.FileName = "cmd.exe";
        psi.Arguments = "/d /s /c \"" + cmd + "\"";
        psi.StandardOutputEncoding = OemEncoding;
        psi.StandardErrorEncoding = OemEncoding;
        if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd))
            psi.WorkingDirectory = wd;
        return psi;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已退出 / 无权限 */ }
    }

    private static string Cap(string text) => text.Length <= MaxCapturedChars
        ? text
        : text[..MaxCapturedChars] + "\n…（输出过长，已截断）";

    private static Encoding? CreateOemEncoding()
    {
        try
        {
            // .NET 默认不带 OEM 代码页；注册一次即可按系统 OEM 编码解码 cmd 的输出（中文不乱码）。
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return null;   // 拿不到就退回运行时默认编码
        }
    }
}
