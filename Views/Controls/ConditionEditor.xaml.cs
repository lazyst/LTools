using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using LTools.Features;
using Condition = LTools.Features.Condition;

namespace LTools.Views.Controls;

/// <summary>
/// 条件编辑控件：取值器 + 比较方式 + 右值 + 取反 + 结果变量。
/// 比较方式随取值方式收敛（图像只有「找到了/没找到」，颜色只有「接近」等），避免用户选出无意义的组合。
/// </summary>
public partial class ConditionEditor : UserControl
{
    private bool _ready;
    private ProbeKind _opsKind = ProbeKind.text;

    public ConditionEditor()
    {
        InitializeComponent();
        Probe.KindChanged += RebuildOps;
        Probe.ValueChanged += Value_Changed;
        _ready = true;

        Probe.Load(new Probe { Kind = ProbeKind.text });
        RebuildOps();
    }

    /// <summary>载入条件。</summary>
    public void Load(Condition? condition)
    {
        condition ??= new Condition { Probe = new Probe { Kind = ProbeKind.text }, Op = CompareOp.contains };

        _ready = false;
        Probe.Load(condition.Probe);
        Probe.ShowColorTarget = true;
        BindBox.Text = condition.Bind ?? "";
        NegateBox.IsChecked = condition.Negate;
        RebuildOps();

        // 选回原比较方式（当前取值方式不支持时回落到默认）。
        for (int i = 0; i < OpCombo.Items.Count; i++)
        {
            if (OpCombo.Items[i] is OpItem item && item.Op == condition.Op)
            {
                OpCombo.SelectedIndex = i;
                break;
            }
        }
        if (OpCombo.SelectedIndex < 0) OpCombo.SelectedIndex = 0;

        OperandBox.Text = condition.Operand ?? "";
        _ready = true;
        UpdateOperand();
    }

    /// <summary>读出条件；失败返回 false 并给出可读原因。</summary>
    public bool TryRead(out Condition condition, out string error)
    {
        condition = new Condition();
        error = "";

        if (!Probe.TryRead(out var probe, out error)) return false;
        condition.Probe = probe;
        condition.Op = (OpCombo.SelectedItem as OpItem)?.Op ?? DefaultOp(probe.Kind);
        condition.Operand = NeedsOperand(condition.Op) ? OperandBox.Text : null;
        condition.Negate = NegateBox.IsChecked == true;
        condition.Bind = string.IsNullOrWhiteSpace(BindBox.Text) ? null : BindBox.Text.Trim();

        if (NeedsOperand(condition.Op) && string.IsNullOrWhiteSpace(condition.Operand))
        {
            error = "请填写比较值。";
            return false;
        }

        return true;
    }

    // —— 比较方式随取值方式变化 ——

