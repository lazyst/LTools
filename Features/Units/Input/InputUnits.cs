using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>模拟按键：条目为 "ctrl+s" / "text:你好" / "sleep:200"。</summary>
public sealed class SendKeysUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.sendKeys",
        Name = "模拟按键",
        Category = "输入",
        Icon = "keyboard",
        Description = "依次发送按键组合、短文本或等待；条目写法 ctrl+s / text:你好 / sleep:200",
        Inputs = new[]
        {
            new InputSpec("keys", "按键", Editor: InputEditor.multiline, Required: true),
            new InputSpec("focusDelayMs", "焦点延时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50)),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var items = KeyItems(args);
        if (items.Count == 0) throw new ActionException("模拟按键的条目为空");

        int delay = (int)Math.Clamp(args.Number("focusDelayMs"), 0, 10_000);
        if (delay > 0 && !ctx.Wait(delay))
            throw new ActionAbortedException("已停止", userCancelled: true);

        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i].AsText().Trim();
            if (item.Length == 0) continue;

            if (item.StartsWith("sleep:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(item[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int ms) && ms > 0 && !ctx.Wait(ms))
                    throw new ActionAbortedException("已停止", userCancelled: true);
                continue;
            }

            if (item.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                string text = item[5..];
                if (text.Length > 0) TextSender.Dispatch(text, SendTextMode.auto, "模拟按键");
                continue;
            }

            if (!KeyStroke.TryParse(item, out var vks, out string error))
                throw new ActionException($"第 {i + 1} 个按键无法解析「{item}」：{error}");

            InputHelper.Combo(vks.ToArray());
            if (i + 1 < items.Count) ctx.Wait(15);
        }
    }

    /// <summary>
    /// 按键条目：值本身是列表时直接取项；编辑器里填的多行文本按行拆分（去掉空行）。
    /// </summary>
    private static IReadOnlyList<Value> KeyItems(StepArgs args)
    {
        Value value = args.Get("keys");
        if (value.Kind == ValueKind.list) return value.Items;

        string text = value.AsText().Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(Value.FromText)
            .ToList();
    }
}

/// <summary>鼠标操作：移动 / 单击 / 双击 / 右键 / 中键 / 滚轮。</summary>
public sealed class MouseUnit : IStepUnit
{
    private static readonly string[] Operations =
        { "move", "click", "doubleClick", "rightClick", "middleClick", "wheelUp", "wheelDown" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "input.mouse",
        Name = "鼠标操作",
        Category = "输入",
        Icon = "mouse",
        Description = "移动光标到坐标并点击 / 滚轮；坐标留空表示当前位置",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"), Options: Operations),
            new InputSpec("x", "X", ValueKind.number, InputEditor.number),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number),
            new InputSpec("wheel", "滚轮格数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(3),
                VisibleWhen: "operation == 'wheelUp' || operation == 'wheelDown'"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "mouseX"),
            new OutputSpec("y", "Y", ValueKind.number, "mouseY"),
            new OutputSpec("value", "完整结果", ValueKind.@object),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim();
        bool hasX = args.Has("x") && args.Text("x").Trim().Length > 0;
        bool hasY = args.Has("y") && args.Text("y").Trim().Length > 0;

        if (hasX != hasY)
            throw new ActionException("鼠标坐标 X / Y 要同时填写或同时留空");

        if (hasX)
        {
            int x = (int)Math.Round(args.Number("x"));
            int y = (int)Math.Round(args.Number("y"));
            MouseOps.Move(x, y);
            ctx.Wait(10);   // 让光标位置先落定，再发按键
        }

        switch (op)
        {
            case "move":
                break;

            case "click":
                MouseOps.Perform(MouseActionKind.click);
                break;

            case "doubleClick":
                MouseOps.Perform(MouseActionKind.doubleClick);
                break;

            case "rightClick":
                MouseOps.Perform(MouseActionKind.rightClick);
                break;

            case "middleClick":
                MouseOps.Perform(MouseActionKind.middleClick);
                break;

            case "wheelUp":
                MouseOps.Perform(MouseActionKind.wheelUp, WheelDelta(args));
                break;

            case "wheelDown":
                MouseOps.Perform(MouseActionKind.wheelDown, WheelDelta(args));
                break;

            default:
                throw new ActionException($"未知鼠标操作：{op}");
        }

