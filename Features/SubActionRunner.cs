using System;
using System.Collections.Generic;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 把全局子动作当成一次动作跑起来（设置页「子动作库 → 试运行」用）。
/// </summary>
/// <remarks>
/// 只服务试运行：不写任何配置，也不改子动作本身。真正被动作调用时仍然走
/// <see cref="ActionEngine.RunSubAction"/>（新作用域 + 按名传参 + 输出回写），
/// 这里只是把定义摊平成一份 <see cref="ActionProgram"/>，复用编辑器试运行的同一条链路。
/// </remarks>
internal static class SubActionRunner
{
    /// <summary>启动一次试运行；已有动作在跑时返回 false。</summary>
    public static bool TryRun(SubActionDef def, IReadOnlyDictionary<string, Value>? inputs,
        Action<ActionRunResult>? completed = null)
    {
        if (def == null) return false;

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
        return AutomationRunner.TryRunProgram(name, program, null, null, completed, null);
    }
}
