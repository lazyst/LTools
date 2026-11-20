using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 覆盖层的快捷键派发：一张可配置的表（<see cref="CaptureShortcutTable"/>）说了算，
/// 默认值逐条对齐 Snow Shot 的 <c>screenshot_shortcuts/*</c> 与 <c>drawing_shortcuts/*</c>。
/// </summary>
/// <remarks>
/// <para>
/// 这里只做「表里的动作落到既有方法上」，不另写一套实现：选区、标注、取色、二维码、长截图
/// 都还是原来那些入口。动作能不能触发沿用上游的 <c>canActivate</c> 口径（取色类只在取色工具下、
/// 恢复上一块选区只在移动工具下、长截图要有选区…），不满足就**当作没命中**，让后面的
/// 工具快捷键照旧工作。
/// </para>
/// <para>
/// 「按住」类动作（整个选区 / 锁正方形 / 锁比例）和轻按 Alt 切参考线不进 keydown 派发——
/// 它们由拖动过程与 <see cref="TrackModifierUp"/> 处理。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>最近框过的区域（R / Shift+R 轮换，照上游的「选择历史」）。</summary>
    private readonly List<Rect> _recentSelections = new();

    /// <summary>轮换到第几条；-1 = 还没轮换过（下次从最新那块的前一块开始）。</summary>
    private int _recentIndex = -1;

    /// <summary>隐藏光标（<c>toggle_cursor_visibility</c>）。</summary>
    private bool _cursorHidden;

    /// <summary>按了「表格识别」：结果窗算完直接切到表格页。</summary>
    private bool _tableRequested;

    /// <summary>这次识别要直接打开表格页（<c>table_recognition</c>）。</summary>
    internal bool TableRequested => _tableRequested;

    /// <summary>这些动作不占 keydown（按住类 / 轻按修饰键），由手势与 keyup 处理。</summary>
    private static readonly HashSet<string> HeldOnKeyDown = new(StringComparer.Ordinal)
    {
        "move_entire_selection",
        "keep_selection_width_and_height_consistent",
        "selection_aspect_ratio_snap",
        "toggle_guides",
    };

    /// <summary>
    /// 上游要求取色上下文活着才生效的那几项（<c>moveToolActive</c>）：选择工具与取色工具下都算。
    /// </summary>
    private static readonly HashSet<string> PickerOnly = new(StringComparer.Ordinal)
    {
        "move_cursor_up",
        "move_cursor_down",
        "move_cursor_left",
        "move_cursor_right",
        "copy_color",
        "toggle_coordinate_mode",
    };

    /// <summary>
    /// 先查覆盖层表、再查绘图工具表。命中并执行了返回 true（调用方吃掉这个键）。
    /// </summary>
    /// <remarks>
    /// 修饰键状态取「WPF 的实时状态 **或** 我们自己在 <see cref="TrackModifierDown"/> 记的那一笔」：
    /// 正常按键盘两者一致；而窗口刚拿到焦点时 Shift / Alt 可能早就按着（没有 KeyDown 落到这里），
    /// 只有 <see cref="Keyboard.Modifiers"/> 知道——反过来在某些宿主里实时状态读不到，
    /// 我们自己记的那一笔还在。命中后要把这一下修饰键标成「用掉了」，
    /// 抬起时别再当轻按（<see cref="ConsumeModifiers"/>）。
    /// </remarks>
    private bool TryCaptureShortcut(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ushort vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return false;

        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0 || _shiftHeld;
        bool alt = (mods & ModifierKeys.Alt) != 0 || _altHeld;
        bool win = (mods & ModifierKeys.Windows) != 0;

        if (CaptureShortcutTable.Match(CaptureShortcutScope.Screenshot, vk, ctrl, shift, alt, win, out string id) &&
            !HeldOnKeyDown.Contains(id) && RunScreenshotShortcut(id))
        {
            ConsumeModifiers(alt, shift);
            return true;
        }

        if (CaptureShortcutTable.Match(CaptureShortcutScope.Drawing, vk, ctrl, shift, alt, win, out string drawing) &&
            RunDrawingShortcut(drawing))
        {
            ConsumeModifiers(alt, shift);
            return true;
        }

        return false;
    }

    /// <summary>覆盖层动作。<paramref name="id"/> 是上游的 <c>screenshot_shortcuts/*</c> 键。</summary>
    private bool RunScreenshotShortcut(string id)
    {
        // 取色 / 光标采样：上游 moveCursorOnePixel 也要求 colorPickerContext().active
        // （选择工具与取色工具下都算，见 PickerOnly 的注释）
        if (PickerOnly.Contains(id) && _tool is not (CaptureTool.Picker or CaptureTool.Select)) return false;

        switch (id)
        {
            case "move_tool":
                SetTool(CaptureTool.Select);
                return true;

            case "move_cursor_up": return MoveCursorBy(0, -1);
            case "move_cursor_down": return MoveCursorBy(0, 1);
            case "move_cursor_left": return MoveCursorBy(-1, 0);
            case "move_cursor_right": return MoveCursorBy(1, 0);

            case "switch_selection_between_window_and_window_sub_element":
                if (!SmartPreviewActive) return false;
                ToggleSmartTarget();
                return true;

            case "next_selection_type":
                return CycleSelectionShape(1);

            case "previous_selection_type":
                return CycleSelectionShape(-1);

            case "previous_screenshot_history":
                return StepHistory(-1);

            case "next_screenshot_history":
                return StepHistory(1);

            case "select_previously_selected_area":
                if (_tool != CaptureTool.Select || _drag != DragMode.None) return false;
                return CycleRecentSelection(-1);

            case "select_next_previously_selected_area":
                if (_tool != CaptureTool.Select || _drag != DragMode.None) return false;
                return CycleRecentSelection(1);

            case "duplicate_latest_region":
                if (_drag != DragMode.None) return false;
                return DuplicateLatestRegion();

            case "toggle_cursor_visibility":
                ToggleCursorVisibility();
                return true;

            case "recapture":
                // 原位重抓（照上游 requestRecapture）：只换底图，选区 / 标注 / 撤销栈都不动，
                // 不再「退出本次截图重开一轮」（那条路会把这些全丢掉）。
                _ = RecaptureAsync();
                return true;

            case "toggle_coordinate_mode":
                OverlayUiSettings.SetColorPickerCoordinateMode(OverlayUiSettings.RelativePickerCoordinates
                    ? OverlayUiSettings.CoordGlobal
                    : OverlayUiSettings.CoordRelative);
                ShowPickerAt(_guideCursor);
                RefreshHint();
                PlaceHint();
                return true;

            case "copy_color":
                PickColor(_guideCursor);
                return true;

            case "table_recognition":
                _tableRequested = true;
                Accept(FinishKind.Recognize);
                return true;

            case "qr_code_recognition":
                SetQrMarkersVisible(!_qrVisible);
                return true;

            case "video_recording":
                Accept(FinishKind.Record);
                return true;

            case "text_recognition":
                Accept(FinishKind.Recognize);
                return true;

            case "text_translation":
                _translate = true;
                Accept(FinishKind.Recognize);
                return true;

            case "scrolling_screenshot":
                if (_sel is not Rect scrollSel || !IsValid(scrollSel)) return false;
                return StartScrollMode();

            case "quick_save":
                Accept(FinishKind.QuickSave);
                return true;

            case "print":
                Accept(FinishKind.Print);
                return true;

            case "save_as_file":
                Accept(FinishKind.Save);
                return true;

            case "pin_to_screen":
                Accept(FinishKind.Pin);
                return true;

            case "pin_window_to_screen":
                if (_hoverTarget is not SnapBox pinTarget) return false;
                SetSingleRect(ToDipRect(pinTarget.Bounds));
                UpdateChrome();
                Accept(FinishKind.Pin);
                return true;

            case "cancel_screenshot":
                RequestCancelViaShortcut();
                return true;

            case "copy_to_clipboard":
                Accept(FinishKind.Copy);
                return true;

            case "undo":
                _doc.Undo();
                RefreshMarks();
                return true;

            case "redo":
                _doc.Redo();
                RefreshMarks();
                return true;

            default:
                return false;
        }
    }

    /// <summary>绘图工具动作（<c>drawing_shortcuts/*</c>）；只有「清空画布」不是换工具。</summary>
    private bool RunDrawingShortcut(string id)
    {
        if (id == "reset_canvas")
        {
            _doc.ClearMarks();
            RefreshMarks();
            return true;
        }

        if (CaptureShortcutTable.ToolOf(id) is not CaptureTool tool) return false;
        SetTool(tool);
        return true;
    }

    /// <summary>物理光标挪 1 像素（按住 Shift ×10）；挪完把参考线 / 悬停 / 取色卡跟过去。</summary>
    private bool MoveCursorBy(int dx, int dy)
    {
        if (!Win32.GetCursorPos(out var cursor)) return false;

        int step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 10 : 1;
        if (!Win32.SetCursorPos(cursor.X + dx * step, cursor.Y + dy * step)) return false;

        if (Win32.GetCursorPos(out var moved) && TryToDip(moved, out var dip))
        {
            var at = Clamp(dip);
            _guideCursor = at;
            Surface.GuideCursor = at;
            Surface.InvalidateVisual();
            UpdateHover(at);
            if (_tool is CaptureTool.Picker or CaptureTool.Select) ShowPickerAt(at);
        }

        return true;
    }

    /// <summary>矩形 → 折线 → 曲线 → 自由绘制 循环（上游 next/previous_selection_type 的枚举顺序）。</summary>
    private bool CycleSelectionShape(int step)
    {
        SelectionMode[] order =
            { SelectionMode.Rect, SelectionMode.Polyline, SelectionMode.Curve, SelectionMode.Lasso };
        int index = Array.IndexOf(order, _shape);
        if (index < 0) index = 0;
        int next = ((index + step) % order.Length + order.Length) % order.Length;
        if (next == index) return false;

        SetSelectionShape(order[next]);
        return true;
    }

    /// <summary>一次框选落定（<c>fresh</c>）：记进「最近选区」，供 R / Shift+R 轮换。</summary>
    private void RememberRecentSelection()
    {
        if (_sel is not Rect sel || !IsValid(sel)) return;
        if (_recentSelections.Count > 0 && _recentSelections[^1] == sel) return;

        _recentSelections.Add(sel);
        if (_recentSelections.Count > 20) _recentSelections.RemoveAt(0);
        _recentIndex = -1;
    }

    /// <summary>R / Shift+R：在最近框过的区域之间前后轮换（到端点就停）。</summary>
    private bool CycleRecentSelection(int delta)
    {
        if (_recentSelections.Count == 0) return false;

        if (_recentIndex < 0) _recentIndex = _recentSelections.Count - 1;
        int next = Math.Clamp(_recentIndex + delta, 0, _recentSelections.Count - 1);
        if (next == _recentIndex) return false;

        _recentIndex = next;
        EndSmartPreviewKeepingSelection();
        SetSingleRect(_recentSelections[next]);
        UpdateChrome();
        return true;
    }

    /// <summary>duplicate_latest_region：把最近那块按原尺寸贴到光标处，作为一块新的加区。</summary>
    private bool DuplicateLatestRegion()
    {
        if (_recentSelections.Count == 0) return false;

        Rect source = _recentSelections[^1];
        var bounds = new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        double width = Math.Min(source.Width, bounds.Width);
        double height = Math.Min(source.Height, bounds.Height);
        if (width < MinSize || height < MinSize) return false;

        double x = Math.Clamp(_guideCursor.X - width / 2, 0, Math.Max(0, bounds.Width - width));
        double y = Math.Clamp(_guideCursor.Y - height / 2, 0, Math.Max(0, bounds.Height - height));

        EndSmartPreviewKeepingSelection();
        _regions.Add(SelectionRegion.FromRect(RegionMode.Add, new Rect(x, y, width, height)));
        RefreshSelection();
        UpdateChrome();
        return true;
    }

    /// <summary>显示 / 隐藏光标（<c>toggle_cursor_visibility</c>）。</summary>
    private void ToggleCursorVisibility()
    {
        _cursorHidden = !_cursorHidden;
        if (_cursorHidden)
        {
            Cursor = Cursors.None;
            return;
        }

        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip)) RefreshCursor(Clamp(dip));
        else Cursor = _tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
    }
}
