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
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Features.Migration.Legacy;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>
/// 设置 GUI（阶段 3 重构为左导航 + 多页面）。页面：通用 / 动作管理 / CapsLock快捷菜单 / 超级面板 / 终端路径 / 备份与恢复。
/// 沿用 v2.1.0 防抖自动保存（800ms），扩展到动作 / 菜单 / 超级面板编辑。
/// 两个全局开关（CapsLock 键功能 / 超级面板）即时落盘，重启不丢失。
/// </summary>
public partial class ConfigHelperWindow : Window
{
    private readonly DispatcherTimer _saveDebounce;
    private readonly ObservableCollection<string> _groups = new();
    private readonly ObservableCollection<string> _items = new();
    private readonly ObservableCollection<ActionRow> _actionRows = new();
    private bool _initializing = true;   // 初始化设 IsChecked 会触发事件，用此标志跳过
    private bool _loadingList;           // 列表重建期间抑制选择事件
    private bool _pagesReady;            // 页面全部构建完成前抑制导航切换
    private bool _syncingNav;            // 程序化点亮「面板管理」下拉时不重复导航
    private string _currentNav = "";      // 当前导航 tag（用于父项在子页时再点回主页）
    private bool _backupBusy;            // 备份 / 导入进行中，禁用重复点击
    private bool _loadingLog;            // 日志页控件回填期间抑制保存事件
    private bool _loadingRunNotice;      // 通用页「运行提示」回填期间抑制保存事件

    public ConfigHelperWindow(string configPath)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        ConfigStore.Initialize(configPath);
        ConfigPath = configPath;

        GroupList.ItemsSource = _groups;
        ItemList.ItemsSource = _items;
        ActionList.ItemsSource = _actionRows;

