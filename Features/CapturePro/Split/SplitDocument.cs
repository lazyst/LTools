using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Split;

/// <summary>把一份设置交给文档之后会怎样：已生效 / 没变化 / 要先在后台算线。</summary>
internal enum SplitApply
{
    Unchanged,
    Applied,
    NeedsPrepare,
}

/// <summary>
/// 切图文档：底图 + 当前状态 + 撤销栈 + 手拖切分线。照上游 <c>ImageSplitDocument</c>。
/// </summary>
/// <remarks>
/// <para>
/// 内容真源是 <see cref="State"/>（设置 + 两条轴上的线 + 有没有被手拖过）。<see cref="Layout"/>
/// 是它派生出来的、可直接查格子 / 导出矩形的布局；<see cref="Revision"/> 每变一次加 1，
/// 后台算完的智能线回来时用它做 CAS——算旧了的结果直接丢掉，不会盖掉用户刚改的设置。
/// </para>
/// <para>
/// **只改重叠宽度不动手拖过的线**：上游 <c>setSettings</c> 特意分开处理，重叠只影响导出
/// 边距，不该把用户精心摆好的切分线重置回等分。
/// </para>
/// <para>
/// **智能线可以缓一层**：同一张图、同一方向、同一张数算出来的线缓存在
/// <see cref="CachedLayout"/> 里，来回改「重叠 / 精确参数」时不必重算投影。
/// </para>
/// </remarks>
internal sealed class SplitDocument
{
    private readonly List<HistoryEntry> _history = new();
    private int _cursor;
    private SplitPrepared? _cachedSmart;
    private SplitState? _beforeDrag;
    private bool _dragVertical = true;
    private int _dragIndex = -1;
    private int _dragStartIndex = -1;

    public SplitDocument()
    {
        State = new SplitState(new SplitSettings(), Array.Empty<int>(), Array.Empty<int>(), false);
        Layout = new SplitLayout(0, 0);
        _history.Add(new HistoryEntry(State, Snapshot(Layout.Errors)));
    }

    /// <summary>底图（空 = 还没选图）。</summary>
    public BitmapSource? Image { get; private set; }

    /// <summary>当前可查的布局（线 + 重叠 + 错误）。</summary>
    public SplitLayout Layout { get; private set; }

    /// <summary>当前状态（设置 + 线 + 是否手拖过）。</summary>
    public SplitState State { get; private set; }

    /// <summary>每次内容变化都 +1；后台结果靠它对账。</summary>
    public long Revision { get; private set; }

    public bool Dragging => _beforeDrag != null;

    /// <summary>正在拖的那条线现在落在第几号（跨过相邻线时会变）。</summary>
    public int DragIndex => _dragIndex;

    public bool CanUndo => _beforeDrag == null && _cursor > 0;

    public bool CanRedo => _beforeDrag == null && _cursor + 1 < _history.Count;

    public event Action? Changed;

    public event Action? HistoryChanged;

    // ============ 装图 / 设置 ============

