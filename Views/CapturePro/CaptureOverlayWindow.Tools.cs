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

    /// <summary>
    /// 选中的标注（多选；顺序即加入顺序，最后一个就是主选中项 <see cref="_selectedIndex"/>）。
    /// 单选也走它，所以「有没有选中」只有一个真源。
    /// </summary>
    private readonly List<int> _selected = new();

    /// <summary>选中的全部下标（只读快照，画布 / 自检看它）。</summary>
    internal IReadOnlyList<int> SelectedIndices => _selected;

    private bool _toolbarPlaced;

    /// <summary>选中标注时参数行的临时显示样式（那一族创建样式的副本 + 这一条的值）。</summary>
    private ToolStyle? _selectionStyle;

    /// <summary>开始编辑那一刻显示样式的快照：合并时只把跟它不一样的字段写回创建样式。</summary>
    private ToolStyle? _selectionBaseline;

    /// <summary>选中的这条属于哪一族（合并回创建样式用）。</summary>
    private string? _selectionFamily;

    /// <summary>选中的每一条各自的显示样式（同一族）：参数行据此显示「多个值」（照上游 Mixed 位）。</summary>
    private List<ToolStyle>? _selectionPeers;


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
        Toolbar.RecaptureRequested += () => _ = RecaptureAsync();
    }

    /// <summary>
    /// 选中一批标注时改参数 = 先写回**同一族的每一条**，再把**变过的字段**合并进该族的创建样式
    /// （上游 <c>set_shape_style_patch</c>：只按 <c>properties</c> 里的位 merge，且只作用于
    /// <c>kind</c> 匹配的那些元素）。整批只占一个撤销位。
    /// 没有选中标注时什么都不做——那时参数行改的就是创建样式本身。
    /// </summary>
    private void ApplyStyleToSelection()
    {
        if (_selectionStyle == null || _selectionBaseline == null || _selectionFamily == null) return;
        if (_selected.Count == 0) return;

        ToolStyle display = _selectionStyle;
        string family = _selectionFamily;
        bool colorChanged = display.Color != _selectionBaseline.Color;

        var targets = _selected.Where(index => index >= 0 && index < _doc.Marks.Count &&
                                               MarkStyler.InFamily(_doc.Marks[index], family)).ToArray();

        if (targets.Length > 0)
        {
            _doc.EditBatch(list =>
            {
                foreach (int index in targets)
                {
                    if (index >= list.Count) continue;
                    list[index] = MarkStyler.Apply(list[index], display);
                }
            });
        }

        // 只合并变过的字段：不然「选一条旧箭头看一眼」会把创建样式整份换成它的旧值
        display.CopyChangedTo(_styles.ForFamily(family), _selectionBaseline);
        if (colorChanged) _styles.SetColor(family, display.Color);   // 含「共享颜色」传播

        // 写回之后各条的值都变了（「多个值」可能就此消除）：RefreshMarks 会按新的标注重算
        RefreshMarks();
    }

    /// <summary>
    /// 选中 / 取消选中：参数行切到这一族的样式族并显示**这一条的值**（上游 <c>Selected*</c> 来源）；
    /// 多选时按上游 <c>style_toolbar_source</c> 的优先顺序选族，取主选中项（或第一个同类成员）的值，
    /// 同一族里其它成员的值一起交给工具栏算「多个值」。
    /// 取消选中（或选中水印 / 恢复块这种不归族的）就把参数行还给当前工具。
    /// </summary>
    private void SyncSelectionStyle()
    {
        _selectionStyle = null;
        _selectionBaseline = null;
        _selectionFamily = null;
        _selectionPeers = null;

        if (_selected.Count > 0 &&
            MarkStyler.TrySelectionFamily(_doc.Marks, _selected, _selectedIndex,
                out string family, out CaptureTool tool, out int representative))
        {
            var display = _styles.ForFamily(family).Clone();
            MarkStyler.Capture(_doc.Marks[representative], display);
            _selectionStyle = display;
            _selectionBaseline = display.Clone();
            _selectionFamily = family;

            var peers = new List<ToolStyle>();
            foreach (int index in _selected)
            {
                if (index < 0 || index >= _doc.Marks.Count) continue;
                if (!MarkStyler.InFamily(_doc.Marks[index], family)) continue;
                var peer = _styles.ForFamily(family).Clone();
                MarkStyler.Capture(_doc.Marks[index], peer);
                peers.Add(peer);
            }

            _selectionPeers = peers;
            Toolbar.SyncSelection(tool, display, peers);
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

        // 预选态换工具 = 进入编辑：这块预选就地变成普通选区，智能选区暂停
        EndSmartPreviewKeepingSelection();

        CancelGesture();
        if (_selectedIndex >= 0) SelectMark(-1);    // 换工具 = 放弃选中（参数行要交给新工具）
        _tool = tool;
        CaptureToolMemory.Remember(tool);
        Toolbar.Sync(tool);
        if (tool is not (CaptureTool.Picker or CaptureTool.Select)) HidePicker();
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
    /// 右键：一级一级往回退（折线 → 手势 → 选中的标注 → **选区** → 工具 → 退出）。
    /// 「有选区」单独占一档：像别的截图工具一样，框完右键先把这次框选取消掉，
    /// 覆盖层留着让用户重新框；没选区时再按一次才关掉覆盖层。
    /// </summary>
    /// <returns>
    /// true = 这一档是「退出这次截图」。调用方**不要**当场 <see cref="Cancel"/>：
    /// 照上游把这一下当鼠标释放动作武装起来，按钮抬起之后一拍才真的关。
    /// </returns>
    /// <remarks>
    /// **Esc 不走这里**：上游（snow-shot）Esc = <c>cancel_screenshot</c>，直接退出这次截图；
    /// 逐级收是右键的事（上游 <c>handleRightClick</c>）。
    /// </remarks>
    private bool StepBack()
    {
        // 智能选区预选态：右键直接退出这次截图（上游 preselection 里的 handleRightClick = CancelCapture）
        if (SmartPreviewActive) return true;

        if (BrowsingHistory) ExitHistory();
        else if (_selPolyActive) { CancelSelectionPolyline(); UpdateChrome(); }
        else if (_polyActive) CancelPolyline();
        else if (_drag != DragMode.None) CancelDrag();
        else if (_selectedIndex >= 0) ClearMarkSelection();
        else if (IsValid(_sel)) ClearSelection();
        else if (_tool != CaptureTool.Select) SetTool(CaptureTool.Select);
        else return true;

        return false;
    }

    /// <summary>
    /// 取消这次框选：选区与它的标注一起清掉（标注只对这块选区有意义），
    /// 覆盖层留着、光标回到十字，鼠标还在原处时把悬停高亮算回来。
    /// </summary>
    private void ClearSelection()
    {
        StopSmartTicker();
        _smartPreviewActive = false;
        _smartShown = Rect.Empty;
        _smart.ClearTransientState();     // 上游 clearSelection：命中路径一起清掉

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
            // 切到取色 / 选择工具之后马上摆好色卡（别等用户先动一下鼠标）
            if (_tool is CaptureTool.Picker or CaptureTool.Select) ShowPickerAt(p);
        }
        else Cursor = _tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    /// <summary>
    /// 单选一条标注（-1 = 清空）：画面上的虚线框与缩放手柄跟着走，
    /// 参数行同时切到这一条自己的样式族（见 <see cref="SyncSelectionStyle"/>）。
    /// </summary>
    private void SelectMark(int index)
    {
        _selected.Clear();
        if (index >= 0 && index < _doc.Marks.Count) _selected.Add(index);
        PublishSelection();
    }

    /// <summary>
    /// 整组选中（框选 / 恢复选中）：<paramref name="primary"/> 是主选中项（排到最后，参数行显示它的值）。
    /// 越界与重复的下标直接丢掉。
    /// </summary>
    private void SelectMarks(IEnumerable<int> indices, int primary)
    {
        _selected.Clear();
        foreach (int index in indices)
            if (index >= 0 && index < _doc.Marks.Count && !_selected.Contains(index))
                _selected.Add(index);

        if (primary >= 0 && _selected.Remove(primary)) _selected.Add(primary);
        PublishSelection();
    }

    /// <summary>
    /// Shift+点选（上游 <c>toggle_selection</c>）：已在选区里就摘掉（摘掉的是主选中项时主选中项换成
    /// 剩下的最后一个），否则追加成新的主选中项。
    /// </summary>
    private void ToggleMarkSelection(int index)
    {
        if (index < 0 || index >= _doc.Marks.Count) return;

        if (!_selected.Remove(index)) _selected.Add(index);
        PublishSelection();
    }

    /// <summary>这一条在不在选区里。</summary>
    private bool IsMarkSelected(int index) => _selected.Contains(index);

    /// <summary>把选区状态推给画布与工具栏（单选 / 多选都走这里，别再各写一遍）。</summary>
    private void PublishSelection()
    {
        _selectedIndex = _selected.Count > 0 ? _selected[^1] : -1;

        Surface.SelectedIndex = _selectedIndex;
        Surface.SelectedIndices = _selected.ToArray();
        Surface.ShowHandles = ShowsMarkHandles;
        Surface.InvalidateVisual();
        SyncSelectionStyle();
        Toolbar.SetMeasuredDistance(MeasuredDistance());
    }

    private void ClearMarkSelection() => SelectMark(-1);

    /// <summary>删掉选中的标注（Delete / Backspace）；多条一起删只占一个撤销位。</summary>
    private void DeleteSelectedMark()
    {
        if (_selected.Count == 0) return;

        var drop = new HashSet<int>(_selected);
        _doc.EditBatch(list =>
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (drop.Contains(i)) list.RemoveAt(i);
        });

        SelectMark(-1);
        RefreshMarks();
    }

    /// <summary>事件源是否来自浮动工具栏（工具栏背景也要让路，否则一点就拖出新选区）。</summary>
    private bool IsOnToolbar(DependencyObject? source) => VisualTree.IsInside(source, Toolbar);

    /// <summary>鼠标现在是不是停在工具栏上（移动时别更新悬停候选 / 改光标）。</summary>
    private bool IsOverToolbar => Toolbar.IsVisible && Toolbar.IsMouseOver;
}
