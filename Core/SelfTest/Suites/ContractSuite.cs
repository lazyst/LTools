using LTools.Core.Actions;
using LTools.Features.Units.Data;
using LTools.Features.Units.Text;

namespace LTools.Core.SelfTest;

/// <summary>
/// 动作单元契约（<c>docs/UNIT_CONTRACT_AUDIT.md</c> 建议 1-6）：说明写清「哪个 operation 写哪个输出」，
/// 声明了 <c>found</c> 的单元每步都写它，恒真的 <c>found</c> 说明失败语义。
/// </summary>
/// <remarks>
/// 引擎**不预清**输出变量：没有写过的输出会保留上一步的值，不报错。所以这里不光断言文案，
/// 还用真单元跑一遍「先命中、再跑不命中的操作」，确认 <c>found</c> 被写成 false 而不是读到旧值。
/// </remarks>
internal static class ContractSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("contract", "data.list 的输出说明写清 operation", ListOutputDocs));
        cases.Add(new("contract", "data.list 的 found 每步都写", ListFoundWritten));
        cases.Add(new("contract", "text.process 的 found 每步都写", TextFoundWritten));
        cases.Add(new("contract", "file.operation 的 move 显示「移动」", FileOperationLabels));
        cases.Add(new("contract", "恒真的 found 说明了失败语义", AlwaysTrueFoundDocs));
        cases.Add(new("contract", "图片路径参数声明成文本", ImagePathTypes));
        cases.Add(new("contract", "枚举选项值都能被派发", OptionDispatch));
    }

    private static void ListOutputDocs()
    {
        UnitSpec spec = StepRegistry.Default.Get("data.list").Spec;

        string list = Doc(spec, "list");
        Check.True(list.Contains("count") && list.Contains("removeAt"),
            "列表输出要列清哪些 operation 写它：" + list);

        string text = Doc(spec, "text");
        Check.True(text.Contains("join") && text.Contains("sum"),
            "文本输出要写清 join / sum / avg 这类来源：" + text);

        string number = Doc(spec, "number");
        Check.True(number.Contains("indexOf") && number.Contains("sum"),
            "数值输出要写清下标与求和两类来源：" + number);

        string found = Doc(spec, "found");
        Check.True(found.Contains("false"), "found 说明要写「其余操作写 false」：" + found);
    }

    private static void ListFoundWritten()
    {
        var registry = TestRegistry.Create(new ListUnit());
        var program = new ActionProgram { Name = "列表 found" };
        program.Variables.Add(Var("l", ValueKind.list));
        program.Variables.Add(Var("hit", ValueKind.@bool));
        program.Steps.Add(Step.Of("data.list")
            .WithText("operation", "split").WithText("value", "a,b").Output("list", "l"));
        program.Steps.Add(Step.Of("data.list")
            .WithText("operation", "contains").WithVar("list", "l").WithText("value", "b")
            .Output("found", "hit"));
        program.Steps.Add(Step.Of("data.list")
            .WithText("operation", "count").WithVar("list", "l").Output("found", "hit"));

        ActionRunResult result = new ActionEngine(registry).Run(program);

        Check.True(result.Ok, "三步都该成功：" + result.Error);
        Check.Equal(false, result.Variables["hit"].AsBool(),
            "count 不查找，found 必须被写成 false（读到上一步的 true 就是没写）");
    }

    private static void TextFoundWritten()
    {
        var registry = TestRegistry.Create(new ProcessTextUnit());
        var program = new ActionProgram { Name = "文本 found" };
        program.Variables.Add(Var("hit", ValueKind.@bool));
        program.Variables.Add(Var("pos", ValueKind.number));
        program.Steps.Add(Step.Of("text.process")
            .WithText("operation", "indexOf").WithText("text", "abc").WithText("find", "b")
            .Output("found", "hit").Output("number", "pos"));
        program.Steps.Add(Step.Of("text.process")
            .WithText("operation", "trim").WithText("text", " x ").Output("found", "hit"));

        ActionRunResult result = new ActionEngine(registry).Run(program);

        Check.True(result.Ok, "两步都该成功：" + result.Error);
        Check.Equal(1d, result.Variables["pos"].AsNumber(), "indexOf 的位置（从 0 数）");
        Check.Equal(false, result.Variables["hit"].AsBool(),
            "trim 不查找，found 必须被写成 false");
    }

    private static void FileOperationLabels()
    {
        UnitSpec spec = StepRegistry.Default.Get("file.operation").Spec;
        InputSpec op = spec.Inputs.Single(i => i.Key == "operation");

        Check.Equal("移动", OptionLabels.For(op, "move"),
            "file.operation 的 move 不该跟着鼠标的「移动到」");
        Check.Equal("复制", OptionLabels.For(op, "copy"), "其余选项仍走全局表");
    }

    private static void AlwaysTrueFoundDocs()
    {
        foreach (string key in new[] { "crypto.encrypt", "web.download", "ui.textWindow" })
        {
            string doc = Doc(StepRegistry.Default.Get(key).Spec, "found");
            Check.True(doc.Contains("报错") || doc.Contains("抛错"),
                $"单元「{key}」的 found 恒为真，说明要写清「失败整步报错」：" + doc);
        }
    }

    private static void ImagePathTypes()
    {
        foreach (string key in new[] { "clipboard.image", "image.info" })
        {
            InputSpec path = StepRegistry.Default.Get(key).Spec.Inputs.Single(i => i.Key == "path");
            Check.Equal(ValueKind.text, path.Type,
                $"单元「{key}」的 path 是文件路径，声明成 any 会让 consumes:\"text\" 反查漏掉它");
        }
    }

    private static void OptionDispatch()
    {
        UnitSpec listSpec = StepRegistry.Default.Get("data.list").Spec;
        foreach (string option in listSpec.Inputs.Single(i => i.Key == "operation").Options!)
        {
            var program = new ActionProgram { Name = "列表派发" };
            program.Variables.Add(Var("l", ValueKind.list));
            program.Steps.Add(Step.Of("data.list")
                .WithText("operation", "split").WithText("value", "1,2").Output("list", "l"));
            program.Steps.Add(Step.Of("data.list")
                .WithText("operation", option).WithVar("list", "l")
                .WithText("value", "1").With("index", StepArg.FromNumber(0)));

            ActionRunResult result = new ActionEngine(TestRegistry.Create(new ListUnit())).Run(program);
            Check.False((result.Error ?? "").Contains("未知"),
                $"data.list 的「{option}」是声明过的选项，必须能派发：" + result.Error);
        }

        UnitSpec textSpec = StepRegistry.Default.Get("text.process").Spec;
        foreach (string option in textSpec.Inputs.Single(i => i.Key == "operation").Options!)
        {
            var program = new ActionProgram { Name = "文本派发" };
            program.Steps.Add(Step.Of("text.process")
                .WithText("operation", option).WithText("text", "abc")
                .WithText("find", "b").WithText("replace", "x").WithText("separator", ",")
                .With("list", StepArg.FromValue(Value.FromList(
                    [Value.FromText("a"), Value.FromText("b")]))));

            ActionRunResult result = new ActionEngine(TestRegistry.Create(new ProcessTextUnit())).Run(program);
            Check.False((result.Error ?? "").Contains("未知"),
                $"text.process 的「{option}」是声明过的选项，必须能派发：" + result.Error);
        }
    }

    private static string Doc(UnitSpec spec, string key) =>
        spec.Outputs.Single(o => o.Key == key).Description ?? "";

    private static VariableDef Var(string key, ValueKind kind, Value? value = null) =>
        new() { Key = key, Type = kind, Default = value };
}
