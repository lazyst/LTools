using System;
using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>图片点击：在区域里找模板图，找到就点它；找不到就是这一步失败。</summary>
public sealed class ClickImageUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.clickImage",
        Name = "图片点击",
        Category = "键鼠与输入",
        Icon = "imageClick",
        Description = "在区域里找模板图并点击命中位置；找不到就是这一步失败",
        Inputs = new[]
        {
            new InputSpec("image", "模板图", ValueKind.text, InputEditor.image, Required: true),
            new InputSpec("region", "区域", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("foreground"), Options: LTools.Features.Units.Screen.ScreenArgs.Regions,
                OptionParams: OptionMap.Of(
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
            new InputSpec("index", "点第几个", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(1),
                Description: "1 = 第一个（从上到下、从左到右）；同一个图标出现多次时用它点第 N 个，超出命中数就是这一步失败"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85), Advanced: true),
            new InputSpec("untilFound", "直到找到", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "勾上就一直找，直到出现并点中才继续（按停止热键结束）；不勾按下面的等待时间"),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), VisibleWhen: "untilFound == false",
                Description: "最多等多久（毫秒）：0 = 只找一次；-1 = 一直等到找到（按停止热键结束）；最多 10 分钟"),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(200), Advanced: true,
                Description: "两次匹配之间隔多久（50–5000）；太小费 CPU，太大反应慢"),
            new InputSpec("mouseAction", "点击方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("click"),
                Options: new[] { "click", "doubleClick", "rightClick", "middleClick" }),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "clickX", Description: "点击位置 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "clickY", Description: "点击位置 Y（屏幕坐标）"),
            new OutputSpec("score", "相似度", ValueKind.number, "clickScore", Description: "模板匹配相似度（0–1）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var result = ImageClickAction.Run(new ImageClickRequest
        {
            Name = "图片点击",
            Image = args.Text("image"),
            Region = LTools.Features.Units.Screen.ScreenArgs.ParseRegion(args.Text("region")),
            Rect = LTools.Features.Units.Screen.ScreenArgs.ParseRect(args),
            MinScore = Math.Clamp(args.Number("minScore"), 0.30, 0.99),
            TimeoutMs = Timeouts.FromArgs(args),
            IntervalMs = (int)Math.Clamp(args.Number("intervalMs"), 50, 5_000),
            MouseAction = ClickTextUnit.ParseMouse(args.Text("mouseAction")),
            Index = (int)Math.Max(1, Math.Round(args.Number("index"))),
        }, ProbeContext.Of(ctx));

        ctx.SetOutput("x", Value.FromNumber(result.X ?? 0));
        ctx.SetOutput("y", Value.FromNumber(result.Y ?? 0));
        ctx.SetOutput("score", Value.FromNumber(result.Score ?? 0));
        ctx.SetOutput("value", UnitValues.Match(
            result.X ?? 0, result.Y ?? 0, result.Score ?? 0));
    }
}

/// <summary>读取当前光标位置（纯取值，不移动光标）。</summary>
