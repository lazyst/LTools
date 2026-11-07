using System.Windows;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 动作编辑统一入口（W4 起指向 <see cref="ActionEditorWindow"/>）。
/// 所有调用点（设置页 / 超级面板 / 动作池选择器）只走 <see cref="Show"/>，不自行判断类型：
/// 旧格式动作在打开时迁移成 v2 动作体，保存后即以新结构落盘。
/// </summary>
/// <remarks>
/// 旧的 <see cref="ActionEditorDialog"/> / <see cref="CompositeActionDialog"/> 保留到 W5 删除，
/// 这里已不再使用它们。
/// </remarks>
internal static class ActionEditor
{
    /// <summary>
    /// 弹出模态编辑器；取消返回 null。existing=null 表示新建（空动作 + 工具箱）。
    /// <paramref name="owner"/> 可以为 null（超级面板「先关面板再开编辑器」走这条）：
    /// 这时按「无 Owner = 置顶」处理，避免编辑器被其它窗口盖住。
    /// </summary>
    public static ActionDto? Show(Window? owner, string title, ActionDto? existing)
    {
        var dlg = new ActionEditorWindow(title, existing)
        {
            Owner = owner,
            Topmost = owner?.Topmost ?? true,
        };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
