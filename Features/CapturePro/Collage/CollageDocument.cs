using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼图的内容真源（照上游 <c>ImageStitchingDocument</c>）：图片清单 + 设置 + 选中项，
/// 所有改动都从这里过，<see cref="Layout"/> 按需重算。
/// </summary>
/// <remarks>
/// 文档操作的副作用照上游逐条对齐：追加只在原先没选中时才选中新图；删除选中项时选相邻一张；
/// 反转 / 移动 / 删除会清掉受影响范围的手摆位置；交换清掉两张的；**只有几何设置变**（背景色不算）
/// 才清空全部手摆位置。这些都不是「实现细节」——清多了用户摆好的图会跳回去，清少了尺寸又对不上。
/// </remarks>
internal sealed class CollageDocument
{
    private readonly List<CollageItem> _items = new();
    private long _nextId = 1;
    private long _selected;
    private bool _layoutDirty = true;
    private CollageLayout _layout = CollageLayout.Invalid;

    public CollageDocument()
    {
        Settings = CollageSettings.Default;
    }

    /// <summary>内容或几何变了（画布重画、页脚重算、缩略图重建）。</summary>
    public event Action? Changed;

    /// <summary>选中项变了（选中框、表单、缩略图高亮）。</summary>
    public event Action? SelectionChanged;

    public IReadOnlyList<CollageItem> Items => _items;

    public CollageSettings Settings { get; private set; }

    /// <summary>输出类型（静态图像 / PDF 文档）；只影响界面显示哪一套设置。</summary>
    public CollageOutputType OutputType { get; private set; } = CollageOutputType.StaticImage;

    /// <summary>用户当前填的 PDF 设置（可能校验不过，界面要照这个显示提示）。</summary>
    public CollagePdfSettings PdfSettings { get; private set; } = CollagePdfSettings.Default;

    /// <summary>最后一次校验通过的 PDF 设置（预览 / 页脚 / 保存都用它）。</summary>
    public CollagePdfSettings LastValidPdfSettings { get; private set; } = CollagePdfSettings.Default;

    public long SelectedId => _selected;

    public bool IsEmpty => _items.Count == 0;

    public CollageLayout Layout
    {
        get
        {
            if (_layoutDirty)
            {
                _layout = CollageLayoutCalculator.Calculate(_items, Settings);
                _layoutDirty = false;
            }

            return _layout;
        }
    }

    public CollageItem? Selected => Find(_selected);

    public CollageItem? Find(long id)
    {
        int index = IndexOf(id);
        return index >= 0 ? _items[index] : null;
    }

