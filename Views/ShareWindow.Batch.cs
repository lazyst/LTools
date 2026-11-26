using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LTools.Views;

/// <summary>ShareWindow 的「批量管理」部分：发出去的 / 收到的两组各自多选、全选、删除。</summary>
/// <remarks>
/// 勾选状态存在 <c>_pickedOut</c> / <c>_pickedIn</c>（按行键），列表重建时按它回填，
/// 所以 1 秒轮询刷新不会把勾选冲掉。<c>BatchBtn</c> 是「批量管理 / 完成」同一个按钮，
/// 进入后多出「全选」「删除选中 (N)」。
/// </remarks>
public partial class ShareWindow : Window
{
    private bool _batchOut;
    private bool _batchIn;
    private readonly HashSet<string> _pickedOut = new();
    private readonly HashSet<string> _pickedIn = new();

    // ============ 进入 / 退出、全选 ============

    private void OutBatch_Click(object sender, RoutedEventArgs e) => ToggleBatch(incoming: false);

    private void InBatch_Click(object sender, RoutedEventArgs e) => ToggleBatch(incoming: true);

    private void ToggleBatch(bool incoming)
    {
        if (incoming)
        {
            _batchIn = !_batchIn;
            if (!_batchIn) _pickedIn.Clear();
        }
        else
        {
            _batchOut = !_batchOut;
            if (!_batchOut) _pickedOut.Clear();
        }

        ReloadLists();
    }

    private void OutAll_Click(object sender, RoutedEventArgs e) => PickAll(incoming: false);

    private void InAll_Click(object sender, RoutedEventArgs e) => PickAll(incoming: true);

    private void PickAll(bool incoming)
    {
        HashSet<string> picked = incoming ? _pickedIn : _pickedOut;
        List<ShareItemRow> rows = Rows(incoming);

        if (picked.Count > 0 && picked.Count >= rows.Count) picked.Clear();
        else foreach (ShareItemRow row in rows) picked.Add(row.Key);

        SyncPickedRows();
        SyncBatchButtons();
    }

    // ============ 单行勾选 ============

    private void RowPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShareItemRow row }) return;

        HashSet<string> picked = Picked(row.Key);
        if (row.Selected) picked.Add(row.Key);
        else picked.Remove(row.Key);

        SyncBatchButtons();
    }

    /// <summary>整行点一下（批量模式下）：勾上 / 取消。</summary>
    private void ToggleRowPick(ShareItemRow row)
    {
        row.Selected = !row.Selected;
        HashSet<string> picked = Picked(row.Key);
        if (row.Selected) picked.Add(row.Key);
        else picked.Remove(row.Key);

        SyncBatchButtons();
    }

    private HashSet<string> Picked(string key) =>
        key.StartsWith("item:", StringComparison.Ordinal) ? _pickedOut : _pickedIn;

    /// <summary>这一行属于哪一组、那组是不是批量模式。</summary>
    private bool IsBatchRow(string key) =>
        key.StartsWith("item:", StringComparison.Ordinal) ? _batchOut : _batchIn;

    // ============ 删除选中 ============

    private void OutDelete_Click(object sender, RoutedEventArgs e)
    {
        List<ShareItemRow> rows = Rows(incoming: false).Where(r => r.Selected).ToList();
        if (rows.Count == 0) return;

        int copies = rows.Count(r => _store.Find(r.Key[5..])?.Copied == true);
        string message = copies > 0
            ? $"其中 {copies} 项是副本，会连文件一起删；其余只从共享列表移除，原文件不动。"
            : "只从共享列表移除，原文件不会被删除。";

        if (!ConfirmDialog.Confirm(this, $"移除选中的 {rows.Count} 项？", message, okText: "移除")) return;

        foreach (ShareItemRow row in rows) _store.Remove(row.Key[5..]);
        _pickedOut.Clear();
        _batchOut = false;
        ReloadLists();
    }

    private void InDelete_Click(object sender, RoutedEventArgs e)
    {
        List<ShareItemRow> rows = Rows(incoming: true).Where(r => r.Selected).ToList();
        if (rows.Count == 0) return;

        if (!ConfirmDialog.Confirm(this, $"删除收到的 {rows.Count} 个文件？",
                "这些文件会从本机删掉，不能恢复。", danger: true, okText: "删除")) return;

        foreach (ShareItemRow row in rows) _store.RemoveInbox(row.Key[6..]);
        _pickedIn.Clear();
        _batchIn = false;
        ReloadLists();
    }

    // ============ 状态同步 ============

    private List<ShareItemRow> Rows(bool incoming) =>
        (incoming ? InList : OutList).Items.Cast<ShareItemRow>().ToList();

    /// <summary>把两组列表里每一行的勾选状态按集合摆正（全选 / 重建后调）。</summary>
    private void SyncPickedRows()
    {
        List<ShareItemRow> outRows = OutList.Items.Cast<ShareItemRow>().ToList();
        List<ShareItemRow> inRows = InList.Items.Cast<ShareItemRow>().ToList();

        // 条目自己消失（外部删除 / 换目录）时把勾选一起摘掉，不然计数会多出来。
        _pickedOut.RemoveWhere(key => outRows.All(r => r.Key != key));
        _pickedIn.RemoveWhere(key => inRows.All(r => r.Key != key));

        foreach (ShareItemRow row in outRows) row.Selected = _pickedOut.Contains(row.Key);
        foreach (ShareItemRow row in inRows) row.Selected = _pickedIn.Contains(row.Key);
    }

    /// <summary>刷新按钮文案与显隐（列表重建、勾选变化后都调）。</summary>
    private void SyncBatchButtons()
    {
        SyncGroup(incoming: false);
        SyncGroup(incoming: true);
    }

    private void SyncGroup(bool incoming)
    {
        bool on = incoming ? _batchIn : _batchOut;
        List<ShareItemRow> rows = (incoming ? InList : OutList).Items.Cast<ShareItemRow>().ToList();
        if (rows.Count == 0 && on)
        {
            // 删空 / 条目自己消失后不留一个「还在批量管理」的空壳：下次来新内容不该直接带勾选框。
            if (incoming) { _batchIn = false; _pickedIn.Clear(); }
            else { _batchOut = false; _pickedOut.Clear(); }
            on = false;
        }

        int total = rows.Count;
        int picked = rows.Count(r => r.Selected);

        Button batch = incoming ? InBatchBtn : OutBatchBtn;
        Button all = incoming ? InAllBtn : OutAllBtn;
        Button remove = incoming ? InDeleteBtn : OutDeleteBtn;

        batch.Content = on ? "完成" : "批量管理";
        batch.Visibility = total > 0 || on ? Visibility.Visible : Visibility.Collapsed;
        all.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        remove.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        all.Content = picked > 0 && picked >= total ? "取消全选" : "全选";
        remove.Content = picked > 0 ? $"删除选中 ({picked})" : "删除选中";
        remove.IsEnabled = picked > 0;
    }

    /// <summary>列表重建：清掉签名，让下一次 Refresh 真的重排两行表。</summary>
    private void ReloadLists()
    {
        _listSignature = "";
        Refresh();
    }
}
