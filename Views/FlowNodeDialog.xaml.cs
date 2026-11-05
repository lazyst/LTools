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

    /// <summary>流程里其它节点已有的变量名（用于提示下标变量重名会覆盖）。</summary>
    private readonly HashSet<string> _otherVariables;

    /// <summary>本节点原有下标变量名（重名提示时排除自己，否则每次打开都报警）。</summary>
    private readonly string _ownIndexVar;

    /// <summary>本节点原来的变量名（「设置变量」提示时排除自己，免得把「第一次赋值」说成「覆盖旧值」）。</summary>
    private readonly string _ownVar;

    /// <summary>确定后的新节点（取消时为 null）。</summary>
    public FlowNode? Result { get; private set; }

    public FlowNodeDialog(FlowNode node, IEnumerable<VariableInfo>? customVariables = null)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _node = node.Clone();

        var infos = customVariables?.ToList() ?? new List<VariableInfo>();
        _otherVariables = new HashSet<string>(infos.Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        _ownIndexVar = _node.Loop?.IndexVar?.Trim() ?? "";
        _ownVar = _node.Set?.Var?.Trim() ?? "";
        VariableSource.SetNames(this, FlowVariables.ExpandPaths(infos));

        switch (_node.Kind)
        {
            case FlowNodeKind.set:
                Title = "设置变量";
                SetSection.Visibility = Visibility.Visible;
                VarBox.Text = _node.Set?.Var ?? "";
                SetAppendRadio.IsChecked = _node.Set?.Append == true;
                SetProbe.ShowColorTarget = false;
                SetProbe.Load(_node.Set?.Probe ?? new Probe { Kind = ProbeKind.text });
                UpdateSetHint();
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
                UpdateLoopIndexTexts();
                UpdateIndexWarn();
                break;
        }
    }

    private void LoopMode_Changed(object sender, RoutedEventArgs e)
    {
        if (LoopCountPanel == null) return;   // 初始化期间字段尚未就绪
        UpdateLoopPanels();
        UpdateLoopIndexTexts();
        UpdateIndexWarn();
    }

    // —— 循环 ——

    private void UpdateLoopPanels()
    {
        bool byCount = LoopCountRadio.IsChecked == true;
        LoopCountPanel.Visibility = byCount ? Visibility.Visible : Visibility.Collapsed;
        LoopWhilePanel.Visibility = byCount ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>固定次数叫「下标」，条件循环里其实是已执行轮次——文案跟着模式变，免得误导。</summary>
    private void UpdateLoopIndexTexts()
    {
        if (LoopIndexLabel == null) return;

        bool byCount = LoopCountRadio.IsChecked == true;
        LoopIndexLabel.Text = byCount ? "下标变量（可选）" : "轮次变量（可选）";
        LoopIndexHint.Text = byCount
            ? "每轮开始时写入当前下标（从 0 开始），循环体内用 ${变量名} 引用；结束后保留最后一次的值。"
            : "每轮开始时写入已执行轮次（从 0 开始），循环体内用 ${变量名} 引用；结束后保留最后一次的值。";
    }

    private void LoopIndexVar_Changed(object sender, TextChangedEventArgs e) => UpdateIndexWarn();

    /// <summary>下标变量与流程里其它变量重名时给出提示：运行时会直接覆盖它。</summary>
    private void UpdateIndexWarn()
    {
        if (LoopIndexWarn == null || LoopIndexVarBox == null) return;

        string name = LoopIndexVarBox.Text.Trim();
        bool clash = name.Length > 0
                     && _otherVariables.Contains(name)
                     && !string.Equals(name, _ownIndexVar, StringComparison.OrdinalIgnoreCase);

        LoopIndexWarn.Visibility = clash ? Visibility.Visible : Visibility.Collapsed;
        if (clash)
            LoopIndexWarn.Text = $"流程里已有变量「{name}」，循环会覆盖它的值（结束后保留最后一次下标）。";
    }

    // —— 设置变量 ——

    private void VarBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSetHint();

    private void SetMode_Changed(object sender, RoutedEventArgs e)
    {
        if (SetVarHint == null) return;   // 初始化期间字段尚未就绪
        UpdateSetHint();
    }

    /// <summary>
    /// 名字提示：明确告诉用户「同名变量可以重新赋值」——覆盖会说会盖掉旧值，
    /// 追加会说会接在旧文本后面，避免以为变量只能创建一次。
    /// </summary>
    private void UpdateSetHint()
    {
        if (SetVarHint == null || VarBox == null) return;

        string name = VarBox.Text.Trim();
        bool append = SetAppendRadio?.IsChecked == true;

        if (name.Length == 0)
        {
            SetVarHint.Text = "变量名可以用中文，例如「名称」；之后用 ${名称} 引用。同名变量可以反复赋值，选「追加」就是 a += 右值。";
            return;
        }
        if (!IsValidVariableName(name))
        {
            SetVarHint.Text = "变量名不能包含空格、小数点、$ 或 { }（点用于取属性，如 ${变量.x}）。";
            return;
        }

        // 其它节点里的同名变量（排除本节点自己：编辑一个首次赋值的节点时不该说「覆盖旧值」）
        bool exists = _otherVariables.Contains(name)
                      && !string.Equals(name, _ownVar, StringComparison.OrdinalIgnoreCase);
        if (append)
        {
            SetVarHint.Text = exists
                ? $"流程里已有「{name}」：它在这一步之前有值时，新值会接在后面（相当于 {name} += 右值）；没有值就当作空串。"
                : $"「{name}」还不存在：追加会从空串开始，结果就是右值本身。";
        }
        else
        {
            SetVarHint.Text = exists
                ? $"流程里已有「{name}」：这次赋值会覆盖它在这一步之前的值（同名变量可以反复赋值；要接在后面就选「追加」）。"
                : $"将创建新变量「{name}」；之后可以用 ${name} 引用。";
        }
    }

    /// <summary>变量名不能带空白、属性分隔符或模板符号，否则 ${名字} 无法解析或会和属性混淆。</summary>
    private static bool IsValidVariableName(string name)
    {
        foreach (char c in name)
            if (char.IsWhiteSpace(c) || c is '$' or '{' or '}' or '.')
                return false;
        return true;
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
                if (!IsValidVariableName(name))
                {
                    ConfirmDialog.Info(this, "设置变量", "变量名不能包含空格、小数点、$ 或 { }（点用于取属性，如 ${变量.x}）。");
                    VarBox.Focus();
                    return;
                }
                _node.Set = new SetNode
                {
                    Var = name,
                    Probe = probe,
                    Append = SetAppendRadio.IsChecked == true,
                };
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
                        count < 1 || count > RunContext.MaxLoopIterations)
                    {
                        ConfirmDialog.Info(this, "循环", $"次数必须是 1–{RunContext.MaxLoopIterations} 之间的整数。");
                        LoopCountBox.Focus();
                        return;
                    }
                    loop.Mode = LoopMode.count;
                    loop.Count = count;
                    loop.While = null;
                }

                string indexVar = LoopIndexVarBox.Text.Trim();
                if (!IsValidVariableName(indexVar))
                {
                    ConfirmDialog.Info(this, "循环", "变量名不能包含空格、小数点、$ 或 { }（点用于取属性，如 ${变量.x}）。");
                    LoopIndexVarBox.Focus();
                    return;
                }

                loop.IndexVar = indexVar;
                _node.Loop = loop;
                break;
            }
        }

        Result = _node;
        DialogResult = true;
    }
}
