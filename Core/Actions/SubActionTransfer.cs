using LTools.Features;

namespace LTools.Core.Actions;

/// <summary>
/// 子动作 ↔ 动作互转：两边同构（变量表 + 步骤），差别只在变量的「方向」。
/// </summary>
/// <remarks>
/// 方向是子动作独有的概念，转过去之后<b>一律写成 <see cref="VariableDirection.local"/></b>：
/// 自动猜「哪些是输入、哪些是输出」只会猜错（一个变量常常既被读又被写），
/// 让用户在变量表里按需要改，比替他决定更省事。
/// <para>
/// 互转只搬内容，不搬 id：动作 id（<c>a1</c>）与子动作 id（<c>sub-xxxxxxxx</c>）各由自己的库生成。
/// 图片也不搬——子动作没有自己的 <c>files/</c>，动作里的模板图仍留在原动作目录。
/// </para>
/// </remarks>
internal static class SubActionTransfer
{
    /// <summary>子动作 → 新动作（Id 留空，由 <see cref="ConfigStore.AddAction"/> 或调用方生成）。</summary>
    public static ActionDto ToAction(SubActionDef def)
    {
        ArgumentNullException.ThrowIfNull(def);

        var program = new ActionProgram
        {
            Name = def.Name,
            Icon = def.Icon ?? "",
            Description = def.Description ?? "",
        };

        foreach (var variable in def.Variables) program.Variables.Add(Local(variable));
        foreach (var step in def.Steps) program.Steps.Add(step.Clone());

        return new ActionDto
        {
            Name = string.IsNullOrWhiteSpace(def.Name) ? "子动作" : def.Name,
            Icon = string.IsNullOrWhiteSpace(def.Icon) ? IconCatalog.Default : def.Icon,
            Program = program,
        };
    }

    /// <summary>动作 → 子动作（<paramref name="id"/> 是新的子动作 id，见 <see cref="SubActionStore.NewId"/>）。</summary>
    public static SubActionDef FromAction(ActionDto action, string id)
    {
        ArgumentNullException.ThrowIfNull(action);

        var def = new SubActionDef
        {
            Id = (id ?? "").Trim(),
            Name = action.Name ?? "",
            Icon = action.Icon ?? "",
            Description = action.Program?.Description ?? "",
        };

        if (action.Program is { } program)
        {
            foreach (var variable in program.Variables) def.Variables.Add(Local(variable));
            foreach (var step in program.Steps) def.Steps.Add(step.Clone());
        }

        return def;
    }

    /// <summary>复制一条变量声明并抹掉方向（值不可变，直接共享）。</summary>
    private static VariableDef Local(VariableDef variable)
    {
        var copy = variable.Clone();
        copy.Direction = VariableDirection.local;
        return copy;
    }
}
