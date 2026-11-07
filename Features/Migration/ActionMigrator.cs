using System.Globalization;
using System.Text.RegularExpressions;
using LTools.Core.Actions;

namespace LTools.Features.Migration;

/// <summary>迁移结果：新动作 + 告警 / 错误（不静默丢字段）。</summary>
public sealed class MigrationResult
{
    /// <summary>迁移后的动作（schema v2）。</summary>
    public ActionProgram Program { get; } = new();

    /// <summary>能迁但语义有出入的地方（容差丢失、结果类型不支持等）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>无法迁移、必须人工处理的地方。</summary>
    public List<string> Errors { get; } = new();

    /// <summary>没有错误。</summary>
    public bool Ok => Errors.Count == 0;

    /// <summary>错误 + 告警，按顺序。</summary>
    public IEnumerable<string> Problems => Errors.Concat(Warnings);
}

/// <summary>
/// 旧动作（<see cref="ActionDto"/> / <see cref="FlowNode"/> / <see cref="Probe"/>）→ 新动作
/// （<see cref="ActionProgram"/> + <see cref="Step"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 只做结构映射，不猜测用户意图：能对应上的字段一一搬过去，对不上的写进
/// <see cref="MigrationResult.Warnings"/>（例如颜色比较的容差、没有结果的「存入变量」）。
/// </para>
/// <para>
/// 三处语义差异要在迁移时显式补上，否则新引擎的默认值会改变旧行为：
/// <list type="bullet">
/// <item>旧流程默认失败策略是 <c>continue</c>，新引擎默认 <c>stop</c> —— 每一步都写 <c>onFail</c>；</item>
/// <item>旧 <c>DelayMs</c> 是节点执行**前**等待 —— 迁移成前置的 <c>flow.delay</c>；</item>
/// <item>旧 <c>runCommand</c> 只有填了输出变量才捕获输出 —— 否则 <c>capture = false</c>。</item>
/// </list>
/// </para>
/// </remarks>
public static class ActionMigrator
{
    private static readonly Regex VariablePattern =
        new(@"\$\{([^}]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>迁移一个动作。</summary>
    public static MigrationResult Migrate(ActionDto? action)
    {
        var result = new MigrationResult();
        var ctx = new Context(result);

        if (action == null)
        {
            result.Errors.Add("动作为空");
            return result;
        }

        result.Program.Id = action.Id ?? "";
        result.Program.Name = action.Name ?? "";
        result.Program.Icon = action.Icon ?? "";

        if (action.Type == ActionType.composite)
        {
            result.Program.Steps.AddRange(ConvertComposite(action, ctx));
        }
        else
        {
            if (action.Flow is { Count: > 0 } || action.Steps is { Count: > 0 })
                ctx.Warn($"「{action.Name}」不是组合动作，却带有流程 / 步骤，已忽略");

            result.Program.Steps.AddRange(ConvertAction(action, action.OutputVar, ctx));
        }

        DeclareVariables(ctx, result.Program.Steps);
        ctx.Declare(result.Program);
        return result;
    }

    /// <summary>批量迁移（每个动作一份结果，互不影响）。</summary>
    public static IReadOnlyList<MigrationResult> MigrateAll(IEnumerable<ActionDto?> actions) =>
        actions.Select(Migrate).ToList();

    // —— 组合动作 / 流程节点 ——

    private static List<Step> ConvertComposite(ActionDto action, Context ctx)
    {
        List<FlowNode> nodes = FlowMigration.Ensure(action);
        if (nodes.Count == 0)
            ctx.Warn($"组合动作「{action.Name}」没有任何步骤");

        return ConvertNodes(nodes, action.CompositeOnFail, ctx);
    }

    private static List<Step> ConvertNodes(
        IEnumerable<FlowNode> nodes, OnFailStrategy? defaultFail, Context ctx)
    {
        var result = new List<Step>();

        foreach (var node in nodes)
        {
            if (node == null) continue;

            if (node.DelayMs > 0)
            {
                // 旧语义：节点执行**前**等待。
                var delay = Step.Of("flow.delay").With("ms", StepArg.FromNumber(node.DelayMs));
                delay.OnFail = FailMode.@continue;
                result.Add(delay);
            }

            FailMode onFail = EffectiveFail(node.OnFail, defaultFail);

            switch (node.Kind)
            {
                case FlowNodeKind.action:
                {
                    if (node.Action == null)
                    {
                        ctx.Error("流程里的动作节点为空");
                        break;
                    }

                    var actionSteps = node.Action.Type == ActionType.composite
                        ? ConvertComposite(node.Action, ctx)
                        : ConvertAction(node.Action, node.Action.OutputVar, ctx);

                    foreach (var step in actionSteps) step.OnFail = onFail;
                    result.AddRange(actionSteps);
                    break;
                }

                case FlowNodeKind.set:
                {
                    var set = node.Set;
                    if (set == null)
                    {
                        ctx.Error("赋值节点缺少参数");
                        break;
                    }

                    string name = Text(set.Var).Trim();
                    if (name.Length == 0)
                    {
                        ctx.Error("赋值节点的变量名为空");
                        break;
                    }

                    var step = Step.Of("flow.set")
                        .With("name", StepArg.FromText(name))
                        .With("value", ProbeArg(set.Probe, ctx));
                    if (set.Append) step.With("append", StepArg.FromBool(true));
                    step.OnFail = onFail;

                    ctx.Var(name);
                    result.Add(step);
                    break;
                }

                case FlowNodeKind.@if:
                {
                    var ifNode = node.If;
                    if (ifNode == null)
                    {
                        ctx.Error("条件节点缺少参数");
                        break;
                    }

                    Condition condition = ifNode.Condition;
                    string bind = Text(condition.Bind).Trim();

                    StepArg left;
                    if (bind.Length > 0)
                    {
                        result.Add(WithFail(
                            Step.Of("flow.set")
                                .With("name", StepArg.FromText(bind))
                                .With("value", ProbeArg(condition.Probe, ctx)),
                            onFail));
                        ctx.Var(bind);
                        left = StepArg.FromVar(bind);
                    }
                    else
                    {
                        left = ProbeArg(condition.Probe, ctx);
                    }

                    var step = Step.Of("flow.if").With("left", left);
                    ApplyCompare(step, condition, ctx);
                    step.Then.AddRange(ConvertNodes(ifNode.Then, defaultFail, ctx));
                    if (ifNode.Else != null)
                        step.Else.AddRange(ConvertNodes(ifNode.Else, defaultFail, ctx));
                    step.OnFail = onFail;
                    result.Add(step);
                    break;
                }

                case FlowNodeKind.loop:
                {
                    var loop = node.Loop;
                    if (loop == null)
                    {
                        ctx.Error("循环节点缺少参数");
                        break;
                    }

                    string index = Text(loop.IndexVar).Trim();
                    if (loop.Mode == LoopMode.count)
                    {
                        var step = Step.Of("flow.repeat")
                            .With("count", StepArg.FromNumber(loop.Count));
                        if (index.Length > 0)
                        {
                            step.Output("index", index);
                            ctx.Var(index);
                        }

                        step.Body.AddRange(ConvertNodes(loop.Body, defaultFail, ctx));
                        step.OnFail = onFail;
                        result.Add(step);
                        break;
                    }

                    Condition? condition = loop.While;
                    if (condition == null)
                    {
                        ctx.Error("条件循环缺少条件");
                        break;
                    }

                    string bind = Text(condition.Bind).Trim();
                    StepArg left;
                    if (bind.Length > 0)
                    {
                        // 循环前先求一次值，循环体开头再求一次（旧实现每轮都重新取值）。
                        result.Add(WithFail(
                            Step.Of("flow.set")
                                .With("name", StepArg.FromText(bind))
                                .With("value", ProbeArg(condition.Probe, ctx)),
                            onFail));
                        ctx.Var(bind);
                        left = StepArg.FromVar(bind);
                    }
                    else
                    {
                        left = ProbeArg(condition.Probe, ctx);
                    }

                    var whileStep = Step.Of("flow.while").With("left", left);
                    ApplyCompare(whileStep, condition, ctx);

                    if (index.Length > 0)
                    {
                        whileStep.Output("index", index);
                        ctx.Var(index);
                    }

                    if (bind.Length > 0)
                    {
                        whileStep.Body.Add(WithFail(
                            Step.Of("flow.set")
                                .With("name", StepArg.FromText(bind))
                                .With("value", ProbeArg(condition.Probe, ctx)),
                            FailMode.@continue));
                    }

                    whileStep.Body.AddRange(ConvertNodes(loop.Body, defaultFail, ctx));
                    whileStep.OnFail = onFail;
                    result.Add(whileStep);
                    break;
                }

                case FlowNodeKind.@break:
                    result.Add(WithFail(Step.Of("flow.break"), onFail));
                    break;

                case FlowNodeKind.@continue:
                    result.Add(WithFail(Step.Of("flow.continue"), onFail));
                    break;

                case FlowNodeKind.stop:
                    result.Add(WithFail(Step.Of("flow.stop"), onFail));
                    break;

                default:
                    ctx.Error($"未知流程节点：{node.Kind}");
                    break;
            }
        }

        return result;
    }

    // —— 叶子动作 ——

    private static List<Step> ConvertAction(ActionDto action, string? outputVar, Context ctx)
    {
        var steps = new List<Step>();
        if (action == null)
        {
            ctx.Error("动作为空");
            return steps;
        }

        if (action.Type == ActionType.composite)
        {
            ctx.Warn($"「{action.Name}」是嵌套组合动作（旧版不支持），已就地展开");
            steps.AddRange(ConvertComposite(action, ctx));
            return steps;
        }

        Step? primary = action.Type switch
        {
            ActionType.launchApp => Step.Of("app.launch")
                .WithText("target", action.Target)
                .WithText("args", action.Args)
                .WithText("workdir", action.Workdir),

            ActionType.openFile => Step.Of("file.open")
                .WithText("operation", "file")
                .WithText("path", action.Path)
                .WithText("workdir", action.Workdir),

            ActionType.openFolder => Step.Of("file.open")
                .WithText("operation", "folder")
                .WithText("path", action.Path)
                .WithText("workdir", action.Workdir),

            ActionType.openUrl => Step.Of("file.open")
                .WithText("operation", "url")
                .WithText("path", action.Url),

            ActionType.runCommand => BuildRunCommand(action),

            ActionType.sendText => Step.Of("text.send")
                .WithText("text", action.Text)
                .WithText("mode", (action.Mode ?? SendTextMode.auto).ToString())
                .With("appendEnter", StepArg.FromBool(action.AppendEnter == true))
                .With("focusDelayMs", StepArg.FromNumber(50)),

            ActionType.setClipboard => Step.Of("text.clipboard")
                .WithText("operation", "write")
                .WithText("text", action.Text),

            ActionType.toast => BuildToast(action),

            ActionType.quickNote => Step.Of("data.noteWrite")
                .WithText("body", action.Text)
                .WithText("title", action.NoteTitle)
                .WithText("category", action.NoteCategory),

            ActionType.window => BuildWindow(action, ctx),

            ActionType.clickText => BuildClickText(action, ctx),

            ActionType.imageClick => BuildClickImage(action),

            ActionType.sendKeys => BuildSendKeys(action, ctx),

            ActionType.mouse => BuildMouse(action),

            ActionType.macro => Step.Of("input.macro")
                .WithText("macroId", action.MacroId),

            ActionType.@internal => Step.Of("internal.invoke")
                .WithText("command", action.Command),

            _ => null,
        };

        if (primary == null)
        {
            ctx.Error($"未知动作类型：{action.Type}");
            return steps;
        }

        if (!string.IsNullOrWhiteSpace(outputVar))
        {
            if (SupportsValue(primary.Unit))
            {
                primary.Output("value", outputVar!.Trim());
                ctx.Var(outputVar);
            }
            else
            {
                ctx.Warn($"「{action.Name}」（{primary.Unit}）没有结果，" +
                         $"「存入变量 {outputVar}」已忽略");
            }
        }

        steps.Add(primary);
        return steps;
    }

    private static Step BuildRunCommand(ActionDto action)
    {
        // 旧实现：填了输出变量才捕获输出（否则打开终端窗口），这里保持一致。
        bool capture = !string.IsNullOrWhiteSpace(action.OutputVar);

        var step = Step.Of("app.runCommand")
            .WithText("command", action.Cmd)
            .WithText("terminal", string.IsNullOrWhiteSpace(action.Terminal) ? "direct" : action.Terminal)
            .WithText("workdir", action.Workdir)
            .With("capture", StepArg.FromBool(capture));

        if (!capture)
            step.With("keepWindow", StepArg.FromBool(action.KeepWindow ?? false));

        return step;
    }

    private static Step BuildToast(ActionDto action)
    {
        var step = Step.Of("ui.toast").WithText("text", action.Text);
        if (!string.IsNullOrWhiteSpace(action.Name)) step.WithText("title", action.Name);
        if (action.DurationMs is int ms) step.With("durationMs", StepArg.FromNumber(ms));
        return step;
    }

    private static Step BuildWindow(ActionDto action, Context ctx)
    {
        WindowActionKind op = action.WindowOp ?? WindowActionKind.activate;

        var step = Step.Of("window.operate")
            .WithText("operation", op.ToString())
            .WithText("title", action.WindowTitle)
            .WithText("class", action.WindowClass);

        if (op == WindowActionKind.resize && action.WindowRect is { Length: >= 4 } rect)
        {
            step.With("x", StepArg.FromNumber(rect[0]))
                .With("y", StepArg.FromNumber(rect[1]))
                .With("w", StepArg.FromNumber(rect[2]))
                .With("h", StepArg.FromNumber(rect[3]));
        }
        else if (op != WindowActionKind.resize && action.WindowRect is { Length: > 0 })
        {
            ctx.Warn($"窗口操作「{action.Name}」不是调整大小，目标矩形已忽略");
        }

        return step;
    }

    private static Step BuildClickText(ActionDto action, Context ctx)
    {
        var step = Step.Of("input.clickText")
            .WithText("text", action.Text)
            .WithText("region", (action.Region ?? ImageRegion.foreground).ToString())
            .WithText("ocrEngine", EngineName(action.OcrEngine))
            .With("timeoutMs", StepArg.FromNumber(action.TimeoutMs ?? 0))
            .WithText("mouseAction", ClickKind(action.MouseAction, action.Name, ctx));

        if (action.Rect is { Length: >= 4 } rect)
            step.WithText("rect", RectText(rect));

        return step;
    }

    private static Step BuildClickImage(ActionDto action)
    {
        var step = Step.Of("input.clickImage")
            .WithText("image", action.Image)
            .WithText("region", (action.Region ?? ImageRegion.foreground).ToString())
            .With("minScore", StepArg.FromNumber(action.MinScore ?? 0.85))
            .With("timeoutMs", StepArg.FromNumber(action.TimeoutMs ?? 0))
            .WithText("mouseAction", ClickKind(action.MouseAction, action.Name, null));

        if (action.Rect is { Length: >= 4 } rect)
            step.WithText("rect", RectText(rect));

        return step;
    }

    private static Step BuildSendKeys(ActionDto action, Context ctx)
    {
        var keys = new List<Value>();

        foreach (var item in action.Items ?? new List<KeyItem>())
        {
            if (item == null) continue;

            switch (item.Kind)
            {
                case KeyItemKind.chord:
                    foreach (string stroke in item.Strokes ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(stroke)) keys.Add(Value.FromText(stroke.Trim()));
                    break;

                case KeyItemKind.text:
                    if (!string.IsNullOrEmpty(item.Text))
                        keys.Add(Value.FromText("text:" + item.Text));
                    break;

                case KeyItemKind.sleep:
                    if (item.Ms > 0)
                        keys.Add(Value.FromText(
                            "sleep:" + item.Ms.ToString(CultureInfo.InvariantCulture)));
                    break;
            }
        }

        if (keys.Count == 0)
            ctx.Warn($"模拟按键「{action.Name}」没有任何条目");

        return Step.Of("input.sendKeys")
            .With("keys", StepArg.FromValue(Value.FromList(keys)))
            .With("focusDelayMs", StepArg.FromNumber(50));
    }

    private static Step BuildMouse(ActionDto action)
    {
        MouseActionKind kind = action.MouseAction ?? MouseActionKind.click;

        var step = Step.Of("input.mouse").WithText("operation", kind.ToString());

        if (!string.IsNullOrWhiteSpace(action.X)) step.WithText("x", action.X);
        if (!string.IsNullOrWhiteSpace(action.Y)) step.WithText("y", action.Y);

        if (kind is MouseActionKind.wheelUp or MouseActionKind.wheelDown)
        {
            // 旧 WheelDelta 是 Win32 的 120 倍数；新参数是「格数」。
            int delta = action.WheelDelta ?? 120;
            double notches = Math.Max(1, Math.Round(delta / 120.0, MidpointRounding.AwayFromZero));
            step.With("wheel", StepArg.FromNumber(notches));
        }

        return step;
    }

    // —— 取值器（Probe）→ 参数 ——

    private static StepArg ProbeArg(Probe? probe, Context ctx)
    {
        if (probe == null) return StepArg.FromText("");

        switch (probe.Kind)
        {
            case ProbeKind.text:
                return StepArg.FromText(probe.Text ?? "");

            case ProbeKind.variable:
            {
                string name = Text(probe.Name).Trim();
                if (name.Length == 0)
                {
                    ctx.Warn("取值器「变量」没有填变量名");
                    return StepArg.FromText("");
                }

                return StepArg.FromVar(name);
            }

            case ProbeKind.selection:
                return Inline("text.selection");

            case ProbeKind.clipboard:
                return Inline("text.clipboard", ("operation", StepArg.FromText("read")));

            case ProbeKind.cursor:
                return Inline("input.cursor");

            case ProbeKind.datetime:
            {
                var step = Step.Of("data.time");
                if (!string.IsNullOrWhiteSpace(probe.Format))
                    step.With("format", StepArg.FromText(probe.Format));
                return StepArg.FromInline(step);
            }

            case ProbeKind.user:
            {
                var step = Step.Of("ui.input");
                if (!string.IsNullOrWhiteSpace(probe.Prompt))
                    step.With("prompt", StepArg.FromText(probe.Prompt));
                if (!string.IsNullOrWhiteSpace(probe.Default))
                    step.With("defaultValue", StepArg.FromText(probe.Default));
                if (probe.Multiline)
                    step.With("multiline", StepArg.FromBool(true));
                return StepArg.FromInline(step);
            }

            case ProbeKind.screenshot:
            {
                var step = Step.Of("screen.capture")
                    .With("mode", StepArg.FromText(probe.ScreenshotMode.ToString()));
                if (!string.IsNullOrWhiteSpace(probe.ScreenshotRect))
                    step.With("rect", StepArg.FromText(probe.ScreenshotRect));
                return StepArg.FromInline(step);
            }

            case ProbeKind.image:
            {
                var step = Step.Of("screen.findImage")
                    .With("image", StepArg.FromText(probe.Image ?? ""))
                    .With("region", StepArg.FromText(probe.Region.ToString()))
                    .With("minScore", StepArg.FromNumber(probe.MinScore))
                    .With("timeoutMs", StepArg.FromNumber(probe.TimeoutMs))
                    .With("intervalMs", StepArg.FromNumber(probe.IntervalMs));
                if (probe.Rect is { Length: >= 4 } rect)
                    step.With("rect", StepArg.FromText(RectText(rect)));
                return StepArg.FromInline(step);
            }

            case ProbeKind.ocr:
            {
                var step = Step.Of("screen.ocr")
                    .With("image", StepArg.FromText(probe.OcrImage ?? ""))
                    .With("engine", StepArg.FromText(EngineName(probe.OcrEngine)));
                return StepArg.FromInline(step);
            }

            case ProbeKind.textSearch:
            {
                var step = Step.Of("screen.findText")
                    .With("text", StepArg.FromText(probe.Text ?? ""))
                    .With("region", StepArg.FromText(probe.Region.ToString()))
                    .With("engine", StepArg.FromText(EngineName(probe.OcrEngine)))
                    .With("timeoutMs", StepArg.FromNumber(probe.TimeoutMs))
                    .With("intervalMs", StepArg.FromNumber(probe.IntervalMs));
                if (probe.Rect is { Length: >= 4 } rect)
                    step.With("rect", StepArg.FromText(RectText(rect)));
                return StepArg.FromInline(step);
            }

            case ProbeKind.color:
            {
                var step = Step.Of("screen.pixel")
                    .With("x", StepArg.FromText(probe.X ?? ""))
                    .With("y", StepArg.FromText(probe.Y ?? ""));
                return StepArg.FromInline(step);
            }

            case ProbeKind.note:
            {
                var step = Step.Of("data.noteRead");
                if (!string.IsNullOrWhiteSpace(probe.NoteCategory))
                    step.With("category", StepArg.FromText(probe.NoteCategory));
                if (!string.IsNullOrWhiteSpace(probe.Text))
                    step.With("filter", StepArg.FromText(probe.Text));
                return StepArg.FromInline(step);
            }

            case ProbeKind.window:
            {
                var step = Step.Of("window.find");
                if (!string.IsNullOrWhiteSpace(probe.WindowTitle))
                    step.With("title", StepArg.FromText(probe.WindowTitle));
                if (!string.IsNullOrWhiteSpace(probe.WindowClass))
                    step.With("class", StepArg.FromText(probe.WindowClass));
                return StepArg.FromInline(step);
            }

            case ProbeKind.process:
                return Inline("app.process",
                    ("name", StepArg.FromText(probe.ProcessName ?? "")),
                    ("operation", StepArg.FromText("exists")));

            case ProbeKind.lastError:
                return Inline("flow.lastError");

            default:
                ctx.Warn($"未知取值器：{probe.Kind}");
                return StepArg.FromText("");
        }
    }

    /// <summary>把条件写进 <c>flow.if</c> / <c>flow.while</c> 的公共参数。</summary>
    private static void ApplyCompare(Step step, Condition condition, Context ctx)
    {
        bool nearColor = condition.Op == CompareOp.nearColor;

        if (nearColor)
        {
            ctx.Warn($"颜色比较的容差 {condition.Probe.Tolerance} 无法迁移，已改为精确相等（equals）");
            step.With("op", StepArg.FromText("equals"));
            string target = condition.Probe.Color ?? condition.Operand ?? "";
            if (target.Length > 0) step.With("right", StepArg.FromText(target));
        }
        else
        {
            step.With("op", StepArg.FromText(condition.Op.ToString()));
            if (!string.IsNullOrEmpty(condition.Operand))
                step.With("right", StepArg.FromText(condition.Operand));
        }

        if (condition.Negate)
            step.With("negate", StepArg.FromBool(true));
    }

    // —— 变量声明 ——

    private static void DeclareVariables(Context ctx, IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            foreach (var arg in step.In.Values) CollectArg(arg, ctx);
            foreach (string name in step.Out.Values) ctx.Var(name);

            DeclareVariables(ctx, step.Body);
            DeclareVariables(ctx, step.Then);
            DeclareVariables(ctx, step.Else);
        }
    }

