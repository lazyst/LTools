using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LTools.Features;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 流程节点的参数编辑（设置变量 / 如果 / 循环）。
/// </summary>
/// <remarks>
/// 只编辑节点自身的参数：<c>if</c> 的 Then/Else、<c>loop</c> 的 Body 保持原样，
/// 因此重开对话框改条件不会丢子步骤。动作节点走普通动作编辑器，不在此窗口。
/// 变量通过输入框右侧的「插入变量」按钮插入（与动作编辑器一致），不另设变量侧栏。
/// </remarks>
public partial class FlowNodeDialog : Window
{
    private readonly FlowNode _node;

    /// <summary>确定后的新节点（取消时为 null）。</summary>
    public FlowNode? Result { get; private set; }

    public FlowNodeDialog(FlowNode node, IEnumerable<VariableInfo>? customVariables = null)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _node = node.Clone();

        var infos = customVariables?.ToList() ?? new List<VariableInfo>();
        VariableSource.SetNames(this, FlowVariables.ExpandPaths(infos));

        switch (_node.Kind)
        {
            case FlowNodeKind.set:
                Title = "设置变量";
                SetSection.Visibility = Visibility.Visible;
                VarBox.Text = _node.Set?.Var ?? "";
                SetProbe.ShowColorTarget = false;
                SetProbe.Load(_node.Set?.Probe ?? new Probe { Kind = ProbeKind.text });
                Loaded += (_, _) => VarBox.Focus();
                break;

            case FlowNodeKind.@if:
                Title = "条件分支";
                IfSection.Visibility = Visibility.Visible;
                IfCondition.Load(_node.If?.Condition);
                break;

            case FlowNodeKind.loop:
                Title = "循环";
                LoopSection.Visibility = Visibility.Visible;
                var loop = _node.Loop ?? new LoopNode();
                if (loop.Mode == LoopMode.@while)
                {
                    LoopWhileRadio.IsChecked = true;
                    LoopCondition.Load(loop.While);
                }
                else
                {
                    LoopCountRadio.IsChecked = true;
                    LoopCountBox.Text = loop.Count.ToString(CultureInfo.InvariantCulture);
                    LoopCondition.Load(null);
                }
                UpdateLoopPanels();
                LoopIndexVarBox.Text = loop.IndexVar ?? "";
                break;
        }
    }

    private void LoopMode_Changed(object sender, RoutedEventArgs e)
    {
        if (LoopCountPanel == null) return;   // 初始化期间字段尚未就绪
        UpdateLoopPanels();
    }

    // —— 循环 ——

    private void UpdateLoopPanels()
    {
        bool byCount = LoopCountRadio.IsChecked == true;
        LoopCountPanel.Visibility = byCount ? Visibility.Visible : Visibility.Collapsed;
        LoopWhilePanel.Visibility = byCount ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        switch (_node.Kind)
        {
            case FlowNodeKind.set:
            {
                string name = VarBox.Text.Trim();
                if (name.Length == 0)
                {
                    ConfirmDialog.Info(this, "设置变量", "请填写变量名。");
                    VarBox.Focus();
                    return;
                }
                if (!SetProbe.TryRead(out var probe, out string error))
                {
                    ConfirmDialog.Info(this, "设置变量", error);
                    return;
                }
                _node.Set = new SetNode { Var = name, Probe = probe };
                break;
            }

            case FlowNodeKind.@if:
            {
                if (!IfCondition.TryRead(out var condition, out string error))
                {
                    ConfirmDialog.Info(this, "条件分支", error);
                    return;
                }
                _node.If ??= new IfNode();
                _node.If.Condition = condition;
                break;
            }

            case FlowNodeKind.loop:
            {
                var loop = _node.Loop ?? new LoopNode();
                if (LoopWhileRadio.IsChecked == true)
                {
                    if (!LoopCondition.TryRead(out var condition, out string error))
                    {
                        ConfirmDialog.Info(this, "循环", error);
                        return;
                    }
                    loop.Mode = LoopMode.@while;
                    loop.While = condition;
                }
                else
                {
                    string text = LoopCountBox.Text.Trim();
                    if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                        count < 1 || count > 100_000)
                    {
                        ConfirmDialog.Info(this, "循环", "次数必须是 1–100000 之间的整数。");
                        LoopCountBox.Focus();
                        return;
                    }
                    loop.Mode = LoopMode.count;
                    loop.Count = count;
                    loop.While = null;
                }
                loop.IndexVar = LoopIndexVarBox.Text.Trim();
                _node.Loop = loop;
                break;
            }
        }

        Result = _node;
        DialogResult = true;
    }
}
