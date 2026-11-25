using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Builtin;

namespace LTools.Core.SelfTest;

/// <summary>
/// 动作参数（变量表里「方向 = 输入」的变量）：取哪些、要不要问、界面上那句话怎么拼。
/// </summary>
/// <remarks>
/// 弹框（<c>ParamInputDialog</c>）与「记住上一次填的值」是 UI / 磁盘的事，靠手点验；
/// 这里只断言逻辑与**发现性文案**——界面上「这个动作要填参数」只有这几处，别各写一份。
/// </remarks>
internal static class ActionParamSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("actionParams", "取哪些：只有「输入」且名字非空，按声明顺序", InputsOf));
        cases.Add(new("actionParams", "要不要问：没有输入变量就不弹框", NeedsAsk));
        cases.Add(new("actionParams", "文案：最多列 3 个名字，再多的并成「等 N 个」", Describe));
        cases.Add(new("actionParams", "文案：优先用显示名，没有才用变量名", DescribePrefersLabel));
        cases.Add(new("actionParams", "悬停提示里带上「运行前填参数」（面板 / 悬浮 / 面板管理共用）", Detail));
    }

    private static List<VariableDef> Variables(params VariableDef[] variables) => variables.ToList();

    private static ActionProgram Program(IEnumerable<VariableDef> variables)
    {
        var program = new ActionProgram();
        foreach (var variable in variables) program.Variables.Add(variable);
        return program;
    }

    private static VariableDef Var(string key, VariableDirection direction, string? label = null) => new()
    {
        Key = key,
        Type = ValueKind.text,
        Direction = direction,
        Label = label ?? "",
    };

    private static void InputsOf()
    {
        var program = Program(new[]
        {
            Var("a", VariableDirection.input),
            Var("b", VariableDirection.local),
            Var("c", VariableDirection.output),
            Var("  ", VariableDirection.input),
            Var("d", VariableDirection.input),
        });

        var inputs = ActionParams.InputsOf(program);
        Check.Equal("a,d", string.Join(",", inputs.Select(v => v.Key)), "只收输入方向、名字非空、保持顺序");
        Check.Equal(0, ActionParams.InputsOf(null).Count, "没有动作体 = 没有参数");
    }

    private static void NeedsAsk()
    {
        Check.True(ActionParams.NeedsAsk(ActionDtoWith(Var("a", VariableDirection.input))), "有输入变量要问");
        Check.False(ActionParams.NeedsAsk(ActionDtoWith(Var("a", VariableDirection.local),
            Var("b", VariableDirection.output))), "只有本地 / 输出不问");
        Check.False(ActionParams.NeedsAsk(new ActionDto { Program = Program(Array.Empty<VariableDef>()) }), "空变量表不问");
        Check.False(ActionParams.NeedsAsk(null), "没有动作不问");
    }

    private static ActionDto ActionDtoWith(params VariableDef[] variables) =>
        new() { Program = Program(variables) };

    private static void Describe()
    {
        Check.Equal("", ActionParams.Describe((ActionDto?)null), "没有动作 = 没有这句话");

        Check.Equal("运行前填参数：a", ActionParams.Describe(Variables(Var("a", VariableDirection.input))),
            "一个参数");
        Check.Equal("运行前填参数：a、b", ActionParams.Describe(Variables(
                Var("a", VariableDirection.input), Var("b", VariableDirection.input))),
            "两个参数用顿号连起来");
        Check.Equal("运行前填参数：a、b、c", ActionParams.Describe(Variables(
                Var("a", VariableDirection.input), Var("b", VariableDirection.input),
                Var("c", VariableDirection.input))), "三个参数全列");
        Check.Equal("运行前填参数：a、b 等 4 个", ActionParams.Describe(Variables(
                Var("a", VariableDirection.input), Var("b", VariableDirection.input),
                Var("c", VariableDirection.input), Var("d", VariableDirection.input))),
            "超过三个只列前两个并报总数（这句话要塞进一行 tooltip）");
        Check.Equal("运行前填参数：a", ActionParams.Describe(Variables(
            Var("a", VariableDirection.input), Var("b", VariableDirection.local))), "本地变量不进这句话");
    }

    private static void DescribePrefersLabel()
    {
        Check.Equal("运行前填参数：文件名（a）", ActionParams.Describe(Variables(
                Var("a", VariableDirection.input, "文件名（a）"))),
            "有显示名就用显示名");
        Check.Equal("运行前填参数：a、文件名", ActionParams.Describe(Variables(
                Var("a", VariableDirection.input), Var("b", VariableDirection.input, "文件名"))),
            "同一句话里可以混用");
    }

    private static void Detail()
    {
        const string id = "01selftestactionparams00001";
        var action = ActionDtoWith(Var("target", VariableDirection.input, "目标"));
        action.Id = id;
        action.Name = "填参数的冒烟动作";
        action.Program!.Description = "这句是说明";

        ActionRegistry.Register(action);
        try
        {
            var lines = BindingRef.Detail(id).Split('\n');
            Check.Equal("填参数的冒烟动作", lines[0], "第一行仍是名称");
            Check.Equal("这句是说明", lines.Length > 1 ? lines[1] : "", "第二行仍是说明");
            Check.Equal("运行前填参数：目标", lines.Length > 2 ? lines[2] : "",
                "第三行写明要填哪些参数（面板 / 悬浮 / 面板管理的悬停提示都走这里）");

            action.Program.Variables.Clear();
            Check.Equal("填参数的冒烟动作\n这句是说明", BindingRef.Detail(id), "没有输入变量时不长第三行");
        }
        finally
        {
            ActionRegistry.Remove(id);
        }
    }
}
