using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>截图找文字：区域截图 → 带坐标 OCR → 按包含匹配。</summary>
public sealed class FindTextUnit : IStepUnit
{
    private static readonly string[] Engines = { "windows", "v6tiny", "v6small" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.findText",
        Name = "截图找文字",
        Category = "屏幕",
        Icon = "findText",
        Description = "在区域里找指定的文字，返回命中中心；没找到只是 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "要找的文字", ValueKind.text, Required: true),
            new InputSpec("region", "区域", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("foreground"), Options: ScreenArgs.Regions),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region,
                VisibleWhen: "region == 'rect'"),
            new InputSpec("engine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6tiny"), Options: Engines),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(300), Advanced: true),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "命中文字", ValueKind.text, "foundText", Description: "实际命中的那段文字"),
            new OutputSpec("x", "X", ValueKind.number, "textX", Description: "命中文字中心 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "textY", Description: "命中文字中心 Y（屏幕坐标）"),
            new OutputSpec("found", "找到", ValueKind.@bool, "textFound", Description: "是否找到；没找到不是错误"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string needle = args.Text("text").Trim();
        if (needle.Length == 0) throw new ActionException("截图找文字没有指定要找的文字");

        var probe = ScreenArgs.BuildProbe(
            args, ScreenArgs.ParseRegion(args.Text("region")), ScreenArgs.ParseRect(args));
        probe.Kind = ProbeKind.textSearch;
        probe.Text = needle;
        probe.TimeoutMs = (int)Math.Clamp(args.Number("timeoutMs"), 0, 600_000);
        probe.IntervalMs = (int)Math.Clamp(args.Number("intervalMs"), 50, 5_000);
        probe.OcrEngine = args.Text("engine").Trim();

        var value = LegacyFlow.Evaluate(ctx, legacy => TextLocator.Run(probe, legacy));
        string text = UnitValues.Member(value, "text")?.AsText() ?? "";
        int x = (int)MemberNumber(value, "x");
        int y = (int)MemberNumber(value, "y");
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("value", found
            ? Value.Object(
                ("text", Value.FromText(text)),
                ("number", Value.FromNumber(0)),
                ("x", Value.FromNumber(x)),
                ("y", Value.FromNumber(y)),
                ("found", Value.FromBool(true)))
            : Value.Object(
                ("text", Value.FromText(text)),
                ("found", Value.FromBool(false))));
    }

    private static double MemberNumber(Value value, string name)
    {
        var member = UnitValues.Member(value, name);
        if (member == null) return 0;
        try { return member.AsNumber(); }
        catch (ActionException) { return 0; }
    }
}

/// <summary>读取屏幕某一点的颜色。</summary>
