using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 校验诊断：一句话结论 + 可点击清单 + 步骤行上的红 / 黄标记。
/// </summary>
/// <remarks>
/// <para>
/// 以前「校验」只弹一个只读对话框，问题一多就得靠人肉在步骤列表里找。现在：
/// 错误 / 提醒都进底部清单（<c>DiagnosticBar</c>），点一条就展开祖先块、选中并滚到那一步；
/// 每个受影响的步骤行左侧带 <c>✕</c> / <c>！</c> 标记，悬停能看到这一步的全部问题。
/// </para>
/// <para>
/// 标记按 <see cref="Step"/> 对象引用记（不是按路径 / 下标）：结构一改路径就挪位，按路径存的高亮会指到别的步骤上。
/// 折叠起来的块会把子步骤的问题合并显示（否则「折叠着但里面是红的」看不出来）。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    private readonly ObservableCollection<DiagnosticRow> _diagnostics = new();

    /// <summary>步骤 → 它自己的问题（不含子步骤；合并显示在 <see cref="MakeRow"/> 里做）。</summary>
    private readonly Dictionary<Step, List<ActionIssue>> _issueByStep = new();

    /// <summary>
    /// 静态校验：与 AI 草稿的 <c>ltools_staging_check</c> 用同一个 <see cref="ActionValidator"/>，
    /// 免得编辑器和 MCP 两套规则长期分叉。
    /// </summary>
    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        _program.Name = (NameBox.Text ?? "").Trim();
        _program.Icon = IconPicker.SelectedIcon ?? "";

        ActionValidationResult result = ValidateCurrent();

        SetStatus(result.Issues.Count == 0
                ? "校验通过：没有发现问题"
                : $"校验：{result.Errors} 个错误 / {result.Warnings} 个提醒",
            hint: result.Ok, danger: !result.Ok);

        ApplyValidation(result);
    }

    /// <summary>
    /// 子动作模式要按子动作的规则校验（<c>flow.return</c> 在那边合法、输出变量必须有人赋值），
    /// 别拿动作那套去报一堆假错。
    /// </summary>
    private ActionValidationResult ValidateCurrent()
    {
        if (!_isSubAction) return ActionValidator.Validate(_program, null, ResourceRoot);

        var def = _subDef!.Clone();
        def.Name = _program.Name;
        def.Icon = _program.Icon;

        def.Variables.Clear();
        foreach (var variable in _program.Variables) def.Variables.Add(variable.Clone());

        def.Steps.Clear();
        foreach (var step in _program.Steps) def.Steps.Add(step);

        return ActionValidator.ValidateSubAction(def, null);
    }

    /// <summary>
    /// 把一次校验结果落到诊断清单与步骤行标记上。
    /// 问题按「路径 → 步骤对象」换算一次，之后一切都按对象引用走。
    /// </summary>
    private void ApplyValidation(ActionValidationResult result)
    {
        _diagnostics.Clear();
        _issueByStep.Clear();

        var stepOfPath = new Dictionary<string, Step>(StringComparer.Ordinal);
        foreach (var (path, step) in WalkPaths(_program.Steps, "", new List<(string, Step)>()))
            stepOfPath[path] = step;

        var rows = new List<DiagnosticRow>();

        foreach (ActionIssue issue in result.Issues)
        {
            Step? step = StepOfPath(issue.Path, stepOfPath);
            if (step != null)
            {
                if (!_issueByStep.TryGetValue(step, out List<ActionIssue>? own))
                    _issueByStep[step] = own = new List<ActionIssue>();
                own.Add(issue);
            }

            bool error = issue.Severity == IssueSeverity.error;
            rows.Add(new DiagnosticRow
            {
                Step = step,
                Severity = issue.Severity,
                Glyph = error ? "✕" : "！",
                Brush = (Brush)FindResource(error ? "DangerBrush" : "WarningBrush"),
                Where = issue.Label.Length > 0 ? issue.Label : "（动作整体）",
                Message = issue.Message,
                Tip = issue.Unit.Length > 0 ? $"{issue.Unit} · {issue.Message}" : issue.Message,
            });
        }

        // 错误排在前面（同级保持校验器给的步骤顺序），先看要命的。
        foreach (DiagnosticRow row in rows.OrderByDescending(r => r.Severity == IssueSeverity.error))
            _diagnostics.Add(row);

        RefreshDiagnosticBar();

        // 行标记是在 MakeRow 里按 Step 引用取的，重建一次列表即可，不用逐个改控件。
        ReloadRows();
    }

    private void RefreshDiagnosticBar()
    {
        bool any = _diagnostics.Count > 0;
        DiagnosticBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            DiagnosticTitle.Text = "";
            DiagnosticList.ItemsSource = null;
            return;
        }

        int errors = _diagnostics.Count(d => d.Severity == IssueSeverity.error);
        int warnings = _diagnostics.Count - errors;
        DiagnosticTitle.Text = errors > 0
            ? $"校验：{errors} 个错误 / {warnings} 个提醒 · 点一条定位到步骤"
            : $"校验：{warnings} 个提醒（不影响运行）· 点一条定位到步骤";
        DiagnosticList.ItemsSource = _diagnostics;
    }

    private void DiagnosticClose_Click(object sender, RoutedEventArgs e) =>
        DiagnosticBar.Visibility = Visibility.Collapsed;

    /// <summary>点诊断清单里的一条：展开祖先块、选中并滚到那一步。</summary>
    private void DiagnosticRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListViewItem item || item.DataContext is not DiagnosticRow row) return;

        if (row.Step == null)
        {
            SetStatus("这条是动作整体的问题，没有对应的步骤", hint: true);
            return;
        }

        var entry = AllSteps().FirstOrDefault(e => ReferenceEquals(e.Step, row.Step));
        if (entry.Step == null)
        {
            // 那一步已经被删掉 / 换掉了：陈旧的诊断留着只会让人白点，就地清掉。
            var stale = _diagnostics.Where(d => ReferenceEquals(d.Step, row.Step)).ToList();
            foreach (DiagnosticRow dead in stale) _diagnostics.Remove(dead);
            _issueByStep.Remove(row.Step);
            RefreshDiagnosticBar();
            SetStatus($"那一步已经改掉了（{stale.Count} 条诊断已失效），改完再点「校验」", hint: true);
            return;
        }

        foreach (Step ancestor in entry.Ancestors) _collapsed.Remove(ancestor);
        ReloadRows(row.Step);
    }

    /// <summary>装配 <see cref="StepRow"/> 上的校验标记（折叠块把子步骤的问题一起算上）。</summary>
    private (string Mark, Brush Brush, string Tip) IssueMarkFor(Step step, bool includeDescendants)
    {
        var issues = new List<ActionIssue>();
        CollectIssues(step, includeDescendants, issues);
        if (issues.Count == 0) return ("", Brushes.Transparent, "");

        bool error = issues.Any(i => i.Severity == IssueSeverity.error);
        return (error ? "✕" : "！",
            (Brush)FindResource(error ? "DangerBrush" : "WarningBrush"),
            string.Join("\n", issues.Select(i =>
                (i.Severity == IssueSeverity.error ? "错误：" : "提醒：") + i.Message)));
    }

    private void CollectIssues(Step step, bool includeDescendants, List<ActionIssue> output)
    {
        if (_issueByStep.TryGetValue(step, out List<ActionIssue>? own)) output.AddRange(own);
        if (!includeDescendants) return;

        foreach (Step child in step.Then) CollectIssues(child, true, output);
        foreach (Step child in step.Else) CollectIssues(child, true, output);
        foreach (Step child in step.Body) CollectIssues(child, true, output);
    }

    /// <summary>
    /// 问题路径 → 步骤对象。参数级问题的路径是 <c>/steps/2/then/0/in/dx</c>、输出是 <c>…/out/键</c>，
    /// 逐段往回收直到命中某个步骤自己的路径（<c>/steps/2/then/0</c>）；动作 / 变量级问题（<c>""</c>、<c>/variables</c>）没有步骤。
    /// </summary>
    private static Step? StepOfPath(string? path, Dictionary<string, Step> stepOfPath)
    {
        string candidate = path ?? "";
        while (candidate.Length > 0)
        {
            if (stepOfPath.TryGetValue(candidate, out Step? step)) return step;

            int cut = candidate.LastIndexOf('/');
            if (cut <= 0) return null;
            candidate = candidate[..cut];
        }

        return null;
    }
}

/// <summary>诊断清单的一行：级别 + 位置 + 说明，<see cref="Step"/> 非空时点了能跳过去。</summary>
internal sealed class DiagnosticRow
{
    public Step? Step { get; init; }

    public IssueSeverity Severity { get; init; }

    public string Glyph { get; init; } = "";

    public Brush Brush { get; init; } = Brushes.Transparent;

    public string Where { get; init; } = "";

    public string Message { get; init; } = "";

    public string Tip { get; init; } = "";
}
