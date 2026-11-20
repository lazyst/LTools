using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 悬浮工具条的布局编辑器（照上游 <c>floating_toolbar/layout</c>）：左边一叠一行、
/// 右边是收起来的功能。
/// </summary>
/// <remarks>
/// 只编辑「哪些功能、分几叠、什么顺序、哪些收起来」；叠内顺序 = 展开后二级工具条的顺序。
/// 保存时走 <see cref="FloatingToolbarLayout.Normalize"/>（未知引用丢掉、重复只算一次），
/// 所以这份编辑结果可以直接落盘。
/// </remarks>
public partial class FloatingToolbarLayoutWindow : Window
{
    private readonly List<List<string>> _stacks = new();
    private readonly List<string> _hidden = new();

    internal FloatingToolbarLayoutWindow(FloatingToolbarLayout layout)
    {
        InitializeComponent();

        foreach (var stack in layout.Positions) _stacks.Add(stack.ToList());
        _hidden.AddRange(layout.Hidden);

        Rebuild();
    }

    /// <summary>打开编辑器；返回 null = 取消，否则是新的布局。</summary>
    internal static FloatingToolbarLayout? Show(Window? owner, FloatingToolbarLayout layout)
    {
        var window = new FloatingToolbarLayoutWindow(layout)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? false,
        };

        return window.ShowDialog() == true ? window.Result : null;
    }

    /// <summary>按下「保存」时的布局（<c>null</c> = 还没保存）。</summary>
    internal FloatingToolbarLayout? Result { get; private set; }

    private void Rebuild()
    {
        int selectedStack = StackList.SelectedIndex;
        int selectedHidden = HiddenList.SelectedIndex;

        StackList.Items.Clear();
        for (int i = 0; i < _stacks.Count; i++)
        {
            string names = string.Join(" · ", _stacks[i].Select(FloatingToolbarLayout.LabelOf));
            StackList.Items.Add(new ListBoxItem
            {
                Content = $"{i + 1}. {names}",
                Tag = i,
            });
        }

        HiddenList.Items.Clear();
        foreach (string id in _hidden)
            HiddenList.Items.Add(new ListBoxItem { Content = FloatingToolbarLayout.LabelOf(id), Tag = id });

        if (_stacks.Count > 0)
            StackList.SelectedIndex = Math.Clamp(selectedStack, 0, _stacks.Count - 1);
        if (_hidden.Count > 0)
            HiddenList.SelectedIndex = Math.Clamp(selectedHidden, 0, _hidden.Count - 1);

        int visible = _stacks.Sum(s => s.Count);
        PreviewText.Text = $"工具条上 {visible} 个按钮，{_stacks.Count} 叠；收起 {_hidden.Count} 个。";
        HintText.Text = "";
    }

    private string? SelectedHidden =>
        (HiddenList.SelectedItem as ListBoxItem)?.Tag as string;

    private void AddToStack_Click(object sender, RoutedEventArgs e)
    {
        string? id = SelectedHidden;
        if (id == null) { HintText.Text = "先在右边选一个收起来的功能。"; return; }

        if (StackList.SelectedIndex >= 0 && StackList.SelectedIndex < _stacks.Count)
            _stacks[StackList.SelectedIndex].Add(id);
        else
            _stacks.Add(new List<string> { id });

        _hidden.Remove(id);
        Rebuild();
    }

    private void NewStack_Click(object sender, RoutedEventArgs e)
    {
        string? id = SelectedHidden;
        if (id == null) { HintText.Text = "先在右边选一个收起来的功能。"; return; }

        _stacks.Add(new List<string> { id });
        _hidden.Remove(id);
        Rebuild();
        StackList.SelectedIndex = _stacks.Count - 1;
    }

    private void RemoveStack_Click(object sender, RoutedEventArgs e)
    {
        if (StackList.SelectedIndex < 0 || StackList.SelectedIndex >= _stacks.Count)
        {
            HintText.Text = "先在左边选一叠。";
            return;
        }

        _hidden.AddRange(_stacks[StackList.SelectedIndex]);
        _stacks.RemoveAt(StackList.SelectedIndex);
        Rebuild();
    }

    private void Up_Click(object sender, RoutedEventArgs e) => MoveStack(-1);

    private void Down_Click(object sender, RoutedEventArgs e) => MoveStack(+1);

    private void MoveStack(int delta)
    {
        int index = StackList.SelectedIndex;
        int target = index + delta;
        if (index < 0 || index >= _stacks.Count || target < 0 || target >= _stacks.Count) return;

        (_stacks[index], _stacks[target]) = (_stacks[target], _stacks[index]);
        Rebuild();
        StackList.SelectedIndex = target;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        FloatingToolbarLayout defaults = FloatingToolbarLayout.Default();
        _stacks.Clear();
        foreach (var stack in defaults.Positions) _stacks.Add(stack.ToList());
        _hidden.Clear();
        _hidden.AddRange(defaults.Hidden);
        Rebuild();
        HintText.Text = "已恢复默认布局（按「保存」才生效）。";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = FloatingToolbarLayout.Normalize(
            _stacks.Select(stack => string.Join(",", stack)).ToList(), _hidden, appendMissing: true);
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
