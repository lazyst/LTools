using LTools.Features.CapturePro;

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

    /// <summary>「延时截图」先等多少秒（1–10，默认 3）。启动时从 <c>settings.json</c> 灌进来，设置页改值经 <see cref="Settings.SetScreenshotDelay"/> 同步。</summary>
    public static volatile int ScreenshotDelaySeconds = 3;

    /// <summary>
    /// 录屏的默认开关（声音 / 摄像头 / 特效 / 编码器 / 录完裁剪）。
    /// 启动时从 <c>settings.json</c> 灌进来，「录屏选项」窗口保存时换一个新实例（别就地改，读方可能正在用）。
    /// </summary>
    public static RecordOptions Record = new();

    // —— 已吞键集合：快捷键 / CapsLock 组合键被吞后，其 keyup 也要吞，保持事件平衡 ——
    // HashSet<int>：钩子回调对每次 keyup 做 Contains/Remove，O(1) 且无 int 装箱；
    // 旧 ArrayList 为 O(n) 且每次装箱（虽集合通常很小，热路径零成本更好）。
    public static readonly HashSet<int> SwallowedVks = new();

    // 托盘气球提示已移至 TrayService.Notify（不再持有 WinForms NotifyIcon）

    /// <summary>
    /// 把配置里「运行期缓存」的那几项灌进内存（两个进程都要走这一处）。
    /// </summary>
    /// <remarks>
    /// 提权进程在 <c>App.OnStartup</c> 调一次；普通权限宿主在只读加载配置时调一次、宿主自己写完
    /// <c>configs/settings.json</c> 后再调一次——截图 / 录屏搬进宿主之后，那些窗口也读这些值
    /// （延时秒数、录屏默认开关、导出场景），两边各写一份就会走岔。
    /// </remarks>
    public static void ApplySettings(Config.AppConfig cfg)
    {
        ApplySettings(Config.ConfigBundle.ToSettings(cfg));
        // 超级面板的两个开关在 panel.json 里（不在 settings 域），单独同步
        ApplySuperPanel(cfg.SuperPanel);
    }

    /// <summary>
    /// 只灌 <c>settings.json</c> 那一份（宿主的「写完就刷」用这个重载）。
    /// </summary>
    /// <remarks>
    /// 设置页改一个开关就要刷一次，而整份 <see cref="Config.AppConfig"/> 会把 <c>actions/</c> 也解析一遍
    /// （几百个动作时是几十毫秒，压在 UI 线程上）。这一份管的是「全局行为 + 截图 / 录屏默认值」；
    /// 超级面板那两个开关在 <c>panel.json</c> 里，走 <see cref="ApplySuperPanel"/>。
    /// </remarks>
    public static void ApplySettings(Config.SettingsFile settings)
    {
        CapsLockModifier = settings.CapsLockModifier;
        EdgeWheelVolume = settings.EdgeWheelVolume;
        EdgeWheelVolumeEdge = (int)settings.EdgeWheelVolumeEdge;
        TrayDoubleClick = settings.TrayDoubleClick;
        ScreenshotDelaySeconds = Features.CapturePro.CaptureDelay.Normalize(
            (settings.Screenshot ?? new Config.ScreenshotFile()).DelaySeconds);
        Features.CapturePro.ExportSettings.Apply(settings.Export);
        Record = (settings.Record ?? new RecordOptions()).Normalized();
        // 终端路径覆盖（设置 → 终端路径）：执行器要按它起 shell，宿主里那页也要显示当前值。
        Features.TerminalLauncher.LoadFromConfig(settings);
    }

    /// <summary>
    /// 只同步「超级面板」那两个开关（<c>panel.json</c> 里不是结构的那部分）。
    /// </summary>
    /// <remarks>
    /// 提权进程从托盘改了它（<c>PanelStore.Modify</c> → <c>data.write</c>）之后，宿主只刷这两项：
    /// **面板结构以宿主内存为准**，整份 <c>PanelLibrary</c> 重新读会把正在拖动 / 编辑的磁贴打回去。
    /// </remarks>
    public static void ApplySuperPanel(Config.SuperPanelConfig superPanel)
    {
        IsSuperPanelEnabled = superPanel.Enabled;
        SuperPanelThresholdMs = superPanel.LongPressThresholdMs;
    }

    /// <summary>CapsLock 是否当前按下（便捷查询，供功能模块判断）。</summary>
    public static bool IsCapsLockDown => CapsLockIsDown;
}
