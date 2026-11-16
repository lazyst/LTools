using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 工具状态与工具栏接线：当前工具、当前样式、选中的标注，以及工具栏抛回来的动作。
/// </summary>
/// <remarks>
/// 这里只放「状态」；按下鼠标之后怎么画在 <c>.Annotate.cs</c>，输入文字在 <c>.Text.cs</c>。
/// 工具栏自己只改 <see cref="ToolStyle"/> 并抛事件，文档始终只有覆盖层这一处写。
/// </remarks>
public partial class CaptureOverlayWindow
{
    private readonly ToolStyle _style = new();
    private CaptureTool _tool = CaptureTool.Select;
    private int _selectedIndex = -1;
    private bool _toolbarPlaced;

    private void WireToolbar()
    {
        Toolbar.Init(_style, _tool);
        Toolbar.ToolChanged += SetTool;
        Toolbar.UndoRequested += () => { _doc.Undo(); RefreshMarks(); };
        Toolbar.RedoRequested += () => { _doc.Redo(); RefreshMarks(); };
        Toolbar.CopyRequested += () => Accept(FinishKind.Copy);
        Toolbar.PinRequested += () => Accept(FinishKind.Pin);
        Toolbar.SaveRequested += () => Accept(FinishKind.Save);
        Toolbar.CancelRequested += Cancel;
    }

    private void SetTool(CaptureTool tool)
    {
        if (_tool == tool) return;
        CancelGesture();
        _tool = tool;
        Toolbar.Sync(tool);
        if (tool != CaptureTool.Picker) HidePicker();
        UpdateCursorForTool();
        UpdateChrome();
    }

    /// <summary>Esc / 右键：一级一级往回退（折线 → 手势 → 选中的标注 → 工具 → 退出）。</summary>
    private void StepBack()
    {
        if (_polyActive) CancelPolyline();
        else if (_drag != DragMode.None) CancelDrag();
        else if (_selectedIndex >= 0) ClearMarkSelection();
        else if (_tool != CaptureTool.Select) SetTool(CaptureTool.Select);
        else Cancel();
    }

    private void UpdateCursorForTool()
    {
        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
        {
            var p = Clamp(dip);
            RefreshCursor(p);
            if (_tool == CaptureTool.Picker) ShowPickerAt(p);   // 切过去就显示色卡，不用先动一下
        }
        else Cursor = _tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    /// <summary>选中最上面一条命中的标注（-1 = 没选中）；画面上的虚线框跟着走。</summary>
    private void SelectMark(int index)
    {
        if (_selectedIndex == index && Surface.SelectedIndex == index) return;
        _selectedIndex = index;
        Surface.SelectedIndex = index;
        Surface.InvalidateVisual();
    }

    private void ClearMarkSelection() => SelectMark(-1);

    /// <summary>删掉选中的标注（Delete / Backspace）。</summary>
    private void DeleteSelectedMark()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return;
        _doc.RemoveAt(_selectedIndex);
        SelectMark(-1);
        RefreshMarks();
    }

    /// <summary>事件源是否来自浮动工具栏（工具栏背景也要让路，否则一点就拖出新选区）。</summary>
    private bool IsOnToolbar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, Toolbar)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>鼠标现在是不是停在工具栏上（移动时别更新悬停候选 / 改光标）。</summary>
    private bool IsOverToolbar => Toolbar.IsVisible && Toolbar.IsMouseOver;
}
