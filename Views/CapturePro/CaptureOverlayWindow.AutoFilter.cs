using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.AutoFilter;

namespace LTools.Views.CapturePro;

/// <summary>
/// 自动打码：进入工具就识别选区里的文字 / 图片 / 头像 / 图标…，然后在画布上点亮哪块、
/// 框选一批、点一下（或「填充区域」按类型）用当前滤镜一次打码。
/// </summary>
/// <remarks>
/// <para>
/// 逐条对齐上游 <c>ScreenshotAutoFilterController</c> + <c>auto_filter_workflow.rs</c> +
/// <c>auto_filter.rs</c>：检测输入是选区内的**底图**（不含标注）；换算
/// <c>bounds.x + region.x * bounds.width / pixels.width</c> 后与选区求交、丢掉空区域；
/// 结果到了闪红 300ms、识别中扫过一道光带；单击 / 框选松手都是 <c>toggle = true</c>
/// 的填充事务（同类型再点一次删掉、不同类型替换），「填充区域」是 <c>toggle = false</c>。
/// </para>
/// <para>
/// 区域坐标进 <see cref="AutoFilterState"/> 时已经是窗口 DIP（与标注 / 选区同一个坐标系），
/// 画布只读它，导出完全不受影响（自动打码落的是普通 <see cref="Mark.Filter"/>）。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private AutoFilterState _autoFilter = new();
    private CancellationTokenSource? _autoFilterCts;
    private DispatcherTimer? _autoFilterTick;
    private Rect _autoFilterDetecting;      // 正在识别的那块选区（换选区就作废重来）
    private Point _autoFilterStart;

    // ============ 进入 / 退出工具 ============

    /// <summary>切到自动打码：接过画布覆盖层，选区已经有内容就立刻识别（上游 <c>validate()</c>）。</summary>
    private void EnterAutoFilter()
    {
        Surface.AutoFilter = _autoFilter;
        Surface.AutoFillKind = _style.FilterForAutoFill;
        Surface.AutoFilterClock = Environment.TickCount64;
        SyncAutoFilter(detect: true);
    }

    /// <summary>离开自动打码：取消识别、丢掉结果、覆盖层撤掉（识别一半的线程自己会退出）。</summary>
    private void LeaveAutoFilter()
    {
        CancelAutoFilterDetection();
        StopAutoFilterTimer();
        _autoFilter.Clear();
        Surface.AutoFilter = null;
        Surface.InvalidateVisual();
        Toolbar.SetAutoFilterState(false);
    }

    /// <summary>
    /// 对齐「识别结果 ↔ 当前选区」：对得上就直接用；对不上就作废，<paramref name="detect"/> 时重新识别。
    /// 选区还没框好（或已经清掉）就什么都不做——「填充区域」一直是灰的。
    /// </summary>
    private void SyncAutoFilter(bool detect)
    {
        if (_tool != CaptureTool.AutoFilter)
        {
            Toolbar.SetAutoFilterState(false);
            return;
        }

        Surface.AutoFilter = _autoFilter;
        Surface.AutoFillKind = _style.FilterForAutoFill;

        if (_sel is not Rect bounds || !IsValid(bounds))
        {
            CancelAutoFilterDetection();
            StopAutoFilterTimer();
            _autoFilter.Clear();
            Toolbar.SetAutoFilterState(false);
            Surface.InvalidateVisual();
            return;
        }

        if (_autoFilter.Matches(bounds))
        {
            Toolbar.SetAutoFilterState(true);
            return;
        }

        if (detect && _autoFilter.Detecting && AutoFilterLogic.BoundsMatch(_autoFilterDetecting, bounds)) return;

        CancelAutoFilterDetection();
        _autoFilter.Clear();
        Toolbar.SetAutoFilterState(false);

        if (detect) StartAutoFilterDetection(bounds);
        else Surface.InvalidateVisual();
    }

    /// <summary>选区被挪动 / 改了大小：只把「填充区域」置灰，结果等松手再作废（上游同样是提交后才清记录）。</summary>
    private void RefreshAutoFilterAvailability()
    {
        bool available = _tool == CaptureTool.AutoFilter &&
                         _sel is Rect bounds && IsValid(bounds) && _autoFilter.Matches(bounds);
        Toolbar.SetAutoFilterState(available);
    }

    // ============ 识别 ============

    private void StartAutoFilterDetection(Rect bounds)
    {
        CancelAutoFilterDetection();

        var image = CropBaseToBgr(bounds, out int pixelWidth, out int pixelHeight);
        if (image == null) return;

        var cts = new CancellationTokenSource();
        _autoFilterCts = cts;
        _autoFilterDetecting = bounds;
        _autoFilter.Detecting = true;
        _autoFilter.ScanStartMs = Environment.TickCount64;
        Surface.AutoFilterClock = Environment.TickCount64;
        Surface.InvalidateVisual();
        StartAutoFilterTimer();

        _ = DetectAsync(image, bounds, pixelWidth, pixelHeight, cts);
    }

    private async Task DetectAsync(BgrImg image, Rect bounds, int pixelWidth, int pixelHeight,
        CancellationTokenSource cts)
    {
        List<DetectedRegion> detected;
        try
        {
            detected = await Task.Run(() => AutoFilterDetector.Detect(image), cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            detected = new List<DetectedRegion>();
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_autoFilterCts, cts)) return;

        _autoFilterCts = null;
        cts.Dispose();

        var projected = AutoFilterLogic.Project(detected, bounds, pixelWidth, pixelHeight);
        _autoFilter.Detecting = false;
        _autoFilter.Set(bounds, projected);

        // 结果刚到：整批闪红 300ms（上游 m_flashUntil = arrived + 300）
        long now = Environment.TickCount64;
        _autoFilter.FlashUntil = projected.Count > 0 ? now + (long)AutoFilterState.FlashMs : 0;
        Surface.AutoFilterClock = now;
        Surface.InvalidateVisual();
        RefreshAutoFilterAvailability();
        StartAutoFilterTimer();
    }

    private void CancelAutoFilterDetection()
    {
        var cts = _autoFilterCts;
        _autoFilterCts = null;
        if (cts != null)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* 已经结束了 */ }
            cts.Dispose();
        }

        _autoFilter.Detecting = false;
        _autoFilterDetecting = Rect.Empty;
    }

    /// <summary>
    /// 选区内的**底图**（不含标注）→ BGR 图，坐标按底图自己的像素算。
    /// 检测坐标系与导出 / 找图同源：DIP × (物理 ÷ DIP) 换成底图像素。
    /// </summary>
    private BgrImg? CropBaseToBgr(Rect bounds, out int pixelWidth, out int pixelHeight)
    {
        pixelWidth = 0;
        pixelHeight = 0;

        var doc = _doc;
        double scale = doc.Scale > 0 ? doc.Scale : 1;

        int x = (int)Math.Round((bounds.X - doc.Origin.X) * scale);
        int y = (int)Math.Round((bounds.Y - doc.Origin.Y) * scale);
        int w = (int)Math.Round(bounds.Width * scale);
        int h = (int)Math.Round(bounds.Height * scale);
        if (w < 2 || h < 2) return null;

        x = Math.Clamp(x, 0, Math.Max(0, doc.Base.PixelWidth - 2));
        y = Math.Clamp(y, 0, Math.Max(0, doc.Base.PixelHeight - 2));
        w = Math.Min(w, doc.Base.PixelWidth - x);
        h = Math.Min(h, doc.Base.PixelHeight - y);
        if (w < 2 || h < 2) return null;

        BitmapSource crop = new CroppedBitmap(doc.Base, new Int32Rect(x, y, w, h));
        if (crop.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = crop;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            crop = converted;
        }

        int stride = w * 4;
        var bytes = new byte[stride * h];
        crop.CopyPixels(bytes, stride, 0);

        pixelWidth = w;
        pixelHeight = h;
        return BgrImg.FromBgra(bytes, w, h);
    }

    // ============ 悬停 / 手势 ============

    /// <summary>光标下的那一块（最小面积优先，同面积取 id 小的）点亮；光标移开就熄掉。</summary>
    private void UpdateAutoFilterHover(Point p)
    {
        int id = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;
        if (id == _autoFilter.HoverId) return;
        _autoFilter.HoverId = id;
        Surface.InvalidateVisual();
    }

    private bool BeginAutoFilterGesture(Point p)
    {
        _autoFilterStart = p;
        _autoFilter.GestureActive = true;
        _autoFilter.Marquee = null;
        _autoFilter.Hits.Clear();

        int id = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;
        _autoFilter.HoverId = id;
        if (id >= 0) _autoFilter.Hits.Add(id);

        _drag = DragMode.Draw;
        Surface.InvalidateVisual();
        return true;
    }

    private void UpdateAutoFilterGesture(Point p)
    {
        bool dragging = (p - _autoFilterStart).Length >= AutoFilterLogic.DragThreshold;

        Rect? marquee = dragging ? MarkGeometry.RectOf(_autoFilterStart, p) : null;
        _autoFilter.Marquee = marquee;
        _autoFilter.Hits.Clear();

        if (dragging && marquee is Rect box)
        {
            _autoFilter.Hits.AddRange(AutoFilterLogic.RegionsIn(_autoFilter.Regions, box));
        }
        else
        {
            int id = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;
            if (id >= 0) _autoFilter.Hits.Add(id);
        }

        Surface.InvalidateVisual();
    }

    private void FinishAutoFilterGesture(Point p)
    {
        UpdateAutoFilterGesture(p);

        var ids = _autoFilter.Hits.ToList();
        _autoFilter.GestureActive = false;
        _autoFilter.Marquee = null;
        _autoFilter.Hits.Clear();
        _autoFilter.HoverId = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;

        if (ids.Count == 0)
        {
            Surface.InvalidateVisual();
            return;
        }

        ApplyAutoFilterFill(ids, toggle: true);
    }

    /// <summary>手势被扔掉（Esc / 右键 / 换工具）：高亮与框选一起清掉，不动文档。</summary>
    private void CancelAutoFilterGesture()
    {
        _autoFilter.GestureActive = false;
        _autoFilter.Marquee = null;
        _autoFilter.Hits.Clear();
        Surface.InvalidateVisual();
    }

    // ============ 填充 ============

    /// <summary>「填充区域 → 某类型」：一次给这类所有区域打码（只补不删，上游 <c>toggle = false</c>）。</summary>
    private void OnAutoFilterFillRequested(string category)
    {
        if (_tool != CaptureTool.AutoFilter) return;
        if (_sel is not Rect bounds || !IsValid(bounds) || !_autoFilter.Matches(bounds)) return;

        var ids = _autoFilter.Regions
            .Where(r => r.Category == category)
            .Select(r => r.Id)
            .ToList();
        if (ids.Count == 0) return;

        ApplyAutoFilterFill(ids, toggle: false);
    }

    /// <summary>
    /// 一次填充事务：整批只占一个撤销位（<see cref="CaptureDocument.EditBatch"/>），
    /// 填充完按上游规则重排绘制顺序（智能擦除 → 自动填充按区域 id → 其余）。
    /// </summary>
    private void ApplyAutoFilterFill(IReadOnlyCollection<int> ids, bool toggle)
    {
        var regions = _autoFilter.Regions.ToList();      // 识别结果在事务里只读
        var kind = _style.FilterForAutoFill;
        double strength = _style.FilterStrength;

        _doc.EditBatch(list => AutoFilterLogic.Fill(list, regions, ids, kind, strength, toggle));
        RefreshMarks();
        Surface.InvalidateVisual();
    }

    // ============ 动画（扫描光带 / 闪红） ============

    private void StartAutoFilterTimer()
    {
        if (_autoFilterTick == null)
        {
            _autoFilterTick = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16),
            };
            _autoFilterTick.Tick += AutoFilterTick;
        }

        if (!_autoFilterTick.IsEnabled) _autoFilterTick.Start();
    }

    private void StopAutoFilterTimer()
    {
        if (_autoFilterTick is { IsEnabled: true }) _autoFilterTick.Stop();
    }

    private void AutoFilterTick(object? sender, EventArgs e)
    {
        if (_tool != CaptureTool.AutoFilter)
        {
            StopAutoFilterTimer();
            return;
        }

        long now = Environment.TickCount64;
        Surface.AutoFilterClock = now;
        Surface.InvalidateVisual();

        if (!_autoFilter.Detecting && !_autoFilter.Flashing(now)) StopAutoFilterTimer();
    }
}
