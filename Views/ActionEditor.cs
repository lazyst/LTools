using System.Windows;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 动作编辑统一分发（§10.1）：按 <see cref="ActionDto.Type"/> 选择普通编辑器
/// <see cref="ActionEditorDialog"/> 或组合动作编辑器 <see cref="CompositeActionDialog"/>。
/// 供所有调用点（设置页 / 超级面板 / 动作池选择器）共用——调用方一律走 <see cref="Show"/>，
/// 无需自行判断类型。
/// </summary>
/// <remarks>
/// 两窗口可**互相跳转**（不带数据）：用户在普通编辑器把类型切到「组合动作」、或在组合窗口点
/// 「改为普通动作…」→ 关闭当前窗口、以新草稿打开另一窗口，结果透传给最初的调用方。
/// 由本方法的 while 循环承载：跳转标志置位即换一种编辑器再开一轮，直到用户确定/取消。
/// </remarks>
internal static class ActionEditor
{
    /// <summary>弹出模态编辑器；取消返回 null。existing=null 表示新建。</summary>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing)
    {
        bool wantComposite = existing != null && existing.Type == ActionType.composite;
        string t = title;

        while (true)
        {
            if (wantComposite)
            {
                var dlg = new CompositeActionDialog(t, existing) { Owner = owner };
                dlg.ShowDialog();
                if (dlg.JumpToNormal) { wantComposite = false; existing = null; t = "新建动作"; continue; }
                return dlg.Result;
            }
            else
            {
                var dlg = new ActionEditorDialog(t, existing) { Owner = owner };
                dlg.ShowDialog();
                if (dlg.JumpToComposite) { wantComposite = true; existing = null; t = "新建组合动作"; continue; }
                return dlg.Result;
            }
        }
    }
}
