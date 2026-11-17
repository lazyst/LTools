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
    public BitmapSource Base { get; private set; }

    /// <summary>这块显示器的物理像素矩形（屏幕绝对坐标）。</summary>
    public Int32Rect Monitor { get; private set; }

    /// <summary>物理像素 / DIP。跨屏 + 混合 DPI 时以窗口真正显示时的比例为准（见 <see cref="SetScale"/>）。</summary>
    public double Scale { get; private set; }

    /// <summary>
    /// 底图左上角在窗口 DIP 里的位置。实时截图恒为 <c>(0,0)</c>（窗口正好盖住屏幕）；
    /// 翻采集历史时把那张图摆在窗口中间（见 <see cref="LoadBase"/>）。
    /// </summary>
    public Point Origin { get; private set; }

    /// <summary>
    /// 窗口真正显示之后校准缩放比（<c>像素宽 / ActualWidth</c>，方向是**物理 ÷ DIP**）：跨屏时
    /// 窗口落在哪块显示器上、系统给的 DPI 是多少，只有这时才知道。只影响 DIP 换算，导出的像素尺寸不变。
    /// </summary>
    public void SetScale(double scale)
    {
        if (scale > 0 && Math.Abs(scale - Scale) > 1e-6) Scale = scale;
    }

    /// <summary>
    /// 换一张底图（翻采集历史）：标注、撤销栈、序号、选区一起清空，坐标原点按 <paramref name="origin"/>
    /// 摆在窗口里的某个位置（历史图居中）。**这是唯一会改底图的入口**——别在别处给 <see cref="Base"/> 赋值。
    /// </summary>
    public void LoadBase(BitmapSource image, Int32Rect monitor, double scale, Point origin)
    {
        Base = image;
        Monitor = monitor;
        Scale = scale <= 0 ? 1.0 : scale;
        Origin = origin;

        _marks.Clear();
        _undo.Clear();
        _redo.Clear();
        _serial = 0;
        Area = default;
        Regions = null;
        Dirty = false;
    }

    /// <summary>把一份标注快照原样放回来（从历史翻页退回实时截图时用），序号接着最大的那个。</summary>
    public void ResetMarks(IReadOnlyList<Mark> marks)
    {
        _marks.Clear();
        _marks.AddRange(marks);
        _undo.Clear();
        _redo.Clear();
        _serial = _marks.OfType<Mark.Serial>().Select(s => s.Number).DefaultIfEmpty(0).Max();
    }

    /// <summary>选区（物理像素，屏幕绝对坐标）。未框选时宽高为 0。</summary>
    public Int32Rect Area { get; set; }

    /// <summary>
    /// 多区域选区（**图像像素，相对底图左上角**）：加区并、减区挖，导出时按它裁透明。
    /// null = 一整块矩形（位图本身就是选区尺寸，不用裁）。
    /// 外接框必须与 <see cref="Area"/> 对得上——两者由 <c>SyncArea</c> 从同一份选区算出来。
    /// </summary>
    public SelectionRegion[]? Regions { get; set; }

    /// <summary>是不是需要按形状裁（多块 / 自由 / 折线 / 挖洞）。</summary>
    public bool HasRegions => Regions is { Length: > 0 };

    public bool HasArea => Area.Width >= 2 && Area.Height >= 2;

    /// <summary>标注列表，顺序即绘制顺序（后画的在上面）。</summary>
    public IReadOnlyList<Mark> Marks => _marks;

    /// <summary>底图在 DIP 下的整块矩形（左上角是 <see cref="Origin"/>）。</summary>
    public Rect Canvas => new(Origin.X, Origin.Y, Base.PixelWidth / Scale, Base.PixelHeight / Scale);

    /// <summary>选区在窗口 DIP 坐标下的矩形（绘制与命中用）。</summary>
    public Rect SelectionDip
    {
        get
        {
            if (!HasArea) return Rect.Empty;
            return new Rect(Origin.X + (Area.X - Monitor.X) / Scale, Origin.Y + (Area.Y - Monitor.Y) / Scale,
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
    /// 按选区导出成品：从底图 + 标注**重绘**，不是截屏再裁。
    /// 只在 UI 线程调用（<see cref="RenderTargetBitmap"/> 要求）。
    /// </summary>
    public BitmapSource Render()
    {
        var area = Area;
        if (!HasArea) throw new InvalidOperationException("还没有选区，不能导出");

        // 选区相对这块屏的左上角（物理像素）；历史翻页时底图还整个偏了 Origin，一起抵掉
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));               // DIP → 物理像素
            dc.PushTransform(new TranslateTransform(-dx / Scale - Origin.X, -dy / Scale - Origin.Y)); // 平移到选区左上角

            // 多区域 / 自由 / 折线选区：按合起来的几何裁（形状外留透明）。几何写的是「相对底图左上角的 DIP」，
            // 跟上面两层变换同一个坐标系；一整块矩形不用裁（位图本身就是它的尺寸）。
            Geometry? clip = RegionClip();
            if (clip != null) dc.PushClip(clip);

            dc.DrawImage(Base, Canvas);
            MarkRenderer.DrawAll(dc, _marks, Base, Canvas, Scale);

            if (clip != null) dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(Math.Max(1, area.Width), Math.Max(1, area.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 选区的裁剪几何：把「图像像素」换成「相对底图左上角的 DIP」，与渲染的两层变换同一坐标系
    /// （<c>Scale + Translate</c>）。**别再减选区的 dx / dy**：那等于把裁剪框又挪了一个选区的位置，
    /// 形状外一大片被裁成透明（2026-11-17 修，导出的图右边 / 下边被切掉）。一整块矩形返回 null（不用裁）。
    /// </summary>
    private Geometry? RegionClip()
    {
        if (Regions is not { Length: > 0 } regions) return null;

        double s = Scale > 0 ? Scale : 1;
        var local = new SelectionRegion[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            var region = regions[i];
            var box = new Rect(
                region.Box.X / s + Origin.X, region.Box.Y / s + Origin.Y,
                region.Box.Width / s, region.Box.Height / s);

            Point[]? points = null;
            if (region.Points is { } source)
            {
                points = new Point[source.Length];
                for (int j = 0; j < source.Length; j++)
                    points[j] = new Point(source[j].X / s + Origin.X, source[j].Y / s + Origin.Y);
            }

            local[i] = new SelectionRegion(region.Mode, box, points);
        }

        return SelectionSet.Combine(local);
    }
}
