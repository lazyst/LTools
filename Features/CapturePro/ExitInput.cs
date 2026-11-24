using LTools.Core;
using LTools.Core.Link;
using LTools.Hooks;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 遮罩 / 覆盖层关窗时，把「退出那一下」的收尾吞掉（右键的抬起、Esc / Enter 的抬起）。
/// </summary>
/// <remarks>
/// <para>
/// 这些窗口都是「按下就走」：右键 down 在窗口自己的消息里就被处理掉、窗口随即关闭，
/// 但配对的 up 是**这一次点击的后半截**，关窗之后会落到光标下面的程序上——资源管理器直接
/// 弹右键菜单、编辑器把光标挪过去、对话框像被按了一下回车。所以关窗时立刻采样「还按着哪个键」，
/// 交给两个钩子在它们抬起时吞掉；窗口见过 down、下面的程序两半都没见过，不会留下按住的假状态。
/// </para>
/// <para>
/// **采样必须在关窗的这一刻、在关窗的那个进程里做**（跨进程要几毫秒，手快就会读成「已经松开」）：
/// 宿主里没有钩子，所以采样完把结果经链路送给提权进程（<see cref="InputBridge.Swallow"/>），
/// 由它上膛（<see cref="Apply"/>）。窗口在提权进程（截图 Pro 还没搬完时）就直接本地采样 + 上膛。
/// </para>
/// </remarks>
internal static class ExitInput
{
    /// <summary>关窗时调用（UI 线程，窗口刚关、键还按着的那一刻）。</summary>
    public static void Swallow()
    {
        bool left = IsDown(Win32.VkLbutton);
        bool right = IsDown(Win32.VkRbutton);
        bool middle = IsDown(Win32.VkMbutton);
        int key = IsDown(Win32.VkEscape) ? Win32.VkEscape
            : IsDown(Win32.VkReturn) ? Win32.VkReturn
            : 0;

        if (!left && !right && !middle && key == 0) return;

        var sampled = new SwallowInputEvent(left, right, middle, key);
        if (AppRole.IsUiHost) InputBridge.Swallow(sampled);
        else Apply(sampled);
    }

    /// <summary>执行器侧：把采样结果交给两个钩子上膛（宿主送来的、本进程刚采的都走这里）。</summary>
    public static void Apply(SwallowInputEvent sampled)
    {
        MouseHook.SwallowReleases(sampled.Left, sampled.Right, sampled.Middle);
        KeyboardHook.ArmRelease(sampled.Key);
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
}
