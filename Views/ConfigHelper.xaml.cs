using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPro.Config;
using CapsLockPro.Core;
using CapsLockPro.Features;
using CapsLockPro.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CapsLockPro.Views;

/// <summary>
/// 设置 GUI（阶段 3 重构为左导航 + 多页面）。页面：通用 / 动作管理 / 菜单组 / 超级面板 / 终端路径。
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
            // 自启状态（schtasks 是外部进程，异步查询避免白屏）
            AutoStartBox.IsChecked = await System.Threading.Tasks.Task.Run(() => AutoStartService.IsEnabled());
            _initializing = false;
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
        Page_General.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        Page_Actions.Visibility = tag == "actions" ? Visibility.Visible : Visibility.Collapsed;
        Page_Menus.Visibility = tag == "menus" ? Visibility.Visible : Visibility.Collapsed;
        Page_SuperPanel.Visibility = tag == "superpanel" ? Visibility.Visible : Visibility.Collapsed;
        Page_Terminals.Visibility = tag == "terminals" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "terminals") PopulateTerminals();
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
        }
    }

    // ============ 动作管理 ============
    private void PopulateActions()
    {
        int sel = ActionList.SelectedIndex;
        _loadingList = true;
        _actionRows.Clear();
        foreach (var a in ActionRegistry.All.OrderBy(x => x.Id))
            _actionRows.Add(new ActionRow(a));
        _loadingList = false;
        if (sel >= 0 && sel < _actionRows.Count) ActionList.SelectedIndex = sel;
    }

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ActionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActionList.SelectedIndex >= 0) EditAction_Click(sender, e);
    }

    private ActionDto? SelectedAction() =>
        ActionList.SelectedIndex >= 0 && ActionList.SelectedIndex < _actionRows.Count
            ? ActionRegistry.FindById(_actionRows[ActionList.SelectedIndex].Id)
            : null;

    private void NewAction_Click(object sender, RoutedEventArgs e)
    {
        var dto = ActionEditorDialog.Show(this, "新建动作", null);
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
        var dto = ActionEditorDialog.Show(this, "编辑动作", cur);
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
        if (cur == null) return;
        if (!ConfirmDialog.Confirm(this, "删除动作", $"确定删除「{cur.Name}」吗？\n引用此动作的菜单项 / 超级面板槽位也会被清除。",
            danger: true)) return;
        ConfigStore.RemoveAction(cur.Id);
        PopulateActions();
        PopulateGroupList();
        // 清掉超级面板槽位中对该动作的引用
        bool pruned = false;
        foreach (var p in _superPages)
            for (int i = 0; i < p.Count; i++)
                if (p[i] == cur.Id) { p[i] = null; pruned = true; }
        if (pruned) { _superDirty = true; BuildSuperPagesUI(); }
        MarkDirty();
    }

    // ============ 菜单组 ============
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
        if (ItemList.SelectedIndex >= 0) EditItem_Click(sender, e);
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

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (MenuSystem.GetGroup(SelectedSlot) == null) return;
        if (!ConfirmDialog.Confirm(this, "删除菜单组", "确定要删除选中的菜单组吗？", danger: true)) return;
        MenuSystem.DeleteGroup(SelectedSlot);
        PopulateGroupList();
        MarkDirty();
    }

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        if (g == null)
        {
            int slot = MenuSystem.AddGroup("新组");
            if (slot < 0) { ConfirmDialog.Info(this, "设置", "菜单组已满（最多10组）"); return; }
            PopulateGroupList();
            GroupList.SelectedIndex = slot - 1;
        }
        var picker = new ActionPoolPicker(this, "添加菜单项 — 选择动作") { Owner = this };
        picker.ShowDialog();
        if (picker.Result == null) return;
        MenuSystem.AddItem(SelectedSlot, picker.Result);
        PopulateItemList();
        var g2 = MenuSystem.GetGroup(SelectedSlot);
        if (g2 != null) ItemList.SelectedIndex = g2.Items.Count - 1;
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

    private void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        int idx = ItemList.SelectedIndex;
        if (MenuSystem.GetGroup(SelectedSlot) == null || idx < 0) return;
        MenuSystem.DeleteItem(SelectedSlot, idx);
        PopulateItemList();
        if (_items.Count > 0) ItemList.SelectedIndex = Math.Min(idx, _items.Count - 1);
        MarkDirty();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveItem(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveItem(1);

    private void MoveItem(int delta)
    {
        int idx = ItemList.SelectedIndex;
        if (MenuSystem.GetGroup(SelectedSlot) == null || idx < 0) return;
        if (!MenuSystem.MoveMenuItem(SelectedSlot, idx, delta)) return;
        PopulateItemList();
        ItemList.SelectedIndex = idx + delta;
        MarkDirty();
    }

    // ============ 超级面板 ============
    private void PopulateSuperPanel()
    {
        var cfg = AppConfig.Load(ConfigPath);
        // 内存编辑态：从配置克隆（每页补齐到 9 槽）
        _superPages.Clear();
        foreach (var p in cfg.SuperPanel.Pages)
        {
            var page = new List<string?>(9);
            for (int i = 0; i < 9; i++) page.Add(i < p.Count ? p[i] : null);
            _superPages.Add(page);
        }
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

            // 3×3 网格
            var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(0, 10, 0, 0) };
            for (int si = 0; si < 9; si++)
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

        // 右键菜单（共享菜单 §5.5/§5.6 统一）：新建/快捷新建/从动作池选择/清除
        btn.ContextMenu = SlotMenu.BuildAddMenu(this, slotIdx,
            (s, t) => NewActionAt(pageIdx, s, t),
            s => PickSlotAction(pageIdx, s),
            string.IsNullOrEmpty(id) ? null : new Action<int>(s => ClearSlot(pageIdx, s)));
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
            dto = ActionEditorDialog.Show(this, "新建动作", null);
        else
        {
            string title = presetType == ActionType.composite ? "新建组合动作" : "新建动作";
            var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = presetType.Value };
            dto = ActionEditorDialog.Show(this, title, draft);
        }
        if (dto == null) return;

        ConfigStore.AddAction(dto);
        _superPages[pageIdx][slotIdx] = dto.Id;
        _superDirty = true;
        PopulateActions();      // 动作管理列表同步刷新
        BuildSuperPagesUI();
        MarkSuperDirty();
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
                (_superPages[sp][ss], _superPages[dp][ds]) = (_superPages[dp][ds], _superPages[sp][ss]);
                _superDirty = true;
                BuildSuperPagesUI();
                MarkSuperDirty();
            }
        }
        else
        {
            // 位移未超阈值→视为单击：弹出共享添加菜单（与面板空格左键一致）
            Defer(() => ShowSlotMenu(sp, ss));
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

    /// <summary>遍历所有页卡片的 3×3 网格，找光标下的槽位（page, slot）；无则 (-1,-1)。</summary>
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

    private void ShowSlotGhost(int page, int slot)
    {
        string? id = _superPages[page][slot];
        var a = id != null ? ActionRegistry.FindById(id) : null;
        GhostIcon.Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710";
        GhostName.Text = a != null ? a.Name : "(空)";
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
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
        SetHover((-1, -1));
    }

    private void AddPage_Click(object sender, RoutedEventArgs e)
    {
        var page = new List<string?>(9);
        for (int i = 0; i < 9; i++) page.Add(null);
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

    // ============ 重新加载 / 自动保存 ============
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _saveDebounce.Stop();
        ConfigStore.Reload();
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
        public ActionRow(ActionDto a)
        {
            Id = a.Id;
            Name = a.Name;
            TypeLabel = ActionTypeLabel.Of(a.Type);
            Glyph = IconCatalog.GetGlyph(a.Icon);
            Summary = Summarize(a);
        }
        public string Id { get; }
        public string Name { get; }
        public string TypeLabel { get; }
        public string Glyph { get; }
        public string Summary { get; }

        private static string Summarize(ActionDto a) => a.Type switch
        {
            ActionType.launchApp => a.Target ?? "",
            ActionType.openFile or ActionType.openFolder => a.Path ?? "",
            ActionType.openUrl => a.Url ?? "",
            ActionType.runCommand => string.IsNullOrEmpty(a.Cmd) ? "" : $"{a.Cmd}",
            ActionType.@internal => a.Command ?? "",
            ActionType.composite => a.Steps != null ? $"{a.Steps.Count} 个步骤" : "",
            _ => "",
        };
    }

    private sealed record PathRow(string Label, string Path);
}
