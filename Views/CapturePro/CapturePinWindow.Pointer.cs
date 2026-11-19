using System;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的指针动作：双击 / 中键按设置执行（照 Snow Shot 的
/// <c>pin_to_screen/double_click_action</c>、<c>pin_to_screen/middle_mouse_button_action</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游的动作表分两类：<c>reset_zoom</c> / <c>thumbnail_mode</c> 按下就做；
/// <c>close</c> / <c>destroy</c> 只<b>武装</b>，等按钮抬起之后一拍才做（见
/// <see cref="MouseReleaseGesture"/>）。实时贴图的门禁是
/// <see cref="PointerActions.AllowedInLive"/>（只有「不处理 / 重置缩放 / 关闭」过得去）。
/// </para>
/// <para>
/// 「删除贴图」= 连 <c>pins/</c> 里的记录与素材一起删（关掉只是记一条「已关闭」，还能从贴图管理页贴回来），
/// 所以它默认要确认（<see cref="PointerSettings.ConfirmBeforeDestroyingWindow"/>）。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    private MouseReleaseGate? _release;

    private void InitPointerActions() => _release = new MouseReleaseGate(this);

    /// <summary>按下（左键的第二下 / 中键）时按设置分派：立即做的直接做，关 / 删先武装。</summary>
    private void InvokePointerAction(PinPointerAction action, PointerButton button)
    {
        if (action == PinPointerAction.None) return;
        if (_live && !PointerActions.AllowedInLive(action)) return;

        if (action is PinPointerAction.Close or PinPointerAction.Destroy)
        {
            Action run = action == PinPointerAction.Close ? RequestUserClose : ConfirmDestroy;
            _release?.Arm(button, run);
            return;
        }

        ExecutePointerAction(action);
    }

    /// <summary>立即可做的动作（重置缩放 / 缩略图 / 收到屏幕顶部 / 显示文字）。</summary>
    private void ExecutePointerAction(PinPointerAction action)
    {
        switch (action)
        {
            case PinPointerAction.ResetZoom:
                SetZoom(1);
                break;

            case PinPointerAction.ThumbnailMode:
                SetThumbnail(!_thumb);
                break;

            case PinPointerAction.HideToTop:
                ToggleHideToTop();
                break;

            case PinPointerAction.TextRecognition:
                ToggleOcrMode();
                break;
        }
    }

    /// <summary>「关闭贴图」的出口（双击 / 中键 / ✕ / 右键菜单共用）。</summary>
    internal void RequestUserClose()
    {
        Close();
    }

    /// <summary>
    /// 「删除贴图」：先按设置确认，再把历史记录（含两张图）一起删掉。
    /// 删掉之后贴图管理页里也没有了，贴不回来。
    /// </summary>
    internal void ConfirmDestroy()
    {
        if (!PointerSettings.ConfirmBeforeDestroyingWindow)
        {
            DestroyPin();
            return;
        }

        // 贴图不是前台窗口，弹框不能拿它当 Owner（会被 DWM 画成黑块）；
        // ConfirmDialog 无 Owner 时自己置顶。
        if (ConfirmDialog.Confirm(null, "删除贴图", "要删除这张贴图吗？删除后无法恢复。", danger: true, okText: "删除"))
            DestroyPin();
    }

    private void DestroyPin()
    {
        string id = PinId;
        PinId = "";                                  // 先清空：Close() 的 Forget 不再记「已关闭」
        if (id.Length > 0) PinStore.Remove(id);      // 记录 + 素材 + 缩略图一起删
        Close();
    }
}
