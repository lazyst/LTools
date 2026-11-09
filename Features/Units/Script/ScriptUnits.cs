using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features.Script;

namespace LTools.Features.Units.Script;

/// <summary>
/// 运行 AutoHotkey v2 脚本（<c>script.ahk</c>）。运行时随包分发，不要求用户预先安装 AHK。
/// </summary>
/// <remarks>
/// <para>
/// 脚本通过前言提供的三个函数与流程交互（见 <see cref="AhkRunner"/>）：
/// <c>LT_Var(name, default)</c> 读运行变量、<c>LT_Set(name, value)</c> 写回运行变量、
/// <c>LT_Print(text)</c> 往标准输出写一行。脚本里其余部分就是普通 AHK v2 代码。
/// </para>
/// <para>
/// 失败语义与其它步骤一致：抛 <see cref="ActionException"/>，消息里带用户代码的行号，
/// 由引擎加上「第 N 行「AutoHotkey 脚本」失败：」前缀，走同一条提示 / 轨迹链。
/// AHK 自己的错误弹窗已由前言的 <c>OnError</c> 抑制。
/// </para>
/// </remarks>
public sealed class AhkScriptUnit : IStepUnit
{
    /// <summary>超时上限（毫秒）：再长就该用「运行命令」跑常驻脚本了。</summary>
    private const int MaxTimeoutMs = 3_600_000;

    public UnitSpec Spec { get; } = new()
    {
        Key = "script.ahk",
        Name = "AutoHotkey 脚本",
        Category = "脚本",
        Icon = "script",
        Description = "运行 AutoHotkey v2 代码；LT_Var 读变量、LT_Set 写回变量、LT_Print 写到标准输出",
        Inputs = new[]
        {
            new InputSpec("code", "代码", ValueKind.text, InputEditor.code,
                Required: true, Raw: true),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(60_000), Advanced: true),
        },
        Outputs = new[]
        {
            new OutputSpec("stdout", "标准输出", ValueKind.text, "output",
                Description: "脚本用 LT_Print 输出的内容（每行一条）"),
            new OutputSpec("stderr", "错误输出", ValueKind.text,
                Description: "AutoHotkey 自己写到 stderr 的内容；脚本正常运行时为空"),
            new OutputSpec("exit", "退出码", ValueKind.number,
                Description: "脚本进程的退出码，0 表示成功"),
            new OutputSpec("ok", "成功", ValueKind.@bool,
                Description: "脚本是否正常结束（无语法错误 / 运行时错误 / 超时）"),
            new OutputSpec("value", "完整结果", ValueKind.@object,
                Description: "整步结果（含以上各项与 LT_Set 写回的变量），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string code = args.Text("code");
        if (string.IsNullOrWhiteSpace(code))
            throw new ActionException("脚本内容为空");

        int timeoutMs = args.Has("timeoutMs")
            ? (int)Math.Clamp(Math.Round(args.Number("timeoutMs")), 0, MaxTimeoutMs)
            : 60_000;

        var variables = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ctx.Scope.Flatten())
            variables[pair.Key] = pair.Value;

        AhkRunResult result = AhkRunner.Run(
            code, variables, timeoutMs, args.Text("workdir"), ctx);

        // LT_Set 的语义就是「写运行变量」：与「设置变量」单元一样直接落到当前作用域，
        // 后续步骤用 ${名字} 就能取到；失败时也写，方便看脚本跑到哪一步。
        foreach (var pair in result.Variables)
            ctx.Scope.Set(pair.Key, Value.FromText(pair.Value));

        ctx.SetOutput("stdout", Value.FromText(result.Stdout));
        ctx.SetOutput("stderr", Value.FromText(result.Stderr));
        ctx.SetOutput("exit", Value.FromNumber(result.ExitCode));
        ctx.SetOutput("ok", Value.FromBool(result.Error == null));
        ctx.SetOutput("value", BuildValue(result));

        if (result.Error != null) throw new ActionException(result.Error);
    }

    /// <summary>整步结果对象：命令结果的那套成员 + LT_Set 写回的变量。</summary>
    private static Value BuildValue(AhkRunResult result)
    {
        var members = new List<(string Key, Value Value)>
        {
            ("text", Value.FromText(result.Stdout)),
            ("stdout", Value.FromText(result.Stdout)),
            ("out", Value.FromText(result.Stdout)),
            ("stderr", Value.FromText(result.Stderr)),
            ("err", Value.FromText(result.Stderr)),
            ("exit", Value.FromNumber(result.ExitCode)),
            ("code", Value.FromNumber(result.ExitCode)),
            ("number", Value.FromNumber(result.ExitCode)),
            ("ok", Value.FromBool(result.Error == null)),
            ("found", Value.FromBool(result.Error == null)),
        };

        var seen = new HashSet<string>(members.Select(m => m.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in result.Variables)
        {
            if (!seen.Add(pair.Key)) continue;
            members.Add((pair.Key, Value.FromText(pair.Value)));
        }

        return Value.Object(members.ToArray());
    }
}
