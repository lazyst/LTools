using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>文字点击：在区域里识别文字，找到就点它；找不到按失败处理。</summary>
public sealed class ClickTextUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.clickText",
        Name = "文字点击",
        Category = "输入",
        Icon = "clickText",
        Description = "在区域里识别文字，点击命中的文字；找不到就是这一步失败",
        Inputs = new[]
        {
            new InputSpec("text", "要点的文字", ValueKind.text, Required: true),
            new InputSpec("region", "区域", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("foreground"), Options: LTools.Features.Units.Screen.ScreenArgs.Regions),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region,
                VisibleWhen: "region == 'rect'"),
            new InputSpec("ocrEngine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6tiny"), Options: new[] { "windows", "v6tiny", "v6small" }),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("mouseAction", "点击方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("click"),
                Options: new[] { "click", "doubleClick", "rightClick", "middleClick" }),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "clickX", Description: "点击位置 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "clickY", Description: "点击位置 Y（屏幕坐标）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string needle = args.Text("text").Trim();
        if (needle.Length == 0) throw new ActionException("文字点击还没有指定要点的文字");

        var result = TextClickAction.Run(new TextClickRequest
        {
            Name = "文字点击",
            Text = needle,
            Region = LTools.Features.Units.Screen.ScreenArgs.ParseRegion(args.Text("region")),
            Rect = LTools.Features.Units.Screen.ScreenArgs.ParseRect(args),
            OcrEngine = args.Text("ocrEngine").Trim(),
            TimeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 600_000),
            MouseAction = ParseMouse(args.Text("mouseAction")),
        }, LegacyFlow.Create(ctx));

        ctx.SetOutput("x", Value.FromNumber(result.X ?? 0));
        ctx.SetOutput("y", Value.FromNumber(result.Y ?? 0));
        ctx.SetOutput("value", UnitValues.Point(result.X ?? 0, result.Y ?? 0));
    }

    internal static MouseActionKind ParseMouse(string text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "doubleClick" or "doubleclick" => MouseActionKind.doubleClick,
        "rightClick" or "rightclick" => MouseActionKind.rightClick,
        "middleClick" or "middleclick" => MouseActionKind.middleClick,
        _ => MouseActionKind.click,
    };
}

/// <summary>图片点击：在区域里找模板图，找到就点它；找不到就是这一步失败。</summary>
