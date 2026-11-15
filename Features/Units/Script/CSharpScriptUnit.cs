using LTools.Core.Actions;
using LTools.Features.Script;

namespace LTools.Features.Units.Script;

/// <summary>
/// 运行 C# 脚本（<c>script.csharp</c>）。进程内 Roslyn，与 <see cref="AhkScriptUnit"/> 并存。
/// </summary>
/// <remarks>
/// <para>
/// 脚本通过 globals 的 <c>vars</c> 与流程交互：<c>vars.Text / Number / Flag / Get</c> 读变量、
/// <c>vars.SetText / SetNumber / SetFlag / SetList / Set</c> 写回变量（类型保真）、
/// <c>vars.Print</c> 写输出、<c>vars.CheckStop()</c> 响应停止热键。
/// 最后一行表达式或 <c>return</c> 的值成为输出 <c>value</c>。
/// </para>
/// <para>
/// 失败语义与其它步骤一致：编译 / 运行错误抛 <see cref="ActionException"/>（编译错误带用户行号），
/// 超时按 <see cref="ActionErrors.Timeout"/> 标性质，停止抛 <see cref="ActionAbortedException"/>
/// （不是失败），统一走提示 / 轨迹链。
/// </para>
/// </remarks>
public sealed class CSharpScriptUnit : IStepUnit
{
    private const int DefaultTimeoutMs = 60_000;

    public UnitSpec Spec { get; } = new()
    {
        Key = "script.csharp",
        Name = "C# 脚本",
        Category = "脚本",
        Icon = "script",
        Description = "运行 C# 脚本；vars 读 / 写变量、vars.Print 输出、vars.CheckStop 响应停止热键",
        Kind = UnitKind.action,
        DryRun = false,
        Inputs = new[]
        {
            new InputSpec("code", "代码", ValueKind.text, InputEditor.code,
                Required: true, Raw: true,
                Description: "C# 脚本；最后一行表达式或 return 的值成为输出 value。vars.Text(\"名\") 读变量、vars.SetText(\"名\", 值) 写回"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(DefaultTimeoutMs),
                Description: "0 / -1 = 不限制；其余夹到 1–600000 毫秒。纯 CPU 紧密循环无法强制中断（脚本里用 vars.CheckStop）"),
        },
        Outputs = new[]
        {
            new OutputSpec("value", "结果", Description: "脚本返回的值（保留 list / object / number / bool 类型）"),
            new OutputSpec("stdout", "输出", ValueKind.text, "output",
                Description: "vars.Print 输出的内容（每行一条）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "结果的文本形式"),
            new OutputSpec("found", "有结果", ValueKind.@bool, Description: "脚本是否返回了非空值"),
            new OutputSpec("ok", "成功", ValueKind.@bool,
                Description: "脚本是否正常结束（无编译 / 运行错误 / 超时）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string code = args.Text("code");
        if (string.IsNullOrWhiteSpace(code))
            throw new ActionException("脚本内容为空");

        int timeoutMs = args.Has("timeoutMs")
            ? Timeouts.Normalize(args.Number("timeoutMs"))
            : DefaultTimeoutMs;

        CSharpRunResult result = CSharpRunner.Run(code, ctx.Scope, timeoutMs, ctx.Token);

        // 失败时也写输出，方便看脚本跑到哪一步（与 script.ahk 同口径）。
        ctx.SetOutput("stdout", Value.FromText(string.Join("\n", result.Printed)));
        ctx.SetOutput("value", result.Value);
        ctx.SetOutput("text", Value.FromText(result.Value.AsText()));
        ctx.SetOutput("found", Value.FromBool(!string.IsNullOrEmpty(result.Value.AsText())));
        ctx.SetOutput("ok", Value.FromBool(result.Error == null));

        if (result.Cancelled)
            throw new ActionAbortedException("已停止", userCancelled: true);
        if (result.TimedOut)
            throw ActionErrors.Timeout(result.Error ?? "脚本执行超时");
        if (result.Error != null)
            throw new ActionException(result.Error);
    }
}
