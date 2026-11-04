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
        // 元数据自愈放后台：宏文件可能在别处被增删，扫描不阻塞启动。
        _ = Task.Run(MacroStore.Reconcile);
        // 录制即生成动作：全局热键 / 托盘 / 设置页录完的宏，自动进动作池（动作编辑器内录制除外）。
        MacroRecorder.MacroSaved += OnMacroSaved;

        // 停止录制的兜底热键：消息窗口必须在钩子安装前建好（注册时不能再建窗口）。
        MacroHotkey.Initialize();

        // —— 内部动作注册（动作系统 §7）——
        InternalActionRegistry.RegisterDefaults();
        RemoveObsoleteMacroActions();

        // —— 启动提示：在无钩子阶段同步显示——首个 WPF 窗口的首帧会初始化渲染栈（D3D 等），
        //    耗时落在此处，不阻塞任何钩子回调。 ——
        MouseTip.Show("LTools 已启动");

        // —— 钩子：先预热回调 JIT（把首次 JIT 编译提前到无钩子阶段），再安装 ——
        KeyboardHook.WarmUp();
        MouseHook.WarmUp();
        try { KeyboardHook.Install(); }
        catch (Win32Exception ex) { CrashLog.Write("KeyboardHookInstall", ex); }
        try { MouseHook.Install(); }
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
        menu.Items.Add(NewItem("宏管理…", () => ConfigHelper.OpenMacroPage()));
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

        var meta = MacroStore.List().FirstOrDefault(m => m.Id == id);
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
    /// 旧配置迁移：v2.3.3 起移除内部动作「回放上次宏」（<c>macro.playLast</c>）。
    /// 老用户的默认动作 / 菜单 / 超级面板引用一并清掉，避免留下点了没反应的死项。
    /// </summary>
    private static void RemoveObsoleteMacroActions()
    {
        var stale = ActionRegistry.All
            .Where(a => a.Type == ActionType.@internal && a.Command == "macro.playLast")
            .Select(a => a.Id)
            .ToList();

        // 组合步骤是内嵌快照，删动作不会自动清；单独扫一遍，把旧「回放上次宏」步骤移除。
        bool stepChanged = false;
        foreach (var a in ActionRegistry.All)
        {
            if (a.Steps == null) continue;
            stepChanged |= a.Steps.RemoveAll(s =>
                s.Action.Type == ActionType.@internal && s.Action.Command == "macro.playLast") > 0;
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
        _watchdog?.Stop();
        KeyboardHook.Uninstall();
        MouseHook.Uninstall();
        _tray?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        Shutdown();
    }
}
