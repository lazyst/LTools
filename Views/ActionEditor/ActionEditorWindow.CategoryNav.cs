using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 分类导航：工具箱左缘的一列小图标，只做**定位**（把分类标题滚到视图顶部）。
/// </summary>
/// <remarks>
/// <para>
/// 第一项是「全部分类」= 一直以来的平铺页，点它回顶部；其余项与左栏分类组同序
/// （数据全在 <see cref="CategoryNav"/>，从注册表派生，新增动作单元不用改这里）。
/// **不切页、不筛选、不落盘**：收藏 / 最近使用 / 分类折叠 / 拼音搜索 / 拖入插入一律照旧。
/// </para>
/// <para>
/// 参照 <c>Views/SuperPanel.Build.cs</c> 的 <c>ScrollToGroup</c> / <c>UpdateZonePad</c> /
/// <c>SyncRail</c> 三个已验证做法：末尾留白让最后一个分类也能滚到顶部、刚重建完只重试一次、
/// 滚动时按「视口顶部所在的分类」同步高亮。这里刻意**不排队动画**（定位是即时的），
/// 也不无条件 <c>BeginInvoke</c> 重试——内容装得下时会把自己无限排回队列、堵死 UI 线程。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private const string CategoryNavActiveTag = "Active";
    private const double CategoryNavTolerance = 8;

    /// <summary>图标条当前项（纯数据，与 <see cref="_categoryNavButtons"/> 下标一致）。</summary>
    private List<CategoryNavEntry> _categoryNavEntries = new();

    /// <summary>图标按钮，下标 0 = 全部分类，i = 第 i−1 个分类。</summary>
    private readonly List<Button> _categoryNavButtons = new();

    /// <summary>左栏里每个分类的标题元素（定位锚点），顺序与分类组一致。</summary>
    private readonly List<(string Key, FrameworkElement Element)> _categoryAnchors = new();

    /// <summary>末尾留白：让最后一个分类的标题也能滚到视图顶部。搜索 / 单分类时不加。</summary>
    private Border? _categoryPad;

    private int _categoryNavActive = -1;

    /// <summary>搜索态点分类图标：先清搜索、等左栏重建完再定位（−1 = 没有待办）。</summary>
    private int _pendingCategoryNav = -1;

    /// <summary>「等布局再滚」的一次性计时器（关窗要停，别去碰已经关掉的窗口）。</summary>
    private DispatcherTimer? _categoryScrollRetry;

    // ============ 构建 ============

    /// <summary>
    /// <c>BuildToolbox</c> 收尾：加末尾留白 → 重建图标条 → 量一次高度 → 同步高亮。
    /// 搜索态清搜索后的定位也在这里接上（那时锚点才是完整的分类列表）。
    /// </summary>
    private void FinishToolboxBuild(bool searching)
    {
        if (searching || _categoryAnchors.Count < 2)
        {
            _categoryPad = null;
        }
        else
        {
            _categoryPad = new Border { Height = 0 };
            Toolbox.Children.Add(_categoryPad);
        }

        BuildCategoryNav();
        Toolbox.UpdateLayout();
        UpdateCategoryPad();
        SyncCategoryNav();

        if (_pendingCategoryNav > 0)
        {
            int target = _pendingCategoryNav;
            _pendingCategoryNav = -1;
            LocateCategory(target);
        }
    }

    /// <summary>重建图标条（每次 <c>BuildToolbox</c> 都重建：分类组换了，锚点也换了）。</summary>
    private void BuildCategoryNav()
    {
        _categoryNavButtons.Clear();
        _categoryNavEntries = CategoryNav.Entries();
        if (CategoryNavHost == null) return;   // 资源没接好时不拖垮编辑动作

        CategoryNavHost.Children.Clear();
        for (int i = 0; i < _categoryNavEntries.Count; i++)
        {
            int index = i;
            CategoryNavEntry entry = _categoryNavEntries[i];
            var button = new Button
            {
                Style = (Style)FindResource("CategoryNavBtn"),
                Content = entry.Glyph,
                Tag = i == 0 ? CategoryNavActiveTag : null,
                ToolTip = entry.IsAll
                    ? $"{entry.Label}\n全部动作单元（平铺页，回顶部）"
                    : $"{entry.Label}\n定位到「{entry.Label}」",
            };
            AutomationProperties.SetName(button, entry.Label);
            button.Click += (_, _) => OnCategoryNavClicked(index);
            CategoryNavHost.Children.Add(button);
            _categoryNavButtons.Add(button);

            // 「全部分类」和具体分类之间一道细分隔线（不进按钮列表，不影响下标）
            if (i == 0)
                CategoryNavHost.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 3, 8, 3),
                    Background = (Brush)FindResource("BorderBrush"),
                });
        }

        _categoryNavActive = 0;
    }

    // ============ 点击 / 定位 ============

    private void OnCategoryNavClicked(int index)
    {
        if (index <= 0)
        {
            ToolboxScroll.ScrollToTop();
            SetCategoryNavActive(0);
            return;
        }
        if (index >= _categoryNavEntries.Count) return;

        // 搜索时分类位置会变，锚点没有意义：先清搜索（Now 立刻重建，不走 150ms 防抖），
        // FinishToolboxBuild 里再定位到这个分类。
        if (!string.IsNullOrEmpty(SearchBox?.Text))
        {
            _pendingCategoryNav = index;
            SearchBox!.Text = "";
            _toolboxSearchDebounce.Now();
            return;
        }

        LocateCategory(index);
    }

    /// <summary>把第 <paramref name="index"/> 个分类的标题滚到视图顶部。</summary>
    private void LocateCategory(int index, bool allowRetry = true)
    {
        if (index <= 0 || index >= _categoryNavEntries.Count) return;
        string key = _categoryNavEntries[index].Key;

        FrameworkElement? anchor = null;
        foreach (var (k, element) in _categoryAnchors)
            if (string.Equals(k, key, StringComparison.Ordinal)) { anchor = element; break; }
        if (anchor == null) return;   // 这个分类这次没出现（搜索已清，正常不会走到）

        // 先把末尾留白算好再读可滚距离：否则最后一个分类永远滚不到顶部。
        UpdateCategoryPad();
        ToolboxScroll.UpdateLayout();

        double target;
        try { target = anchor.TranslatePoint(new Point(0, 0), Toolbox).Y - 2; }
        catch (InvalidOperationException) { return; }
        double max = Math.Max(0, ToolboxScroll.ScrollableHeight);

        // 刚重建 / 窗口刚显示，高度还没量好：只重试一次，别无限排队把 UI 线程堵死。
        if (target > 0.5 && max <= 0.5)
        {
            if (!allowRetry || _categoryScrollRetry != null)
            {
                // 内容本来就装得下（没有可滚距离）：不用滚，但高亮仍要跟上这次点击。
                SetCategoryNavActive(index);
                return;
            }
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _categoryScrollRetry = retry;
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                if (ReferenceEquals(_categoryScrollRetry, retry)) _categoryScrollRetry = null;
                LocateCategory(index, allowRetry: false);
            };
            retry.Start();
            return;
        }

        ToolboxScroll.ScrollToVerticalOffset(Math.Clamp(target, 0, max));
        SetCategoryNavActive(index);
    }

    // ============ 滚动同步 / 末尾留白 ============

    private void ToolboxScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => SyncCategoryNav();

    private void ToolboxScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCategoryPad();
        SyncCategoryNav();
    }

    /// <summary>高亮「视口顶部所在的分类」；只是「你在哪一段」，不改内容、不写盘。</summary>
    private void SyncCategoryNav()
    {
        if (_categoryNavButtons.Count == 0) return;

        // 搜索时分类位置会变，锚点没有意义：一律停在「全部分类」。
        if (!string.IsNullOrEmpty(SearchBox?.Text)) { SetCategoryNavActive(0); return; }

        // 窗口还没量好 / 正在重建（锚点数与图标数对不上）：别把过渡态当成用户滚到的分类。
        if (ToolboxScroll == null || ToolboxScroll.ViewportHeight <= 0 ||
            _categoryAnchors.Count == 0 || _categoryNavButtons.Count != _categoryAnchors.Count + 1)
        {
            SetCategoryNavActive(0);
            return;
        }

        var tops = new List<double>(_categoryAnchors.Count);
        foreach (var (_, element) in _categoryAnchors)
        {
            try { tops.Add(element.TranslatePoint(new Point(0, 0), Toolbox).Y); }
            catch (InvalidOperationException) { return; }
        }

        // 判定只有一处（CategoryNav.ActiveIndex 是纯函数，--selftest 直接断言定位语义）。
        int active = CategoryNav.ActiveIndex(tops, ToolboxScroll.VerticalOffset,
            ToolboxScroll.ScrollableHeight, CategoryNavTolerance);
        SetCategoryNavActive(Math.Clamp(active, 0, _categoryNavButtons.Count - 1));
    }

    private void SetCategoryNavActive(int index)
    {
        if (index < 0 || index >= _categoryNavButtons.Count || _categoryNavActive == index) return;
        _categoryNavActive = index;
        for (int i = 0; i < _categoryNavButtons.Count; i++)
            _categoryNavButtons[i].Tag = i == index ? CategoryNavActiveTag : null;
    }

    /// <summary>
    /// 末尾留白：高度 = 视口高 − 最后一块的高，让最后一个分类也能把标题滚到视图顶部。
    /// 直接按「去掉留白后的内容高」算一次到位，不做增量反馈（不会来回抖）；单分类 / 搜索时不加。
    /// </summary>
    private void UpdateCategoryPad()
    {
        Border? pad = _categoryPad;
        if (pad == null) return;
        if (_categoryAnchors.Count < 2) { pad.Height = 0; return; }

        Toolbox.UpdateLayout();
        ToolboxScroll.UpdateLayout();
        if (ToolboxScroll.ViewportHeight <= 0) return;

        double contentWithoutPad = ToolboxScroll.ExtentHeight - pad.Height;
        double lastTop;
        try { lastTop = _categoryAnchors[^1].Element.TranslatePoint(new Point(0, 0), Toolbox).Y; }
        catch (InvalidOperationException) { return; }

        double want = CategoryNav.PadHeight(ToolboxScroll.ViewportHeight, contentWithoutPad, lastTop);
        if (Math.Abs(want - pad.Height) > 0.5) pad.Height = want;
    }

    /// <summary>关窗时收尾：停掉「等布局再滚」的一次性计时器。</summary>
    private void StopCategoryNavTimers()
    {
        _categoryScrollRetry?.Stop();
        _categoryScrollRetry = null;
        _pendingCategoryNav = -1;
    }
}
