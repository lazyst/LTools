using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>CaptureOverlayWindow 的「框选标注（Shift + 空白拖动）」部分。</summary>
public partial class CaptureOverlayWindow
{
    // ============ 框选标注（Shift + 空白拖动，上游 MarqueeSelection） ============

    /// <summary>开始框选：记下原有选区（Shift 拖着框选 = 加选），清掉画面上上一次的框。</summary>
    private void BeginMarkMarquee(Point p)
    {
        _marqueeBase = _selected.ToArray();
        _marqueeStart = p;
        _drag = DragMode.Marquee;
        Surface.MarkMarquee = Rect.Empty;
        Surface.MarqueeHits = Array.Empty<int>();
        Surface.InvalidateVisual();
    }

    /// <summary>拖动中：实时把框里（相交即命中）的标注描出候选轮廓，松手才真的选中。</summary>
    private void UpdateMarkMarquee(Point p)
    {
        Rect box = MarkMarquee.RectOf(_marqueeStart, p);
        Surface.MarkMarquee = box;
        Surface.MarqueeHits = MarkMarquee.Hits(_doc.Marks, box, _marqueeBase);
        Surface.InvalidateVisual();
    }

    /// <summary>松手提交：框里的接上原有选区（加选），主选中项取最后命中的那条。</summary>
    private void CommitMarkMarquee(Point p)
    {
        Rect box = MarkMarquee.RectOf(_marqueeStart, p);
        var hits = MarkMarquee.Hits(_doc.Marks, box, _marqueeBase);

        Surface.MarkMarquee = Rect.Empty;
        Surface.MarqueeHits = Array.Empty<int>();
        _marqueeBase = null;

        if (hits.Count > 0) SelectMarks(hits, hits[^1]);
        else SelectMark(-1);
    }

    private void CancelMarkMarquee()
    {
        Surface.MarkMarquee = Rect.Empty;
        Surface.MarqueeHits = Array.Empty<int>();
        _marqueeBase = null;
        Surface.InvalidateVisual();
    }

}
