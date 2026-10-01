using LTools.Features;
using LTools.Native;

namespace LTools.Core;

/// <summary>
/// CapsLock 状态机（对应原版 lib/CapsLockHandler.ahk）。
/// 行为：
/// - CapsLock keydown：记录状态；启用态吞掉（不传系统，灯恒灭）；禁用态放行恢复原生。
/// - CapsLock keyup：按时长/其他键/Esc 决定——单击(&lt;300ms 无其他键 无Esc)→Esc；
///   CapsLock+Esc→禁用；禁用态 CapsLock+Esc 长按(≥300ms)→重新启用；
///   禁用态单击(无Esc &lt;300ms)→手动切换大写锁定。
/// - Ctrl+CapsLock：手动切换大写锁定灯（注入 VK_CAPITAL toggle）。
/// - Ctrl+Alt+I：切换调试提示。
/// - Esc keydown（CapsLock 按住期间）：记录 capsLockEscPressed（放行 Esc）。
/// </summary>
internal static class CapsLockStateMachine
{
    private const int ClickThresholdMs = 300; // 单击判定时长阈值

    /// <summary>菜单打开期间记录一次新 CapsLock 按下，用于 keyup 时直接关闭菜单。</summary>
    private static bool _menuClosePending;

    /// <summary>
    /// 处理一次性键盘事件。
    /// </summary>
    /// <param name="vk">虚拟键码</param>
    /// <param name="isDown">按下</param>
    /// <param name="isUp">释放</param>
    /// <param name="swallow">是否吞掉（拦截不传系统）</param>
    /// <returns>true=已处理（swallow 决定拦截）；false=未处理，交功能模块</returns>
    public static bool TryHandle(ushort vk, bool isDown, bool isUp, out bool swallow)
    {
        swallow = false;

        // —— Ctrl+Alt+I → 调试提示切换 ——
        if (isDown && vk == 'I' && (Win32.GetAsyncKeyState(Win32.VkControl) & 0x8000) != 0
            && (Win32.GetAsyncKeyState(Win32.VkMenu) & 0x8000) != 0)
        {
            AppState.ShowDebugTooltips = !AppState.ShowDebugTooltips;
            ShowTooltip(AppState.ShowDebugTooltips ? "调试提示已开启" : "调试提示已关闭");
            swallow = true;
            return true;
        }

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
                AppState.CapsLockEscPressed = (Win32.GetAsyncKeyState(Win32.VkEscape) & 0x8000) != 0;
                // 菜单打开期间的新一次按下：标记 keyup 时直接关闭菜单（不依赖 300ms 阈值）
                _menuClosePending = MenuSystem.IsMenuOpen;
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
                // —— 禁用态 keyup ——
                if (AppState.CapsLockEscPressed && duration >= ClickThresholdMs)
                {
                    // CapsLock+Esc 长按 → 重新启用（落盘，重启不恢复旧状态）
                    Settings.SetCapsLockEnabled(true);
                    swallow = true; // 吞掉 keyup（禁用手势已处理）
                }
                else if (!AppState.CapsLockEscPressed && duration < ClickThresholdMs)
                {
                    // 禁用态单击 → 手动切换大写锁定（放行让系统 toggle，但同步状态）
                    AppState.OtherKeyPressed = true;
                    // 灯由系统 toggle；同步 ManuallyEnabled
                    AppState.CapsLockManuallyEnabled = !LightOn(); // 放行后系统会 toggle，预测
                    swallow = false; // 放行 keyup
                }
                else
                {
                    swallow = false; // 放行
                }
                ResetEscOther();
                return true;
            }

            // —— 启用态 keyup：吞掉 ——
            swallow = true;
            if (!AppState.CapsLockManuallyEnabled && LightOn())
            {
                InputHelper.Tap((ushort)Win32.VkCapital);
            }

            if (AppState.CapsLockEscPressed && !AppState.OtherKeyPressed)
            {
                // CapsLock+Esc → 禁用（立即生效并落盘，重启不恢复）
                Settings.SetCapsLockEnabled(false);
                ResetEscOther();
                return true;
            }

            // 菜单打开期间的一次 CapsLock 单击（无其他键、无 Esc）→ 直接关闭菜单
            if (_menuClosePending && !AppState.OtherKeyPressed)
            {
                _menuClosePending = false;
                MenuSystem.CloseCurrent();
                ResetEscOther();
                return true;
            }

            if (!AppState.OtherKeyPressed && duration < ClickThresholdMs && !AppState.CapsLockEscPressed)
            {
                // 单击 → 发送 Esc
                InputHelper.Tap((ushort)Win32.VkEscape);
            }

            ResetEscOther();
            return true;
        }

        // —— Escape keydown（CapsLock 按住期间）：记录 escPressed，放行 Esc ——
        if (vk == Win32.VkEscape && isDown && AppState.CapsLockIsDown)
        {
            AppState.CapsLockEscPressed = true;
            // 放行 Esc（~Escape，让系统正常处理）
            swallow = false;
            return true;
        }

        return false; // 未处理，交功能模块分发
    }

    /// <summary>启动时确保 CapsLock 灯灭（对应 AHK InitializeApp 的 SetCapsLockState AlwaysOff）。</summary>
    public static void EnsureLightOff()
    {
        if (LightOn()) InputHelper.Tap((ushort)Win32.VkCapital);
    }

    /// <summary>看门狗 tick（对应 AHK CheckCapsLockState，2s 定时）：启用态且非手动且灯亮→灭灯。</summary>
    public static void WatchdogTick()
    {
        if (!AppState.IsToolEnabled) return;
        if (AppState.CapsLockManuallyEnabled) return;
        if (LightOn()) InputHelper.Tap((ushort)Win32.VkCapital);
    }

    /// <summary>CapsLock 灯是否点亮（GetKeyState 低位）。</summary>
    private static bool LightOn() => (Win32.GetKeyState(Win32.VkCapital) & 1) != 0;

    /// <summary>注入一次 VK_CAPITAL，让系统 toggle 灯状态。</summary>
    private static void ToggleCapsLockLight() => InputHelper.Tap((ushort)Win32.VkCapital);

    private static void ResetEscOther()
    {
        AppState.CapsLockEscPressed = false;
        AppState.OtherKeyPressed = false;
    }

    /// <summary>在鼠标附近显示提示（阶段1 用托盘气球，后续可改原生 Tooltip）。</summary>
    private static void ShowTooltip(string msg)
    {
        TrayService.Notify(msg);
    }
}
