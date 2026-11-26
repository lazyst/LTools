using System;
using System.Collections.Generic;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.UIA3;
using LTools.Features.CapturePro;

namespace LTools.Features;

/// <summary>
/// 控件选择器的取数实现：把 UI Automation 元素包成 <see cref="SnapChild"/> / <see cref="ControlTarget"/>，
/// 交给 <see cref="SnapTree"/> / <see cref="SnapProbe"/> 沿光标那条支路懒下钻。
/// </summary>
/// <remarks>
/// <para>
/// 与截图吸附共用同一套 walker，差别只有「每个节点多带身份」：吸附只要一个能贴边的标签，
/// 选择器要名称 / AutomationId / 类型 / 类名。**悬停不再做整棵快照**——旧的
/// <see cref="ControlTargets.Snapshot"/>（深 12 / 1000 节点 / 每个节点 7 个属性）只留给
/// 「从控件树选…」对话框与 MCP 的 <c>ltools_uia_tree</c>，而且改成打开对话框时才扫。
/// </para>
/// <para>
/// 同名计数走 provider 自己的条件查找（<c>FindAllDescendants(名称 + id)</c>，一次调用），
/// 拿到结果还要**逐条核对身份**：有些 provider 会忽略条件，直接报个数就是假消息。
/// 核不上 / 太多就返回 <c>null</c>（「不确定」），卡片上宁可不写。
/// </para>
/// </remarks>
internal sealed class ControlSnapSource : ISnapSource
{
    /// <summary>
    /// 真正的取数（与截图吸附同一份：UIA + 缓存请求）。这里只做一层壳，
    /// 因为点选器另外还要「同名计数」那一个 provider 侧查找。
    /// </summary>
    private readonly UiaSnapSource _snap = new();

    private readonly UIA3Automation _automation = new()
    {
        // 与截图吸附同一口径：慢应用首次建树经常超过 400 ms
        ConnectionTimeout = TimeSpan.FromMilliseconds(500),
        TransactionTimeout = TimeSpan.FromMilliseconds(900),
    };

    public object? Root(IntPtr hwnd) => _snap.Root(hwnd);

    public IReadOnlyList<SnapChild>? Children(object node) => _snap.Children(node);

    public string Label(object node) => _snap.Label(node);

    public object? Describe(object node) => _snap.Describe(node);

    /// <summary>
    /// 窗口里有多少个「名称 + AutomationId + 类型」都一样的控件；<c>null</c> = 查不准
    /// （provider 忽略条件 / 结果太多）。一次 provider 侧查找，不在全量快照里数。
    /// </summary>
    public int? CountSame(IntPtr hwnd, ControlTarget target, int cap = 50)
    {
        string name = target.Name.Trim();
        string id = target.AutomationId.Trim();
        if (name.Length == 0 && id.Length == 0) return 1;

        try
        {
            AutomationElement? root = _automation.FromHandle(hwnd);
            if (root == null) return null;

            ConditionFactory factory = _automation.ConditionFactory;

            // 名称与 id 用 AND；只有其中一个时单独用（FlaUI 没有 TrueCondition，别写成空条件）
            ConditionBase condition = name.Length > 0 && id.Length > 0
                ? factory.ByName(name).And(factory.ByAutomationId(id))
                : name.Length > 0
                    ? factory.ByName(name)
                    : factory.ByAutomationId(id);

            AutomationElement[] found = root.FindAllDescendants(condition);
            int count = 0;

            foreach (AutomationElement element in found)
            {
                // 条件可能被 provider 忽略：结果对不上就不要给一个假数字
                if (name.Length > 0 &&
                    !ControlTargets.ReadName(element).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (id.Length > 0 &&
                    !ControlTargets.ReadAutomationId(element).Equals(id, StringComparison.OrdinalIgnoreCase))
                    return null;

                string type = ControlTargets.ReadControlType(element);
                if (target.ControlType.Length > 0 &&
                    !type.Equals(target.ControlType, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (++count > cap) return null;
            }

            return count;
        }
        catch
        {
            return null;
        }
    }
}
