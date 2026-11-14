using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>步骤模型与 JSON：三态参数、嵌套子步骤、程序整体往返。</summary>
internal static class ModelSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("model", "参数三态往返", ArgKindsRoundTrip));
        cases.Add(new("model", "整串变量写成字面量仍是字面量", LiteralVariableTextStaysLiteral));
        cases.Add(new("model", "空变量名报错", EmptyVariableNameFails));
        cases.Add(new("model", "内联取值步骤往返", InlineStepRoundTrip));
        cases.Add(new("model", "空值按空文本读", NullArgBecomesEmptyText));
        cases.Add(new("model", "不认识的键当对象字面量", UnknownObjectIsLiteral));
        cases.Add(new("model", "程序整体往返", ProgramRoundTrip));
    }

    private static void ArgKindsRoundTrip()
    {
        Step step = Step.Of("screen.capture")
            .WithText("mode", "rect")
            .With("count", StepArg.FromNumber(3))
            .With("flag", StepArg.FromBool(true))
            .With("list", StepArg.FromValue(Value.FromList(new[] { Value.FromText("a"), Value.FromNumber(1) })))
            .With("obj", StepArg.FromValue(Value.Object(("k", Value.FromText("v")))))
            .WithVar("path", "some.var");

        string json = JsonSerializer.Serialize(step);
        Step back = JsonSerializer.Deserialize<Step>(json)!;

        Check.Equal(StepArgKind.literal, back.In["mode"].Kind, "文本仍是字面量");
        Check.Equal(3d, back.In["count"].Literal.AsNumber(), "数字仍是数字");
        Check.Equal(true, back.In["flag"].Literal.AsBool(), "布尔仍是布尔");
        Check.Equal(ValueKind.list, back.In["list"].Literal.Kind, "列表仍是列表");
        Check.Equal(ValueKind.@object, back.In["obj"].Literal.Kind, "对象仍是对象");
        Check.Equal(StepArgKind.variable, back.In["path"].Kind, "变量仍是变量");
        Check.Equal("some.var", back.In["path"].VariablePath, "变量名");
        Check.Equal(json, JsonSerializer.Serialize(back), "往返后 JSON 应完全一致");
    }

    private static void LiteralVariableTextStaysLiteral()
    {
        Step step = Step.Of("test.echo").WithText("text", "${items}");
        string json = JsonSerializer.Serialize(step);
        Step back = JsonSerializer.Deserialize<Step>(json)!;

        Check.Equal(StepArgKind.literal, back.In["text"].Kind, "写成字面量的 ${items} 仍是字面量");
        Check.Equal("${items}", back.In["text"].Literal.AsText(), "原文保留");
    }

    private static void EmptyVariableNameFails()
    {
        Check.Throws<JsonException>(
            () => JsonSerializer.Deserialize<Step>("{\"unit\":\"x\",\"in\":{\"a\":{\"var\":\"  \"}}}"),
            "{\"var\": \"\"} 应报 JSON 错误");
    }

    private static void InlineStepRoundTrip()
    {
        const string json = """
        {
          "unit": "flow.set",
          "in": {
            "name": "v",
            "value": { "unit": "test.echo", "in": { "text": "hi" }, "out": { "text": "inline" } }
          }
        }
        """;

        Step step = JsonSerializer.Deserialize<Step>(json)!;
        StepArg arg = step.In["value"];

        Check.Equal(StepArgKind.inline, arg.Kind, "带 unit 的对象应读成内联取值");
        Check.Equal("test.echo", arg.InlineStep?.Unit, "内联步骤的动作单元");
        Check.Equal("inline", arg.InlineStep?.Out["text"], "内联步骤的输出映射");
        Check.Contains(JsonSerializer.Serialize(step), "\"unit\":\"test.echo\"", "写回时保留内联步骤");
    }

    private static void NullArgBecomesEmptyText()
    {
        Step step = JsonSerializer.Deserialize<Step>("{\"unit\":\"x\",\"in\":{\"a\":null}}")!;
        Check.Equal(StepArgKind.literal, step.In["a"].Kind, "null 读成字面量");
        Check.Equal("", step.In["a"].Literal.AsText(), "null 读成空文本");
        Check.True(step.In["a"].IsEmpty, "空文本算「等于没填」");
    }

    private static void UnknownObjectIsLiteral()
    {
        Step step = JsonSerializer.Deserialize<Step>("{\"unit\":\"x\",\"in\":{\"a\":{\"foo\":1}}}")!;
        Check.Equal(StepArgKind.literal, step.In["a"].Kind, "没有 var / unit 的对象是字面量");
        Check.Equal(ValueKind.@object, step.In["a"].Literal.Kind, "对象值");
    }

    private static void ProgramRoundTrip()
    {
        var program = new ActionProgram
        {
            Id = "id1",
            Name = "往返测试",
            Icon = "star",
            Description = "一句话说明",
        };
        program.Variables.Add(new VariableDef
        {
            Key = "items",
            Type = ValueKind.list,
            Default = Value.FromList(new[] { Value.FromText("a") }),
            Label = "列表",
            Direction = VariableDirection.input,
        });

        Step loop = Step.Of("flow.forEach").WithVar("items", "items").Output("item", "cur");
        loop.Note = "遍历";
        loop.DelayMs = 20;
        loop.Disabled = true;
        loop.OnFail = FailMode.@continue;
        loop.Body.Add(Step.Of("test.echo").WithText("text", "${cur}").Output("text", "last"));
        loop.Then.Add(Step.Of("flow.break"));

        Step choice = Step.Of("flow.if").With("left", StepArg.FromText("a")).WithText("op", "isEmpty");
        choice.Else.Add(Step.Of("flow.set").WithText("name", "x").WithText("value", "1"));
        program.Steps.Add(loop);
        program.Steps.Add(choice);

        string json = JsonSerializer.Serialize(program);
        ActionProgram back = JsonSerializer.Deserialize<ActionProgram>(json)!;

        Check.Equal(json, JsonSerializer.Serialize(back), "程序往返后 JSON 应完全一致");
        Check.Equal("一句话说明", back.Description, "描述字段");
        Check.Equal(ValueKind.list, back.Variables[0].Type, "变量类型");
        Check.Equal(VariableDirection.input, back.Variables[0].Direction, "变量方向");
        Check.Equal(1, back.Steps[0].Body.Count, "循环体");
        Check.Equal(1, back.Steps[0].Then.Count, "条件分支（then）");
        Check.Equal(1, back.Steps[1].Else.Count, "条件分支（else）");
        Check.Equal(FailMode.@continue, back.Steps[0].OnFail, "失败策略");
        Check.Equal(20, back.Steps[0].DelayMs, "步骤延迟");
        Check.True(back.Steps[0].Disabled, "跳过标记");
        Check.Equal("遍历", back.Steps[0].Note, "备注");
    }
}