    private void RebuildOps()
    {
        // 注意：不能因 _ready=false 提前返回——Load 期间需要在禁用事件副作用的同时重建选项。
        ProbeKind kind = Probe.EffectiveKind;
        _opsKind = kind;
        CompareOp? previous = (OpCombo.SelectedItem as OpItem)?.Op;

        var ops = new List<OpItem>();
        foreach (var (op, label) in OpsFor(kind))
            ops.Add(new OpItem(label, op));

        _ready = false;
        OpCombo.ItemsSource = ops;

        // 优先保留原比较方式；新取值方式不支持时回落到该类别的默认项。
        int index = -1;
        if (previous is CompareOp p)
        {
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].Op == p) { index = i; break; }
        }
        if (index < 0)
        {
            CompareOp fallback = DefaultOp(kind);
            for (int i = 0; i < ops.Count; i++)
                if (ops[i].Op == fallback) { index = i; break; }
        }
        if (index < 0) index = 0;
        OpCombo.SelectedIndex = ops.Count > 0 ? index : -1;
        _ready = true;
        UpdateOperand();
    }

    private void Op_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateOperand();
    }

    /// <summary>文本内容在「模板文本 / 纯变量」之间切换时，比较方式列表也要跟着换。</summary>
    private void Value_Changed()
    {
        if (!_ready) return;
        if (Probe.EffectiveKind != _opsKind) RebuildOps();
    }

    private void UpdateOperand()
    {
        CompareOp op = (OpCombo.SelectedItem as OpItem)?.Op ?? DefaultOp(Probe.EffectiveKind);
        bool needs = NeedsOperand(op);
        OperandPanel.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        OperandLabel.Text = op switch
        {
            CompareOp.regex => "正则表达式",
            CompareOp.greater or CompareOp.less or CompareOp.greaterOrEqual or CompareOp.lessOrEqual => "比较值（数字或 ${变量}）",
            _ => "比较文本（支持 ${变量}）",
        };
    }

    private static bool NeedsOperand(CompareOp op) => FlowText.NeedsOperand(op);

    private static CompareOp DefaultOp(ProbeKind kind) => kind switch
    {
        ProbeKind.image => CompareOp.found,
        ProbeKind.color => CompareOp.nearColor,
        ProbeKind.window => CompareOp.found,
        ProbeKind.process => CompareOp.found,
        ProbeKind.cursor => CompareOp.greater,
        _ => CompareOp.contains,
    };

    private static IEnumerable<(CompareOp Op, string Label)> OpsFor(ProbeKind kind)
    {
        switch (kind)
        {
            case ProbeKind.image:
                yield return (CompareOp.found, "找到了");
                yield return (CompareOp.notFound, "没找到");
                break;

            case ProbeKind.color:
                yield return (CompareOp.nearColor, "颜色接近目标");
                break;

            case ProbeKind.window:
                yield return (CompareOp.found, "窗口存在");
                yield return (CompareOp.notFound, "窗口不存在");
                yield return (CompareOp.active, "是前台窗口");
                yield return (CompareOp.notActive, "不是前台窗口");
                break;

            case ProbeKind.process:
                yield return (CompareOp.found, "进程正在运行");
                yield return (CompareOp.notFound, "进程未运行");
                break;

            case ProbeKind.variable:
                yield return (CompareOp.isTrue, "为真");
                yield return (CompareOp.isFalse, "为假");
                yield return (CompareOp.found, "是有效结果（如图像找到）");
                yield return (CompareOp.notFound, "不是有效结果");
                yield return (CompareOp.equals, "等于");
                yield return (CompareOp.notEquals, "不等于");
                yield return (CompareOp.contains, "包含");
                yield return (CompareOp.notContains, "不包含");
                yield return (CompareOp.startsWith, "开头是");
                yield return (CompareOp.endsWith, "结尾是");
                yield return (CompareOp.regex, "匹配正则");
                yield return (CompareOp.isEmpty, "为空");
                yield return (CompareOp.isNotEmpty, "不为空");
                yield return (CompareOp.greater, "大于");
                yield return (CompareOp.less, "小于");
                yield return (CompareOp.greaterOrEqual, "大于等于");
                yield return (CompareOp.lessOrEqual, "小于等于");
                break;

            case ProbeKind.cursor:
                yield return (CompareOp.greater, "大于");
                yield return (CompareOp.less, "小于");
                yield return (CompareOp.greaterOrEqual, "大于等于");
                yield return (CompareOp.lessOrEqual, "小于等于");
                break;

            default:
                yield return (CompareOp.contains, "包含");
                yield return (CompareOp.notContains, "不包含");
                yield return (CompareOp.equals, "等于");
                yield return (CompareOp.notEquals, "不等于");
                yield return (CompareOp.startsWith, "开头是");
                yield return (CompareOp.endsWith, "结尾是");
                yield return (CompareOp.regex, "匹配正则");
                yield return (CompareOp.isEmpty, "为空");
                yield return (CompareOp.isNotEmpty, "不为空");
                break;
        }
    }

    private sealed record OpItem(string Label, CompareOp Op)
    {
        public override string ToString() => Label;
    }
}
