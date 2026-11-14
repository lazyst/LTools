using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>
/// 静态校验器：断言「级别 + 位置」，不锁整句文案（文案会随手改，规则不该跟着红）。
/// </summary>
internal static class ValidatorSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("validator", "干净动作零问题", Clean));
        cases.Add(new("validator", "未知动作单元", UnknownUnit));
        cases.Add(new("validator", "输出键拼错", OutKeyTypo));
        cases.Add(new("validator", "当前选项不支持的参数", InvisibleParam));
        cases.Add(new("validator", "枚举参数用变量", EnumFromVariable));
        cases.Add(new("validator", "列表输出写进数字变量", ListIntoNumber));
        cases.Add(new("validator", "模板里的未知变量", UnknownTemplateVariable));
        cases.Add(new("validator", "必填参数缺失", RequiredMissing));
        cases.Add(new("validator", "草稿缺图片是错误", MissingImageDraft));
        cases.Add(new("validator", "编辑器缺图片只是提醒", MissingImageEditor));
        cases.Add(new("validator", "返回只能用在子动作里", ReturnInAction));
        cases.Add(new("validator", "子动作自调用成环", SubActionCycle));
        cases.Add(new("validator", "重试次数超范围", RetryOutOfRange));
        cases.Add(new("validator", "块步骤上重试给提醒", RetryOnBlock));
        cases.Add(new("validator", "超大动作给提醒", LargeAction));
    }

    private static void Clean()
    {
        ActionValidationResult result = Validate(Program(
            Var("v", ValueKind.text),
            Step.Of("flow.set").WithText("name", "v").WithText("value", "hi")));

        Check.True(result.Ok, "干净动作应通过：" + Describe(result));
        Check.Equal(0, result.Issues.Count, "不该有任何问题：" + Describe(result));
    }

    private static void UnknownUnit()
    {
        ActionValidationResult result = Validate(Program(Step.Of("nope.nothing")));

        Check.False(result.Ok, "未知单元应是错误");
        Check.True(Has(result, IssueSeverity.error, "/steps/0"), "位置应指向那一步：" + Describe(result));
    }

    private static void OutKeyTypo()
    {
        ActionValidationResult result = Validate(Program(
            Var("v", ValueKind.text),
            Step.Of("flow.set").WithText("name", "v").WithText("value", "hi").Output("nope", "v")));

        Check.False(result.Ok, "out 键拼错应是错误");
        Check.True(Has(result, IssueSeverity.error, "/out/nope"), "位置应指向 out 键：" + Describe(result));
    }

    private static void InvisibleParam()
    {
        // 默认操作 = 单击，dx 只在相对移动 / 拖动时生效。
        ActionValidationResult result = Validate(Program(
            Step.Of("input.mouse").With("dx", StepArg.FromNumber(5))));

        Check.True(result.Ok, "不可见参数只是提醒，不该拦运行：" + Describe(result));
        Check.True(Has(result, IssueSeverity.warning, "/in/dx"), "位置应指向 dx：" + Describe(result));
    }

    private static void EnumFromVariable()
    {
        ActionValidationResult result = Validate(Program(
            Var("op", ValueKind.text),
            Step.Of("input.mouse").WithVar("operation", "op")));

        Check.False(result.Ok, "枚举用变量应是错误");
        Check.True(Has(result, IssueSeverity.error, "/in/operation"), "位置应指向 operation：" + Describe(result));
    }

    private static void ListIntoNumber()
    {
        ActionValidationResult result = Validate(Program(
            new[] { Var("items", ValueKind.list), Var("n", ValueKind.number) },
            Step.Of("data.list").WithVar("list", "items").Output("list", "n")));

        Check.True(result.Ok, "列表写进数字变量只是提醒：" + Describe(result));
        Check.True(Has(result, IssueSeverity.warning, "/out/list"), "位置应指向输出映射：" + Describe(result));
    }

    private static void UnknownTemplateVariable()
    {
        ActionValidationResult result = Validate(Program(
            Var("v", ValueKind.text),
            Step.Of("flow.set").WithText("name", "v").WithText("value", "${nope}")));

        Check.True(result.Ok, "模板里引用未知变量只是提醒（运行时会报错，但静态校验不拦）：" + Describe(result));
        Check.True(Has(result, IssueSeverity.warning, "/in/value"), "位置应指向 value：" + Describe(result));
    }

    private static void RequiredMissing()
    {
        ActionValidationResult result = Validate(Program(Step.Of("flow.set")));

        Check.False(result.Ok, "缺必填参数应是错误");
        Check.True(Has(result, IssueSeverity.error, "/in/name"), "位置应指向 name：" + Describe(result));
    }

    private static void MissingImageDraft()
    {
        ActionValidationResult result = Validate(
            Program(Step.Of("input.clickImage").WithText("image", "files/none.png")),
            resourceRoot: TempResourceRoot(),
            draftCheck: true);

        Check.False(result.Ok, "草稿里的模板图必须存在");
        Check.True(Has(result, IssueSeverity.error, "/in/image"), "位置应指向 image：" + Describe(result));
    }

    private static void MissingImageEditor()
    {
        ActionValidationResult result = Validate(
            Program(Step.Of("input.clickImage").WithText("image", "files/none.png")),
            resourceRoot: TempResourceRoot(),
            draftCheck: false);

        Check.True(result.Ok, "编辑器校验缺图只是提醒：" + Describe(result));
        Check.True(Has(result, IssueSeverity.warning, "/in/image"), "位置应指向 image：" + Describe(result));
    }

    private static void ReturnInAction()
    {
        ActionValidationResult result = Validate(Program(Step.Of("flow.return")));

        Check.False(result.Ok, "动作里用返回应是错误：" + Describe(result));
    }

    private static void SubActionCycle()
    {
        var def = new SubActionDef { Id = "self1", Name = "自调用" };
        def.Steps.Add(Step.Of("sub.run").WithText("subAction", "self1"));

        ActionValidationResult result = ActionValidator.ValidateSubAction(def);

        Check.False(result.Ok, "子动作调用自己应是错误：" + Describe(result));
    }

    private static void RetryOutOfRange()
    {
        Step tooMany = Step.Of("flow.set").WithText("name", "v").WithText("value", "1");
        tooMany.Retry = Step.MaxRetry + 1;

        ActionValidationResult result = Validate(Program(Var("v", ValueKind.text), tooMany));

        Check.False(result.Ok, "重试次数超上限应是错误：" + Describe(result));
        Check.True(Has(result, IssueSeverity.error, "/steps/0"), "位置应指向那一步：" + Describe(result));

        Step negative = Step.Of("flow.set").WithText("name", "v").WithText("value", "1");
        negative.RetryDelayMs = -1;

        ActionValidationResult badDelay = Validate(Program(Var("v", ValueKind.text), negative));

        Check.False(badDelay.Ok, "负的重试间隔应是错误：" + Describe(badDelay));
    }

    private static void RetryOnBlock()
    {
        Step loop = Step.Of("flow.repeat").With("count", StepArg.FromNumber(2));
        loop.Retry = 1;
        loop.Body.Add(Step.Of("flow.set").WithText("name", "v").WithText("value", "1"));

        ActionValidationResult result = Validate(Program(Var("v", ValueKind.text), loop));

        Check.True(result.Ok, "块步骤上重试只是提醒，不该拦：" + Describe(result));
        Check.True(Has(result, IssueSeverity.warning, "/steps/0"), "位置应指向那一步：" + Describe(result));
    }

    private static void LargeAction()
    {
        var program = new ActionProgram { Name = "大动作" };
        program.Variables.Add(Var("v", ValueKind.text));
        for (int i = 0; i < 501; i++)
            program.Steps.Add(Step.Of("flow.set").WithText("name", "v").WithText("value", "x"));

        ActionValidationResult result = ActionValidator.Validate(program);

        Check.Equal(0, result.Errors, "501 步本身不该是错误：" + Describe(result));
        Check.True(result.Warnings >= 1, "501 步应给一条提醒：" + Describe(result));
    }

    // ============ 工具 ============

    private static ActionValidationResult Validate(ActionProgram program, string? resourceRoot = null,
        bool draftCheck = false) =>
        ActionValidator.Validate(program, revision: null, resourceRoot: resourceRoot, draftCheck: draftCheck);

    private static bool Has(ActionValidationResult result, IssueSeverity severity, string pathPart) =>
        result.Issues.Any(i => i.Severity == severity && i.Path.Contains(pathPart, StringComparison.Ordinal));

    private static string Describe(ActionValidationResult result) =>
        string.Join(" | ", result.Issues.Select(i => $"{i.Severity} {i.Path} {i.Message}"));

    private static ActionProgram Program(params Step[] steps) => Program((VariableDef[]?)null, steps);

    private static ActionProgram Program(VariableDef variable, params Step[] steps) =>
        Program(new[] { variable }, steps);

    private static ActionProgram Program(VariableDef[]? variables, params Step[] steps)
    {
        var program = new ActionProgram { Name = "校验测试" };
        if (variables != null) program.Variables.AddRange(variables);
        program.Steps.AddRange(steps);
        return program;
    }

    private static VariableDef Var(string key, ValueKind kind) => new() { Key = key, Type = kind };

    private static string TempResourceRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ltools-selftest", "resources");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
