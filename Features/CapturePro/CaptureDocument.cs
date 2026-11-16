using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro;

/// <summary>
/// 一次截图的内容真源：冻结底图 + 选区 + 标注列表 + 撤销栈。
/// 显示与导出都从这里取数据，**没有第二份状态**（工具栏只改当前样式）。
/// </summary>
internal sealed class CaptureDocument
{
    /// <summary>撤销栈保留的快照份数。</summary>
    public const int MaxUndo = 50;

    private readonly List<Mark> _marks = new();
    private readonly List<Mark[]> _undo = new();
    private readonly List<Mark[]> _redo = new();
    private int _serial;

    public CaptureDocument(BitmapSource baseImage, Int32Rect monitor, double scale)
    {
        Base = baseImage;
        Monitor = monitor;
        Scale = scale <= 0 ? 1.0 : scale;
    }

    /// <summary>冻结的整屏图像（物理像素，左上角 = <see cref="Monitor"/> 的左上角）。</summary>
    public BitmapSource Base { get; }

    /// <summary>这块显示器的物理像素矩形（屏幕绝对坐标）。</summary>
    public Int32Rect Monitor { get; }

    /// <summary>物理像素 / DIP。跨屏 + 混合 DPI 时以窗口真正显示时的比例为准（见 <see cref="SetScale"/>）。</summary>
    public double Scale { get; private set; }

    /// <summary>
    /// 窗口真正显示之后校准缩放比（<c>像素宽 / ActualWidth</c>，方向是**物理 ÷ DIP**）：跨屏时
    /// 窗口落在哪块显示器上、系统给的 DPI 是多少，只有这时才知道。只影响 DIP 换算，导出的像素尺寸不变。
    /// </summary>
    public void SetScale(double scale)
    {
        if (scale > 0 && Math.Abs(scale - Scale) > 1e-6) Scale = scale;
    }

    /// <summary>选区（物理像素，屏幕绝对坐标）。未框选时宽高为 0。</summary>
    public Int32Rect Area { get; set; }

    public bool HasArea => Area.Width >= 2 && Area.Height >= 2;

    /// <summary>标注列表，顺序即绘制顺序（后画的在上面）。</summary>
    public IReadOnlyList<Mark> Marks => _marks;

    /// <summary>底图在 DIP 下的整块矩形。</summary>
    public Rect Canvas => new(0, 0, Base.PixelWidth / Scale, Base.PixelHeight / Scale);

    /// <summary>选区在窗口 DIP 坐标下的矩形（绘制与命中用）。</summary>
    public Rect SelectionDip
    {
        get
        {
            if (!HasArea) return Rect.Empty;
            return new Rect((Area.X - Monitor.X) / Scale, (Area.Y - Monitor.Y) / Scale,
                Area.Width / Scale, Area.Height / Scale);
        }
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool Dirty { get; private set; }

    // ============ 编辑 ============

    /// <summary>加一条标注，返回它的下标（撤销从这里退回去）。</summary>
    public int Add(Mark mark)
    {
        Push();
        _marks.Add(mark);
        Dirty = true;
        return _marks.Count - 1;
    }

    /// <summary>换掉一条（改样式 / 拖动后的落点）。</summary>
    public void ReplaceAt(int index, Mark mark)
    {
        if (index < 0 || index >= _marks.Count) return;
        Push();
        _marks[index] = mark;
        Dirty = true;
    }

    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= _marks.Count) return false;
        Push();
        _marks.RemoveAt(index);
        Dirty = true;
        return true;
    }

    /// <summary>
    /// 丢掉所有标注（右键取消这次框选时用）。进撤销栈，所以误清还能 <c>Ctrl+Z</c> 找回来。
    /// </summary>
    public void ClearMarks()
    {
        if (_marks.Count == 0) return;
        Push();
        _marks.Clear();
        Dirty = true;
    }

    /// <summary>最上面一个命中的标注下标（没有 = -1）。</summary>
    public int HitTest(Point point, double tolerance)
    {
        for (int i = _marks.Count - 1; i >= 0; i--)
            if (_marks[i].HitTest(point, tolerance)) return i;
        return -1;
    }

    /// <summary>分配下一个序号（删除不重排，与截图工具的惯例一致）。</summary>
    public int NextSerial() => ++_serial;

    public void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(Snapshot());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(Snapshot());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
    }

    private Mark[] Snapshot() => _marks.ToArray();

    private void Push()
    {
        _undo.Add(Snapshot());
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Restore(Mark[] snapshot)
    {
        _marks.Clear();
        _marks.AddRange(snapshot);
        // 撤销把「刚分配的序号」退回去时，下一个序号不该跳号
        _serial = _marks.OfType<Mark.Serial>().Select(s => s.Number).DefaultIfEmpty(0).Max();
        Dirty = true;
    }

    // ============ 导出 ============

    /// <summary>
    /// 把当前标注叠到另一张图上（长截图的拼接结果）。
    /// 标注坐标本来就以选区左上角为原点，所以长图直接当底图铺一遍即可（马赛克也从这张图取色）。
    /// 没有标注时原样返回。只在 UI 线程调用。
    /// </summary>
    public BitmapSource RenderOnto(BitmapSource baseImage)
    {
        if (_marks.Count == 0 || baseImage.PixelWidth < 1 || baseImage.PixelHeight < 1) return baseImage;

        var canvas = new Rect(0, 0, baseImage.PixelWidth / Scale, baseImage.PixelHeight / Scale);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));   // DIP → 物理像素
            dc.DrawImage(baseImage, canvas);
            MarkRenderer.DrawAll(dc, _marks, baseImage, canvas, Scale);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(baseImage.PixelWidth, baseImage.PixelHeight,
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 按选区导出成品：从底图 + 标注**重绘**，不是截屏再裁。
    /// 只在 UI 线程调用（<see cref="RenderTargetBitmap"/> 要求）。
    /// </summary>
    public BitmapSource Render()
    {
        var area = Area;
        if (!HasArea) throw new InvalidOperationException("还没有选区，不能导出");

        // 选区相对这块屏的左上角（物理像素）
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));               // DIP → 物理像素
            dc.PushTransform(new TranslateTransform(-dx / Scale, -dy / Scale)); // 平移到选区左上角
            dc.DrawImage(Base, Canvas);
            MarkRenderer.DrawAll(dc, _marks, Base, Canvas, Scale);
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(Math.Max(1, area.Width), Math.Max(1, area.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
