using System.Drawing;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.Units.Screen;

/// <summary>文字识别：读一张图片，或现截一块屏幕区域，识别其中的文字。</summary>
public sealed class OcrUnit : IStepUnit
{
    private static readonly string[] Engines = { "windows", "v6tiny", "v6small" };

    /// <summary>图片来源：图片（默认，来自「截图」或文件）/ 前台窗口 / 整个屏幕 / 固定区域。</summary>
    private static readonly string[] Sources = { "image", "foreground", "screen", "rect" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.ocr",
        Name = "文字识别",
        Category = "屏幕",
        Icon = "ocr",
        Description = "识别文字（系统 OCR 或 PP-OCRv6）：图片来自「截图」或文件，也可以直接识别屏幕上的一块区域",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("source", "图片来源", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("image"), Options: Sources,
                OptionParams: OptionMap.Of(
                    ("image", ["image"]),
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("image", "图片", Editor: InputEditor.image, Required: true),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
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
        string source = args.Text("source").Trim().ToLowerInvariant();
        if (source.Length == 0) source = "image";

        Value value = source == "image"
            ? RecognizeImage(ctx, args)
            : RecognizeScreen(ctx, args, source);

        string text = UnitValues.Member(value, "text")?.AsText() ?? "";
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(text)),
            ("found", Value.FromBool(found))));
    }

    /// <summary>读一张已有的图片（「截图」的输出、文件路径，或变量里的 <c>file</c>）。</summary>
    private static Value RecognizeImage(StepContext ctx, StepArgs args)
    {
        string image = ScreenArgs.ImagePath(args);
        if (image.Length == 0)
            throw new ActionException(
                "文字识别没有指定图片：先用「截图」取一张图，再把它的变量填进来；" +
                "也可以把「图片来源」切成前台窗口 / 整个屏幕 / 固定区域，运行时直接截取");

        var probe = new Probe
        {
            Kind = ProbeKind.ocr,
            OcrImage = image,
            OcrEngine = args.Text("engine").Trim(),
        };

        return ProbeContext.Evaluate(ctx, legacy => OcrProbe.Run(probe, legacy));
    }

    /// <summary>
    /// 屏幕区域：运行时现截一块再识别，**不落盘**（要留下 png 就用「截图」）。
    /// 区域语义与「截图」「找图」共用 <see cref="ScreenRegion"/>：前台窗口 / 整个屏幕 / 固定矩形，
    /// 固定区域为空时明确报错，不退化成整屏（那会把满屏文字都认回来）。
    /// </summary>
    private static Value RecognizeScreen(StepContext ctx, StepArgs args, string source)
    {
        if (ctx.Token.IsCancellationRequested)
            throw new ActionAbortedException("已停止", userCancelled: true);

        ImageRegion region = ScreenArgs.ParseRegion(source);
        int[]? rect = ScreenArgs.ParseRect(args);
        if (region == ImageRegion.rect && (rect == null || rect.Length < 4 || rect[2] <= 0 || rect[3] <= 0))
            throw new ActionException("「文字识别」选了固定区域，但区域还是空的：在「固定区域」那一行框选一下");

        var (x, y, w, h) = ScreenRegion.Resolve(region, rect);
        if (w <= 0 || h <= 0)
            throw new ActionException($"识别区域无效：{x},{y} · {w}×{h}");

        // 自动化指示器是置顶窗口，会被截进画面：截图期间收起，结束后恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            using Bitmap bmp = ScreenCapture.Capture(x, y, w, h);
            return UnitValues.FromVarValue(OcrProbe.RecognizeImage(bmp, args.Text("engine").Trim()));
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }
}
