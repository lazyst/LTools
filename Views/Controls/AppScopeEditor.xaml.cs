using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Config;

namespace LTools.Views.Controls;

/// <summary>
/// 「生效范围」编辑器：一个范围下拉 + 进程 / 标题 / 类名三行名单（都能从系统里点选）。
/// </summary>
/// <remarks>
/// 只有这一份实现，快捷键绑定（<c>HotkeyEditDialog</c>）与预设组（<c>HotkeyGroupDialog</c>）共用：
/// 绑定多一档「跟随全局默认」，组没有（组的默认就是「所有窗口」），所以条目由 <see cref="SetOptions"/> 生成。
/// 取值走 <see cref="SelectedScope"/> / <see cref="SelectedFilter"/>，用哪个档位、怎么组合由调用方决定。
/// </remarks>
public partial class AppScopeEditor : UserControl
{
    public AppScopeEditor() => InitializeComponent();

    /// <summary>
    /// 生成范围条目。文案由调用方给：绑定的第一档是「跟随组」，组的第一档是「跟随全局默认」，
    /// 「全部窗口」那一档在绑定里要写明「忽略默认名单」（说了算）。
    /// </summary>
    public void SetOptions(string inheritLabel, string allLabel)
    {
        ScopeBox.Items.Clear();
        Add(inheritLabel, HotkeyScope.inherit);
        Add(allLabel, HotkeyScope.all);
        Add("仅白名单里的程序", HotkeyScope.whitelist);
        Add("排除黑名单里的程序", HotkeyScope.blacklist);
        ScopeBox.SelectedIndex = 0;
        UpdateFilterVisibility();
    }

    /// <summary>回填当前值（名单不在 whitelist / blacklist 时清空，和保存口径一致）。</summary>
    public void Load(HotkeyScope scope, AppFilter? filter)
    {
        int index = IndexOf(scope);
        ScopeBox.SelectedIndex = index >= 0 ? index : 0;

        var f = filter ?? new AppFilter();
        ProcessBox.Text = string.Join(Environment.NewLine, f.Processes);
        TitleBox.Text = string.Join(Environment.NewLine, f.Titles);
        ClassBox.Text = string.Join(Environment.NewLine, f.Classes);
        UpdateFilterVisibility();
    }

    /// <summary>当前选中的范围（Tag 存枚举名，查不到按第一项）。</summary>
    public HotkeyScope SelectedScope
    {
        get
        {
            string tag = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
            return Enum.TryParse<HotkeyScope>(tag, out var scope) ? scope : HotkeyScope.all;
        }
    }

    /// <summary>当前名单：只有 whitelist / blacklist 有值，其它档位返回 null（不写进配置）。</summary>
    public AppFilter? SelectedFilter => SelectedScope is HotkeyScope.whitelist or HotkeyScope.blacklist
        ? new AppFilter
        {
            Processes = Lines(ProcessBox),
            Titles = Lines(TitleBox),
            Classes = Lines(ClassBox),
        }
        : null;

    /// <summary>名单面板显不显示（= 当前档位要不要名单）。</summary>
    public bool FilterVisible => SelectedScope is HotkeyScope.whitelist or HotkeyScope.blacklist;

    private void Add(string label, HotkeyScope scope) =>
        ScopeBox.Items.Add(new ComboBoxItem { Content = label, Tag = scope.ToString() });

    private int IndexOf(HotkeyScope scope)
    {
        for (int i = 0; i < ScopeBox.Items.Count; i++)
        {
            if ((ScopeBox.Items[i] as ComboBoxItem)?.Tag as string == scope.ToString()) return i;
        }
        return -1;
    }

    private void Scope_Changed(object sender, SelectionChangedEventArgs e) => UpdateFilterVisibility();

    private void UpdateFilterVisibility()
    {
        if (FilterPanel == null) return;   // 构造期 SelectionChanged 可能早于字段就绪
        FilterPanel.Visibility = FilterVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    // 三行都能从系统里点选（与触发规则「作用范围」共用同一套），不用手抄进程名 / 标题 / 类名。
    private void PickProcess_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ProcessBox, SystemPickerDialog.PickProcess(Window.GetWindow(this)));

    private void PickTitle_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(TitleBox, SystemPickerDialog.PickWindow(Window.GetWindow(this))?.Title);

    private void PickClass_Click(object sender, RoutedEventArgs e)
        => SystemPickerDialog.AppendLine(ClassBox, SystemPickerDialog.PickWindow(Window.GetWindow(this))?.ClassName);

    private static List<string> Lines(TextBox box) => box.Text
        .Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();
}
