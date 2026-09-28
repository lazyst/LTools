using System.Threading.Tasks;
using CapsLockPro.Core;
using CapsLockPro.Native;

namespace CapsLockPro.Features;

/// <summary>
/// 杂项热键（对应原版 lib/QuickNote.ahk 前半的 #HotIf 块）。
/// 在 CapsLock 按住 + 工具启用期间分发非编辑类键：放大镜 / 空置键 / 双引号花括号 / 快速搜索 /
/// 速记窗口 / 设置。由 <see cref="Hooks.KeyboardHook"/> 在 vim 分发之后调用。
/// </summary>
/// <remarks>
/// 每个动作返回 true 即吞掉该键（keyup 由 <see cref="Core.AppState.SwallowedVks"/> 平衡）。
/// -/-= 空置键对应 AHK <c>Send("")</c>（仅吞键、不注入任何事件）。
/// '"/[]/ 用 <see cref="InputHelper.SendText"/> 注入符号文本（VkKeyScanW 取布局无关的键 + shift 状态）。
/// 放大镜与快速搜索涉及注入/Sleep/剪贴板，在后台线程执行，不阻塞钩子回调。
/// </remarks>
internal static class MiscKeys
{
    /// <summary>处理一次 CapsLock+组合键。返回 true 表示已吞掉该键。</summary>
    public static bool TryHandle(ushort vk)
    {
        switch (vk)
        {
            case Win32.VkTab: ToggleMagnifier(); return true;
            case Win32.VkOemMinus: return true;               // - 空置键 (Send "")
            case Win32.VkOemPlus: return true;                // = 空置键
            case Win32.VkOem7: InputHelper.SendText("\""); return true;  // ' → "
            case Win32.VkOem4: InputHelper.SendText("{"); return true;   // [ → {
            case Win32.VkOem6: InputHelper.SendText("}"); return true;    // ] → }
            case 'Q': QuickSearch.Start(); return true;
            case 'N': QuickNote.Toggle(); return true;
            case Win32.VkOem5: ConfigHelper.Toggle(); return true;        // \ → 设置
            default: return false;
        }
    }

    /// <summary>
    /// 切换 Win11 原生放大镜（对应 AHK Tab:: 放大镜）。
    /// 已开则关闭；未开则发 Win+= 打开，1s 后最小化放大镜控制窗口。
    /// 在后台线程执行（含 1s Sleep，避免阻塞钩子）。
    /// <see cref="InternalActionRegistry"/> 的 <c>magnifier.toggle</c> 直接复用本方法。
    /// </summary>
    internal static void ToggleMagnifier() => Task.Run(() =>
    {
        try
        {
            var hwnd = Win32.FindWindow("MagUIClass", null);
            if (hwnd != IntPtr.Zero)
            {
                Win32.SendMessage(hwnd, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
                ShowTooltip("放大镜已关闭");
            }
            else
            {
                // Win+=（VK_LWIN + VK_OEM_PLUS 和弦）
                InputHelper.Combo((ushort)Win32.VkLwin, (ushort)Win32.VkOemPlus);
                ShowTooltip("放大镜已开启");
                // 1s 后最小化放大镜控制窗口
                System.Threading.Thread.Sleep(1000);
                var mag = Win32.FindWindow("MagUIClass", null);
                if (mag != IntPtr.Zero)
                {
                    Win32.ShowWindowAsync(mag, Win32.SwMinimize);
                    ShowTooltip("放大镜控制窗口已最小化");
                }
            }
        }
        catch { /* 放大镜操作失败静默 */ }
    });

    private static void ShowTooltip(string msg) =>
        Core.TrayService.Notify(msg);
}