        if (Win32.GetCursorPos(out var point))
        {
            ctx.SetOutput("x", Value.FromNumber(point.X));
            ctx.SetOutput("y", Value.FromNumber(point.Y));
            ctx.SetOutput("value", UnitValues.Point(point.X, point.Y));
        }
    }

    private static int WheelDelta(StepArgs args)
    {
        double notches = Math.Clamp(args.Number("wheel"), 1, 100);
        return (int)Math.Round(notches * 120);
    }
}

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
            new InputSpec("rect", "固定区域", ValueKind.text, VisibleWhen: "region == 'rect'"),
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
            new OutputSpec("x", "X", ValueKind.number, "clickX"),
            new OutputSpec("y", "Y", ValueKind.number, "clickY"),
            new OutputSpec("value", "完整结果", ValueKind.@object),
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
public sealed class ClickImageUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.clickImage",
        Name = "图片点击",
        Category = "输入",
        Icon = "imageClick",
        Description = "在区域里找模板图并点击命中位置；找不到就是这一步失败",
        Inputs = new[]
        {
            new InputSpec("image", "模板图", ValueKind.text, InputEditor.image, Required: true),
            new InputSpec("region", "区域", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("foreground"), Options: LTools.Features.Units.Screen.ScreenArgs.Regions),
            new InputSpec("rect", "固定区域", ValueKind.text, VisibleWhen: "region == 'rect'"),
            new InputSpec("minScore", "最低相似度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0.85)),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0)),
            new InputSpec("mouseAction", "点击方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("click"),
                Options: new[] { "click", "doubleClick", "rightClick", "middleClick" }),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "clickX"),
            new OutputSpec("y", "Y", ValueKind.number, "clickY"),
            new OutputSpec("score", "相似度", ValueKind.number),
            new OutputSpec("value", "完整结果", ValueKind.@object),
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
        }, LegacyFlow.Create(ctx));

        ctx.SetOutput("x", Value.FromNumber(result.X ?? 0));
        ctx.SetOutput("y", Value.FromNumber(result.Y ?? 0));
        ctx.SetOutput("score", Value.FromNumber(result.Score ?? 0));
        ctx.SetOutput("value", UnitValues.Match(
            result.X ?? 0, result.Y ?? 0, result.Score ?? 0));
    }
}

/// <summary>读取当前光标位置（纯取值，不移动光标）。</summary>
public sealed class CursorUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.cursor",
        Name = "光标位置",
        Category = "输入",
        Icon = "cursor",
        Description = "读取当前光标在虚拟桌面上的坐标，不移动光标",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "cursorX"),
            new OutputSpec("y", "Y", ValueKind.number, "cursorY"),
            new OutputSpec("value", "完整结果", ValueKind.@object),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        if (!Win32.GetCursorPos(out var point))
            throw new ActionException("读取光标位置失败");

        ctx.SetOutput("x", Value.FromNumber(point.X));
        ctx.SetOutput("y", Value.FromNumber(point.Y));
        ctx.SetOutput("value", UnitValues.Point(point.X, point.Y));
    }
}

/// <summary>回放录制的键鼠宏。</summary>
public sealed class MacroUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.macro",
        Name = "回放宏",
        Category = "输入",
        Icon = "macro",
        Description = "同步回放一个已录制的键鼠宏",
        Inputs = new[]
        {
            new InputSpec("macroId", "宏", ValueKind.text, Required: true),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string id = args.Text("macroId").Trim();
        if (id.Length == 0) throw new ActionException("回放宏没有指定宏");

        var macro = MacroStore.Load(id)
            ?? throw new ActionException($"宏不存在：{id}");

        MacroPlayer.PlaySync(macro);
    }
}
