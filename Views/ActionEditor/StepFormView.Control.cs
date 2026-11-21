using System;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 「选控件…」之后回填：一次写进名称 / AutomationId / 控件类型 / 窗口类名。
/// </summary>
/// <remarks>
/// 控件选择器是 <c>ui.uia</c> 的定位条件入口，与屏幕取点一次填好 X / Y 同一个思路：
/// 让用户点一次，别抄四个字段。按用户选的口径「名称 + AutomationId 都填」——步骤里看得懂，
/// 两条同时变化才失效；`Custom` 类型不写（它是个兜底值，当过滤条件会把目标筛歪）。
/// 窗口标题不动（留空 = 前台窗口），只把**窗口类名**填上：类名稳定，能避免动作跑到别的程序里。
/// </remarks>
public partial class StepFormView
{
    private void ApplyPickedControl(PickedControl picked)
    {
        bool wrote = false;
        wrote |= WritePickedField("name", picked.Name);
        wrote |= WritePickedField("automationId", picked.AutomationId);
        wrote |= WritePickedField("controlType", string.Equals(picked.ControlType, "Custom",
            StringComparison.OrdinalIgnoreCase) ? "" : picked.ControlType);
        wrote |= WritePickedField("className", picked.WindowClass);

        if (wrote) OnEdited();
    }

    /// <summary>写一行（这个动作单元没有这个参数就跳过）；返回是否写到了。</summary>
    private bool WritePickedField(string key, string value)
    {
        InputSpec? spec = _spec.Inputs.FirstOrDefault(i =>
            string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
        if (spec == null || !_fieldSetters.TryGetValue(spec.Key, out Action<string>? set)) return false;

        set(value);
        return true;
    }
}
