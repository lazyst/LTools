using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>文字识别：读一张图片，识别其中的文字。</summary>
public sealed class OcrUnit : IStepUnit
{
    private static readonly string[] Engines = { "windows", "v6tiny", "v6small" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.ocr",
        Name = "文字识别",
        Category = "屏幕",
        Icon = "ocr",
        Description = "识别图片里的文字（系统 OCR 或 PP-OCRv6）；图片来自「截图」或文件路径",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("image", "图片", Editor: InputEditor.image, Required: true),
            new InputSpec("engine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6tiny"), Options: Engines),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "文字", ValueKind.text, "text", Description: "识别出的整段文字"),
            new OutputSpec("found", "有文字", ValueKind.@bool, Description: "是否识别到文字"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string image = ScreenArgs.ImagePath(args);
        if (image.Length == 0)
            throw new ActionException("文字识别没有指定图片：先用「截图」取一张图，再把它的变量填进来");

        var probe = new Probe
        {
            Kind = ProbeKind.ocr,
            OcrImage = image,
            OcrEngine = args.Text("engine").Trim(),
        };

        var value = ProbeContext.Evaluate(ctx, legacy => OcrProbe.Run(probe, legacy));
        string text = UnitValues.Member(value, "text")?.AsText() ?? "";
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(text)),
            ("found", Value.FromBool(found))));
    }
}

/// <summary>截图找文字：区域截图 → 带坐标 OCR → 按包含匹配。</summary>
