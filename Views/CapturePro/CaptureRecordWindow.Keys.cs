using System;
using System.Windows;
using System.Windows.Controls;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏面板的键盘快捷键（照 Snow Shot 的 <c>screenrecordingshortcutcontroller.cpp</c>）：
/// Ctrl+E 结束并导出、Ctrl+S 开始 / 暂停 / 继续、Ctrl+C 复制、Esc 关闭、R / Shift+R 翻录屏区域，
/// Ctrl+Z / Ctrl+Y 撤销重做，以及绘制工具的 V / 1…9 / P·H·T·N·F·E。
/// </summary>
/// <remarks>
/// <para>
/// 面板刻意不抢焦点（<c>WS_EX_NOACTIVATE</c>），拿不到 <c>PreviewKeyDown</c>，所以键由全局钩子
/// 投递（<see cref="RecordShortcutRoute"/>），作用范围 = **指针停在面板上**（或空闲期的区域框里）：
/// 用户在被录的程序里按 Esc / Ctrl+C 不会被吃掉。见 <see cref="RecordShortcuts"/> 的类注释。
/// </para>
/// <para>
/// 这里只做「把动作落到既有的按钮逻辑上」，不另写一套 —— 动作能不能触发由
/// <see cref="RecordShortcuts.CanActivate"/> 先按按钮状态算过（跟上游「快捷键 = 点按钮」一致）。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    /// <summary>翻区域历史翻到第几条（-1 = 还没翻过；用户自己拖过区域就复位）。</summary>
    private int _historyIndex = -1;

    /// <summary>快捷键快照（钩子线程只读；UI 线程每拍重发一份）。</summary>
    private void PublishShortcuts()
    {
        if (_closing || _hwnd == IntPtr.Zero) return;

        bool exportRow = StyleBar != null && StyleBar.Visibility == Visibility.Visible;
        bool idle = !_recording;

        var state = new RecordShortcutState
        {
            Recording = _recording && !_controller.Finished,
            Paused = _recording && _controller.IsPaused,
            Busy = _recording && _controller.Finished,
            HasLastRecord = _hasLastRecord,
            ExportRowVisible = exportRow,
            AreaHistory = idle && exportRow && RecordAreaHistory.AnyOther(_area),
            CanUndo = Annotations?.CanUndo == true,
            CanRedo = Annotations?.CanRedo == true,
            DrawingAllowed = !_controller.Finished,
        };

        RecordShortcutRoute.Publish(state, IsPointerHot(), Perform);
    }

    /// <summary>
    /// 指针是不是停在我们的 UI 上：面板矩形（含四周 40 DIP，覆盖挂在它旁边的弹层），
    /// 或者空闲期的录屏区域框。钩子线程不碰这里，只有 UI 线程会调。
    /// </summary>
    private bool IsPointerHot()
    {
        if (!Win32.GetCursorPos(out var point)) return false;

        double scale = ScreenOf(_area) is { Scale: > 0 } screen ? screen.Scale : 1;
        int pad = (int)Math.Round(40 * scale);

        if (_hwnd != IntPtr.Zero && Win32.GetWindowRect(_hwnd, out var bar))
        {
            if (point.X >= bar.Left - pad && point.X < bar.Right + pad
                && point.Y >= bar.Top - pad && point.Y < bar.Bottom + pad)
            {
                return true;
            }
        }

        // 空闲期：区域框也是我们的 UI（拖边、看尺寸时按 R / Esc 同样应该生效）
        if (_region != null)
        {
            return point.X >= _area.X && point.X < _area.X + _area.Width
                   && point.Y >= _area.Y && point.Y < _area.Y + _area.Height;
        }

        return false;
    }

    /// <summary>执行一个快捷键动作（UI 线程；钩子只负责投递）。</summary>
    private void Perform(RecordShortcutAction action)
    {
        if (_closing) return;

        switch (action)
        {
            case RecordShortcutAction.Export:
                if (!_recording || _controller.Finished) return;
                _controller.Stop();
                Sync();
                return;

            case RecordShortcutAction.ToggleRecording:
                if (!_recording)
                {
                    CloseRegion();
                    _gate?.Start();
                    return;
                }

                if (_controller.Finished) return;
                _controller.TogglePause();
                Sync();
                return;

            case RecordShortcutAction.CopyToClipboard:
                CopyLast();
                return;

            case RecordShortcutAction.EndRecording:
                Cancel_Click(this, new RoutedEventArgs());
                return;

            case RecordShortcutAction.PreviousArea:
                VisitArea(previous: true);
                return;

            case RecordShortcutAction.NextArea:
                VisitArea(previous: false);
                return;

            case RecordShortcutAction.Undo:
                Annotations?.Undo();
                RefreshUndo();
                return;

            case RecordShortcutAction.Redo:
                Annotations?.Redo();
                RefreshUndo();
                return;

            case RecordShortcutAction.ResetCanvas:
                Annotations?.Clear();
                RefreshUndo();
                return;
        }

        if (RecordShortcuts.ToolOf(action) is { } tool) SelectTool(tool);
    }

    /// <summary>按快捷键选工具：等价于点那一个按钮（它的 Checked 会顺带收起导出设置）。</summary>
    private void SelectTool(RecordTool tool)
    {
        foreach (var button in _toolButtons)
        {
            if (button.Tag is not RecordTool tag || tag != tool) continue;

            if (button.IsChecked != true) button.IsChecked = true;
            else Tool_Checked(button, new RoutedEventArgs());   // 已经选中：把工具再同步给标注层
            return;
        }
    }

    /// <summary>R / Shift+R：在录屏区域历史里前后翻一块（照上游 navigateAreaHistory）。</summary>
    private void VisitArea(bool previous)
    {
        var entries = RecordAreaHistory.Entries;
        if (!RecordAreaHistory.TryVisit(entries, _area, previous, _historyIndex, out var rect, out int index))
            return;

        _historyIndex = index;

        CloseRegion();
        _area = rect;
        MetaText.Text = MetaSummary();
        PlaceBar();
        ShowRegion();
        Sync();
    }

    /// <summary>开始录制时把这块区域记进历史（上游：开录成功才 commit）。</summary>
    private void RememberArea()
    {
        if (RecordAreaHistory.Append(_area)) _historyIndex = -1;
    }

    /// <summary>用户自己改过区域：再翻历史时从头算。</summary>
    private void ResetAreaHistory() => _historyIndex = -1;
}
