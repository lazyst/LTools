namespace LTools.Core;

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

    // —— 工具总开关 ——
    /// <summary>CapsLock 键功能是否启用（设置「通用」页 / 托盘菜单切换；关闭后 CapsLock 恢复原生大小写）。</summary>
    public static volatile bool IsToolEnabled = true;

    /// <summary>超级面板是否启用（与 <see cref="IsToolEnabled"/> 相互独立）。</summary>
    public static volatile bool IsSuperPanelEnabled = true;

    /// <summary>超级面板长按阈值（ms，可调 100–800）。裸右键按住超过此值弹面板（计划 §5.1，阶段 5）。</summary>
    public static volatile int SuperPanelThresholdMs = 250;

    // —— 已吞键集合：快捷键 / CapsLock 组合键被吞后，其 keyup 也要吞，保持事件平衡 ——
    // HashSet<int>：钩子回调对每次 keyup 做 Contains/Remove，O(1) 且无 int 装箱；
    // 旧 ArrayList 为 O(n) 且每次装箱（虽集合通常很小，热路径零成本更好）。
    public static readonly HashSet<int> SwallowedVks = new();

    // 托盘气球提示已移至 TrayService.Notify（不再持有 WinForms NotifyIcon）

    /// <summary>CapsLock 是否当前按下（便捷查询，供功能模块判断）。</summary>
    public static bool IsCapsLockDown => CapsLockIsDown;
}
