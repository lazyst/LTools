using LTools.Core.Actions;
using LTools.Features.Units.Data;
using LTools.Features.Units.Text;

namespace LTools.Core.SelfTest;

/// <summary>
/// 动作单元契约（<c>docs/UNIT_CONTRACT_AUDIT.md</c> 建议 1-8）：说明写清「哪个 operation 写哪个输出」，
/// 声明了 <c>found</c> 的单元每步都写它，恒真的 <c>found</c> 说明失败语义，
/// 引擎在每一步执行前把 <c>out</c> 映射到的变量清成零值。
/// </summary>
/// <remarks>
/// 引擎从建议 8 起会预清输出变量，所以「读到上一步的旧值」已经不可能发生；
/// 这里改成从**轨迹**确认单元真的写了 <c>found</c>（没写就不会出现在 <c>Outputs</c> 里），
/// 两条一起才说明「值对」而不是「靠预清兜住的」。
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
        cases.Add(new("contract", "执行前把 out 变量清成零值", OutputsCleared));
        cases.Add(new("contract", "预清不影响自我赋值与追加", AppendStillWorks));
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

        var outputs = new Dictionary<string, IReadOnlyDictionary<string, Value>>();
        ActionRunResult result = new ActionEngine(registry).Run(program, new ActionRunOptions
        {
            StepTrace = t => { lock (outputs) outputs[t.Path] = t.Outputs; },
        });

        Check.True(result.Ok, "三步都该成功：" + result.Error);
        Check.Equal(false, result.Variables["hit"].AsBool(), "count 不查找，found 应是 false");
        Check.True(outputs.TryGetValue("/steps/2", out var written) && written.ContainsKey("found"),
            "count 也要**自己写** found（靠预清兜住的 false 不算数）");
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

        var outputs = new Dictionary<string, IReadOnlyDictionary<string, Value>>();
        ActionRunResult result = new ActionEngine(registry).Run(program, new ActionRunOptions
        {
            StepTrace = t => { lock (outputs) outputs[t.Path] = t.Outputs; },
        });

        Check.True(result.Ok, "两步都该成功：" + result.Error);
        Check.Equal(1d, result.Variables["pos"].AsNumber(), "indexOf 的位置（从 0 数）");
        Check.Equal(false, result.Variables["hit"].AsBool(), "trim 不查找，found 应是 false");
        Check.True(outputs.TryGetValue("/steps/1", out var written) && written.ContainsKey("found"),
            "trim 也要自己写 found");
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

    private static void OutputsCleared()
    {
        var partial = new FakeUnit("test.partial", "部分输出测试",
            inputs: new[] { new InputSpec("text", "文本") },
            outputs: new[]
            {
                new OutputSpec("text", "文本", ValueKind.text),
                new OutputSpec("found", "成立", ValueKind.@bool),
                new OutputSpec("number", "数值", ValueKind.number),
                new OutputSpec("list", "列表", ValueKind.list),
            },
            run: (ctx, args) => ctx.SetOutput("text", args.TryGet("text") ?? Value.Empty));

        var program = new ActionProgram { Name = "预清输出" };
        program.Variables.Add(Var("t", ValueKind.text));
        program.Variables.Add(Var("hit", ValueKind.@bool));
        program.Variables.Add(Var("n", ValueKind.number));
        program.Variables.Add(Var("items", ValueKind.list));

        // 先把四个变量都写成非零值，再跑一个只写 text 的步骤。
        program.Steps.Add(Step.Of("test.scope").WithText("name", "hit").With("value", StepArg.FromBool(true)));
        program.Steps.Add(Step.Of("test.scope").WithText("name", "n").With("value", StepArg.FromNumber(7)));
        program.Steps.Add(Step.Of("test.scope").WithText("name", "items")
            .With("value", StepArg.FromValue(Value.FromList([Value.FromText("a")]))));
        program.Steps.Add(Step.Of("test.partial").WithText("text", "x")
            .Output("text", "t").Output("found", "hit").Output("number", "n").Output("list", "items"));

        ActionRunResult result = new ActionEngine(TestRegistry.Create(partial)).Run(program);

        Check.True(result.Ok, "四步都该成功：" + result.Error);
        Check.Equal("x", result.Variables["t"].AsText(), "写了的输出照常");
        Check.Equal(false, result.Variables["hit"].AsBool(), "没写的布尔输出 = false");
        Check.Equal(0d, result.Variables["n"].AsNumber(), "没写的数字输出 = 0");
        Check.Equal(0, result.Variables["items"].AsList().Count, "没写的列表输出 = 空列表");
    }

    private static void AppendStillWorks()
    {
        var program = new ActionProgram { Name = "自我赋值" };
        program.Variables.Add(Var("a", ValueKind.text, Value.FromText("x")));
        program.Variables.Add(Var("l", ValueKind.list,
            Value.FromList([Value.FromText("a"), Value.FromText("b")])));

        // 参数在清空**之前**求值：追加读到的还是旧值。
        program.Steps.Add(Step.Of("flow.set").WithText("name", "a").WithText("value", "${a}")
            .With("append", StepArg.FromBool(true)));

        // 同一个变量既当输入又当输出：输入解析在清空之前，列表不该丢。
        program.Steps.Add(Step.Of("data.list").WithText("operation", "add")
            .WithVar("list", "l").WithText("value", "c").Output("list", "l"));

        ActionRunResult result = new ActionEngine(TestRegistry.Create(new ListUnit())).Run(program);

        Check.True(result.Ok, "两步都该成功：" + result.Error);
        Check.Equal("xx", result.Variables["a"].AsText(), "flow.set 追加仍读到旧值");
        Check.Equal("a,b,c", string.Join(",", result.Variables["l"].AsList().Select(v => v.AsText())),
            "既当输入又当输出的列表不该被清空吃掉");
    }

    private static string Doc(UnitSpec spec, string key) =>
        spec.Outputs.Single(o => o.Key == key).Description ?? "";

    private static VariableDef Var(string key, ValueKind kind, Value? value = null) =>
        new() { Key = key, Type = kind, Default = value };
}
