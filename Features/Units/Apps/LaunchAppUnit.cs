using System.Diagnostics;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Apps;

/// <summary>启动程序：按数据根 / 工作目录解析相对路径后启动，返回进程号。</summary>
public sealed class LaunchAppUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "app.launch",
        Name = "启动程序",
        Category = "窗口与程序",
        Icon = "play",
        Description = "启动一个可执行文件；相对路径按数据根 / 工作目录解析",
        Inputs = new[]
        {
            new InputSpec("target", "程序", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("args", "参数", ValueKind.text, InputEditor.multiline),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("pid", "进程号", ValueKind.number, "pid", Description: "新进程的 ID；拿不到句柄时为 0"),
            new OutputSpec("started", "已启动", ValueKind.@bool, Description: "是否成功启动"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string target = args.Text("target").Trim();
        if (target.Length == 0) throw new ActionException("启动程序缺少「程序」");

        string workdir = AppPaths.ResolveWorkdir(args.Text("workdir"));
        // CreateProcess 解析相对 exe 用的是父进程 CWD，不是 WorkingDirectory；
        // 先解析成绝对路径，避免开机自启（CWD=System32）时找不到 resources\foo.exe。
        target = AppPaths.ResolveTarget(target, workdir);

        var psi = new ProcessStartInfo(target)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        string arguments = args.Text("args");
        if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
        if (!string.IsNullOrEmpty(workdir) && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;

        var process = Process.Start(psi);
        if (process == null)
        {
            // 复用了已有进程 / 没有新进程：启动本身成功。
            ctx.SetOutput("pid", Value.FromNumber(0));
            ctx.SetOutput("started", Value.FromBool(true));
            ctx.SetOutput("value", Result(0));
            return;
        }

        try
        {
            int pid = 0;
            try { pid = process.Id; } catch { /* 进程可能瞬间退出 */ }

            ctx.SetOutput("pid", Value.FromNumber(pid));
            ctx.SetOutput("started", Value.FromBool(true));
            ctx.SetOutput("value", Result(pid));
        }
        finally
        {
            process.Dispose();
        }
    }

    private static Value Result(int pid) => Value.Object(
        ("text", Value.FromText(pid.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        ("number", Value.FromNumber(pid)),
        ("pid", Value.FromNumber(pid)),
        ("found", Value.FromBool(true)));
}
