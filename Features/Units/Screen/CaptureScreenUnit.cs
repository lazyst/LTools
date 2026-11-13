using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>截图：交互框选 / 前台窗口 / 整个屏幕 / 固定区域。</summary>
public sealed class CaptureScreenUnit : IStepUnit
{
    private static readonly string[] Modes = { "interactive", "foreground", "screen", "rect" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.capture",
        Name = "截图",
        Category = "屏幕",
        Icon = "camera",
        Description = "取一张屏幕截图存成 png；交互框选取消时 found = false，不报错",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("mode", "取图方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("interactive"), Options: Modes,
                OptionParams: OptionMap.Of(
                    ("interactive", []),
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "shotX", Description: "截图区域左上角 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "shotY", Description: "截图区域左上角 Y（屏幕坐标）"),
            new OutputSpec("w", "宽", ValueKind.number, "shotW", Description: "截图宽度"),
            new OutputSpec("h", "高", ValueKind.number, "shotH", Description: "截图高度"),
            new OutputSpec("file", "图片文件", ValueKind.text, "shot", Description: "截图 png 的路径，可交给「文字识别」「用默认程序打开文件」或剪贴板"),
            new OutputSpec("found", "取到图", ValueKind.@bool, Description: "是否取到图"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var probe = new Probe
        {
            Kind = ProbeKind.screenshot,
            ScreenshotMode = ParseMode(args.Text("mode")),
            ScreenshotRect = args.Text("rect").Trim(),
        };

        var value = ProbeContext.Evaluate(ctx, legacy => ScreenshotProbe.Run(probe, legacy));

        int x = (int)MemberNumber(value, "x");
        int y = (int)MemberNumber(value, "y");
        int w = (int)MemberNumber(value, "w");
        int h = (int)MemberNumber(value, "h");
        string file = UnitValues.Member(value, "file")?.AsText() ?? "";
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;

        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("w", Value.FromNumber(w));
        ctx.SetOutput("h", Value.FromNumber(h));
        ctx.SetOutput("file", Value.FromText(file));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("value", found
            ? UnitValues.Region(x, y, w, h, file)
            : UnitValues.NotFound());
    }

    private static ScreenshotMode ParseMode(string text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "interactive" => ScreenshotMode.interactive,
        "foreground" => ScreenshotMode.foreground,
        "screen" => ScreenshotMode.screen,
        "rect" => ScreenshotMode.rect,
        _ => throw new ActionException($"截图方式只能是 interactive / foreground / screen / rect（当前 {text}）"),
    };

    private static double MemberNumber(Value value, string name)
    {
        var member = UnitValues.Member(value, name);
        if (member == null) return 0;
        try { return member.AsNumber(); }
        catch (ActionException) { return 0; }
    }
}

/// <summary>截图找图：OpenCV 模板匹配 + 多缩放候选 + 可等待。</summary>
