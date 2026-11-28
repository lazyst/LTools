using LTools.Features.Running;

namespace LTools.Features;

/// <summary>
/// 停止热键的唯一路由：动作运行与宏回放共用同一个组合键，吞键配对状态**只有一份**。
/// </summary>
/// <remarks>
/// <para>
/// 以前键盘钩子里是三元表达式（有动作运行时问动作、否则问宏），两边各存一个「已吞 keydown」
/// 标志：并发之后「宏在动作步骤里回放」两边同时为真，keyup 会漏吞、把半截组合键漏给目标程序。
/// </para>
/// <para>
/// 动作优先：只要有任何动作在跑就停动作（<c>AutomationRunner.Stop</c> 本身也会停宏回放）；
/// 只有宏在回放时才单独停宏。见 <c>docs/RUN_INTEROP.md</c> §6.6。
/// </para>
/// </remarks>
internal static class StopKeyRouter
{
    private static volatile bool _swallowed;

    /// <summary>钩子里的兜底出口（系统热键注册被别的程序占用时仍能停）。</summary>
    public static bool HandleKey(ushort vk, bool isDown, bool isUp)
    {
        if (vk != MacroSettings.PlaybackStopVk) return false;

        if (isUp)
        {
            if (!_swallowed) return false;
            _swallowed = false;
            return true;
        }

        bool running = RunningActions.IsAny;
        if (!isDown || (!running && !MacroPlayer.IsPlaying)) return false;
        if (!MacroSettings.AreModifiersDown(MacroSettings.PlaybackStopModifiers)) return false;

        _swallowed = true;
        if (running) AutomationRunner.Stop();
        else MacroPlayer.Stop();
        return true;
    }
}
