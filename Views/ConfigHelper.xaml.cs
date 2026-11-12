using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>
/// 设置 GUI（阶段 3 重构为左导航 + 多页面）。页面：通用 / 动作管理 / 子动作库 / 快捷键 / 超级面板 / AI / 终端路径 / 日志 / 备份与恢复。
/// 沿用 v2.1.0 防抖自动保存（800ms），扩展到动作 / 快捷键 / 超级面板编辑。
/// 三个全局开关（CapsLock 修饰键 / 边缘滚轮音量 / 超级面板）即时落盘，重启不丢失。
/// </summary>
public partial class ConfigHelperWindow : Window
{
    // 二级下拉导航的四个父项（AppConfig.NavExpandedGroups 里也用这四个值）
    private const string FamilySuperPanel = "superpanel";
    private const string FamilyAi = "ai";
    private const string FamilyLogs = "logs";
    private const string FamilyMacros = "macros";

    private readonly DispatcherTimer _saveDebounce;
    // 别处改了动作 / 宏（面板删除、录制完成、AI 采纳…）：合批后只刷已建页，避免一次操作反复 Reload
    private readonly DispatcherTimer _refreshDebounce;
    // 导航页记忆：切页只记下待写的值，短防抖后丢线程池写盘（见 ScheduleNavPersist）
    private readonly DispatcherTimer _navPersistTimer;
    private bool _navDirty;                 // 有过切页 / 展开收起才需要关窗时补写一次
    private string _pendingNav = "actions";
    private List<string> _pendingGroups = new();
    private readonly ObservableCollection<ActionRow> _actionRows = new();
    private bool _initializing = true;   // 初始化设 IsChecked 会触发事件，用此标志跳过
    private bool _loadingList;           // 列表重建期间抑制选择事件
    private bool _pagesReady;            // 页面全部构建完成前抑制导航切换
    private bool _syncingNav;            // 程序化点亮二级导航时不重复导航
    private bool _restoringNav;          // 恢复上次导航时不写盘、不播动画
    private string _currentNav = "";     // 当前导航 tag（具体子页，如 ai-audit）
    private readonly HashSet<string> _expandedGroups = new();   // 展开着的二级分组（superpanel / ai / macros / logs）
    // 每个二级分组上次所在的子页：点父项切进来时回到这里，而不是永远回第一页
    private readonly Dictionary<string, string> _lastSubPage = new()
    {
        [FamilySuperPanel] = "general",
        [FamilyAi] = "connect",
        [FamilyMacros] = "list",
        [FamilyLogs] = "settings",
    };
    private bool _backupBusy;            // 备份 / 导入进行中，禁用重复点击
    private bool _loadingRunNotice;      // 通用页「运行提示」回填期间抑制保存事件
    private bool _loadingTrayDoubleClick;   // 通用页「左键双击托盘图标」回填期间抑制保存事件
    private bool _navApplied;            // 期间发生过显式导航（取消排队中的首次恢复）

    // 惰性页：XAML 里 Host_* 只是空 ContentControl，首次访问 Page_* 才 new 出视图。
    private HotkeysView? _pageHotkeys;
    private MacroView? _pageMacros;
    private SubActionLibraryView? _pageSubActions;
    private PanelManageView? _pageSuperPanel;
    private TriggersView? _pageTriggers;
    private AiView? _pageAi;

    public ConfigHelperWindow(string configPath)
    {
        // 诊断采样：覆盖整个构造期（XAML 解析 / 内联页 / 数据回填 / 导航恢复排队）
        using var perfSeg = PerfProbe.Measure("ctor:total");
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        // App.OnStartup 已经 Initialize 过（同一锚点）：只有锚点真变了才重做，否则每次打开设置
        // 都白跑一遍扫动作目录 / 修引用 / 播种（实测 200+ms，全压在 UI 线程与鼠标钩子同一条线上）。
        if (!string.Equals(ConfigStore.ConfigPath, configPath, StringComparison.OrdinalIgnoreCase))
            ConfigStore.Initialize(configPath);
        ConfigPath = configPath;

        ActionList.ItemsSource = _actionRows;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveNow(); };

        // 一次业务操作可能连着发好几条通知（删宏 → 删动作 → 回收），150ms 合批只刷一次。
        _refreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _refreshDebounce.Tick += (_, _) => { _refreshDebounce.Stop(); RefreshCreatedPages(); };
        ConfigStore.Changed += OnConfigChanged;

        _navPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _navPersistTimer.Tick += (_, _) => { _navPersistTimer.Stop(); FlushNavPersist(); };

        InitLogAutoScroll();
        // 「动作管理 / 终端路径 / 日志设置」的回填延后到页面真正可见时：下面排到 Background 的
        // ApplyNavPage(startupNav) 会按启动页调用对应的 Populate*，用户中途切页也由 ApplyNavPage 补。
        // 构造期无条件跑三份（尤其 PopulateLogging 会 ReadTail(5000) 扫一遍日志文件），
        // 启动页不是它们时全是白工；这三类回填没有构造期必须完成的初始化副作用。
        // InitLogAutoScroll 只挂 timer / 初始化按钮，必须留在构造期。

        _pagesReady = true;
        // 恢复导航状态只读 ui 单域（原来整份 LoadConfig 会顺带扫一遍 actions/）。
        var ui = UiStore.Read(ConfigPath);
        string lastNav = NormalizeSavedNav(ui.LastNavPage);
        _expandedGroups.Clear();
        foreach (string g in ui.NavExpandedGroups ?? new List<string>())
            if (g is FamilySuperPanel or FamilyAi or FamilyMacros or FamilyLogs) _expandedGroups.Add(g);
        string? startupFamily = NavFamilyOf(lastNav);
        // 当前页所在的组一定展开：否则打开设置看不出自己在哪个子页（其余分组按记录）
        if (startupFamily != null) _expandedGroups.Add(startupFamily);

        _restoringNav = true;
        _syncingNav = true;   // 只点亮 RadioButton，别让 Checked → Nav_Changed 把整页重活拉回构造期
        try
        {
            if (startupFamily != null) NavGroupItem(startupFamily).IsChecked = true;
            bool navMatched = false;
            foreach (var rb in AllNavItems())
            {
                if (rb.Tag is string t && t == lastNav) { rb.IsChecked = true; navMatched = true; break; }
            }
            if (!navMatched) NavActions.IsChecked = true;
            ApplyNavExpandedStates(animate: false);   // 首帧就到位，不做动画
        }
        finally { _syncingNav = false; _restoringNav = false; }

        // 只切可见性（空宿主，纯属性赋值，不建视图、不加载数据），窗口第一帧就有正确的页。
        SetPageVisibility(lastNav);

