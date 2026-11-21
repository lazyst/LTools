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
        Category = "窗口与程序",
        Icon = "terminal",
        Description = "执行命令行；捕获模式下返回 stdout / stderr / 退出码，不打开窗口",
        Inputs = new[]
        {
            new InputSpec("command", "命令", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "一行或多行命令，按所选终端执行；可写 ${变量}"),
            new InputSpec("terminal", "终端", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("direct"), Options: Terminals,
                Description: "用哪个 shell 执行（CMD / PowerShell / Git Bash / WSL）"),
            new InputSpec("capture", "捕获输出", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(true),
                Description: "勾上 = 后台执行并收下 stdout / stderr / 退出码；不勾 = 打开窗口直接跑"),
            new InputSpec("keepWindow", "保持窗口", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false), VisibleWhen: "capture == false",
                Description: "跑完不关窗口；捕获模式下没有窗口，这个选项不生效"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "命令的工作目录；留空 = 程序所在目录"),
        },
        Outputs = new[]
        {
            new OutputSpec("stdout", "标准输出", ValueKind.text, "stdout", Description: "命令的标准输出；需要开启「捕获输出」，否则为空"),
            new OutputSpec("stderr", "错误输出", ValueKind.text, Description: "命令的错误输出；需要开启「捕获输出」，否则为空"),
            new OutputSpec("exit", "退出码", ValueKind.number, Description: "退出码，0 表示成功"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "退出码是否为 0"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
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
        }, ProbeContext.Of(ctx));

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

    /// <summary>发出 Kill 之后等它真的退出多久（读回真实状态，别假装成功）。</summary>
    private const int KillVerifyMs = 3000;

    public UnitSpec Spec { get; } = new()
    {
        Key = "app.process",
        Name = "进程操作",
        Category = "窗口与程序",
        Icon = "process",
        Description = "按进程名（不含 .exe）查询数量 / 进程号，或结束进程（结束时等它真的退出，杀不掉就整步报错）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("name", "进程名", ValueKind.text, Required: true,
                Pickers: PickerKind.variable | PickerKind.process | PickerKind.inline | PickerKind.clear),
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("count"), Options: Operations),
        },
        Outputs = new[]
        {
            new OutputSpec("count", "数量", ValueKind.number, "count",
                Description: "匹配到的进程数（结束时是发出结束请求之前的数量）"),
            new OutputSpec("found", "存在", ValueKind.@bool, Description: "是否至少找到一个匹配进程"),
            new OutputSpec("pids", "进程号", ValueKind.list, Description: "匹配进程的 PID 列表"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
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
            var pids = processes.Select(PidOf).Where(id => id > 0).ToList();

            if (op == "kill")
            {
                // 同一个进程只报一次：key = 进程下标（PID 可能读不到，用下标更稳）。
                var failures = new Dictionary<int, string>();
                var killed = new bool[processes.Length];

                // 1. 逐个发 Kill：调用前就已经退出 = 目标已达成，不算失败。
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        if (processes[i].HasExited) continue;
                        processes[i].Kill(entireProcessTree: true);
                        killed[i] = true;
                    }
                    catch (Exception ex)
                    {
                        failures[i] = $"PID {PidOf(processes[i])}：{ex.Message}";
                    }
                }

                // 2. Kill 只是发出请求，不保证立刻退出：读回真实状态，别假装成功。
                //    没发出请求的（本来就已经退出 / 发请求就失败）不用等。
                long deadline = Environment.TickCount64 + KillVerifyMs;
                for (int i = 0; i < processes.Length; i++)
                {
                    if (!killed[i]) continue;

                    int pid = PidOf(processes[i]);
                    try
                    {
                        while (!processes[i].HasExited)
                        {
                            if (Environment.TickCount64 >= deadline)
                            {
                                failures[i] = $"PID {pid} 仍在运行（可能被保护，或需要更高权限）";
                                break;
                            }

                            if (!ctx.Wait(100))
                                throw new ActionAbortedException("已停止", userCancelled: true);
                        }
                    }
                    catch (ActionAbortedException) { throw; }
                    catch (Exception ex) { failures[i] = $"PID {pid}：{ex.Message}"; }
                }

                ctx.SetOutput("count", Value.FromNumber(processes.Length));
                ctx.SetOutput("found", Value.FromBool(processes.Length > 0));
                ctx.SetOutput("pids", Value.FromList(pids.Select(id => Value.FromNumber(id))));
                ctx.SetOutput("value", Result(name, processes.Length, pids));

                if (failures.Count > 0)
                {
                    List<string> reasons = failures.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
                    throw new ActionException(
                        $"结束进程「{name}」有 {reasons.Count} 个没成功：" +
                        string.Join("；", reasons.Take(3)) +
                        (reasons.Count > 3 ? " …" : ""));
                }

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

    /// <summary>取 PID；进程刚好退出时读不到 id，按 0（只影响展示，不影响判定）。</summary>
    private static int PidOf(Process process)
    {
        try { return process.Id; }
        catch { return 0; }
    }

    private static Value Result(string name, int count, List<int> pids) => Value.Object(
        ("text", Value.FromText(name)),
        ("count", Value.FromNumber(count)),
        ("number", Value.FromNumber(count)),
        ("found", Value.FromBool(count > 0)),
        ("pids", Value.FromList(pids.Select(id => Value.FromNumber(id)))));
}
