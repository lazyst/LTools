using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core;

namespace LTools.Views;

/// <summary>
/// 设置 →「运行日志」→「运行历史」页（P2-2）：最近 N 次运行的时间 / 动作 / 来源 / 结果 / 耗时 /
/// 失败位置，右侧是失败步骤分布。
/// </summary>
/// <remarks>
/// 从 <c>ConfigHelper.Logs.cs</c> 再拆一层（那边只管日志的开关 / 滚动 / 预览）。
/// 数据与统计都来自 <see cref="RunHistory"/>（一次运行一行、上限 500 条），
/// **这里不另写一套读写，也不解析 <c>logs/</c>**：完整轨迹是「最近日志 → 打开失败报告」的事，
/// 本页答的是「最近老在哪儿失败、这个动作多久跑一次」。
/// </remarks>
public partial class ConfigHelperWindow
{
    /// <summary>表格最多显示多少行（历史留 500 条，屏幕上也看不完；完整数据走「复制摘要」）。</summary>
    private const int HistoryRows = 200;

    private readonly ObservableCollection<RunRow> _runs = new();
    private IReadOnlyList<RunRecord> _runRecords = Array.Empty<RunRecord>();

    /// <summary>读历史文件、重填表格 / 统计 / 失败分布（切到本页、点刷新、清空后各调一次）。</summary>
    private void LoadRunHistory()
    {
        var records = RunHistory.Load();
        _runRecords = records;

        if (RunHistoryGrid.ItemsSource == null) RunHistoryGrid.ItemsSource = _runs;
        _runs.Clear();
        foreach (var r in records.Take(HistoryRows)) _runs.Add(new RunRow(r));

        var stats = RunHistory.Summarize(records.Take(RunHistory.StatsWindow).ToArray());
        RunHistoryStats.Text = records.Count == 0
            ? "（还没有运行过：跑一次动作就会出现在这里——面板 / 快捷键 / 悬浮 / 自动触发 / 编辑器试运行都算）"
            : $"最近 {stats.Total} 次：成功 {stats.Ok} · 失败 {stats.Failed} · 停止 {stats.Stopped} · " +
              $"平均 {stats.AverageMs} ms · 最慢 {stats.SlowestMs} ms";

        RunHistoryDist.ItemsSource = stats.Failures.Select(g => new DistRow(g)).ToArray();
        RunHistoryDistHint.Text = stats.Failures.Count > 0
            ? "按次数排序，最多 8 组"
            : records.Count == 0 ? "" : "最近这段没有失败。";

        RunHistoryHint.Text =
            $"共 {records.Count} 条（最多留 {RunHistory.MaxRecords} 条）· 表格显示最近 " +
            $"{Math.Min(records.Count, HistoryRows)} 条 · 双击一行复制它 · " +
            "完整轨迹在「最近日志 → 打开失败报告」里。";
    }

    private void RunHistoryRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadRunHistory();
        ShowStatus("运行历史已刷新", ok: true);
    }

    private void RunHistoryCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(RunHistory.BuildSummaryText(_runRecords));
            ShowStatus("运行历史摘要已复制到剪贴板", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行历史", "复制失败：" + ex.Message);
        }
    }

    private void RunHistoryClear_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "清空运行历史",
                "确定清空全部运行历史吗？\n只删这份统计记录，失败报告与日志文件都不动。",
                danger: true)) return;

        RunHistory.Clear();
        LoadRunHistory();
        ShowStatus("运行历史已清空", ok: true);
    }

    /// <summary>
    /// 双击是否落在一条运行记录上：取命中的 <see cref="DataGridRow"/>，
    /// 表头 / 空白处取不到行就返回 null（不复制上一次选中的那条）。
    /// 抽成纯函数方便无头冒烟直接断言，不用伪造鼠标事件。
    /// </summary>
    internal static RunRow? DoubleClickRunRow(DependencyObject? src) =>
        src == null ? null : FindAncestor<DataGridRow>(src)?.DataContext as RunRow;

    private void RunHistoryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DoubleClickRunRow(e.OriginalSource as DependencyObject) is not { } row) return;

        try
        {
            Clipboard.SetText(row.Detail);
            ShowStatus("这条运行记录已复制", ok: true);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "运行历史", "复制失败：" + ex.Message);
        }
    }

    /// <summary>运行历史表格的一行（只读显示模型，不写回）。</summary>
    public sealed class RunRow
    {
        internal RunRow(RunRecord r)
        {
            Time = r.Time;
            Action = r.Action;
            Source = RunHistory.SourceLabel(r.Source);
            Result = RunHistory.StatusText(r.Status);
            Elapsed = r.ElapsedMs + " ms";
            Step = r.Step.Length == 0
                ? "—"
                : r.Step + (r.Unit.Length == 0 ? "" : $"「{r.Unit}」");
            Error = r.Error.Length == 0 ? "—" : r.Error;

            Detail = $"{r.Time} · {Result} · {r.ElapsedMs} ms · 「{r.Action}」· {Source}"
                     + (r.Step.Length == 0 ? "" : " · " + Step)
                     + (r.Error.Length == 0 ? "" : " · " + r.Error);
        }

        public string Time { get; }
        public string Action { get; }
        public string Source { get; }
        public string Result { get; }
        public string Elapsed { get; }
        public string Step { get; }
        public string Error { get; }

        /// <summary>双击复制的那一行（含错误全文）。</summary>
        public string Detail { get; }
    }

    /// <summary>失败步骤分布的一行。</summary>
    public sealed class DistRow
    {
        internal DistRow(FailureGroup g)
        {
            Count = g.Count;
            Text = g.Describe;
        }

        public int Count { get; }
        public string Text { get; }
    }
}
