using System;
using System.Collections.Generic;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 把全局子动作当成一次动作跑起来（设置页「子动作库 → 试运行」用）。
/// </summary>
/// <remarks>
/// <para>
/// 只服务试运行：不写任何配置，也不改子动作本身。真正被动作调用时仍然走
/// <see cref="ActionEngine.RunSubAction"/>（新作用域 + 按名传参 + 输出回写），
/// 这里只是把定义摊平成一份 <see cref="ActionProgram"/>，复用「试运行」的同一条链路。
/// </para>
/// <para>
/// 走 <see cref="ProgramRunner"/>（不是直接 <c>AutomationRunner</c>）：设置窗在宿主里，
/// 真正跑起来的是提权进程，跨进程的轨迹 / 结果回传都由它接管。
/// </para>
/// </remarks>
internal static class SubActionRunner
{
    /// <summary>启动一次试运行；没跑起来时 <paramref name="error"/> 是可显示的原因。</summary>
    public static bool TryRun(SubActionDef def, IReadOnlyDictionary<string, Value>? inputs,
        Action<ActionRunResult>? completed, out string? error)
    {
        error = null;
        if (def == null)
        {
            error = "子动作不存在";
            return false;
        }

        var program = new ActionProgram
        {
            Id = def.Id,
            Name = def.Name,
            Icon = def.Icon,
            Description = def.Description,
        };

        foreach (var variable in def.Variables)
        {
            var copy = variable.Clone();
            if (inputs != null && inputs.TryGetValue(copy.Key, out var value)) copy.Default = value;
            program.Variables.Add(copy);
        }

        foreach (var step in def.Steps) program.Steps.Add(step.Clone());

        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
        return ProgramRunner.TryRunProgram(name, program, null, null, completed, out error,
            source: RunSource.SubAction);
    }
}
