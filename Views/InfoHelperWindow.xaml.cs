using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.InfoHelper;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 辅助工具：把窗口 / 屏幕区域 / 坐标 / 颜色 / 屏幕环境 / 截图 / 选中的文件取成一张张卡片，
/// 复制成文本贴给 agent 写动作。
/// </summary>
/// <remarks>
/// <para>
/// <b>不落盘</b>：卡片只在内存里，关掉窗口就清空；唯一落盘的是截图（<c>cache/info-helper/</c>，
/// 保留最近 30 张，见 <see cref="InfoShotStore"/>）——那是要给 agent 读的文件路径，得留在磁盘上。
/// </para>
/// <para>
/// 取值本身全在 <see cref="InfoGather"/>（Win32 / COM / 框选），卡片的文案与格式在
/// <see cref="InfoCards"/>（纯函数，<c>--selftest</c> 直接断言）。这个窗口只负责收集与复制。
/// </para>
/// <para>
/// 单实例：托盘再点一次就激活 / 还原已开的窗口，不叠第二个。
/// </para>
/// </remarks>
public partial class InfoHelperWindow : Window
{
    private static InfoHelperWindow? _current;

    private readonly ObservableCollection<InfoCard> _cards = new();
    private readonly DispatcherTimer _statusTimer;

    public InfoHelperWindow()
    {
        InitializeComponent();

        UnitHint.Text = InfoText.Unit;
        CardList.ItemsSource = _cards;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShowStatus("卡片正文可以直接划选、Ctrl+C 复制挑中的行；⧉ 复制整张卡。截图存 cache/info-helper，保留最近 30 张。");
        };

        Loaded += (_, _) => SetEmptyState();
    }

    /// <summary>托盘入口：没开就开一个，开着就激活 / 还原。</summary>
    public static void Toggle()
    {
        if (_current == null)
        {
            var window = new InfoHelperWindow();
            _current = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window)) _current = null;
            };
            WindowFront.Show(window);
            return;
        }

        if (_current.WindowState == WindowState.Minimized)
            _current.WindowState = WindowState.Normal;

        WindowFront.Bring(_current);
        _current.Focus();
    }

    // ============ 取值 ============

    private void Window_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.PickWindow(this), "已取窗口信息");

    private void Region_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Region(this), "已取屏幕区域");

    private void Point_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Point(this), "已取屏幕坐标");

    private void Color_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Color(this), "已取屏幕颜色");

    private void Screen_Click(object sender, RoutedEventArgs e) =>
        Take(InfoGather.Screen, "已取屏幕环境");

    private void Shot_Click(object sender, RoutedEventArgs e)
    {
        if (ShotBtn.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ShotBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void ShotFull_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Shot(this, ShotMode.FullScreen), "已截整个屏幕");

    private void ShotRegion_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Shot(this, ShotMode.Region), "已截框选区域");

    private void ShotWindow_Click(object sender, RoutedEventArgs e) =>
        Take(() => InfoGather.Shot(this, ShotMode.Window), "已截指定窗口");

    /// <summary>
    /// 选中的文件走后台读：<c>Shell.Application</c> 是跨进程 COM，慢的时候能拖几十毫秒，
    /// 而 UI 线程还和低级鼠标钩子共用（见 AGENTS.md），别在按钮回调里同步等它。
    /// </summary>
    private async void Selection_Click(object sender, RoutedEventArgs e)
    {
        SelectionBtn.IsEnabled = false;
        ShowStatus("正在读资源管理器 / 桌面的选择…");
        try
        {
            InfoCard? card = await Task.Run(InfoGather.Selection);
            if (card == null)
            {
                ShowStatus("没有读到选中的项（先在资源管理器或桌面上选中文件再点）", error: true);
                return;
            }
            Add(card);
            ShowStatus("已取选中的文件");
        }
        catch (Exception ex)
        {
            CrashLog.Write("InfoHelper.Selection", ex);
            ShowStatus("读取失败：" + ex.Message, error: true);
        }
        finally
        {
            SelectionBtn.IsEnabled = true;
        }
    }

    /// <summary>执行一次取值：取消（返回 null）只提示，不建卡。</summary>
    private void Take(Func<InfoCard?> gather, string ok)
    {
        try
        {
            InfoCard? card = gather();
            if (card == null)
            {
                ShowStatus("已取消");
                return;
            }
            Add(card);
            ShowStatus(ok + " · 点卡片上的 ⧉ 复制");
        }
        catch (Exception ex)
        {
            CrashLog.Write("InfoHelper.Take", ex);
            ShowStatus("取不到：" + ex.Message, error: true);
        }
    }

    private void Add(InfoCard card)
    {
        _cards.Insert(0, card);   // 最新的在最上面，不用往下翻
        SetEmptyState();
        CardScroll.ScrollToTop();
    }

    // ============ 卡片操作 ============

    private void CopyCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } card) CopyText(card.Text);
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_cards.Count == 0)
        {
            ShowStatus("还没有卡片");
            return;
        }
        CopyText(string.Join("\n\n", _cards.Select(c => c.Text)));
    }

    private void RefreshCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        int index = _cards.IndexOf(card);
        if (index < 0) return;

        try
        {
            InfoCard? fresh = InfoGather.Refresh(card, this);
            if (fresh == null)
            {
                ShowStatus(card.Kind == InfoKind.Window
                    ? "窗口已经关了，重新点「窗口信息」取一次"
                    : "已取消", error: true);
                return;
            }
            _cards[index] = fresh;
            ShowStatus("已刷新");
        }
        catch (Exception ex)
        {
            CrashLog.Write("InfoHelper.Refresh", ex);
            ShowStatus("刷新失败：" + ex.Message, error: true);
        }
    }

    private void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { } card) return;
        InfoShotStore.Delete(card.FilePath);
        _cards.Remove(card);
        SetEmptyState();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_cards.Count == 0) return;
        if (!ConfirmDialog.Confirm(this, "清空取值卡",
                $"清空这 {_cards.Count} 张卡片？截图文件会一起删掉。", danger: true, okText: "清空"))
            return;

        foreach (InfoCard card in _cards) InfoShotStore.Delete(card.FilePath);
        _cards.Clear();
        SetEmptyState();
        ShowStatus("已清空");
    }

    // ============ 小工具 ============

    /// <summary>
    /// 按钮所在的卡片：行内按钮点下去不会选中任何行，标识只能走 <c>CommandParameter</c>
    /// （别用 <c>Tag</c>：<c>BtnGhost</c> 把 <c>Tag</c> 当悬停底色）。
    /// </summary>
    private static InfoCard? CardOf(object sender) =>
        (sender as Button)?.CommandParameter as InfoCard;

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            ShowStatus("没有可复制的内容");
            return;
        }
        bool ok = NativeClipboard.SetText(text);
        ShowStatus(ok
            ? "已复制，可以直接粘给 agent"
            : "复制失败（剪贴板被别的程序占着，稍后再试）",
            error: !ok);
    }

    private void SetEmptyState()
    {
        EmptyHint.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CopyAllBtn.IsEnabled = _cards.Count > 0;
        ClearBtn.IsEnabled = _cards.Count > 0;
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerBrush" : "HintTextBrush");

        _statusTimer.Stop();
        if (!error) _statusTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
    }
}
