using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Share;
using LTools.Features;
using LTools.Features.Units.Screen;

namespace LTools.Views;

/// <summary>
/// 局域网共享的管理窗口（布局 A：左内容流、右二维码 / 地址 / 开关）。
/// </summary>
/// <remarks>
/// 关窗**不停止**共享；只有关程序或在这里点「停止」才停。列表靠 1 秒轮询，
/// 也接受资源管理器直接往 <c>share/files/</c> 放文件（清单是唯一来源，不放这里）。
/// </remarks>
public partial class ShareWindow : Window
{
    private readonly ShareStore _store = ShareStore.Default;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _qrUrl;
    private int _qrIndex;
    private string _listSignature = "";
    private bool _closed;
    private bool _dragLogged;

    /// <summary>刷新回填「接收」下拉时别再触发一次保存。</summary>
    private bool _syncingInbound;

    /// <summary>文字条目的正文预览（按 路径 + 修改时间 + 大小 缓存，1 秒轮询不会反复读盘）。</summary>
    private readonly Dictionary<string, (DateTime Stamp, long Size, string Text)> _previews = new();

    private DispatcherTimer? _hintTimer;

    private static ShareWindow? _open;

    public ShareWindow()
    {
        InitializeComponent();

        SendBtn.IsEnabled = false;
        ComposeBox.ContextMenu = BuildComposeMenu();
        PreviewKeyDown += Window_PreviewKeyDown;

        var rows = new ShareRowSelector
        {
            Plain = (DataTemplate)FindResource("ShareRowTemplate"),
            Text = (DataTemplate)FindResource("ShareTextRowTemplate"),
        };
        OutList.ItemTemplateSelector = rows;
        InList.ItemTemplateSelector = rows;

        InboundCombo.ItemsSource = ShareOptions.InboundChoices;

        // 拖放走标准 OLE（XAML 里 AllowDrop + PreviewDragOver / PreviewDrop）：共享窗口住在
        // 普通权限宿主里，资源管理器拖过来不会被 UIPI 拦。以前那层 WM_DROPFILES 桥
        // （FileDropBridge）是替提权窗口兜底的，实测连消息都收不到，已随拆分一起删掉。

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            ShareServer.Changed += OnServerChanged;
            Refresh();
            _timer.Start();
            ComposeBox.Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            ShareServer.Changed -= OnServerChanged;
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    /// <summary>打开或激活同一个窗口（内置功能 / 托盘都走这里）。</summary>
    /// <summary>
    /// 打开（或唤起）**本进程**里的共享窗口。
    /// </summary>
    /// <remarks>
    /// 只有两处会用它：界面宿主（<c>UiHost.exe</c>，普通权限）与冒烟。
    /// 主程序里别调它——主程序是提权的，在这里开的窗口收不到 Explorer 的拖放；
    /// 托盘 / 内置功能走 <see cref="UiHostProcess.ShowShareWindow"/>（把宿主拉起来 / 唤起）。
    /// </remarks>
    public static void ShowOrActivate()
    {
        if (_open is { IsLoaded: true })
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        _open = new ShareWindow();
        _open.Show();
    }

    private void OnServerChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Ui.Post(Refresh);
            return;
        }
        Refresh();
    }

}
