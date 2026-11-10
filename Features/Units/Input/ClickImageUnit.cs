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
                Default: Value.FromText("foreground"), Options: LTools.Features.Units.Screen.ScreenArgs.Regions),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region,
                VisibleWhen: "region == 'rect'"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85), Advanced: true),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true),
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
            TimeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 600_000),
            MouseAction = ClickTextUnit.ParseMouse(args.Text("mouseAction")),
        }, ProbeContext.Of(ctx));

        ctx.SetOutput("x", Value.FromNumber(result.X ?? 0));
        ctx.SetOutput("y", Value.FromNumber(result.Y ?? 0));
        ctx.SetOutput("score", Value.FromNumber(result.Score ?? 0));
        ctx.SetOutput("value", UnitValues.Match(
            result.X ?? 0, result.Y ?? 0, result.Score ?? 0));
    }
}

/// <summary>读取当前光标位置（纯取值，不移动光标）。</summary>
