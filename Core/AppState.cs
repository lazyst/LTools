namespace LTools.Core;

/// <summary>
/// 全局应用状态（对应原版 lib/Globals.ahk 的全局变量）。
/// 2026-11 阶段 2 起：低级键鼠钩子回调跑在专用线程（<c>Hooks/HookDispatcher</c>），托盘 / 设置 UI
/// 在主 UI 线程，自动化流程在后台线程。这里的标量一律 <c>volatile</c>（状态机字段会被
/// HotkeyEditDialog / AutomationRunner 从别的线程读写）；集合 / 字典由各自的所有者线程独占。
/// </summary>
internal static class AppState
{
    // —— CapsLock 状态机（钩子线程写；设置页 HotkeyEditDialog 与自动化停止时会读）——
    /// <summary>CapsLock 是否处于按下态。</summary>
    public static volatile bool CapsLockIsDown;

    /// <summary>CapsLock 按下时刻（Environment.TickCount）。</summary>
    public static volatile int CapsLockPressTime;

    /// <summary>CapsLock 按住期间是否按了其他键（影响单击→Esc 判定）。</summary>
    public static volatile bool OtherKeyPressed;

    /// <summary>是否手动启用大写锁定（Ctrl+CapsLock 切换）。</summary>
    public static volatile bool CapsLockManuallyEnabled;

    // —— 功能开关 ——
    /// <summary>CapsLock 当快捷键修饰键（短按发 Esc）；关闭后 CapsLock 走原生大小写切换。</summary>
    public static volatile bool CapsLockModifier = true;

    /// <summary>鼠标停在屏幕边缘时滚轮调系统音量。</summary>
    public static volatile bool EdgeWheelVolume = true;

    /// <summary>
    /// 边缘滚轮音量算哪条边，取 <see cref="Core.WheelVolumeEdge"/> 的整数值。
    /// 存 int 而不是枚举字段：钩子回调（当前线程）要无锁读，枚举字段没法标 volatile。
    /// </summary>
    public static volatile int EdgeWheelVolumeEdge = (int)WheelVolumeEdge.bottom;

    /// <summary>超级面板是否启用（与上面的开关相互独立）。</summary>
    public static volatile bool IsSuperPanelEnabled = true;

    /// <summary>超级面板长按阈值（ms，可调 100–800）。裸右键按住超过此值弹面板（计划 §5.1，阶段 5）。</summary>
    public static volatile int SuperPanelThresholdMs = 250;

    /// <summary>左键双击托盘图标的行为。读配置落在这里，双击回调（UI 线程）零磁盘 IO；设置页改值经 <see cref="Settings.SetTrayDoubleClick"/> 同步。</summary>
    public static TrayDoubleClickAction TrayDoubleClick = TrayDoubleClickAction.settings;

    // —— 已吞键集合：快捷键 / CapsLock 组合键被吞后，其 keyup 也要吞，保持事件平衡 ——
    // HashSet<int>：钩子回调对每次 keyup 做 Contains/Remove，O(1) 且无 int 装箱；
    // 旧 ArrayList 为 O(n) 且每次装箱（虽集合通常很小，热路径零成本更好）。
    public static readonly HashSet<int> SwallowedVks = new();

    // 托盘气球提示已移至 TrayService.Notify（不再持有 WinForms NotifyIcon）

    /// <summary>CapsLock 是否当前按下（便捷查询，供功能模块判断）。</summary>
    public static bool IsCapsLockDown => CapsLockIsDown;
}