        _groupVisuals = new DragVisuals(this, GhostLayer);
        _itemDrag = new ListDragSort(
            this, GhostLayer, ItemList,
            () => _items.Count,
            MoveItemPreview,
            TakeItemSnapshot,
            RestoreItemSnapshot,
            MarkDirty,
            _ => MenuSystem.GetGroup(SelectedSlot) != null);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !_groupDragging) return;
            EndGroupDrag();
            e.Handled = true;
        };

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveNow(); };

        PopulateActions();
        PopulateGroupList();
        PopulateSuperPanel();
        PopulateTerminals();
        PopulateLogging();

        _pagesReady = true;
        // 恢复上次导航页（§10.x）：存于 AppConfig.LastNavPage，默认「动作管理」
        string lastNav = ConfigStore.LoadConfig().LastNavPage;
        if (string.IsNullOrEmpty(lastNav)) lastNav = "actions";
        bool navMatched = false;
        foreach (var rb in AllNavItems())
        {
            if (rb.Tag is string t && t == lastNav) { rb.IsChecked = true; navMatched = true; break; }
        }
        if (!navMatched) NavActions.IsChecked = true;

        // 初始化完成后再响应用户操作
        Loaded += async (_, _) =>
        {
            _initializing = true;
            // 开关状态
            CapsLockEnabledBox.IsChecked = AppState.IsToolEnabled;
            SuperPanelEnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
            PopulateRunNotice();
            // 自启状态：任务计划 COM 首次激活要百毫秒级，放线程池避免窗口白屏。
            // 查询期间保持 _initializing，免得用户此刻点开关与查询结果互相覆盖。
            var autoStart = await System.Threading.Tasks.Task.Run(AutoStartService.Query);
            _initializing = false;
            ApplyAutoStartState(autoStart);
        };
        Closed += (_, _) =>
        {
            if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        };
    }

    /// <summary>配置文件路径（供显示/调试）。</summary>
    private string ConfigPath { get; }

    // ============ 导航切换 ============
    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_pagesReady || _syncingNav) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        // 切换页前先落盘未保存改动，避免重建列表时丢弃（如超级面板 800ms 防抖内的编辑）
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        ApplyNavPage(tag);
    }

    /// <summary>
    /// 已在某个二级页时再点「面板管理」：父项本来就是选中态，不会再触发 Checked，
    /// 这里把它当作「回到常规主页」。
    /// </summary>
    private void NavSuperPanel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pagesReady || _syncingNav) return;
        if (NavSuperPanel.IsChecked == true &&
            _currentNav is "superpanel-general" or "superpanel-scenes" or "superpanel-blocked")
            ApplyNavPage("superpanel");
    }

    /// <summary>左栏所有导航项（含「面板管理」下拉里的二级项）。</summary>
    private IEnumerable<RadioButton> AllNavItems() =>
        NavList.Children.OfType<RadioButton>()
            .Concat(SuperPanelSubNav.Children.OfType<RadioButton>());

    /// <summary>
    /// 「面板管理」二级下拉：把父项与对应子项一起点亮，箭头朝下、子列表展开。
    /// 期间置 <see cref="_syncingNav"/>，避免程序改 IsChecked 又触发一次导航。
    /// </summary>
    private void SyncSuperPanelNav(string page)
    {
        _syncingNav = true;
        try
        {
            SuperPanelSubNavHost.Visibility = Visibility.Visible;
            NavSuperPanelChevron.Text = "\uE70D";   // ChevronDown
            if (NavSuperPanel.IsChecked != true) NavSuperPanel.IsChecked = true;

            string want = "superpanel-" + page;
            foreach (var rb in SuperPanelSubNav.Children.OfType<RadioButton>())
            {
                bool on = rb.Tag is string t && t == want;
                if (rb.IsChecked != on) rb.IsChecked = on;
            }
        }
        finally { _syncingNav = false; }
    }

    private void ApplyNavPage(string tag)
    {
        _currentNav = tag;
        bool superPanel = tag is "superpanel" or "superpanel-general"
                               or "superpanel-scenes" or "superpanel-blocked";

        Page_General.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_Actions.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Page_SubActions.Visibility = tag == "subactions" ? Visibility.Visible : Visibility.Collapsed;
        Page_Menus.Visibility = tag == "menus" ? Visibility.Visible : Visibility.Collapsed;
        Page_SuperPanel.Visibility = superPanel ? Visibility.Visible : Visibility.Collapsed;
        Page_Terminals.Visibility = tag == "terminals" ? Visibility.Visible : Visibility.Collapsed;
        Page_Logs.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        Page_Backup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;

        if (superPanel)
        {
            // 面板管理主页 = 常规；二级项各自对应一页
            string page = tag switch
            {
                "superpanel-scenes" => "scenes",
                "superpanel-blocked" => "blocked",
                _ => "general",
            };
            SyncSuperPanelNav(page);
            Page_SuperPanel.ShowPage(page);
        }
        else
        {
            // 离开面板管理：收起二级下拉，箭头朝右
            SuperPanelSubNavHost.Visibility = Visibility.Collapsed;
            NavSuperPanelChevron.Text = "\uE76C";   // ChevronRight
        }

        if (tag == "terminals") PopulateTerminals();
        if (tag == "logs") PopulateLogging();
        if (tag == "general")
        {
            // 开关可能在「面板管理」页被改过，切回来时同步显示
            _initializing = true;
            CapsLockEnabledBox.IsChecked = AppState.IsToolEnabled;
            SuperPanelEnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
            PopulateRunNotice();
            _initializing = false;
        }
        // 记忆当前导航页，下次打开恢复（ConfigIO 读改写：只动 LastNavPage，余字段不动）
        ConfigIO.Modify(ConfigPath, cfg => cfg.LastNavPage = tag);
        if (tag == "actions") PopulateActions();
        if (tag == "subactions") Page_SubActions.Reload();
        if (tag == "menus") PopulateGroupList();
        if (superPanel) PopulateSuperPanel();
    }

    /// <summary>从面板跳转过来：切到「面板管理 → 场景」并选中指定场景。</summary>
    public void NavigateToPanelScene(string? sceneId)
    {
        if (_pagesReady)
        {
            _syncingNav = true;
            try
            {
                NavSuperPanel.IsChecked = true;
                foreach (var rb in SuperPanelSubNav.Children.OfType<RadioButton>())
                    if (rb.Tag is string t && t == "superpanel-scenes") rb.IsChecked = true;
            }
            finally { _syncingNav = false; }
            ApplyNavPage("superpanel-scenes");
        }
        Page_SuperPanel.SelectScene(sceneId);
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // ============ 通用：开关 ============
    private void CapsLockEnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool on = CapsLockEnabledBox.IsChecked == true;
        Settings.SetCapsLockEnabled(on);
    }

    private void SuperPanelEnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.SetSuperPanelEnabled(SuperPanelEnabledBox.IsChecked == true);
        Page_SuperPanel.Reload();
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
        var macroNames = MacroStore.NameMap();
        string filter = ActionFilterBox?.Text?.Trim() ?? "";
        int total = ActionRegistry.All.Count;

        _loadingList = true;
        _actionRows.Clear();
        foreach (var a in ActionRegistry.All.OrderBy(x => x.Name, StringComparer.CurrentCulture))
        {
            var row = new ActionRow(a, macroNames, PanelLibrary.DescribeReferences(a.Id));
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

    private void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不触发编辑
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

    /// <summary>删除动作：确认后落盘，并清理菜单项 / 面板入口中的引用。</summary>
    private void DeleteAction(string id)
    {
        if (!ConfirmDialog.Confirm(this, "删除动作",
            $"确定删除「{ActionRegistry.DisplayName(id)}」吗？\n\n面板引用：{PanelLibrary.DescribeReferences(id)}\n菜单项 / 组合步骤中的引用也会一并清除。",
            danger: true)) return;

        ConfigStore.RemoveAction(id);   // 内含清菜单项、组合步骤、面板引用
        PopulateActions();
        PopulateGroupList();
        Page_SuperPanel.Reload();
        MarkDirty();
    }

    // ============ CapsLock快捷菜单 ============
    private int SelectedSlot => GroupList.SelectedIndex + 1;

    private void PopulateGroupList()
    {
        _loadingList = true;
        int sel = GroupList.SelectedIndex;
        _groups.Clear();
        for (int i = 1; i <= MenuSystem.GroupCount; i++)
        {
            var g = MenuSystem.GetGroup(i);
            string seq = (i % 10).ToString();   // 1-9→"1"~"9"，10→"0"
            _groups.Add($"{seq}. {g?.Name ?? "(空)"}");
        }
        if (sel >= 0 && sel < _groups.Count) GroupList.SelectedIndex = sel;
        _loadingList = false;
        PopulateItemList();
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingList) return;
        PopulateItemList();
    }

    private void PopulateItemList()
    {
        _loadingList = true;
        int sel = ItemList.SelectedIndex;
        _items.Clear();
        var g = MenuSystem.GetGroup(SelectedSlot);
        if (g != null)
        {
            for (int i = 0; i < g.Items.Count; i++)
                _items.Add($"{i + 1}. {ActionRegistry.DisplayName(g.Items[i])}");
        }
        if (sel >= 0 && sel < _items.Count) ItemList.SelectedIndex = sel;
        _loadingList = false;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不触发
        // 双击菜单项 → 编辑该菜单项所引用的动作本身（而非换一个动作引用，那是「修改」按钮的职责）
        if (ItemList.SelectedIndex >= 0) EditItemAction_Click(sender, e);
    }

    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        string currentName = g?.Name ?? "";
        var (ok, name) = InputDialog.Show(this, "编辑菜单组", "请输入菜单组名称:", currentName);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        if (g == null)
        {
            int slot = MenuSystem.AddGroup(name.Trim());
            if (slot < 0) { ConfirmDialog.Info(this, "设置", "菜单组已满（最多10组）"); return; }
            PopulateGroupList();
            GroupList.SelectedIndex = slot - 1;
        }
        else
        {
            MenuSystem.EditGroup(SelectedSlot, name.Trim());
            PopulateGroupList();
        }
        MarkDirty();
    }

    /// <summary>行内 hover ✕：删除该菜单组（单条）。</summary>
    private void GroupRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        int idx = RowIndex(GroupList, b);
        if (idx < 0) return;
        int slot = idx + 1;
        if (MenuSystem.GetGroup(slot) == null) return;
        if (!ConfirmDialog.Confirm(this, "删除菜单组", "确定要删除选中的菜单组吗？", danger: true)) return;
        MenuSystem.DeleteGroup(slot);
        PopulateGroupList();
        MarkDirty();
    }

    /// <summary>编辑菜单项前确保有菜单组：当前槽为空则新建「新组」并选中。</summary>
    private bool EnsureGroupForEdit()
    {
        if (MenuSystem.GetGroup(SelectedSlot) != null) return true;
        int slot = MenuSystem.AddGroup("新组");
        if (slot < 0) { ConfirmDialog.Info(this, "设置", "菜单组已满（最多10组）"); return false; }
        PopulateGroupList();
        GroupList.SelectedIndex = slot - 1;
        return true;
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGroupForEdit()) return;
        var picker = new ActionPoolPicker(this, "添加菜单项 — 选择动作") { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        MenuSystem.AddItem(SelectedSlot, picker.Result);
        PopulateItemList();
        var g2 = MenuSystem.GetGroup(SelectedSlot);
        if (g2 != null) ItemList.SelectedIndex = g2.Items.Count - 1;
        MarkDirty();
    }

    /// <summary>在菜单页新建一个动作并加入当前菜单组（无组则先建组）。</summary>
    private void NewActionFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGroupForEdit()) return;
        var dto = ActionEditor.Show(this, "新建动作", null);
        if (dto == null) return;
        ConfigStore.AddAction(dto);
        MenuSystem.AddItem(SelectedSlot, dto.Id);
        PopulateActions();          // 动作池新增了动作，刷新动作管理列表
        PopulateItemList();
        var g = MenuSystem.GetGroup(SelectedSlot);
        if (g != null) ItemList.SelectedIndex = g.Items.Count - 1;
        MarkDirty();
    }

    private void EditItem_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        int idx = ItemList.SelectedIndex;
        if (g == null || idx < 0 || idx >= g.Items.Count) return;
        string currentId = g.Items[idx];
        var picker = new ActionPoolPicker(this, "修改菜单项 — 选择动作", currentId) { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        MenuSystem.EditItem(SelectedSlot, idx, picker.Result);
        PopulateItemList();
        ItemList.SelectedIndex = idx;
        MarkDirty();
    }

    /// <summary>双击菜单项 → 编辑该菜单项所引用的动作本身（打开动作编辑窗口），
    /// 而非「修改菜单项」换一个动作引用（那是 <see cref="EditItem_Click"/> 的职责）。
    /// 引用的动作已被删（引用悬空）时回退到 <see cref="EditItem_Click"/> 重新选一个。</summary>
    private void EditItemAction_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        int idx = ItemList.SelectedIndex;
        if (g == null || idx < 0 || idx >= g.Items.Count) return;
        var cur = ActionRegistry.FindById(g.Items[idx]);
        if (cur == null) { EditItem_Click(sender, e); return; }   // 引用悬空 → 回退换选
        var dto = ActionEditor.Show(this, "编辑动作", cur);
        if (dto == null) return;
        dto.Id = cur.Id;   // 保留原 Id（编辑不换 Id，与 EditAction_Click 一致）
        ConfigStore.UpdateAction(dto);
        PopulateItemList();          // 动作名 / 图标可能改了，刷新菜单项显示
        ItemList.SelectedIndex = idx;
        MarkDirty();
    }

    /// <summary>行内 hover ✕：删除该菜单项（单条）。</summary>
    private void ItemRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSystem.GetGroup(SelectedSlot) == null) return;
        if (sender is not Button b) return;
        int idx = RowIndex(ItemList, b);
        if (idx < 0) return;
        MenuSystem.DeleteItem(SelectedSlot, idx);
        PopulateItemList();
        if (_items.Count > 0) ItemList.SelectedIndex = Math.Min(idx, _items.Count - 1);
        MarkDirty();
    }

    // —— 菜单组 / 菜单项拖动排序 ——
    // 统一规格（照 Quicker）：快照幽灵跟随光标 + 源行虚线占位 + 让位动画 + Esc 取消。
    // 菜单组是固定 1..10 槽位（交换语义）；菜单项是插入语义（实时预览，松手落定）。
    private readonly DragVisuals _groupVisuals;
    private readonly ListDragSort _itemDrag;

    private Point _menuDragOrigin;
    private int _groupDragFrom = -1;              // 组拖动源 index（0-based，对应 1..10 槽）
    private bool _groupDragging;
    private ListBoxItem? _groupSrcRow;
    private FrameworkElement? _groupHole;
    private int _groupHover = -1;                 // 组拖动目标行（高亮）
    private List<string>? _itemSnapshot;          // 菜单项拖动前的顺序（Esc 还原）

    /// <summary>visual tree 上溯找最近的 <typeparamref name="T"/> 祖先（命中 ListBoxItem 用）。</summary>
    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    /// <summary>按钮所在行的索引（供行内 ✕ 删除定位行）。</summary>
    private static int RowIndex(ListBox list, DependencyObject src)
    {
        var lbi = FindAncestor<ListBoxItem>(src);
        return lbi != null ? list.ItemContainerGenerator.IndexFromContainer(lbi) : -1;
    }

    // —— 组拖动（交换语义；源槽虚线占位 + 目标行高亮）——

    private void GroupList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragFrom = -1;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不启动拖动
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } lbi) return;
        int idx = GroupList.ItemContainerGenerator.IndexFromContainer(lbi);
        if (idx < 0 || MenuSystem.GetGroup(idx + 1) == null) return;   // 空槽不参与拖动
        _groupDragFrom = idx;
        _menuDragOrigin = e.GetPosition(this);
        _groupDragging = false;
    }

    private void GroupList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_groupDragFrom < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndGroupDrag(); return; }

        var pos = e.GetPosition(this);
        if (!_groupDragging)
        {
            if (Math.Abs(pos.X - _menuDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _menuDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            if (!StartGroupDrag(pos)) return;
        }

        _groupVisuals.MoveGhost(pos);
        if (_groupHole != null
            && GroupList.ItemContainerGenerator.ContainerFromIndex(_groupDragFrom) is ListBoxItem row)
            DragFx.PlaceOver(GhostLayer, _groupHole, row, this);

        HighlightGroupTarget(HitTestRow(GroupList, pos));
        e.Handled = true;
    }

    private bool StartGroupDrag(Point pos)
    {
        if (GroupList.ItemContainerGenerator.ContainerFromIndex(_groupDragFrom) is not ListBoxItem row)
            return false;

        _groupVisuals.ShowGhost(row, pos);
        _groupHole = DragFx.CreateHole(6);
        GhostLayer.Children.Add(_groupHole);
        row.Opacity = 0;
        _groupSrcRow = row;
        DragFx.PlaceOver(GhostLayer, _groupHole, row, this);
        _groupDragging = true;
        GroupList.CaptureMouse();   // 拖出列表也能收到移动 / 释放
        return true;
    }

    private void GroupList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_groupDragging) { _groupDragFrom = -1; return; }
        int from = _groupDragFrom;
        int to = HitTestRow(GroupList, e.GetPosition(this));
        var old = DragFx.CaptureByIndex(GroupList);
        EndGroupDrag();

        if (to < 0 || to == from) return;
        if (!MenuSystem.SwapGroups(from + 1, to + 1)) return;
        PopulateGroupList();
        GroupList.SelectedIndex = to;
        DragFx.AnimateReorderByIndex(GroupList, old, i => DragFx.OldIndexOfSwap(i, from, to));
        MarkDirty();
        e.Handled = true;
    }

    private void EndGroupDrag()
    {
        if (_groupHole != null) { GhostLayer.Children.Remove(_groupHole); _groupHole = null; }
        _groupVisuals.End();
        GroupList.ReleaseMouseCapture();
        if (_groupSrcRow != null) { _groupSrcRow.Opacity = 1; _groupSrcRow = null; }
        // 列表马上会重建，容器可能被回收复用：逐行复位，避免残留隐形行
        for (int i = 0; i < GroupList.Items.Count; i++)
            if (GroupList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c) c.Opacity = 1;
        ClearGroupHover();
        _groupDragging = false;
        _groupDragFrom = -1;
    }

    // —— 项拖动（插入语义；实时让位，松手落定）——

    private void ItemList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不启动拖动
        if (MenuSystem.GetGroup(SelectedSlot) == null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } lbi) return;
        int idx = ItemList.ItemContainerGenerator.IndexFromContainer(lbi);
        if (idx < 0) return;
        _itemDrag.Press(idx, e.GetPosition(this));
    }

    /// <summary>实时预览：把源项移到插入位置，返回它的新下标（-1 = 没有变化）。</summary>
    private int MoveItemPreview(int from, int insertAt)
    {
        int to = insertAt > from ? insertAt - 1 : insertAt;   // 移除源后索引前移
        if (to == from) return -1;

        var old = DragFx.CaptureByIndex(ItemList);
        if (!MenuSystem.MoveItemTo(SelectedSlot, from, to)) return -1;
        PopulateItemList();
        ItemList.SelectedIndex = to;
        DragFx.AnimateReorderByIndex(ItemList, old, i => DragFx.OldIndexOfMove(i, from, to));
        return to;
    }

    private void TakeItemSnapshot()
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        _itemSnapshot = g != null ? new List<string>(g.Items) : null;
    }

    private void RestoreItemSnapshot()
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        if (g != null && _itemSnapshot != null)
        {
            g.Items.Clear();
            g.Items.AddRange(_itemSnapshot);
        }
        PopulateItemList();
    }

    // —— 菜单拖动命中 / 指示 ——

    /// <summary>光标命中的 ListBox 行 index；列表外或未命中行返回 -1。</summary>
    private int HitTestRow(ListBox list, Point windowPos)
    {
        var rel = TranslatePoint(windowPos, list);
        if (rel.X < 0 || rel.Y < 0 || rel.X > list.ActualWidth || rel.Y > list.ActualHeight) return -1;
        if (list.InputHitTest(rel) is not DependencyObject d) return -1;
        var lbi = FindAncestor<ListBoxItem>(d);
        return lbi != null ? list.ItemContainerGenerator.IndexFromContainer(lbi) : -1;
    }

    /// <summary>组拖动目标行高亮（交换语义）；源行本身不高亮。</summary>
    private void HighlightGroupTarget(int idx)
    {
        if (idx == _groupDragFrom) idx = -1;
        if (idx == _groupHover) return;
        ClearGroupHover();
        _groupHover = idx;
        if (idx >= 0 && GroupList.ItemContainerGenerator.ContainerFromIndex(idx) is ListBoxItem lbi)
            lbi.Background = (Brush)FindResource("SurfaceAltBrush");
    }

    private void ClearGroupHover()
    {
        if (_groupHover >= 0 && GroupList.ItemContainerGenerator.ContainerFromIndex(_groupHover) is ListBoxItem lbi)
            lbi.ClearValue(BackgroundProperty);
        _groupHover = -1;
    }

    // ============ 面板管理 ============
    private void PopulateSuperPanel() => Page_SuperPanel.Reload();

    // ============ 终端路径 ============
    private void PopulateTerminals()
    {
        var rows = new List<PathRow>();
        foreach (var key in TerminalLauncher.ResolvableTerminals)
            rows.Add(new PathRow(TerminalLauncher.DisplayLabel(key), TerminalLauncher.Resolve(key) ?? "（未检测到）"));
        PathsGrid.ItemsSource = rows;
        GitBashBox.Text = TerminalLauncher.GetGitBashPath() ?? "";
    }

    private void BrowseGitBash_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Filter = "bash.exe|bash.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            FileName = "bash.exe",
            Title = "选择 Git Bash 可执行文件",
        };
        if (ofd.ShowDialog(this) == true)
            GitBashBox.Text = ofd.FileName;
    }

    private void SaveGitBash_Click(object sender, RoutedEventArgs e)
    {
        ConfigStore.SaveGitBashPath(GitBashBox.Text.Trim());
        PopulateTerminals();
        TrayService.Notify("终端路径已保存");
        ShowStatus("终端路径已保存", ok: true);
    }

    // ============ 运行提示（通用页） ============

    /// <summary>把配置里的提示策略回填到控件（回填期间不触发保存）。</summary>
    private void PopulateRunNotice()
    {
        var cfg = ConfigStore.LoadConfig();

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

        ConfigIO.Modify(ConfigPath, cfg =>
        {
            cfg.RunNotice = RunNoticeCombo.SelectedIndex switch
            {
                0 => RunNoticeMode.off,
                2 => RunNoticeMode.always,
                _ => RunNoticeMode.onFailure,
            };
            cfg.ToastPosition = ToastPositionCombo.SelectedIndex switch
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

    // ============ 运行日志 ============

    /// <summary>把配置里的日志设置回填到控件（回填期间不触发保存）。</summary>
    private void PopulateLogging()
    {
        var log = ConfigStore.LoadConfig().Logging ?? new LogConfig();

        _loadingLog = true;
        LogEnabledBox.IsChecked = log.Enabled;
        LogLevelCombo.SelectedIndex = log.Level switch
        {
            LogLevel.error => 0,
            LogLevel.debug => 2,
            _ => 1,
        };
        LogRetentionCombo.SelectedIndex = log.RetentionDays switch
        {
            3 => 0,
            7 => 1,
            14 => 2,
            30 => 3,
            0 => 4,
            _ => 2,
        };
        _loadingLog = false;
        RefreshLogPage(log);
    }

    private void LogOption_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingLog) return;

        var cfg = new LogConfig
        {
            Enabled = LogEnabledBox.IsChecked == true,
            Level = LogLevelCombo.SelectedIndex switch
            {
                0 => LogLevel.error,
                2 => LogLevel.debug,
                _ => LogLevel.info,
            },
            RetentionDays = LogRetentionCombo.SelectedIndex switch
            {
                0 => 3,
                1 => 7,
                2 => 14,
                3 => 30,
                _ => 0,
            },
        };

        ConfigIO.Modify(ConfigPath, c => c.Logging = cfg);
        AppLog.Configure(cfg.Enabled, cfg.Level, cfg.RetentionDays);
        if (cfg.Enabled)
            AppLog.Info("Settings", $"日志已更新：级别={cfg.Level}，保留={cfg.RetentionDays} 天");
        RefreshLogPage(cfg);
        ShowStatus(cfg.Enabled ? "日志设置已保存" : "运行日志已关闭", ok: true);
    }

    private void RefreshLogPage(LogConfig log)
    {
        string dir = AppPaths.LogsDir;
        LogPathText.Text = "文件夹：" + dir
                           + "\n今天的日志：" + Path.Combine(dir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
        string level = log.Level switch
        {
            LogLevel.error => "仅错误",
            LogLevel.debug => "详细（含变量值）",
            _ => "常规",
        };
        string keep = log.RetentionDays > 0 ? $"{log.RetentionDays} 天" : "不自动清理";
        LogStatusText.Text = log.Enabled
            ? $"当前：已启用 · 级别 {level} · 保留 {keep}"
            : "当前：已关闭，不会写入新的日志（已有文件保留）。";
        LogLevelHint.Text = log.Level == LogLevel.debug
            ? "详细级别会记录每一步的执行细节与取值结果，可能包含剪贴板 / 选中文本等敏感内容；只在本机 logs 文件夹里。"
            : "常规级别记录动作与流程的执行、设置变更和异常，不记录文本正文；排查具体某一步时再临时改用「详细」。";
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.LogsDir) { UseShellExecute = true });
            ShowStatus("已打开日志文件夹", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "打开日志文件夹失败：" + ex.Message);
        }
    }

    private void OpenTodayLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string file = Path.Combine(AppPaths.LogsDir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(file)) File.WriteAllText(file, "");
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "打开日志文件失败：" + ex.Message);
        }
    }

    private void CopyLogPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AppPaths.LogsDir);
            ShowStatus("日志文件夹路径已复制", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行日志", "复制失败：" + ex.Message);
        }
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
            PopulateGroupList();
            PopulateSuperPanel();
            // 全局开关 / 鼠标速度在 AppState，启动时从配置读取；导入新配置后重新应用，避免要重启才生效
            var cfg = ConfigStore.LoadConfig();
            AppState.IsToolEnabled = cfg.CapsLockEnabled;
            AppState.IsSuperPanelEnabled = cfg.SuperPanel.Enabled;
            AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;
            AppState.MouseModeSpeed = cfg.MouseModeSpeed;
            MacroSettings.Load(cfg);
            PopulateLogging();
            var logCfg = cfg.Logging ?? new LogConfig();
            AppLog.Configure(logCfg.Enabled, logCfg.Level, logCfg.RetentionDays);
            _initializing = true;
            CapsLockEnabledBox.IsChecked = AppState.IsToolEnabled;
            SuperPanelEnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
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
        PopulateGroupList();
        PopulateSuperPanel();
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
        public ActionRow(ActionDto a, IReadOnlyDictionary<string, string>? macroNames = null,
            string references = "未使用")
        {
            Id = a.Id;
            Name = a.Name;
            TypeLabel = a.Program != null ? "动作" : LegacyActionSummary.TypeLabel(a);
            Glyph = IconCatalog.GetGlyph(a.Icon);
            Summary = a.Program != null ? ProgramSummary(a) : LegacyActionSummary.Summarize(a, macroNames);
            References = references;
        }
        public string Id { get; }
        public string Name { get; }
        public string References { get; }
        public string TypeLabel { get; }
        public string Glyph { get; }
        public string Summary { get; }

        /// <summary>v2 动作体：显示步骤总数（含子步骤）。</summary>
        private static string ProgramSummary(ActionDto a)
        {
            int count = a.Program?.Steps.Sum(s => s.SelfAndDescendants().Count()) ?? 0;
            return count == 0 ? "空动作" : $"{count} 步";
        }
    }

    private sealed record PathRow(string Label, string Path);
}
