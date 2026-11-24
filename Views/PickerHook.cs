using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Hooks;

namespace LTools.Views;

/// <summary>
/// 「全屏点选器正在占用输入」的唯一开关：让「裸右键长按 = 面板」的鼠标钩子让路。
/// </summary>
/// <remarks>
/// 钩子跑在提权进程、点选器跑在宿主（普通权限），所以分两种情况：
/// 提权进程（点选器可能还在本地，如截图 Pro）直接调 <see cref="MouseHook.SetWindowPickerActive"/>；
/// 宿主过链路通知提权进程（<see cref="InputBridge"/>）。新增全屏遮罩入口一律调这里。
/// </remarks>
internal static class PickerHook
{
    public static void SetActive(bool active)
    {
        if (!AppRole.IsUiHost)
        {
            MouseHook.SetWindowPickerActive(active);
            return;
        }

        InputBridge.SetPickerActive(active);
    }
}
