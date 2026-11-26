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
    // 二级下拉导航的父项（AppConfig.NavExpandedGroups 里也用这些值；五个分组共用一套机制）
    private const string FamilySuperPanel = "superpanel";
    private const string FamilyAi = "ai";
    private const string FamilyLogs = "logs";
    private const string FamilyMacros = "macros";
    private const string FamilyCapture = "capture";

    // 别处改了动作 / 宏（面板删除、录制完成、AI 采纳…）：合批后只刷已建页，避免一次操作反复 Reload
    private readonly DispatcherTimer _refreshDebounce;
    // 导航页记忆：切页只记下待写的值，短防抖后丢线程池写盘（见 ScheduleNavPersist）
    private readonly DispatcherTimer _navPersistTimer;
    private bool _navDirty;                 // 有过切页 / 展开收起才需要关窗时补写一次
    private string _pendingNav = "actions";
    private List<string> _pendingGroups = new();
    private readonly ObservableCollection<ActionRow> _actionRows = new();
    private readonly List<ActionRow> _allActionRows = new();   // 全量行，筛选只做投影，不重新建行
    private string _actionRefFilterKey = ActionReferenceIndex.All;
    private bool _buildingActionRows;                          // 重建下拉时抑制 SelectionChanged 回灌
    private readonly SearchDebounce _actionFilterDebounce;
    private bool _initializing = true;   // 初始化设 IsChecked 会触发事件，用此标志跳过
    private bool _loadingList;           // 列表重建期间抑制选择事件
    private bool _pagesReady;            // 页面全部构建完成前抑制导航切换
    private bool _syncingNav;            // 程序化点亮二级导航时不重复导航
    private bool _restoringNav;          // 恢复上次导航时不写盘、不播动画
    private string _currentNav = "";     // 当前导航 tag（具体子页，如 ai-audit）
    private readonly HashSet<string> _expandedGroups = new();   // 展开着的二级分组（superpanel / ai / macros / logs / capture）
    // 每个二级分组上次所在的子页：点父项切进来时回到这里，而不是永远回第一页
    private readonly Dictionary<string, string> _lastSubPage = new()
    {
        [FamilySuperPanel] = "general",
        [FamilyAi] = "connect",
        [FamilyMacros] = "list",
        [FamilyLogs] = "settings",
        [FamilyCapture] = "general",
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
        // 启动时已经 Initialize / InitializeReadOnly 过（同一锚点）：只有锚点真变了才重做，否则每次
        // 打开设置都白跑一遍扫动作目录 / 修引用 / 播种（实测 200+ms，全压在 UI 线程上）。
        // 宿主里那份是只读的（写权归自己也只是「不修引用、不播种」），所以走 InitializeReadOnly。
        if (!string.Equals(ConfigStore.ConfigPath, configPath, StringComparison.OrdinalIgnoreCase))
        {
            if (AppRole.IsUiHost) ConfigStore.InitializeReadOnly(configPath);
            else ConfigStore.Initialize(configPath);
        }
        ConfigPath = configPath;

        ActionList.ItemsSource = _actionRows;

        // 动作管理：搜索每敲一键只重启 150ms 计时，停手后投影一次（不重新建行 / 读配置）。
        _actionFilterDebounce = new SearchDebounce(150, ApplyActionFilter);

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
            if (_lastSubPage.ContainsKey(g)) _expandedGroups.Add(g);
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
            // 自启状态：任务计划 COM 首次激活 / 一次链路往返都要百毫秒级，放线程池避免窗口白屏。
            // 查询期间保持 _initializing，免得用户此刻点开关与查询结果互相覆盖。
            AutoStartInfo? autoStart = null;
            string? autoStartError = null;
            await System.Threading.Tasks.Task.Run(() => autoStart = AppControl.Query(out autoStartError));
            _initializing = false;
            ApplyAutoStartState(autoStart, autoStartError);
        };
        Closed += (_, _) =>
        {
            // 先退订：窗口关掉后还收通知会对已卸载的控件 Reload，也会一直持有这个窗口
            ConfigStore.Changed -= OnConfigChanged;
            _refreshDebounce.Stop();
            _actionFilterDebounce.Cancel();
            _settingsSearchDebounce?.Cancel();
            // 只在防抖还没落盘时补写一次：用户打开设置什么都没改就关掉，不该覆盖记住的页
            if (_navDirty)
            {
                _navPersistTimer.Stop();
                try { PersistNav(); } catch (Exception ex) { AppLog.Warn("Nav", "退出时写 ui.json 失败：" + ex.Message); }
            }
        };
    }

    /// <summary>配置文件路径（供显示/调试）。</summary>
    private string ConfigPath { get; }

}
