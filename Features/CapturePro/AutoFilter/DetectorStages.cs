using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>掩码运算（上游 <c>mask.rs</c>）。</summary>
internal static class MaskOps
{
    public static MaskGrid Or(MaskGrid a, MaskGrid b)
    {
        var output = a.Clone();
        for (int i = 0; i < output.Data.Length; i++) output.Data[i] |= b.Data[i];
        return output;
    }

    public static MaskGrid Suppress(MaskGrid a, MaskGrid b)
    {
        var output = a.Clone();
        for (int i = 0; i < output.Data.Length; i++)
            if (b.Data[i] > 0)
                output.Data[i] = 0;
        return output;
    }

    public static MaskGrid Rasterize(IEnumerable<RRect> rects, int width, int height)
    {
        var output = new MaskGrid(width, height);
        foreach (var rect in rects)
        {
            int y0 = Math.Max(rect.Y, 0);
            int y1 = Math.Min(rect.Y2, height);
            int x0 = Math.Max(rect.X, 0);
            int x1 = Math.Min(rect.X2, width);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    output.Set(x, y, 255);
        }

        return output;
    }

    public static RRect? Shrink(RRect rect, MaskGrid mask, int pad)
    {
        int x0 = mask.W, y0 = mask.H, x1 = -1, y1 = -1;
        int startY = Math.Max(rect.Y, 0);
        int endY = Math.Min(rect.Y2, mask.H);
        int startX = Math.Max(rect.X, 0);
        int endX = Math.Min(rect.X2, mask.W);
        for (int y = startY; y < endY; y++)
            for (int x = startX; x < endX; x++)
            {
                if (mask.At(x, y) == 0) continue;
                x0 = Math.Min(x0, x);
                y0 = Math.Min(y0, y);
                x1 = Math.Max(x1, x);
                y1 = Math.Max(y1, y);
            }

        if (x1 < 0) return null;
        return Geo.Clip(new RRect(x0 - pad, y0 - pad, x1 - x0 + 1 + 2 * pad, y1 - y0 + 1 + 2 * pad), mask.W, mask.H);
    }

    /// <summary>填掉被前景包住的空洞（与上游 <c>fill_holes</c> 的补边洪水填充一致）。</summary>
    public static MaskGrid FillHoles(MaskGrid mask)
    {
        int width = mask.W + 2;
        int height = mask.H + 2;
        var outside = new bool[width * height];
        var queue = new Queue<(int X, int Y)>();
        outside[0] = true;
        queue.Enqueue((0, 0));
        int[] dxs = { -1, 1, 0, 0 };
        int[] dys = { 0, 0, -1, 1 };
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            for (int d = 0; d < 4; d++)
            {
                int nx = x + dxs[d];
                int ny = y + dys[d];
                if (nx < 0 || nx >= width || ny < 0 || ny >= height || outside[ny * width + nx]) continue;
                bool isForeground = nx > 0 && nx <= mask.W && ny > 0 && ny <= mask.H
                                    && mask.At(nx - 1, ny - 1) > 0;
                if (isForeground) continue;
                outside[ny * width + nx] = true;
                queue.Enqueue((nx, ny));
            }
        }

        var output = mask.Clone();
        for (int y = 0; y < mask.H; y++)
            for (int x = 0; x < mask.W; x++)
            {
                if (mask.At(x, y) > 0 || !outside[(y + 1) * width + x + 1]) output.Set(x, y, 255);
            }

