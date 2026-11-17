using LTools.Hooks;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 遮罩 / 覆盖层关窗时，把「退出那一下」的收尾吞掉（右键的抬起、Esc / Enter 的抬起）。
/// </summary>
/// <remarks>
/// 这些窗口都是「按下就走」：右键 down 在窗口自己的消息里就被处理掉、窗口随即关闭，
/// 但配对的 up 是**这一次点击的后半截**，关窗之后会落到光标下面的程序上——资源管理器直接
/// 弹右键菜单、编辑器把光标挪过去、对话框像被按了一下回车。所以在 <c>Closed</c> 里立刻
/// 采样「还按着哪个键」，交给两个钩子在它们抬起时吞掉；窗口见过 down、下面的程序两半都没见过，
/// 不会留下按住的假状态。
/// </remarks>
internal static class ExitInput
{
    /// <summary>关窗时调用（UI 线程，窗口刚关、键还按着的那一刻）。</summary>
    public static void Swallow()
    {
        MouseHook.SwallowPendingReleases();
        KeyboardHook.SwallowPendingReleases(Win32.VkEscape, Win32.VkReturn);
    }
}
