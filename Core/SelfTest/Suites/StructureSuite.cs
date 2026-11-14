using LTools.Core.Actions;
using LTools.Views;

namespace LTools.Core.SelfTest;

/// <summary>
/// P4「结构统一」的纯逻辑：可见性单一模型（选项门槛 + 附加条件 + 声明自检）、
/// 子步骤归属（<see cref="StepBody"/>）在引擎 / 校验器 / 步骤文本三处同一口径。
/// </summary>
internal static class StructureSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("structure", "可见性：选项门槛与附加条件", Visibility));
        cases.Add(new("structure", "可见性：声明自检拦坏声明", VisibilityLint));
        cases.Add(new("structure", "可见性：右值只给二元比较", ConditionRight));
        cases.Add(new("structure", "子步骤：声明之外的子列表", BodyRules));
        cases.Add(new("structure", "子步骤：位置与首条问题", BodyFirstProblem));
        cases.Add(new("structure", "子步骤：步骤文本解析拒绝", BodyStepText));
        cases.Add(new("structure", "子步骤：校验器报 error", BodyValidator));
        cases.Add(new("structure", "子步骤：引擎不静默忽略", BodyEngine));
        cases.Add(new("structure", "参数冻结：三态与改用固定值", ArgFreezeStates));
        cases.Add(new("structure", "参数冻结：来源文案", ArgFreezeSources));
    }

    // ============ 可见性 ============

    private static void Visibility()
    {
        UnitSpec spec = VisibilitySpec();

        var click = Args(("operation", "click"));
        Check.True(VisibleWhen.IsVisible(spec.Inputs[1], click, spec), "单击：x 可见");
        Check.False(VisibleWhen.IsVisible(spec.Inputs[2], click, spec), "单击：y 不可见");

        var drag = Args(("operation", "drag"));
        Check.True(VisibleWhen.IsVisible(spec.Inputs[2], drag, spec), "拖动：y 可见");

        var missing = Args();
        Check.False(VisibleWhen.IsVisible(spec.Inputs[2], missing, spec), "驱动键缺失时按默认值判定");

        var unknown = Args(("operation", "weird"));
        Check.True(VisibleWhen.IsVisible(spec.Inputs[2], unknown, spec), "认不出的旧值按可见处理");

        // 附加条件与选项门槛按 AND：布尔开关成立、但选项没启用该参数时仍然隐藏。
        var extraOn = Args(("operation", "click"));
        extraOn["flag"] = StepArg.FromBool(true);
        Check.True(VisibleWhen.IsVisible(spec.Inputs[3], extraOn, spec), "附加条件成立且没被选项管：显示");

        var gatedOff = Args(("operation", "click"));
        gatedOff["flag"] = StepArg.FromBool(true);
        Check.False(VisibleWhen.IsVisible(spec.Inputs[4], gatedOff, spec), "选项没启用时附加条件成立也不显示");

        var gatedOn = Args(("operation", "drag"));
        gatedOn["flag"] = StepArg.FromBool(true);
        Check.True(VisibleWhen.IsVisible(spec.Inputs[4], gatedOn, spec), "选项启用 + 附加条件成立：显示");
    }

    private static void VisibilityLint()
    {
        // 引用不存在的参数：注册时就该报，而不是运行期「某个参数永远不显示」。
        ActionException missingKey = Check.Throws<ActionException>(
            () => VisibilitySpec(extraWhen: "nope == true").Validate(), "引用不存在的参数应报错");
        Check.Contains(missingKey.Message, "不存在的参数");

        // 解析不了的写法。
        ActionException bad = Check.Throws<ActionException>(
            () => VisibilitySpec(extraWhen: "flag = true").Validate(), "解析不了的可见条件应报错");
        Check.Contains(bad.Message, "解析不了");

        // 带引号的枚举字面量必须在 Options 里。
        ActionException notOption = Check.Throws<ActionException>(
            () => VisibilitySpec(extraWhen: "operation == 'nope'").Validate(), "枚举字面量不在 Options 里应报错");
        Check.Contains(notOption.Message, "不是");

        // 合法声明不报错（含 || 与 in）。
        VisibilitySpec(extraWhen: "operation in ('click','drag') || flag == true").Validate();
        Check.True(true, "合法声明通过自检");

        // 真实动作单元：右值改走 OptionParams 之后声明仍然自检通过。
        StepRegistry.Default.TryGet("flow.if", out var flowIf);
        flowIf.Spec.Validate();
        Check.True(true, "flow.if 声明自检通过");
    }

    private static void ConditionRight()
    {
        StepRegistry.Default.TryGet("flow.if", out var unit);
        UnitSpec spec = unit.Spec;
        InputSpec right = spec.Inputs.First(i => i.Key == "right");

        Check.True(VisibleWhen.IsVisible(right, Args(("op", "equals")), spec), "等于：要右值");
        Check.True(VisibleWhen.IsVisible(right, Args(("op", "regex")), spec), "正则：要右值");
        Check.False(VisibleWhen.IsVisible(right, Args(("op", "isTrue")), spec), "为真：不要右值");
        Check.False(VisibleWhen.IsVisible(right, Args(("op", "isEmpty")), spec), "为空：不要右值");
        Check.True(VisibleWhen.IsVisible(right, Args(), spec), "没写比较方式时按默认（等于）要右值");
        Check.True(VisibleWhen.IsVisible(right, Args(("op", "weird")), spec), "认不出的旧比较方式不藏参数");

        // 框选 / 摘要靠它把配套枚举切到「会显示该参数」的选项。
        Check.True(VisibleWhen.TryGetOptionFor(right, spec, out string key, out string value),
            "右值能反查到驱动选项");
        Check.Equal("op", key, "驱动键是比较方式");
        Check.True(FlowUnits.Compare.BinaryOps.Contains(value), $"反查到的应是二元比较（实际「{value}」）");

        // 一元判断没有附加条件了（P4 起走 OptionParams），声明里不该再留长达十几项的字符串。
        Check.True(string.IsNullOrWhiteSpace(right.VisibleWhen), "右值的可见性改由 OptionParams 声明");
    }

    // ============ 子步骤 ============

    private static void BodyRules()
    {
        // 普通步骤：任何子列表都不行。
        var plain = Step.Of("flow.set");
        Check.True(StepBody.Problem(plain) == null, "普通步骤没有子列表时合规");

        var plainThen = Step.Of("flow.set");
        plainThen.Then.Add(Step.Of("flow.delay"));
        Check.Contains(StepBody.Problem(plainThen), "不带子步骤", "普通步骤带 then 应报错");

        var plainElse = Step.Of("flow.set");
        plainElse.Else.Add(Step.Of("flow.delay"));
        Check.Contains(StepBody.Problem(plainElse), "不带子步骤", "普通步骤带 else 应报错");

        // 循环：只认 body。
        var loop = Step.Of("flow.repeat");
        Check.True(StepBody.Problem(loop) == null, "循环体可空");
        loop.Body.Add(Step.Of("flow.delay"));
        Check.True(StepBody.Problem(loop) == null, "循环带 body 合规");

        var loopThen = Step.Of("flow.repeat");
        loopThen.Then.Add(Step.Of("flow.delay"));
        Check.Contains(StepBody.Problem(loopThen), "只有循环体", "循环带 then 应报错");

        // 如果：只认 then / else。
        var branch = Step.Of("flow.if");
        branch.Then.Add(Step.Of("flow.delay"));
        branch.Else.Add(Step.Of("flow.delay"));
        Check.True(StepBody.Problem(branch) == null, "如果带 then / else 合规");

        var branchBody = Step.Of("flow.if");
        branchBody.Body.Add(Step.Of("flow.delay"));
        Check.Contains(StepBody.Problem(branchBody), "不认循环体", "如果带 body 应报错");

        // 未知动作单元不判定（照读照写，交给「未知的动作单元」那条错误）。
        var unknown = Step.Of("nope.nothing");
        unknown.Body.Add(Step.Of("flow.delay"));
        Check.True(StepBody.Problem(unknown) == null, "未知单元不判子步骤");
    }

    private static void BodyFirstProblem()
    {
        var ok = Step.Of("flow.repeat");
        ok.Body.Add(Step.Of("flow.delay"));

        var bad = Step.Of("flow.set").WithText("name", "x");
        bad.Else.Add(Step.Of("flow.delay"));

        Check.True(StepBody.FirstProblem(new List<Step> { ok }) == null, "全是合规步骤时没有问题");

        string? problem = StepBody.FirstProblem(new List<Step> { ok, bad });
        Check.Contains(problem, "第 2 行", "位置指向那条步骤");
        Check.Contains(problem, "设置变量", "说明里带动作单元名");
        Check.Contains(problem, "1 步", "说明里带「有几步没地方执行」");

        // 嵌套：问题在分支里时位置要带分支名。
        var outer = Step.Of("flow.if");
        outer.Then.Add(bad);
        string? nested = StepBody.FirstProblem(new List<Step> { outer });
        Check.Contains(nested, "第 1 行 › 满足时 › 第 1 行", "嵌套位置带分支名");
    }

    private static void BodyStepText()
    {
        // 非循环 / 分支的步骤下面缩进：解析直接报错，而不是悄悄挪到别处。
        Check.False(StepText.TryParse("flow.set name=x\n  flow.delay ms=10", out _, out string? error),
            "给普通步骤缩进子步骤应失败");
        Check.Contains(error, "不能带子步骤", "错误说明该说清原因");

        // 循环体照常可解析。
        Check.True(StepText.TryParse("flow.repeat count=2\n  flow.delay ms=10", out List<Step> steps,
            out _), "循环体可以带子步骤");
        Check.Equal(1, steps.Count, "只有一条顶层步骤");
        Check.Equal(1, steps[0].Body.Count, "子步骤落在循环体里");
    }

    private static void BodyValidator()
    {
        var bad = Step.Of("flow.set").WithText("name", "x");
        bad.Body.Add(Step.Of("flow.delay"));

        var program = new ActionProgram { Name = "结构测试" };
        program.Steps.Add(bad);

        ActionValidationResult result = ActionValidator.Validate(program);
        Check.True(result.Issues.Any(i =>
                i.Severity == IssueSeverity.error && i.Message.Contains("不带子步骤", StringComparison.Ordinal)),
            "校验器把「声明之外的子步骤」判为 error：" + Describe(result));
        Check.True(result.Issues.Any(i => i.Path == "/steps/0"),
            "问题挂在出问题的那一步上：" + Describe(result));
    }

    private static void BodyEngine()
    {
        var bad = Step.Of("flow.set").WithText("name", "x");
        bad.Body.Add(Step.Of("flow.delay"));

        var program = new ActionProgram { Name = "结构测试" };
        program.Steps.Add(bad);

        ActionRunResult result = new ActionEngine(StepRegistry.Default).Run(program, new ActionRunOptions());
        Check.False(result.Ok, "引擎拒绝执行「子步骤没地方跑」的动作");
        Check.Contains(result.Error, "不带子步骤", "错误说明与校验器一致");

        // 合规的循环照常跑（回归：这条检查不能误伤真正的循环体）。
        var loop = Step.Of("flow.repeat").WithText("count", "2");
        loop.Body.Add(Step.Of("flow.delay").WithText("ms", "1"));
        var fine = new ActionProgram { Name = "结构测试" };
        fine.Steps.Add(loop);
        ActionRunResult ok = new ActionEngine(StepRegistry.Default).Run(fine, new ActionRunOptions());
        Check.True(ok.Ok, "循环体照常执行：" + ok.Error);
    }

    // ============ 参数冻结（布尔 / 选项 / 多选遇到变量或取值） ============

    private static void ArgFreezeStates()
    {
        // 字面量：可编辑，读回控件当前值。
        var literal = new ArgFreezeState(StepArg.FromText("abc"));
        Check.False(literal.Frozen, "字面量不冻结");
        Check.Equal("x", literal.Read(() => StepArg.FromText("x"))?.Literal.AsText(), "不冻结时读控件当前值");

        // 变量引用：冻结，读回原引用（控件里选什么都不算数）。
        var variable = new ArgFreezeState(StepArg.FromVar("n"));
        Check.True(variable.Frozen, "变量引用冻结");
        Check.Equal("n", variable.Read(() => StepArg.FromText("x"))?.VariablePath, "冻结时读回原引用");

        // 取值动作单元：冻结，且原样返回同一个对象。
        StepArg inline = StepArg.FromInline(Step.Of("data.time"));
        var inlineState = new ArgFreezeState(inline);
        Check.True(inlineState.Frozen, "取值冻结");
        Check.True(ReferenceEquals(inline, inlineState.Read(() => StepArg.FromText("x"))),
            "冻结时返回同一个引用对象");

        // 点「改用固定值」之后：读控件当前值，引用丢掉。
        variable.Thaw();
        Check.False(variable.Frozen, "解冻后不再冻结");
        Check.Equal("x", variable.Read(() => StepArg.FromText("x"))?.Literal.AsText(), "解冻后读控件当前值");

        // 空值与非文本字面量都不该冻结（数字 / 开关照常可编辑）。
        Check.False(new ArgFreezeState(null).Frozen, "空值不冻结");
        Check.False(ArgFreezeState.IsFrozen(StepArg.FromNumber(1)), "数字字面量不冻结");
        Check.False(ArgFreezeState.IsFrozen(StepArg.FromBool(false)), "开关字面量不冻结");
    }

    private static void ArgFreezeSources()
    {
        Check.Equal("变量 ${n}", ArgFreeze.Source(StepArg.FromVar("n")), "变量的来源文案");
        Check.Equal("固定值", ArgFreeze.Source(StepArg.FromText("x")), "字面量的来源文案");

        string inline = ArgFreeze.Source(StepArg.FromInline(Step.Of("data.time")));
        Check.Contains(inline, "取值「", "取值的来源文案");
        Check.Contains(inline, "」", "取值文案要收口，别把整句话拼坏");
        Check.False(inline.Contains("${", StringComparison.Ordinal), "取值不是变量，不该带 ${}");
    }

    // ============ 夹具 ============

    private static UnitSpec VisibilitySpec(string? extraWhen = null) => new()
    {
        Key = "test.visibility",
        Name = "可见性测试",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"),
                Options: new[] { "click", "drag" },
                OptionParams: OptionMap.Of(
                    ("click", new[] { "x" }),
                    ("drag", new[] { "x", "y", "gated" }))),
            new InputSpec("x", "X", ValueKind.number, InputEditor.number),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number),
            new InputSpec("extra", "附加", ValueKind.text, VisibleWhen: extraWhen),
            new InputSpec("gated", "受管 + 附加", ValueKind.text, VisibleWhen: "flag == true"),
            new InputSpec("flag", "开关", ValueKind.@bool, InputEditor.boolean, Default: Value.FromBool(false)),
        },
    };

    private static Dictionary<string, StepArg> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, StepArg>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs) args[key] = StepArg.FromText(value);
        return args;
    }

    private static string Describe(ActionValidationResult result) =>
        string.Join(" | ", result.Issues.Select(i => $"{i.Severity} {i.Path} {i.Message}"));
}
