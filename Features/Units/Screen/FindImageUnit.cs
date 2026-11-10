using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>截图找图：OpenCV 模板匹配 + 多缩放候选 + 可等待。</summary>
public sealed class FindImageUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.findImage",
        Name = "截图找图",
        Category = "屏幕",
        Icon = "image",
        Description = "在区域里匹配模板图，返回命中中心与相似度；没找到只是 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("image", "模板图", Editor: InputEditor.image, Required: true),
            new InputSpec("region", "区域", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("foreground"), Options: ScreenArgs.Regions),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region,
                VisibleWhen: "region == 'rect'"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85), Advanced: true,
                Description: "0–1，越高越严格；找不到时可降到 0.7 左右再试"),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true,
                Description: "找不到时最多等这么久；0 = 只找一次"),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(200), Advanced: true,
                Description: "等待期间每隔多久再找一次"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "matchX", Description: "命中中心 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "matchY", Description: "命中中心 Y（屏幕坐标）"),
            new OutputSpec("score", "相似度", ValueKind.number, "matchScore", Description: "最高相似度（0–1）"),
            new OutputSpec("found", "找到", ValueKind.@bool, "matchFound", Description: "是否找到模板图；没找到不是错误"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string image = ScreenArgs.ImagePath(args);
        if (image.Length == 0) throw new ActionException("截图找图没有指定模板图");

        var probe = ScreenArgs.BuildProbe(
            args, ScreenArgs.ParseRegion(args.Text("region")), ScreenArgs.ParseRect(args));
        probe.Image = image;

        var value = ProbeContext.Evaluate(ctx, legacy => ImageProbe.Run(probe, legacy));
        int x = (int)MemberNumber(value, "x");
        int y = (int)MemberNumber(value, "y");
        double score = MemberNumber(value, "score");
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;

        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("score", Value.FromNumber(score));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("value", found
            ? UnitValues.Match(x, y, score)
            : Value.Object(
                ("text", Value.FromText("")),
                ("found", Value.FromBool(false)),
                ("score", Value.FromNumber(score))));
    }

    private static double MemberNumber(Value value, string name)
    {
        var member = UnitValues.Member(value, name);
        if (member == null) return 0;
        try { return member.AsNumber(); }
        catch (ActionException) { return 0; }
    }
}

/// <summary>文字识别：读一张图片，识别其中的文字。</summary>
