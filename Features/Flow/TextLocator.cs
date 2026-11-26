using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using LTools.Core.Actions;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>一次找字的结果：命中中心（虚拟桌面物理像素）+ 命中的文字 + 最后一张图认出什么。</summary>
internal sealed class TextMatch
{
    /// <summary>是否找到。</summary>
    public bool Found { get; init; }

    /// <summary>命中文字外接矩形的中心（虚拟桌面物理像素）；未命中为 0。</summary>
    public int X { get; init; }

    /// <summary>命中文字外接矩形的中心（虚拟桌面物理像素）；未命中为 0。</summary>
    public int Y { get; init; }

    /// <summary>实际命中的文字（可能比要找的多，如找「保存」命中「保存并关闭」）。</summary>
    public string Matched { get; init; } = "";

    /// <summary>最后一张图里识别到的行文本，供「没找到」的报错说明情况。</summary>
    public IReadOnlyList<string> Seen { get; init; } = Array.Empty<string>();
}

/// <summary>一个文字命中：外接矩形 + 实际命中的那段文字（虚拟桌面物理像素）。</summary>
internal sealed class TextHit
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>一次找字的结果：全部命中（从上到下、从左到右）+ 最后一张图认出什么。</summary>
internal sealed class TextLookup
{
    public IReadOnlyList<TextHit> Hits { get; init; } = Array.Empty<TextHit>();

