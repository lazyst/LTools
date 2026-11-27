using System.Windows;
using LTools.Config;

namespace LTools.Views;

/// <summary>
/// 编辑一个预设组的「组生效范围」：组内绑定的默认生效范围（绑定自己设了就以绑定为准）。
/// </summary>
/// <remarks>
/// 只是 <see cref="Controls.AppScopeEditor"/> 的一层壳：组的第一档是「跟随全局默认」，
/// 和绑定的「跟随组」串成 绑定 &gt; 组 &gt; 全局默认 的优先级。
/// </remarks>
public partial class HotkeyGroupDialog : Window
{
    private HotkeyGroupDialog(HotkeyGroup group)
    {
        InitializeComponent();
        Header.Text = $"「{group.Name}」的组生效范围";
        Editor.SetOptions("跟随全局默认", "所有窗口");
        Editor.Load(group.Scope, group.Filter);
    }

    /// <summary>编辑一个组的生效范围；取消返回 null。</summary>
    public static (HotkeyScope Scope, AppFilter? Filter)? Show(Window? owner, HotkeyGroup group)
    {
        var dlg = new HotkeyGroupDialog(group);
        if (owner != null) { dlg.Owner = owner; dlg.Topmost = owner.Topmost; }
        return dlg.ShowDialog() == true ? (dlg.Editor.SelectedScope, dlg.Editor.SelectedFilter) : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
