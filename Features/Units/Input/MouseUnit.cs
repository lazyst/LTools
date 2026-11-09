using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>鼠标操作：移动 / 单击 / 双击 / 右键 / 中键 / 滚轮。</summary>
public sealed class MouseUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "move", "click", "doubleClick", "rightClick", "middleClick", "wheelUp", "wheelDown" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "input.mouse",
        Name = "鼠标操作",
        Category = "键鼠与输入",
        Icon = "mouse",
        Description = "移动光标到坐标并点击 / 滚轮；坐标留空表示当前位置",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"), Options: Operations),
            new InputSpec("x", "X", ValueKind.number, InputEditor.number),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number),
            new InputSpec("wheel", "滚轮格数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(3),
                VisibleWhen: "operation == 'wheelUp' || operation == 'wheelDown'"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "mouseX", Description: "操作后的光标 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "mouseY", Description: "操作后的光标 Y（屏幕坐标）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim();
        bool hasX = args.Has("x") && args.Text("x").Trim().Length > 0;
        bool hasY = args.Has("y") && args.Text("y").Trim().Length > 0;

        if (hasX != hasY)
            throw new ActionException("鼠标坐标 X / Y 要同时填写或同时留空");

        if (hasX)
        {
            int x = (int)Math.Round(args.Number("x"));
            int y = (int)Math.Round(args.Number("y"));
            MouseOps.Move(x, y);
            ctx.Wait(10);   // 让光标位置先落定，再发按键
        }

        switch (op)
        {
            case "move":
                break;

            case "click":
                MouseOps.Perform(MouseActionKind.click);
                break;

            case "doubleClick":
                MouseOps.Perform(MouseActionKind.doubleClick);
                break;

            case "rightClick":
                MouseOps.Perform(MouseActionKind.rightClick);
                break;

            case "middleClick":
                MouseOps.Perform(MouseActionKind.middleClick);
                break;

            case "wheelUp":
                MouseOps.Perform(MouseActionKind.wheelUp, WheelDelta(args));
                break;

            case "wheelDown":
                MouseOps.Perform(MouseActionKind.wheelDown, WheelDelta(args));
                break;

            default:
                throw new ActionException($"未知鼠标操作：{op}");
        }

        if (Win32.GetCursorPos(out var point))
        {
            ctx.SetOutput("x", Value.FromNumber(point.X));
            ctx.SetOutput("y", Value.FromNumber(point.Y));
            ctx.SetOutput("value", UnitValues.Point(point.X, point.Y));
        }
    }

    private static int WheelDelta(StepArgs args)
    {
        double notches = Math.Clamp(args.Number("wheel"), 1, 100);
        return (int)Math.Round(notches * 120);
    }
}

/// <summary>文字点击：在区域里识别文字，找到就点它；找不到按失败处理。</summary>
