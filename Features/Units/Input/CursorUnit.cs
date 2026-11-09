using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>读取当前光标位置（纯取值，不移动光标）。</summary>
public sealed class CursorUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.cursor",
        Name = "光标位置",
        Category = "键鼠与输入",
        Icon = "cursor",
        Description = "读取当前光标在虚拟桌面上的坐标，不移动光标",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "cursorX", Description: "光标 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "cursorY", Description: "光标 Y（屏幕坐标）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        if (!Win32.GetCursorPos(out var point))
            throw new ActionException("读取光标位置失败");

        ctx.SetOutput("x", Value.FromNumber(point.X));
        ctx.SetOutput("y", Value.FromNumber(point.Y));
        ctx.SetOutput("value", UnitValues.Point(point.X, point.Y));
    }
}

/// <summary>回放录制的键鼠宏。</summary>
