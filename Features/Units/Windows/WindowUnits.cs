using System.Diagnostics;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Windows;

/// <summary>按标题 / 类名查找窗口（与「窗口操作」「条件」共用一套匹配语义）。</summary>
public sealed class FindWindowUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "window.find",
        Name = "查找窗口",
        Category = "窗口与程序",
        Icon = "window",
        Description = "标题包含 + 类名精确，命中最上面的可见窗口",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("title", "标题包含", ValueKind.text),
            new InputSpec("class", "类名", ValueKind.text),
        },
        Outputs = new[]
        {
            new OutputSpec("title", "实际标题", ValueKind.text, "windowTitle", Description: "找到的窗口标题"),
            new OutputSpec("found", "找到", ValueKind.@bool, "windowFound", Description: "是否找到窗口"),
            new OutputSpec("active", "在前台", ValueKind.@bool, Description: "该窗口是否在前台"),
            new OutputSpec("handle", "句柄", ValueKind.number, Description: "窗口句柄"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string title = args.Text("title").Trim();
        string cls = args.Text("class").Trim();
        if (title.Length == 0 && cls.Length == 0)
            throw new ActionException("查找窗口至少要填「标题包含」或「类名」");

        IntPtr hwnd = WindowFinder.Find(title, cls, out string matched);
        bool found = hwnd != IntPtr.Zero;
        bool active = found && hwnd == Win32.GetForegroundWindow();

        ctx.SetOutput("title", Value.FromText(matched));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("active", Value.FromBool(active));
        ctx.SetOutput("handle", Value.FromNumber(hwnd.ToInt64()));
        ctx.SetOutput("value", UnitValues.Window(matched, found, active, hwnd.ToInt64()));
    }
}

/// <summary>读取当前前台窗口。</summary>
public sealed class ForegroundWindowUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "window.foreground",
        Name = "前台窗口",
        Category = "窗口与程序",
        Icon = "foreground",
        Description = "读取当前前台窗口的标题与进程名",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("title", "标题", ValueKind.text, "windowTitle", Description: "前台窗口标题"),
            new OutputSpec("exe", "进程名", ValueKind.text, "windowExe", Description: "前台程序的 exe 名"),
            new OutputSpec("handle", "句柄", ValueKind.number, Description: "窗口句柄"),
            new OutputSpec("found", "有窗口", ValueKind.@bool, Description: "是否取到前台窗口"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            ctx.SetOutput("title", Value.FromText(""));
            ctx.SetOutput("exe", Value.FromText(""));
            ctx.SetOutput("handle", Value.FromNumber(0));
            ctx.SetOutput("found", Value.FromBool(false));
            ctx.SetOutput("value", UnitValues.Window("", false, false));
            return;
        }

        string title = WindowFinder.GetText(hwnd);
        string exe = "";
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var process = Process.GetProcessById((int)pid);
                exe = process.ProcessName + ".exe";
            }
        }
        catch
        {
            // 进程可能刚退出 / 权限不足：标题仍然有用。
        }

        ctx.SetOutput("title", Value.FromText(title));
        ctx.SetOutput("exe", Value.FromText(exe));
        ctx.SetOutput("handle", Value.FromNumber(hwnd.ToInt64()));
        ctx.SetOutput("found", Value.FromBool(true));
        ctx.SetOutput("value", UnitValues.Window(title, true, true, hwnd.ToInt64()));
    }
}

/// <summary>窗口操作：切到前台 / 最大化 / 最小化 / 还原 / 调整大小。</summary>
public sealed class OperateWindowUnit : IStepUnit
{
    private static readonly string[] Operations = { "activate", "maximize", "minimize", "restore", "resize" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "window.operate",
        Name = "窗口操作",
        Category = "窗口与程序",
        Icon = "windowAction",
        Description = "按标题 / 类名找到窗口后切前台、最大化、最小化、还原或调整大小；读回真实状态",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("activate"), Options: Operations),
            new InputSpec("title", "标题包含", ValueKind.text),
            new InputSpec("class", "类名", ValueKind.text),
            new InputSpec("x", "X", ValueKind.number, InputEditor.number, VisibleWhen: "operation == 'resize'"),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number, VisibleWhen: "operation == 'resize'"),
            new InputSpec("w", "宽", ValueKind.number, InputEditor.number, VisibleWhen: "operation == 'resize'"),
            new InputSpec("h", "高", ValueKind.number, InputEditor.number, VisibleWhen: "operation == 'resize'"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "windowX", Description: "操作后的窗口左上角 X"),
            new OutputSpec("y", "Y", ValueKind.number, "windowY", Description: "操作后的窗口左上角 Y"),
            new OutputSpec("w", "宽", ValueKind.number, "windowW", Description: "操作后的窗口宽度"),
            new OutputSpec("h", "高", ValueKind.number, "windowH", Description: "操作后的窗口高度"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string title = args.Text("title").Trim();
        string cls = args.Text("class").Trim();
        if (title.Length == 0 && cls.Length == 0)
            throw new ActionException("窗口操作至少要填「标题包含」或「类名」");

        string op = args.Text("operation").Trim().ToLowerInvariant();
        WindowActionKind kind = op switch
        {
            "activate" => WindowActionKind.activate,
            "maximize" => WindowActionKind.maximize,
            "minimize" => WindowActionKind.minimize,
            "restore" => WindowActionKind.restore,
            "resize" => WindowActionKind.resize,
            _ => throw new ActionException($"未知窗口操作：{op}"),
        };

        int[]? rect = null;
        if (kind == WindowActionKind.resize)
        {
            if (!args.Has("x") || !args.Has("y") || !args.Has("w") || !args.Has("h"))
                throw new ActionException("调整大小需要填写 X / Y / 宽 / 高");

            rect = new[]
            {
                (int)Math.Round(args.Number("x")),
                (int)Math.Round(args.Number("y")),
                (int)Math.Round(args.Number("w")),
                (int)Math.Round(args.Number("h")),
            };
        }

        var result = WindowAction.Run(new WindowActionRequest
        {
            Name = "窗口操作",
            Title = title,
            Class = cls,
            Op = kind,
            Rect = rect,
        });
        ctx.SetOutput("x", Value.FromNumber(result.X ?? 0));
        ctx.SetOutput("y", Value.FromNumber(result.Y ?? 0));
        ctx.SetOutput("w", Value.FromNumber(result.W ?? 0));
        ctx.SetOutput("h", Value.FromNumber(result.H ?? 0));
        ctx.SetOutput("value", UnitValues.Region(
            result.X ?? 0, result.Y ?? 0, result.W ?? 0, result.H ?? 0));
    }
}
