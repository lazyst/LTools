using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 截图 Pro →「采集历史」页：照 Snow Shot 的 <c>capture_history/*</c> 五项
/// （开关 / 永久保留 / 保留天数 / 条数上限 / 磁盘上限）。
/// </summary>
/// <remarks>
/// 数字框只在失焦 / 回车时落盘（每敲一键写一次会顶住 UI 线程），越界按范围夹取；
/// 改完立刻按新策略剪一次（<see cref="CaptureHistoryStore"/> 里调 <c>CaptureCatalog.Maintain</c>）。
/// </remarks>
public partial class ConfigHelperWindow
{
    private bool _historyLoading;

    private void PopulateCaptureHistory()
    {
        _historyLoading = true;
        try
        {
            CaptureHistoryPolicy policy = CaptureHistoryStore.Current;
            CaptureHistoryEnabledBox.IsChecked = policy.Enabled;
            CaptureHistoryKeepBox.IsChecked = policy.KeepPermanently;
            CaptureHistoryDaysBox.Text = policy.RetentionDays.ToString(CultureInfo.InvariantCulture);
            CaptureHistoryEntriesBox.Text = policy.MaxEntries.ToString(CultureInfo.InvariantCulture);
            CaptureHistoryDiskBox.Text = policy.MaxDiskMiB.ToString(CultureInfo.InvariantCulture);

            // 三个数字框跟着「永久保留」一起置灰（永久保留时不参与清理，但值仍记着）
            CaptureHistoryDaysBox.IsEnabled = !policy.KeepPermanently;
            CaptureHistoryEntriesBox.IsEnabled = !policy.KeepPermanently;
            CaptureHistoryDiskBox.IsEnabled = !policy.KeepPermanently;

            UpdateHistorySummary();
        }
        finally { _historyLoading = false; }
    }

    private void UpdateHistorySummary()
    {
        int count = CaptureCatalog.Load().Count;
        long bytes = CaptureCatalog.Load().Sum(CaptureCatalog.EntryBytes);        double mib = bytes / 1024.0 / 1024.0;
        CaptureHistorySummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "当前 {0} 条，占用 {1:0.#} MB。采集历史窗口在截图覆盖层的「更多」菜单里（上一张 / 下一张 / 采集历史窗口…）。",
            count, mib);
    }

    private void CaptureHistory_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _historyLoading) return;

        CaptureHistoryStore.SetEnabled(CaptureHistoryEnabledBox.IsChecked == true);
        CaptureHistoryStore.SetKeepPermanently(CaptureHistoryKeepBox.IsChecked == true);

        // 「永久保留」一变，三个数字框跟着可用 / 置灰
        bool locked = CaptureHistoryKeepBox.IsChecked == true;
        CaptureHistoryDaysBox.IsEnabled = !locked;
        CaptureHistoryEntriesBox.IsEnabled = !locked;
        CaptureHistoryDiskBox.IsEnabled = !locked;

        UpdateHistorySummary();
    }

    private void CaptureHistoryNumber_LostFocus(object sender, RoutedEventArgs e) => CommitHistoryNumbers();

    private void CaptureHistoryNumber_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHistoryNumbers();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    /// <summary>三个数字一起提交（失焦一次只写一遍 settings.json）。</summary>
    private void CommitHistoryNumbers()
    {
        if (_initializing || _historyLoading) return;

        CaptureHistoryPolicy current = CaptureHistoryStore.Current;
        int days = ReadNumber(CaptureHistoryDaysBox, current.RetentionDays,
            CaptureHistoryStore.MinRetentionDays, CaptureHistoryStore.MaxRetentionDays);
        int entries = ReadNumber(CaptureHistoryEntriesBox, current.MaxEntries,
            CaptureHistoryStore.MinEntries, CaptureHistoryStore.MaxEntries);
        int disk = ReadNumber(CaptureHistoryDiskBox, current.MaxDiskMiB,
            CaptureHistoryStore.MinDiskMiB, CaptureHistoryStore.MaxDiskMiB);

        if (days != current.RetentionDays) CaptureHistoryStore.SetRetentionDays(days);
        if (entries != current.MaxEntries) CaptureHistoryStore.SetMaxEntries(entries);
        if (disk != current.MaxDiskMiB) CaptureHistoryStore.SetMaxDiskMiB(disk);

        CaptureHistoryDaysBox.Text = days.ToString(CultureInfo.InvariantCulture);
        CaptureHistoryEntriesBox.Text = entries.ToString(CultureInfo.InvariantCulture);
        CaptureHistoryDiskBox.Text = disk.ToString(CultureInfo.InvariantCulture);
        UpdateHistorySummary();
    }

    private static int ReadNumber(TextBox box, int fallback, int min, int max)
    {
        string text = box.Text.Trim();
        if (text.Length == 0) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return fallback;
        return Math.Clamp(value <= 0 ? fallback : value, min, max);
    }
}
