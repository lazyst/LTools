using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史翻页：<c>,</c> / <c>.</c> 把上一条 / 下一条采集到的图当底图放进来继续标 / 复制 / 贴图。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的「上一张 / 下一张」：**不弹历史窗口、不关覆盖层**，就地换底图。
/// 换进来时选区自动框满整张图（历史图与屏幕没有位置关系，所以摆在窗口中间），
/// 标注 / 撤销栈清空——那张图上的标注早就画好了，这里是从它出发再画新的。
/// </para>
/// <para>
/// <c>Esc</c> / 右键 = 回到刚才这次截图（<see cref="HistoryLive"/> 把底图、选区、标注、工具都存了一份）。
/// 翻页期间悬停探测与吸附一律关闭：底图已经不对应屏幕内容，再去命中窗口只会框错地方。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    /// <summary>进历史之前的那一份现场（退出时原样放回）。</summary>
    private sealed record HistoryLive(
        System.Windows.Media.Imaging.BitmapSource Base, Int32Rect Monitor, double Scale, Point Origin,
        Int32Rect Area, SelectionRegion[]? Regions, SelectionRegion[] RegionsDip, Mark[] Marks,
        Rect? Selection, CaptureTool Tool, int[] Selected);

    private HistoryLive? _live;
    private List<CaptureEntry> _history = new();
    private int _historyIndex = -1;

    /// <summary>正在翻采集历史（底图已经不是刚才截的那张）。</summary>
    private bool BrowsingHistory => _historyIndex >= 0 && _historyIndex < _history.Count;

    /// <summary>
    /// <paramref name="delta"/> = +1 更早、-1 更新。返回 true = 这次按键归翻页处理（调用方不再往下走）。
    /// </summary>
    private bool StepHistory(int delta)
    {
        if (_history.Count == 0)
        {
            _history = CaptureCatalog.Load()
                .Where(e => !e.IsRecord && File.Exists(CaptureCatalog.AssetPath(e.Id)))
                .ToList();

            if (_history.Count == 0)
            {
                ToastService.Show("还没有采集历史：截几张图之后再按 , / . 翻页", "采集历史",
                    2200, ToastKind.info, key: "capture-history-empty");
                return true;
            }
        }

        if (_live == null) EnterHistory();

        int next = _historyIndex < 0 ? 0 : Math.Clamp(_historyIndex + delta, 0, _history.Count - 1);
        if (next == _historyIndex)
        {
            // 到头了：说清楚是「没有更早的了」而不是按键没反应
            ToastService.Show(delta > 0 ? "这是最早的一张采集记录了" : "这是最新的一张采集记录了", null,
                1600, ToastKind.info, key: "capture-history-edge");
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

    /// <summary>第一次翻页：把实时截图这一份现场存下来（Esc 回来时恢复）。</summary>
    private void EnterHistory()
    {
        _live = new HistoryLive(_doc.Base, _doc.Monitor, _doc.Scale, _doc.Origin, _doc.Area,
            _doc.Regions, _regions.ToArray(), _doc.Marks.ToArray(), _sel, _tool, _selected.ToArray());

        SetHover(null);
        _hoverCandidates.Clear();
        CancelGesture();
    }

    /// <summary>换到第 <paramref name="index"/> 条历史图；文件没了就跳过它。</summary>
    private void ShowHistoryEntry(int index)
    {
        if (_history.Count == 0) { ExitHistory(); return; }
        index = Math.Clamp(index, 0, _history.Count - 1);

        var entry = _history[index];
        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id), 0);
        if (image == null)
        {
            // 图被删了：摘掉它继续翻，别停在一条打不开的记录上
            AppLog.Warn("CaptureOverlay.History", "读不到采集图：" + entry.Id);
            _history.RemoveAt(index);
            if (_history.Count == 0) { ExitHistory(); return; }
            ShowHistoryEntry(Math.Clamp(index, 0, _history.Count - 1));
            return;
        }

        _historyIndex = index;

        // 历史图与屏幕没有位置关系：摆在窗口中间，选区框满整张
        double scale = _doc.Scale > 0 ? _doc.Scale : 1;
        double w = image.PixelWidth / scale;
        double h = image.PixelHeight / scale;
        var origin = new Point(Math.Max(0, (ActualWidth - w) / 2), Math.Max(0, (ActualHeight - h) / 2));

        _doc.LoadBase(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight), scale, origin);
        _doc.Area = new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight);
        _doc.Regions = null;             // 历史图是矩形整张：多块 / 点列对不上别的图
        SetSingleRect(new Rect(origin.X, origin.Y, w, h));

        _tool = CaptureTool.Select;
        Toolbar.Sync(_tool);
        SelectMark(-1);
        RefreshMarks();
        UpdateCursorForTool();
    }

    /// <summary>回到刚才这次截图（底图 / 选区 / 标注 / 工具一起恢复）。</summary>
    private void ExitHistory()
    {
        if (_live is not { } live) return;

        _live = null;
        _historyIndex = -1;
        _history.Clear();      // 下次再按 , / . 重新读一遍（这段时间可能又截了新图）

        _doc.LoadBase(live.Base, live.Monitor, live.Scale, live.Origin);
        _doc.Area = live.Area;
        _doc.Regions = live.Regions;     // LoadBase 会清掉，这里原样放回
        _regions.Clear();
        _regions.AddRange(live.RegionsDip);
        _originRegions = null;
        _liveRect = null;
        _replaceOnCommit = false;
        _lasso.Clear();
        _doc.ResetMarks(live.Marks);
        _sel = live.Selection;
        _tool = live.Tool;
        Toolbar.Sync(_tool);

        SelectMark(-1);
        RefreshMarks();
        if (live.Selected.Length > 0) SelectMarks(live.Selected, live.Selected[^1]);
        UpdateCursorForTool();

        if (Win32.GetCursorPos(out var cursor) && TryToDip(cursor, out var dip))
            UpdateHover(Clamp(dip));
    }

    /// <summary>顶部提示里的「历史 3/87 · 14:22 · 截图 · 800 × 600」那句。</summary>
    private string HistoryHint()
    {
        if (!BrowsingHistory) return "";
        var entry = _history[_historyIndex];
        return $"历史 {_historyIndex + 1}/{_history.Count} · {entry.TimeText} · {entry.KindLabel} · {entry.SizeText} px" +
               " · , / . 翻页 · Esc 回到刚才的截图";
    }
}
