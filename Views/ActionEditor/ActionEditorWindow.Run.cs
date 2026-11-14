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
        if (!Commit()) return;
        DialogResult = true;
    }

    /// <summary>
    /// 把编辑器里当前的内容收进 <see cref="Result"/> / <see cref="SubResult"/>。
    /// 「确定」与关闭时选「保存」共用这一条路径；校验不过返回 false（留在编辑器里，别关掉丢改动）。
    /// </summary>
    private bool Commit()
    {
        // 抽屉里还开着一步表单：先按它自己的「确定」收尾（校验不过就留在编辑器里）。
        if (!TryCommitDrawer()) return false;

        // 子步骤只属于「如果」/ 循环：别的步骤下面挂着步骤时引擎不会执行它们，
        // 保存前就拦下来（粘贴、导入、手改 JSON 都可能带进来）。
        if (StepBody.FirstProblem(_program.Steps) is string bodyProblem)
        {
            ConfirmDialog.Info(this, _isSubAction ? "子动作" : "动作", bodyProblem);
            return false;
        }

        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ConfirmDialog.Info(this, _isSubAction ? "子动作" : "动作",
                _isSubAction ? "给子动作起一个名字。" : "给动作起一个名字。");
            NameBox.Focus();
            return false;
        }

        _program.Name = name;
        _program.Icon = IconPicker.SelectedIcon ?? "";
        _program.Description = (DescriptionBox.Text ?? "").Trim();

        _program.Variables.Clear();
        var variables = ReadVariables();
        if (variables == null) return false;
        foreach (var variable in variables) _program.Variables.Add(variable);

        // 写出的变量永远在变量表里（写配置的人不用再手动补声明）：输出映射与设置变量的目标名都算，
        // 判定见 VariableWrites。子动作不自动补：那里「本地 / 输出」是刻意标记的，补一条反而要用户再改。
        if (!_isSubAction)
        {
            var declared = new HashSet<string>(
                _program.Variables.Select(v => v.Key), StringComparer.OrdinalIgnoreCase);
            foreach (var step in _program.Steps.SelectMany(s => s.SelfAndDescendants()))
            {
                foreach (var write in VariableWrites.Of(step))
                {
                    string key = write.TrimmedName;
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
            def.Description = _program.Description;

            def.Variables.Clear();
            foreach (var variable in _program.Variables) def.Variables.Add(variable);

            def.Steps.Clear();
            foreach (var step in _program.Steps) def.Steps.Add(step);

            SubResult = def;
            _saved = true;
            ForgetAutosave();
            return true;
        }

        _program.Id = _draft!.Id ?? "";

        _draft.Name = name;
        _draft.Icon = _program.Icon;
        _draft.Program = _program;

        Result = _draft;
        _saved = true;
        ForgetAutosave();
        return true;
    }

    // ============ 试运行 ============

    private void RunToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = RunBody.Visibility != Visibility.Visible;
        RunBody.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RunToggleButton.Content = show ? "收起" : "展开";
    }

    private void Run_Click(object sender, RoutedEventArgs e) => StartRun(RunScope.All);

    private void RunFrom_Click(object sender, RoutedEventArgs e) => StartRun(RunScope.FromRow);

    /// <summary>右键菜单「运行选中的 N 步」/「只运行这一步」。</summary>
    private void RunSelected()
    {
        if (_running) return;
        if (SelectedRows().Count == 0)
        {
            ConfirmDialog.Info(this, "试运行", "先选中要运行的步骤（Ctrl / Shift 可多选）。");
            return;
        }

        StartRun(RunScope.Selection);
    }

    /// <summary>试运行的范围：整个动作 / 从选中行到结尾 / 只跑选中的步骤。</summary>
    private enum RunScope { All, FromRow, Selection }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        AutomationRunner.Stop();
        RunStatus.Text = "正在停止…（等待当前步骤返回）";
    }

    /// <summary>
    /// 试运行：跑的是**编辑器里现在的内容**（按范围克隆，不必先保存），轨迹按步骤路径回填。
    /// </summary>
    private void StartRun(RunScope scope)
    {
        if (_running) return;

        string? startPath = null;
        List<StepRow>? selection = null;
        if (scope == RunScope.FromRow)
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
        else if (scope == RunScope.Selection)
        {
            selection = OutermostSelectedRows();
            if (selection.Count == 0) return;
        }

        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) name = "未命名动作";

        // 子动作的「输入」就是变量：试运行前让用户填一次，没填的沿用默认值。
        // 真实调用时这些值由调用方的参数传入（RunSubAction），这里只影响这一次试运行。
        IReadOnlyDictionary<string, Value>? inputs = null;
        if (_isSubAction)
        {
            var declared = ReadVariables();
            if (declared == null) return;   // 默认值填错，ReadVariables 已经提示过

            string subName = string.IsNullOrWhiteSpace(_program.Name) ? "子动作" : _program.Name;
            inputs = ParamInputDialog.Ask(this, subName + " · 输入", declared);
            if (inputs == null) return;     // 用户取消
        }
        else
        {
            // 动作的「输入」变量：试运行也问一次（与面板运行同一个口径），填过的记住。
            var paramInputs = ActionParams.InputsOf(_program);
            if (paramInputs.Count > 0)
            {
                inputs = ActionParams.Ask(this, _draft?.Id ?? "", name + " · 参数", paramInputs,
                    "只影响这次试运行；面板运行时问你的是同一批变量。");
                if (inputs == null) return;
            }
        }

        // 顶层选择按原下标补齐（未选中的顶层步骤变成跳过），这样轨迹里的「第 N 行」仍是编辑器里的行号；
        // 选中里含分支步骤时没法补，压紧成临时动作（行号以这次运行为准）。
        bool topLevelOnly = selection?.All(r => ReferenceEquals(r.Owner, _program.Steps)) == true;
        ActionProgram? program = BuildRunProgram(selection, topLevelOnly, inputs);
        if (program == null) return;

        PrepareTraceTargets(selection == null || topLevelOnly ? null : SourcePaths(selection!));

        _traceRowsByPath.Clear();
        _traceRows.Clear();
        _snapshotRows.Clear();
        lock (_pendingTraces) _pendingTraces.Clear();

        RunBody.Visibility = Visibility.Visible;
        RunToggleButton.Content = "收起";
        SetStatus(scope switch
        {
            RunScope.FromRow => "试运行（从此行）",
            RunScope.Selection => topLevelOnly
                ? $"试运行（选中 {selection!.Count} 步）"
                : $"试运行（选中 {selection!.Count} 步 · 按临时动作编号）",
            _ => "试运行中",
        }, hint: true);

        if (!AutomationRunner.TryRunProgram(name, program, startPath, OnTrace, OnRunCompleted, ResourceRoot,
                source: RunSource.Editor, slowStep: OnSlowStep))
        {
            SetStatus("已有动作正在运行，先等它结束或按停止", hint: false);
            return;
        }

        SetRunning(true);
        StartTraceFlush();
    }

    /// <summary>选中的最外层步骤行（被另一个选中步骤包含的后代不重复算），按显示顺序。</summary>
    private List<StepRow> OutermostSelectedRows()
    {
        List<StepRow> rows = SelectedRows();
        return rows
            .Where(r => !rows.Any(o => !ReferenceEquals(o, r) && ContainsStep(o.Step!, r.Step!)))
            .OrderBy(r => _rows.IndexOf(r))
            .ToList();
    }

    /// <summary>每一行步骤在源程序里的路径（选中运行压紧下标时用来平移轨迹路径）。</summary>
    private List<string> SourcePaths(IReadOnlyList<StepRow> rows)
    {
        var paths = WalkPaths(_program.Steps, "", new List<(string, Step)>());
        var pathOf = new Dictionary<Step, string>();
        foreach (var (path, step) in paths) pathOf[step] = path;
        return rows.Select(r => pathOf[r.Step!]).ToList();
    }

    /// <summary>
    /// 克隆当前编辑器内容：步骤是活的模型，变量表要现读（保存前 <see cref="_program.Variables"/> 是旧的）。
    /// <paramref name="only"/> 非空时只保留这些步骤：全是顶层时按原下标补齐（未选中的顶层步骤置为跳过），
    /// 否则按显示顺序压紧成临时动作。
    /// </summary>
    private ActionProgram? BuildRunProgram(List<StepRow>? only = null, bool keepIndex = false,
        IReadOnlyDictionary<string, Value>? inputs = null)
    {
        var variables = ReadVariables();
        if (variables == null) return null;   // 默认值填错，ReadVariables 已经提示过

        ActionProgram program = _program.Clone();
        program.Variables.Clear();
        foreach (var variable in variables) program.Variables.Add(variable.Clone());

        // 子动作试运行时填的输入值写进这一次运行的默认值（不改编辑器里的变量表）。
        if (inputs != null)
        {
            foreach (var variable in program.Variables)
            {
                string key = (variable.Key ?? "").Trim();
                if (key.Length > 0 && inputs.TryGetValue(key, out var value)) variable.Default = value;
            }
        }

        if (only == null) return program;

        var selected = new HashSet<Step>(only.Select(r => r.Step!));
        program.Steps.Clear();

        if (keepIndex)
        {
            // 下标对齐：未选中的顶层步骤留成跳过（引擎遇到 Disabled 直接 continue，不执行、不记轨迹）
            foreach (Step step in _program.Steps)
            {
                Step copy = step.Clone();
                if (!selected.Contains(step)) copy.Disabled = true;
                program.Steps.Add(copy);
            }
        }
        else
        {
            foreach (StepRow row in only) program.Steps.Add(row.Step!.Clone());
        }

        return program;
    }

    /// <summary>源程序里的每条路径与步骤（先序，路径规则同引擎：<c>/steps/2/then/0</c>）。</summary>
    private static List<(string Path, Step Step)> WalkPaths(List<Step> steps, string prefix,
        List<(string Path, Step Step)> output)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            string path = prefix.Length == 0 ? $"/steps/{i}" : $"{prefix}/{i}";
            output.Add((path, step));
            WalkPaths(step.Then, $"{path}/then", output);
            WalkPaths(step.Else, $"{path}/else", output);
            WalkPaths(step.Body, $"{path}/body", output);
        }

        return output;
    }

    /// <summary>
    /// 轨迹路径 → 编辑器行。折叠块里的步骤没有可见行，不参与高亮。
    /// <paramref name="remap"/> 非空时（选中运行压紧了外层下标）：
    /// 临时程序的 <c>/steps/i</c> 对应 <c>remap[i]</c> 那棵子树。
    /// </summary>
    private void PrepareTraceTargets(List<string>? remap)
    {
        _traceTargets.Clear();

        var rowOf = new Dictionary<Step, StepRow>();
        foreach (StepRow row in _rows)
            if (row.Step != null) rowOf[row.Step] = row;

        var paths = WalkPaths(_program.Steps, "", new List<(string, Step)>());
        if (remap == null)
        {
            foreach (var (path, step) in paths)
                if (rowOf.TryGetValue(step, out StepRow? row)) _traceTargets[path] = row;
            return;
        }

        for (int i = 0; i < remap.Count; i++)
        {
            string source = remap[i];
            string temp = $"/steps/{i}";
            foreach (var (path, step) in paths)
            {
                if (!path.StartsWith(source, StringComparison.Ordinal)) continue;
                if (path.Length > source.Length && path[source.Length] != '/') continue;
                if (rowOf.TryGetValue(step, out StepRow? row))
                    _traceTargets[temp + path[source.Length..]] = row;
            }
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

    /// <summary>
    /// 填「变量（本次 / 上次）」：本次 = 这次运行结束时的值，上次 = 上一次试运行的值（变了加粗 + 圆点）。
    /// 对比只在这个窗口里记，关掉即丢。
    /// </summary>
    private void FillSnapshot(ActionRunResult result)
    {
        _snapshotRows.Clear();
        foreach (SnapshotRow row in SnapshotRow.Build(result.Variables, _previousValues))
            _snapshotRows.Add(row);

        _previousValues.Clear();
        foreach (var pair in result.Variables)
            _previousValues[pair.Key] = SnapshotRow.TextOf(pair.Value);
    }

    /// <summary>双击变量快照行取到要复制的值（表头 / 空白 / 非 SnapshotRow 一律 null）。</summary>
    internal static string? SnapshotDoubleClickValue(object? item) => (item as SnapshotRow)?.Value;

    /// <summary>双击变量快照的一行 = 复制该变量运行结束时的值，方便粘到别处排查。</summary>
    private void SnapshotList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        string? value = SnapshotDoubleClickValue(row?.DataContext);
        if (value == null) return;
        try
        {
            Clipboard.SetText(value);
            SetStatus($"已复制变量值（{value.Length} 字）", hint: true);
        }
        catch { /* 剪贴板被别的程序占着就算了，别打断编辑 */ }
        e.Handled = true;
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

    /// <summary>
    /// 慢步骤（默认 30 秒）：只写在状态行上，**不打断**试运行——不可取消的长调用只能等它返回。
    /// 运行已经结束时（哪怕回调刚好晚到）不再改状态行，免得把结果覆盖掉。
    /// </summary>
    private void OnSlowStep(SlowStepInfo info)
    {
        int seconds = Math.Max(1, (int)Math.Round(info.Elapsed.TotalSeconds));
        Dispatcher.BeginInvoke(() =>
        {
            if (_running)
                SetStatus($"{info.Label}「{info.UnitName}」已运行 {seconds} 秒（仍在执行）", hint: false);
        });
    }

    private void SetStatus(string text, bool hint, bool danger = false)
    {
        RunStatus.Text = text;
        RunStatus.Foreground = (Brush)FindResource(
            danger ? "DangerBrush" : hint ? "HintTextBrush" : "TextBrush");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DiscardDrawer();
        // 取消 = 明确放弃这次编辑：自动存盘也一起清掉，别下次打开又冒出来问「恢复吗」。
        ForgetAutosave();
        DialogResult = false;
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
