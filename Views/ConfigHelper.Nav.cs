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

/// <summary>ConfigHelperWindow 的「导航切换」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 导航切换 ============
    private static readonly Duration SubNavAnimDuration = new(TimeSpan.FromMilliseconds(170));

    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_pagesReady || _syncingNav) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        ApplyNavPage(tag);
    }

    /// <summary>「面板管理」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavSuperPanel_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilySuperPanel);

    /// <summary>「AI 接入」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavAi_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyAi);

    /// <summary>「键鼠宏」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavMacros_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyMacros);

    /// <summary>「运行日志」父项点击：已在该分组里时 = 展开 / 收起。</summary>
    private void NavLogs_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyLogs);

    /// <summary>「截图 Pro」父项：已选中时 = 展开 / 收起，未选中交给 Checked 进该分组。</summary>
    private void NavCapture_MouseUp(object sender, MouseButtonEventArgs e) =>
        ToggleNavGroup(FamilyCapture);

    /// <summary>
    /// 父项已经选中（正在这个分组里）时，再点它就是展开 / 收起，不换页；
    /// 没选中时不动它，交给 RadioButton 的 Checked → <see cref="ApplyNavPage"/> 进该分组。
    /// </summary>
    /// <remarks>
    /// **这里绝不能 <c>e.Handled = true</c>**（2026-11-13 修「点哪儿都在触发这个导航」）：
    /// 本方法挂在 <c>PreviewMouseLeftButtonUp</c> 上，把 mouseup 吞掉后
    /// <c>ButtonBase.OnMouseLeftButtonUp</c> 不会执行，<c>ReleaseMouseCapture()</c> 与
    /// <c>IsPressed</c> 复位全被跳过，鼠标捕获会卡在这个 RadioButton 上 —— 之后点窗口任意
    /// 位置都被它吃掉，每次都重新跑一遍展开 / 收起。父项已选中时 <c>IsChecked</c> 不变、
    /// <c>Checked</c> 本来就不会再发，所以「不换页」不需要靠吞事件实现。
    /// </remarks>
    private void ToggleNavGroup(string family)
    {
        if (!_pagesReady || _syncingNav) return;
        if (NavGroupItem(family).IsChecked != true) return;
        SetNavGroupExpanded(family, !_expandedGroups.Contains(family), animate: true);
        ScheduleNavPersist();
    }

    /// <summary>左栏所有导航项（含四个二级下拉里的子项）。</summary>
    private IEnumerable<RadioButton> AllNavItems() =>
        NavList.Children.OfType<RadioButton>()
            .Concat(SuperPanelSubNav.Children.OfType<RadioButton>())
            .Concat(AiSubNav.Children.OfType<RadioButton>())
            .Concat(MacrosSubNav.Children.OfType<RadioButton>())
            .Concat(CaptureSubNav.Children.OfType<RadioButton>())
            .Concat(LogsSubNav.Children.OfType<RadioButton>());

    // 五个分组共用一套机制：下面四张表把 family 映射到各自的父项 / 下拉容器 / 箭头，
    // 新增分组只加行，别再写 family == FamilyAi ? A : B 这种二元判断（第三组起就会落到 superpanel 上）。
    private RadioButton NavGroupItem(string family) => family switch
    {
        FamilyAi => NavAi,
        FamilyMacros => NavMacros,
        FamilyLogs => NavLogs,
        FamilyCapture => NavCapture,
        _ => NavSuperPanel,
    };

    private StackPanel NavGroupPanel(string family) => family switch
    {
        FamilyAi => AiSubNav,
        FamilyMacros => MacrosSubNav,
        FamilyLogs => LogsSubNav,
        FamilyCapture => CaptureSubNav,
        _ => SuperPanelSubNav,
    };

    private Border NavGroupHost(string family) => family switch
    {
        FamilyAi => AiSubNavHost,
        FamilyMacros => MacrosSubNavHost,
        FamilyLogs => LogsSubNavHost,
        FamilyCapture => CaptureSubNavHost,
        _ => SuperPanelSubNavHost,
    };

    private TextBlock NavGroupChevron(string family) => family switch
    {
        FamilyAi => NavAiChevron,
        FamilyMacros => NavMacrosChevron,
        FamilyLogs => NavLogsChevron,
        FamilyCapture => NavCaptureChevron,
        _ => NavSuperPanelChevron,
    };

    /// <summary>导航 tag → 所属二级分组（null = 普通一级页）。</summary>
    private static string? NavFamilyOf(string? tag) => tag switch
    {
        "superpanel" or "superpanel-general" or "superpanel-scenes" or "superpanel-blocked" => FamilySuperPanel,
        "ai" or "ai-connect" or "ai-audit" => FamilyAi,
        "macros" or "macros-list" or "macros-settings" => FamilyMacros,
        "capture" or "capture-general" or "capture-behavior" or "capture-drawing"
            or "capture-overlay" or "capture-ocr" or "capture-pin" or "capture-record"
            or "capture-history" or "capture-output" => FamilyCapture,
        "logs" or "logs-settings" or "logs-recent" or "logs-history" => FamilyLogs,
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
        (FamilyLogs, "logs-history") => "history",
        (FamilyCapture, "capture-behavior") => "behavior",
        (FamilyCapture, "capture-drawing") => "drawing",
        (FamilyCapture, "capture-overlay") => "overlay",
        (FamilyCapture, "capture-ocr") => "ocr",
        (FamilyCapture, "capture-pin") => "pin",
        (FamilyCapture, "capture-record") => "record",
        (FamilyCapture, "capture-history") => "history",
        (FamilyCapture, "capture-output") => "output",
        (FamilyCapture, _) => "general",
        _ => "settings",
    };

    private static string NavTagOf(string family, string page) => family switch
    {
        FamilyAi => "ai-" + page,
        FamilyMacros => "macros-" + page,
        FamilyLogs => "logs-" + page,
        FamilyCapture => "capture-" + page,
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
        FamilyCapture => "general",
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

    /// <summary>按 <see cref="_expandedGroups"/> 把所有二级下拉套一遍（启动恢复用，不播动画）。</summary>
    /// <remarks>
    /// 遍历分组表（<see cref="_lastSubPage"/> 的键），**别再在这里手写分组清单**：
    /// 「截图 Pro」当初就是漏在清单外——`_expandedGroups` 记着它、可视状态却一直折叠，
    /// 于是第一下点父项被当成「收起」（什么也看不见），要连点好几次才展开。
    /// </remarks>
    private void ApplyNavExpandedStates(bool animate)
    {
        foreach (string family in _lastSubPage.Keys)
            SetNavGroupExpanded(family, _expandedGroups.Contains(family), animate);
    }

    /// <summary>把当前选中的导航项滚进视野（启动恢复时用：四个下拉都展开会超出可视高度）。</summary>
    private void ScrollCheckedNavIntoView()
    {
        RadioButton? parent = null;
        foreach (var rb in AllNavItems())
        {
            if (rb.IsChecked != true || rb.Tag is not string t) continue;
            if (_lastSubPage.ContainsKey(t)) { parent = rb; continue; }
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
        bool capture = family == FamilyCapture;

        Page_General.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_Actions.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Host_SubActions.Visibility = tag == "subactions" ? Visibility.Visible : Visibility.Collapsed;
        Host_Hotkeys.Visibility = tag == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        // 「截图 Pro」八个同层子页：一个子页一个 ScrollViewer，按 page 切可见性
        Page_CaptureGeneral.Visibility = capture && page == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureBehavior.Visibility = capture && page == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureDrawing.Visibility = capture && page == "drawing" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureOverlay.Visibility = capture && page == "overlay" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureOcr.Visibility = capture && page == "ocr" ? Visibility.Visible : Visibility.Collapsed;
        Page_CapturePin.Visibility = capture && page == "pin" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureRecord.Visibility = capture && page == "record" ? Visibility.Visible : Visibility.Collapsed;
        Page_CaptureHistory.Visibility = capture && page == "history" ? Visibility.Visible : Visibility.Collapsed;
        Page_Output.Visibility = capture && page == "output" ? Visibility.Visible : Visibility.Collapsed;
        Host_Macros.Visibility = macros ? Visibility.Visible : Visibility.Collapsed;
        Host_Triggers.Visibility = tag == "triggers" ? Visibility.Visible : Visibility.Collapsed;
        Host_SuperPanel.Visibility = superPanel ? Visibility.Visible : Visibility.Collapsed;
        Page_Terminals.Visibility = tag == "terminals" ? Visibility.Visible : Visibility.Collapsed;
        // 「运行日志」拆成三个同层页：设置（记录设置 + 日志文件）/ 最近日志 / 运行历史（P2-2）
        Page_LogsSettings.Visibility = logs && page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        Page_LogsRecent.Visibility = logs && page == "recent" ? Visibility.Visible : Visibility.Collapsed;
        Page_LogsHistory.Visibility = logs && page == "history" ? Visibility.Visible : Visibility.Collapsed;
        Host_Ai.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        Page_Backup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyNavPage(string tag)
    {
        // 诊断采样：单个 tag 的整次切页（含惰性建页 / Reload / 动画注册）
        using var perfSeg = PerfProbe.Measure("nav:" + tag);

        // 父项（superpanel / ai / macros / capture / logs）落到该分组上次所在的子页
        if (tag is FamilySuperPanel or FamilyAi or FamilyMacros or FamilyLogs or FamilyCapture)
            tag = NavTagOf(tag, _lastSubPage[tag]);

        _navApplied = true;   // 显式导航已发生：取消排队中的「启动后恢复」，别把页改回去
        _currentNav = tag;
        string? family = NavFamilyOf(tag);
        string page = family == null ? "" : SubPageOf(family, tag);
        if (family != null) _lastSubPage[family] = page;
        bool superPanel = family == FamilySuperPanel;
        bool ai = family == FamilyAi;
        bool macros = family == FamilyMacros;
        bool logs = family == FamilyLogs;
        bool capture = family == FamilyCapture;

        SetPageVisibility(tag);

        // 进哪个分组就展开 + 点亮哪个；离开不自动收起（展开状态由用户点父项决定，记进配置）
        if (superPanel) SyncNavGroup(FamilySuperPanel, page);
        if (ai) SyncNavGroup(FamilyAi, page);
        if (macros) SyncNavGroup(FamilyMacros, page);
        if (logs) SyncNavGroup(FamilyLogs, page);
        if (capture) SyncNavGroup(FamilyCapture, page);
        // 五套子项各有 GroupName（subnav / subnav-ai / subnav-macros / subnav-capture / subnav-logs），
        // 跨组不会自动互斥：切到别处时清掉不在当前分组的子项，否则左边栏会同时高亮多项。
        // 「上次子页」保留在 _lastSubPage 里，点回父项时还落在那儿。
        if (!superPanel) ClearSubNav(FamilySuperPanel);
        if (!ai) ClearSubNav(FamilyAi);
        if (!macros) ClearSubNav(FamilyMacros);
        if (!logs) ClearSubNav(FamilyLogs);
        if (!capture) ClearSubNav(FamilyCapture);

        if (tag == "terminals") PopulateTerminals();
        if (capture && page == "output") PopulateExport();
        if (logs) PopulateLogging();
        // 只有「运行历史」页可见时才读历史文件（500 条 JSONL，别在别的页白读）
        if (logs && page == "history") LoadRunHistory();
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
        else if (capture)
        {
            // 截图 Pro 的选项也可能被覆盖层 / 录屏面板 / 颜色预设窗改过：每次进组同步一遍显示值
            _initializing = true;
            PopulateToggles();
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

}
