using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Features.CapturePro;

namespace LTools.Views;

/// <summary>
/// 截图 Pro →「贴图窗口」页里的贴图历史策略（照 Snow Shot 的 <c>pinned_history/*</c>）：
/// 开关 / 永久保留 / 保留天数 / 条数上限 / 磁盘上限。
/// </summary>
/// <remarks>
/// 默认值沿用 LTools 既有口径（30 天 / 200 条 / 1024 MB），不与上游的 7 天 / 100 条对齐，
/// 免得升级时把用户已有的贴图历史删掉；改完立刻按新策略剪一次。
/// </remarks>
public partial class ConfigHelperWindow
{
    private bool _pinHistoryLoading;

    private void PopulatePinHistory()
    {
        _pinHistoryLoading = true;
        try
        {
            PinnedHistoryPolicy policy = PinnedHistoryStore.Current;
            PinHistoryEnabledBox.IsChecked = policy.Enabled;
            PinHistoryKeepBox.IsChecked = policy.KeepPermanently;
            PinHistoryDaysBox.Text = policy.RetentionDays.ToString(CultureInfo.InvariantCulture);
            PinHistoryEntriesBox.Text = policy.MaxEntries.ToString(CultureInfo.InvariantCulture);
            PinHistoryDiskBox.Text = policy.MaxDiskMiB.ToString(CultureInfo.InvariantCulture);

            PinHistoryDaysBox.IsEnabled = !policy.KeepPermanently;
            PinHistoryEntriesBox.IsEnabled = !policy.KeepPermanently;
            PinHistoryDiskBox.IsEnabled = !policy.KeepPermanently;

            UpdatePinHistorySummary();
        }
        finally { _pinHistoryLoading = false; }
    }

    private void UpdatePinHistorySummary()
    {
        var records = PinStore.Load();
        long bytes = records.Sum(PinStore.EntryBytes);
        PinHistorySummaryText.Text = string.Format(CultureInfo.CurrentCulture,
            "当前 {0} 条，占用 {1:0.#} MB。管理页在贴图右键菜单「贴图管理…」与托盘菜单里。",
            records.Count, bytes / 1024.0 / 1024.0);
    }

    private void PinHistory_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing || _pinHistoryLoading) return;

        PinnedHistoryStore.SetEnabled(PinHistoryEnabledBox.IsChecked == true);
        PinnedHistoryStore.SetKeepPermanently(PinHistoryKeepBox.IsChecked == true);

        bool locked = PinHistoryKeepBox.IsChecked == true;
        PinHistoryDaysBox.IsEnabled = !locked;
        PinHistoryEntriesBox.IsEnabled = !locked;
        PinHistoryDiskBox.IsEnabled = !locked;

        UpdatePinHistorySummary();
    }

    private void PinHistoryNumber_LostFocus(object sender, RoutedEventArgs e) => CommitPinHistoryNumbers();

    private void PinHistoryNumber_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitPinHistoryNumbers();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void CommitPinHistoryNumbers()
    {
        if (_initializing || _pinHistoryLoading) return;

        PinnedHistoryPolicy current = PinnedHistoryStore.Current;
        int days = ReadPinHistoryNumber(PinHistoryDaysBox, current.RetentionDays,
            PinnedHistoryStore.MinRetentionDays, PinnedHistoryStore.MaxRetentionDays);
        int entries = ReadPinHistoryNumber(PinHistoryEntriesBox, current.MaxEntries,
            PinnedHistoryStore.MinEntries, PinnedHistoryStore.MaxEntries);
        int disk = ReadPinHistoryNumber(PinHistoryDiskBox, current.MaxDiskMiB,
            PinnedHistoryStore.MinDiskMiB, PinnedHistoryStore.MaxDiskMiB);

        if (days != current.RetentionDays) PinnedHistoryStore.SetRetentionDays(days);
        if (entries != current.MaxEntries) PinnedHistoryStore.SetMaxEntries(entries);
        if (disk != current.MaxDiskMiB) PinnedHistoryStore.SetMaxDiskMiB(disk);

        PinHistoryDaysBox.Text = days.ToString(CultureInfo.InvariantCulture);
        PinHistoryEntriesBox.Text = entries.ToString(CultureInfo.InvariantCulture);
        PinHistoryDiskBox.Text = disk.ToString(CultureInfo.InvariantCulture);
        UpdatePinHistorySummary();
    }

    private static int ReadPinHistoryNumber(TextBox box, int fallback, int min, int max)
    {
        string text = box.Text.Trim();
        if (text.Length == 0) return fallback;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            return fallback;
        return Math.Clamp(value <= 0 ? fallback : value, min, max);
    }
}
