using System.Windows;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 动作编辑统一入口（W4 起指向 <see cref="ActionEditorWindow"/>）。
/// 所有调用点（设置页 / 超级面板 / 动作池选择器）只走 <see cref="Show"/>，不自行判断类型。
/// </summary>
/// <remarks>旧编辑器（按动作类型分支的 <c>ActionEditorDialog</c> / <c>CompositeActionDialog</c>）已在 W5 删除。</remarks>
internal static class ActionEditor
{
    /// <summary>
    /// 弹出模态编辑器；取消 / 已经有同一个动作的编辑器时返回 null。existing=null 表示新建（空动作 + 工具箱）。
    /// <paramref name="owner"/> 可以为 null（超级面板「先关面板再开编辑器」走这条）。
    /// <paramref name="resourceRoot"/> 是草稿的资源根（<c>staging/&lt;id&gt;/files</c>）：编辑草稿时传它，
    /// 编辑器里的测试与校验才认 <c>files/x.png</c>；正式动作不传，由本方法自动取它自己的 <c>actions/&lt;id&gt;/files</c>。
    /// </summary>
    /// <remarks>
    /// **编辑器不置顶**（2026-11-15 按用户要求取消）：写动作常常要切到别的程序里对照 / 取坐标，
    /// 置顶的编辑器正好挡在那上面。它 <c>ShowInTaskbar</c>，被盖住也能从任务栏点回来；
    /// <c>Owner</c> 仍然传，所以它至少压在宿主之上、并随宿主最小化。
    /// **同一个动作只开一个编辑器**（<see cref="SingleEditor"/>）：第二个入口把已有的拉到前面，
    /// 否则两边各拿一份快照、后保存的会把对方刚改的步骤盖掉。
    /// </remarks>
    public static ActionDto? Show(Window? owner, string title, ActionDto? existing, string? resourceRoot = null)
    {
        string? key = KeyOf(existing, resourceRoot);
        if (SingleEditor.Reveal(key)) return null;

        var dlg = new ActionEditorWindow(title, existing)
        {
            Owner = owner,
            Topmost = false,
            // 正式动作的模板图在动作目录里（actions/<id>/files）；草稿由调用方显式给根。
            ResourceRoot = resourceRoot ?? (existing is null ? null : ActionStore.ResourceRootFor(existing.Id)),
        };

        SingleEditor.Track(key, dlg);
        try
        {
            // 模态窗口自己跑消息循环：挂在 Loaded / ContentRendered 上，等它真的显示出来再提到最前
            // （第一次从面板打开时前台锁会把「活动窗口」压在别的程序后面）。
            WindowFront.Attach(dlg);
            dlg.ShowDialog();
        }
        finally
        {
            SingleEditor.Release(key, dlg);
        }

        return dlg.Result;
    }

    /// <summary>
    /// 同一个动作的键：有 Id 用 Id；草稿（还没采纳，没有动作 Id）按它自己的资源根认；
    /// 「新建动作」两者都没有 → null（想同时开几个就开几个）。
    /// </summary>
    private static string? KeyOf(ActionDto? existing, string? resourceRoot)
    {
        string id = existing?.Id?.Trim() ?? "";
        if (id.Length > 0) return "action:" + id;

        string root = resourceRoot?.Trim() ?? "";
        return root.Length > 0 ? "draft:" + root : null;
    }
}
