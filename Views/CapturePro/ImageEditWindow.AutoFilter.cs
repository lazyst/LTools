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
/// 图片编辑窗的自动打码（照上游 <c>ImageEditingWindow</c> 里的
/// <c>ScreenshotAutoFilterController</c>）：整张图识别一遍 → 悬停点亮、点一下 / 框选一批、
/// 「填充区域 → 某类型」一次打码。
/// </summary>
/// <remarks>
/// <para>
/// 与截图覆盖层共用同一套实现（<see cref="AutoFilterState"/> / <see cref="AutoFilterLogic"/> /
/// <see cref="AutoFilterDetector"/> / <see cref="CaptureCanvas.AutoFilter"/>），差别只有
/// **识别范围 = 整张图**（上游 <c>autoFilter</c> 的 bounds 就是 <c>document.bounds()</c>），
/// 而覆盖层是当前选区。编辑窗的文档单位就是图像像素，所以区域坐标直接落进画布坐标系。
/// </para>
/// <para>
/// 落的是普通 <see cref="Mark.Filter"/>，可撤销 / 可跟标注一起导出；检测失败只当空结果
/// （面板里「填充区域」保持灰的），不弹框、不假装打码。
/// </para>
/// </remarks>
public partial class ImageEditWindow
{
    private AutoFilterState _autoFilter = new();
    private CancellationTokenSource? _autoFilterCts;
    private DispatcherTimer? _autoFilterTick;
    private Rect _autoFilterDetecting;
    private Point _autoFilterStart;

    /// <summary>识别范围 = 整张图（图像像素 = 画布坐标）。</summary>
    private Rect AutoFilterBounds => _doc.HasImage ? new Rect(0, 0, _doc.Width, _doc.Height) : Rect.Empty;

    private bool AutoFilterActive => !_panMode && _cropAxis == null && _tool == CaptureTool.AutoFilter;

    // ============ 进入 / 退出 ============

    private void EnterAutoFilter()
    {
        Canvas.AutoFilter = _autoFilter;
        Canvas.AutoFillKind = CurrentStyle.FilterForAutoFill;
        Canvas.AutoFilterClock = Environment.TickCount64;
        SyncAutoFilter(detect: true);
    }

    private void LeaveAutoFilter()
    {
        CancelAutoFilterDetection();
        StopAutoFilterTimer();
        _autoFilter.Clear();
        Canvas.AutoFilter = null;
        Canvas.InvalidateVisual();
        Toolbar.SetAutoFilterState(false);
    }

