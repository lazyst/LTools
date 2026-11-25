using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Storage;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 动作的历史版本：左列「每次保存前的样子」，右侧「和当前动作的差异」。
/// </summary>
/// <remarks>
/// <para>
/// 快照由 <see cref="ActionStore"/> 在内容真的变化时自动存（<see cref="ActionHistory"/>），
/// 所以这里的列表就是「这个动作最近 10 次改动前后的样子」。
/// </para>
/// <para>
/// 只返回选中的那份内容（<see cref="Result"/>），落盘由调用方走 <c>ActionWrites.Update</c>：
/// 恢复本身也会产生一条新快照，所以「恢复错了」还能再恢复回来。
/// </para>
/// </remarks>
public partial class ActionHistoryDialog : Window
{
    private readonly ActionDto _current;
    private readonly List<HistoryRow> _rows;

    /// <summary>用户选中并确认恢复的那份内容（<c>Id</c> 已对齐到当前动作）。</summary>
    public ActionDto? Result { get; private set; }

    public ActionHistoryDialog(Window? owner, ActionDto current)
    {
        InitializeComponent();

        _current = current;
        Title = $"历史版本 · {current.Name}";
        if (owner != null) Owner = owner;
        else Topmost = true;

        HeaderText.Text = $"动作「{current.Name}」最近 {ActionHistory.Keep} 次改动前的版本。";
        HintText.Text = "恢复会把这份内容写回动作（当前版本会自动进历史，恢复错了还能退回来）。"
                        + "历史只存步骤与变量，模板图以动作目录里当前的为准。";

        _rows = ActionHistory.List(ActionStore.Anchor, current.Id)
            .Select(entry => new HistoryRow
            {
                Entry = entry,
                TimeText = entry.SavedAt.ToString("MM-dd HH:mm:ss"),
                Detail = $"{entry.StepCount} 步 · {entry.Name}",
            })
            .ToList();

        HistoryList.ItemsSource = _rows;
        if (_rows.Count == 0)
            HintText.Text = "还没有历史版本——改一次这个动作（保存后）就会出现。";
        else
            HistoryList.SelectedIndex = 0;
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row)
        {
            Diff.Visibility = Visibility.Collapsed;
            EmptyText.Visibility = Visibility.Visible;
            RestoreBtn.IsEnabled = false;
            return;
        }

        ActionDto? snapshot = ActionHistory.Read(ActionStore.Anchor, _current.Id, row.Entry.File);
        if (snapshot == null)
        {
            EmptyText.Text = "这个历史版本已经读不出来了（文件被删掉或改坏）。";
            EmptyText.Visibility = Visibility.Visible;
            Diff.Visibility = Visibility.Collapsed;
            RestoreBtn.IsEnabled = false;
            return;
        }

        EmptyText.Visibility = Visibility.Collapsed;
        Diff.Visibility = Visibility.Visible;
        var lines = ProgramDiff.Compare(snapshot.Program, _current.Program);
        Diff.Fill(lines);

        var (added, removed, changed) = ProgramDiff.Count(lines);
        HintText.Text = lines.Count == 0
            ? "这个版本和当前动作完全一样。"
            : $"和当前动作相比：新增 {added} 处 · 删除 {removed} 处 · 修改 {changed} 处（恢复 = 用它替换当前内容）。";
        RestoreBtn.IsEnabled = true;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        ActionDto? snapshot = ActionHistory.Read(ActionStore.Anchor, _current.Id, row.Entry.File);
        if (snapshot == null)
        {
            ConfirmDialog.Info(this, "历史版本", "这个历史版本已经读不出来了。");
            return;
        }

        snapshot.Id = _current.Id;      // 动作 Id 是存储身份，别让历史里的旧 Id 改名
        Result = snapshot;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

/// <summary>历史版本列表的一行。</summary>
internal sealed class HistoryRow
{
    public ActionHistoryEntry Entry { get; init; } = new("", DateTime.MinValue, "", 0);

    public string TimeText { get; init; } = "";

    public string Detail { get; init; } = "";
}