    private static void CollectArg(StepArg arg, Context ctx)
    {
        switch (arg.Kind)
        {
            case StepArgKind.variable:
                ctx.Var(arg.VariablePath);
                break;

            case StepArgKind.inline:
                if (arg.InlineStep != null)
                    foreach (var inner in arg.InlineStep.In.Values) CollectArg(inner, ctx);
                break;

            case StepArgKind.literal:
                CollectLiteral(arg.Literal, ctx);
                break;
        }
    }

    private static void CollectLiteral(Value value, Context ctx)
    {
        switch (value.Kind)
        {
            case ValueKind.text:
                foreach (Match match in VariablePattern.Matches(value.AsText()))
                    ctx.Var(BaseName(match.Groups[1].Value));
                break;

            case ValueKind.list:
                foreach (var item in value.Items) CollectLiteral(item, ctx);
                break;

            case ValueKind.@object:
                foreach (var member in value.Members.Values) CollectLiteral(member, ctx);
                break;
        }
    }

    private static string BaseName(string path)
    {
        string text = (path ?? "").Trim();
        int cut = text.IndexOfAny(new[] { '.', '[' });
        return cut < 0 ? text : text[..cut];
    }

    // —— 小工具 ——

    private static Step WithFail(Step step, FailMode mode)
    {
        step.OnFail = mode;
        return step;
    }

