using System.Windows;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Internal;

/// <summary>触发内置功能入口（速记 / 搜索 / 放大镜等）。</summary>
public sealed class InvokeInternalUnit : IStepUnit
{
    /// <summary>可选的命令键（存英文键；<see cref="LTools.Features.InternalActionRegistry"/> 里注册的同名入口）。</summary>
    private static readonly string[] Commands =
    {
        "quickNote.toggle", "quickSearch.run", "magnifier.toggle", "windowPin.toggle",
    };

    private static readonly IReadOnlyDictionary<string, string> CommandLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["quickNote.toggle"] = "速记窗口",
            ["quickSearch.run"] = "快速搜索",
            ["magnifier.toggle"] = "放大镜",
            ["windowPin.toggle"] = "窗口置顶",
        };

    public UnitSpec Spec { get; } = new()
    {
        Key = "internal.invoke",
        Name = "内置功能",
        Category = "LTools 内置功能",
        Icon = "internal",
        Description = "触发一个程序内置的功能入口（速记窗口 / 快速搜索 / 放大镜 / 窗口置顶）",
        Inputs = new[]
        {
            new InputSpec("command", "功能", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText(Commands[0]),
                Options: Commands, Labels: CommandLabels),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string command = args.Text("command").Trim();
        if (command.Length == 0) throw new ActionException("内置功能没有指定命令");
        if (!InternalActionRegistry.TryGet(command, out var handler) || handler == null)
            throw new ActionException($"未注册的内部命令：{command}");

        // 光标坐标为 0 时取当前位置（面板唤起时记录的坐标优先）
        int x = ctx.CursorX, y = ctx.CursorY;
        if (x == 0 && y == 0 && Win32.GetCursorPos(out var pt))
        {
            x = pt.X;
            y = pt.Y;
        }

        // 内置功能触碰 WPF 窗口：同步 dispatch 到 UI 线程，保证步骤严格顺序、异常能上抛。
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            handler(x, y);
            return;
        }

        dispatcher.Invoke(new Action(() => handler(x, y)));
    }
}

/// <summary>上一次被「失败继续」吞掉的错误文本。</summary>
public sealed class LastErrorUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "flow.lastError",
        Name = "上一步的错误",
        Category = "文本与数据",
        Icon = "warning",
        Description = "取本次运行里最近一次被「失败继续」吞掉的错误文本",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("text", "错误文本", ValueKind.text, "lastError", Description: "最近一次失败步骤的错误文本；没有错误时为空"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args) =>
        ctx.SetOutput("text", Value.FromText(ctx.LastError));
}
