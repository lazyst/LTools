using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPro.Core;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>设置 GUI。菜单组/菜单项双列表 + 增删改 + 上下移动；逻辑委托 <see cref="MenuSystem"/>。
/// 使用 ObservableCollection 绑定（不全量重建）；CRUD 后防抖自动保存到配置。</summary>
public partial class ConfigHelperWindow : Window
{
    private readonly string _configPath;
    private readonly ObservableCollection<string> _groups = new();
    private readonly ObservableCollection<string> _items = new();
    private readonly DispatcherTimer _saveDebounce;
    private bool _initializing = true;   // 初始化设 IsChecked 会触发 Checked/Unchecked，用此标志跳过
    private bool _loadingList;           // 列表重建期间抑制选择事件

    public ConfigHelperWindow(string configPath)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _configPath = configPath;
        GroupList.ItemsSource = _groups;
        ItemList.ItemsSource = _items;
        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveNow(); };
        try { MenuSystem.Load(_configPath); } catch { /* 加载失败留空 */ }
        PopulateGroupList();
        // 异步查询开机自启状态（schtasks 是外部进程，同步调用会阻塞 UI 线程导致白屏）
        Loaded += async (_, _) =>
        {
            _initializing = true;
            AutoStartBox.IsChecked = await System.Threading.Tasks.Task.Run(() => AutoStartService.IsEnabled());
            _initializing = false;
        };
        Closed += (_, _) =>
        {
            if (_saveDebounce.IsEnabled) { _saveDebounce.Stop(); SaveNow(); }
        };
    }

    /// <summary>显示槽位 → 组槽位（1..10）。</summary>
    private int SelectedSlot => GroupList.SelectedIndex + 1;

    private void PopulateGroupList()
    {
        _loadingList = true;
        int sel = GroupList.SelectedIndex;
        _groups.Clear();
        for (int i = 1; i <= MenuSystem.GroupCount; i++)
        {
            var g = MenuSystem.GetGroup(i);
            string seq = (i % 10).ToString();   // 1-9→"1"~"9"，10→"0"，对应 CapsLock+1~9,0 选组热键
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
            {
                string id = g.Items[i];
                _items.Add($"{i + 1}. {ActionRegistry.DisplayName(id)}");
            }
        }
        if (sel >= 0 && sel < _items.Count) ItemList.SelectedIndex = sel;
        _loadingList = false;
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void ItemList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedIndex >= 0) EditItem_Click(sender, e);
    }

    // —— 组操作 ——
    private void EditGroup_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        string currentName = g?.Name ?? "";
        var (ok, name) = InputDialog.Show(this, "编辑菜单组", "请输入菜单组名称:", currentName);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        if (g == null)
        {
            // 空槽 → 创建组
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

    // —— 项操作 ——
    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var g = MenuSystem.GetGroup(SelectedSlot);
        if (g == null)
        {
            // 空槽 → 先自动创建组（用默认名），再添加项
            int slot = MenuSystem.AddGroup("新组");
            if (slot < 0) { ConfirmDialog.Info(this, "设置", "菜单组已满（最多10组）"); return; }
            PopulateGroupList();
            GroupList.SelectedIndex = slot - 1;
        }
        string? actionId = MenuItemEditDialog.PickAction(this, "添加菜单项", null);
        if (actionId == null) return;
        MenuSystem.AddItem(SelectedSlot, actionId);
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
        string? actionId = MenuItemEditDialog.PickAction(this, "编辑菜单项", currentId);
        if (actionId == null) return;
        MenuSystem.EditItem(SelectedSlot, idx, actionId);
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

    // —— 自启 / 终端 / 重载 ——
    private void AutoStartBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool want = AutoStartBox.IsChecked == true;
        bool ok = want ? AutoStartService.Enable() : AutoStartService.Disable();
        if (!ok)
        {
            // 操作失败：回滚勾选并提示
            _initializing = true;
            AutoStartBox.IsChecked = !want;
            _initializing = false;
            ConfirmDialog.Info(this, "开机自启", want ? "启用失败，请检查权限或任务计划服务。" : "禁用失败。");
        }
    }

    private void TerminalPaths_Click(object sender, RoutedEventArgs e)
    {
        _saveDebounce.Stop();
        SaveNow(); // 先落盘，避免重载丢弃未保存改动
        TerminalPathsDialog.ShowDialog(this);
        MenuSystem.ReloadFromConfig(_configPath);
        PopulateGroupList();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        _saveDebounce.Stop();
        MenuSystem.ReloadFromConfig(_configPath);
        PopulateGroupList();
        StatusText.Text = "已重新加载";
        StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    // —— 自动保存 ——
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
            MenuSystem.SaveToConfig(_configPath);
            QuickNote.Refresh();
            StatusText.Text = "已自动保存";
            StatusText.Foreground = (Brush)FindResource("SuccessBrush");
        }
        catch (Exception ex)
        {
            StatusText.Text = "保存失败";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            ConfirmDialog.Info(this, "设置", "保存失败: " + ex.Message);
        }
    }
}