    private static FailMode EffectiveFail(OnFailStrategy? node, OnFailStrategy? fallback) =>
        (node ?? fallback ?? OnFailStrategy.@continue) == OnFailStrategy.abort
            ? FailMode.stop
            : FailMode.@continue;

    private static bool SupportsValue(string unit) =>
        StepRegistry.Default.TryGet(unit, out var impl) &&
        impl.Spec.Outputs.Any(output =>
            string.Equals(output.Key, "value", StringComparison.OrdinalIgnoreCase));

    private static StepArg Inline(string unit, params (string Key, StepArg Value)[] args)
    {
        var step = Step.Of(unit);
        foreach (var (key, value) in args) step.With(key, value);
        return StepArg.FromInline(step);
    }

    /// <summary>旧 <c>null</c> / 空引擎 = 系统 OCR；新单元默认是 PP-OCRv6，必须写死 <c>windows</c>。</summary>
    private static string EngineName(string? engine) =>
        string.IsNullOrWhiteSpace(engine) ? "windows" : engine!.Trim();

    private static string ClickKind(MouseActionKind? kind, string name, Context? ctx)
    {
        if (kind == null) return "click";

        if (kind is MouseActionKind.click or MouseActionKind.doubleClick
            or MouseActionKind.rightClick or MouseActionKind.middleClick)
            return kind.Value.ToString();

        ctx?.Warn($"点击动作「{name}」的点击方式 {kind} 不支持，已改为单击");
        return "click";
    }

    private static string RectText(int[] rect) =>
        string.Join(",", rect.Take(4).Select(
            value => value.ToString(CultureInfo.InvariantCulture)));

    private static string Text(string? value) => value ?? "";

    /// <summary>迁移过程中的变量收集（保序，重复忽略）。</summary>
    private sealed class Context
    {
        private readonly MigrationResult _result;
        private readonly List<string> _order = new();
        private readonly HashSet<string> _variables = new(StringComparer.OrdinalIgnoreCase);

        public Context(MigrationResult result) => _result = result;

        public void Warn(string message) => _result.Warnings.Add(message);

        public void Error(string message) => _result.Errors.Add(message);

        /// <summary>登记一个变量（按第一段名字，<c>a.b</c> 只登记 <c>a</c>）。</summary>
        public void Var(string? name)
        {
            string key = BaseName(name ?? "");
            if (key.Length == 0) return;
            if (_variables.Add(key)) _order.Add(key);
        }

        public void Declare(ActionProgram program)
        {
            foreach (string key in _order)
                program.Variables.Add(new VariableDef { Key = key, Type = ValueKind.text });
        }
    }
}
