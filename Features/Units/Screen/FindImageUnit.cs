using System;
using System.Collections.Generic;
using System.Linq;
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
                Default: Value.FromText("foreground"), Options: ScreenArgs.Regions,
                Description: "在哪儿找；框选模板图只取图、不改这里（要收窄范围点右侧 ▾ →「屏幕区域…」）",
                OptionParams: OptionMap.Of(
                    ("foreground", []),
                    ("screen", []),
                    ("rect", ["rect"]))),
            new InputSpec("rect", "固定区域", ValueKind.text, InputEditor.region),
            new InputSpec("all", "找全部", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "勾上就把所有命中都列进 list（默认只返回相似度最高的那个）"),
            new InputSpec("limit", "最多几个", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50), Advanced: true, VisibleWhen: "all == true",
                Description: "找全部时最多收集几个（1–200）；纯色区域也不会刷出成百上千个"),
            new InputSpec("order", "命中顺序", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("natural"), VisibleWhen: "all == true",
                Options: new[] { "natural", "score" },
                Description: "natural = 从上到下、从左到右；score = 相似度从高到低"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85), Advanced: true,
                Description: "0–1，越高越严格；找不到时可降到 0.7 左右再试"),
            new InputSpec("untilFound", "直到找到", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "勾上就一直找，直到找到才继续（按停止热键结束）；不勾按下面的等待时间"),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), VisibleWhen: "untilFound == false",
                Description: "最多等多久（毫秒）：0 = 只查一次；-1 = 一直等到找到（按停止热键结束）；最多 10 分钟"),
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
            new OutputSpec("count", "命中个数", ValueKind.number, "matchCount",
                Description: "命中几个；没勾「找全部」时只会是 0 或 1"),
            new OutputSpec("list", "命中列表", ValueKind.list, "matchList",
                Description: "每个命中一项：x / y / w / h / rect / score（勾「找全部」才有多个）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string image = ScreenArgs.ImagePath(args);
        if (image.Length == 0) throw new ActionException("截图找图没有指定模板图");

        bool all = args.Has("all") && args.Bool("all");
        int limit = all ? (int)Math.Clamp(args.Number("limit"), 1, ImageProbe.MaxHits) : 1;
        bool byScore = string.Equals(args.Text("order").Trim(), "score", StringComparison.OrdinalIgnoreCase);

        var probe = ScreenArgs.BuildProbe(
            args, ScreenArgs.ParseRegion(args.Text("region")), ScreenArgs.ParseRect(args));
        probe.Image = image;

        ImageProbeResult result = ProbeContext.Run(ctx, run => all
            ? ImageProbe.RunAll(probe, run, limit)
            : ImageProbe.RunSingle(probe, run));

        IReadOnlyList<ImageHit> hits = result.Hits;
        if (all)
        {
            hits = byScore
                ? hits.OrderByDescending(h => h.Score).ToList()
                : hits.OrderBy(h => h.Y).ThenBy(h => h.X).ToList();
        }

        ImageHit? first = hits.Count > 0 ? hits[0] : null;
        int x = first?.X ?? 0;
        int y = first?.Y ?? 0;
        double score = first?.Score ?? result.BestScore;

        ctx.SetOutput("x", Value.FromNumber(x));
        ctx.SetOutput("y", Value.FromNumber(y));
        ctx.SetOutput("score", Value.FromNumber(score));
        ctx.SetOutput("found", Value.FromBool(hits.Count > 0));
        ctx.SetOutput("count", Value.FromNumber(hits.Count));
        ctx.SetOutput("list", Value.FromList(
            hits.Select(h => UnitValues.Match(h.X, h.Y, h.Score, h.W, h.H))));
        ctx.SetOutput("value", hits.Count > 0
            ? Value.Object(
                ("text", Value.FromText($"{x},{y}")),
                ("x", Value.FromNumber(x)),
                ("y", Value.FromNumber(y)),
                ("score", Value.FromNumber(score)),
                ("found", Value.FromBool(true)),
                ("count", Value.FromNumber(hits.Count)),
                ("list", Value.FromList(hits.Select(h =>
                    UnitValues.Match(h.X, h.Y, h.Score, h.W, h.H)))))
            : Value.Object(
                ("text", Value.FromText("")),
                ("found", Value.FromBool(false)),
                ("score", Value.FromNumber(result.BestScore)),
                ("count", Value.FromNumber(0)),
                ("list", Value.FromList())));
    }
}