    public IReadOnlyList<string> Seen { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 「在区域里找文字」：截一块屏 → 带坐标 OCR → 按「包含」匹配。
/// 「文字点击」动作与「找文字」取值器共用这一套定位，命中坐标是虚拟桌面物理像素，可直接交给鼠标。
/// </summary>
/// <remarks>
/// <para>
/// 匹配忽略大小写与词间空白，所以「保存」「Save as」这样写都能命中；多个命中按「从上到下、
/// 从左到右」排序（脚本通常想操作第一个出现的）。<c>exact = true</c> 时要求命中的字符区间
/// 首尾都落在词边界上——「保存」不再命中「保存并关闭」，但仍是「保存 (Ctrl+S)」里那个词。
/// </para>
/// <para>
/// 找不到不是错误：<see cref="Locate"/> 返回 <c>Found = false</c>，由调用方决定是报失败（文字点击）
/// 还是当作一次「没找到」的值（取值器）。等文字出现用 <c>timeoutMs</c> 轮询，不用在流程里套循环。
/// </para>
/// </remarks>
internal static class TextLocator
{
    /// <summary>默认轮询间隔（ms）。OCR 本身耗时远大于它。</summary>
    public const int DefaultIntervalMs = 300;

    /// <summary>一次最多收集多少个命中。</summary>
    internal const int MaxHits = 200;

    /// <summary>
    /// 在区域内反复找目标文字，直到找到（或凑够 <paramref name="minCount"/> 个）或超时。
    /// </summary>
    public static TextMatch Locate(string needle, ImageRegion region, int[]? rect, string? engine,
        int timeoutMs, int intervalMs, bool exact, RunContext? ctx)
    {
        TextLookup lookup = LocateAll(needle, region, rect, engine, timeoutMs, intervalMs, exact, 1, ctx);
        if (lookup.Hits.Count == 0) return new TextMatch { Seen = lookup.Seen };

        TextHit hit = lookup.Hits[0];
        return new TextMatch { Found = true, X = hit.X, Y = hit.Y, Matched = hit.Text, Seen = lookup.Seen };
    }

    /// <summary>
    /// 在区域内找出所有命中（同一段文字出现多次）。命中数达到 <paramref name="minCount"/> 就返回，
    /// 超时返回最后一次看到的那些——语义与「截图找图」的 <c>RunAll</c> 一致。
    /// </summary>
    public static TextLookup LocateAll(string needle, ImageRegion region, int[]? rect, string? engine,
        int timeoutMs, int intervalMs, bool exact, int minCount, RunContext? ctx)
    {
        var (rx, ry, rw, rh) = ScreenRegion.Resolve(region, rect);
        if (rw <= 0 || rh <= 0)
            throw new FlowException($"找字区域无效：{rx},{ry} · {rw}×{rh}");

        minCount = Math.Clamp(minCount, 1, MaxHits);
        timeoutMs = Timeouts.Loop(timeoutMs);
        intervalMs = Math.Clamp(intervalMs, 50, 5_000);
        long deadline = Environment.TickCount64 + timeoutMs;
        IReadOnlyList<string> seen = Array.Empty<string>();
        IReadOnlyList<TextHit> last = Array.Empty<TextHit>();

        // 自动化指示器是置顶窗口，会盖住目标并被截进画面：找字期间收起，结束后恢复。
        using var _ = RecordingIndicatorWindow.SuppressForCapture();
        while (true)
        {
            IReadOnlyList<OcrLineBox> lines;
            using (Bitmap bmp = ScreenCapture.Capture(rx, ry, rw, rh))
                lines = OcrLayout.Read(bmp, engine);

            seen = LineTexts(lines);
            var hits = new List<TextHit>();
            foreach (TextHit hit in CollectLines(lines, needle, exact))
            {
                hits.Add(new TextHit
                {
                    X = rx + hit.X,
                    Y = ry + hit.Y,
                    W = hit.W,
                    H = hit.H,
                    Text = hit.Text,
                });
                if (hits.Count >= MaxHits) break;
            }

            last = hits;
            if (last.Count >= minCount) return new TextLookup { Hits = last, Seen = seen };

            long remain = deadline - Environment.TickCount64;
            if (remain <= 0) break;

            int wait = (int)Math.Min(intervalMs, remain);
            if (ctx != null)
            {
                if (!ctx.Wait(wait)) throw new FlowAbortException("已停止", userCancelled: true);
            }
            else
            {
                Thread.Sleep(wait);
            }
        }

        return new TextLookup { Hits = last, Seen = seen };
    }

    /// <summary>让「找文字」取值器可被 set / 条件直接使用。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        string needle = Template.Render(probe.Text ?? "", ctx).Trim();
        if (needle.Length == 0) throw new FlowException("「找文字」还没有指定要查找的文字");

        var result = Locate(needle, probe.Region, probe.Rect, probe.OcrEngine,
            probe.TimeoutMs, probe.IntervalMs, exact: false, ctx);

        // ${变量} = 命中的文字；位置在 ${变量.x} / ${变量.y}；没找到时 found=false、文字为空。
        return result.Found
            ? new VarValue { Text = result.Matched, X = result.X, Y = result.Y, Flag = true }
            : new VarValue { Text = "", Flag = false };
    }

    // ============ 匹配 ============

    /// <summary>
    /// 在一张图的行里找文字，命中返回它在图内的中心坐标（左上角原点）。
    /// 按「包含」（或 <paramref name="exact"/> 时的词边界精确）匹配，忽略大小写与词间空格；
    /// 多个命中按「从上到下、从左到右」排序。
    /// </summary>
    public static bool TryLocate(IReadOnlyList<OcrLineBox> lines, string needle,
        out int x, out int y, out string matched)
    {
        IReadOnlyList<TextHit> hits = CollectLines(lines, needle, exact: false);
        if (hits.Count == 0)
        {
            x = y = 0;
            matched = "";
            return false;
        }

        x = hits[0].X;
        y = hits[0].Y;
        matched = hits[0].Text;
        return true;
    }

    /// <summary>把一张图里所有命中按「上 → 下、左 → 右」排好序（重叠的只留先出现的）。</summary>
    internal static IReadOnlyList<TextHit> CollectLines(
        IReadOnlyList<OcrLineBox> lines, string needle, bool exact)
    {
        var hits = new List<TextHit>();
        foreach (OcrLineBox line in lines)
            CollectInLine(line, needle, exact, hits);

        hits.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return Dedupe(hits);
    }

    /// <summary>
    /// 在一行里收集所有命中。一行里同一个词出现两次就是两个目标（以前只取第一个）。
    /// </summary>
    internal static void CollectInLine(OcrLineBox line, string needle, bool exact, List<TextHit> into)
    {
        string compactNeedle = Compact(needle);
        if (compactNeedle.Length == 0) return;

        // 把词按字符摊平，记下每个字属于哪个词——这样「包含」命中后能换算回词的矩形。
        var joined = new StringBuilder();
        var owner = new List<int>();
        for (int i = 0; i < line.Words.Count; i++)
        {
            foreach (char ch in line.Words[i].Text)
            {
                if (char.IsWhiteSpace(ch)) continue;   // 词间空格不参与匹配，避免「Save as」对不上
                joined.Append(ch);
                owner.Add(i);
            }
        }

        // 没有词框的行（引擎没给词级结果）只能按整行文本判断，命中就点整行中心。
        if (owner.Count == 0)
        {
            string compactLine = Compact(line.Text);
            bool hit = exact
                ? string.Equals(compactLine, compactNeedle, StringComparison.OrdinalIgnoreCase)
                : compactLine.IndexOf(compactNeedle, StringComparison.OrdinalIgnoreCase) >= 0;
            if (hit && line.W > 0 && line.H > 0)
            {
                into.Add(new TextHit
                {
                    X = line.X + line.W / 2,
                    Y = line.Y + line.H / 2,
                    W = line.W,
                    H = line.H,
                    Text = line.Text,
                });
            }
            return;
        }

        string haystack = joined.ToString();
        int from = 0;
        while (from <= haystack.Length - compactNeedle.Length)
        {
            int idx = haystack.IndexOf(compactNeedle, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            int len = Math.Min(compactNeedle.Length, haystack.Length - idx);
            from = idx + Math.Max(1, len);   // 同一段连续文字里不重复计数

            if (exact && !WordBounded(owner, idx, len)) continue;

            int first = owner[idx];
            int last = owner[Math.Min(idx + len - 1, owner.Count - 1)];

            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            for (int i = first; i <= last; i++)
            {
                OcrWordBox w = line.Words[i];
                left = Math.Min(left, w.X);
                top = Math.Min(top, w.Y);
                right = Math.Max(right, w.X + w.W);
                bottom = Math.Max(bottom, w.Y + w.H);
            }

            if (right <= left || bottom <= top) continue;

            into.Add(new TextHit
            {
                X = (left + right) / 2,
                Y = (top + bottom) / 2,
                W = right - left,
                H = bottom - top,
                Text = haystack.Substring(idx, len),
            });
        }
    }

    /// <summary>
    /// 命中的字符区间首尾是否都落在词边界上（精确匹配用）。
    /// 「保存并关闭」是一个词 → 找「保存」不算；「保存 (Ctrl+S)」是两个词 → 算。
    /// </summary>
    private static bool WordBounded(List<int> owner, int idx, int len)
    {
        int last = idx + len - 1;
        if (last >= owner.Count) return false;

        bool startsAtWord = idx == 0 || owner[idx - 1] != owner[idx];
        bool endsAtWord = last == owner.Count - 1 || owner[last + 1] != owner[last];
        return startsAtWord && endsAtWord;
    }

    /// <summary>同一块文字被引擎报了两遍时只留一个（交叠超过较小矩形一半）。</summary>
    private static IReadOnlyList<TextHit> Dedupe(List<TextHit> hits)
    {
        var kept = new List<TextHit>(hits.Count);
        foreach (TextHit hit in hits)
        {
            bool overlap = false;
            foreach (TextHit other in kept)
            {
                int left = Math.Max(hit.X - hit.W / 2, other.X - other.W / 2);
                int top = Math.Max(hit.Y - hit.H / 2, other.Y - other.H / 2);
                int right = Math.Min(hit.X + hit.W / 2, other.X + other.W / 2);
                int bottom = Math.Min(hit.Y + hit.H / 2, other.Y + other.H / 2);
                if (right <= left || bottom <= top) continue;

                long inter = (long)(right - left) * (bottom - top);
                long smaller = Math.Min((long)hit.W * hit.H, (long)other.W * other.H);
                if (smaller > 0 && inter * 2 > smaller)
                {
                    overlap = true;
                    break;
                }
            }
            if (!overlap) kept.Add(hit);
        }
        return kept;
    }

    /// <summary>去掉所有空白（空格 / 制表 / 换行 / 全角空格），让「Save as」和「Saveas」等价。</summary>
    public static string Compact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>把行文本摊成列表（报错时说明「区域里到底认出了什么」）。</summary>
    private static IReadOnlyList<string> LineTexts(IReadOnlyList<OcrLineBox> lines)
    {
        var texts = new List<string>(lines.Count);
        foreach (OcrLineBox line in lines)
            if (line.Text.Length > 0) texts.Add(line.Text);
        return texts;
    }

    /// <summary>找不到时的报错：等没等、区域里到底认出了什么，一句话说清。</summary>
    public static string MissMessage(string name, string needle, IReadOnlyList<string> seen, int timeoutMs)
    {
        var joined = new StringBuilder();
        foreach (string text in seen)
        {
            if (joined.Length > 0) joined.Append(" / ");
            joined.Append(text);
            if (joined.Length > 80) { joined.Append('…'); break; }
        }

        string wait = Timeouts.DescribeWait(timeoutMs);
        string got = joined.Length > 0 ? $"；识别到的文字：{joined}" : "；区域里没有识别到文字";
        return $"「{name}」在区域里没有找到「{needle}」{wait}{got}";
    }
}