    /// <summary>识别结果对不上现在的图（换图 / 裁剪 / 撤销）就作废，需要时重新识别。</summary>
    private void SyncAutoFilter(bool detect)
    {
        if (!AutoFilterActive)
        {
            Toolbar.SetAutoFilterState(false);
            return;
        }

        Canvas.AutoFilter = _autoFilter;
        Canvas.AutoFillKind = CurrentStyle.FilterForAutoFill;

        var bounds = AutoFilterBounds;
        if (bounds.Width < 2 || bounds.Height < 2)
        {
            CancelAutoFilterDetection();
            StopAutoFilterTimer();
            _autoFilter.Clear();
            Toolbar.SetAutoFilterState(false);
            Canvas.InvalidateVisual();
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
        else Canvas.InvalidateVisual();
    }

    // ============ 识别 ============

    private void StartAutoFilterDetection(Rect bounds)
    {
        CancelAutoFilterDetection();

        var image = BaseToBgr();
        if (image == null) return;

        var cts = new CancellationTokenSource();
        _autoFilterCts = cts;
        _autoFilterDetecting = bounds;
        _autoFilter.Detecting = true;
        _autoFilter.ScanStartMs = Environment.TickCount64;
        Canvas.AutoFilterClock = Environment.TickCount64;
        Canvas.InvalidateVisual();
        StartAutoFilterTimer();

        _ = DetectAsync(image, bounds, cts);
    }

    private async Task DetectAsync(BgrImg image, Rect bounds, CancellationTokenSource cts)
    {
        List<DetectedRegion> detected;
        try
        {
            detected = await Task.Run(() => AutoFilterDetector.Detect(image), cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            LTools.Core.AppLog.Warn("ImageEditWindow.AutoFilter", ex.Message);
            detected = new List<DetectedRegion>();
        }

        if (cts.IsCancellationRequested || !ReferenceEquals(_autoFilterCts, cts)) return;

        _autoFilterCts = null;
        cts.Dispose();

        var projected = AutoFilterLogic.Project(detected, bounds, (int)bounds.Width, (int)bounds.Height);
        _autoFilter.Detecting = false;
        _autoFilter.Set(bounds, projected);

        // 结果刚到：整批闪红 300ms（上游 m_flashUntil = arrived + 300）
        long now = Environment.TickCount64;
        _autoFilter.FlashUntil = projected.Count > 0 ? now + (long)AutoFilterState.FlashMs : 0;
        Canvas.AutoFilterClock = now;
        Canvas.InvalidateVisual();
        Toolbar.SetAutoFilterState(true);
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

    /// <summary>底图 → BGR 图（不带标注；上游同样拿 <c>document.image()</c> 做检测）。</summary>
    private BgrImg? BaseToBgr()
    {
        if (_doc.Image is not { } source || source.PixelWidth < 2 || source.PixelHeight < 2) return null;

        BitmapSource image = source;
        if (image.Format != PixelFormats.Bgra32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = image;
            converted.DestinationFormat = PixelFormats.Bgra32;
            converted.EndInit();
            converted.Freeze();
            image = converted;
        }

        int w = image.PixelWidth;
        int h = image.PixelHeight;
        int stride = w * 4;
        var bytes = new byte[stride * h];

        try
        {
            image.CopyPixels(bytes, stride, 0);
        }
        catch (Exception ex)
        {
            LTools.Core.AppLog.Warn("ImageEditWindow.AutoFilter.CopyPixels", ex.Message);
            return null;
        }

        return BgrImg.FromBgra(bytes, w, h);
    }

    // ============ 悬停 / 手势 ============

    /// <summary>光标下的那一块点亮（最小面积优先、同面积取 id 小的，同 <see cref="AutoFilterLogic.RegionAt"/>）。</summary>
    private void UpdateAutoFilterHover(Point p)
    {
        int id = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;
        if (id == _autoFilter.HoverId) return;
        _autoFilter.HoverId = id;
        Canvas.InvalidateVisual();
    }

    private void BeginAutoFilterGesture(Point p)
    {
        _autoFilterStart = p;
        _autoFilter.GestureActive = true;
        _autoFilter.Marquee = null;
        _autoFilter.Hits.Clear();

        int id = AutoFilterLogic.RegionAt(_autoFilter.Regions, p)?.Id ?? -1;
        _autoFilter.HoverId = id;
        if (id >= 0) _autoFilter.Hits.Add(id);

        Canvas.InvalidateVisual();
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

        Canvas.InvalidateVisual();
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
            Canvas.InvalidateVisual();
            return;
        }

        ApplyAutoFilterFill(ids, toggle: true);
    }

    /// <summary>手势被扔掉（Esc / 右键 / 换工具）：高亮与框选一起清掉，不动文档。</summary>
    private void CancelAutoFilterGesture()
    {
        if (!_autoFilter.GestureActive && _autoFilter.Marquee == null && _autoFilter.Hits.Count == 0) return;

        _autoFilter.GestureActive = false;
        _autoFilter.Marquee = null;
        _autoFilter.Hits.Clear();
        Canvas.InvalidateVisual();
    }

    // ============ 填充 ============

    /// <summary>「填充区域 → 某类型」：一次给这类所有区域打码（只补不删，上游 <c>toggle = false</c>）。</summary>
    private void OnAutoFilterFillRequested(string category)
    {
        if (!AutoFilterActive || !_autoFilter.Matches(AutoFilterBounds)) return;

        var ids = _autoFilter.Regions
            .Where(r => r.Category == category)
            .Select(r => r.Id)
            .ToList();
        if (ids.Count == 0) return;

        ApplyAutoFilterFill(ids, toggle: false);
    }

    /// <summary>一次填充事务：整批只占一个撤销位，填完按上游规则重排绘制顺序。</summary>
    private void ApplyAutoFilterFill(IReadOnlyCollection<int> ids, bool toggle)
    {
        if (_doc.Doc is not { } doc) return;

        var regions = _autoFilter.Regions.ToList();      // 识别结果在事务里只读
        var kind = CurrentStyle.FilterForAutoFill;
        double strength = CurrentStyle.FilterStrength;

        _doc.PushUndo();
        doc.EditBatch(list => AutoFilterLogic.Fill(list, regions, ids, kind, strength, toggle));
        RefreshMarks();
        Canvas.InvalidateVisual();
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
        if (!AutoFilterActive)
        {
            StopAutoFilterTimer();
            return;
        }

        long now = Environment.TickCount64;
        Canvas.AutoFilterClock = now;
        Canvas.InvalidateVisual();

        if (!_autoFilter.Detecting && !_autoFilter.Flashing(now)) StopAutoFilterTimer();
    }
}
