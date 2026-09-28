using System.Windows;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>菜单项添加/编辑对话框：从全局动作池选一个动作 Id 引用（计划 §6）。
/// 阶段 3 将由 ActionEditorDialog 取代完整的动作创建流程；此对话框仅负责"选引用"。</summary>
public partial class MenuItemEditDialog : Window
{
    private bool _ok;

    /// <summary>选中的动作 Id（确定时非空）。</summary>
    public string SelectedActionId { get; private set; } = "";

    public MenuItemEditDialog(string title, string? preselectId)
    {
        InitializeComponent();
        Title = title;

        // 从全局动作清单填充下拉（名称 + 类型标记）
        foreach (var a in ActionRegistry.All)
            ActionCombo.Items.Add(new ActionListItem(a.Id, a.Name, a.Type));

        if (preselectId != null)
        {
            for (int i = 0; i < ActionCombo.Items.Count; i++)
                if (((ActionListItem)ActionCombo.Items[i]).Id == preselectId)
                {
                    ActionCombo.SelectedIndex = i;
                    break;
                }
        }

        Loaded += (_, _) => ActionCombo.Focus();
    }

    /// <summary>弹出模态选择框；返回选中的动作 Id，取消返回 null。</summary>
    public static string? PickAction(Window? owner, string title, string? preselectId)
    {
        var dlg = new MenuItemEditDialog(title, preselectId);
        if (owner != null) dlg.Owner = owner;
        dlg.ShowDialog();
        return dlg._ok ? dlg.SelectedActionId : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ActionCombo.SelectedItem is not ActionListItem item) return;
        SelectedActionId = item.Id;
        _ok = true;
        DialogResult = true;
    }

    /// <summary>下拉项：显示「名称 [类型]」，携带 Id。</summary>
    public sealed class ActionListItem
    {
        public string Id { get; }
        public string Name { get; }
        public ActionType Type { get; }
        public ActionListItem(string id, string name, ActionType type) { Id = id; Name = name; Type = type; }
        public override string ToString() => $"{Name}  [{Type}]";
    }
}