    public int IndexOf(long id)
    {
        if (id == 0) return -1;
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id == id) return i;
        }

        return -1;
    }

    // ============ 增删改 ============

    /// <summary>追加一张图；返回新 id（照上游 <c>append</c>：只在原先没选中时才选中它）。</summary>
    public long Append(BitmapSource image, string label)
    {
        var item = new CollageItem(_nextId++, image, label);
        _items.Add(item);

        var normalized = CollageLayoutCalculator.NormalizeCustomGrid(Settings, _items.Count,
            out bool adjusted);
        if (adjusted) Settings = normalized;

        bool select = _selected == 0;
        if (select) _selected = item.Id;
        MarkLayoutDirty();
        Changed?.Invoke();
        if (select) SelectionChanged?.Invoke();
        return item.Id;
    }

    /// <summary>换掉一张图的像素（照上游 <c>replace</c>：保留 id / 位置 / 顺序）。</summary>
    public bool Replace(long id, BitmapSource image, string label)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        var previous = _items[index];
        var replaced = new CollageItem(previous.Id, image, label)
        {
            Transform = previous.Transform,
            Placement = previous.Placement,
            PdfPlacement = previous.PdfPlacement,
        };
        _items[index] = replaced;
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>删掉一张（照上游 <c>remove</c>：清掉它之后所有手摆位置，选中项换到相邻）。</summary>
    public bool Remove(long id)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        _items.RemoveAt(index);
        ResetPlacements(index, _items.Count);

        bool selectionRemoved = _selected == id;
        if (selectionRemoved)
            _selected = _items.Count == 0 ? 0 : _items[Math.Min(index, _items.Count - 1)].Id;

        MarkLayoutDirty();
        Changed?.Invoke();
        if (selectionRemoved) SelectionChanged?.Invoke();
        return true;
    }

    /// <summary>清空（照上游 <c>clear</c>：取消选中、清空手摆位置）。</summary>
    public void Clear()
    {
        if (_items.Count == 0) return;

        _items.Clear();
        _selected = 0;
        MarkLayoutDirty();
        Changed?.Invoke();
        SelectionChanged?.Invoke();
    }

    /// <summary>反转顺序（照上游 <c>reverseItems</c>：只有原来的首尾会换位，所以清两端的手摆）。</summary>
    public bool Reverse()
    {
        if (_items.Count < 2) return false;

        _items.Reverse();
        int middle = _items.Count / 2;
        ResetPlacements(0, middle);
        ResetPlacements(middle + _items.Count % 2, _items.Count);
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>交换两张（照上游 <c>swapItems</c>：清掉两张的手摆位置）。</summary>
    public bool Swap(long first, long second)
    {
        int a = IndexOf(first);
        int b = IndexOf(second);
        if (a < 0 || b < 0 || a == b) return false;

        (_items[a], _items[b]) = (_items[b], _items[a]);
        _items[a].Placement = default;
        _items[b].Placement = default;
        _items[a].PdfPlacement = default;
        _items[b].PdfPlacement = default;
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>把一张挪到 <paramref name="finalIndex"/>（照上游 <c>moveItem</c>：清掉区间内的手摆）。</summary>
    public bool Move(long id, int finalIndex)
    {
        int source = IndexOf(id);
        if (source < 0 || finalIndex < 0 || finalIndex >= _items.Count || source == finalIndex)
            return false;

        var item = _items[source];
        _items.RemoveAt(source);
        _items.Insert(finalIndex, item);
        ResetPlacements(Math.Min(source, finalIndex), Math.Max(source, finalIndex) + 1);
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>顺时针 / 逆时针转 90°（照上游 <c>rotate</c>，矩阵右乘）。</summary>
    /// <remarks>
    /// 转之前先 <see cref="FreezePdfScale"/>：模板里的图没手调过比例时，把当前自动算出的
    /// 「毫米 / 像素」固定下来，转完还占同样的物理尺寸（照上游 <c>freezePdfScale</c>）。
    /// </remarks>
    public bool Rotate(long id, bool clockwise)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        FreezePdfScale(index);
        var item = _items[index];
        item.Transform = item.Transform.Multiply(clockwise
            ? CollageTransform.Clockwise
            : CollageTransform.CounterClockwise);
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>水平 / 垂直翻转（照上游 <c>flip</c>，同样先固定模板里的物理比例）。</summary>
    public bool Flip(long id, bool horizontal)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        FreezePdfScale(index);
        var item = _items[index];
        item.Transform = item.Transform.Multiply(horizontal
            ? CollageTransform.MirrorHorizontal
            : CollageTransform.MirrorVertical);
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>恢复原样（照上游 <c>resetTransform</c>：旋转 / 翻转 / 手摆一起清）。</summary>
    public bool ResetTransform(long id)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        var item = _items[index];
        bool orientation = !item.Transform.IsIdentity;
        bool placed = !item.Placement.IsDefault;
        bool pdfPlaced = !item.PdfPlacement.IsDefault;
        if (!orientation && !placed && !pdfPlaced) return true;

        item.Transform = CollageTransform.Identity;
        item.Placement = default;
        item.PdfPlacement = default;
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>记下手的摆放（照上游 <c>setStaticPlacement</c>：算出来是空矩形就拒绝）。</summary>
    public bool SetStaticPlacement(long id, CollagePlacement placement)
    {
        int index = IndexOf(id);
        if (index < 0) return false;

        var item = _items[index];
        if (item.Placement == placement) return false;

        var current = Layout;
        if (!current.IsValid) return false;
        if (index >= current.AutomaticRects.Count) return false;

        var candidate = new CollageItem(item.Id, item.Source, item.Label)
        {
            Transform = item.Transform,
            Placement = placement,
        };
        var resolved = CollageLayoutCalculator.ResolveStaticRect(candidate,
            current.AutomaticRects[index], current.Clips[index]);
        if (resolved.Width <= 0 || resolved.Height <= 0) return false;

        item.Placement = placement;
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>换设置（照上游 <c>setSettings</c>：几何变 → 清空手摆位置）。</summary>
    public bool SetSettings(CollageSettings settings)
    {
        if (!settings.IsValid) return false;

        var normalized = CollageLayoutCalculator.NormalizeCustomGrid(settings, _items.Count, out _);
        if (normalized == Settings) return false;

        if (!normalized.GeometryEquals(Settings))
        {
            foreach (var item in _items) item.Placement = default;
        }

        Settings = normalized;
        MarkLayoutDirty();
        Changed?.Invoke();
        return true;
    }

    /// <summary>换输出类型（照上游 <c>setOutputType</c>）。</summary>
    public bool SetOutputType(CollageOutputType type)
    {
        if (type is not (CollageOutputType.StaticImage or CollageOutputType.PdfDocument) ||
            type == OutputType)
            return false;

        OutputType = type;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 换 PDF 设置（照上游 <c>setPdfSettings</c>）：<see cref="PdfSettings"/> 永远记用户当前填的
    /// （界面要照它显示哪一项没填对），只有校验通过的才更新 <see cref="LastValidPdfSettings"/>；
    /// 几何真的变了（模板 / 纸张 / 方向 / 页边距 / 间距）才清空全部 PDF 手摆位置。
    /// </summary>
    public bool SetPdfSettings(CollagePdfSettings settings)
    {
        if (settings == PdfSettings) return false;

        PdfSettings = settings;
        if (CollagePdfGeometry.Validate(settings).Count == 0)
        {
            if (PdfGeometryChanged(LastValidPdfSettings, settings))
            {
                foreach (var item in _items) item.PdfPlacement = default;
            }

            LastValidPdfSettings = settings;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>记下 PDF 模板里的手摆（照上游 <c>setPdfPlacement</c>：比例乘像素溢出 / 非有限值都拒绝）。</summary>
    public bool SetPdfPlacement(long id, CollagePdfPlacement placement)
    {
        int index = IndexOf(id);
        if (index < 0 || !placement.IsValid) return false;

        var item = _items[index];
        if (item.PdfPlacement == placement) return false;

        if (placement.MillimetersPerPixel is double scale)
        {
            var (width, height) = item.TransformedSize;
            if (!double.IsFinite(width * scale) || !double.IsFinite(height * scale)) return false;
        }

        item.PdfPlacement = placement;
        Changed?.Invoke();
        return true;
    }

    /// <summary>PDF 几何比较（照上游 <c>pdfGeometryChanged</c>：导出模式 / 质量不算几何）。</summary>
    private static bool PdfGeometryChanged(CollagePdfSettings previous, CollagePdfSettings next) =>
        previous.Template != next.Template || previous.Paper != next.Paper ||
        previous.Orientation != next.Orientation ||
        CollagePdfGeometry.PaperMillimeters(previous) !=
        CollagePdfGeometry.PaperMillimeters(next) ||
        previous.MarginTopMm != next.MarginTopMm ||
        previous.MarginBottomMm != next.MarginBottomMm ||
        previous.MarginLeftMm != next.MarginLeftMm ||
        previous.MarginRightMm != next.MarginRightMm ||
        previous.ImageSpacingMm != next.ImageSpacingMm;

    /// <summary>选中（0 = 取消选中；不存在的 id 也当取消）。</summary>
    public void Select(long id)
    {
        long selected = IndexOf(id) >= 0 ? id : 0;
        if (selected == _selected) return;

        _selected = selected;
        SelectionChanged?.Invoke();
    }

    // ============ 内部 ============

    private void ResetPlacements(int first, int end)
    {
        int from = Math.Max(0, first);
        int to = Math.Min(_items.Count, end);
        for (int i = from; i < to; i++)
        {
            _items[i].Placement = default;
            _items[i].PdfPlacement = default;
        }
    }

    /// <summary>
    /// 把模板里这一张的自动比例固定成显式的「毫米 / 像素」（照上游 <c>freezePdfScale</c>）：
    /// 只在模板拼接、还没手调过比例时做一次，之后再转 / 翻都保持同一物理尺寸。
    /// </summary>
    private void FreezePdfScale(int index)
    {
        if (!LastValidPdfSettings.IsTemplateBased) return;
        if (index < 0 || index >= _items.Count) return;

        var item = _items[index];
        if (item.PdfPlacement.MillimetersPerPixel is not null) return;

        int capacity = CollagePdfGeometry.Capacity(LastValidPdfSettings);
        if (capacity <= 0) return;

        var page = CollagePdfGeometry.ComposedPage(CollagePdfRender.Snapshot(_items),
            LastValidPdfSettings, index / capacity);
        if (!page.IsValid || index % capacity >= page.Cells.Count) return;

        var cell = page.Cells[index % capacity];
        int width = item.TransformedSize.Width;
        if (width <= 0 || cell.ItemIndex < 0) return;

        item.PdfPlacement = item.PdfPlacement with
        {
            MillimetersPerPixel = cell.ImageMillimeters.Width / width,
        };
    }

    private void MarkLayoutDirty() => _layoutDirty = true;
}
