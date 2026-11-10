using System;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>
/// 关闭时的「未保存改动」检查。
/// </summary>
/// <remarks>
/// <para>
/// 打开编辑器时记一份内容快照，关闭时（标题栏 ✕ / Esc / 取消）比对：不一样就问一句
/// 「保存 / 不保存 / 取消」（<see cref="ConfirmDialog.ConfirmDiscard"/>）。
/// 用快照而不是逐处打脏标记：改动入口很多（名称 / 图标 / 变量表 / 步骤 / 撤销），
/// 漏一个就会静默丢改动；快照还会自动把「改完又改回来」当成没改。
/// </para>
/// <para>
/// 选「保存」走与「确定」相同的 <see cref="Commit"/>（校验不过就留在编辑器里）。
/// 那次关闭先拦下，再按「确定」的路径重新关：<see cref="Window.DialogResult"/> 才会是
/// <c>true</c>，子动作库那类「ShowDialog() == true 才算保存」的调用方不会把结果当取消。
/// </para>
/// </remarks>
public partial class ActionEditorWindow
{
    /// <summary>打开时的内容快照（名称 / 图标 / 变量表 / 步骤）；和它一样就是「没改过」。</summary>
    private string _baseline = "";

    /// <summary>已经收进 <see cref="Result"/>（点过确定，或关闭时选了保存）：之后关闭不再追问。</summary>
    private bool _saved;

    /// <summary>载入完成后记基线：此刻的样子就是「没改过」。</summary>
    private void CaptureBaseline() => _baseline = Snapshot();

    private bool IsDirty() => !string.Equals(_baseline, Snapshot(), StringComparison.Ordinal);

    /// <summary>
    /// 当前内容的一份可比较快照。取的就是保存会写进去的东西：
    /// 名称 / 图标 / 变量表每一行（名字·类型·方向·默认值）/ 步骤树。
    /// </summary>
    private string Snapshot()
    {
        var sb = new StringBuilder();
        sb.Append(NameBox.Text ?? "").Append('\n');
        sb.Append(IconPicker.SelectedIcon ?? "").Append('\n');

        foreach (var row in _variables)
        {
            sb.Append(row.Name.Text ?? "").Append('\u001f')
                .Append((row.Type.SelectedItem as KindChoice)?.Kind)
                .Append('\u001f')
                .Append((row.Direction?.SelectedItem as DirectionChoice)?.Direction)
                .Append('\u001f')
                .Append(row.Default.Text ?? "")
                .Append('\n');
        }

        // 步骤是纯数据（Step 自带 JSON 形状），直接序列化比手写比较稳。
        sb.Append(JsonSerializer.Serialize(_program.Steps, ConfigBundle.Options));
        return sb.ToString();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_saved || !IsDirty())
        {
            base.OnClosing(e);
            return;
        }

        switch (ConfirmDialog.ConfirmDiscard(this, _isSubAction ? "子动作" : "动作",
                    "有还没保存的改动，要保存吗？"))
        {
            case SaveConfirmResult.Save:
                if (!Commit())
                {
                    // 名字 / 变量默认值不合格：留在编辑器里改，别关掉丢改动。
                    e.Cancel = true;
                    return;
                }

                // 这次关闭先拦下，再走「确定」的路径重新关（DialogResult = true）。
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        DialogResult = true;
                    }
                    catch (InvalidOperationException)
                    {
                        // 非模态打开时（调试 / 冒烟用 Show()）没有 DialogResult，直接关即可。
                        Close();
                    }
                }));
                return;

            case SaveConfirmResult.Discard:
                base.OnClosing(e);
                return;

            default:
                e.Cancel = true;
                return;
        }
    }
}
