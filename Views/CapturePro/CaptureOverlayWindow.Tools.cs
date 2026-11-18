using System;
using System.Linq;
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
    /// <summary>每个工具家族一套样式（落盘在 <c>ui.json</c>，见 <see cref="CaptureStyleStore"/>）。</summary>
    private readonly ToolStyleSet _styles = CaptureStyleStore.Current;

    /// <summary>当前工具的那一套（工具栏改它、标注创建读它，两边同一个对象）。</summary>
    private ToolStyle _style => _styles.For(_tool);

    private CaptureTool _tool = CaptureTool.Select;
    private int _selectedIndex = -1;
    private bool _toolbarPlaced;

    /// <summary>选中标注时参数行的临时显示样式（那一族创建样式的副本 + 这一条的值）。</summary>
    private ToolStyle? _selectionStyle;

    /// <summary>开始编辑那一刻显示样式的快照：合并时只把跟它不一样的字段写回创建样式。</summary>
    private ToolStyle? _selectionBaseline;

    /// <summary>选中的这条属于哪一族（合并回创建样式用）。</summary>
    private string? _selectionFamily;

    private void WireToolbar()
    {
        Toolbar.Init(_styles, _tool);
        Toolbar.ToolChanged += SetTool;
        Toolbar.ActionRequested += OnToolbarAction;
        Toolbar.StyleChanged += () =>
        {
            CaptureStyleStore.SaveSoon();
            ApplyStyleToSelection();
            UpdateToolPreview();
        };
        Toolbar.AutoFilterFillRequested += OnAutoFilterFillRequested;
        Toolbar.DistanceValueSubmitted += OnDistanceValueSubmitted;
        Toolbar.UndoRequested += () => { _doc.Undo(); RefreshMarks(); };
        Toolbar.RedoRequested += () => { _doc.Redo(); RefreshMarks(); };
        Toolbar.ResetRequested += () => { _toolbarPlaced = false; UpdateChrome(); };
    }

    /// <summary>
    /// 选中一条标注时改参数 = 先写回那一条，再把**变过的字段**合并进该族的创建样式
    /// （上游 <c>set_shape_style_patch</c> 只 merge <c>properties</c> 里的位）。
    /// 没有选中标注时什么都不做——那时参数行改的就是创建样式本身。
    /// </summary>
    private void ApplyStyleToSelection()
    {
        if (_selectionStyle == null || _selectionBaseline == null || _selectionFamily == null) return;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return;

        bool colorChanged = _selectionStyle.Color != _selectionBaseline.Color;

        // 只合并变过的字段：不然「选一条旧箭头看一眼」会把创建样式整份换成它的旧值
        _selectionStyle.CopyChangedTo(_styles.ForFamily(_selectionFamily), _selectionBaseline);
        if (colorChanged) _styles.SetColor(_selectionFamily, _selectionStyle.Color);   // 含「共享颜色」传播

        _doc.ReplaceAt(_selectedIndex, MarkStyler.Apply(_doc.Marks[_selectedIndex], _selectionStyle));
        _selectionBaseline = _selectionStyle.Clone();     // 之后的改动继续以「改到这里」为基准
        RefreshMarks();
    }

    /// <summary>
    /// 选中 / 取消选中：参数行切到这条标注的样式族并显示**它自己的值**（上游 <c>Selected*</c> 来源）；
    /// 取消选中（或选中水印 / 恢复块这种不归族的）就把参数行还给当前工具。
    /// </summary>
    private void SyncSelectionStyle()
    {
        _selectionStyle = null;
        _selectionBaseline = null;
        _selectionFamily = null;

        if (_selectedIndex >= 0 && _selectedIndex < _doc.Marks.Count &&
            MarkStyler.TryFamily(_doc.Marks[_selectedIndex], out string family, out CaptureTool tool))
        {
            var display = _styles.ForFamily(family).Clone();
            MarkStyler.Capture(_doc.Marks[_selectedIndex], display);
            _selectionStyle = display;
            _selectionBaseline = display.Clone();
            _selectionFamily = family;
            Toolbar.SyncSelection(tool, display);
            return;
        }

        Toolbar.ClearSelectionStyle();
    }

    /// <summary>选中的那条测距量出来有多长（屏幕物理像素）；没有可编辑目标 = 0。</summary>
    private double MeasuredDistance()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return 0;
        if (_doc.Marks[_selectedIndex] is not Mark.Distance d || d.Length <= 0.01) return 0;
        return d.Length * (_doc.Scale > 0 ? _doc.Scale : 1);
    }

    /// <summary>
    /// 「实测值」提交：<c>系数 = 值 / 实测长度</c>（上游 <c>setDistanceValue</c>，钳 0.01–1000），
    /// 只作用在选中的那一条上，并同样把系数合并进测距族的创建样式。
    /// </summary>
    private void OnDistanceValueSubmitted(double value)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return;
        if (_doc.Marks[_selectedIndex] is not Mark.Distance) return;

        double length = MeasuredDistance();
        if (length <= 0 || !double.IsFinite(value) || value <= 0) return;

        double factor = Math.Clamp(value / length, 0.01, 1000);

        if (_selectionStyle == null)
        {
            _style.DistanceFactor = factor;
            _doc.ReplaceAt(_selectedIndex, MarkStyler.Apply(_doc.Marks[_selectedIndex], _style));
            CaptureStyleStore.SaveSoon();
            RefreshMarks();
            return;
        }

        _selectionStyle.DistanceFactor = factor;
        ApplyStyleToSelection();
        CaptureStyleStore.SaveSoon();
    }

    private void SetTool(CaptureTool tool)
    {
        if (_tool == tool) return;
        CancelGesture();
        if (_selectedIndex >= 0) SelectMark(-1);    // 换工具 = 放弃选中（参数行要交给新工具）
        _tool = tool;
        Toolbar.Sync(tool);
        if (tool != CaptureTool.Picker) HidePicker();
        if (tool == CaptureTool.AutoFilter) EnterAutoFilter();
        else LeaveAutoFilter();
        UpdateCursorForTool();
        UpdateToolPreview();
        UpdateChrome();
    }

    /// <summary>
    /// 水印是「整张图的效果」：选中工具就先画一层预览，点一下才真正落到文档里，
    /// 参数一改预览立刻跟着变（其它工具没有常驻预览）。
    /// </summary>
    private void UpdateToolPreview()
    {
        if (_tool == CaptureTool.AutoFilter)
        {
            // 类型 / 强度直接影响高亮颜色（同类型填过 = 绿）与下一次填充
            Surface.AutoFillKind = _style.FilterForAutoFill;
            Surface.InvalidateVisual();
            return;
        }

        if (NeedsWatermarkPreview)
        {
            Surface.Preview = WatermarkMark;
            Surface.InvalidateVisual();
            return;
        }

        if (Surface.Preview is Mark.Watermark)
        {
            Surface.Preview = null;
            Surface.InvalidateVisual();
        }
    }

    /// <summary>
    /// 水印选了工具还没落盘时才画预览：已经在图上的那一块就是成品，
    /// 再叠一层预览会让不透明度翻倍（看着比实际深）。
    /// </summary>
    private bool NeedsWatermarkPreview =>
        _tool == CaptureTool.Watermark && !_doc.Marks.OfType<Mark.Watermark>().Any();

    /// <summary>按当前样式造一块水印（预览与实际落盘都用它，别两处各拼一次）。</summary>
    private Mark.Watermark WatermarkMark => new(_style.WatermarkSpecOf());

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
        SyncAutoFilter(detect: false);    // 选区没了：识别结果与「填充区域」一起作废
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

    /// <summary>
    /// 选中最上面一条命中的标注（-1 = 没选中）：画面上的虚线框与缩放手柄跟着走，
    /// 参数行同时切到这一条自己的样式族（见 <see cref="SyncSelectionStyle"/>）。
    /// </summary>
    private void SelectMark(int index)
    {
        _selectedIndex = index;
        Surface.SelectedIndex = index;
        Surface.ShowHandles = ShowsMarkHandles;
        Surface.InvalidateVisual();
        SyncSelectionStyle();
        Toolbar.SetMeasuredDistance(MeasuredDistance());
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
