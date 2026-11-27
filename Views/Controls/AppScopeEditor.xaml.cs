using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Config;

namespace LTools.Views.Controls;

/// <summary>
/// 「生效范围」编辑器：一个范围下拉 + 进程 / 标题 / 类名三份名单（都能从系统里点选）。
/// </summary>
/// <remarks>
/// <para>
/// 只有这一份实现，快捷键绑定（<c>HotkeyEditDialog</c>）与预设组（<c>HotkeyGroupDialog</c>）共用：
/// 绑定多一档「跟随全局默认」，组没有（组的默认就是「所有窗口」），所以条目由 <see cref="SetOptions"/> 生成。
/// 取值走 <see cref="SelectedScope"/> / <see cref="SelectedFilter"/>，用哪个档位、怎么组合由调用方决定。
/// </para>
/// <para>
/// 名单是**一行一个输入框 + 每行一个删除**（2026-11-27 从「一个大文本框」改过来）：谁能删、
/// 删掉的是哪一条一眼可见，加一条也不用先学会「每行一个」的约定。三份名单共用
/// <see cref="AddRow"/> / <see cref="Rows"/>，别在这里再写第二套行结构。
/// </para>
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
        Fill(ProcessRows, f.Processes);
        Fill(TitleRows, f.Titles);
        Fill(ClassRows, f.Classes);
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
            Processes = Rows(ProcessRows),
            Titles = Rows(TitleRows),
            Classes = Rows(ClassRows),
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

    // ============ 名单：一行一个输入框 + 删除按钮 ============

    /// <summary>回填一份名单；空名单也留一行空输入框，别让人对着「无」发呆。</summary>
    private static void Fill(StackPanel host, IEnumerable<string> values)
    {
        host.Children.Clear();
        foreach (string value in values)
        {
            if (value.Trim().Length > 0) AddRow(host, value);
        }

        if (host.Children.Count == 0) AddRow(host, "");
    }

    /// <summary>加一行（空输入框 + 删除），返回那个输入框（调用方要设焦点）。</summary>
    /// <remarks>
    /// 输入框右侧的「清除 ×」要关掉：行尾已经有一个删除按钮，两个 × 挨着分不清哪个删什么。
    /// 占位文字取宿主 <see cref="FrameworkElement.Tag"/>（每种名单不一样，如 chrome.exe / 记事本）。
    /// </remarks>
    private static TextBox AddRow(StackPanel host, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            Text = text,
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = host,
        };
        TextBoxClear.SetAttach(box, false);
        if (host.Tag is string hint && hint.Length > 0) TextBoxPlaceholder.SetText(box, hint);
        box.KeyDown += Row_KeyDown;
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var delete = new Button
        {
            Content = "✕",
            ToolTip = "删掉这一行",
            Focusable = false,
            MinWidth = 0,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 0, 0, 0),
        };
        delete.SetResourceReference(StyleProperty, "BtnGhostDanger");
        delete.Click += (_, _) => RemoveRow(host, grid);
        Grid.SetColumn(delete, 1);
        grid.Children.Add(delete);

        host.Children.Add(grid);
        return box;
    }

    /// <summary>删掉一行；删空了就补一行空的，焦点交给相邻那一行。</summary>
    private static void RemoveRow(StackPanel host, Grid row)
    {
        int index = host.Children.IndexOf(row);
        host.Children.Remove(row);

        if (host.Children.Count == 0)
        {
            AddRow(host, "").Focus();
            return;
        }

        int next = Math.Clamp(index, 0, host.Children.Count - 1);
        if (host.Children[next] is Grid grid && grid.Children.Count > 0
            && grid.Children[0] is TextBox box)
        {
            box.Focus();
        }
    }

    /// <summary>Enter = 在下面再加一行（不能让它落到对话框的「确定」上）。</summary>
    private static void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.Tag is not StackPanel host) return;
        e.Handled = true;
        AddRow(host, "").Focus();
    }

    /// <summary>读一份名单：每行的输入框各取一条，空行与首尾空白丢掉。</summary>
    private static List<string> Rows(StackPanel host)
    {
        var values = new List<string>();
        foreach (UIElement child in host.Children)
        {
            if (child is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not TextBox box) continue;
            string text = box.Text.Trim();
            if (text.Length > 0) values.Add(text);
        }

        return values;
    }

    /// <summary>点选器给的那条加一行；已经有了就只把焦点挪过去，不重复加。</summary>
    private static void Append(StackPanel host, string value)
    {
        string text = value.Trim();
        if (text.Length == 0) return;

        foreach (UIElement child in host.Children)
        {
            if (child is not Grid grid || grid.Children.Count == 0 || grid.Children[0] is not TextBox box) continue;
            if (string.Equals(box.Text.Trim(), text, StringComparison.OrdinalIgnoreCase))
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
                return;
            }
        }

        TextBox added = AddRow(host, text);
        added.Focus();
        added.CaretIndex = added.Text.Length;
    }

    // 三份名单都能从系统里点选（与触发规则「作用范围」共用同一套），不用手抄进程名 / 标题 / 类名。
    private void PickProcess_Click(object sender, RoutedEventArgs e)
    {
        string? value = SystemPickerDialog.PickProcess(Window.GetWindow(this));
        if (!string.IsNullOrWhiteSpace(value)) Append(ProcessRows, value!);
    }

    private void PickTitle_Click(object sender, RoutedEventArgs e)
    {
        string? value = SystemPickerDialog.PickWindow(Window.GetWindow(this))?.Title;
        if (!string.IsNullOrWhiteSpace(value)) Append(TitleRows, value!);
    }

    private void PickClass_Click(object sender, RoutedEventArgs e)
    {
        string? value = SystemPickerDialog.PickWindow(Window.GetWindow(this))?.ClassName;
        if (!string.IsNullOrWhiteSpace(value)) Append(ClassRows, value!);
    }

    private void AddProcess_Click(object sender, RoutedEventArgs e) => AddRow(ProcessRows, "").Focus();

    private void AddTitle_Click(object sender, RoutedEventArgs e) => AddRow(TitleRows, "").Focus();

    private void AddClass_Click(object sender, RoutedEventArgs e) => AddRow(ClassRows, "").Focus();
}
