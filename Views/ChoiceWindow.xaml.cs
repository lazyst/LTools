using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LTools.Views;

/// <summary>
/// 「确认框」与「选择器」共用的窗口：一段话 + 若干按钮，或一段话 + 候选列表 + 确定 / 取消。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>ui.confirm</c> / <c>ui.select</c> 经 <see cref="Features.ChoiceWindowService"/> 打开，
/// 单元自己轮询结果——和文本窗口一样**不用 <c>ShowDialog</c>**：模态对话框会卡住 UI 线程，
/// 停止热键就中止不了流程了。
/// </para>
/// <para>
/// 两种模式的差别只在「答案从哪来」：确认框是<b>按了哪个按钮</b>（第一个按钮算肯定），
/// 选择器是<b>列表里选了哪一项</b>（按钮只有确定 / 取消）。
/// </para>
/// </remarks>
public partial class ChoiceWindow : Window
{
    private readonly IReadOnlyList<string> _options;
    private readonly bool _selectMode;

    /// <summary>窗口是否已经关掉（单元轮询它决定何时继续）。</summary>
    public bool IsClosed { get; private set; }

    /// <summary>用户是否给出了答案（关窗 / 取消 / 超时都是 false）。</summary>
    public bool Accepted { get; private set; }

    /// <summary>确认框：按下的按钮下标（0 起）；选择器：选中的列表项下标。</summary>
    public int Index { get; private set; } = -1;

    /// <summary>确认框：按钮文字；选择器：选中的列表项文字。</summary>
    public string Value { get; private set; } = "";

    public ChoiceWindow(string title, string message, IReadOnlyList<string> options,
        IReadOnlyList<string> buttons, bool selectMode)
    {
        InitializeComponent();

        _options = options ?? Array.Empty<string>();
        _selectMode = selectMode && _options.Count > 0;

        Title = string.IsNullOrWhiteSpace(title) ? (_selectMode ? "请选择" : "确认") : title.Trim();
        MessageText.Text = message ?? "";
        MessageText.Visibility = MessageText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        Bar.ShowMinimize = false;
        Bar.ShowMaximize = false;

        if (_selectMode)
        {
            ListHost.Visibility = Visibility.Visible;
            OptionList.ItemsSource = _options;
            OptionList.SelectedIndex = 0;
        }

        BuildButtons(buttons);
        PreviewKeyDown += OnKeyDown;
        Closed += (_, _) => IsClosed = true;
    }

    private void BuildButtons(IReadOnlyList<string> buttons)
    {
        var labels = (buttons ?? Array.Empty<string>())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .ToList();

        // 选择器只有「确定 / 取消」两个按钮；确认框没填按钮就按「确定」处理。
        if (_selectMode && labels.Count == 0) labels.AddRange(new[] { "确定", "取消" });
        if (labels.Count == 0) labels.Add("确定");

        for (int i = 0; i < labels.Count; i++)
        {
            var button = new Button
            {
                Content = labels[i],
                MinWidth = 78,
                Margin = new Thickness(0, 0, 8, 0),
                Style = (Style)FindResource(i == 0 ? "BtnPrimary" : "BtnGhost"),
            };

            int index = i;
            button.Click += (_, _) => Accept(index);
            ButtonHost.Children.Add(button);
        }
    }

    private void Accept(int buttonIndex)
    {
        if (_selectMode)
        {
            if (buttonIndex != 0)
            {
                Accepted = false;
                Close();
                return;
            }

            int selected = OptionList.SelectedIndex;
            if (selected < 0) selected = 0;

            Accepted = true;
            Index = selected;
            Value = _options[selected];
            Close();
            return;
        }

        // 确认框：按钮就是答案，第一个按钮是「肯定」（单元据此给出 confirmed）。
        Accepted = true;
        Index = buttonIndex;
        Value = (ButtonHost.Children[buttonIndex] as Button)?.Content as string ?? "";
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Accepted = false;
            Close();
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Accept(0);
        }
    }

    private void OptionList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selectMode) Accept(0);
    }
}
