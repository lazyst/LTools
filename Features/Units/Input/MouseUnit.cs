using System.Collections.Generic;
using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>
/// 鼠标操作：移动 / 相对移动 / 单击 / 双击 / 右键 / 中键 / 滚轮 / 按下 / 抬起 / 拖动。
/// </summary>
/// <remarks>
/// <para>
/// 「移动」与「相对移动」可开「逐渐移动到目标」：把一次瞬移拆成多步小移动，让资源管理器拖文件、
/// 滑块、画图这类程序收到中间的 WM_MOUSEMOVE。「拖动」是「按下 → 逐渐移动 → 抬起」的单步快捷，
/// try/finally 保证不残留按下状态；「按下 / 抬起」跨步骤保持状态，由运行结束的
/// <see cref="InputHoldState.ReleaseAll"/> 兜底释放。
/// </para>
/// <para>
/// x/y 是屏幕绝对坐标（与截图 / 找图同源），dx/dy 是相对位移；
/// relativeMove / drag 里 x/y 表示「先定位到这个起点」，down / up 都不读坐标（要定位先加一步「移动」）。
/// </para>
/// </remarks>
public sealed class MouseUnit : IStepUnit
{
    private static readonly string[] Operations =
    {
        "move", "relativeMove", "click", "doubleClick", "rightClick", "middleClick",
        "wheelUp", "wheelDown", "down", "up", "drag",
    };

    private static readonly string[] Buttons = { "left", "right", "middle", "x1", "x2" };

