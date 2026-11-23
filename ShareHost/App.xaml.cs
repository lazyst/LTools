using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Link;
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

    // 与提权主程序的链路（过渡期：宿主做服务端、提权进程连上来；拉面板快照验证 + 为下一步渲染做准备）。
    private LinkServer? _link;
    private PanelSnapshot? _snapshot;

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
        // 写自己的日志文件（ltools-host-*.log）：主程序此刻也在写 ltools-*.log，
        // 两个进程共用同一个文件会互相把对方锁成「一行都写不进去」。
        AppLog.Configure(log.Enabled, log.Level, log.RetentionDays, fileTag: "host");

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

        // 起链路服务端（很快）+ 后台只读加载配置（重活，别拖住窗口首帧）。
        StartLink();
        _ = Task.Run(LoadReadOnlyConfig);
    }

    /// <summary>只读加载配置：宿主显示面板要用，但一个字都不写（写入归提权进程）。</summary>
    private static void LoadReadOnlyConfig()
    {
        try
        {
            ConfigStore.InitializeReadOnly(ConfigLocator.FindPath());
        }
        catch (Exception ex)
        {
            AppLog.Warn("ShareHost", $"只读加载配置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 起链路服务端。**宿主做服务端**：中完整性进程建管道，提权进程作为客户端连上来
    /// （中完整性写不了高完整性的管道，反过来可以，见 docs/PROCESS_SPLIT.md §5.1）。
    /// </summary>
    private void StartLink()
    {
        string? token = LinkToken.Read();
        if (token == null)
        {
            AppLog.Info("ShareHost", "没有链路令牌，跳过链路（主程序没在跑？）");
            return;
        }

        try
        {
            var server = new LinkServer(
                LinkToken.PipeName,
                token,
                static (_, request) => Task.FromResult(LinkMessage.Response(request, error: "宿主暂不支持该命令")),
                OnLinkEvent);

            Volatile.Write(ref _link, server);
            server.ClientChanged += OnLinkClientChanged;
            server.Start();

            AppLog.Info("ShareHost", $"链路服务端已启动（{LinkToken.PipeName}），等主程序连上");
        }
        catch (Exception ex)
        {
            AppLog.Warn("ShareHost", $"链路服务端启动失败：{ex.Message}");
        }
    }

    private static void OnLinkClientChanged(bool connected) =>
        AppLog.Info("ShareHost", connected ? "主程序已连接" : "主程序已断开");

    private void OnLinkEvent(LinkMessage message)
    {
        if (!string.Equals(message.Method, LinkMethods.EventClientReady, StringComparison.Ordinal)) return;
        _ = PullSnapshotAsync();
    }

    /// <summary>主程序就绪后拉一份面板快照（验证链路；下一步面板真的在宿主里渲染就有数据了）。</summary>
    private async Task PullSnapshotAsync()
    {
        LinkServer? server = Volatile.Read(ref _link);
        if (server == null) return;

        try
        {
            LinkMessage reply = await server.RequestAsync(LinkMethods.PanelSnapshot, timeoutMs: 3000)
                .ConfigureAwait(false);

            if (reply.Ok && reply.Read<PanelSnapshot>() is { } snapshot)
            {
                Volatile.Write(ref _snapshot, snapshot);
                AppLog.Info("ShareHost",
                    $"面板快照：{snapshot.Actions.Count} 个动作、{snapshot.Panel.Global.Scenes.Count} 个全局场景");
            }
            else
            {
                AppLog.Warn("ShareHost", "面板快照失败：" + reply.Error);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ShareHost", $"拉面板快照失败：{ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watch?.Stop();

        LinkServer? link = Volatile.Read(ref _link);
        if (link != null) _ = link.DisposeAsync();

        ShareServer.Stop();
        AppLog.Shutdown();
        try { _mutex?.Dispose(); } catch { /* 已释放 */ }
        base.OnExit(e);
    }

    /// <summary>最近一次从主程序拉到的面板快照（下一步宿主渲染面板用）。</summary>
    internal PanelSnapshot? Snapshot => Volatile.Read(ref _snapshot);

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
        }

        // 关窗只是 Hide：再来一次要能重新显示（Show 里带「没显示就 Show」），
        // 而且助手是后台进程，第一次 Show 同样会被前台锁压在别的窗口后面。
        WindowFront.Show(_window);
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
