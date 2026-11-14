using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 撤销快照里的一行变量：存用户**当时填的原文**（含还没解析的默认值文本），
/// 恢复时照原样写回去，所以「列表 / 对象的多行默认值」也不会在撤销 / 重做之间变样。
/// </summary>
/// <param name="Name">名称输入框文本（可能为空 = 还没填完）。</param>
/// <param name="Kind">类型下拉当前项。</param>
/// <param name="Direction">方向下拉当前项（动作 / 子动作都是这一列）。</param>
/// <param name="Default">默认值输入框原文（不解析）。</param>
/// <param name="Origin">「谁写进来的」那行小字（步骤自动登记 / 手动创建）。</param>
/// <param name="AutoType">类型是否仍由步骤声明自动决定（用户改过类型就是 false）。</param>
/// <param name="LastName">上一次确认过的名字（改名同步用）。</param>
internal sealed record VariableDraft(
    string Name,
    ValueKind Kind,
    VariableDirection Direction,
    string Default,
    string Origin,
    bool AutoType,
    string LastName);

/// <summary>
/// 一次编辑动作的快照：步骤树 + 变量表 + 这次操作叫什么。
/// </summary>
/// <remarks>
/// 以前只快照步骤（<c>CloneSteps</c>），撤销一次「删除步骤」只把步骤放回来，
/// 变量表还是删完之后的样子——删掉的步骤写出的变量、用户当时刚加的变量都对不上。
/// 现在两边一起进快照（原文进，不解析），撤销 / 重做是同一个 <see cref="DeepClone"/>。
/// </remarks>
internal sealed record EditorUndoState(
    List<Step> Steps,
    List<VariableDraft> Variables,
    string Label)
{
    /// <summary>再拷一份（撤销 / 重做互相搬运时用，避免两边共享同一批步骤对象）。</summary>
    public EditorUndoState DeepClone() => new(
        Steps.Select(step => step.Clone()).ToList(),
        new List<VariableDraft>(Variables),
        Label);
}
