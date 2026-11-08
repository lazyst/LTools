using System.Windows;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 动作编辑统一入口（W4 起指向 <see cref="ActionEditorWindow"/>）。
/// 所有调用点（设置页 / 超级面板 / 动作池选择器）只走 <see cref="Show"/>，不自行判断类型：
/// 旧格式动作在打开时迁移成 v2 动作体，保存后即以新结构落盘。
/// </summary>
/// <remarks>
/// 旧编辑器（按动作类型分支的 <c>ActionEditorDialog</c> / <c>CompositeActionDialog</c>）已在 W5 删除。
/// </remarks>
internal static class ActionEditor
{
    /// <summary>
    /// 弹出模态编辑器；取消返回 null。existing=null 表示新建（空动作 + 工具箱）。
    /// <paramref name="owner"/> 可以为 null（超级面板「先关面板再开编辑器」走这条）：
    /// 这时按「无 Owner = 置顶」处理，避免编辑器被其它窗口盖住。
    /// <paramref name="resourceRoot"/> 是草稿的资源根（<c>staging/&lt;id&gt;/files</c>）：
    /// 编辑草稿时传它，编辑器里的测试与校验才认 <c>files/x.png</c>；正式动作不传，
    /// 由本方法自动取它自己的 <c>actions/&lt;id&gt;/files</c>。
    /// </summary>
    public static ActionDto? Show(Window? owner, string title, ActionDto? existing, string? resourceRoot = null)
    {
        var dlg = new ActionEditorWindow(title, existing)
        {
            Owner = owner,
            Topmost = owner?.Topmost ?? true,
            // 正式动作的模板图在动作目录里（actions/<id>/files）；草稿由调用方显式给根。
            ResourceRoot = resourceRoot ?? (existing is null ? null : ActionStore.ResourceRootFor(existing.Id)),
        };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