        return output;
    }

    /// <summary>按纵向空白把一行文字切成几段（上游 <c>split_gaps</c>）。</summary>
    public static List<RRect> SplitGaps(RRect rect, MaskGrid ink, double gapFraction)
    {
        int x0 = Math.Max(rect.X, 0);
        int y0 = Math.Max(rect.Y, 0);
        int x1 = Math.Min(rect.X2, ink.W);
        int y1 = Math.Min(rect.Y2, ink.H);
        if (x1 <= x0 || y1 <= y0) return new List<RRect> { rect };

        var columns = new bool[x1 - x0];
        for (int x = x0; x < x1; x++)
        {
            bool any = false;
            for (int y = y0; y < y1; y++)
            {
                if (ink.At(x, y) == 0) continue;
                any = true;
                break;
            }

            columns[x - x0] = any;
        }

        var runs = new List<(int A, int B)>();
        int i = 0;
        while (i < columns.Length)
        {
            if (columns[i])
            {
                int j = i;
                while (j < columns.Length && columns[j]) j++;
                runs.Add((i, j));
                i = j;
            }
            else
            {
                i += 1;
            }
        }

        if (runs.Count <= 1) return new List<RRect> { rect };
        int gap = Math.Max(5, (int)Math.Round(rect.H * gapFraction, MidpointRounding.ToEven));
        var groups = new List<(int A, int B)> { runs[0] };
        for (int r = 1; r < runs.Count; r++)
        {
            var (a, b) = runs[r];
            var last = groups[^1];
            if (a - last.B >= gap) groups.Add((a, b));
            else groups[^1] = (last.A, b);
        }

        if (groups.Count == 1) return new List<RRect> { rect };
        return groups.Select(g => new RRect(x0 + g.A, rect.Y, g.B - g.A, rect.H)).ToList();
    }
}

/// <summary>矩形合并 / 抑制 / 排序（上游 <c>selection.rs</c>）。</summary>
internal static class Selection
{
    public static List<RRect> MergeBy(List<RRect> items, Func<RRect, RRect, bool> predicate)
    {
        while (true)
        {
            bool changed = false;
            var output = new List<RRect>();
            var used = new bool[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                if (used[i]) continue;
                var current = items[i];
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (used[j] || !predicate(current, items[j])) continue;
                    current = Geo.Union(current, items[j]);
                    used[j] = true;
                    changed = true;
                }

                used[i] = true;
                output.Add(current);
            }

            items = output;
            if (!changed) return items;
        }
    }

    public static List<RRect> Merge(List<RRect> items, double threshold) =>
        MergeBy(items, (a, b) => Geo.IoU(a, b) >= threshold || Geo.Containment(b, a) > 0.7);

    /// <summary>同一片区域只留一个：面积最小的优先，重叠时保留更小的那个。</summary>
    public static List<RegionBox> Nms(List<RegionBox> items, double threshold)
    {
        var sorted = items.OrderBy(r => r.Rect.Area).ToList();
        var keep = new List<RegionBox>();
        foreach (var r in sorted)
        {
            int index = keep.FindIndex(k => Geo.IoU(r.Rect, k.Rect) >= threshold);
            if (index < 0)
            {
                keep.Add(r);
                continue;
            }

            if (r.Rect.Area <= keep[index].Rect.Area) keep[index] = r;
        }

        return keep;
    }

    public static List<RegionBox> SortRegions(List<RegionBox> items) => items
        .OrderByDescending(r => Math.Max(r.Rect.W, r.Rect.H))
        .ThenByDescending(r => r.Rect.Area)
        .ThenBy(r => r.Rect.Y)
        .ThenBy(r => r.Rect.X)
        .ToList();
}

/// <summary>一个检测结果（对应上游 <c>Region</c>：矩形 + 类型）。</summary>
internal readonly record struct RegionBox(RRect Rect, string Kind);

/// <summary>
/// 检测阶段：版面 / 文字行 / 图片 / 徽章 / 图标 / 纹理，
/// 逐行照抄上游 <c>surfaces.rs</c> <c>text.rs</c> <c>media.rs</c> <c>icons.rs</c>。
/// </summary>
internal static class Stages
{
    private static byte Truncate(double value) =>
        (byte)Math.Clamp(Math.Truncate(value), 0, 255);