        // 当前页的实例化 + 数据加载排到窗口 Show() 之后：先让窗口出来，再补内容。
        // 期间若被托盘双击 / 面板跳转 / 用户点击显式导航（_navApplied），这里直接跳过，别把页改回去。
        string startupNav = lastNav;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_navApplied) return;
            _restoringNav = true;
            try { ApplyNavPage(startupNav); }
            finally { _restoringNav = false; }
        }));

        // 布局完成后把当前页滚进视野：两个下拉都展开时会超出左边栏的可视高度
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(ScrollCheckedNavIntoView));

        // 初始化完成后再响应用户操作
        Loaded += async (_, _) =>
        {
            _initializing = true;
            // 开关状态
            PopulateToggles();
            PopulateRunNotice();
            PopulateTrayDoubleClick();
            // 自启状态：任务计划 COM 首次激活要百毫秒级，放线程池避免窗口白屏。
            // 查询期间保持 _initializing，免得用户此刻点开关与查询结果互相覆盖。
            var autoStart = await System.Threading.Tasks.Task.Run(AutoStartService.Query);
            _initializing = false;
            ApplyAutoStartState(autoStart);
        };
        Closed += (_, _) =>
        {
            // 先退订：窗口关掉后还收通知会对已卸载的控件 Reload，也会一直持有这个窗口
            ConfigStore.Changed -= OnConfigChanged;
            _refreshDebounce.Stop();
            // 只在防抖还没落盘时补写一次：用户打开设置什么都没改就关掉，不该覆盖记住的页
            if (_navDirty)
            {
                _navPersistTimer.Stop();
                try { PersistNav(); } catch (Exception ex) { AppLog.Warn("Nav", "退出时写 ui.json 失败：" + ex.Message); }
            }
            if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        };
    }

    /// <summary>配置文件路径（供显示/调试）。</summary>
    private string ConfigPath { get; }

    // ============ 惰性页（B 方案） ============
    // XAML 里 Host_* 只是空 ContentControl；首次访问 Page_* 才 new 出真正的 UserControl 并塞进宿主。
    // 这样构造期不必一次建 6 棵可视树（「从超级面板打开设置」实测卡顿的大头），且只有切到的
    // 那一页被实例化 + 载入数据。可见性一律设在 Host_* 上（不触发创建），方法与数据走 Page_*
    // （触发创建）。跨页刷新用 RefreshCreatedPages()，别为没打开过的页强建视图。
    private HotkeysView Page_Hotkeys => _pageHotkeys ??= Mount(Host_Hotkeys, () => new HotkeysView());
    private MacroView Page_Macros => _pageMacros ??= Mount(Host_Macros, () => new MacroView());
    private SubActionLibraryView Page_SubActions => _pageSubActions ??= Mount(Host_SubActions, () => new SubActionLibraryView());
    private PanelManageView Page_SuperPanel => _pageSuperPanel ??= Mount(Host_SuperPanel, () => new PanelManageView());
    private TriggersView Page_Triggers => _pageTriggers ??= Mount(Host_Triggers, () => new TriggersView());
    private AiView Page_Ai => _pageAi ??= Mount(Host_Ai, () => new AiView());

    private static T Mount<T>(ContentControl host, Func<T> factory) where T : UIElement
    {
        var view = factory();
        host.Content = view;
        return view;
    }

    /// <summary>
    /// 配置 / 动作在别处变了（删动作 / 导入 / 重新加载 / 保存）：只刷已经建出来的页。
    /// 从没打开过的页下次切入时 ApplyNavPage 自然会 Reload，不必现在就为它建视图。
    /// </summary>
    private void RefreshCreatedPages()
    {
        if (_pageHotkeys != null) _pageHotkeys.Reload();
        if (_pageSuperPanel != null) _pageSuperPanel.Reload();
        if (_pageAi != null) _pageAi.Reload();
        if (_pageMacros != null) _pageMacros.Reload();
        if (_pageSubActions != null) _pageSubActions.Reload();
        if (_pageTriggers != null) _pageTriggers.Reload();
    }

    /// <summary>别处改了动作 / 宏：切回 UI 线程并合批刷新（事件可能来自录制 / AI 采纳的后台线程）。</summary>
    /// <remarks>只在数据真变时发，不在切页 / 导航记忆写盘时发，保住阶段 1 惰性建页的减负成果。</remarks>
    private void OnConfigChanged()
    {
        Ui.Post(() =>
        {
            if (!IsLoaded) return;   // 窗口还没显示 / 已关闭，下次开窗自然会重读新数据
            _refreshDebounce.Stop();
            _refreshDebounce.Start();
        });
    }

    // ============ 导航切换 ============
    private static readonly Duration SubNavAnimDuration = new(TimeSpan.FromMilliseconds(170));

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_pagesReady || _syncingNav) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        // 切换页前先落盘未保存改动，避免重建列表时丢弃（如超级面板 800ms 防抖内的编辑）
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        ApplyNavPage(tag);
    }

    /// <summary>「面板管理」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavSuperPanel_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilySuperPanel, e);

    /// <summary>「AI 接入」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavAi_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyAi, e);

    /// <summary>「键鼠宏」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavMacros_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyMacros, e);

    /// <summary>「运行日志」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavLogs_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyLogs, e);

    /// <summary>
    /// 父项已经选中（正在这个分组里）时，再点它就是展开 / 收起，不换页；
    /// 没选中时不动它，交给 RadioButton 的 Checked → <see cref="ApplyNavPage"/> 进该分组。
    /// </summary>
    private void ToggleNavGroup(string family, MouseButtonEventArgs e)
    {
        if (!_pagesReady || _syncingNav) return;
        if (NavGroupItem(family).IsChecked != true) return;
        e.Handled = true;
        SetNavGroupExpanded(family, !_expandedGroups.Contains(family), animate: true);
        ScheduleNavPersist();
    }

    /// <summary>左栏所有导航项（含四个二级下拉里的子项）。</summary>
    private IEnumerable<RadioButton> AllNavItems() =>
        NavList.Children.OfType<RadioButton>()
            .Concat(SuperPanelSubNav.Children.OfType<RadioButton>())
            .Concat(AiSubNav.Children.OfType<RadioButton>())
            .Concat(MacrosSubNav.Children.OfType<RadioButton>())
            .Concat(LogsSubNav.Children.OfType<RadioButton>());

    // 四个分组共用一套机制：下面四张表把 family 映射到各自的父项 / 下拉容器 / 箭头，
    // 新增分组只加行，别再写 family == FamilyAi ? A : B 这种二元判断（第三组起就会落到 superpanel 上）。
    private RadioButton NavGroupItem(string family) => family switch
    {
        FamilyAi => NavAi,
        FamilyMacros => NavMacros,
        FamilyLogs => NavLogs,
        _ => NavSuperPanel,
    };

    private StackPanel NavGroupPanel(string family) => family switch
    {
        FamilyAi => AiSubNav,
        FamilyMacros => MacrosSubNav,
        FamilyLogs => LogsSubNav,
        _ => SuperPanelSubNav,
    };

    private Border NavGroupHost(string family) => family switch
    {
        FamilyAi => AiSubNavHost,
        FamilyMacros => MacrosSubNavHost,
        FamilyLogs => LogsSubNavHost,
        _ => SuperPanelSubNavHost,
    };

    private TextBlock NavGroupChevron(string family) => family switch
    {
        FamilyAi => NavAiChevron,
        FamilyMacros => NavMacrosChevron,
        FamilyLogs => NavLogsChevron,
        _ => NavSuperPanelChevron,
    };

    /// <summary>导航 tag → 所属二级分组（null = 普通一级页）。</summary>
    private static string? NavFamilyOf(string? tag) => tag switch
    {
        "superpanel" or "superpanel-general" or "superpanel-scenes" or "superpanel-blocked" => FamilySuperPanel,
        "ai" or "ai-connect" or "ai-audit" => FamilyAi,
        "macros" or "macros-list" or "macros-settings" => FamilyMacros,
        "logs" or "logs-settings" or "logs-recent" => FamilyLogs,
        _ => null,
    };

    /// <summary>导航 tag → 子页 key（PanelManageView / MacroView / AiView 的 ShowPage 认这个；日志页自己切两个 ScrollViewer）。</summary>
    private static string SubPageOf(string family, string tag) => (family, tag) switch
    {
        (FamilySuperPanel, "superpanel-scenes") => "scenes",
        (FamilySuperPanel, "superpanel-blocked") => "blocked",
        (FamilySuperPanel, _) => "general",
        (FamilyAi, "ai-audit") => "audit",
        (FamilyAi, _) => "connect",
        (FamilyMacros, "macros-settings") => "settings",
        (FamilyMacros, _) => "list",
        (FamilyLogs, "logs-recent") => "recent",
        _ => "settings",
    };

    private static string NavTagOf(string family, string page) => family switch
    {
        FamilyAi => "ai-" + page,
        FamilyMacros => "macros-" + page,
        FamilyLogs => "logs-" + page,
        _ => page switch
        {
            "scenes" => "superpanel-scenes",
            "blocked" => "superpanel-blocked",
            _ => "superpanel-general",
        },
    };

    /// <summary>父项 tag 落到该分组的默认子页（点父项时第一站）。</summary>
    private static string DefaultSubPage(string family) => family switch
    {
        FamilyAi => "connect",
        FamilyMacros => "list",
        FamilyLogs => "settings",
        _ => "general",
    };

    /// <summary>校验存下来的导航 tag：父项落到具体子页，不认识的（旧版本 / 手改）回到动作管理。</summary>
    private static string NormalizeSavedNav(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return "actions";
        string? family = NavFamilyOf(tag);
        if (family != null)
            return tag == family ? NavTagOf(family, DefaultSubPage(family)) : tag;
        return FirstLevelNav.Contains(tag) ? tag : "actions";
    }

    /// <summary>一级导航的 tag（与 <c>ConfigHelper.xaml</c> 的 RadioButton 一一对应；四个父项不在此表）。</summary>
    private static readonly HashSet<string> FirstLevelNav = new(StringComparer.Ordinal)
    {
        "general", "actions", "subactions", "hotkeys", "triggers", "terminals", "backup",
    };

    /// <summary>把某个二级下拉设为展开 / 收起，并记住这个状态。</summary>
    private void SetNavGroupExpanded(string family, bool expanded, bool animate)
    {
        if (expanded) _expandedGroups.Add(family);
        else _expandedGroups.Remove(family);
        AnimateSubNav(NavGroupHost(family), NavGroupChevron(family), expanded, animate);
    }

    /// <summary>按 <see cref="_expandedGroups"/> 套一遍四个下拉（启动恢复用，不播动画）。</summary>
    private void ApplyNavExpandedStates(bool animate)
    {
        SetNavGroupExpanded(FamilySuperPanel, _expandedGroups.Contains(FamilySuperPanel), animate);
        SetNavGroupExpanded(FamilyAi, _expandedGroups.Contains(FamilyAi), animate);
        SetNavGroupExpanded(FamilyMacros, _expandedGroups.Contains(FamilyMacros), animate);
        SetNavGroupExpanded(FamilyLogs, _expandedGroups.Contains(FamilyLogs), animate);
    }

    /// <summary>把当前选中的导航项滚进视野（启动恢复时用：四个下拉都展开会超出可视高度）。</summary>
    private void ScrollCheckedNavIntoView()
    {
        RadioButton? parent = null;
        foreach (var rb in AllNavItems())
        {
            if (rb.IsChecked != true || rb.Tag is not string t) continue;
            if (t is FamilySuperPanel or FamilyAi or FamilyMacros or FamilyLogs) { parent = rb; continue; }
            rb.BringIntoView();   // 具体子页优先：父项在上面，滚它不一定能看到子项
            return;
        }
        parent?.BringIntoView();
    }

    /// <summary>
    /// 二级下拉的展开 / 收起：高度 0↔内容高 + 淡入淡出 + 箭头 0°↔90°。
    /// 同一个 host 用 <see cref="FrameworkElement.Tag"/> 记序号，旧动画的 Completed 不许覆盖新状态。
    /// </summary>
    private void AnimateSubNav(Border host, TextBlock chevron, bool expanded, bool animate)
    {
        int seq = (host.Tag as int? ?? 0) + 1;
        host.Tag = seq;

        if (chevron.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            chevron.RenderTransform = rotate;
        }
        rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        host.BeginAnimation(MaxHeightProperty, null);
        host.BeginAnimation(OpacityProperty, null);

        if (!animate)
        {
            host.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            host.MaxHeight = double.PositiveInfinity;
            host.Opacity = 1;
            rotate.Angle = expanded ? 90 : 0;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (expanded)
        {
            double target = MeasureSubNavHeight(host);
            host.Visibility = Visibility.Visible;
            host.MaxHeight = double.PositiveInfinity;   // 基值：动画清掉后回落到正文高度
            host.Opacity = 1;

            var height = new DoubleAnimation(0, target, SubNavAnimDuration) { EasingFunction = ease };
            height.Completed += (_, _) =>
            {
                if (host.Tag is not int cur || cur != seq) return;
                host.BeginAnimation(MaxHeightProperty, null);   // 交回 Auto，内容变了不会被夹住
                host.BringIntoView();                           // 展开后整组滚进视野
            };
            host.BeginAnimation(MaxHeightProperty, height);
            host.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, SubNavAnimDuration) { EasingFunction = ease });
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 90, SubNavAnimDuration) { EasingFunction = ease });
            return;
        }

        double from = host.ActualHeight > 0 ? host.ActualHeight : MeasureSubNavHeight(host);
        if (from <= 0 || host.Visibility != Visibility.Visible)
        {
            host.Visibility = Visibility.Collapsed;
            host.MaxHeight = double.PositiveInfinity;
            host.Opacity = 1;
            rotate.Angle = 0;
            return;
        }

        host.MaxHeight = double.PositiveInfinity;   // 基值：动画被清掉时不会留下 0 高
        var collapse = new DoubleAnimation(from, 0, SubNavAnimDuration) { EasingFunction = ease };
        collapse.Completed += (_, _) =>
        {
            if (host.Tag is not int cur || cur != seq) return;
            host.BeginAnimation(MaxHeightProperty, null);
            host.BeginAnimation(OpacityProperty, null);
            host.Visibility = Visibility.Collapsed;
        };
        host.BeginAnimation(MaxHeightProperty, collapse);
        host.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, 0, SubNavAnimDuration) { EasingFunction = ease });
        rotate.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(90, 0, SubNavAnimDuration) { EasingFunction = ease });
    }

    /// <summary>展开目标高度：Border 的 padding / border + 内容高度（不含 Margin）。</summary>
    private static double MeasureSubNavHeight(Border host)
    {
        if (host.Child is not FrameworkElement content) return 0;
        double width = host.ActualWidth - host.Padding.Left - host.Padding.Right
                       - host.BorderThickness.Left - host.BorderThickness.Right;
        if (width <= 0) width = 150;
        content.Measure(new Size(width, double.PositiveInfinity));
        return content.DesiredSize.Height + host.Padding.Top + host.Padding.Bottom
               + host.BorderThickness.Top + host.BorderThickness.Bottom;
    }

    /// <summary>
    /// 点亮某个二级分组：父项选中 + 展开，对应子项选中。
    /// 期间置 <see cref="_syncingNav"/>，避免程序改 IsChecked 又触发一次导航。
    /// </summary>
    private void SyncNavGroup(string family, string page)
    {
        _syncingNav = true;
        try
        {
            var parent = NavGroupItem(family);
            if (parent.IsChecked != true) parent.IsChecked = true;

            string want = NavTagOf(family, page);
            foreach (var rb in NavGroupPanel(family).Children.OfType<RadioButton>())
            {
                bool on = rb.Tag is string t && t == want;
                if (rb.IsChecked != on) rb.IsChecked = on;
            }
        }
        finally { _syncingNav = false; }

        // 恢复期间不动展开集合：状态由 NavExpandedGroups 决定（ApplyNavExpandedStates 统一套上）
        // 已经展开的组不重播动画（不然每点一个子页都让左边栏滚一下）
        if (!_restoringNav && !_expandedGroups.Contains(family))
            SetNavGroupExpanded(family, true, animate: true);
    }

    /// <summary>
    /// 清掉某分组里被选中的子项（不触发导航；分组的「上次子页」保留，点回父项时还落在那儿）。
    /// </summary>
    private void ClearSubNav(string family)
    {
        _syncingNav = true;
        try
        {
            foreach (var rb in NavGroupPanel(family).Children.OfType<RadioButton>())
            {
                if (rb.IsChecked == true) rb.IsChecked = false;
            }
        }
        finally { _syncingNav = false; }
    }

    /// <summary>程序化选中某个导航项（含它的父项），不触发导航事件。</summary>
    private void SelectNavItem(string tag)
    {
        string? family = NavFamilyOf(tag);
        _syncingNav = true;
        try
        {
            if (family != null) NavGroupItem(family).IsChecked = true;
            foreach (var rb in AllNavItems())
            {
                if (rb.Tag is string t && t == tag) { rb.IsChecked = true; break; }
            }
        }
        finally { _syncingNav = false; }
    }

    /// <summary>
    /// 只切各页的 Visibility：惰性页设在空宿主 Host_* 上（不建视图、不加载数据），
    /// 内联页仍在 Page_* 上。构造期先调一次让窗口第一帧就有正确的页，ApplyNavPage 再调一次。
    /// </summary>
    private void SetPageVisibility(string tag)
    {
        string? family = NavFamilyOf(tag);
        string page = family == null ? "" : SubPageOf(family, tag);
        bool superPanel = family == FamilySuperPanel;
        bool ai = family == FamilyAi;
        bool macros = family == FamilyMacros;
        bool logs = family == FamilyLogs;

        Page_General.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_Actions.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Host_SubActions.Visibility = tag == "subactions" ? Visibility.Visible : Visibility.Collapsed;
        Host_Hotkeys.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        Host_Macros.Visibility = macros ? Visibility.Visible : Visibility.Collapsed;
        Host_Triggers.Visibility = tag == "triggers" ? Visibility.Visible : Visibility.Collapsed;
        Host_SuperPanel.Visibility = superPanel ? Visibility.Visible : Visibility.Collapsed;
        Page_Terminals.Visibility = tag == "terminals" ? Visibility.Visible : Visibility.Collapsed;
        // 「运行日志」拆成两个同层 ScrollViewer：设置（记录设置 + 日志文件）/ 最近日志
        Page_LogsSettings.Visibility = logs && page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        Page_LogsRecent.Visibility = logs && page == "recent" ? Visibility.Visible : Visibility.Collapsed;
        Host_Ai.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        Page_Backup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNavPage(string tag)
    {
        // 诊断采样：单个 tag 的整次切页（含惰性建页 / Reload / 动画注册）
        using var perfSeg = PerfProbe.Measure("nav:" + tag);

        // 父项（superpanel / ai / macros / logs）落到该分组上次所在的子页
        if (tag is FamilySuperPanel or FamilyAi or FamilyMacros or FamilyLogs) tag = NavTagOf(tag, _lastSubPage[tag]);

        _navApplied = true;   // 显式导航已发生：取消排队中的「启动后恢复」，别把页改回去
        _currentNav = tag;
        string? family = NavFamilyOf(tag);
        string page = family == null ? "" : SubPageOf(family, tag);
        if (family != null) _lastSubPage[family] = page;
        bool superPanel = family == FamilySuperPanel;
        bool ai = family == FamilyAi;
        bool macros = family == FamilyMacros;
        bool logs = family == FamilyLogs;

        SetPageVisibility(tag);

        // 进哪个分组就展开 + 点亮哪个；离开不自动收起（展开状态由用户点父项决定，记进配置）
        if (superPanel) SyncNavGroup(FamilySuperPanel, page);
        if (ai) SyncNavGroup(FamilyAi, page);
        if (macros) SyncNavGroup(FamilyMacros, page);
        if (logs) SyncNavGroup(FamilyLogs, page);
        // 四套子项各有 GroupName（subnav / subnav-ai / subnav-macros / subnav-logs），跨组不会自动互斥：
        // 切到别处时清掉不在当前分组的子项，否则左边栏会同时高亮多项。
        // 「上次子页」保留在 _lastSubPage 里，点回父项时还落在那儿。
        if (!superPanel) ClearSubNav(FamilySuperPanel);
        if (!ai) ClearSubNav(FamilyAi);
        if (!macros) ClearSubNav(FamilyMacros);
        if (!logs) ClearSubNav(FamilyLogs);

        if (tag == "terminals") PopulateTerminals();
        if (logs) PopulateLogging();
        // 只在「最近日志」页轮询 + 自动贴底；切走就停（见 ConfigHelper.Logs.cs）
        OnLogPageChanged(logs && page == "recent");
        if (tag == "general")
        {
            // 开关可能在「面板管理」页被改过，切回来时同步显示
            _initializing = true;
            PopulateToggles();
            PopulateRunNotice();
            PopulateTrayDoubleClick();
            _initializing = false;
        }
        // 记忆当前页 + 展开状态，下次打开恢复（恢复期间不写盘、不播动画）
        if (!_restoringNav) ScheduleNavPersist();

        // 只实例化 + 载入「当前显示的那一页」：其余惰性页一律不碰（B 方案）。
        // 二级子页（superpanel / macros / ai）由 ShowPage 触发创建，首次载入走 RefreshIfNeeded
        // （已建过就跳过，数据真变时由 RefreshCreatedPages 统一 Reload），避免每次切页整表重建。
        if (superPanel) Page_SuperPanel.ShowPage(page);
        if (ai) Page_Ai.ShowPage(page);
        if (macros) Page_Macros.ShowPage(page);
        if (tag == "actions") PopulateActions();
        if (tag == "subactions") Page_SubActions.RefreshIfNeeded();
        if (ai) Page_Ai.RefreshIfNeeded();
        if (tag == "hotkeys") Page_Hotkeys.RefreshIfNeeded();
        if (macros) Page_Macros.RefreshIfNeeded();
        if (tag == "triggers") Page_Triggers.RefreshIfNeeded();
        if (superPanel) Page_SuperPanel.Reload();
        // AI 页的 1 秒状态刷新只在可见时跑；用字段访问，别为没打开过的页强建视图。
        // 放在 ShowPage / RefreshIfNeeded 之后：首次进入时实例刚建好，SetActive(true) 才生效。
        _pageAi?.SetActive(ai);
    }

    /// <summary>记住当前导航页与二级下拉的展开状态（只动 ui.json 的这两个字段）。</summary>
    /// <remarks>
    /// <see cref="UiStore.Modify"/> 会走 <c>ConfigIO.ModifyDomain</c> 的严格读——
    /// <c>AppConfig.LoadStrict</c> 里 <c>ActionStore.LoadAll</c> 会扫描并解析整个 <c>actions/</c>，
    /// 再原子写一次 ui.json，实测约 11ms。低级鼠标钩子和 WPF 共用这一个 UI 线程，
    /// 每次切页都同步做这件事会把光标顶住一下（切到「快捷键」页最明显，因为它还叠加整表重建）。
    /// 所以切页只捕获待写值，300ms 防抖后丢线程池：快速连切合并成一次写，且写不占 UI 线程。
    /// </remarks>
    private void PersistNav() => WriteNav(_pendingNav, _pendingGroups, ConfigPath);

    private static void WriteNav(string nav, List<string> groups, string path) =>
        UiStore.Modify(path, ui =>
        {
            ui.LastNavPage = nav;
            ui.NavExpandedGroups = groups;
        });

    private void ScheduleNavPersist()
    {
        // 诊断采样会反复切页，不能把用户记住的导航页覆盖掉，也不该在采样里混进后台写盘。
        if (PerfProbe.Enabled) return;
        _pendingNav = string.IsNullOrEmpty(_currentNav) ? "actions" : _currentNav;
        _pendingGroups = _expandedGroups.ToList();
        _navDirty = true;
        _navPersistTimer.Stop();
        _navPersistTimer.Start();
    }

    private void FlushNavPersist()
    {
        _navDirty = false;   // 已经进后台写队列，关窗不用再补一次
        string nav = _pendingNav, path = ConfigPath;
        var groups = _pendingGroups;   // 捕获快照，别让 UI 线程后续改动影响这次写
        _ = Task.Run(() =>
        {
            try { WriteNav(nav, groups, path); }
            catch (Exception ex) { AppLog.Warn("Nav", "写 ui.json 失败：" + ex.Message); }
        });
    }

    /// <summary>从面板跳转过来：切到「面板管理 → 场景」并选中指定场景。</summary>
    public void NavigateToPanelScene(string? sceneId)
    {
        if (_pagesReady)
        {
            SelectNavItem("superpanel-scenes");
            ApplyNavPage("superpanel-scenes");
        }
        Page_SuperPanel.SelectScene(sceneId);
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>从面板暂存区跳过来：停在「AI 接入」上次所在的子页（没进过就是连接）。</summary>
    public void NavigateToAi()
    {
        if (_pagesReady)
        {
            string target = NavFamilyOf(_currentNav) == FamilyAi
                ? _currentNav
                : NavTagOf(FamilyAi, _lastSubPage[FamilyAi]);
            SelectNavItem(target);
            ApplyNavPage(target);
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>从托盘「键鼠宏…」跳过来：停在该分组上次停留的子页（首次默认「录制的宏」）。</summary>
    public void NavigateToMacros()
    {
        if (_pagesReady)
        {
            SelectNavItem("macros");
            ApplyNavPage("macros");
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>
    /// 打开设置并跳到指定导航页（双击托盘图标用）。一级页 tag 直接跳；
    /// 父项 tag（<c>superpanel</c> / <c>ai</c> / <c>macros</c> / <c>logs</c>）由 <see cref="ApplyNavPage"/>
    /// 落到该组 <c>_lastSubPage</c> 记住的子页——双击「打开面板管理」「键鼠宏」就停在上次停留的子页。
    /// </summary>
    public void NavigateTo(string tag)
    {
        if (_pagesReady)
        {
            SelectNavItem(tag);
            ApplyNavPage(tag);
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ============ 通用：开关 ============

    /// <summary>把「工具开关」的状态刷到控件（调用方负责 <c>_initializing</c>）。
    /// 超级面板的开关不在这里：它归「面板管理 → 常规 → 行为」。</summary>
    private void PopulateToggles()
    {
        CapsLockModifierBox.IsChecked = AppState.CapsLockModifier;
        EdgeWheelVolumeBox.IsChecked = AppState.EdgeWheelVolume;
        EdgeWheelEdgeCombo.SelectedIndex = (WheelVolumeEdge)AppState.EdgeWheelVolumeEdge switch
        {
            WheelVolumeEdge.top => 1,
            WheelVolumeEdge.both => 2,
            _ => 0,
        };
        ApplyEdgeWheelVisibility();
    }

    /// <summary>边缘滚轮关掉时把「算哪条边」与说明一起收起，只留一行干净的开关。</summary>
    private void ApplyEdgeWheelVisibility()
    {
        var v = EdgeWheelVolumeBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        EdgeWheelEdgeRow.Visibility = v;
        EdgeWheelVolumeHint.Visibility = v;
    }

    private void CapsLockModifierBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.SetCapsLockModifier(CapsLockModifierBox.IsChecked == true);
    }

    private void EdgeWheelVolumeBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        ApplyEdgeWheelVisibility();
        Settings.SetEdgeWheelVolume(EdgeWheelVolumeBox.IsChecked == true);
    }

    private void EdgeWheelEdge_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.SetEdgeWheelVolumeEdge(EdgeWheelEdgeCombo.SelectedIndex switch
        {
            1 => WheelVolumeEdge.top,
            2 => WheelVolumeEdge.both,
            _ => WheelVolumeEdge.bottom,
        });
    }

    private void AutoStartBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool want = AutoStartBox.IsChecked == true;
        bool ok = want ? AutoStartService.Enable() : AutoStartService.Disable();
        if (!ok)
        {
            _initializing = true;
            AutoStartBox.IsChecked = !want;
            _initializing = false;
            ConfirmDialog.Info(this, "开机自启", want ? "启用失败，请检查权限或任务计划服务。" : "禁用失败。");
            return;
        }
        // 重新注册会把 Command 改成当前 exe，旧的「路径已失效」提示随之消失
        ApplyAutoStartState(AutoStartService.Query());
    }

    /// <summary>
    /// 把自启任务状态刷到「通用」页：勾选 = 任务存在；任务指向别的路径时
    /// （多为便携目录被挪走）给出红字说明与「改为当前程序」按钮。
    /// </summary>
    private void ApplyAutoStartState(AutoStartInfo info)
    {
        _initializing = true;
        AutoStartBox.IsChecked = info.TaskExists;
        _initializing = false;

        bool stale = info.State == AutoStartState.Stale;
        AutoStartFixButton.Visibility = stale ? Visibility.Visible : Visibility.Collapsed;
        AutoStartHint.Foreground = (Brush)FindResource(stale ? "DangerBrush" : "HintTextBrush");
        AutoStartHint.Text = !stale
            ? "通过任务计划程序创建 ONLOGON / 最高权限任务，开机无 UAC 弹窗。"
            : info.RegisteredPathExists
                ? $"自启任务指向另一份拷贝 {info.RegisteredPath}，登录时启动的不是这一份。点下面「改为当前程序」统一到这一份（启动时也会自动对齐）。"
                : $"自启任务指向 {info.RegisteredPath}，该文件已不存在（多为挪动了程序目录），登录时不会启动。点下面「改为当前程序」修复（启动时也会自动对齐）。";
    }

    private void AutoStartFix_Click(object sender, RoutedEventArgs e)
    {
        if (!AutoStartService.Enable())
        {
            ConfirmDialog.Info(this, "开机自启", "修复失败，请检查权限或任务计划服务。");
            return;
        }
        ApplyAutoStartState(AutoStartService.Query());
    }

    // ============ 动作管理 ============
    private void PopulateActions()
    {
        int sel = ActionList.SelectedIndex;
        string filter = ActionFilterBox?.Text?.Trim() ?? "";
        int total = ActionRegistry.All.Count;

        // 暂存区入口带上草稿数：AI 写完的东西在这里先看一眼，再决定是否采纳。
        try { OpenStagingButton.Content = $"暂存区（{DraftStore.List().Count}）"; }
        catch { OpenStagingButton.Content = "暂存区"; }

        _loadingList = true;
        _actionRows.Clear();
        foreach (var a in ActionRegistry.All.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            var row = new ActionRow(a, PanelLibrary.DescribeReferences(a.Id));
            if (filter.Length > 0
                && !row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.TypeLabel.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !row.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _actionRows.Add(row);
        }
        _loadingList = false;
        if (sel >= 0 && sel < _actionRows.Count) ActionList.SelectedIndex = sel;
        ActionCountText.Text = filter.Length > 0
            ? $"显示 {_actionRows.Count} / {total} 个动作"
            : $"共 {total} 个动作";
    }

    private void ActionFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingList) return;
        PopulateActions();
    }

    /// <summary>动作管理页顶部「暂存区（N）」：打开面板的暂存区页（草稿不在设置里）。</summary>
    private void OpenStagingPage_Click(object sender, RoutedEventArgs e)
        => Features.SuperPanel.ShowStash();

    private void ActionList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // DataGridCell 在「自己还没有键盘焦点」时会把这一下 MouseDown 标记成已处理，
        // 单元格里 Focusable="False" 的行内图标就收不到点击（HotkeysView 踩过）。
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();
    }

    /// <summary>
    /// 动作行右侧 3 个图标的分发器：按 <c>DataContext</c> 认行（点按钮不会自动选中那一行），
    /// 把它设为选中项后复用既有的编辑 / 复制 / 删除逻辑，别另写一套。
    /// </summary>
    private void ActionRowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not ActionRow row) return;
        ActionList.SelectedItem = row;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "edit": EditAction_Click(sender, e); break;
            case "copy": DuplicateAction_Click(sender, e); break;
            case "delete": DeleteAction_Click(sender, e); break;
        }
    }

    private void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点行内图标不触发编辑
        if (ActionList.SelectedIndex >= 0) EditAction_Click(sender, e);
    }

    private ActionDto? SelectedAction() =>
        ActionList.SelectedIndex >= 0 && ActionList.SelectedIndex < _actionRows.Count
            ? ActionRegistry.FindById(_actionRows[ActionList.SelectedIndex].Id)
            : null;

    private void NewAction_Click(object sender, RoutedEventArgs e)
    {
        var dto = ActionEditor.Show(this, "新建动作", null);
        if (dto == null) return;
        ConfigStore.AddAction(dto);
        PopulateActions();
        // 选中新动作
        for (int i = 0; i < _actionRows.Count; i++)
            if (_actionRows[i].Id == dto.Id) { ActionList.SelectedIndex = i; break; }
        MarkDirty();
    }

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur == null) return;
        var dto = ActionEditor.Show(this, "编辑动作", cur);
        if (dto == null) return;
        dto.Id = cur.Id;   // 保留原 Id（编辑不换 Id）
        ConfigStore.UpdateAction(dto);
        PopulateActions();
        MarkDirty();
    }

    private void DuplicateAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur == null) return;
        var copy = ConfigStore.DuplicateAction(cur.Id);
        if (copy == null) return;
        PopulateActions();
        for (int i = 0; i < _actionRows.Count; i++)
            if (_actionRows[i].Id == copy.Id) { ActionList.SelectedIndex = i; break; }
        MarkDirty();
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var cur = SelectedAction();
        if (cur != null) DeleteAction(cur.Id);
    }

    /// <summary>删除动作：确认后落盘，并清理快捷键 / 面板 / 自动触发 / 悬浮中的引用。</summary>
    private void DeleteAction(string id)
    {
        if (!ConfirmDialog.Confirm(this, "删除动作", ActionRemoval.ConfirmMessage(id), danger: true)) return;

        ConfigStore.RemoveAction(id);   // 内含清快捷键 / 面板 / 触发规则 / 悬浮引用与资源回收
        PopulateActions();
        RefreshCreatedPages();          // 只刷已经建出来的页，别为没打开过的页强建视图
        MarkDirty();
    }

    /// <summary>visual tree 上溯找最近的 <typeparamref name="T"/> 祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>visual tree 下溯找第一个 <typeparamref name="T"/> 后代。</summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var deep = FindDescendant<T>(child);
            if (deep != null) return deep;
        }
        return null;
    }

    // ============ 终端路径 ============

    /// <summary>
    /// 终端路径表格的滚轮：内层 ScrollViewer 还有可滚空间就让它自己滚（内容看得全），滚到头
    /// 才把滚轮交还给外层 Page_Terminals。与 AiView.AuditGrid 同源，但 PathsGrid 有固定 220 高、
    /// 可能真的需要自己滚，所以不设 VerticalScrollBarVisibility=Disabled，也不能无条件转发
    /// （否则超出的行永远看不到）。
    /// </summary>
    private void PathsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (FindDescendant<ScrollViewer>(PathsGrid) is { ScrollableHeight: > 0 } inner)
        {
            bool canScroll = e.Delta < 0
                ? inner.VerticalOffset < inner.ScrollableHeight - 0.5
                : inner.VerticalOffset > 0.5;
            if (canScroll) return;   // 交给内层自己滚，不吞
        }
        e.Handled = true;
        Page_Terminals.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = Page_Terminals,
        });
    }

    private void PopulateTerminals()
    {
        var rows = new List<PathRow>();
        foreach (var key in TerminalLauncher.ResolvableTerminals)
            rows.Add(new PathRow(key, TerminalLauncher.DisplayLabel(key)));

        PathsGrid.ItemsSource = rows;
        foreach (var row in rows) row.Refresh();
    }

    /// <summary>
    /// DataGridCell 在「自己还没有键盘焦点」时会把这一下 MouseDown 标记成已处理（先给自己抢焦点），
    /// 单元格里的输入框 / 「浏览…」第一下就会落空。预览阶段先把单元格焦点给上，它就不再吞。
    /// </summary>
    private void PathsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();
    }

    /// <summary>
    /// 终端路径表双击判定：命中一行、且没点「浏览…」按钮才进「覆盖」编辑。
    /// 抽成纯函数便于无头冒烟直接断言（照 TriggersView.DoubleClickEditRow 的做法）。
    /// </summary>
    internal static bool PathsDoubleClickEditsOverride(Button? hitButton, object? rowDataContext) =>
        hitButton == null && rowDataContext is PathRow;

    /// <summary>双击整行 = 把焦点给这一行的「覆盖」输入框并全选，直接开始改路径。</summary>
    private void PathsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<DataGridRow>(source);
        if (!PathsDoubleClickEditsOverride(FindAncestor<Button>(source), row?.DataContext)) return;
        if (row?.DataContext is not PathRow path) return;

        PathsGrid.SelectedItem = path;
        var col = PathsGrid.Columns.FirstOrDefault(c => Equals(c.Header, "覆盖"));
        if (col?.GetCellContent(path) is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
        e.Handled = true;
    }

    /// <summary>输入框失焦：保存这一行的覆盖（空 = 恢复自动探测）。</summary>
    private void TerminalOverride_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is PathRow row) SaveTerminalRow(row, box.Text);
    }

    /// <summary>回车：同样保存，然后把焦点交出去（不然改了看不到「已保存」）。</summary>
    private void TerminalOverride_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.DataContext is not PathRow row) return;
        e.Handled = true;
        SaveTerminalRow(row, box.Text);
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    /// <summary>挑一个 exe 当这一行的覆盖路径，选完即保存。</summary>
    private void BrowseTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not PathRow row) return;

        var ofd = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = $"选择「{row.Label}」的可执行文件",
            FileName = row.Override.Length > 0 ? row.Override : row.Path,
        };
        if (ofd.ShowDialog(this) != true) return;

        SaveTerminalRow(row, ofd.FileName);
    }

    /// <summary>
    /// 保存一行的覆盖并刷新「实际路径」。和已存的值一样就不写盘——
    /// 绑定的 <c>UpdateSourceTrigger=PropertyChanged</c> 已经把文字写进 <see cref="PathRow.Override"/>，
    /// 所以比较的是**配置里**的值，不是行对象上的值。
    /// </summary>
    private void SaveTerminalRow(PathRow row, string? text)
    {
        string value = (text ?? "").Trim();
        string saved = TerminalLauncher.GetOverride(row.Key) ?? "";

        if (!string.Equals(saved, value, StringComparison.OrdinalIgnoreCase))
        {
            row.Override = value;
            TerminalLauncher.SavePath(row.Key, value);
            ShowStatus($"{row.Label} 路径已保存", ok: true);
        }

        row.Refresh();
    }

    // ============ 运行提示（通用页） ============

    /// <summary>把配置里的提示策略回填到控件（回填期间不触发保存）。只读 settings 域，别整份读配置拖慢切页。</summary>
    private void PopulateRunNotice()
    {
        var cfg = SettingsStore.Read(ConfigPath);

        _loadingRunNotice = true;
        RunNoticeCombo.SelectedIndex = cfg.RunNotice switch
        {
            RunNoticeMode.off => 0,
            RunNoticeMode.always => 2,
            _ => 1,
        };
        ToastPositionCombo.SelectedIndex = cfg.ToastPosition switch
        {
            ToastPosition.bottomLeft => 1,
            ToastPosition.bottomCenter => 2,
            ToastPosition.topRight => 3,
            ToastPosition.topLeft => 4,
            ToastPosition.topCenter => 5,
            _ => 0,
        };
        _loadingRunNotice = false;
    }

    private void RunNotice_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingRunNotice) return;

        SettingsStore.Modify(ConfigPath, s =>
        {
            s.RunNotice = RunNoticeCombo.SelectedIndex switch
            {
                0 => RunNoticeMode.off,
                2 => RunNoticeMode.always,
                _ => RunNoticeMode.onFailure,
            };
            s.ToastPosition = ToastPositionCombo.SelectedIndex switch
            {
                1 => ToastPosition.bottomLeft,
                2 => ToastPosition.bottomCenter,
                3 => ToastPosition.topRight,
                4 => ToastPosition.topLeft,
                5 => ToastPosition.topCenter,
                _ => ToastPosition.bottomRight,
            };
        });
    }

    // ============ 双击托盘图标（通用页） ============

    /// <summary>
    /// 回填「左键双击」下拉。<see cref="TrayDoubleClickAction"/> 显式赋值 0–5，与 XAML 里 6 个
    /// ComboBoxItem 一一对应，故 SelectedIndex 就是枚举值；读 <see cref="AppState.TrayDoubleClick"/>
    /// （双击回调读的同一份运行时缓存），不读整份配置。
    /// </summary>
    private void PopulateTrayDoubleClick()
    {
        _loadingTrayDoubleClick = true;
        int i = (int)AppState.TrayDoubleClick;
        TrayDoubleClickCombo.SelectedIndex =
            i >= 0 && i < TrayDoubleClickCombo.Items.Count ? i : 0;   // 越界/未知值兜底成「打开设置」
        _loadingTrayDoubleClick = false;
    }

    private void TrayDoubleClick_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTrayDoubleClick || _initializing) return;
        int i = TrayDoubleClickCombo.SelectedIndex;
        if (i < 0 || i >= TrayDoubleClickCombo.Items.Count) return;
        // 立即生效（同步 AppState）+ 只写 configs/settings.json，双击不用读盘
        Settings.SetTrayDoubleClick((TrayDoubleClickAction)i);
    }

    // ============ 备份 / 恢复 ============
    private async void BackupExport_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        // 先落盘未保存的编辑，保证导出的是用户当前所见配置
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        bool res = BackupIncludeResourcesBox.IsChecked == true;
        bool notes = BackupIncludeNotesBox.IsChecked == true;

        var dlg = new SaveFileDialog
        {
            Filter = "LTools 备份 (*.ltbak)|*.ltbak",
            FileName = $"LTools-backup-{DateTime.Now:yyyyMMdd-HHmmss}",
            Title = "导出备份",
        };
        if (dlg.ShowDialog(this) != true) return;

        long size;
        try { size = await Task.Run(() => BackupService.EstimateSize(res, notes)); }
        catch (Exception ex)
        {
            ShowStatus("导出失败", ok: false);
            ConfirmDialog.Info(this, "备份", "导出失败: " + ex.Message);
            return;
        }
        if (size > BackupService.ExportWarnBytes
            && !ConfirmDialog.Confirm(this, "导出", $"备份约 {size / 1024.0 / 1024.0:F1} MB，确定导出吗？", okText: "确认导出"))
            return;

        _backupBusy = true;
        BackupStatusText.Text = "正在导出…";
        try
        {
            await Task.Run(() => BackupService.Export(dlg.FileName, res, notes));
            ShowStatus($"已导出到 {dlg.FileName}", ok: true);
            BackupStatusText.Text = "已导出：" + dlg.FileName;
        }
        catch (Exception ex)
        {
            ShowStatus("导出失败", ok: false);
            BackupStatusText.Text = "导出失败";
            ConfirmDialog.Info(this, "备份", "导出失败: " + ex.Message);
        }
        finally { _backupBusy = false; }
    }

    private async void BackupImport_Click(object sender, RoutedEventArgs e)
    {
        if (_backupBusy) return;
        // 先落盘未保存的编辑，避免导入后又被防抖保存覆盖回去
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        var ofd = new OpenFileDialog
        {
            Filter = "LTools 备份 (*.ltbak)|*.ltbak",
            Title = "导入备份",
        };
        if (ofd.ShowDialog(this) != true) return;

        _backupBusy = true;
        BackupStatusText.Text = "正在读取备份…";
        BackupService.ImportPreview? preview = null;
        try
        {
            preview = await Task.Run(() => BackupService.Inspect(ofd.FileName));

            var msg = new StringBuilder();
            msg.AppendLine($"备份包含 {preview.ExecutableCount} 个可执行文件，并将替换当前配置。");
            if (preview.ExecutableCount > 0)
            {
                msg.AppendLine("导入后以下动作可能以管理员权限执行其中脚本：");
                if (preview.ResourceActionRefs.Count == 0)
                    msg.AppendLine("  （未发现引用 resources 的动作）");
                else
                    foreach (var r in preview.ResourceActionRefs) msg.AppendLine("  • " + r);
            }
            msg.Append("仅导入来源可信的备份。继续？");
            if (!ConfirmDialog.Confirm(this, "导入备份", msg.ToString(), okText: "确认导入"))
                return;

            BackupStatusText.Text = "正在导入…";
            await Task.Run(() => BackupService.Import(preview));
            ConfigStore.Reload();
            PopulateActions();
            RefreshCreatedPages();
            // 全局开关在 AppState，启动时从配置读取；导入新配置后重新应用，避免要重启才生效
            var cfg = ConfigStore.LoadConfig();
            AppState.CapsLockModifier = cfg.CapsLockModifier;
            AppState.EdgeWheelVolume = cfg.EdgeWheelVolume;
            AppState.EdgeWheelVolumeEdge = (int)cfg.EdgeWheelVolumeEdge;
            AppState.IsSuperPanelEnabled = cfg.SuperPanel.Enabled;
            AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;
            MacroSettings.Load(cfg);
            PopulateLogging();
            var logCfg = cfg.Logging ?? new LogConfig();
            AppLog.Configure(logCfg.Enabled, logCfg.Level, logCfg.RetentionDays);
            _initializing = true;
            PopulateToggles();
            _initializing = false;
            ShowStatus("导入完成", ok: true);
            BackupStatusText.Text = "导入完成（旧数据已备份为 .bak-时间戳）";
        }
        catch (Exception ex)
        {
            ShowStatus("导入失败", ok: false);
            BackupStatusText.Text = "导入失败";
            ConfirmDialog.Info(this, "备份", "导入失败: " + ex.Message);
        }
        finally
        {
            BackupService.Cleanup(preview);
            _backupBusy = false;
        }
    }

    // ============ 重新加载 / 自动保存 ============
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _saveDebounce.Stop();
        ConfigStore.Reload();
        MacroSettings.Load(ConfigStore.LoadConfig());
        PopulateActions();
        RefreshCreatedPages();
        ShowStatus("已重新加载", ok: true);
    }

    private void MarkDirty()
    {
        StatusText.Text = "保存中…";
        StatusText.ClearValue(TextBlock.ForegroundProperty);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void SaveNow()
    {
        try
        {
            ConfigStore.Save();      // 菜单组 + 动作清单
            // 动作可能刚被改名：热键表里的「动作」列是取名字的快照，让下次切到快捷键页重读一次。
            // 没建出来的页不必管：首次切入时 RefreshIfNeeded 自然会 Reload。
            if (_pageHotkeys != null) _pageHotkeys.Invalidate();
            QuickNote.Refresh();
            ShowStatus("已自动保存", ok: true);
        }
        catch (Exception ex)
        {
            ShowStatus("保存失败", ok: false);
            ConfirmDialog.Info(this, "设置", "保存失败: " + ex.Message);
        }
    }

    private void ShowStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
    }

    // —— 数据模型 ——

    /// <summary>动作列表展示模型。</summary>
    public sealed class ActionRow
    {
        public ActionRow(ActionDto a, string references = "未使用")
        {
            Id = a.Id;
            Name = a.Name;
            TypeLabel = MacroAction.MacroIdOf(a) != null ? "宏" : "动作";
            Glyph = IconCatalog.GetGlyph(a.Icon);
            Summary = ProgramSummary(a);
            References = references;
        }
        public string Id { get; }
        public string Name { get; }
        public string References { get; }
        public string TypeLabel { get; }
        public string Glyph { get; }
        public string Summary { get; }

        /// <summary>动作体：显示步骤总数（含子步骤）；宏动作显示时长与事件数。</summary>
        private static string ProgramSummary(ActionDto a)
        {
            if (MacroAction.SummaryOf(a) is { } macro) return macro;
            int count = a.Program?.Steps.Sum(s => s.SelfAndDescendants().Count()) ?? 0;
            return count == 0 ? "空动作" : $"{count} 步";
        }
    }

    /// <summary>终端路径表的一行：覆盖值 / 实际路径变了要通知界面刷新。</summary>
    private sealed class PathRow : System.ComponentModel.INotifyPropertyChanged
    {
        private string _override;
        private string _path = "";

        public PathRow(string key, string label)
        {
            Key = key;
            Label = label;
            _override = TerminalLauncher.GetOverride(key) ?? "";
        }

        public string Key { get; }

        public string Label { get; }

        /// <summary>配置里的覆盖（界面直接编辑它；空 = 自动探测）。</summary>
        public string Override
        {
            get => _override;
            set
            {
                if (_override == value) return;
                _override = value;
                Raise(nameof(Override));
            }
        }

        /// <summary>当前生效的路径：覆盖优先，否则自动探测。</summary>
        public string Path
        {
            get => _path;
            private set
            {
                if (_path == value) return;
                _path = value;
                Raise(nameof(Path));
            }
        }

        public void Refresh()
        {
            string? live = TerminalLauncher.Resolve(Key);
            string? over = TerminalLauncher.GetOverride(Key);
            bool overBroken = over != null && !string.Equals(over, live, StringComparison.OrdinalIgnoreCase);

            Path = live == null
                ? (overBroken ? "（未检测到；覆盖的路径不存在）" : "（未检测到）")
                : overBroken ? $"{live}（覆盖的路径不存在，已回退）" : live;
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
