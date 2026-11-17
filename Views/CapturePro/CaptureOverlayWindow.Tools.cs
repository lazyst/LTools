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
        Toolbar.ActionRequested += OnToolbarAction;
        Toolbar.UndoRequested += () => { _doc.Undo(); RefreshMarks(); };
        Toolbar.RedoRequested += () => { _doc.Redo(); RefreshMarks(); };
        Toolbar.ResetRequested += () => { _toolbarPlaced = false; UpdateChrome(); };
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

    /// <summary>
    /// Esc / 右键：一级一级往回退（折线 → 手势 → 选中的标注 → 工具 → **选区** → 退出）。
    /// 「有选区」单独占一档：像别的截图工具一样，框完右键先把这次框选取消掉，
    /// 覆盖层留着让用户重新框；没选区时再按一次才关掉覆盖层。
    /// </summary>
    /// <param name="preferSelection">
    /// 右键传 true：**有选区就直接取消这次框选**（截图工具里右键的惯常含义：Snipaste / 微信截图都这样），
    /// 不必先退回选择工具。Esc 不传，仍先退回选择工具（提示里写的是「Esc 逐级退出」）。
    /// </param>
    private void StepBack(bool preferSelection = false)
    {
        if (BrowsingHistory) ExitHistory();
        else if (_selPolyActive) { CancelSelectionPolyline(); UpdateChrome(); }
        else if (_polyActive) CancelPolyline();
        else if (_drag != DragMode.None) CancelDrag();
        else if (_selectedIndex >= 0) ClearMarkSelection();
        else if (preferSelection && IsValid(_sel)) ClearSelection();
        else if (_tool != CaptureTool.Select) SetTool(CaptureTool.Select);
        else if (IsValid(_sel)) ClearSelection();
        else Cancel();
    }

    /// <summary>
    /// 取消这次框选：选区与它的标注一起清掉（标注只对这块选区有意义），
    /// 覆盖层留着、光标回到十字，鼠标还在原处时把悬停高亮算回来。
    /// </summary>
    private void ClearSelection()
    {
        _regions.Clear();
        _originRegions = null;
        _liveRect = null;
        _replaceOnCommit = false;
        _lasso.Clear();
        _sel = null;
        CancelSelectionPolyline();
        SetTool(CaptureTool.Select);     // 工具也回到「选择」：再框一块就是从头开始
        _doc.Area = default;             // 选区也没了：别让「有区域、没框」的中间态留在文档里
        _doc.Regions = null;
        _doc.ClearMarks();
        UpdateCursorForTool();
        UpdateChrome();                  // 收起尺寸条 / 工具栏，提示改回「拖动框选」

        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
            UpdateHover(Clamp(dip));     // 悬停高亮只有没选区时才出现，现在该露出来了
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

    /// <summary>选中最上面一条命中的标注（-1 = 没选中）；画面上的虚线框与缩放手柄跟着走。</summary>
    private void SelectMark(int index)
    {
        _selectedIndex = index;
        Surface.SelectedIndex = index;
        Surface.ShowHandles = ShowsMarkHandles;
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
