using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Native;

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
        SetCursor(null, null);
    }

    /// <summary>
    /// 只换底图：**原位重抓**用（照 Snow Shot 的 recapture）。几何（<see cref="Monitor"/> /
    /// <see cref="Origin"/> / <see cref="Scale"/>）、选区、标注、撤销栈全部不动——
    /// 和 <see cref="SwapBase"/> 的区别就在这里，那个是图片编辑窗换图，会把几何重置。
    /// </summary>
    public void ReplaceBaseKeepingFrame(BitmapSource image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        Base = image;
    }

    /// <summary>
    /// 只换底图（图片编辑窗的裁剪 / 撤销）：标注、序号、撤销栈都**不动**，坐标原点归零。
    /// 与 <see cref="LoadBase"/> 的区别是它会把标注一起清掉；选区 / 多区几何由调用方重设。
    /// </summary>
    public void SwapBase(BitmapSource image)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));
        Base = image;
        Origin = default;
        // 编辑器里没有「显示器」概念：把 Monitor 也收成这张图，选区 / 导出按它算偏移才不会留旧尺寸
        Monitor = new Int32Rect(0, 0, Math.Max(1, image.PixelWidth), Math.Max(1, image.PixelHeight));
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

    /// <summary>撤销栈（每份是一整份标注列表快照，老的在前面）；采集历史落盘时读它。</summary>
    public IReadOnlyList<Mark[]> UndoStack => _undo;

    /// <summary>重做栈。</summary>
    public IReadOnlyList<Mark[]> RedoStack => _redo;

    /// <summary>
    /// 整份放回：标注 + 撤销 / 重做栈（采集历史「重新编辑截图」）。
    /// 与 <see cref="ResetMarks"/> 的区别是会**连历史一起**恢复——用户接着按 Ctrl+Z 能退到当初更早的状态。
    /// </summary>
    public void ImportHistory(IReadOnlyList<Mark> marks, IEnumerable<Mark[]> undo, IEnumerable<Mark[]> redo)
    {
        _marks.Clear();
        _marks.AddRange(marks);

        _undo.Clear();
        foreach (var snapshot in undo) _undo.Add(snapshot);
        // 落盘上限与内存一致：多出来的老快照丢掉（超出的都是更早的）
        if (_undo.Count > MaxUndo) _undo.RemoveRange(0, _undo.Count - MaxUndo);

        _redo.Clear();
        foreach (var snapshot in redo) _redo.Add(snapshot);

        _serial = _marks.OfType<Mark.Serial>().Select(s => s.Number).DefaultIfEmpty(0).Max();
        Dirty = true;
    }

    /// <summary>选区（物理像素，屏幕绝对坐标）。未框选时宽高为 0。</summary>
    public Int32Rect Area { get; set; }

    // ============ 鼠标指针（照上游 screenshot/capture_cursor） ============

    private BitmapSource? _cursorImage;
    private Point _cursorOffset;
    private bool _cursorVisible;
    private bool _cursorAvailable;

    /// <summary>这一张抓帧时取到了光标形状（取到了才给「显示光标」开关）。</summary>
    public bool CursorAvailable => _cursorAvailable;

    /// <summary>成品里要不要画鼠标指针（新截图默认取 <c>screenshot/capture_cursor</c>，覆盖层里可切）。</summary>
    public bool CursorVisible => _cursorAvailable && _cursorVisible;

    /// <summary>
    /// 换一份光标快照：<paramref name="frame"/> 是形状（直通 alpha 的 BGRA），
    /// <paramref name="hotspot"/> 是它在虚拟桌面上的热点（物理像素）。
    /// 传 <c>null</c> = 这张没有光标（取不到 / 老记录），开关跟着失效。
    /// </summary>
    public void SetCursor(CursorCapture.CursorFrame? frame, Point? hotspot, bool? visible = null)
    {
        if (frame is not { Width: > 0, Height: > 0 } || hotspot is not { } at ||
            frame.Pixels.Length != frame.Width * frame.Height * 4)
        {
            SetCursorImage(null, default, false);
            return;
        }

        var image = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32,
            null, frame.Pixels, frame.Width * 4);
        image.Freeze();

        // 相对底图左上角（图像像素）：粘贴时热点对准屏幕坐标，画的时候加回左上角
        SetCursorImage(image, CursorTopLeft(at, frame.HotspotX, frame.HotspotY, Monitor),
            visible ?? _cursorVisible);
    }

    /// <summary>指针热点（屏幕物理像素）→ 光标小图左上角相对整屏底图的偏移（图像像素）。</summary>
    internal static Point CursorTopLeft(Point hotspot, int iconHotspotX, int iconHotspotY, Int32Rect monitor) =>
        new(hotspot.X - iconHotspotX - monitor.X, hotspot.Y - iconHotspotY - monitor.Y);

    /// <summary>
    /// 直接放一张已经画好的光标小图（采集历史里「重新编辑截图」用：那一份只有像素和偏移）。
    /// <paramref name="offset"/> 相对底图左上角（图像像素）。
    /// </summary>
    public void SetCursorImage(BitmapSource? image, Point offset, bool visible)
    {
        if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            _cursorImage = null;
            _cursorAvailable = false;
            _cursorVisible = false;
            _cursorOffset = default;
            return;
        }

        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        _cursorImage = image;
        _cursorOffset = offset;
        _cursorAvailable = true;
        _cursorVisible = visible;
    }

    /// <summary>切「成品里画不画鼠标指针」。返回切换之后的状态；没有光标快照时恒为 false。</summary>
    public bool SetCursorVisible(bool visible)
    {
        if (!_cursorAvailable) return false;
        _cursorVisible = visible;
        return _cursorVisible;
    }

    /// <summary>这一张的光标快照（落采集历史用）。</summary>
    public (BitmapSource? Image, Point Offset, bool Visible) CursorSnapshot =>
        (_cursorImage, _cursorOffset, CursorVisible);

    /// <summary>
    /// 把鼠标指针画到底图上（预览与导出共用）：
    /// 调用方已经推好「图像像素 → 输出」的变换，这里只按底图左上角摆那一小块。
    /// </summary>
    public void DrawCursor(DrawingContext dc)
    {
        if (!CursorVisible || _cursorImage is null) return;

        double s = Scale > 0 ? Scale : 1;
        dc.DrawImage(_cursorImage, new Rect(
            _cursorOffset.X / s, _cursorOffset.Y / s,
            _cursorImage.Width / s, _cursorImage.Height / s));
    }

    /// <summary>
    /// 多区域选区（**图像像素，相对底图左上角**）：加区并、减区挖，导出时按它裁透明。
    /// null = 一整块矩形（位图本身就是选区尺寸，不用裁）。
    /// 外接框必须与 <see cref="Area"/> 对得上——两者由 <c>SyncArea</c> 从同一份选区算出来。
    /// </summary>
    public SelectionRegion[]? Regions { get; set; }

    /// <summary>是不是需要按形状裁（多块 / 自由 / 折线 / 挖洞）。</summary>
    public bool HasRegions => Regions is { Length: > 0 };

    /// <summary>
    /// 选区外观（圆角 / 阴影 / 边框）。预览与导出都读这一份，**没有第二份状态**；
    /// 直角且没有效果时导出走「不合成」的快路径（尺寸与以前完全一致）。
    /// </summary>
    public SelectionStyle Style { get; set; } = SelectionStyle.None;

    /// <summary>
    /// 导出时压缩选区里的空白（照上游 <c>layout_compaction_enabled</c>）：多块加区之间那些整段空白的
    /// 行列被抽掉、按 <see cref="CompactSpacing"/> 重新贴紧。单块矩形 / 单块点列没有可压缩的整段空白。
    /// </summary>
    public bool CompactLayout { get; set; }

    /// <summary>压缩后每段之间留多少物理像素（0–100，照上游 <c>layout_compaction_spacing</c>）。</summary>
    public int CompactSpacing { get; set; }

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
    /// 一次改多条（自动打码一次填充 / 一次框选）：整批**只占一个撤销位**。
    /// 改完的列表与现在一模一样（引用序列相同）就不记撤销，也别把「点了一下没生效」写进历史。
    /// </summary>
    public void EditBatch(Action<List<Mark>> edit)
    {
        var next = new List<Mark>(_marks);
        edit(next);
        if (next.Count == _marks.Count && next.SequenceEqual(_marks)) return;

        Push();
        _marks.Clear();
        _marks.AddRange(next);
        Dirty = true;
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

    /// <summary>最上面一个命中**且满足条件**的标注下标（快速选择按工具族过滤时用）。</summary>
    public int HitTestWhere(Point point, double tolerance, Func<Mark, bool> accept)
    {
        for (int i = _marks.Count - 1; i >= 0; i--)
            if (_marks[i].HitTest(point, tolerance) && accept(_marks[i])) return i;
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
    /// <remarks>
    /// 圆角 / 阴影 / 边框走 <see cref="RenderStyled"/>：输出尺寸 = 内容 + 两侧效果宽，
    /// 内容按形状裁（圆角外留透明），效果画在内容外面（照上游 <c>ScreenshotResultCompositor</c>）。
    /// 直角且没有效果时 <see cref="RenderPlain"/> 保持原尺寸（不多一圈透明）。
    /// </remarks>
    public BitmapSource Render()
    {
        var area = Area;
        if (!HasArea) throw new InvalidOperationException("还没有选区，不能导出");

        var style = Style.Clamp();

        // 版面压缩：多块加区之间整段空白的行列抽掉（输出尺寸会变小）
        if (CompactionPlanFor(area, style) is { } plan)
            return RenderCompacted(area, style, plan);

        var result = style.IsPlain ? RenderPlain(area) : RenderStyled(area, style);
        return style.BackgroundOpacity > 0 ? WithBackground(result, area, style) : result;
    }

    /// <summary>
    /// 把底图（**不含标注**）按 <see cref="SelectionStyle.BackgroundOpacity"/> 垫在成品后面：
    /// 圆角外 / 形状外 / 阴影留白那片原本透明的地方变成淡化的上下文（照上游
    /// <c>background_opacity_percent</c> + <c>maskBackgroundForSelection</c>）。
    /// </summary>
    private BitmapSource WithBackground(BitmapSource result, Int32Rect area, SelectionStyle style)
    {
        double s = Scale > 0 ? Scale : 1;
        int effect = style.IsPlain ? 0 : style.EffectPixels(s);
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushOpacity(style.BackgroundOpacity / 100.0);
            PushContent(dc, dx - effect, dy - effect);
            dc.DrawImage(Base, Canvas);
            dc.Pop();
            dc.Pop();
            dc.Pop();

            // 成品画在上面：内容不透明的地方完全盖住背景，透明的地方透出淡化的底图
            dc.DrawImage(result, new Rect(0, 0, result.PixelWidth, result.PixelHeight));
        }

        return Finish(visual, result.PixelWidth, result.PixelHeight);
    }

    /// <summary>多块加区在内容像素下的外接框（压缩方案只认加区，减区跟着它自己那块一起搬）。</summary>
    private LayoutCompaction.Plan? CompactionPlanFor(Int32Rect area, SelectionStyle style)
    {
        if (!CompactLayout || Regions is not { Length: > 0 } regions) return null;

        var blocks = new List<Rect>();
        for (int i = 0; i < regions.Length; i++)
        {
            if (regions[i].Mode != RegionMode.Add) continue;
            blocks.Add(ToContentPixels(regions[i].Bounds, area));
        }

        if (blocks.Count < 2) return null;
        return LayoutCompaction.Build(blocks, area.Width, area.Height, CompactSpacing);
    }

    /// <summary>「图像像素（相对底图左上角）」→「内容像素（相对选区左上角）」。</summary>
    private Rect ToContentPixels(Rect bounds, Int32Rect area)
    {
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;
        return new Rect(bounds.X - dx, bounds.Y - dy, bounds.Width, bounds.Height);
    }

    /// <summary>
    /// 压缩后的导出：每块内容按方案搬到新位置，形状裁剪、圆角、阴影 / 边框、背景都跟着搬
    /// （照上游 <c>ScreenshotLayoutCompactionPlan</c> 的 tile 模型）。
    /// </summary>
    private BitmapSource RenderCompacted(Int32Rect area, SelectionStyle style, LayoutCompaction.Plan plan)
    {
        double s = Scale > 0 ? Scale : 1;
        int effect = style.IsPlain ? 0 : style.EffectPixels(s);
        int radius = style.RadiusPixels(s);
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        int width = Math.Max(1, plan.Width + effect * 2);
        int height = Math.Max(1, plan.Height + effect * 2);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var tile in plan.Tiles)
            {
                if (tile.Region < 0 || tile.Region >= (Regions?.Length ?? 0)) continue;

                // 这一块搬到输出坐标后的形状（内容像素 → 输出像素）
                SelectionRegion moved = MoveRegion(Regions![tile.Region], dx, dy,
                    effect + tile.Dx, effect + tile.Dy);
                Geometry path = SelectionSet.Combine(new[] { moved }, radius) ?? moved.Geometry();

                if (style.BackgroundOpacity > 0)
                {
                    dc.PushOpacity(style.BackgroundOpacity / 100.0);
                    PushContent(dc, dx - effect - tile.Dx, dy - effect - tile.Dy);
                    dc.DrawImage(Base, Canvas);
                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                }

                dc.PushClip(path);
                PushContent(dc, dx - effect - tile.Dx, dy - effect - tile.Dy);
                DrawContent(dc);
                dc.Pop();
                dc.Pop();
                dc.Pop();

                if (effect > 0)
                {
                    if (style.BorderType == SelectionBorderType.Border)
                        SelectionEffects.DrawBorder(dc, path, moved.Bounds, radius, effect, style.BorderColor);
                    else
                        SelectionEffects.DrawShadow(dc, path, moved.Bounds, radius, effect, style.ShadowColor);
                }
            }
        }

        return Finish(visual, width, height);
    }

    /// <summary>把一块选区从「图像像素」搬到输出像素下的新位置。</summary>
    private static SelectionRegion MoveRegion(SelectionRegion region, double dx, double dy,
        double shiftX, double shiftY)
    {
        var box = new Rect(region.Box.X - dx + shiftX, region.Box.Y - dy + shiftY,
            region.Box.Width, region.Box.Height);
        Point[]? points = null;
        if (region.Points is { } source)
        {
            points = new Point[source.Length];
            for (int i = 0; i < source.Length; i++)
                points[i] = new Point(source[i].X - dx + shiftX, source[i].Y - dy + shiftY);
        }

        return new SelectionRegion(region.Mode, box, points, region.Smooth);
    }

    private BitmapSource RenderPlain(Int32Rect area)
    {
        // 选区相对这块屏的左上角（物理像素）；历史翻页时底图还整个偏了 Origin，一起抵掉
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            PushContent(dc, dx, dy);

            // 多区域 / 自由 / 折线选区：按合起来的几何裁（形状外留透明）。几何写的是「相对底图左上角的 DIP」，
            // 跟上面两层变换同一个坐标系；一整块矩形不用裁（位图本身就是它的尺寸）。
            Geometry? clip = RegionClip();
            if (clip != null)
            {
                dc.PushClip(clip);
                DrawContent(dc);
                dc.Pop();
            }
            else
            {
                DrawContent(dc);
            }

            dc.Pop();
            dc.Pop();
        }

        return Finish(visual, Math.Max(1, area.Width), Math.Max(1, area.Height));
    }

    /// <summary>
    /// 带圆角 / 阴影 / 边框的导出（照上游 <c>ScreenshotResultCompositor::compose</c>）：
    /// 内容画在 <c>效果宽</c> 的内边距里，外面再加一圈阴影或外扩边框。
    /// </summary>
    private BitmapSource RenderStyled(Int32Rect area, SelectionStyle style)
    {
        double s = Scale > 0 ? Scale : 1;
        int effect = style.EffectPixels(s);
        int radius = style.RadiusPixels(s);
        double dx = area.X - Monitor.X;
        double dy = area.Y - Monitor.Y;

        var content = new Rect(effect, effect, Math.Max(1, area.Width), Math.Max(1, area.Height));
        Geometry path = ContentPathPixels(content, radius);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // 内容：DIP 变换 + 形状裁剪（圆角 / 多块 / 挖洞都在 path 里）
            dc.PushClip(path);
            PushContent(dc, dx - effect, dy - effect);
            DrawContent(dc);
            dc.Pop();
            dc.Pop();
            dc.Pop();

            // 效果画在内容外面：阴影（默认）或外扩边框
            if (style.BorderType == SelectionBorderType.Border)
                SelectionEffects.DrawBorder(dc, path, content, radius, effect, style.BorderColor);
            else
                SelectionEffects.DrawShadow(dc, path, content, radius, effect, style.ShadowColor);
        }

        return Finish(visual, Math.Max(1, area.Width + effect * 2), Math.Max(1, area.Height + effect * 2));
    }

    /// <summary>推入「DIP 内容」的两层变换（缩放 + 平移到选区左上角）。</summary>
    private void PushContent(DrawingContext dc, double dx, double dy)
    {
        dc.PushTransform(new ScaleTransform(Scale, Scale));                                   // DIP → 物理像素
        dc.PushTransform(new TranslateTransform(-dx / Scale - Origin.X, -dy / Scale - Origin.Y)); // 平移到选区左上角
    }

    /// <summary>底图 + 标注（调用方已经推好变换与裁剪）。</summary>
    private void DrawContent(DrawingContext dc)
    {
        dc.DrawImage(Base, Canvas);
        DrawCursor(dc);
        MarkRenderer.DrawAll(dc, _marks, Base, Canvas, Scale);
    }

    private static BitmapSource Finish(DrawingVisual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 内容形状（**输出像素坐标**）：一块矩形 + 圆角，或者多块合成（矩形块各自圆角、点列块原样）。
    /// </summary>
    private Geometry ContentPathPixels(Rect content, int radius)
    {
        if (Regions is not { Length: > 0 } regions)
            return new RectangleGeometry(content, radius, radius);

        // 选区几何是「相对底图左上角的图像像素」→ 输出像素：减掉选区左上的图像坐标，再加效果内边距
        double dx = Area.X - Monitor.X - content.X;
        double dy = Area.Y - Monitor.Y - content.Y;

        var local = new SelectionRegion[regions.Length];
        for (int i = 0; i < regions.Length; i++)
        {
            var region = regions[i];
            var box = new Rect(region.Box.X - dx, region.Box.Y - dy, region.Box.Width, region.Box.Height);
            Point[]? points = null;
            if (region.Points is { } source)
            {
                points = new Point[source.Length];
                for (int j = 0; j < source.Length; j++)
                    points[j] = new Point(source[j].X - dx, source[j].Y - dy);
            }
            local[i] = new SelectionRegion(region.Mode, box, points, region.Smooth);
        }

        return SelectionSet.Combine(local, radius) ?? new RectangleGeometry(content, radius, radius);
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

            local[i] = new SelectionRegion(region.Mode, box, points, region.Smooth);
        }

        return SelectionSet.Combine(local);
    }
}
