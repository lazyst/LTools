using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features.Running;

namespace LTools.Views;

/// <summary>
/// 「运行中的动作」窗口：逐项看 / 逐项停 / 全部停止。见 <c>docs/RUN_INTEROP.md</c> §8.2。
/// </summary>
/// <remarks>
/// <para>
/// 数据来自**注册表**（<see cref="RunCoordinator.Snapshot"/>），1 秒刷一次；窗口与注册表在同一个进程
/// （托盘与运行指示器都在提权进程），所以这里不需要链路命令。
/// </para>
/// <para>
/// 入口：托盘右键「运行中的动作…（N）」与指示器折叠行的「全部…」。
/// </para>
/// </remarks>
public partial class RunningActionsWindow : Window
{
    private static RunningActionsWindow? _window;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>一行（绑定用，属性必须 public）。</summary>
    public sealed class Row
    {
        public required string RunId { get; init; }
        public required string Label { get; init; }
        public required string Source { get; init; }
        public required string Elapsed { get; init; }
        public required string Step { get; init; }
        public required Visibility StopVisibility { get; init; }
    }

    public RunningActionsWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _window = null;
        };
    }

    /// <summary>打开（已开着就提到前面）。</summary>
    public static void Toggle()
    {
        if (_window != null)
        {
            WindowFront.Bring(_window);
            return;
        }

        var window = new RunningActionsWindow();
        _window = window;
        WindowFront.Show(window);
    }

    private void Refresh()
    {
        IReadOnlyList<ActionRunSession> runs = RunCoordinator.Snapshot();
        long now = Environment.TickCount64;

        RunList.ItemsSource = runs
            .OrderBy(r => r.StartedTick)
            .Select(r => new Row
            {
                RunId = r.RunId,
                Label = r.Label,
                Source = RunHistory.SourceLabel(RunHistory.SourceKey(r.Source)),
                Elapsed = Elapsed(now - r.StartedTick),
                Step = r.StopRequested || r.Cts.IsCancellationRequested
                    ? "正在停止"
                    : r.Step > 0 ? $"第 {r.Step} 步" : "准备中",
                StopVisibility = Visibility.Visible,
            })
            .ToList();

        bool any = runs.Count > 0;
        HeaderText.Text = any
            ? $"正在运行 {runs.Count} 个动作（同一动作的多份会显示 #2、#3）"
            : "现在没有正在运行的动作";
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        RunList.Visibility = any ? Visibility.Visible : Visibility.Hidden;
        StopAllBtn.IsEnabled = any;
    }

    private static string Elapsed(long ms)
    {
        long seconds = Math.Max(0, ms / 1000);
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private void StopOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Row row } && !string.IsNullOrEmpty(row.RunId))
            RunningActions.Cancel(row.RunId);
        Refresh();
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        RunCoordinator.CancelAll();
        Refresh();
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        // 设置窗口在宿主（普通权限）里：这里只发一条「打开运行历史页」的意图。
        LinkWorker.RequestSettings(new SettingsOpenRequest(SettingsActions.Open, "logs-history"));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
