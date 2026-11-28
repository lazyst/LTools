using LTools.Core.Running;

namespace LTools.Features;

/// <summary>
/// 「动作运行时钩子怎么让路」的**唯一判定**：纯函数，钩子回调里只读一次快照再问一句。
/// </summary>
/// <remarks>
/// <para>
/// 四档只改这里的返回值，不改钩子结构：<see cref="ActionRunMode.exclusive"/> 是产品回退档
/// （与放开并发之前一模一样：运行中整段让路），其余三档「运行中照常使用 LTools」。
/// </para>
/// <para>
/// 纯函数 → <c>--selftest</c> 直接断言，不必伪造钩子事件（照 <c>DoubleClickEditRow</c> /
/// <c>MenuCone</c> 的路子）。判定结果只影响「这条按键 / 手势归谁」，开跑 / 排队 / 拒绝
/// 仍在 <c>RunCoordinator.Submit</c> 一处（见 <c>docs/RUN_INTEROP.md</c> §3.6 / §6.2）。
/// </para>
/// </remarks>
internal static class RunHookPolicy
{
    /// <summary>运行中这条全局快捷键要不要接管（否 = 原样传给前台程序）。</summary>
    /// <remarks>
    /// 接管 ≠ 一定开跑：动作还要过准入（可能排队 / 被拒），但那个组合键一律吞掉并给提示，
    /// 否则 <c>CapsLock+O</c> 里的 <c>o</c> 会漏进目标程序。
    /// 独占档与放开并发之前一致：运行中连内置功能也不接管（全屏蔽）。
    /// </remarks>
    public static bool HandlesHotkey(ActionRunConfig run, int runningCount) =>
        runningCount <= 0 || run.Mode != ActionRunMode.exclusive;

    /// <summary>运行中轻按 CapsLock 要不要补 Esc（不补——那一下会打进目标程序）。</summary>
    public static bool CapsLockTapSendsEsc(int runningCount) => runningCount <= 0;

    /// <summary>鼠标手势（裸右键长按开面板 / 边缘滚轮音量）要不要让路。</summary>
    public static bool MouseGesturesEnabled(ActionRunConfig run, int runningCount) =>
        runningCount <= 0 || run.Mode != ActionRunMode.exclusive;

    /// <summary>「被拒 / 已排队」的提示节流：同一组合键 1 秒内只提示一次。</summary>
    public static bool ShouldNotifyBusy(long lastTick, long nowTick) =>
        lastTick == 0 || nowTick - lastTick >= BusyNotifyIntervalMs;

    /// <summary>节流窗口（毫秒）。</summary>
    public const int BusyNotifyIntervalMs = 1000;
}
