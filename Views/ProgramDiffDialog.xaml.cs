using System.Windows;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 「改之前先看清楚」：两份动作内容的步骤级差异（草稿采纳前 / 历史版本恢复前）。
/// </summary>
/// <remarks>
/// 只负责展示与「继续 / 取消」，不落盘——采纳与恢复仍走各自的既有路径
/// （<see cref="LTools.Core.Ai.DraftPromoter"/> / <c>ActionWrites.Update</c>）。
/// </remarks>
public partial class ProgramDiffDialog : Window
{
    /// <param name="owner">宿主窗口；空 = 置顶（面板已关 / 无窗口上下文时用，规则同 <c>ConfirmDialog.ShouldTopmost</c>：无 Owner 才置顶）。</param>
    /// <param name="title">窗标题。</param>
    /// <param name="summary">一句话说明这次对比的是哪两份内容。</param>
    /// <param name="okText">确认按钮文案（「覆盖」/「恢复此版本」）。</param>
    public ProgramDiffDialog(Window? owner, string title, string summary,
        ActionProgram? before, ActionProgram? after, string okText)
    {
        InitializeComponent();

        Title = title;
        if (owner != null) Owner = owner;
        else Topmost = true;

        HeaderText.Text = summary;
        OkBtn.Content = okText;

        var lines = ProgramDiff.Compare(before, after);
        var (added, removed, changed) = ProgramDiff.Count(lines);
        CountText.Text = lines.Count == 0
            ? "两份内容完全一样，没有可采纳的改动。"
            : $"新增 {added} 处 · 删除 {removed} 处 · 修改 {changed} 处（按动作单元对齐，缩进 = 子步骤层级）";
        Diff.Fill(lines);

        // 内容一样就别让人点「覆盖」白跑一趟（恢复历史版本时尤其容易碰上）。
        OkBtn.IsEnabled = lines.Count > 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
