using System;
using System.Windows;
using LTools.Core;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 宏动作横幅：这个动作就是一段录制的宏（单步「回放宏」）时，在步骤列表上方显示
/// 宏名 / 时长 / 事件数，并提供「重新录制」与「键鼠宏设置」两个入口。
/// </summary>
/// <remarks>
/// 判定用 <see cref="MacroAction.MacroIdOf(ActionProgram)"/>（结构判定），所以它随步骤列表一起刷新：
/// 用户把唯一那步改成别的动作单元、或换了一个宏，横幅立刻跟着消失 / 更新。
/// 重录沿用同一个宏 Id，动作与面板 / 快捷键上的引用都不用重建。
/// </remarks>
public partial class ActionEditorWindow
{
    private string? _macroId;
    private bool _macroHidden;

    /// <summary>按当前动作体刷新横幅（由 <c>ReloadRows</c> 调用）。</summary>
    private void UpdateMacroPanel()
    {
        _macroId = MacroAction.MacroIdOf(_program);
        if (_macroId == null)
        {
            MacroPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var meta = MacroStore.MetaOf(_macroId);
        MacroPanel.Visibility = Visibility.Visible;
        MacroTitle.Text = meta == null ? "宏文件缺失" : meta.Name;
        MacroMetaText.Text = meta == null
            ? "这段宏的事件文件已经不在了：重新录一遍，或到「键鼠宏」页删除它"
            : $"{meta.DurationText} · {meta.EventCount} 事件 · 录制于 {meta.UpdatedAt}";
    }

    /// <summary>重新录制：窗口先让开（录制期间钩子透明，编辑器上的点击也会被录进去），停止后恢复。</summary>
    private void MacroRerecord_Click(object sender, RoutedEventArgs e)
    {
        if (_macroId == null || MacroRecorder.IsRecording) return;

        _macroHidden = true;
        Hide();
        MacroRecorder.StartInto(_macroId, MacroTitle.Text);
        if (!MacroRecorder.IsRecording) RestoreAfterMacroRecording();   // 被拒绝（没启用 / 正在回放）
    }

    private void MacroSettings_Click(object sender, RoutedEventArgs e) => ConfigHelper.OpenMacros();

    private void OnMacroRecordingStopped()
    {
        if (!_macroHidden) return;
        RestoreAfterMacroRecording();
        UpdateMacroPanel();
    }

    /// <summary>宏落盘完成（后台线程）：元数据变了，横幅与步骤摘要一起刷新。</summary>
    private void OnMacroSavedRefresh(string id)
    {
        if (!string.Equals(id, _macroId, StringComparison.Ordinal)) return;
        Dispatcher.BeginInvoke(() =>
        {
            MacroStore.Invalidate();
            if (string.Equals(id, _macroId, StringComparison.Ordinal)) ReloadRows();
        });
    }

    private void RestoreAfterMacroRecording()
    {
        if (!_macroHidden) return;
        _macroHidden = false;
        try
        {
            Show();
            Activate();
        }
        catch (Exception ex)
        {
            CrashLog.Write("ActionEditorWindow.Macro", ex);
        }
    }
}
