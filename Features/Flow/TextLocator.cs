using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
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

/// <summary>
/// 「在区域里找文字」：截一块屏 → 带坐标 OCR → 按「包含」匹配。
/// 「文字点击」动作与「找文字」取值器共用这一套定位，命中坐标是虚拟桌面物理像素，可直接交给鼠标。
/// </summary>
/// <remarks>
/// <para>
/// 匹配忽略大小写与词间空白，所以「保存」「Save as」这样写都能命中；多个命中取最上面那个
/// （同高取最左），因为脚本通常想操作第一个出现的。
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

    /// <summary>
    /// 在区域内反复找目标文字，直到找到或超时。命中时返回桌面物理坐标。
    /// </summary>
    public static TextMatch Locate(string needle, ImageRegion region, int[]? rect, string? engine,
        int timeoutMs, int intervalMs, RunContext? ctx)
    {
        var (rx, ry, rw, rh) = ScreenRegion.Resolve(region, rect);
        if (rw <= 0 || rh <= 0)
            throw new FlowException($"找字区域无效：{rx},{ry} · {rw}×{rh}");

        timeoutMs = Math.Clamp(timeoutMs, 0, 600_000);
        intervalMs = Math.Clamp(intervalMs, 50, 5_000);
        long deadline = Environment.TickCount64 + timeoutMs;
        IReadOnlyList<string> seen = Array.Empty<string>();

        // 自动化指示器是置顶窗口，会盖住目标并被截进画面：找字期间收起，结束后恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            while (true)
            {
                IReadOnlyList<OcrLineBox> lines;
                using (Bitmap bmp = ScreenCapture.Capture(rx, ry, rw, rh))
                    lines = OcrLayout.Read(bmp, engine);

                seen = LineTexts(lines);
                if (TryLocate(lines, needle, out int lx, out int ly, out string matched))
                    return new TextMatch { Found = true, X = rx + lx, Y = ry + ly, Matched = matched, Seen = seen };

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
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }

        return new TextMatch { Seen = seen };
    }

    /// <summary>让「找文字」取值器可被 set / 条件直接使用。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        string needle = Template.Render(probe.Text ?? "", ctx).Trim();
        if (needle.Length == 0) throw new FlowException("「找文字」还没有指定要查找的文字");

        var result = Locate(needle, probe.Region, probe.Rect, probe.OcrEngine,
            probe.TimeoutMs, probe.IntervalMs, ctx);

        // ${变量} = 命中的文字；位置在 ${变量.x} / ${变量.y}；没找到时 found=false、文字为空。
        return result.Found
            ? new VarValue { Text = result.Matched, X = result.X, Y = result.Y, Flag = true }
            : new VarValue { Text = "", Flag = false };
    }

    // ============ 匹配 ============

    /// <summary>
    /// 在一张图的行里找文字，命中返回它在图内的中心坐标（左上角原点）。
    /// 按「包含」匹配，忽略大小写与词间空格；多个命中取最上面那个。
    /// </summary>
    public static bool TryLocate(IReadOnlyList<OcrLineBox> lines, string needle,
        out int x, out int y, out string matched)
    {
        x = y = 0;
        matched = "";
        string compactNeedle = Compact(needle);
        if (compactNeedle.Length == 0) return false;

        bool found = false;
        int bestTop = int.MaxValue, bestLeft = int.MaxValue;
        var chunk = new StringBuilder();

        foreach (OcrLineBox line in lines)
        {
            if (!TryLocateInLine(line, compactNeedle, out int lx, out int ly, out string text)) continue;

            // 多行命中：先比上下，再比左右（点「第一个」而不是随便一个）。
            if (found && (ly > bestTop || (ly == bestTop && lx >= bestLeft))) continue;

            found = true;
            bestTop = ly;
            bestLeft = lx;
            x = lx;
            y = ly;
            chunk.Clear().Append(text);
        }

        if (!found) return false;
        matched = chunk.ToString();
        return true;
    }

    /// <summary>在一行里定位：优先用词框圈出「这几个字」，没有词框就退回整行中心。</summary>
    private static bool TryLocateInLine(OcrLineBox line, string compactNeedle,
        out int x, out int y, out string text)
    {
        x = y = 0;
        text = "";

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

        string haystack = joined.ToString();
        int idx = haystack.IndexOf(compactNeedle, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            // 没有词框的行（引擎没给词级结果）只用整行文本兜底，点整行中心。
            if (line.Words.Count > 0 || line.W <= 0 || line.H <= 0) return false;
            if (Compact(line.Text).IndexOf(compactNeedle, StringComparison.OrdinalIgnoreCase) < 0) return false;

            x = line.X + line.W / 2;
            y = line.Y + line.H / 2;
            text = line.Text;
            return true;
        }

        int first = owner[idx];
        int last = owner[Math.Min(idx + compactNeedle.Length - 1, owner.Count - 1)];

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int i = first; i <= last; i++)
        {
            OcrWordBox w = line.Words[i];
            left = Math.Min(left, w.X);
            top = Math.Min(top, w.Y);
            right = Math.Max(right, w.X + w.W);
            bottom = Math.Max(bottom, w.Y + w.H);
        }

        if (right <= left || bottom <= top) return false;

        x = (left + right) / 2;
        y = (top + bottom) / 2;
        text = haystack.Substring(idx, Math.Min(compactNeedle.Length, haystack.Length - idx));
        return true;
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

        string wait = timeoutMs > 0 ? $"，等了 {timeoutMs} 毫秒" : "";
        string got = joined.Length > 0 ? $"；识别到的文字：{joined}" : "；区域里没有识别到文字";
        return $"「{name}」在区域里没有找到「{needle}」{wait}{got}";
    }
}
