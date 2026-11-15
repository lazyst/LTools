using LTools.Core.Actions;
using LTools.Features.Script;

namespace LTools.Core.SelfTest;

/// <summary>
/// C# 脚本层（<c>script.csharp</c>）：声明、语法检查、返回值类型保真、变量读写、
/// 编译错误行号、停止点。只跑进程内编译 / 求值，不碰键鼠 / 窗口 / 网络 / 用户配置。
/// </summary>
internal static class CSharpSuite
{
    private const string UnitKey = "script.csharp";

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("csharp", "script.csharp 声明完整", Declaration));
        cases.Add(new("csharp", "语法检查：合法 / 非法 / 不执行脚本", CheckSyntax));
        cases.Add(new("csharp", "返回值与类型保真", ReturnValues));
        cases.Add(new("csharp", "变量读写按类型保真", Variables));
        cases.Add(new("csharp", "编译错误带用户行号", CompileErrors));
        cases.Add(new("csharp", "停止点响应取消", StopPoint));
    }

    private static CSharpRunResult Run(string code, CancellationToken token = default) =>
        CSharpRunner.Run(code, new VariableScope(), 0, token);

    private static void Declaration()
    {
        UnitSpec spec = StepRegistry.Default.Get(UnitKey).Spec;
        Check.Equal(UnitKey, spec.Key, "动作单元键");
        Check.Equal("脚本", spec.Category, "分类沿用现有「脚本」");
        Check.False(spec.RunsInDryRun, "脚本不进干跑");
        Check.Equal(UnitKind.action, spec.Kind, "普通步骤（有副作用）");

        InputSpec code = spec.Inputs.Single(i => i.Key == "code");
        Check.True(code.Raw, "代码参数 Raw: true");
        Check.Equal(InputEditor.code, code.Editor, "代码编辑器形态");
        Check.True(code.Required, "代码必填");

        InputSpec timeout = spec.Inputs.Single(i => i.Key == "timeoutMs");
        Check.False(timeout.Advanced, "timeoutMs 必须是可见参数");

        Check.True(spec.Outputs.Any(o => o.Key == "value"), "声明 value 输出");
        Check.True(spec.Outputs.Any(o => o.Key == "stdout"), "声明 stdout 输出");
        spec.Validate();
    }

    private static void CheckSyntax()
    {
        Check.True(CSharpRunner.Check("1 + 1").Ok, "合法表达式语法没问题");
        // 语法合法但会在运行时抛——Check 只编译、不执行，所以仍应通过。
        Check.True(CSharpRunner.Check("throw new System.Exception(\"x\");").Ok,
            "语法检查不执行脚本");
        CSharpCheckResult bad = CSharpRunner.Check("int x = ");
        Check.False(bad.Ok, "缺右值应报错");
        Check.Contains(bad.Message, "第", "语法错误应带行号");
    }

    private static void ReturnValues()
    {
        Check.Equal(2d, Run("1 + 1").Value.AsNumber(), "数字返回值保真");
        Check.Equal("hi", Run("\"hi\"").Value.AsText(), "文本返回值保真");
        Check.Equal(ValueKind.list, Run("new[] { 1, 2, 3 }").Value.Kind, "数组 → 列表");
        Check.Equal(3, Run("new[] { 1, 2, 3 }").Value.Items.Count, "列表项数");
        Check.Equal(ValueKind.@bool, Run("true").Value.Kind, "布尔返回值保真");
    }

    private static void Variables()
    {
        CSharpRunResult text = Run(
            "vars.SetText(\"a\", \"hi\"); return vars.Text(\"a\");");
        Check.Equal("hi", text.Value.AsText(), "vars.SetText / vars.Text");
        Check.Equal(3.5d,
            Run("vars.SetNumber(\"n\", 3.5); return vars.Number(\"n\");").Value.AsNumber(),
            "vars.SetNumber / vars.Number");

        CSharpRunResult list = Run(
            "vars.Set(\"x\", Value.FromList(new[] { Value.FromText(\"a\"), Value.FromText(\"b\") })); return vars.Get(\"x\");");
        Check.Equal(ValueKind.list, list.Value.Kind, "vars.Set 写回的列表不压成文本");
        Check.Equal(2, list.Value.Items.Count, "列表项数保真");
    }

    private static void CompileErrors()
    {
        CSharpRunResult result = Run("int a = 1;\nint b = ;");
        Check.False(result.Error == null, "编译错误应返回失败");
        Check.Contains(result.Error, "第 2 行", "编译错误带用户代码行号");
    }

    private static void StopPoint()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        CSharpRunResult result = Run("vars.CheckStop(); return 1;", cts.Token);
        Check.True(result.Cancelled, "vars.CheckStop 应把停止转成中止而不是失败");
    }
}