    private static readonly IReadOnlyDictionary<string, string> ButtonLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = "左键",
            ["right"] = "右键",
            ["middle"] = "中键",
            ["x1"] = "X1（后退）",
            ["x2"] = "X2（前进）",
        };

    public UnitSpec Spec { get; } = new()
    {
        Key = "input.mouse",
        Name = "鼠标操作",
        Category = "键鼠与输入",
        Icon = "mouse",
        Description = "移动 / 相对移动 / 点击 / 滚轮 / 按下 / 抬起 / 拖动；坐标留空表示当前位置",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"), Options: Operations,
                OptionParams: OptionMap.Of(
                    ("move", ["x", "y", "gradual"]),
                    ("relativeMove", ["x", "y", "dx", "dy", "gradual"]),
                    ("click", ["x", "y"]),
                    ("doubleClick", ["x", "y"]),
                    ("rightClick", ["x", "y"]),
                    ("middleClick", ["x", "y"]),
                    ("wheelUp", ["x", "y", "wheel"]),
                    ("wheelDown", ["x", "y", "wheel"]),
                    ("down", ["button"]),
                    ("up", ["button"]),
                    ("drag", ["x", "y", "dx", "dy", "button"]))),
            // x/y 刻意不给 Default：默认 0 会把「留空 = 当前位置」变成「移到 (0,0)」。
            // 读取有 hasX 保护 + Num 兜底，不会被空文本触发「「」不是数字」。
            new InputSpec("x", "X", ValueKind.number, InputEditor.number,
                Pickers: PickerKind.variable | PickerKind.point | PickerKind.inline | PickerKind.clear,
                Description: "屏幕绝对坐标 X；relativeMove / drag 时表示先定位到这个起点；按下 / 抬起不读坐标"),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number,
                Pickers: PickerKind.variable | PickerKind.point | PickerKind.inline | PickerKind.clear,
                Description: "屏幕绝对坐标 Y；与 X 要同时填或同时留空"),
            new InputSpec("dx", "X 偏移", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0),
                Pickers: PickerKind.variable | PickerKind.inline | PickerKind.clear,
                Description: "相对当前光标的水平位移，可为负；拖动时它是拖动的位移"),
            new InputSpec("dy", "Y 偏移", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0),
                Pickers: PickerKind.variable | PickerKind.inline | PickerKind.clear,
                Description: "相对当前光标的垂直位移，可为负；拖动时它是拖动的位移"),
            new InputSpec("button", "按键", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("left"), Options: Buttons, Labels: ButtonLabels),
            new InputSpec("gradual", "逐渐移动到目标", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "把一次瞬移拆成多步小移动，让目标程序收到中间的鼠标移动（拖动 / 悬停需要）"),
            // steps / stepMs 的「gradual == true」是布尔附加条件，不能进 OptionParams，
            // 否则「移动 + 逐渐」时会被 operation 门槛判成不可见。
            new InputSpec("steps", "插值步数", ValueKind.number, InputEditor.number,
                VisibleWhen: "gradual == true || operation == 'drag'",
                Default: Value.FromNumber(20), Advanced: true,
                Description: "拆成多少步；太少仍会被程序当成瞬移，太多会慢"),
            new InputSpec("stepMs", "每步间隔毫秒", ValueKind.number, InputEditor.number,
                VisibleWhen: "gradual == true || operation == 'drag'",
                Default: Value.FromNumber(15), Advanced: true,
                Description: "每步之间等多久，可被停止热键中断；0 = 尽快发完"),
            new InputSpec("wheel", "滚轮格数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(3),
                Description: "滚动多少格；正数向上 / 向前，负数向下 / 向后"),
        },
        Outputs = new[]
        {
            new OutputSpec("x", "X", ValueKind.number, "mouseX", Description: "操作后的光标 X（屏幕坐标）"),
            new OutputSpec("y", "Y", ValueKind.number, "mouseY", Description: "操作后的光标 Y（屏幕坐标）"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim();
        // 按下 / 抬起只改按键状态，完全不碰坐标（要定位先加一步「移动」）。
        bool readsCoords = op is not ("down" or "up");
        bool hasX = readsCoords && args.Has("x") && args.Text("x").Trim().Length > 0;
        bool hasY = readsCoords && args.Has("y") && args.Text("y").Trim().Length > 0;

        if (hasX != hasY)
            throw new ActionException("鼠标坐标 X / Y 要同时填写或同时留空");

        // 可选数字参数一律走 Num：没填 / 空文本在引擎里是空文本 Value，
        // 直接 args.Number(...) 会抛「「」不是数字」，哪怕这一步根本用不到这个参数。
        int x = hasX ? (int)Math.Round(Num(args, "x")) : 0;
        int y = hasY ? (int)Math.Round(Num(args, "y")) : 0;
        int dx = (int)Math.Round(Num(args, "dx"));
        int dy = (int)Math.Round(Num(args, "dy"));
        int button = ButtonNumber(args.Text("button"));
        int steps = (int)Math.Clamp(Num(args, "steps", 20), 1, 600);
        int stepMs = (int)Math.Clamp(Num(args, "stepMs", 15), 0, 1000);
        bool gradual = op is "drag"
            || (op is "move" or "relativeMove" && args.Bool("gradual"));

        switch (op)
        {
            case "move":
                if (hasX) MoveTo(ctx, x, y, gradual, steps, stepMs);
                break;

            case "relativeMove":
                // x/y 是起点（瞬移定位），dx/dy 才是这一步的主位移。
                if (hasX) { MouseOps.Move(x, y); Wait(ctx, 10); }
                MoveBy(ctx, dx, dy, gradual, steps, stepMs);
                break;

            case "click":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.click);
                break;

            case "doubleClick":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.doubleClick);
                break;

            case "rightClick":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.rightClick);
                break;

            case "middleClick":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.middleClick);
                break;

            case "wheelUp":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.wheelUp, WheelDelta(args));
                break;

            case "wheelDown":
                PreMove(ctx, hasX, x, y);
                MouseOps.Perform(MouseActionKind.wheelDown, WheelDelta(args));
                break;

            case "down":
                // 只改按键状态，不读坐标：要定位先加一步「移动」。
                MouseOps.Down(button);
                break;

            case "up":
                // 只改按键状态，不读坐标（抬起时移动光标等于又拖了一下）。
                MouseOps.Up(button);
                break;

            case "drag":
                PreMove(ctx, hasX, x, y);
                if (dx == 0 && dy == 0)
                    throw new ActionException("拖动位移 dx / dy 不能都为 0（只按住请用「按下 / 抬起」）");
                MouseOps.Down(button);
                try
                {
                    Wait(ctx, 10);
                    MoveBy(ctx, dx, dy, gradual: true, steps, stepMs);
                    Wait(ctx, 10);
                }
                finally
                {
                    MouseOps.Up(button);   // 失败 / 停止也必须抬起
                }
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

    /// <summary>点击 / 滚轮 / 按下 / 拖动前：先瞬移到 x/y 并让光标落定（原有语义）。</summary>
    private static void PreMove(StepContext ctx, bool hasX, int x, int y)
    {
        if (!hasX) return;
        MouseOps.Move(x, y);
        Wait(ctx, 10);
    }

    /// <summary>绝对移动：gradual 时从当前光标插值到 (x,y)，否则一次瞬移。</summary>
    private static void MoveTo(StepContext ctx, int x, int y, bool gradual, int steps, int stepMs)
    {
        if (!gradual)
        {
            MouseOps.Move(x, y);
            Wait(ctx, 10);
            return;
        }
        if (!Win32.GetCursorPos(out var from))
        {
            MouseOps.Move(x, y);
            return;
        }
        for (int i = 1; i <= steps; i++)
        {
            int px = from.X + (int)Math.Round((x - from.X) * (double)i / steps);
            int py = from.Y + (int)Math.Round((y - from.Y) * (double)i / steps);
            MouseOps.Move(px, py);
            Wait(ctx, stepMs);
        }
    }

    /// <summary>相对移动：gradual 时按 dx/N 增量逐步发，末步自动补齐取整误差。</summary>
    private static void MoveBy(StepContext ctx, int dx, int dy, bool gradual, int steps, int stepMs)
    {
        if (!gradual)
        {
            if (dx != 0 || dy != 0) MouseOps.MoveRelative(dx, dy);
            Wait(ctx, 10);
            return;
        }
        int sentX = 0, sentY = 0;
        for (int i = 1; i <= steps; i++)
        {
            int tx = (int)Math.Round(dx * (double)i / steps);
            int ty = (int)Math.Round(dy * (double)i / steps);
            int ix = tx - sentX, iy = ty - sentY;
            if (ix != 0 || iy != 0) MouseOps.MoveRelative(ix, iy);
            sentX = tx;
            sentY = ty;
            Wait(ctx, stepMs);
        }
    }

    private static void Wait(StepContext ctx, int ms)
    {
        if (!ctx.Wait(ms)) throw new ActionAbortedException("已停止", userCancelled: true);
    }

    /// <summary>
    /// 读可选数字：没填 / 空文本按 <paramref name="fallback"/> 处理。
    /// 引擎给「未提供且无默认值」的参数是 <c>Value.Empty</c>（空文本），
    /// 直接 <c>args.Number(...)</c> 会抛「「」不是数字」——别再直接调。
    /// </summary>
    private static double Num(StepArgs args, string key, double fallback = 0)
    {
        Value? value = args.TryGet(key);
        if (value == null || (value.Kind == ValueKind.text && value.AsText().Trim().Length == 0))
            return fallback;
        return value.AsNumber();
    }

    /// <summary>按键英文键 → 宏回放同款的 1..5 编号（1 左 / 2 右 / 3 中 / 4 X1 / 5 X2）。</summary>
    private static int ButtonNumber(string text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "right" => 2,
        "middle" => 3,
        "x1" => 4,
        "x2" => 5,
        _ => 1,
    };

    private static int WheelDelta(StepArgs args)
    {
        double notches = Math.Clamp(Num(args, "wheel", 3), 1, 100);
        return (int)Math.Round(notches * 120);
    }
}

/// <summary>文字点击：在区域里识别文字，找到就点它；找不到按失败处理。</summary>
