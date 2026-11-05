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
    private readonly List<List<string?>> _superPages = new();   // 超级面板槽位（内存编辑态）
    private bool _initializing = true;   // 初始化设 IsChecked 会触发事件，用此标志跳过
    private bool _loadingList;           // 列表重建期间抑制选择事件
    private bool _superDirty;            // 超级面板有未保存改动
    private bool _pagesReady;            // 页面全部构建完成前抑制导航切换
    private bool _backupBusy;            // 备份 / 导入进行中，禁用重复点击
    private bool _loadingLog;            // 日志页控件回填期间抑制保存事件

    // —— 超级面板槽位拖动（设置页内，交换语义，跨卡片=跨页）——
    private int _dragSrcPage = -1, _dragSrcSlot = -1;
    private Point _dragOrigin;
    private bool _dragging;
    private (int page, int slot) _hover = (-1, -1);

    public ConfigHelperWindow(string configPath)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        ConfigStore.Initialize(configPath);
        ConfigPath = configPath;

        GroupList.ItemsSource = _groups;
        ItemList.ItemsSource = _items;
        ActionList.ItemsSource = _actionRows;

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
        foreach (var rb in NavList.Children.OfType<RadioButton>())
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
            SuperPanelBox2.IsChecked = AppState.IsSuperPanelEnabled;
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

        // 超级面板槽位拖动（窗口级接管，捕获后拖出窗口也能收到 Up）
        PreviewMouseMove += OnSlotDragMove;
        PreviewMouseLeftButtonUp += OnSlotDragUp;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _dragging) { CancelSlotDrag(); e.Handled = true; }
        };
    }

    /// <summary>配置文件路径（供显示/调试）。</summary>
    private string ConfigPath { get; }

    // ============ 导航切换 ============
    private void Nav_Changed(object sender, RoutedEventArgs e)
    {
        if (!_pagesReady) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        // 切换页前先落盘未保存改动，避免重建列表时丢弃（如超级面板 800ms 防抖内的编辑）
        if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        ApplyNavPage(tag);
    }

    private void ApplyNavPage(string tag)
    {
        Page_General.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_Actions.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Page_Menus.Visibility = tag == "menus" ? Visibility.Visible : Visibility.Collapsed;
        Page_SuperPanel.Visibility = tag == "superpanel" ? Visibility.Visible : Visibility.Collapsed;
        Page_Terminals.Visibility = tag == "terminals" ? Visibility.Visible : Visibility.Collapsed;
        Page_Logs.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;
        Page_Backup.Visibility = tag == "backup" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "terminals") PopulateTerminals();
        if (tag == "logs") PopulateLogging();
        // 记忆当前导航页，下次打开恢复（ConfigIO 读改写：只动 LastNavPage，余字段不动）
        ConfigIO.Modify(ConfigPath, cfg => cfg.LastNavPage = tag);
        if (tag == "actions") PopulateActions();
        if (tag == "menus") PopulateGroupList();
        if (tag == "superpanel") PopulateSuperPanel();
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
        bool on = (sender == SuperPanelEnabledBox ? SuperPanelEnabledBox.IsChecked : SuperPanelBox2.IsChecked) == true;
        // 两个复选框保持同步
        _initializing = true;
        SuperPanelEnabledBox.IsChecked = on;
        SuperPanelBox2.IsChecked = on;
        _initializing = false;
        Settings.SetSuperPanelEnabled(on);
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
        _loadingList = true;
        _actionRows.Clear();
        foreach (var a in ActionRegistry.All.OrderBy(x => x.Id))
            _actionRows.Add(new ActionRow(a, macroNames));
        _loadingList = false;
        if (sel >= 0 && sel < _actionRows.Count) ActionList.SelectedIndex = sel;
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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

    /// <summary>新建组合动作（动作管理页入口，与超级面板槽位的「新建组合动作」一致走 ActionEditor 分发）。</summary>
    private void NewCompositeAction_Click(object sender, RoutedEventArgs e)
    {
        // 传 composite 草稿（Id 空）→ ActionEditor.Show 走 CompositeActionDialog 新建模式
        var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = ActionType.composite };
        var dto = ActionEditor.Show(this, "新建组合动作", draft);
        if (dto == null) return;
        ConfigStore.AddAction(dto);
        PopulateActions();
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

    /// <summary>行内 hover ✕：删除该行动作（单条）。</summary>
    private void ActionRowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is ActionRow row)
            DeleteAction(row.Id);
    }

    /// <summary>删除动作：确认后落盘，并清理菜单项 / 超级面板槽位中的引用。</summary>
    private void DeleteAction(string id)
    {
        if (!ConfirmDialog.Confirm(this, "删除动作",
            $"确定删除「{ActionRegistry.DisplayName(id)}」吗？\n引用此动作的菜单项 / 超级面板槽位也会被清除。",
            danger: true)) return;

        ConfigStore.RemoveAction(id);
        PopulateActions();
        PopulateGroupList();
        // 清掉超级面板槽位中对该动作的引用
        bool pruned = false;
        foreach (var p in _superPages)
            for (int i = 0; i < p.Count; i++)
                if (p[i] == id) { p[i] = null; pruned = true; }
        if (pruned) { _superDirty = true; BuildSuperPagesUI(); }
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

    /// <summary>在菜单页新建一个组合动作并加入当前菜单组。</summary>
    private void NewCompositeFromMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureGroupForEdit()) return;
        var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = ActionType.composite };
        var dto = ActionEditor.Show(this, "新建组合动作", draft);
        if (dto == null) return;
        ConfigStore.AddAction(dto);
        MenuSystem.AddItem(SelectedSlot, dto.Id);
        PopulateActions();
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

    // —— 菜单组 / 菜单项拖动重排（窗口级鼠标跟踪 + 幽灵跟随 + 源占位 + 目标指示 + 位移动画，§12）——
    // 不走 OLE DoDragDrop（系统 drag image 与自绘幽灵叠加）；节奏与超级面板/组合步骤一致。
    private Point _menuDragOrigin;
    private bool _menuDragging;
    private int _groupDragFrom = -1;              // 组拖动源 index（0-based，对应 1..10 槽）
    private int _itemDragFrom = -1;               // 项拖动源 index
    private ListBoxItem? _menuDragSrcItem;        // 源行（半透明占位）
    private int _groupHover = -1;                 // 组拖动目标行（高亮）

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

    private void BeginMenuDrag(ListBox list, int index, string ghostText)
    {
        _menuDragging = true;
        list.CaptureMouse();
        ListGhostText.Text = ghostText;
        ListGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem c)
        {
            c.Opacity = 0.35;
            _menuDragSrcItem = c;
        }
    }

    private void UpdateListGhost(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, GhostLayer);
        double w = ListGhost.ActualWidth > 0 ? ListGhost.ActualWidth : ListGhost.MinWidth;
        double h = ListGhost.ActualHeight > 0 ? ListGhost.ActualHeight : 30;
        Canvas.SetLeft(ListGhost, rel.X - w / 2);
        Canvas.SetTop(ListGhost, rel.Y - h / 2);
    }

    private void EndMenuDrag()
    {
        if (!_menuDragging) return;
        ListGhost.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (_menuDragSrcItem != null) { _menuDragSrcItem.Opacity = 1; _menuDragSrcItem = null; }
        ClearGroupHover();
        InsertLine.Visibility = Visibility.Collapsed;
        _menuDragging = false;
    }

    // —— 组拖动（交换语义；目标行高亮，不用插入线）——

    private void GroupList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _groupDragFrom = -1;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不启动拖动
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } lbi) return;
        int idx = GroupList.ItemContainerGenerator.IndexFromContainer(lbi);
        if (idx < 0 || MenuSystem.GetGroup(idx + 1) == null) return;   // 空槽不参与拖动
        _groupDragFrom = idx;
        _menuDragOrigin = e.GetPosition(this);
    }

    private void GroupList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_groupDragFrom < 0 || e.LeftButton != MouseButtonState.Pressed) { _groupDragFrom = -1; return; }
        var pos = e.GetPosition(this);
        if (!_menuDragging)
        {
            if (Math.Abs(pos.X - _menuDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _menuDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            BeginMenuDrag(GroupList, _groupDragFrom, MenuSystem.GetGroup(_groupDragFrom + 1)?.Name ?? "");
        }
        UpdateListGhost(pos);
        HighlightGroupTarget(HitTestRow(GroupList, pos));
        e.Handled = true;
    }

    private void GroupList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_menuDragging || _groupDragFrom < 0) { _groupDragFrom = -1; return; }
        int from = _groupDragFrom;
        int to = HitTestRow(GroupList, e.GetPosition(this));
        GroupList.ReleaseMouseCapture();
        EndMenuDrag();
        _groupDragFrom = -1;
        if (to < 0 || to == from) return;
        var old = Views.Controls.DragFx.CaptureByIndex(GroupList);
        if (!MenuSystem.SwapGroups(from + 1, to + 1)) return;
        PopulateGroupList();
        GroupList.SelectedIndex = to;
        Views.Controls.DragFx.AnimateReorderByIndex(GroupList, old,
            i => Views.Controls.DragFx.OldIndexOfSwap(i, from, to));
        MarkDirty();
        e.Handled = true;
    }

    // —— 项拖动（插入语义；目标位置插入线）——

    private void ItemList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _itemDragFrom = -1;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点 ✕ 不启动拖动
        if (MenuSystem.GetGroup(SelectedSlot) == null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } lbi) return;
        int idx = ItemList.ItemContainerGenerator.IndexFromContainer(lbi);
        if (idx < 0) return;
        _itemDragFrom = idx;
        _menuDragOrigin = e.GetPosition(this);
    }

    private void ItemList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_itemDragFrom < 0 || e.LeftButton != MouseButtonState.Pressed) { _itemDragFrom = -1; return; }
        var pos = e.GetPosition(this);
        if (!_menuDragging)
        {
            if (Math.Abs(pos.X - _menuDragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(pos.Y - _menuDragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            var g = MenuSystem.GetGroup(SelectedSlot);
            BeginMenuDrag(ItemList, _itemDragFrom,
                g != null && _itemDragFrom < g.Items.Count ? ActionRegistry.DisplayName(g.Items[_itemDragFrom]) : "");
        }
        UpdateListGhost(pos);
        PositionItemInsertLine(e);
        e.Handled = true;
    }

    private void ItemList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_menuDragging || _itemDragFrom < 0) { _itemDragFrom = -1; return; }
        int from = _itemDragFrom;
        int insertAt = ItemInsertAt(e);        // 0..Count（基于移动前）
        ItemList.ReleaseMouseCapture();
        EndMenuDrag();
        _itemDragFrom = -1;
        if (insertAt < 0) return;
        int to = insertAt > from ? insertAt - 1 : insertAt;   // 移除源后索引前移
        if (to == from) return;
        var old = Views.Controls.DragFx.CaptureByIndex(ItemList);
        if (!MenuSystem.MoveItemTo(SelectedSlot, from, to)) return;
        PopulateItemList();
        ItemList.SelectedIndex = to;
        Views.Controls.DragFx.AnimateReorderByIndex(ItemList, old,
            i => Views.Controls.DragFx.OldIndexOfMove(i, from, to));
        MarkDirty();
        e.Handled = true;
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

    /// <summary>组拖动目标行高亮（交换语义）。</summary>
    private void HighlightGroupTarget(int idx)
    {
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

    /// <summary>项拖动的插入位置（0..Count）：命中行上/下半；列表内空白 → 末尾；列表外 → -1。</summary>
    private int ItemInsertAt(MouseEventArgs e)
    {
        var rel = e.GetPosition(ItemList);
        if (rel.X < 0 || rel.Y < 0 || rel.X > ItemList.ActualWidth || rel.Y > ItemList.ActualHeight)
            return -1;
        int n = MenuSystem.GetGroup(SelectedSlot)?.Items.Count ?? 0;
        if (ItemList.InputHitTest(rel) is DependencyObject d)
        {
            var lbi = FindAncestor<ListBoxItem>(d);
            if (lbi != null)
            {
                int i = ItemList.ItemContainerGenerator.IndexFromContainer(lbi);
                if (i >= 0) return e.GetPosition(lbi).Y < lbi.ActualHeight / 2 ? i : i + 1;
            }
        }
        return n;   // 列表内空白 → 末尾
    }

    /// <summary>把插入线定位到光标所停留行的上/下边缘。</summary>
    private void PositionItemInsertLine(MouseEventArgs e)
    {
        var rel = e.GetPosition(ItemList);
        if (rel.X < 0 || rel.Y < 0 || rel.X > ItemList.ActualWidth || rel.Y > ItemList.ActualHeight)
        { InsertLine.Visibility = Visibility.Collapsed; return; }
        if (ItemList.InputHitTest(rel) is not DependencyObject d) { InsertLine.Visibility = Visibility.Collapsed; return; }
        var lbi = FindAncestor<ListBoxItem>(d);
        if (lbi == null) { InsertLine.Visibility = Visibility.Collapsed; return; }
        bool after = e.GetPosition(lbi).Y >= lbi.ActualHeight / 2;
        var p = lbi.TransformToAncestor(GhostLayer).Transform(
            new Point(0, after ? lbi.ActualHeight : 0));
        Canvas.SetLeft(InsertLine, p.X);
        Canvas.SetTop(InsertLine, p.Y - 1.5);
        InsertLine.Width = lbi.ActualWidth > 0 ? lbi.ActualWidth : ItemList.ActualWidth;
        InsertLine.Visibility = Visibility.Visible;
    }

    // ============ 超级面板 ============
    private void PopulateSuperPanel()
    {
        var cfg = AppConfig.Load(ConfigPath);
        // 内存编辑态：从配置克隆（每页规整为 16 槽 4×4，旧 3×3 数据行映射）
        _superPages.Clear();
        foreach (var p in cfg.SuperPanel.Pages)
            _superPages.Add(Features.SuperPanel.NormalizePage(p));
        _superDirty = false;

        _initializing = true;
        SuperPanelEnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
        SuperPanelBox2.IsChecked = AppState.IsSuperPanelEnabled;
        ThresholdSlider.Value = cfg.SuperPanel.LongPressThresholdMs;
        ThresholdText.Text = $"{(int)ThresholdSlider.Value} ms";
        AppState.SuperPanelThresholdMs = cfg.SuperPanel.LongPressThresholdMs;   // 与运行态同步（防外部改配置漂移）
        _initializing = false;

        BuildSuperPagesUI();
    }

    private void BuildSuperPagesUI()
    {
        _hover = (-1, -1);   // 子元素已清空，悬停引用失效
        SuperPagesPanel.Children.Clear();
        for (int pi = 0; pi < _superPages.Count; pi++)
        {
            int pageIdx = pi;   // for 循环变量在闭包里共享同一引用——循环结束后 pi==Count，
                                // 所有「删除页」闭包都会传越界值。须局部副本隔离。
            var card = new Border
            {
                Style = (Style)FindResource("CardBox"),
                Margin = new Thickness(0, 0, 0, 12),
            };
            var sp = new StackPanel();

            // 页头
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new TextBlock
            {
                Text = $"第 {pageIdx + 1} 页",
                Style = (Style)FindResource("SectionLabel"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.SetValue(Grid.ColumnProperty, 0);
            header.Children.Add(title);
            var delBtn = new Button
            {
                Style = (Style)FindResource("BtnGhostDanger"),
                Content = "删除页",
                MinWidth = 64,
            };
            delBtn.SetValue(Grid.ColumnProperty, 1);
            delBtn.Click += (_, _) => DeletePage(pageIdx);
            header.Children.Add(delBtn);
            sp.Children.Add(header);

            // 4×4 网格
            var grid = new UniformGrid { Rows = 4, Columns = 4, Margin = new Thickness(0, 10, 0, 0) };
            for (int si = 0; si < 16; si++)
            {
                var cell = BuildSlotButton(pageIdx, si);
                grid.Children.Add(cell);
            }
            sp.Children.Add(grid);
            card.Child = sp;
            SuperPagesPanel.Children.Add(card);
        }
    }

    private Button BuildSlotButton(int pageIdx, int slotIdx)
    {
        string? id = _superPages[pageIdx][slotIdx];
        var a = id != null ? ActionRegistry.FindById(id) : null;
        var btn = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Margin = new Thickness(2),
            MinHeight = 78,
            Cursor = Cursors.Hand,
        };
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710",   // add 字形
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = a != null ? a.Name : "(空)",
            Margin = new Thickness(0, 4, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        btn.Content = content;

        // 右键菜单：空格子 = 共享「添加动作」菜单；有动作的格子 = 编辑 / 复制 / 换绑 / 清除
        btn.ContextMenu = BuildSlotMenu(pageIdx, slotIdx);
        btn.PreviewMouseLeftButtonDown += (_, e) => OnSlotDown(pageIdx, slotIdx, e);
        return btn;
    }

    private void PickSlotAction(int pageIdx, int slotIdx)
    {
        string? current = _superPages[pageIdx][slotIdx];
        var picker = new ActionPoolPicker(this, "超级面板槽位 — 选择动作", current) { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        _superPages[pageIdx][slotIdx] = picker.Result;
        _superDirty = true;
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    private void ClearSlot(int pageIdx, int slotIdx)
    {
        _superPages[pageIdx][slotIdx] = null;
        _superDirty = true;
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    /// <summary>新建动作并直接放入指定槽位（共享菜单的「新建/快捷新建」入口）。</summary>
    private void NewActionAt(int pageIdx, int slotIdx, ActionType? presetType)
    {
        ActionDto? dto;
        if (presetType == null)
            dto = ActionEditor.Show(this, "新建动作", null);
        else
        {
            string title = presetType == ActionType.composite ? "新建组合动作" : "新建动作";
            var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = presetType.Value };
            dto = ActionEditor.Show(this, title, draft);
        }
        if (dto == null) return;

        ConfigStore.AddAction(dto);
        _superPages[pageIdx][slotIdx] = dto.Id;
        _superDirty = true;
        PopulateActions();      // 动作管理列表同步刷新
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    /// <summary>编辑槽位引用的动作：走与「动作管理」页编辑同一条路径（Id 不变、原地更新），
    /// 改名 / 改图标后动作列表与本页格子同步刷新。引用悬空（动作已被删）时退化为添加菜单。</summary>
    private void EditSlotAction(int pageIdx, int slotIdx)
    {
        string? id = _superPages[pageIdx][slotIdx];
        var cur = id != null ? ActionRegistry.FindById(id) : null;
        if (cur == null) { ShowSlotMenu(pageIdx, slotIdx); return; }

        var dto = ActionEditor.Show(this, "编辑动作", cur);
        if (dto == null) return;
        dto.Id = cur.Id;           // 编辑不换 Id（与 EditAction_Click 一致）
        ConfigStore.UpdateAction(dto);
        PopulateActions();         // 动作管理列表同步刷新
        BuildSuperPagesUI();       // 本页格子的名称 / 图标同步
        MarkDirty();
    }

    /// <summary>复制槽位动作：副本进动作池，并放进本页第一个空格子；本页已满则只入池并提示。</summary>
    private void DuplicateSlot(int pageIdx, int slotIdx)
    {
        string? id = _superPages[pageIdx][slotIdx];
        if (id == null) return;
        var copy = ConfigStore.DuplicateAction(id);
        if (copy == null) return;

        int empty = _superPages[pageIdx].FindIndex(x => string.IsNullOrEmpty(x));
        if (empty < 0)
        {
            PopulateActions();
            MarkDirty();
            ConfirmDialog.Info(this, "超级面板", "本页已满，副本已加入动作池（可在空格子里选择它）。");
            return;
        }
        _superPages[pageIdx][empty] = copy.Id;
        _superDirty = true;
        PopulateActions();
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    /// <summary>槽位的右键菜单：空格子沿用共享「添加动作」菜单（新建 / 快捷新建 / 从动作池选择）；
    /// 有动作的格子给「编辑… / 复制 / 换绑 / 清除」——「清除」只把动作移出格子，不删除动作本身
    /// （与超级面板里长按菜单的「删除」语义不同，设置页删动作请到「动作管理」页）。</summary>
    private ContextMenu BuildSlotMenu(int pageIdx, int slotIdx)
    {
        if (string.IsNullOrEmpty(_superPages[pageIdx][slotIdx]))
            return SlotMenu.BuildAddMenu(this, slotIdx,
                (s, t) => NewActionAt(pageIdx, s, t),
                s => PickSlotAction(pageIdx, s),
                onClear: null);

        var menu = new ContextMenu();

        // 菜单项 Click 内同步开模态对话框会与「菜单正在关闭」冲突（见 SlotMenu），统一推迟一帧
        void Add(string header, Action invoke)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => Defer(invoke);
            menu.Items.Add(mi);
        }

        Add("编辑…", () => EditSlotAction(pageIdx, slotIdx));
        Add("复制", () => DuplicateSlot(pageIdx, slotIdx));
        menu.Items.Add(new Separator());
        Add("从动作池选择…（换绑）", () => PickSlotAction(pageIdx, slotIdx));
        Add("清除", () => ClearSlot(pageIdx, slotIdx));
        return menu;
    }

    /// <summary>在指定格子中心弹出共享添加菜单（左键短击走此路径，与面板空格一致）。</summary>
    private void ShowSlotMenu(int pageIdx, int slotIdx)
    {
        bool hasAction = !string.IsNullOrEmpty(_superPages[pageIdx][slotIdx]);
        var menu = SlotMenu.BuildAddMenu(this, slotIdx,
            (s, t) => NewActionAt(pageIdx, s, t),
            s => PickSlotAction(pageIdx, s),
            hasAction ? new Action<int>(s => ClearSlot(pageIdx, s)) : null);
        if (GetSlotButton(pageIdx, slotIdx) is UIElement target)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Center;
            menu.IsOpen = true;
        }
    }

    // —— 槽位拖动重排（交换语义，跨卡片=跨页；与面板 §5.6 同思路）——

    private void OnSlotDown(int page, int slot, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragSrcPage = page;
        _dragSrcSlot = slot;
        _dragOrigin = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();          // 捕获到窗口：拖出窗口后 PreviewMouseMove/Up 仍路由到本窗口
        e.Handled = true;        // 吞掉 Button.Click——点击/拖动由释放时位移判定
    }

    private void OnSlotDragMove(object sender, MouseEventArgs e)
    {
        if (_dragSrcPage < 0) return;
        var pos = e.GetPosition(this);

        if (!_dragging)
        {
            double dx = Math.Abs(pos.X - _dragOrigin.X);
            double dy = Math.Abs(pos.Y - _dragOrigin.Y);
            if (dx <= SystemParameters.MinimumHorizontalDragDistance
                && dy <= SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            ShowSlotGhost(_dragSrcPage, _dragSrcSlot);
        }

        UpdateSlotGhost(pos);
        SetHover(HitTestSlot(pos));
        e.Handled = true;
    }

    private void OnSlotDragUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _dragSrcPage < 0) return;
        ReleaseMouseCapture();

        int sp = _dragSrcPage, ss = _dragSrcSlot;
        var target = _hover;
        bool wasDrag = _dragging;
        EndSlotDragVisuals();

        if (wasDrag)
        {
            int dp = target.page, ds = target.slot;
            // 交换：源格 ↔ 目标格（目标无效或同位→取消）
            if (dp >= 0 && ds >= 0 && !(dp == sp && ds == ss))
            {
                bool samePage = dp == sp;
                (_superPages[sp][ss], _superPages[dp][ds]) = (_superPages[dp][ds], _superPages[sp][ss]);
                _superDirty = true;
                BuildSuperPagesUI();
                MarkSuperDirty();
                // 同页交换：两格内容从对方位置滑入（跨页=跨卡片，跳过动画）
                if (samePage)
                {
                    var a = GetSlotButton(sp, ss);
                    var b = GetSlotButton(dp, ds);
                    if (a != null && b != null)
                        Views.Controls.DragFx.AnimateSwap(a, b, this);
                }
            }
        }
        else
        {
            // 位移未超阈值→视为单击：有动作的格子直接编辑该动作；空格子弹出共享添加菜单。
            // （原先无论有没有动作都弹「添加」菜单，想改已配好的格子无从下手）
            Defer(() =>
            {
                if (string.IsNullOrEmpty(_superPages[sp][ss])) ShowSlotMenu(sp, ss);
                else EditSlotAction(sp, ss);
            });
        }
        _dragSrcPage = -1;
        _dragSrcSlot = -1;
        _dragging = false;
        e.Handled = true;
    }

    private void CancelSlotDrag()
    {
        if (_dragSrcPage < 0) return;
        ReleaseMouseCapture();
        EndSlotDragVisuals();
        _dragSrcPage = -1;
        _dragSrcSlot = -1;
        _dragging = false;
    }

    private void Defer(Action action) => Dispatcher.InvokeAsync(action);

    // —— 命中测试 / 目标高亮 ——

    /// <summary>遍历所有页卡片的 4×4 网格，找光标下的槽位（page, slot）；无则 (-1,-1)。</summary>
    private (int page, int slot) HitTestSlot(Point windowPos)
    {
        for (int pi = 0; pi < SuperPagesPanel.Children.Count; pi++)
        {
            if (SuperPagesPanel.Children[pi] is not Border card) continue;
            var grid = FindSlotGrid(card);
            if (grid == null) continue;
            var rel = TranslatePoint(windowPos, grid);
            if (rel.X < 0 || rel.Y < 0 || rel.X > grid.ActualWidth || rel.Y > grid.ActualHeight) continue;
            var hit = grid.InputHitTest(rel) as DependencyObject;
            if (hit == null) continue;
            DependencyObject? d = hit;
            while (d != null && d != grid)
            {
                if (d is Button b)
                {
                    int si = grid.Children.IndexOf(b);
                    if (si >= 0) return (pi, si);
                }
                d = VisualTreeHelper.GetParent(d);
            }
        }
        return (-1, -1);
    }

    private static UniformGrid? FindSlotGrid(Border card)
    {
        if (card.Child is not StackPanel sp) return null;
        foreach (var c in sp.Children) if (c is UniformGrid g) return g;
        return null;
    }

    private Button? GetSlotButton(int page, int slot)
    {
        if (page < 0 || page >= SuperPagesPanel.Children.Count) return null;
        if (SuperPagesPanel.Children[page] is not Border card) return null;
        var grid = FindSlotGrid(card);
        if (grid == null || slot < 0 || slot >= grid.Children.Count) return null;
        return grid.Children[slot] as Button;
    }

    private void SetHover((int page, int slot) h)
    {
        if (h == _hover) return;
        ClearHoverSlot(_hover);
        _hover = h;
        ApplyHoverSlot(_hover);
    }

    private void ApplyHoverSlot((int page, int slot) h)
    {
        if (h.page < 0) return;
        if (GetSlotButton(h.page, h.slot) is Button b)
        {
            b.BorderBrush = (Brush)FindResource("PrimaryBrush");
            b.BorderThickness = new Thickness(2);
        }
    }

    private void ClearHoverSlot((int page, int slot) h)
    {
        if (h.page < 0) return;
        if (GetSlotButton(h.page, h.slot) is Button b)
        {
            b.ClearValue(BorderBrushProperty);     // 还原 BtnGhost 默认（透明边框）
            b.ClearValue(BorderThicknessProperty);
        }
    }

    // —— 幽灵 ——

    private Button? _slotGhostSrc;   // 拖动源槽位（半透明占位，EndSlotDragVisuals 恢复）

    private void ShowSlotGhost(int page, int slot)
    {
        string? id = _superPages[page][slot];
        var a = id != null ? ActionRegistry.FindById(id) : null;
        GhostIcon.Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710";
        GhostName.Text = a != null ? a.Name : "(空)";
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
        // 源半透明占位
        if (GetSlotButton(page, slot) is Button b) { b.Opacity = 0.35; _slotGhostSrc = b; }
    }

    private void UpdateSlotGhost(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, GhostLayer);
        double w = DragGhost.ActualWidth > 0 ? DragGhost.ActualWidth : DragGhost.Width;
        double h = DragGhost.ActualHeight > 0 ? DragGhost.ActualHeight : DragGhost.Height;
        Canvas.SetLeft(DragGhost, rel.X - w / 2);
        Canvas.SetTop(DragGhost, rel.Y - h / 2);
    }

    private void EndSlotDragVisuals()
    {
        DragGhost.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (_slotGhostSrc != null) { _slotGhostSrc.Opacity = 1; _slotGhostSrc = null; }
        SetHover((-1, -1));
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        var page = new List<string?>(16);
        for (int i = 0; i < 16; i++) page.Add(null);
        _superPages.Add(page);
        _superDirty = true;
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    private void DeletePage(int pi)
    {
        if (pi < 0 || pi >= _superPages.Count) return;
        if (!ConfirmDialog.Confirm(this, "删除页", $"确定删除第 {pi + 1} 页吗？", danger: true)) return;
        _superPages.RemoveAt(pi);
        _superDirty = true;
        BuildSuperPagesUI();
        MarkSuperDirty();
    }

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        int v = (int)ThresholdSlider.Value;
        ThresholdText.Text = $"{v} ms";
        AppState.SuperPanelThresholdMs = v;   // 立即生效（手势读运行态值，不等防抖落盘）
        _superDirty = true;
        MarkSuperDirty();
    }

    private void MarkSuperDirty()
    {
        // 超级面板改动走防抖落盘（与 MarkDirty 合并）
        MarkDirty();
    }

    /// <summary>把内存中的超级面板（页 / 槽 / 阈值）写回配置（读磁盘→替换 Pages+阈值→保存）。</summary>
    private void SaveSuperPanel()
    {
        var pages = _superPages.Select(p => new List<string?>(p)).ToList();
        int threshold = (int)ThresholdSlider.Value;
        ConfigIO.Modify(ConfigPath, cfg =>
        {
            cfg.SuperPanel.Pages = pages;
            cfg.SuperPanel.LongPressThresholdMs = threshold;
        });
        _superDirty = false;
    }

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
            // 重置超级面板脏标记（否则旧的内存编辑态会覆盖刚导入的配置）
            _superDirty = false;
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
            SuperPanelBox2.IsChecked = AppState.IsSuperPanelEnabled;
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
            if (_superDirty) SaveSuperPanel();
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
        public ActionRow(ActionDto a, IReadOnlyDictionary<string, string>? macroNames = null)
        {
            Id = a.Id;
            Name = a.Name;
            TypeLabel = ActionTypeLabel.Of(a.Type);
            Glyph = IconCatalog.GetGlyph(a.Icon);
            Summary = Summarize(a, macroNames);
        }
        public string Id { get; }
        public string Name { get; }
        public string TypeLabel { get; }
        public string Glyph { get; }
        public string Summary { get; }

        private static string Summarize(ActionDto a, IReadOnlyDictionary<string, string>? macroNames) => a.Type switch
        {
            ActionType.launchApp => a.Target ?? "",
            ActionType.openFile or ActionType.openFolder => a.Path ?? "",
            ActionType.openUrl => a.Url ?? "",
            ActionType.runCommand => string.IsNullOrEmpty(a.Cmd) ? "" : $"{a.Cmd}",
            ActionType.sendText => BriefText(a.Text),
            ActionType.setClipboard => BriefText(a.Text),
            ActionType.toast => BriefText(a.Text),
            ActionType.window => WindowSummary(a),
            ActionType.clickText => ClickTextSummary(a),
            ActionType.imageClick => ImageClickSummary(a),
            ActionType.sendKeys => a.Items != null ? $"{a.Items.Count} 条输入" : "",
            ActionType.macro => MacroLabel(a.MacroId, macroNames),
            ActionType.@internal => a.Command ?? "",
            ActionType.composite => CompositeSummary(a),
            _ => "",
        };

        /// <summary>组合动作摘要：统计含嵌套在内的节点数（旧 Steps 也能显示）。</summary>
        private static string CompositeSummary(ActionDto a)
        {
            if (a.Flow == null && a.Steps == null) return "";
            int count = CountNodes(FlowMigration.Ensure(a));
            return count > 0 ? $"{count} 个步骤" : "";
        }

        /// <summary>窗口操作摘要：「切到前台 · 记事本」/「改尺寸 800×600 · 记事本」。</summary>
        private static string WindowSummary(ActionDto a)
        {
            WindowActionKind op = a.WindowOp ?? WindowActionKind.activate;
            string target = !string.IsNullOrWhiteSpace(a.WindowTitle) ? a.WindowTitle!.Trim()
                          : !string.IsNullOrWhiteSpace(a.WindowClass) ? a.WindowClass!.Trim()
                          : "?";
            if (op == WindowActionKind.resize && a.WindowRect is { Length: >= 4 } r)
                return $"{WindowActionLabel.Short(op)} {r[2]}×{r[3]} · {target}";
            return $"{WindowActionLabel.Short(op)} · {target}";
        }

        /// <summary>文字点击摘要：「前台窗口 · 保存」。</summary>
        private static string ClickTextSummary(ActionDto a)
        {
            string text = string.IsNullOrWhiteSpace(a.Text) ? "(未填文字)" : a.Text!.Trim();
            string where = (a.Region ?? ImageRegion.foreground) switch
            {
                ImageRegion.screen => "整个屏幕",
                ImageRegion.rect => "固定区域",
                _ => "前台窗口",
            };
            return $"{where} · {text}";
        }

        /// <summary>图片点击摘要：「前台窗口 · save.png」。</summary>
        private static string ImageClickSummary(ActionDto a)
        {
            string image = string.IsNullOrWhiteSpace(a.Image)
                ? "(未选模板图)"
                : System.IO.Path.GetFileName(a.Image!.Trim());
            string where = (a.Region ?? ImageRegion.foreground) switch
            {
                ImageRegion.screen => "整个屏幕",
                ImageRegion.rect => "固定区域",
                _ => "前台窗口",
            };
            return $"{where} · {image}";
        }

        private static int CountNodes(List<FlowNode> nodes)
        {
            int count = 0;
            foreach (var n in nodes)
            {
                count++;
                if (n.If != null)
                {
                    count += CountNodes(n.If.Then);
                    if (n.If.Else != null) count += CountNodes(n.If.Else);
                }
                if (n.Loop != null) count += CountNodes(n.Loop.Body);
            }
            return count;
        }

        private static string MacroLabel(string? id, IReadOnlyDictionary<string, string>? names)
        {
            if (string.IsNullOrWhiteSpace(id)) return "（未选择宏）";
            if (names != null && names.TryGetValue(id!, out var name)) return name;
            return id!;
        }

        /// <summary>文本摘要：首行截断（超长加省略号），供动作列表 Summary 列。</summary>
        private static string BriefText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var oneLine = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')[0];
            return oneLine.Length > 40 ? oneLine[..40] + "…" : oneLine;
        }
    }

    private sealed record PathRow(string Label, string Path);
}
