using LTools.Features;
using LTools.Native;

namespace LTools.Core;

/// <summary>
/// CapsLock 状态机：轻按发 Esc、按住即修饰键（快捷键由 <see cref="HotkeyRegistry"/> 匹配）。
/// </summary>
/// <remarks>
/// <para>
/// - CapsLock keydown：记录按下态；启用时吞掉（灯恒灭），禁用时放行恢复原生大小写切换。
/// - CapsLock keyup：启用时吞掉；轻按（&lt;300ms 且期间没按别的键）发 Esc。
/// - Ctrl+CapsLock：手动切换大写锁定灯。
/// </para>
/// <para>
/// 旧的 CapsLock+Esc 启停、CapsLock+数字菜单、vim 文本编辑、帮助面板等内建手势已移除，
/// 需要的话在「快捷键」里绑成动作。
/// </para>
/// </remarks>
internal static class CapsLockStateMachine
{
    private const int ClickThresholdMs = 300; // 单击判定时长阈值

    /// <summary>
    /// 处理一次性键盘事件。
    /// </summary>
    /// <param name="vk">虚拟键码</param>
    /// <param name="isDown">按下</param>
    /// <param name="isUp">释放</param>
    /// <param name="swallow">是否吞掉（拦截不传系统）</param>
    /// <returns>true=已处理（swallow 决定拦截）；false=未处理</returns>
    public static bool TryHandle(ushort vk, bool isDown, bool isUp, out bool swallow)
    {
        swallow = false;

        // —— Ctrl+CapsLock → 手动切换大写锁定 ——
        if (vk == Win32.VkCapital && (Win32.GetAsyncKeyState(Win32.VkControl) & 0x8000) != 0)
        {
            if (isDown)
            {
                AppState.OtherKeyPressed = true;
                ToggleCapsLockLight();
                AppState.CapsLockManuallyEnabled = LightOn();
                ShowTooltip("大写锁定: " + (AppState.CapsLockManuallyEnabled ? "开启" : "关闭"));
                swallow = true; // 吞掉 Ctrl+CapsLock（灯由我们注入 toggle 控制）
            }
            return true;
        }

        // —— CapsLock keydown ——
        if (vk == Win32.VkCapital && isDown)
        {
            if (!AppState.CapsLockIsDown)
            {
                // 首次按下：记录状态
                AppState.CapsLockIsDown = true;
                AppState.CapsLockPressTime = Environment.TickCount;
                AppState.OtherKeyPressed = false;
            }

            if (AppState.IsToolEnabled)
            {
                // 启用态：吞掉 CapsLock（不传系统，灯不变），并确保灯灭
                if (!AppState.CapsLockManuallyEnabled && LightOn())
                {
                    InputHelper.Tap((ushort)Win32.VkCapital);
                }
                swallow = true;
            }
            // 禁用态：放行 CapsLock，恢复原生大小写切换
            return true;
        }

        // —— CapsLock keyup ——
        if (vk == Win32.VkCapital && isUp)
        {
            if (!AppState.CapsLockIsDown) return true; // 异常状态，忽略
            int duration = Environment.TickCount - AppState.CapsLockPressTime;
            AppState.CapsLockIsDown = false;

            if (!AppState.IsToolEnabled)
            {
                // 禁用态：放行 keyup，让系统完成原生 toggle
                if (duration < ClickThresholdMs && !AppState.OtherKeyPressed)
                {
                    AppState.OtherKeyPressed = true;
                    AppState.CapsLockManuallyEnabled = !LightOn(); // 放行后系统会 toggle，预测
                }
                AppState.OtherKeyPressed = false;
                return true;
            }

            // 启用态 keyup：吞掉
            swallow = true;
            if (!AppState.CapsLockManuallyEnabled && LightOn())
            {
                InputHelper.Tap((ushort)Win32.VkCapital);
            }

            if (!AppState.OtherKeyPressed && duration < ClickThresholdMs)
            {
                // 单击 → 发送 Esc（按住 CapsLock 再按别的键时 OtherKeyPressed 已置位）
                InputHelper.Tap((ushort)Win32.VkEscape);
            }

            AppState.OtherKeyPressed = false;
            return true;
        }

        return false; // 未处理
    }

    private static void ToggleCapsLockLight() => InputHelper.Tap((ushort)Win32.VkCapital);

    /// <summary>启动时确保 CapsLock 灯灭（对应 AHK InitializeApp 里的 SetCapsLockState AlwaysOff）。</summary>
    public static void EnsureLightOff()
    {
        if (LightOn()) InputHelper.Tap((ushort)Win32.VkCapital);
    }

    /// <summary>看门狗 tick（对应 AHK CheckCapsLockState 的 1s 定时）：启用态且非手动且灯亮 → 灭灯。</summary>
    public static void WatchdogTick()
    {
        if (!AppState.IsToolEnabled) return;
        if (AppState.CapsLockManuallyEnabled) return;
        if (LightOn()) InputHelper.Tap((ushort)Win32.VkCapital);
    }

    /// <summary>CapsLock 灯是否点亮（GetKeyState 低位）。</summary>
    private static bool LightOn() => (Win32.GetKeyState(Win32.VkCapital) & 1) != 0;

    private static void ShowTooltip(string message) => TrayService.Notify(message);
}
