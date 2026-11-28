using System;
using System.Threading;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 运行中的「目标窗口」：注入键鼠前把目标切到前台，让用户中途切走也不打偏。
/// </summary>
/// <remarks>
/// <para>
/// 三个输入类动作单元（<c>input.sendKeys</c> / <c>text.send</c> / <c>input.mouse</c>）的可选参数，
/// 留空 = 当前前台（与以前一致）。见 <c>docs/RUN_INTEROP.md</c> §7.2。
/// </para>
/// <para>
/// **不假装成功**：找不到窗口 → <see cref="ActionErrors.NotFound"/>；找到了但没能切到前台
/// （前台锁 / 目标最小化被别的置顶窗挡住）→ 这一步失败，别把按键打进别的程序。
/// 等待只用几百毫秒的短轮询，不阻塞钩子线程（这里跑在运行线程上）。
/// </para>
/// </remarks>
internal static class RunTarget
{
    /// <summary>前台锁偶尔慢一拍：最多等这么久（毫秒）。</summary>
    private const int ActivateWaitMs = 300;

    /// <summary>有目标就切过去；留空什么都不做。</summary>
    /// <param name="titleFragment">窗口标题（包含匹配，留空表示不按标题过滤）。</param>
    /// <param name="className">窗口类名（精确匹配）。</param>
    /// <param name="unitName">出错文案里的动作单元名（如「模拟按键」）。</param>
    public static void Activate(string? titleFragment, string? className, string unitName)
    {
        if (string.IsNullOrWhiteSpace(titleFragment) && string.IsNullOrWhiteSpace(className)) return;

        IntPtr hwnd = WindowFinder.Find(titleFragment, className, out string matched);
        if (hwnd == IntPtr.Zero)
            throw ActionErrors.NotFound(
                $"{unitName}：找不到目标窗口「{WindowFinder.Describe(titleFragment, className)}」");

        if (Win32.GetForegroundWindow() == hwnd) return;

        WindowAction.ActivateNoWait(hwnd);

        for (int waited = 0; waited < ActivateWaitMs; waited += 10)
        {
            if (Win32.GetForegroundWindow() == hwnd) return;
            Thread.Sleep(10);
        }

        throw new ActionException(
            $"{unitName}：没能把「{matched}」切到前台（可能被系统前台锁或别的置顶窗口挡住），这一步没有执行");
    }
}