    /// <summary>扁平色块估计：返回「局部背景」图、候选色块、边缘掩码。</summary>
    public static (BgrImg Bg, List<RRect> Surfaces, MaskGrid Border) EstimateSurfaces(BgrImg bgr)
    {
        int w = bgr.W;
        int h = bgr.H;
        var keys = new ushort[w * h];
        var counts = new int[32768];
        for (int i = 0, p = 0; i < keys.Length; i++, p += 3)
        {
            int key = ColorStats.QuantKey(bgr.Data[p], bgr.Data[p + 1], bgr.Data[p + 2], 8);
            keys[i] = (ushort)key;
            counts[key]++;
        }

        // 与上游逐 key 全图 CC 等价：一次 8 连通洪水填充拿到所有 key 的连通域，
        // 再按 (key 升序, 光栅序) 还原上游的候选顺序。
        var labels = new int[w * h];
        var components = new List<(int Key, RRect Rect, int Area, int Id)>();
        var stack = new int[w * h];
        int nextId = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int start = y * w + x;
                if (labels[start] != 0) continue;
                int key = keys[start];
                nextId++;
                int x0 = x, y0 = y, x1 = x, y1 = y, area = 0;
                int top = 0;
                stack[top++] = start;
                labels[start] = nextId;
                while (top > 0)
                {
                    int index = stack[--top];
                    int cy = index / w;
                    int cx = index - cy * w;
                    area++;
                    if (cx < x0) x0 = cx;
                    if (cx > x1) x1 = cx;
                    if (cy < y0) y0 = cy;
                    if (cy > y1) y1 = cy;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = cy + dy;
                        if (ny < 0 || ny >= h) continue;
                        int row = ny * w;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx;
                            if (nx < 0 || nx >= w) continue;
                            int neighbor = row + nx;
                            if (labels[neighbor] != 0 || keys[neighbor] != key) continue;
                            labels[neighbor] = nextId;
                            stack[top++] = neighbor;
                        }
                    }
                }

                components.Add((key, new RRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), area, nextId));
            }

        int minCount = Math.Max(200, w * h / 10000);
        var candidates = new List<(RRect Rect, MaskGrid Mask, double[] Color)>();
        foreach (var component in components
                     .Where(c => counts[c.Key] >= minCount)
                     .OrderBy(c => c.Key)
                     .ThenBy(c => c.Id))
        {
            var r = component.Rect;
            if (component.Area < 200 || r.W < 10 || r.H < 10 || component.Area < 0.3 * r.Area) continue;

            var cropped = new MaskGrid(r.W, r.H);
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    if (labels[(r.Y + y) * w + r.X + x] == component.Id) cropped.Set(x, y, 255);
                }

            var filled = MaskOps.FillHoles(cropped);
            int filledCount = 0;
            foreach (byte value in filled.Data)
                if (value > 0)
                    filledCount++;
            if (filledCount < 0.65 * r.Area) continue;

            var pixels = new List<(byte B, byte G, byte R)>();
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    if (cropped.At(x, y) == 0) continue;
                    pixels.Add(bgr.At(r.X + x, r.Y + y));
                }

            candidates.Add((r, filled, ColorStats.MedianColor(pixels)));
        }

        var bg = Morphology.MedianBlurImage(bgr, 31);
        var border = new MaskGrid(w, h);
        var surfaces = new List<RRect>();
        foreach (var (r, mask, color) in candidates.OrderByDescending(c => c.Rect.Area))
        {
            var padded = new MaskGrid(r.W + 4, r.H + 4);
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    byte m = mask.At(x, y);
                    padded.Set(x + 2, y + 2, m);
                    if (m > 0) bg.Set(r.X + x, r.Y + y, Truncate(color[0]), Truncate(color[1]), Truncate(color[2]));
                }

            var edge = Morphology.Gradient(padded, 5, 5);
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    int index = (r.Y + y) * w + r.X + x;
                    border.Data[index] |= edge.At(x + 2, y + 2);
                }

            if (r.W < 0.9 * w && r.H < 0.5 * h) surfaces.Add(r);
        }

        return (bg, surfaces, border);
    }

    /// <summary>文字行（这里顺带产出 ink 掩码与估计的字高）。</summary>
    public static (List<RRect> Texts, MaskGrid Ink, int CharHeight) TextLines(BgrImg bgr, BgrImg bg, MaskGrid sup)
    {
        int w = bgr.W;
        int h = bgr.H;
        var bw = MaskOps.Suppress(ColorStats.LocalForeground(bgr, bg, 20), sup);
        var hline = Morphology.Open(bw, Math.Max(80, w / 15), 1);
        var vline = Morphology.Open(bw, 1, Math.Max(80, h / 15));
        bw = MaskOps.Suppress(bw, MaskOps.Or(hline, vline));

        var (_, stats) = Morphology.ConnectedComponents(bw);
        var heights = stats
            .Where(c => c.Rect.H >= 8
                        && c.Rect.H <= (int)(0.06 * h)
                        && c.Rect.W >= 2
                        && c.Rect.W <= (int)(0.5 * w)
                        && c.Area >= 8)
            .Select(c => c.Rect.H)
            .OrderBy(v => v)
            .ToList();

        int charHeight;
        if (heights.Count == 0)
        {
            charHeight = Math.Max(16, (int)(0.022 * h));
        }
        else
        {
            double pos = (heights.Count - 1) * 0.7;
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            charHeight = (int)Math.Round(heights[lo] + (heights[hi] - heights[lo]) * (pos - lo),
                MidpointRounding.ToEven);
        }

        int maxHeight = Math.Max(96, 3 * charHeight);
        int badgeSide = Math.Max(36, (int)(0.024 * h));
        int kx = Math.Max(5, (int)Math.Round(0.85 * charHeight, MidpointRounding.ToEven));
        kx = Math.Min(kx, Math.Max(22, (int)(0.018 * w)));
        int ky = Math.Max(1, (int)Math.Round(0.14 * charHeight, MidpointRounding.ToEven));
        var closed = Morphology.Close(bw, kx, ky);

        var (_, closedStats) = Morphology.ConnectedComponents(closed);
        var words = new List<RRect>();
        foreach (var c in closedStats)
        {
            var r = c.Rect;
            double fill = (double)c.Area / r.Area;
            double aspect = (double)r.W / r.H;
            if (r.H < 8
                || r.H > maxHeight
                || r.W < 6
                || fill < 0.10
                || (Math.Min(r.W, r.H) >= badgeSide && aspect is >= 0.65 and <= 1.45 && fill >= 0.48))
            {
                continue;
            }

            words.Add(r);
        }

        var lines = Selection.MergeBy(words, (a, b) =>
        {
            if (Math.Min(a.Y2, b.Y2) - Math.Max(a.Y, b.Y) < 0.5 * Math.Min(a.H, b.H)) return false;
            int gap = Math.Max(a.X, b.X) - Math.Min(a.X2, b.X2);
            return gap < 0
                   || gap <= (Math.Min(a.H, b.H) >= 26 ? 1.15 : 0.82) * Math.Min(a.H, b.H);
        });

        int pad = Math.Max(3, (int)Math.Round(charHeight * 0.12, MidpointRounding.ToEven));
        var output = new List<RRect>();
        foreach (var line in lines)
            foreach (var r in MaskOps.SplitGaps(line, bw, 1.05))
            {
                var t = MaskOps.Shrink(r, bw, pad);
                if (t == null) continue;
                var value = t.Value;
                if (value.H >= 8 && value.W >= 6 && !(value.W < 10 && value.W * 2 <= value.H)) output.Add(value);
            }

        return (output, bw, charHeight);
    }

    /// <summary>连续 3 行以上、左对齐、字号相近的文本合成段落。</summary>
    public static List<RRect> Paragraphs(List<RRect> texts)
    {
        var items = texts.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        var groups = new List<List<RRect>>();
        foreach (var r in items)
        {
            int found = groups.FindIndex(g =>
            {
                var last = g[^1];
                double mh = Math.Min(last.H, r.H);
                double ratio = (double)last.H / r.H;
                return Math.Abs(last.X - r.X) < 0.35 * mh
                       && r.Y - last.Y2 > 0
                       && r.Y - last.Y2 < 0.8 * mh
                       && ratio > 0.85
                       && ratio < 1.18
                       && Math.Min(last.W, r.W) > 8 * r.H;
            });
            if (found >= 0) groups[found].Add(r);
            else groups.Add(new List<RRect> { r });
        }

        return groups
            .Where(g => g.Count >= 3)
            .Select(g => g.Aggregate(Geo.Union))
            .ToList();
    }

    /// <summary>文字行跨过竖直色块边缘时按色块边界切开。</summary>
    public static List<RRect> SplitSurfaceText(List<RRect> texts, List<RRect> surfaces, MaskGrid ink)
    {
        var output = new List<RRect>();
        foreach (var r in texts)
        {
            var cuts = new List<int> { r.X, r.X2 };
            foreach (var s in surfaces)
            {
                if (s.W < 0.7 * r.W
                    && s.H > 0.7 * r.H
                    && s.H < 2 * r.H
                    && Math.Min(r.Y2, s.Y2) - Math.Max(r.Y, s.Y) > 0.7 * r.H)
                {
                    if (r.X < s.X && s.X < r.X2) cuts.Add(s.X);
                    if (r.X < s.X2 && s.X2 < r.X2) cuts.Add(s.X2);
                }
            }

            cuts.Sort();
            var unique = cuts.Distinct().ToList();
            for (int i = 0; i + 1 < unique.Count; i++)
            {
                var t = MaskOps.Shrink(new RRect(unique[i], r.Y, unique[i + 1] - unique[i], r.H), ink, 3);
                if (t != null) output.Add(t.Value);
            }
        }

        return output;
    }

    /// <summary>一行里字体 / 颜色明显不同的两段切开。</summary>
    public static List<RRect> SplitStyledLines(List<RRect> texts, BgrImg bgr, MaskGrid ink)
    {
        var output = new List<RRect>();
        foreach (var r in texts)
        {
            if (r.W <= 0) continue;
            var votes = new float[r.W];
            var totals = new float[r.W];
            for (int y = r.Y; y < r.Y2; y++)
                for (int x = r.X; x < r.X2; x++)
                {
                    if (ink.At(x, y) == 0) continue;
                    int i = x - r.X;
                    totals[i] += 1f;
                    var (b, g, red) = bgr.At(x, y);
                    int max = Math.Max(b, Math.Max(g, red));
                    int min = Math.Min(b, Math.Min(g, red));
                    if (max - min > 70) votes[i] += 1f;
                }

            int kernel = Math.Max(3, (int)(r.H * 0.6));
            var v = Morphology.BlurRow(votes, kernel);
            var t = Morphology.BlurRow(totals, kernel);
            var states = new byte[r.W];
            for (int i = 0; i < states.Length; i++) states[i] = v[i] > Math.Max(1f, t[i] * 0.65f) ? (byte)1 : (byte)0;
            states = Morphology.MedianRow(states, 5);

            var runs = new List<(int A, int B)>();
            int start = 0;
            byte state = states[0];
            for (int x = 1; x < states.Length; x++)
            {
                if (states[x] == state) continue;
                runs.Add((start, x));
                start = x;
                state = states[x];
            }

            runs.Add((start, r.W));
            bool split = runs.Count > 1 && runs.All(run => run.B - run.A > Math.Max(100, 4 * r.H));
            if (!split)
            {
                output.Add(r);
                continue;
            }

            foreach (var (a, b) in runs)
            {
                var t2 = MaskOps.Shrink(new RRect(r.X + a, r.Y, b - a, r.H), ink, 3);
                if (t2 != null) output.Add(t2.Value);
            }
        }

        return output;
    }

    public static List<RRect> DetectImages(BgrImg bgr, MaskGrid fg)
    {
        int w = bgr.W;
        int h = bgr.H;
        int k = Math.Max(9, (int)Math.Round(0.008 * h, MidpointRounding.ToEven));
        var fat = MaskOps.FillHoles(Morphology.Open(fg, k, k));
        var (labels, stats) = Morphology.ConnectedComponents(fat);
        var output = new List<RRect>();
        foreach (var c in stats)
        {
            var r = c.Rect;
            if (r.W > (int)(0.55 * w)
                || r.H > (int)(0.45 * h)
                || r.Area > (long)(0.20 * w * h)
                || r.W < 28
                || r.H < 24
                || c.Area < 700)
            {
                continue;
            }

            double fill = (double)c.Area / r.Area;
            int minPhoto = Math.Max(64, (int)(0.040 * h));
            if (fill < 0.48 || r.W < minPhoto || r.H < minPhoto) continue;
            if (ColorStats.RoiStd(bgr, r, labels, c.Id) < 12) continue;
            var t = MaskOps.Shrink(r, fat, 1);
            if (t != null) output.Add(t.Value);
        }

        return Selection.Merge(output, 0.2);
    }

    public static List<RRect> DetectLogos(BgrImg bgr, MaskGrid fg)
    {
        int w = bgr.W;
        int h = bgr.H;
        var band = fg.Clone();
        int from = Math.Min(Math.Max(48, (int)(0.14 * h)), h);
        for (int y = from; y < h; y++)
            for (int x = 0; x < w; x++)
                band.Set(x, y, 0);

        var closed = Morphology.Close(band, 17, 9);
        var (labels, stats) = Morphology.ConnectedComponents(closed);
        var output = new List<RRect>();
        foreach (var c in stats)
        {
            var r = c.Rect;
            double aspect = (double)r.W / r.H;
            if (r.W > (int)(0.42 * w)
                || r.H > (int)(0.11 * h)
                || r.W < 72
                || r.H < 28
                || aspect is < 1.4 or > 5.5
                || (double)c.Area / r.Area < 0.18
                || ColorStats.RoiStd(bgr, r, labels, c.Id) < 20)
            {
                continue;
            }

            var t = MaskOps.Shrink(r, closed, 1);
            if (t != null) output.Add(t.Value);
        }

        return output;
    }

    private static List<RRect> SquareComponents(MaskGrid mask, MaskGrid sup, int min, int max, double lo, double hi)
    {
        var (_, stats) = Morphology.ConnectedComponents(MaskOps.Suppress(mask, sup));
        var output = new List<RRect>();
        foreach (var c in stats)
        {
            var r = c.Rect;
            double aspect = (double)r.W / r.H;
            double fill = (double)c.Area / r.Area;
            if (r.W < min || r.H < min || r.W > max || r.H > max
                || aspect is < 0.62 or > 1.55 || fill < lo || fill > hi)
            {
                continue;
            }

            var t = Geo.Clip(new RRect(r.X - 2, r.Y - 2, r.W + 4, r.H + 4), mask.W, mask.H);
            if (t != null) output.Add(t.Value);
        }

        return output;
    }

    public static List<RRect> SquareBadges(BgrImg bgr, MaskGrid fg, double[] bg, MaskGrid sup)
    {
        int min = Math.Max(24, (int)(0.018 * bgr.H));
        int max = Math.Max(90, (int)(0.055 * bgr.H));
        var solids = SquareComponents(fg, sup, min, max, 0.28, 1.0);
        var output = SquareComponents(ColorStats.Foreground(bgr, bg, 12.0), sup, min, max, 0.04, 0.25);
        foreach (var s in solids)
            if (!output.Any(r => Geo.IoU(s, r) >= 0.25 || Geo.Containment(s, r) >= 0.5))
                output.Add(s);
        return output;
    }

    public static List<RRect> Textures(BgrImg bgr, MaskGrid ink)
    {
        var work = Morphology.Open(Morphology.Close(ink, 11, 11), 13, 13);
        var (_, stats) = Morphology.ConnectedComponents(work);
        var output = new List<RRect>();
        var transposed = new MaskGrid(ink.H, ink.W);
        for (int y = 0; y < ink.H; y++)
            for (int x = 0; x < ink.W; x++)
                transposed.Set(y, x, ink.At(x, y));

        foreach (var c in stats)
        {
            var r = c.Rect;
            double aspect = (double)r.W / r.H;
            if (Math.Min(r.W, r.H) < 30
                || Math.Max(r.W, r.H) > 0.4 * Math.Max(ink.W, ink.H)
                || aspect <= 0.45
                || aspect >= 4.0)
            {
                continue;
            }

            if (ColorStats.Histogram(bgr, r, 16).Count(v => v >= 3) < 45
                || c.Area < 0.55 * r.Area
                || ColorStats.SurfaceFraction(bgr, r) > 0.6)
            {
                continue;
            }

            foreach (var p in MaskOps.SplitGaps(r, ink, 0.03))
                foreach (var s in MaskOps.SplitGaps(new RRect(p.Y, p.X, p.H, p.W), transposed, 0.03))
                {
                    var rr = new RRect(s.Y, s.X, s.H, s.W);
                    if (Math.Min(rr.W, rr.H) >= 30)
                        output.Add(Geo.Expand(rr, 2, ink.W, ink.H));
                }
        }

        return output;
    }

    public static List<RRect> RejectGlyphs(List<RRect> rects, MaskGrid ink, List<RRect> surfaces)
    {
        var (_, stats) = Morphology.ConnectedComponents(ink);
        var components = stats.Where(c => c.Area >= 8).Select(c => c.Rect).ToList();
        var output = new List<RRect>();
        foreach (var r in rects)
        {
            bool neighbors = components.Any(c =>
                c.H >= 0.45 * r.H
                && Math.Min(r.Y2, c.Y2) - Math.Max(r.Y, c.Y) > 0.5 * Math.Min(r.H, c.H)
                && Geo.Containment(c, r) < 0.3
                && Math.Max(r.X, c.X) - Math.Min(r.X2, c.X2) >= -4
                && Math.Max(r.X, c.X) - Math.Min(r.X2, c.X2) < 0.4 * Math.Min(r.H, c.H));

            int count = 0;
            for (int y = Math.Max(r.Y, 0); y < Math.Min(r.Y2, ink.H); y++)
                for (int x = Math.Max(r.X, 0); x < Math.Min(r.X2, ink.W); x++)
                    if (ink.At(x, y) > 0)
                        count++;
            double density = (double)count / r.Area;

            if (!neighbors
                || (Math.Min(r.W, r.H) >= 45 && density > 0.5)
                || surfaces.Any(s => Geo.IoU(r, s) > 0.65))
            {
                output.Add(r);
            }
        }

        return output;
    }

    public static (List<RRect> Avatars, List<RRect> Icons) RefineAvatars(List<RRect> candidates, List<RRect> texts,
        int w, int h)
    {
        var avatars = new List<RRect>();
        var icons = new List<RRect>();
        int maxIcon = Math.Max(36, (int)(0.032 * h));
        int headerY = (int)(0.14 * h);
        foreach (var r in candidates)
        {
            bool hasText = texts.Any(t =>
                Math.Min(r.Y2, t.Y2) - Math.Max(r.Y, t.Y) >= 0.25 * Math.Min(r.H, t.H)
                && t.X >= r.X2 - 8
                && t.X <= r.X2 + 5.5 * r.W);
            bool leftColumn = r.X < 0.42 * w && r.Y > headerY;
            if (hasText || leftColumn || Math.Max(r.W, r.H) > maxIcon) avatars.Add(r);
            else icons.Add(r);
        }

        return (avatars, icons);
    }

    /// <summary>点状菜单（三个 / 六个 / 九个点连成的小方块）。</summary>
    public static List<RRect> DotIcons(MaskGrid ink)
    {
        var (labels, stats) = Morphology.ConnectedComponents(ink);
        var dots = new MaskGrid(ink.W, ink.H);
        var small = new List<RRect>();
        foreach (var c in stats)
        {
            var r = c.Rect;
            double aspect = (double)r.W / r.H;
            if (r.W is >= 2 and <= 8
                && r.H is >= 2 and <= 8
                && aspect is >= 0.6 and <= 1.7
                && c.Area >= 0.5 * r.Area)
            {
                for (int y = r.Y; y < r.Y2; y++)
                    for (int x = r.X; x < r.X2; x++)
                    {
                        if (labels[y * ink.W + x] != c.Id) continue;
                        dots.Set(x, y, 255);
                    }

                small.Add(r);
            }
        }

        var (_, joined) = Morphology.ConnectedComponents(Morphology.Dilate(dots, 9));
        var output = new List<RRect>();
        foreach (var c in joined)
        {
            var r = c.Rect;
            var children = small.Where(d => Geo.Containment(d, r) > 0.9).ToList();
            if (children.Count is not (3 or 6 or 9) || r.W > 50 || r.H > 50) continue;
            if (children.Max(d => d.W) - children.Min(d => d.W) > 2
                || children.Max(d => d.H) - children.Min(d => d.H) > 2)
            {
                continue;
            }

            var rr = children.Aggregate(Geo.Union);
            var context = Geo.Expand(rr, 5, ink.W, ink.H);
            bool inkOutsideDots = false;
            for (int y = context.Y; y < context.Y2 && !inkOutsideDots; y++)
                for (int x = context.X; x < context.X2; x++)
                {
                    if (ink.At(x, y) == 0 || dots.At(x, y) != 0) continue;
                    inkOutsideDots = true;
                    break;
                }

            if (inkOutsideDots) continue;
            var near = Geo.Expand(rr, 7, ink.W, ink.H);
            long maxArea = small.Max(d => d.Area);
            if (stats.Any(s => s.Area > maxArea && Geo.Intersect(near, s.Rect) != null)) continue;
            output.Add(context);
        }

        return output;
    }

    public static List<RRect> Icons(MaskGrid fg, MaskGrid sup, MaskGrid textMask, List<RRect> texts)
    {
        var residual = MaskOps.Suppress(MaskOps.Suppress(fg, sup), textMask);
        var closed = Morphology.Apply(Morphology.Apply(residual, MorphOp.Close, 3, 3, KernelShape.Ellipse),
            MorphOp.Close, 3, 11, KernelShape.Rect);
        int min = Math.Max(14, (int)(0.012 * fg.H));
        int max = Math.Max(28, (int)(0.038 * fg.H));
        var (_, stats) = Morphology.ConnectedComponents(closed);
        var output = new List<RRect>();
        foreach (var c in stats)
        {
            var r = c.Rect;
            if (r.W is >= 1 and <= 3
                && r.H >= min
                && r.H <= max * 1.6
                && c.Area > 0.6 * r.Area)
            {
                output.Add(r);
                continue;
            }

            double fill = (double)c.Area / r.Area;
            double aspect = (double)r.W / r.H;
            if (r.W < min
                || r.H < min
                || r.W > max * 1.4
                || r.H > max * 1.6
                || fill < 0.22
                || c.Area < 80
                || aspect is < 0.32 or > 2.2)
            {
                continue;
            }

            if (texts.Any(t => Geo.Containment(r, t) > 0.5 || Geo.Containment(t, r) > 0.5)) continue;
            output.Add(r);
        }

        return output;
    }
}

