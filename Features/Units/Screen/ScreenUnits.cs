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
                Required: true, Default: Value.FromText("interactive"), Options: Modes),
            new InputSpec("rect", "固定区域", ValueKind.text, VisibleWhen: "mode == 'rect'"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "shotX"),
            new OutputSpec("y", "Y", ValueKind.number, "shotY"),
            new OutputSpec("w", "宽", ValueKind.number, "shotW"),
            new OutputSpec("h", "高", ValueKind.number, "shotH"),
            new OutputSpec("file", "图片文件", ValueKind.text, "shot"),
            new OutputSpec("found", "取到图", ValueKind.@bool),
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

        var value = LegacyFlow.Evaluate(ctx, legacy => ScreenshotProbe.Run(probe, legacy));

        ctx.SetOutput("x", Value.FromNumber(MemberNumber(value, "x")));
        ctx.SetOutput("y", Value.FromNumber(MemberNumber(value, "y")));
        ctx.SetOutput("w", Value.FromNumber(MemberNumber(value, "w")));
        ctx.SetOutput("h", Value.FromNumber(MemberNumber(value, "h")));
        ctx.SetOutput("file", Value.FromText(UnitValues.Member(value, "file")?.AsText() ?? ""));
        ctx.SetOutput("found", Value.FromBool(UnitValues.Member(value, "found")?.AsBool() ?? false));
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
            new InputSpec("rect", "固定区域", ValueKind.text, VisibleWhen: "region == 'rect'"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85)),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(200)),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "matchX"),
            new OutputSpec("y", "Y", ValueKind.number, "matchY"),
            new OutputSpec("score", "相似度", ValueKind.number),
            new OutputSpec("found", "找到", ValueKind.@bool, "matchFound"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string image = ScreenArgs.ImagePath(args);
        if (image.Length == 0) throw new ActionException("截图找图没有指定模板图");

        var probe = ScreenArgs.BuildProbe(
            args, ScreenArgs.ParseRegion(args.Text("region")), ScreenArgs.ParseRect(args));
        probe.Image = image;

        var value = LegacyFlow.Evaluate(ctx, legacy => ImageProbe.Run(probe, legacy));
        ctx.SetOutput("x", Value.FromNumber(MemberNumber(value, "x")));
        ctx.SetOutput("y", Value.FromNumber(MemberNumber(value, "y")));
        ctx.SetOutput("score", Value.FromNumber(MemberNumber(value, "score")));
        ctx.SetOutput("found", Value.FromBool(UnitValues.Member(value, "found")?.AsBool() ?? false));
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
            new OutputSpec("text", "文字", ValueKind.text, "text"),
            new OutputSpec("found", "有文字", ValueKind.@bool),
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

        var value = LegacyFlow.Evaluate(ctx, legacy => OcrProbe.Run(probe, legacy));
        ctx.SetOutput("text", Value.FromText(UnitValues.Member(value, "text")?.AsText() ?? ""));
        ctx.SetOutput("found", Value.FromBool(UnitValues.Member(value, "found")?.AsBool() ?? false));
    }
}

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
            new InputSpec("rect", "固定区域", ValueKind.text, VisibleWhen: "region == 'rect'"),
            new InputSpec("engine", "识别引擎", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("v6tiny"), Options: Engines),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(300)),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "命中文字", ValueKind.text, "foundText"),
            new OutputSpec("x", "X", ValueKind.number, "textX"),
            new OutputSpec("y", "Y", ValueKind.number, "textY"),
            new OutputSpec("found", "找到", ValueKind.@bool, "textFound"),
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
        ctx.SetOutput("text", Value.FromText(UnitValues.Member(value, "text")?.AsText() ?? ""));
        ctx.SetOutput("x", Value.FromNumber(MemberNumber(value, "x")));
        ctx.SetOutput("y", Value.FromNumber(MemberNumber(value, "y")));
        ctx.SetOutput("found", Value.FromBool(UnitValues.Member(value, "found")?.AsBool() ?? false));
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
public sealed class PixelUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "screen.pixel",
        Name = "取像素颜色",
        Category = "屏幕",
        Icon = "color",
        Description = "读取虚拟桌面物理坐标处像素的颜色（#RRGGBB）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("x", "X", ValueKind.number, InputEditor.number, Required: true),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number, Required: true),
        },
        Outputs = new[]
        {
            new OutputSpec("color", "颜色", ValueKind.text, "color"),
            new OutputSpec("r", "红", ValueKind.number),
            new OutputSpec("g", "绿", ValueKind.number),
            new OutputSpec("b", "蓝", ValueKind.number),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        int x = (int)Math.Round(args.Number("x"));
        int y = (int)Math.Round(args.Number("y"));
        string color = ScreenCapture.GetPixel(x, y);

        ctx.SetOutput("color", Value.FromText(color));

        string hex = color.TrimStart('#');
        if (hex.Length == 6 &&
            int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            ctx.SetOutput("r", Value.FromNumber((rgb >> 16) & 0xFF));
            ctx.SetOutput("g", Value.FromNumber((rgb >> 8) & 0xFF));
            ctx.SetOutput("b", Value.FromNumber(rgb & 0xFF));
        }
    }
}
