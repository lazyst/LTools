using System.Collections;

namespace CapsLockPro.Core;

/// <summary>
/// 全局应用状态（对应原版 lib/Globals.ahk 的全局变量）。
/// 大多数字段只在主线程（低级钩子回调 + 托盘 UI）访问；
/// 独立剪贴板字段会被工作线程访问，已用 <see cref="volatile"/> 或独立锁保护。
/// </summary>
internal static class AppState
{
    // —— CapsLock 状态机（主线程访问）——
    /// <summary>CapsLock 是否处于按下态。</summary>
    public static bool CapsLockIsDown;

    /// <summary>CapsLock 按下时刻（Environment.TickCount）。</summary>
    public static int CapsLockPressTime;

    /// <summary>CapsLock 按住期间是否按了其他键（影响单击→Esc 判定）。</summary>
    public static bool OtherKeyPressed;

    /// <summary>是否手动启用大写锁定（Ctrl+CapsLock 切换）。</summary>
    public static bool CapsLockManuallyEnabled;

    /// <summary>CapsLock 按住期间是否按了 Esc（禁用/重新启用手势）。</summary>
    public static bool CapsLockEscPressed;

    // —— 工具总开关 ——
    /// <summary>CapsLock 键功能是否启用（CapsLock+Esc 手势切换；也可在设置「通用」页 / 托盘菜单切换）。</summary>
    public static volatile bool IsToolEnabled = true;

    /// <summary>超级面板是否启用（与 <see cref="IsToolEnabled"/> 相互独立）。</summary>
    public static volatile bool IsSuperPanelEnabled = true;

    /// <summary>超级面板长按阈值（ms，可调 100–800）。裸右键按住超过此值弹面板（计划 §5.1，阶段 5）。</summary>
    public static volatile int SuperPanelThresholdMs = 250;

    /// <summary>调试提示开关（Ctrl+Alt+I）。</summary>
    public static bool ShowDebugTooltips;

    // —— 已吞键集合：CapsLock+组合键被吞后，其 keyup 也要吞，保持事件平衡 ——
    public static readonly ArrayList SwallowedVks = new();

    // —— 独立剪贴板 ——
    /// <summary>独立剪贴板私有内容（CapsLock+X/C 存入，CapsLock+V 粘贴）。</summary>
    private static string? _clipboardIndependent;
    private static readonly object _clipboardLock = new();

    public static string? GetIndependentClipboard()
    {
        lock (_clipboardLock) return _clipboardIndependent;
    }

    public static void SetIndependentClipboard(string? value)
    {
        lock (_clipboardLock) { _clipboardIndependent = value; }
    }

    // —— 鼠标模式状态（阶段2）——
    public static bool MouseModeActive;
    public static int MouseModeSpeed = 5;

    // —— 符号跳转状态（阶段3）——
    /// <summary>是否正在执行符号跳转搜索（对应原版 isSeekingSymbol）。</summary>
    public static volatile bool IsSeekingSymbol;

    // 托盘气球提示已移至 TrayService.Notify（不再持有 WinForms NotifyIcon）

    /// <summary>CapsLock 是否当前按下（便捷查询，供功能模块判断）。</summary>
    public static bool IsCapsLockDown => CapsLockIsDown;
}
