using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Hooks;
using H.NotifyIcon;

namespace LTools;

/// <summary>
/// WPF 应用入口（替代旧 WinForms <c>TrayAppContext</c>）。
/// OnStartup：单实例互斥体 → H.NotifyIcon 托盘 → 装/卸钩子 → 初始化菜单/速记/设置 → 看门狗。
/// 主 STA 线程由 WPF Dispatcher 泵送，低级键盘/鼠标钩子回调仍在本线程派发（模型不变）。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\LTools_SingleInstance";
    private static Mutex? _singleInstanceMutex;

    private TaskbarIcon? _tray;
    private DispatcherTimer? _watchdog;
    private MenuItem? _macroMenuItem;      // 托盘「开始 / 停止录制宏」：标题随录制状态变化
    private TextBlock? _macroMenuItemText;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        AppLog.Info("App", $"配置文件 {cfgPath}");

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
                $"配置文件读取失败，已备份为 {Path.GetFileName(cfgPath)}.bad-* 并回退示例；本次不会覆盖原文件");
            TrayService.Notify("配置文件读取失败：原文件已保留，本次使用默认配置（详见运行日志）");
        }

        AppState.MouseModeSpeed = cfg.MouseModeSpeed;
        AppState.IsToolEnabled = cfg.CapsLockEnabled;
        AppState.IsSuperPanelEnabled = cfg.SuperPanel.Enabled;
        AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;
        ConfigStore.Initialize(cfgPath);
        QuickNote.Initialize(cfgPath);
        ConfigHelper.Initialize(cfgPath);
        ResourceDir.Ensure();
        MacroSettings.Load(cfg);
        MacroStore.Ensure();
        // 录制即生成动作：托盘等入口录完的宏，自动进动作池（动作编辑器内录制除外）。
        MacroRecorder.MacroSaved += OnMacroSaved;

        // 停止录制的兜底热键：消息窗口必须在钩子安装前建好（注册时不能再建窗口）。
        MacroHotkey.Initialize();

        // —— 内部动作注册（动作系统 §7）——
        InternalActionRegistry.RegisterDefaults();
        RemoveObsoleteActions();

        // 一宏一动作：回收不再被任何动作 / 组合步骤引用的宏文件（含旧版本遗留的孤儿文件）。
        MacroStore.CollectGarbage();

        // 模板截图同理：删动作 / 换模板后旧 shot-*.png 没有别处回收，启动时按引用清一次。
        ImageLibrary.CollectGarbage();

        // —— 启动提示：在无钩子阶段同步显示——首个 WPF 窗口的首帧会初始化渲染栈（D3D 等），
        //    耗时落在此处，不阻塞任何钩子回调。 ——
        MouseTip.Show("LTools 已启动");

        // —— 钩子：先预热回调 JIT（把首次 JIT 编译提前到无钩子阶段），再安装 ——
        KeyboardHook.WarmUp();
        MouseHook.WarmUp();
        try { KeyboardHook.Install(); AppLog.Info("Hook", "键盘钩子已安装"); }
        catch (Win32Exception ex) { CrashLog.Write("KeyboardHookInstall", ex); }
        try { MouseHook.Install(); AppLog.Info("Hook", "鼠标钩子已安装"); }
        catch (Win32Exception ex) { CrashLog.Write("MouseHookInstall", ex); }
    }

    private void BuildTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewItem("帮助面板 (CapsLock+`)", () => HelpPanel.Toggle()));
        menu.Items.Add(NewItem("速记 (CapsLock+N)", () => QuickNote.Toggle()));
        menu.Items.Add(NewItem("设置 (CapsLock+\\)", () => ConfigHelper.Toggle()));
        menu.Items.Add(new Separator());
        _macroMenuItem = NewItem("开始 / 停止录制宏", () => MacroRecorder.Toggle());
        _macroMenuItemText = (TextBlock)((StackPanel)_macroMenuItem.Header).Children[1];
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("启用 CapsLock 增强", Settings.ToggleCapsLock, () => AppState.IsToolEnabled));
        menu.Items.Add(NewItem("启用超级面板", Settings.ToggleSuperPanel, () => AppState.IsSuperPanelEnabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("退出 LTools", ExitApplication));

        // 状态点每次打开菜单时刷新：状态可能经 CapsLock+Esc 手势或设置页更改，托盘菜单要跟随
        menu.Opened += (_, _) => { RefreshStatusDots(); RefreshMacroMenuItem(); };

        _tray = new TaskbarIcon
        {
            ToolTipText = "LTools",
            IconSource = LoadIconSource(),
            ContextMenu = menu,
        };
        // ⚠️ 必须显式传 false：ForceCreate 的 enablesEfficiencyMode 默认 true，
        // 会把整个进程设为 IDLE 优先级 + EcoQoS（降频/调度到能效核）。
        // 本应用是全局低级键鼠钩子工具，实时性就是生命线——进程被降级后，长时间空闲再右键托盘图标时，
        // 钩子回调/注入/菜单渲染全在 IDLE 优先级 + 降频 CPU 上跑，消息泵被拖慢 → 鼠标可见卡顿。
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// 托盘菜单项：标题左侧统一留出状态点列（做法同系统菜单的勾选列，保证各行文字对齐）。
    /// <paramref name="isOn"/> 非空即开关项：启用 → 该列显示绿色状态点（SuccessColor #16A34A），
    /// 禁用 → 列保留但不显示圆点，文字不因开关状态左右跳动。
    /// 注意：本项目 MenuItem 模板只呈现 Header 不呈现 Icon，故状态点必须放进 Header。
    /// </summary>
    private MenuItem NewItem(string header, Action onClick, Func<bool>? isOn = null)
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
            _statusDots.Add((dot, isOn));
        return mi;
    }

    /// <summary>按各开关的当前状态刷新状态点（打开菜单时 + 点击菜单项后调用）。</summary>
    private void RefreshStatusDots()
    {
        foreach (var (dot, isOn) in _statusDots)
            dot.Fill = isOn() ? EnabledDotBrush : System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>
    /// 托盘宏菜单标题跟随录制状态：录制中显示「■ 停止录制宏（热键）」，让托盘始终是一个可用的停止入口。
    /// </summary>
    private void RefreshMacroMenuItem()
    {
        if (_macroMenuItemText == null) return;
        _macroMenuItemText.Text = MacroRecorder.IsRecording
            ? $"■ 停止录制宏（{MacroSettings.RecordHotkeyDisplay}）"
            : "开始 / 停止录制宏";
    }

    private const double StatusDotSize = 9;
    private const double StatusDotGap = 7;
    private readonly List<(System.Windows.Shapes.Ellipse Dot, Func<bool> IsOn)> _statusDots = new();
    private static readonly System.Windows.Media.Brush EnabledDotBrush = CreateEnabledDotBrush();

    private static System.Windows.Media.Brush CreateEnabledDotBrush()
    {
        // 与 Themes/Modern.xaml 的 SuccessColor (#16A34A) 保持一致
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 宏保存完成（后台线程）：非动作编辑器发起的录制要在动作池生成同名宏动作。
    /// <c>ConfigStore</c> / <c>ActionRegistry</c> 非线程安全，必须回到 UI 线程。
    /// </summary>
    private static void OnMacroSaved(string id, bool autoCreate)
    {
        if (!autoCreate) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try { EnsureMacroAction(id); }
            catch (Exception ex) { CrashLog.Write("App.EnsureMacroAction", ex); }
        });
    }

    /// <summary>为宏生成动作（已存在则跳过），实现「一个键鼠宏就是一个动作」。</summary>
    private static void EnsureMacroAction(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (ActionRegistry.All.Any(a => a.Type == ActionType.macro && a.MacroId == id)) return;

        var meta = MacroStore.MetaOf(id);
        string name = string.IsNullOrWhiteSpace(meta?.Name) ? "宏 " + id : meta!.Name;
        ConfigStore.AddAction(new ActionDto
        {
            Name = name,
            Type = ActionType.macro,
            MacroId = id,
            Icon = "record",
        });
        ConfigStore.Save();
    }

    /// <summary>
    /// 旧配置迁移：删除已取消动作化的内置动作，并清理菜单 / 超级面板 / 组合步骤里的引用，
    /// 避免留下点了没反应的死项。
    /// <list type="bullet">
    /// <item>v2.3.3：<c>macro.playLast</c>（回放上次宏）→ 由「键鼠宏」动作取代。</item>
    /// <item>v2.3.3：<c>settings.toggle</c>（设置）、<c>helpPanel.toggle</c>（帮助面板）→ 改为纯入口
    /// （CapsLock+\ / CapsLock+` / 托盘 / 超级面板标题栏齿轮），不再作为动作出现在动作池里。</item>
    /// <item>v2.3.3：<c>macro.recordToggle</c>（录制宏）、<c>macro.manage</c>（宏管理）→ 属于创作 / 管理入口
    /// （托盘 / 动作编辑器 / 组合动作左栏；宏管理入口已彻底移除），不是可重复执行的动作。</item>
    /// </list>
    /// </summary>
    private static void RemoveObsoleteActions()
    {
        string[] obsolete = ["macro.playLast", "settings.toggle", "helpPanel.toggle", "macro.recordToggle", "macro.manage"];
        static bool IsObsolete(ActionDto a, string[] commands) =>
            a.Type == ActionType.@internal && a.Command != null && commands.Contains(a.Command);

        var stale = ActionRegistry.All
            .Where(a => IsObsolete(a, obsolete))
            .Select(a => a.Id)
            .ToList();

        // 组合步骤是内嵌快照，删动作不会自动清；单独扫一遍，把过期步骤移除。
        bool stepChanged = false;
        foreach (var a in ActionRegistry.All)
        {
            if (a.Type != ActionType.composite) continue;
            if (a.Steps != null)
                stepChanged |= a.Steps.RemoveAll(s => IsObsolete(s.Action, obsolete)) > 0;
            if (a.Flow != null)
                stepChanged |= RemoveObsoleteNodes(a.Flow, obsolete);
        }

        static bool RemoveObsoleteNodes(List<FlowNode> nodes, string[] commands)
        {
            bool changed = false;
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                var n = nodes[i];
                if (n.Action != null && IsObsolete(n.Action, commands))
                {
                    nodes.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (n.If != null)
                {
                    changed |= RemoveObsoleteNodes(n.If.Then, commands);
                    if (n.If.Else != null) changed |= RemoveObsoleteNodes(n.If.Else, commands);
                }
                if (n.Loop != null) changed |= RemoveObsoleteNodes(n.Loop.Body, commands);
            }
            return changed;
        }

        if (stale.Count == 0 && !stepChanged) return;

        foreach (string id in stale) ConfigStore.RemoveAction(id);
        ConfigStore.Save();
    }

    private static System.Windows.Media.ImageSource? LoadIconSource()
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

    private void ExitApplication()
    {
        AppLog.Info("App", "退出 LTools");
        _watchdog?.Stop();
        KeyboardHook.Uninstall();
        MouseHook.Uninstall();
        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        Shutdown();
    }

    /// <summary>无论从托盘退出还是系统注销，都确保日志队列写完（可重复调用）。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Shutdown();
        base.OnExit(e);
    }
}
