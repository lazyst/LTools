using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Migration;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 保存 / 试运行
/// </summary>
public partial class ActionEditorWindow
{
    // ============ 保存 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ConfirmDialog.Info(this, _isSubAction ? "子动作" : "动作",
                _isSubAction ? "给子动作起一个名字。" : "给动作起一个名字。");
            NameBox.Focus();
            return;
        }

        _program.Name = name;
        _program.Icon = IconPicker.SelectedIcon ?? "";

        _program.Variables.Clear();
        var variables = ReadVariables();
        if (variables == null) return;
        foreach (var variable in variables) _program.Variables.Add(variable);

        // 输出变量永远在变量表里（写配置的人不用再手动补声明）。
        // 子动作不自动补：那里「本地 / 输出」是刻意标记的，补一条反而要用户再改。
        if (!_isSubAction)
        {
            var declared = new HashSet<string>(
                _program.Variables.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var step in _program.Steps.SelectMany(s => s.SelfAndDescendants()))
            {
                foreach (string raw in step.Out.Values)
                {
                    string key = (raw ?? "").Trim();
                    if (key.Length == 0 || !declared.Add(key)) continue;
                    _program.Variables.Add(new VariableDef { Key = key, Type = ValueKind.text });
                }
            }
        }

        if (_isSubAction)
        {
            var def = _subDef!;
            def.Name = name;
            def.Icon = _program.Icon;

            def.Variables.Clear();
            foreach (var variable in _program.Variables) def.Variables.Add(variable);

            def.Steps.Clear();
            foreach (var step in _program.Steps) def.Steps.Add(step);

            SubResult = def;
            DialogResult = true;
            return;
        }

        _program.Id = _draft!.Id ?? "";

        _draft.Name = name;
        _draft.Icon = _program.Icon;
        _draft.Type = ActionType.composite;
        // 用新编辑器保存过 = 已是新格式，不再回写 v1 字段
        _draft.ClearLegacy();
        _draft.Program = _program;

        Result = _draft;
        DialogResult = true;
    }

    // ============ 试运行 ============

    private void RunToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = RunBody.Visibility != Visibility.Visible;
        RunBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RunToggleButton.Content = show ? "收起" : "展开";
    }

    private void Run_Click(object sender, RoutedEventArgs e) => StartRun(fromSelected: false);

    private void RunFrom_Click(object sender, RoutedEventArgs e) => StartRun(fromSelected: true);

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        AutomationRunner.Stop();
        RunStatus.Text = "正在停止…";
    }

    /// <summary>
    /// 试运行：跑的是**编辑器里现在的内容**（克隆一份，不必先保存），轨迹按步骤路径回填。
    /// </summary>
    private void StartRun(bool fromSelected)
    {
        if (_running) return;

        string? startPath = null;
        if (fromSelected)
        {
            StepRow? row = SelectedRow();
            if (row?.Step == null || !ReferenceEquals(row.Owner, _program.Steps))
            {
                ConfirmDialog.Info(this, "试运行",
                    "「从此行」只支持顶层步骤：先选中左侧最外层的那一行为起点。");
                return;
            }
            startPath = $"/steps/{row.Index}";
        }

        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) name = "未命名动作";

        ActionProgram? program = BuildRunProgram();
        if (program == null) return;

        // 路径 → 步骤行：等宽两条遍历（程序本身 + 编辑器行），运行时靠路径定位到某一行。
        _traceTargets.Clear();
        var paths = new List<string>();
        IndexStepPaths(_program.Steps, "", paths);
        var realRows = _rows.Where(r => r.Step != null).ToList();
        for (int i = 0; i < paths.Count && i < realRows.Count; i++)
            _traceTargets[paths[i]] = realRows[i];

        _traceRowsByPath.Clear();
        _traceRows.Clear();
        _snapshotRows.Clear();
        lock (_pendingTraces) _pendingTraces.Clear();

        RunBody.Visibility = Visibility.Visible;
        RunToggleButton.Content = "收起";
        SetStatus(fromSelected ? "试运行（从此行）" : "试运行中", hint: true);

        if (!AutomationRunner.TryRunProgram(name, program, startPath, OnTrace, OnRunCompleted, ResourceRoot))
        {
            SetStatus("已有动作正在运行，先等它结束或按停止", hint: false);
            return;
        }

        SetRunning(true);
        StartTraceFlush();
    }

    /// <summary>克隆当前编辑器内容：步骤是活的模型，变量表要现读（保存前 <see cref="_program.Variables"/> 是旧的）。</summary>
    private ActionProgram? BuildRunProgram()
    {
        var variables = ReadVariables();
        if (variables == null) return null;   // 默认值填错，ReadVariables 已经提示过

        ActionProgram program = _program.Clone();
        program.Variables.Clear();
        foreach (var variable in variables) program.Variables.Add(variable.Clone());
        return program;
    }

    /// <summary>按引擎的路径规则遍历：<c>/steps/2/then/0</c>（分支段 + 该层的步骤下标）。</summary>
    private static void IndexStepPaths(List<Step> steps, string prefix, List<string> output)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string path = prefix.Length == 0 ? $"/steps/{i}" : $"{prefix}/{i}";
            output.Add(path);
            IndexStepPaths(step.Then, $"{path}/then", output);
            IndexStepPaths(step.Else, $"{path}/else", output);
            IndexStepPaths(step.Body, $"{path}/body", output);
        }
    }

    /// <summary>轨迹回调（后台线程）：只往队列里塞，由计时器攒批上屏，循环几千步也不会淹掉 UI。</summary>
    private void OnTrace(StepTrace trace)
    {
        lock (_pendingTraces)
        {
            if (_pendingTraces.Count >= 4000) _pendingTraces.RemoveAt(0);
            _pendingTraces.Add(trace);
        }
    }

    private void StartTraceFlush()
    {
        _traceFlush ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _traceFlush.Tick -= TraceFlush_Tick;
        _traceFlush.Tick += TraceFlush_Tick;
        _traceFlush.Start();
    }

    private void TraceFlush_Tick(object? sender, EventArgs e) => FlushTraces();

    private void FlushTraces()
    {
        List<StepTrace> batch;
        lock (_pendingTraces)
        {
            if (_pendingTraces.Count == 0) return;
            batch = new List<StepTrace>(_pendingTraces);
            _pendingTraces.Clear();
        }

        foreach (var trace in batch)
        {
            if (_traceRowsByPath.TryGetValue(trace.Path, out var row))
            {
                row.Merge(trace);
                continue;
            }

            row = new TraceRow(trace);
            _traceRowsByPath[trace.Path] = row;
            _traceRows.Add(row);
        }

        // 只保留最近 400 行（循环里路径去重后一般远小于这个数）。
        while (_traceRows.Count > 400)
        {
            TraceRow first = _traceRows[0];
            _traceRows.RemoveAt(0);
            if (_traceRowsByPath.TryGetValue(first.Path, out var mapped) && ReferenceEquals(mapped, first))
                _traceRowsByPath.Remove(first.Path);
        }
    }

    /// <summary>运行结束（后台线程）：回到 UI 线程收尾、填变量快照、选中失败那一行。</summary>
    private void OnRunCompleted(ActionRunResult result)
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _traceFlush?.Stop();
            FlushTraces();
            SetRunning(false);
            FillSnapshot(result);

            if (result.Stopped)
            {
                SetStatus($"已停止 · 共 {result.Steps} 步", hint: true);
                return;
            }

            if (result.Completed)
            {
                SetStatus($"完成 · {result.Steps} 步 · {result.Elapsed.TotalMilliseconds:0} ms", hint: true);
                return;
            }

            SetStatus(result.Error ?? "执行失败", hint: false, danger: true);
            SelectTraceRow(result.FailedStepLabel);
        }));
    }

    private void FillSnapshot(ActionRunResult result)
    {
        _snapshotRows.Clear();
        foreach (var pair in result.Variables.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            string text;
            try { text = pair.Value.AsText(); }
            catch { text = "(无法显示)"; }

            if (text.Length > 300) text = text[..300] + "…";
            _snapshotRows.Add(new SnapshotRow(pair.Key, text));
        }
    }

    /// <summary>失败定位：按<strong>步骤路径</strong>找回编辑器里的那一行（嵌套步骤也能对上）。</summary>
    private void SelectTraceRow(string? label)
    {
        TraceRow? target = label == null
            ? _traceRows.FirstOrDefault(r => r.IsFailed)
            : _traceRows.FirstOrDefault(r => r.Label == label);

        if (target == null) return;
        TraceList.SelectedItem = target;
        TraceList.ScrollIntoView(target);
    }

    /// <summary>点轨迹行 = 定位到对应的步骤行（左侧列表跟着滚动、选中）。</summary>
    private void TraceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_running) return;   // 跑的时候别抢用户正在看的行

        if (TraceList.SelectedItem is not TraceRow trace) return;
        if (!_traceTargets.TryGetValue(trace.Path, out var row)) return;
        if (!_rows.Contains(row)) return;

        StepList.SelectedItem = row;
        StepList.ScrollIntoView(row);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        RunButton.IsEnabled = !running;
        RunFromButton.IsEnabled = !running;
        StopRunButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running) SetStatus("试运行中…（真的会操作鼠标键盘）", hint: false);
    }

    private void SetStatus(string text, bool hint, bool danger = false)
    {
        RunStatus.Text = text;
        RunStatus.Foreground = (Brush)FindResource(
            danger ? "DangerBrush" : hint ? "HintTextBrush" : "TextBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// 静态校验：与 AI 草稿的 <c>ltools_staging_check</c> 用同一个 <see cref="ActionValidator"/>，
    /// 免得编辑器和 MCP 两套规则长期分叉。
    /// </summary>
    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        _program.Name = (NameBox.Text ?? "").Trim();
        _program.Icon = IconPicker.SelectedIcon ?? "";

        var result = ActionValidator.Validate(_program, null, ResourceRoot);
        if (result.Issues.Count == 0)
        {
            SetStatus("校验通过：没有发现问题", hint: true);
            return;
        }

        const int maxShown = 12;
        string detail = string.Join("\n", result.Issues.Take(maxShown).Select(i =>
            (i.Severity == IssueSeverity.error ? "✕ " : "！ ") + i.Label + i.Message));
        if (result.Issues.Count > maxShown)
            detail += $"\n…还有 {result.Issues.Count - maxShown} 条，修完刷新后再看";

        SetStatus($"校验：{result.Errors} 个错误 / {result.Warnings} 个提醒",
            hint: result.Ok, danger: !result.Ok);
        MessageBox.Show(detail,
            $"LTools · 动作校验（{result.Errors} 个错误 / {result.Warnings} 个提醒）",
            MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }
}