    /// <summary>切回空态：丢掉底图、布局、撤销栈与手拖状态（「重选图片」取消 / 关窗时用）。</summary>
    public void Reset()
    {
        _beforeDrag = null;
        _cachedSmart = null;
        _dragIndex = _dragStartIndex = -1;
        Image = null;
        State = new SplitState(new SplitSettings(), Array.Empty<int>(), Array.Empty<int>(), false);
        Layout = new SplitLayout(0, 0);
        _history.Clear();
        _history.Add(new HistoryEntry(State, Snapshot(Layout.Errors)));
        _cursor = 0;
        Revision++;

        Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    /// <summary>装一张图 + 一份算好的布局；替换底图会清空撤销栈（照上游）。</summary>
    public bool SetPreparedImage(BitmapSource image, SplitPrepared prepared)
    {
        if (prepared.Layout.Width != image.PixelWidth ||
            prepared.Layout.Height != image.PixelHeight)
        {
            return false;
        }

        _beforeDrag = null;
        Image = image;
        State = new SplitState(prepared.Settings.Clone(), prepared.Layout.Vertical.ToArray(),
            prepared.Layout.Horizontal.ToArray(), false);
        Layout = prepared.Layout.Clone();
        _cachedSmart = Cacheable(prepared) ? prepared : null;

        _history.Clear();
        _history.Add(new HistoryEntry(State, Snapshot(Layout.Errors)));
        _cursor = 0;
        Revision++;

        Changed?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 换一份设置：能立刻算出来的（等分 / 精确 / 宫格 / 缓存过的智能线 / 只改重叠）当场生效，
    /// 否则返回 <see cref="SplitApply.NeedsPrepare"/>，由调用方把智能找线丢到后台。
    /// </summary>
    public SplitApply TryApplySettings(SplitSettings settings)
    {
        if (Image == null) return SplitApply.Unchanged;
        if (settings.Equals(State.Settings)) return SplitApply.Unchanged;

        var previous = State.Settings.Clone();
        previous.Overlap = settings.Overlap;
        if (previous.Equals(settings))
        {
            var layout = Layout.Clone();
            layout.Overlap = settings.Overlap;
            return ApplyPrepared(new SplitPrepared(settings, layout), Revision)
                ? SplitApply.Applied
                : SplitApply.Unchanged;
        }

        if (CachedLayout(settings) is { } cached)
        {
            return ApplyPrepared(cached, Revision) ? SplitApply.Applied : SplitApply.Unchanged;
        }

        // 连标称布局都算不出来（张数超了、百分比写错）：不动文档，让表单挂着错误
        return SplitLayoutCalculator.Calculate(Image.PixelWidth, Image.PixelHeight, settings).IsValid
            ? SplitApply.NeedsPrepare
            : SplitApply.Unchanged;
    }

    /// <summary>
    /// 缓存命中就直接拿现成布局；非智能 / 宫格 / 单张 / 一条线都放不下时返回标称布局。
    /// 返回 null = 需要重算智能线。
    /// </summary>
    public SplitPrepared? CachedLayout(SplitSettings settings)
    {
        if (Image == null) return null;

        var nominal = SplitLayoutCalculator.Calculate(Image.PixelWidth, Image.PixelHeight, settings);
        if (!nominal.IsValid) return null;

        bool horizontal = settings.Direction == SplitDirection.Horizontal;
        int dimension = horizontal ? Image.PixelHeight : Image.PixelWidth;
        int length = horizontal ? Image.PixelWidth : Image.PixelHeight;
        if (settings.Type != SplitType.Smart || settings.Direction == SplitDirection.Grid ||
            settings.Count == 1 || settings.Count == dimension || length == 1)
        {
            return new SplitPrepared(settings, nominal);
        }

        if (_cachedSmart == null ||
            _cachedSmart.Settings.Direction != settings.Direction ||
            _cachedSmart.Settings.Count != settings.Count)
        {
            return null;
        }

        var result = new SplitPrepared(settings, _cachedSmart.Layout.Clone());
        result.Layout.Overlap = settings.Overlap;
        return result;
    }

    /// <summary>
    /// 后台算好的布局落地。CAS 检查（版本 + 图尺寸 + 与上次提交的设置一致）不过就丢掉，
    /// 这保证「用户改完设置之前就发出的那次计算」回来时不会盖掉新的输入。
    /// </summary>
    public bool ApplyPrepared(SplitPrepared prepared, long expectedRevision)
    {
        if (Image == null) return false;

        var settings = prepared.Settings;
        if (expectedRevision != Revision ||
            prepared.Layout.Width != Image.PixelWidth ||
            prepared.Layout.Height != Image.PixelHeight ||
            !prepared.Layout.IsValid ||
            prepared.Layout.Overlap != settings.Overlap ||
            settings.Equals(State.Settings) ||
            !SplitLayoutCalculator.Calculate(Image.PixelWidth, Image.PixelHeight, settings).IsValid)
        {
            return false;
        }

        CancelLineDrag();

        var previous = State.Settings.Clone();
        previous.Overlap = settings.Overlap;
        SplitState next;
        if (!previous.Equals(settings))
        {
            Layout = prepared.Layout.Clone();
            next = new SplitState(settings.Clone(), prepared.Layout.Vertical.ToArray(),
                prepared.Layout.Horizontal.ToArray(), false);
            if (settings.Type == SplitType.Smart && settings.Direction != SplitDirection.Grid)
            {
                _cachedSmart = prepared;
            }
        }
        else
        {
            // 只动了重叠：导出边距变，用户摆好的线原样留着
            next = State with { Settings = settings.Clone() };
        }

        if (!Commit(next)) return false;

        Changed?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    public void Undo()
    {
        if (!CanUndo) return;
        _cursor--;
        RestoreHistory();
        Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        _cursor++;
        RestoreHistory();
        Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    // ============ 切分线 ============

    /// <summary>这条轴现在能不能加 / 拖线（宫格两条都可以；单向切只认自己那条）。</summary>
    public bool AllowsAxis(bool vertical) =>
        State.Settings.Direction == SplitDirection.Grid ||
        (vertical && State.Settings.Direction == SplitDirection.Vertical) ||
        (!vertical && State.Settings.Direction == SplitDirection.Horizontal);

    /// <summary>在标尺上点一下 = 新建一条线（同一像素上已有线就什么也不做）。</summary>
    public bool CreateLine(bool vertical, int coordinate)
    {
        if (_beforeDrag != null || !Layout.IsValid || !AllowsAxis(vertical)) return false;

        int dimension = vertical ? Image!.PixelWidth : Image!.PixelHeight;
        if (coordinate <= 0 || coordinate >= dimension) return false;

        var lines = vertical ? State.Vertical : State.Horizontal;
        int insertion = LowerBound(lines, coordinate);
        if (insertion < lines.Length && lines[insertion] == coordinate) return false;

        var updated = Insert(lines, insertion, coordinate);
        var next = vertical
            ? State with { Vertical = updated, ManuallyEdited = true }
            : State with { Horizontal = updated, ManuallyEdited = true };

        if (!Commit(next)) return false;
        Changed?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    /// <summary>删一条线（Delete / Backspace）。</summary>
    public bool RemoveLine(bool vertical, int index)
    {
        if (_beforeDrag != null || !Layout.IsValid || !AllowsAxis(vertical)) return false;

        var lines = vertical ? State.Vertical : State.Horizontal;
        if (index < 0 || index >= lines.Length) return false;

        var updated = new int[lines.Length - 1];
        Array.Copy(lines, 0, updated, 0, index);
        Array.Copy(lines, index + 1, updated, index, lines.Length - index - 1);

        var next = vertical
            ? State with { Vertical = updated, ManuallyEdited = true }
            : State with { Horizontal = updated, ManuallyEdited = true };

        if (!Commit(next)) return false;
        Changed?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    /// <summary>按下一条线开始拖：先记一份「拖之前」，松手时按有没有真的挪过决定进不进撤销栈。</summary>
    public bool BeginLineDrag(bool vertical, int index)
    {
        if (_beforeDrag != null || !Layout.IsValid || !AllowsAxis(vertical)) return false;

        var lines = vertical ? State.Vertical : State.Horizontal;
        if (index < 0 || index >= lines.Length) return false;

        _beforeDrag = State;
        Revision++;
        _dragVertical = vertical;
        _dragIndex = index;
        _dragStartIndex = index;
        HistoryChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// 拖到某个像素：夹在图内、不落在已占用的像素上（落在别的线身上时手势不结束，下一次
    /// 移动可以跨过去），跨过相邻线时更新 <see cref="DragIndex"/>。
    /// </summary>
    public bool MoveDraggedLine(int coordinate)
    {
        if (_beforeDrag == null || Image == null) return false;

        var lines = _dragVertical ? State.Vertical : State.Horizontal;
        int dimension = _dragVertical ? Image.PixelWidth : Image.PixelHeight;
        coordinate = Math.Clamp(coordinate, 1, dimension - 1);

        int previous = lines[_dragIndex];
        if (previous == coordinate) return false;

        int insertion = LowerBound(lines, coordinate);
        if (insertion < lines.Length && lines[insertion] == coordinate) return false;

        int index = insertion > _dragIndex ? insertion - 1 : insertion;
        var updated = new int[lines.Length];
        int source = 0;
        for (int i = 0; i < updated.Length; i++)
        {
            if (i == index) updated[i] = coordinate;
            else
            {
                if (source == _dragIndex) source++;
                updated[i] = lines[source++];
            }
        }

        bool indexChanged = index != _dragIndex;
        _dragIndex = index;
        State = _dragVertical
            ? State with { Vertical = updated, ManuallyEdited = true }
            : State with { Horizontal = updated, ManuallyEdited = true };
        Revision++;
        SynchronizeLines();

        Changed?.Invoke();
        if (indexChanged) HistoryChanged?.Invoke();
        return true;
    }

    /// <summary>松手：真的挪过就进撤销栈（一次拖动只算一步），没挪过就什么也不留。</summary>
    public void EndLineDrag()
    {
        if (_beforeDrag == null) return;

        var next = State;
        bool geometryChanged =
            !next.Vertical.AsSpan().SequenceEqual(_beforeDrag.Vertical) ||
            !next.Horizontal.AsSpan().SequenceEqual(_beforeDrag.Horizontal);

        State = _beforeDrag;
        Revision++;
        _beforeDrag = null;

        if (geometryChanged) Commit(next);
        else SynchronizeLines();

        Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    /// <summary>Esc：整次拖动作废（连跨线改过的序号一起还原）。</summary>
    public void CancelLineDrag()
    {
        if (_beforeDrag == null) return;

        State = _beforeDrag;
        Revision++;
        _beforeDrag = null;
        _dragIndex = _dragStartIndex;
        SynchronizeLines();

        Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    // ============ 内部 ============

    private bool Cacheable(SplitPrepared prepared) =>
        prepared.Settings.Type == SplitType.Smart &&
        prepared.Settings.Direction != SplitDirection.Grid &&
        prepared.Layout.IsValid;

    private void RestoreHistory()
    {
        Revision++;
        var entry = _history[_cursor];
        State = entry.State;
        Layout = new SplitLayout(Image?.PixelWidth ?? 0, Image?.PixelHeight ?? 0);
        foreach (var pair in entry.Errors) Layout.Errors[pair.Key] = pair.Value;
        SynchronizeLines();
    }

    private bool Commit(SplitState next)
    {
        if (next.Equals(State)) return false;

        if (_cursor + 1 < _history.Count)
            _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        _history.Add(new HistoryEntry(next, Snapshot(Layout.Errors)));
        _cursor = _history.Count - 1;
        State = next;
        Revision++;
        SynchronizeLines();
        return true;
    }

    private void SynchronizeLines()
    {
        Layout.Vertical.ReplaceWith(State.Vertical);
        Layout.Horizontal.ReplaceWith(State.Horizontal);
        Layout.Overlap = State.Settings.Overlap;
    }

    private static Dictionary<string, string> Snapshot(Dictionary<string, string> errors) =>
        new(errors, StringComparer.Ordinal);

    private static int LowerBound(int[] values, int coordinate)
    {
        int low = 0, high = values.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (values[mid] < coordinate) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    private static int[] Insert(int[] values, int index, int coordinate)
    {
        var result = new int[values.Length + 1];
        Array.Copy(values, 0, result, 0, index);
        result[index] = coordinate;
        Array.Copy(values, index, result, index + 1, values.Length - index);
        return result;
    }

    private sealed record HistoryEntry(SplitState State, Dictionary<string, string> Errors);
}
