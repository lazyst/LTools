using System;
using System.Collections.Generic;

namespace LTools.Core.Actions;

/// <summary>
/// 「这一步写出了哪个变量」的唯一判定：输出映射（<see cref="Step.Out"/>）与
/// 「设置变量」的目标名（<c>flow.set</c> 的 <c>name</c> 参数）。
/// </summary>
/// <remarks>
/// <para>
/// 写变量有两条路：普通单元把输出键映射到变量名（<c>out</c>），设置变量把目标名放在参数里、
/// 运行时直接 <c>Scope.Set</c>。编辑器变量表 / 引用统计 / 改名同步 / 保存时补声明 / 静态校验
/// 都必须看全这两条，漏掉设置变量就会出现「明明设置了变量，变量表里却没有它、▾ 里也选不到」。
/// </para>
/// <para>
/// 设置变量的名字是**字面量**（<see cref="InputEditor.variable"/> 在文本类型下按名字存）；
/// 写成变量 / 内联取值 / 带 <c>${…}</c> 的模板时运行期才知道写进哪个变量，这里认不出来，交给运行时。
/// </para>
/// </remarks>
internal static class VariableWrites
{
    /// <summary>「设置变量」的单元键。</summary>
    public const string SetUnitKey = "flow.set";

    /// <summary>「设置变量」放目标变量名的参数键。</summary>
    private const string SetNameKey = "name";

    /// <summary>
    /// 这一步写出的变量。名字可能为空（配置坏了），由调用方决定报错还是跳过。
    /// </summary>
    public static IEnumerable<WrittenVariable> Of(Step step)
    {
        foreach (var pair in step.Out)
            yield return new WrittenVariable(pair.Value ?? "", pair.Key, IsOutput: true);

        if (!step.Unit.Equals(SetUnitKey, StringComparison.OrdinalIgnoreCase)) yield break;

        // 名字写成模板（`${前缀}_a`）时运行期才知道写进哪个变量，静态登记 / 校验认不了。
        if (ProgramWalker.LiteralText(step, SetNameKey) is not { } target) yield break;
        if (target.Contains("${", StringComparison.Ordinal)) yield break;

        yield return new WrittenVariable(target, SetNameKey, IsOutput: false);
    }

    /// <summary>
    /// 改名同步：把「设置变量」的目标名一起改掉（输出映射由调用方自己改）。
    /// 返回改动处数（0 / 1）；名字是变量 / 内联取值的动态目标不动。
    /// </summary>
    public static int RenameTarget(Step step, string oldName, string newName)
    {
        if (oldName.Length == 0 || newName.Length == 0) return 0;
        if (!step.Unit.Equals(SetUnitKey, StringComparison.OrdinalIgnoreCase)) return 0;
        if (ProgramWalker.LiteralText(step, SetNameKey)?.Trim() is not { Length: > 0 } current) return 0;
        if (!string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase)) return 0;

        step.In[SetNameKey] = StepArg.FromText(newName);
        return 1;
    }
}

/// <summary>
/// 一处「写变量」：输出映射（<see cref="IsOutput"/>）或设置变量的目标名。
/// <paramref name="Key"/> 是输出键 / 参数键，用来拼变量表里的来源说明。
/// </summary>
internal readonly record struct WrittenVariable(string Name, string Key, bool IsOutput)
{
    /// <summary>去掉前后空白的变量名（空 = 这一处写不出有效变量）。</summary>
    public string TrimmedName => (Name ?? "").Trim();
}
