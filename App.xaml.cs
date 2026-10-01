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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // —— UI 线程未捕获异常兑底：记录到崩溃日志并吞掉（常驻输入工具不因单次异常退出）——
        DispatcherUnhandledException += App_DispatcherUnhandledException;

        // —— 单实例 ——
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
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

        // —— 钩子（主线程安装，Dispatcher 泵送）——
        try { KeyboardHook.Install(); }
        catch (Win32Exception ex) { CrashLog.Write("KeyboardHookInstall", ex); }
        try { MouseHook.Install(); }
        catch (Win32Exception ex) { CrashLog.Write("MouseHookInstall", ex); }

        // —— 加载配置 ——
        var cfgPath = ConfigLocator.FindPath();
        var cfg = AppConfig.Load(cfgPath);
        AppState.MouseModeSpeed = cfg.MouseModeSpeed;
        AppState.IsToolEnabled = cfg.CapsLockEnabled;
        AppState.IsSuperPanelEnabled = cfg.SuperPanel.Enabled;
        AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;
        ConfigStore.Initialize(cfgPath);
        QuickNote.Initialize(cfgPath);
        ConfigHelper.Initialize(cfgPath);

        // —— 内部动作注册（动作系统 §7）——
        InternalActionRegistry.RegisterDefaults();

        // —— 启动提示：鼠标旁显示“LTools 已启动”（复用 MouseTip，1.8s 后自动隐藏）——
        MouseTip.Show("LTools 已启动");
    }

    private void BuildTray()
    {
        var menu = new ContextMenu();
        menu.Items.Add(NewItem("帮助面板 (CapsLock+`)", () => HelpPanel.Toggle()));
        menu.Items.Add(NewItem("速记 (CapsLock+N)", () => QuickNote.Toggle()));
        menu.Items.Add(NewItem("设置 (CapsLock+\\)", () => ConfigHelper.Toggle()));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("启用 CapsLock 增强", Settings.ToggleCapsLock, () => AppState.IsToolEnabled));
        menu.Items.Add(NewItem("启用超级面板", Settings.ToggleSuperPanel, () => AppState.IsSuperPanelEnabled));
        menu.Items.Add(new Separator());
        menu.Items.Add(NewItem("退出 LTools", ExitApplication));

        // 状态点每次打开菜单时刷新：状态可能经 CapsLock+Esc 手势或设置页更改，托盘菜单要跟随
        menu.Opened += (_, _) => RefreshStatusDots();

        _tray = new TaskbarIcon
        {
            ToolTipText = "LTools",
            IconSource = LoadIconSource(),
            ContextMenu = menu,
        };
        _tray.ForceCreate();
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
