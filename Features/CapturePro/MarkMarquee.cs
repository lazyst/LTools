using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>
/// 框选标注（照上游 <c>Editor::marquee_candidate_elements</c> + <c>selection_ids_intersecting_bounds</c>）：
/// 与框**相交**即命中，不是「完全包住」。纯函数，方便自检直接断言。
/// </summary>
internal static class MarkMarquee
{
    /// <summary>拖出来的框（允许往左上 / 右上反向拖）。</summary>
    public static Rect RectOf(Point start, Point end) =>
        new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));

    /// <summary>
    /// 命中框的标注下标（按绘制顺序，小的在前）。
    /// <paramref name="baseSelection"/> 非空 = 加选（上游 <c>additive</c>：Shift 拖着框选时
    /// 框里的追加到原有选区上）；为空 = 只选框里的。
    /// </summary>
    public static List<int> Hits(IReadOnlyList<Mark> marks, Rect box, IReadOnlyList<int>? baseSelection = null)
    {
        var hits = new List<int>();
        if (baseSelection != null)
            foreach (int index in baseSelection)
                if (index >= 0 && index < marks.Count && !hits.Contains(index)) hits.Add(index);

        if (box.Width <= 0.5 && box.Height <= 0.5) return hits;

        for (int i = 0; i < marks.Count; i++)
        {
            if (hits.Contains(i)) continue;

            // 恢复块不可选中（它是橡皮留下的原图像素，不是标注），水印外接框为空
            if (marks[i] is Mark.Restore) continue;

            var frame = marks[i].Frame;
            if (frame.IsEmpty) continue;

            // 直箭头 / 直线这种「框高或框宽 = 0」的标注也要能框到：给它 1 DIP 的最小厚度，
            // 否则 Rect.IntersectsWith 对零面积永远返回 false，横着画一条箭头就选不中。
            if (frame.Width < 1 || frame.Height < 1)
                frame = new Rect(frame.X - 0.5, frame.Y - 0.5,
                    Math.Max(1, frame.Width), Math.Max(1, frame.Height));

            if (!frame.IntersectsWith(box)) continue;
            hits.Add(i);
        }

        return hits;
    }
}
