using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Hooks;
using H.NotifyIcon;

namespace LTools;

/// <summary>
/// WPF 应用入口（替代旧 WinForms <c>TrayAppContext</c>）。
/// OnStartup：单实例互斥体 → H.NotifyIcon 托盘 → 装/卸钩子 → 初始化菜单/速记/设置 → 看门狗。
/// 主 UI STA 线程由 WPF Dispatcher 泵送，只负责托盘与所有 WPF 窗口。
/// 2026-11 阶段 2 起：低级键盘 / 鼠标钩子装在另一条专用 STA 线程（<see cref="Hooks.HookDispatcher"/>），
/// 回调在那里同步派发、只做纯数据判定与吞 / 放；WPF 副作用经 <see cref="Ui.Post"/> 排回本线程。
/// 这样设置窗口首帧渲染 / 切页这类同步重活不再把输入消息顶在队列里（见 cache/perf 基线）。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\LTools_SingleInstance";
    private static Mutex? _singleInstanceMutex;

    private TaskbarIcon? _tray;
    private DispatcherTimer? _watchdog;
    private MenuItem? _macroMenuItem;      // 托盘「开始 / 停止录制宏」：标题随录制状态变化
    private TextBlock? _macroMenuItemText;
    private TextBlock? _triggerMenuItemText;   // 托盘「暂停 / 恢复自动触发」：标题随紧急禁用状态变化

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 主 UI Dispatcher 先登记：钩子专用线程上的一切 WPF 副作用都靠它投递（见 Core/Ui.cs）。
        Ui.Initialize(Dispatcher);

        // —— stdio 桥（LTools.exe --mcp-stdio）：只转发 JSON-RPC，不建托盘 / 钩子 / 单实例互斥体 ——
        if (McpStdioBridge.IsRequested(e.Args))
        {
            Shutdown(McpStdioBridge.Run());
            return;
        }

        // —— UI 线程未捕获异常兑底：记录到崩溃日志并吞掉（常驻输入工具不因单次异常退出）——
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        // —— 单实例 ——
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // 常见误用：已经有一个 LTools 在跑（比如改了新版本直接双击），单实例互斥体会让
            // 本次启动静默退出——用户会以为在测新版本，其实测的是旧进程。必须提示。
            MessageBox.Show(
                "LTools 已经在运行，本次启动已退出。\n\n" +
                "如果要换用新构建：请先从托盘菜单「退出 LTools」，再重新启动。",
                "LTools", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // —— 托盘 ——
        BuildTray();

        // 全局下拉框行为：打开时不自动下滚、关闭状态下滚轮转交给页面（见 ComboBoxAssist）
        Views.Controls.ComboBoxAssist.Register();

        // 窗口圆角：注册一次，之后所有非分层窗口由 DWM 画小圆角（Win10 上自动不生效）
        Views.WindowCorners.Attach();

        // 统一窗口尺寸 / 位置约束：注册一次，按窗口分类（Dialog / Main / Overlay）
        // 动态夹回所在显示器工作区，切换内容 / 改缩放后都不会被任务栏挡住
        Views.WindowSizing.Attach();

        // 启动确保 CapsLock 灯灭
        CapsLockStateMachine.EnsureLightOff();

        // —— 看门狗（对应 AHK CheckCapsLockState，2s）——
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watchdog.Tick += (_, _) => CapsLockStateMachine.WatchdogTick();
        _watchdog.Start();

        // —— 加载配置 + 初始化各模块（重活，须在装钩子之前完成）——
        // 低级鼠标/键盘钩子靠本线程泵消息才会被调用；OnStartup 在 Dispatcher 开始泵消息之前同步执行，
        // 若先装钩子再做下面的重活（JSON 解析 / 动作注册 / 速记文件迁移 / 弹提示窗），主线程不泵消息 →
        // 鼠标事件挂起 → 启动瞬间鼠标卡顿。故重活前置、钩子最后装：装完即返回，Dispatcher 立即泵消息，
        // 钩子回调第一时间被服务。顺带也保证 AppState / 动作清单在钩子首次回调前已就绪。
        var cfgPath = ConfigLocator.FindPath();

        // 旧版「宏动作」（Type = "macro"）已经没有对应类型：连同宏文件一起移进 trash/，
        // 必须在读配置之前做——否则 ActionStore 会把它当成空动作读进来再写回去。
        MacroStore.RetireLegacyMacroActions(cfgPath);

        var cfg = AppConfig.Load(cfgPath);

        // 运行日志：启动即按配置开启，后续设置页改动会即时生效。放在重活最前面，
        // 让启动阶段的失败也能留下记录（CrashLog 会镜像到这里）。
        var logCfg = cfg.Logging ?? new LogConfig();
        AppLog.Configure(logCfg.Enabled, logCfg.Level, logCfg.RetentionDays);
        AppLog.Info("App", $"===== LTools {typeof(App).Assembly.GetName().Version} 启动 =====");
        AppLog.Info("App",
            $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} · " +
            $"管理员={Environment.IsPrivilegedProcess} · 进程 {Environment.ProcessId}");
        AppLog.Info("App", $"数据目录 {AppPaths.DataRoot}");
        AppLog.Info("App", $"配置文件 {LTools.Config.ConfigBundle.ConfigDir(cfgPath)}（锚点 {Path.GetFileName(cfgPath)}）");

        // 配置读坏时每次启动都会留一份 .bad- 副本，只保留最近 3 份，避免反复失败时越积越多。
        AppConfig.PruneBrokenBackups(cfgPath);

        // 自启任务对齐：便携版把目录挪走、或另一份拷贝抢过任务名后，任务指向的
        // exe 就不是当前程序了，登录时启动的不是这一份（甚至启动不了）。
        // 同一个任务名只服务一份程序，启动时统一改写为当前 exe。
        // 放线程池执行——COM 激活与 schtasks 都是外部调用，不占启动阶段的主线程。
        _ = Task.Run(AutoStartService.HealIfStale);

        if (cfg.LoadFailed)
        {
            AppLog.Error("App",
                $"配置文件读取失败，已备份为 .bad-* 并回退默认；本次不会覆盖原文件");
            TrayService.Notify("配置文件读取失败：原文件已保留，本次使用默认配置（详见运行日志）");
        }

        AppState.CapsLockModifier = cfg.CapsLockModifier;
        AppState.EdgeWheelVolume = cfg.EdgeWheelVolume;
        AppState.EdgeWheelVolumeEdge = (int)cfg.EdgeWheelVolumeEdge;
        AppState.IsSuperPanelEnabled = cfg.SuperPanel.Enabled;
        AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;
        AppState.TrayDoubleClick = cfg.TrayDoubleClick;
        ConfigStore.Initialize(cfgPath);
        FloatingLibrary.Initialize(cfgPath);
        QuickNote.Initialize(cfgPath);
        ConfigHelper.Initialize(cfgPath);
        ResourceDir.Ensure();
        // 找图模板的两个子目录：images（程序生成、可回收）/ user（用户素材、永不回收）
        ImageLibrary.EnsureDir();
        MacroSettings.Load(cfg);
        MacroStore.Ensure();
        // 录制即生成动作：录完的宏自动挂成一个「回放宏」动作并放进面板（见 MacroAction）。
        MacroRecorder.MacroSaved += OnMacroSaved;

        // 停止录制的兜底热键：消息窗口必须在钩子安装前建好（注册时不能再建窗口）。
        MacroHotkey.Initialize();

        // —— 内部动作注册（动作系统 §7）——

        // 一宏一动作：回收不再被任何动作步骤引用的宏文件（含旧版本遗留的孤儿文件）。
        MacroStore.CollectGarbage();

        // 模板截图同理：删动作 / 换模板后旧 shot-*.png 没有别处回收，启动时按引用清一次。
        ImageLibrary.CollectGarbage();
        // 运行期截图只在上一轮运行里有意义：启动时整体清空 cache/screenshots（外部程序也不该跨重启依赖它）。
        ImageLibrary.ClearCaptures();

        // AI 接入：知识包随单元清单变化重建（后台线程，别拖住启动）；MCP 服务默认关闭，
        // 只有用户在设置里打开才会在后台起 HTTP 服务（启动失败只记配置里的 LastError）。
        _ = Task.Run(KnowledgePack.Ensure);
        _ = Task.Run(McpServer.Apply);

        // —— 启动提示：屏幕右下角的 toast（抽屉式从右侧滑入、点一下就关），不再跟着鼠标弹小字。
        //    仍在无钩子阶段同步显示：首个 WPF 窗口的首帧会初始化渲染栈（D3D 等），
        //    耗时落在此处，不阻塞任何钩子回调。 ——
        ToastService.Show("LTools 已启动", null, 2500, ToastKind.success);

        // —— 悬浮项无状态：启动不恢复任何按钮（等用户从面板悬浮），只把偏好准备好 ——
        FloatingManager.Initialize();

        // —— 钩子装到专用 STA 线程：HookDispatcher 自己预热 JIT、安装两个钩子并泵消息 ——
        // 主 UI 线程从此只负责 WPF，开窗首帧 / 切页再重也不会把输入消息顶在队列里。
        // 卸载同样由该线程在泵收尾时做（见 ExitApplication → HookDispatcher.Stop）。
        HookDispatcher.Start();

        // 快捷键白 / 黑名单：跟踪前台窗口变化，进程解析放在后台线程（钩子回调里只做字符串比较）。
        ForegroundAppCache.Start();

        // 自动触发：配置 / 动作库 / 前台缓存都就绪后再启动（Start 内部重新读盘拿修过引用的规则）。
        // 总开关默认关闭：即使有规则也不会投递，直到用户在「自动触发」页主动打开。
        // 只有开机自启任务拉起时才带 --autostart（「仅开机自启时」规则的判定依据）。
        bool launchedByAutoStart = Array.Exists(e.Args,
            a => string.Equals(a, AutoStartService.AutoStartArgument, StringComparison.OrdinalIgnoreCase));
        TriggerService.Start(cfgPath, launchedByAutoStart);

        // 诊断模式：钩子与各模块就绪后，自动跑「开窗 / 切页」采样，导出结果即退出。
        // 默认关闭；产品路径上只多一次 e.Args 扫描。
        if (PerfRunner.IsRequested(e.Args)) PerfRunner.Start();
    }

    private void BuildTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewItem("速记 (CapsLock+N)", () => QuickNote.Toggle()));
        menu.Items.Add(NewItem("动作面板 (CapsLock+T)", () => Features.SuperPanel.Toggle()));
        menu.Items.Add(NewItem("设置 (CapsLock+\\)", () => ConfigHelper.Toggle()));
        menu.Items.Add(new Separator());
        _macroMenuItem = NewItem("开始 / 停止录制宏", () => MacroRecorder.Toggle());
        _macroMenuItemText = (TextBlock)((StackPanel)_macroMenuItem.Header).Children[1];
        menu.Items.Add(_macroMenuItem);
        menu.Items.Add(NewItem("键鼠宏…", ConfigHelper.OpenMacros));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("启用 CapsLock 修饰键", Settings.ToggleCapsLockModifier, () => AppState.CapsLockModifier));
        menu.Items.Add(NewItem("启用超级面板", Settings.ToggleSuperPanel, () => AppState.IsSuperPanelEnabled));
        // 和上面两个开关项同一套状态点列：运行中绿点、紧急禁用橙点（不再用暂停 / 播放字符图标，
        // 那个字形在 Segoe UI 下会渲染成带方框的 emoji，和灰阶菜单、绿点列都不搭）。
        var triggerMenuItem = NewItem("暂停自动触发（紧急禁用）", ToggleTriggerEmergency,
            () => !TriggerService.EmergencyDisabled, PausedDotBrush);
        _triggerMenuItemText = (TextBlock)((StackPanel)triggerMenuItem.Header).Children[1];
        menu.Items.Add(triggerMenuItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("关闭所有悬浮项", FloatingManager.CloseAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("退出 LTools", ExitApplication));

        // 状态点每次打开菜单时刷新：状态可能经设置页或托盘切换，托盘菜单要跟随
        menu.Opened += (_, _) => { RefreshStatusDots(); RefreshMacroMenuItem(); RefreshTriggerMenuItem(); };

        _tray = new TaskbarIcon
        {
            ToolTipText = "LTools",
            IconSource = LoadIconSource(),
            ContextMenu = menu,
        };
        // 左键双击托盘图标：按设置 →「通用」的行为打开设置窗口（停上次页 / 跳指定页）。
        // 右键单击仍是上面的 ContextMenu，两者互不干扰；回调只读 AppState.TrayDoubleClick（零磁盘 IO）。
        _tray.TrayLeftMouseDoubleClick += (_, _) => ConfigHelper.OpenOnTrayDoubleClick();
        // ⚠️ 必须显式传 false：ForceCreate 的 enablesEfficiencyMode 默认 true，
        // 会把整个进程设为 IDLE 优先级 + EcoQoS（降频/调度到能效核）。
        // 本应用是全局低级键鼠钩子工具，实时性就是生命线——进程被降级后，长时间空闲再右键托盘图标时，
        // 钩子回调/注入/菜单渲染全在 IDLE 优先级 + 降频 CPU 上跑，消息泵被拖慢 → 鼠标可见卡顿。
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// 托盘菜单项：标题左侧统一留出状态点列（做法同系统菜单的勾选列，保证各行文字对齐）。
    /// <paramref name="isOn"/> 非空即开关项：启用 → 该列显示绿色状态点（SuccessColor #16A34A），
    /// 禁用 → 有 <paramref name="offBrush"/> 就显示它（如紧急禁用的橙点），否则透明但不收列，
    /// 文字不因开关状态左右跳动。
    /// 注意：本项目 MenuItem 模板只呈现 Header 不呈现 Icon，故状态点必须放进 Header。
    /// </summary>
    private MenuItem NewItem(string header, Action onClick, Func<bool>? isOn = null,
        System.Windows.Media.Brush? offBrush = null)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = StatusDotSize,
            Height = StatusDotSize,
            Margin = new Thickness(0, 0, StatusDotGap, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = System.Windows.Media.Brushes.Transparent,
        };
        var headerRow = new StackPanel { Orientation = Orientation.Horizontal };
        headerRow.Children.Add(dot);
        headerRow.Children.Add(new TextBlock
        {
            Text = header,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var mi = new MenuItem { Header = headerRow };
        mi.Click += (_, _) =>
        {
            onClick();
            RefreshStatusDots();
        };
        if (isOn != null)
            _statusDots.Add((dot, isOn, offBrush));
        return mi;
    }

    /// <summary>按各开关的当前状态刷新状态点（打开菜单时 + 点击菜单项后调用）。</summary>
    private void RefreshStatusDots()
    {
        foreach (var (dot, isOn, off) in _statusDots)
            dot.Fill = isOn() ? EnabledDotBrush : (off ?? System.Windows.Media.Brushes.Transparent);
    }

    /// <summary>
    /// 托盘宏菜单标题跟随录制状态：录制中显示「■ 停止录制宏（热键）」，让托盘始终是一个可用的停止入口。
    /// </summary>
    private void RefreshMacroMenuItem()
    {
        if (_macroMenuItemText == null) return;
        _macroMenuItemText.Text = MacroRecorder.IsRecording
            ? $"■ 停止录制宏（{MacroSettings.RecordHotkeyDisplay}）"
            : $"● 开始录制宏（{MacroSettings.RecordHotkeyDisplay}）";
    }

    /// <summary>托盘「暂停自动触发」：切换紧急禁用空文件（数据根下），标题随状态变化。</summary>
    private void ToggleTriggerEmergency()
    {
        TriggerService.SetEmergencyDisabled(!TriggerService.EmergencyDisabled);
        RefreshTriggerMenuItem();
    }

    private void RefreshTriggerMenuItem()
    {
        if (_triggerMenuItemText == null) return;
        // 状态由左侧圆点表达（绿 = 运行中，橙 = 已暂停），文字只说「点了会做什么」。
        _triggerMenuItemText.Text = TriggerService.EmergencyDisabled
            ? "恢复自动触发"
            : "暂停自动触发（紧急禁用）";
    }

    private const double StatusDotSize = 9;
    private const double StatusDotGap = 7;
    private readonly List<(System.Windows.Shapes.Ellipse Dot, Func<bool> IsOn, System.Windows.Media.Brush? OffBrush)> _statusDots = new();
    private static readonly System.Windows.Media.Brush EnabledDotBrush = CreateEnabledDotBrush();
    private static readonly System.Windows.Media.Brush PausedDotBrush = CreatePausedDotBrush();

    private static System.Windows.Media.Brush CreateEnabledDotBrush()
    {
        // 与 Themes/Modern.xaml 的 SuccessColor (#16A34A) 保持一致
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
        brush.Freeze();
        return brush;
    }

    private static System.Windows.Media.Brush CreatePausedDotBrush()
    {
        // 紧急禁用是「暂停」不是「出错」，用橙而不是 DangerColor 红
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xEA, 0x58, 0x0C));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 宏保存完成（后台线程）：把它挂成动作（新建或覆盖同名宏动作），并放进面板
    /// （见 <see cref="MacroAction.Attach"/> / <see cref="MacroAction.PlaceOnPanel"/>）。
    /// <c>ConfigStore</c> / <c>ActionRegistry</c> 非线程安全，必须回到 UI 线程。
    /// </summary>
    private static void OnMacroSaved(string id)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                string name = MacroStore.MetaOf(id)?.Name ?? ("宏 " + id);
                string? actionId = MacroAction.Attach(id, name);
                if (actionId == null) return;

                string? group = MacroAction.PlaceOnPanel(actionId);
                string where = group == null ? "已在面板里" : $"已放进面板「{group}」";
                ToastService.Show($"{name}\n{where} · 右键磁贴可试放 / 重新录制",
                    "键鼠宏已录好", 6000, ToastKind.success, key: "macro:saved",
                    onClick: ConfigHelper.OpenMacros, actionText: "打开键鼠宏设置");
            }
            catch (Exception ex)
            {
                CrashLog.Write("App.OnMacroSaved", ex);
                ToastService.Show("宏已保存，但没能挂成动作：" + ex.Message, "键鼠宏",
                    ToastService.StickyMs, ToastKind.error, key: "macro:saved");
            }
        });
    }

    internal static System.Windows.Media.ImageSource? LoadIconSource()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Icon", "LTools.ico"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Icon", "LTools.ico"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Icon", "LTools.ico"),
        };
        foreach (var p in candidates)
        {
            try
            {
                if (File.Exists(p))
                    return new System.Windows.Media.Imaging.BitmapImage(new Uri(p));
            }
            catch { /* 尝试下一个 */ }
        }
        return null;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashLog.Write("Dispatcher", e.Exception);
        e.Handled = true;
    }

    /// <summary>内置功能「退出 LTools」用：从任意线程安全地走一遍正常退出流程。</summary>
    internal static void RequestExit()
    {
        if (Application.Current is not App app) return;
        if (app.Dispatcher.CheckAccess()) app.ExitApplication();
        else app.Dispatcher.Invoke(app.ExitApplication);
    }

    private void ExitApplication()
    {
        AppLog.Info("App", "退出 LTools");
        _watchdog?.Stop();
        // 只管「别再收请求」并把关停排到后台：绝不在 UI 线程上等 ASP.NET Core 关停（会永久卡死托盘）。
        McpServer.Stop();
        FloatingManager.Shutdown();
        ForegroundAppCache.Stop();
        TriggerService.Stop();
        HookDispatcher.Stop();   // 卸载排在钩子线程自己的泵里，UI 线程不等它
        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        Shutdown();
    }

    /// <summary>无论从托盘退出还是系统注销，都确保日志队列写完（可重复调用）。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        McpServer.Stop();
        FloatingManager.Shutdown();
        TriggerService.Stop();   // 幂等：托盘退出时已 Stop 过一次
        HookDispatcher.Stop();   // 幂等：系统注销 / 直接关进程时也得让钩子线程收尾卸载
        AppLog.Shutdown();
        base.OnExit(e);
    }
}
