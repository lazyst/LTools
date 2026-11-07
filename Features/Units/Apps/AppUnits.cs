using System.Diagnostics;
using LTools.Core.Actions;

namespace LTools.Features.Units.Apps;

/// <summary>运行命令：按终端路由打开，或后台执行并捕获输出。</summary>
public sealed class RunCommandUnit : IStepUnit
{
    private static readonly string[] Terminals =
        { "direct", "pwsh7", "pwsh5", "cmd", "gitbash", "wslbash", "wt" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "app.runCommand",
        Name = "运行命令",
        Category = "程序",
        Icon = "terminal",
        Description = "执行命令行；捕获模式下返回 stdout / stderr / 退出码，不打开窗口",
        Inputs = new[]
        {
            new InputSpec("command", "命令", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("terminal", "终端", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("direct"), Options: Terminals),
            new InputSpec("capture", "捕获输出", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(true)),
            new InputSpec("keepWindow", "保持窗口", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false), VisibleWhen: "capture == false"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("stdout", "标准输出", ValueKind.text, "stdout"),
            new OutputSpec("stderr", "错误输出", ValueKind.text),
            new OutputSpec("exit", "退出码", ValueKind.number),
            new OutputSpec("ok", "成功", ValueKind.@bool),
            new OutputSpec("value", "完整结果", ValueKind.@object),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var outcome = CommandRunner.Run(new CommandRequest
        {
            Command = args.Text("command"),
            Terminal = args.Text("terminal"),
            KeepWindow = args.Bool("keepWindow"),
            Workdir = args.Text("workdir"),
            Label = "运行命令",
            Capture = args.Bool("capture"),
        }, LegacyFlow.Create(ctx));

        if (!outcome.Captured)
        {
            ctx.SetOutput("stdout", Value.FromText(""));
            ctx.SetOutput("stderr", Value.FromText(""));
            ctx.SetOutput("exit", Value.FromNumber(0));
            ctx.SetOutput("ok", Value.FromBool(true));
            ctx.SetOutput("value", UnitValues.CommandResult("", "", 0));
            return;
        }

        ctx.SetOutput("stdout", Value.FromText(outcome.Stdout));
        ctx.SetOutput("stderr", Value.FromText(outcome.Stderr));
        ctx.SetOutput("exit", Value.FromNumber(outcome.ExitCode));
        ctx.SetOutput("ok", Value.FromBool(outcome.ExitCode == 0));
        ctx.SetOutput("value", UnitValues.CommandResult(
            outcome.Stdout, outcome.Stderr, outcome.ExitCode));
    }
}

/// <summary>按进程名查询 / 结束进程。</summary>
public sealed class ProcessUnit : IStepUnit
{
    private static readonly string[] Operations = { "count", "exists", "list", "kill" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "app.process",
        Name = "进程",
        Category = "程序",
        Icon = "process",
        Description = "按进程名（不含 .exe）查询数量 / 进程号，或结束进程",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("name", "进程名", ValueKind.text, Required: true),
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("count"), Options: Operations),
        },
        Outputs = new[]
        {
            new OutputSpec("count", "数量", ValueKind.number, "count"),
            new OutputSpec("found", "存在", ValueKind.@bool),
            new OutputSpec("pids", "进程号", ValueKind.list),
            new OutputSpec("value", "完整结果", ValueKind.@object),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string name = args.Text("name").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        if (name.Length == 0) throw new ActionException("进程名为空");

        string op = args.Text("operation").Trim().ToLowerInvariant();
        var processes = Process.GetProcessesByName(name);
        try
        {
            var pids = processes.Select(p =>
            {
                try { return p.Id; }
                catch { return 0; }
            }).Where(id => id > 0).ToList();

            if (op == "kill")
            {
                foreach (var process in processes)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { /* 已退出 / 无权限 */ }
                }

                ctx.SetOutput("count", Value.FromNumber(processes.Length));
                ctx.SetOutput("found", Value.FromBool(processes.Length > 0));
                ctx.SetOutput("pids", Value.FromList(pids.Select(id => Value.FromNumber(id))));
                ctx.SetOutput("value", Result(name, processes.Length, pids));
                return;
            }

            if (op is not ("count" or "exists" or "list"))
                throw new ActionException($"进程操作只能是 count / exists / list / kill（当前 {op}）");

            ctx.SetOutput("count", Value.FromNumber(processes.Length));
            ctx.SetOutput("found", Value.FromBool(processes.Length > 0));
            ctx.SetOutput("pids", Value.FromList(pids.Select(id => Value.FromNumber(id))));
            ctx.SetOutput("value", Result(name, processes.Length, pids));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Value Result(string name, int count, List<int> pids) => Value.Object(
        ("text", Value.FromText(name)),
        ("count", Value.FromNumber(count)),
        ("number", Value.FromNumber(count)),
        ("found", Value.FromBool(count > 0)),
        ("pids", Value.FromList(pids.Select(id => Value.FromNumber(id)))));
}
