using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Share;
using LTools.Features;
using LTools.Views;

namespace LTools.ShareHost;

/// <summary>
/// 共享助手进程：**普通权限**的窗口 + 共享服务。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一个进程：主程序是 <c>requireAdministrator</c>，而 Explorer 不会把文件拖给提权窗口（UIPI），
/// 所以共享窗口必须活在一个 asInvoker 进程里。它只做两件事——跑 <see cref="ShareServer"/>、
/// 按 <c>share/host.intent</c> 显示 / 隐藏窗口。别的什么都不碰（不装钩子、不加载动作库）。
/// </para>
/// <para>
/// 生命周期：主程序「运行中」时拉起（隐藏）；托盘 / 内置功能再点就是 <c>show</c>；
/// 主程序退出、或窗口里点「停止共享」并关窗，就 <c>stop</c> 退出。
/// **关窗不停止共享**，这与主程序里的语义一致。
/// </para>
/// </remarks>
public partial class App : Application
{
    private Mutex? _mutex;
    private ShareWindow? _window;
    private DispatcherTimer? _watch;
    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：第二个实例立刻退出，而且**绝不能碰意图文件**——那是写给已经在跑的那个的。
        _mutex = new Mutex(true, ShareHelper.MutexName, out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("ShareHost", "共享助手未处理的异常", args.Exception);
            args.Handled = true;
        };

        string configPath = ConfigLocator.FindPath();
        ConfigStore.UsePathOnly(configPath);

        // 日志和主程序用同一套配置：`[Share] 拖放：…` 这些行要能落到 logs/ltools-*.log，
        // 否则「拖不进来」只能靠猜。
        SettingsFile settings = ConfigBundle.ReadSettings(configPath);
        LogConfig log = settings.Logging ?? new LogConfig();
        AppLog.Configure(log.Enabled, log.Level, log.RetentionDays);

        Ui.Initialize(Dispatcher);
        WindowCorners.Attach();
        WindowSizing.Attach();

        ShareOptions options = settings.Share;
        ShareServer.Apply(options);

        // 没有意图文件（用户直接双击）就看窗口；主程序拉起时一定带着意图。
        string? intent = ShareHelper.ReadIntent();
        Apply(intent ?? ShareHelper.ShowIntent);
        if (intent != null) ShareHelper.Consume(intent);

        _watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watch.Tick += (_, _) => Poll();
        _watch.Start();

        AppLog.Info("ShareHost", $"共享助手已启动（{intent ?? "show"}，端口 {options.Port}，运行中 {options.Running}）");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watch?.Stop();
        ShareServer.Stop();
        AppLog.Shutdown();
        try { _mutex?.Dispose(); } catch { /* 已释放 */ }
        base.OnExit(e);
    }

    private void Poll()
    {
        string? intent = ShareHelper.ReadIntent();
        if (string.IsNullOrEmpty(intent)) return;

        // 执行完删掉意图；万一删除撞上主程序刚写的新值，下一轮再按新值做一次（三个动作都幂等）。
        if (Apply(intent)) ShareHelper.Consume(intent);
    }

    private bool Apply(string intent)
    {
        switch (intent)
        {
            case ShareHelper.StopIntent:
                _quitting = true;
                _watch?.Stop();
                ShareServer.Stop();
                _window?.Close();
                Shutdown();
                return true;

            case ShareHelper.ShowIntent:
                ShowWindow();
                return true;

            case ShareHelper.RunIntent:
                _window?.Hide();
                return true;
        }

        return false;
    }

    private void ShowWindow()
    {
        if (_window == null || !_window.IsLoaded)
        {
            _window = new ShareWindow();
            _window.Closing += Window_Closing;
            _window.Show();
            return;
        }

        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;

        // 关窗不停止共享：窗口藏起来，服务继续跑（下次从托盘 / 内置功能唤起）。
        if (ShareServer.IsRunning)
        {
            e.Cancel = true;
            _window?.Hide();
            return;
        }

        // 服务已经停了：关掉窗口之后这个进程没有任何作用，跟着退出。
        Dispatcher.BeginInvoke(new Action(Shutdown), DispatcherPriority.Background);
    }
}
