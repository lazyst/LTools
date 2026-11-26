using System;
using System.Linq;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Screen;

/// <summary>截图找文字：区域截图 → 带坐标 OCR → 按包含匹配。</summary>
public sealed class FindTextUnit : IStepUnit
{
    private static readonly string[] Engines = { "windows", "v6tiny", "v6small", "v6medium" };

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
                Default: Value.FromText("foreground"), Options: ScreenArgs.Regions,
                Description: "在哪儿找；框选模板图不会改这里（要收窄范围点右侧 ▾ →「屏幕区域…」）",
                OptionParams: OptionMap.Of(
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
            new InputSpec("all", "找全部", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "勾上就把所有命中都列进 list（默认只返回最上面那个）"),
            new InputSpec("limit", "最多几个", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50), Advanced: true, VisibleWhen: "all == true",
                Description: "找全部时最多收集几个（1–200）"),
            new InputSpec("match", "匹配方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("contains"), Options: new[] { "contains", "exact" },
                Description: "包含 = 「保存」也命中「保存并关闭」；完全一致 = 只认完整的一个词（「保存 (Ctrl+S)」仍算）"),
            new InputSpec("engine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6tiny"), Options: Engines),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0),
                Description: "最多等多久（毫秒）：0 = 只查一次；-1 = 一直等到找到（按停止热键结束）；最多 10 分钟"),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(300), Advanced: true,
                Description: "等待期间每隔多久再识别一次（50–5000）"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "命中文字", ValueKind.text, "foundText", Description: "实际命中的那段文字"),
            new OutputSpec("x", "X", ValueKind.number, "textX", Description: "命中文字中心 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "textY", Description: "命中文字中心 Y（屏幕坐标）"),
            new OutputSpec("found", "找到", ValueKind.@bool, "textFound", Description: "是否找到；没找到不是错误"),
            new OutputSpec("count", "命中个数", ValueKind.number, "textCount",
                Description: "命中几处；没勾「找全部」时只会是 0 或 1"),
            new OutputSpec("list", "命中列表", ValueKind.list, "textList",
                Description: "每处命中一项：text / x / y / w / h / rect（勾「找全部」才有多个）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string needle = args.Text("text").Trim();
        if (needle.Length == 0) throw new ActionException("截图找文字没有指定要找的文字");

        bool all = args.Has("all") && args.Bool("all");
        int limit = all ? (int)Math.Clamp(args.Number("limit"), 1, TextLocator.MaxHits) : 1;
        bool exact = string.Equals(args.Text("match").Trim(), "exact", StringComparison.OrdinalIgnoreCase);

        ImageRegion region = ScreenArgs.ParseRegion(args.Text("region"));
        int[]? rect = ScreenArgs.ParseRect(args);
        string engine = args.Text("engine").Trim();
        int timeoutMs = Timeouts.Normalize(args.Number("timeoutMs"));
        int intervalMs = (int)Math.Clamp(args.Number("intervalMs"), 50, 5_000);

        TextLookup lookup = ProbeContext.Run(ctx, run => TextLocator.LocateAll(
            needle, region, rect, engine, timeoutMs, intervalMs, exact, limit, run));

        TextHit? first = lookup.Hits.Count > 0 ? lookup.Hits[0] : null;
        string text = first?.Text ?? "";
        int x = first?.X ?? 0;
        int y = first?.Y ?? 0;

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("found", Value.FromBool(lookup.Hits.Count > 0));
        ctx.SetOutput("count", Value.FromNumber(lookup.Hits.Count));
        ctx.SetOutput("list", Value.FromList(
            lookup.Hits.Select(h => UnitValues.TextHit(h.X, h.Y, h.W, h.H, h.Text))));
        ctx.SetOutput("value", lookup.Hits.Count > 0
            ? Value.Object(
                ("text", Value.FromText(text)),
                ("number", Value.FromNumber(0)),
                ("x", Value.FromNumber(x)),
                ("y", Value.FromNumber(y)),
                ("found", Value.FromBool(true)),
                ("count", Value.FromNumber(lookup.Hits.Count)),
                ("list", Value.FromList(lookup.Hits.Select(h =>
                    UnitValues.TextHit(h.X, h.Y, h.W, h.H, h.Text)))))
            : Value.Object(
                ("text", Value.FromText("")),
                ("found", Value.FromBool(false)),
                ("count", Value.FromNumber(0)),
                ("list", Value.FromList())));
    }
}
