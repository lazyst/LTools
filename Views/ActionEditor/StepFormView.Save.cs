using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Script;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 把表单写回步骤：必填校验、重试参数就地校验、输出映射与步骤选项落值。
/// </summary>
public partial class StepFormView
{
    /// <summary>把表单写回步骤；必填项为空时提示并返回 false（对话框不关）。</summary>
    private bool TrySave()
    {
        SyncStepArgs();

        var missing = new List<InputSpec>();
        foreach (var row in _rows)
        {
            if (!row.Spec.Required) continue;
            if (!StepSummary.IsVisible(row.Spec, _step, _spec)) continue;
            if (_step.In.TryGetValue(row.Spec.Key, out var arg) && !arg.IsEmpty) continue;

            missing.Add(row.Spec);
            row.Editor.MarkMissing(true);
        }

        if (missing.Count > 0)
        {
            // 必填项在高级参数里时先展开，别让用户对着看不见的框找错误。
            if (missing.Any(spec => spec.Advanced)) SetAdvancedExpanded(true);

            ProblemText.Text = $"还缺 {missing.Count} 项：{string.Join("、", missing.Select(s => s.Label))}";
            ProblemText.ToolTip = ProblemText.Text;
            ProblemText.Visibility = Visibility.Visible;
            return false;
        }

        foreach (var row in _rows) row.Editor.MarkMissing(false);
        ProblemText.Visibility = Visibility.Collapsed;

        // 重试次数 / 间隔：填错了就地拦下（否则存进 JSON 只能靠校验器报）。
        string retryText = (RetryBox.Text ?? "").Trim();
        int retry = 0;
        if (retryText.Length > 0 &&
            (!int.TryParse(retryText, NumberStyles.Integer, CultureInfo.InvariantCulture, out retry)
             || retry < 0 || retry > Step.MaxRetry))
        {
            ProblemText.Text = $"重试次数要在 0–{Step.MaxRetry} 之间（填的是「{retryText}」）";
            ProblemText.ToolTip = ProblemText.Text;
            ProblemText.Visibility = Visibility.Visible;
            return false;
        }

        string retryDelayText = (RetryDelayBox.Text ?? "").Trim();
        int retryDelay = Step.DefaultRetryDelayMs;
        if (retryDelayText.Length > 0 &&
            (!int.TryParse(retryDelayText, NumberStyles.Integer, CultureInfo.InvariantCulture, out retryDelay)
             || retryDelay < 0))
        {
            ProblemText.Text = $"重试间隔要写非负整数毫秒（填的是「{retryDelayText}」）";
            ProblemText.ToolTip = ProblemText.Text;
            ProblemText.Visibility = Visibility.Visible;
            return false;
        }

        _step.Out.Clear();
        foreach (var row in _outputs.Concat(_dynamicOutputs))
        {
            if (row.Toggle.IsChecked != true) continue;
            string name = (row.Box.Text ?? "").Trim();
            if (name.Length > 0) _step.Out[row.Key] = name;
        }

        _step.OnFail = FailBox.SelectedIndex == 1 ? FailMode.@continue : FailMode.stop;
        _step.DelayMs = int.TryParse((DelayBox.Text ?? "").Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay)
            ? Math.Max(0, delay) : 0;
        _step.Retry = retry;
        _step.RetryDelayMs = retryDelay;
        _step.Disabled = DisabledBox.IsChecked == true;
        _step.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

        Result = _step;
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Cancelled?.Invoke();
    // ============ 文件选择 ============

    public sealed record SubChoice(string Id, string Display)
    {
        public override string ToString() => Display;
    }

}
