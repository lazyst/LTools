using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史翻页：<c>,</c> 更早 / <c>.</c> 更新，就地换回「那次截图的整块屏幕 + 选区 + 标注」。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>previous_screenshot_history</c> / <c>next_screenshot_history</c>
/// （<c>ScreenshotHistoryService::navigatePrevious/Next</c>）：**不弹历史窗口、不关覆盖层**，
/// 就地换底图。位置 0 是**刚才这次截图**（上游的 live endpoint），所以 <c>.</c> 一路按回来会回到
/// 自己正在截的这一张，<c>,</c> 一路按到头是最早那条记录；两头都停住，**不循环**。
/// </para>
/// <para>
/// 换进来的不是成品图，而是**当时那块屏幕的冻结画面**：选区、选区外观、（含版面压缩）与画在上面的
/// 标注（连撤销 / 重做栈）都照原样摆回去——上游 <c>applyEntry</c> 恢复的就是 displays + selection +
/// canvasHistory。所以翻历史 = 换一条记录接着编辑，最终成品按这条记录**重绘**。
/// </para>
/// <para>
/// <c>Esc</c> 照旧是「退出这次截图」（上游 <c>cancel_screenshot</c>）；右键 / 单击提示里的「回到刚才的
/// 截图」= 回到 live（上游 <c>handleRightClick</c> 先 <c>returnToCurrentScreenshot</c>）。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>进历史之前的那一份现场（回到 live 时原样放回）。</summary>
    private sealed record HistoryLive(
        BitmapSource Base, Int32Rect Monitor, double Scale, Point Origin, Int32Rect Area,
        SelectionStyle Style, bool CompactLayout, int CompactSpacing,
        SelectionRegion[]? Regions, SelectionRegion[] RegionsDip, Mark[] Marks,
        Mark[][] Undo, Mark[][] Redo, Rect? Selection, CaptureTool Tool, int[] Selected);

    private HistoryLive? _live;
    private List<CaptureEntry> _history = new();

    /// <summary>-1 = 正在截的这一张（live）；0 起 = 历史记录，0 是「比现在早一条」。</summary>
    private int _historyIndex = -1;

    /// <summary>正在翻采集历史（底图已经不是刚才截的那张）。</summary>
    internal bool BrowsingHistory => _historyIndex >= 0 && _historyIndex < _history.Count;

    /// <summary>
    /// <paramref name="delta"/> = <c>+1</c> 更早（<c>,</c>）、<c>-1</c> 更新（<c>.</c>）。
    /// 返回 true = 这次按键归翻页处理（调用方不再往下走）。
    /// </summary>
    private bool StepHistory(int delta)
    {
        if (EditOnly || _scrollMode || _textEditing) return false;

        // 正在改框 / 拖标注：这次按键不归翻页（上游 canActivate 也挡这一档：!modifyingSelection()）
        if (_drag is DragMode.Move or DragMode.MoveRegion or DragMode.Resize or DragMode.Mark)
            return false;

        if (_history.Count == 0) LoadHistory();

        if (_history.Count == 0)
        {
            ToastService.Show("还没有可翻的采集历史：截几张图之后再按 , / . 翻页", "采集历史",
                2400, ToastKind.info, key: "capture-history-empty");
            return true;
        }

        // 第一次翻页：把这次截图的现场存下来（回来时原样放回）
        if (_live == null) EnterHistory();

        // live 再往回（更新）= 已经最新了，什么都不做（上游 navigateNext 在 index 0 直接返回 false）
        int next = _historyIndex < 0 ? (delta > 0 ? 0 : -1) : _historyIndex + delta;

        if (next < 0)
        {
            if (BrowsingHistory) ExitHistory();
            return true;
        }

        if (next >= _history.Count)
        {
            // 最早一张了：说清楚是「没有更早的了」，而不是按键没反应
            ToastService.Show("这是最早的一张采集记录了", null, 1600, ToastKind.info,
                key: "capture-history-edge");
            return true;
        }

        ShowHistoryEntry(next);
        return true;
    }

    /// <summary>
    /// 「更多 → 采集历史窗口…」：先关覆盖层再开窗。覆盖层是 Topmost 全屏遮罩，
    /// 开着它历史窗会被整块盖住；排在 Background 是等 <see cref="Cancel"/> 的关闭走完。
    /// 这一次不算截图（不产生成品、也不记采集历史）。
    /// </summary>
    private void OpenHistoryWindow()
    {
        Cancel();
        Dispatcher.BeginInvoke(new Action(CaptureHistoryWindow.Toggle),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 只收「能还原成编辑现场」的记录：有整屏底图、底图还在。
    /// 直接截图（当前显示器 / 前台窗口）没有整屏底图，翻进去只剩一块空白，所以跳过。
    /// </summary>
    private void LoadHistory()
    {
        _history = CaptureCatalog.Load()
            .Where(e => !e.IsRecord && e.HasDisplay && File.Exists(CaptureCatalog.DisplayFilePath(e)))
            .ToList();
    }

    /// <summary>
    /// 第一次翻页：把实时截图这一份现场存下来（回到 live 时恢复）。
    /// 正在拖的新选区先**落定**（上游 <c>ensureLiveEndpoint</c> 里的 <c>confirmSelection</c>），
    /// 否则按 <c>,</c> 会把刚拖出来的那块悄悄丢掉。
    /// </summary>
    private void EnterHistory()
    {
        ConfirmSelectingForHistory();

        _live = new HistoryLive(_doc.Base, _doc.Monitor, _doc.Scale, _doc.Origin, _doc.Area,
            _doc.Style, _doc.CompactLayout, _doc.CompactSpacing,
            _doc.Regions, _regions.ToArray(), _doc.Marks.ToArray(),
            _doc.UndoStack.ToArray(), _doc.RedoStack.ToArray(),
            _sel, _tool, _selected.ToArray());

        SetHover(null);
        _hoverCandidates.Clear();
        CancelGesture();
    }

    /// <summary>拖动中的新选区 / 套索：按松手那一刻的口径落定（不落就当作没拖）。</summary>
    private void ConfirmSelectingForHistory()
    {
        if (_drag == DragMode.New)
        {
            _drag = DragMode.None;
            _handle = FrameHandle.None;
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }

            var box = _liveRect ?? Rect.Empty;
            _liveRect = null;
            if (!CommitBox(box)) RefreshSelection();
            FinalizeAspect();
        }
        else if (_drag == DragMode.Lasso)
        {
            _drag = DragMode.None;
            try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
            CommitLasso();
        }
    }

    /// <summary>换到第 <paramref name="index"/> 条历史记录；底图没了就跳过它。</summary>
    private void ShowHistoryEntry(int index)
    {
        if (_history.Count == 0) { ExitHistory(); return; }
        index = Math.Clamp(index, 0, _history.Count - 1);

        var entry = _history[index];
        var display = CaptureCatalog.LoadDisplay(entry);
        if (display == null)
        {
            // 图被删了：摘掉它继续翻，别停在一条打不开的记录上
            AppLog.Warn("CaptureOverlay.History", "读不到整屏底图：" + entry.Id);
            _history.RemoveAt(index);
            if (_history.Count == 0) { ExitHistory(); return; }
            ShowHistoryEntry(Math.Clamp(index, 0, _history.Count - 1));
            return;
        }

        _historyIndex = index;

        // 那次截图的整块屏幕：窗口盖住的还是这次那块虚拟桌面，所以按记录的物理矩形摆回去
        // （历史图与屏幕**有**位置关系——它本来就是屏幕上的一块，别居中摆放）
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        var window = _doc.Monitor;
        var origin = new Point((entry.DisplayX - window.X) / scale, (entry.DisplayY - window.Y) / scale);

        _doc.LoadBase(display, entry.DisplayRect, scale, origin);

        // 选区外观（含版面压缩）：记录没存就沿用这次的外观
        LoadCaptureStyle(entry.Style ?? CaptureStyleDto.Of(_selStyle, _doc.CompactLayout,
            _doc.CompactSpacing));

        // 标注 + 撤销 / 重做栈（上游同样恢复 canvasHistory，所以翻进去还能 Ctrl+Z）
        _doc.ImportHistory(MarkCodec.Read(entry.Annotations), MarkCodec.ReadHistory(entry.UndoMarks),
            MarkCodec.ReadHistory(entry.RedoMarks));

        // 选区：每块（多块 / 自由 / 折线）或外接框那一条
        var regions = CaptureRegionDto.RegionsOf(entry, scale);
        if (regions.Count == 0)     // 老记录连外接框都没有：框满这张屏幕
            regions.Add(SelectionRegion.FromRect(RegionMode.Add,
                new Rect(origin, new Size(display.PixelWidth / scale, display.PixelHeight / scale))));
        LoadSelection(regions);
        SyncArea();

        // 上游 setMoveTool：换进来先落在「选择」工具上
        _tool = CaptureTool.Select;
        Toolbar.Sync(_tool);
        SelectMark(-1);
        RefreshMarks();
        UpdateCursorForTool();
        UpdateChrome();
    }

    /// <summary>回到刚才这次截图（底图 / 选区 / 外观 / 标注 / 撤销栈 / 工具一起恢复）。</summary>
    private void ExitHistory()
    {
        if (_live is not { } live) return;

        _live = null;
        _historyIndex = -1;
        _history.Clear();      // 下次再按 , / . 重新读一遍（这段时间可能又截了新图）

        _doc.LoadBase(live.Base, live.Monitor, live.Scale, live.Origin);
        _doc.Area = live.Area;
        _doc.Regions = live.Regions;     // LoadBase 会清掉，这里原样放回
        _doc.Style = live.Style;
        _doc.CompactLayout = live.CompactLayout;
        _doc.CompactSpacing = live.CompactSpacing;
        _selStyle = live.Style;
        _regions.Clear();
        _regions.AddRange(live.RegionsDip);
        _originRegions = null;
        _liveRect = null;
        _replaceOnCommit = false;
        _lasso.Clear();
        _doc.ImportHistory(live.Marks, live.Undo, live.Redo);
        _sel = live.Selection;
        _tool = live.Tool;
        Toolbar.Sync(_tool);

        SelectMark(-1);
        RefreshMarks();
        if (live.Selected.Length > 0) SelectMarks(live.Selected, live.Selected[^1]);
        UpdateCursorForTool();
        UpdateChrome();

        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
            UpdateHover(Clamp(dip));
    }

    /// <summary>顶部提示里的「历史 3/87 · 14:22 · 截图 · 800 × 600」那句。</summary>
    private string HistoryHint()
    {
        if (!BrowsingHistory) return "";
        var entry = _history[_historyIndex];
        return $"历史 {_historyIndex + 1}/{_history.Count} · {entry.TimeText} · {entry.KindLabel} · {entry.SizeText} px" +
               " · , 更早 · . 更新 · 右键回到刚才的截图";
    }
}
