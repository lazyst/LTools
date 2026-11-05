using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Threading;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「文字点击」：在指定区域里识别文字，找到指定的文字就点它（找不到按步骤失败策略处理）。
/// </summary>
/// <remarks>
/// <para>
/// 取图 → OCR（带坐标）→ 在识别到的词里按「包含」匹配 → 点那几个字的外接矩形中心。
/// 匹配忽略大小写与词间空格，所以「保存」「Save as」这样写都能命中；同一行 / 多行命中时取最上面那个
/// （同高取最左），因为脚本通常想点第一个出现的。
/// </para>
/// <para>
/// 区域与「截图找图」同一套语义：前台窗口 / 整个屏幕 / 固定矩形（后者的坐标是虚拟桌面物理像素，
/// 与鼠标注入、截图一致）。坐标也是物理像素，不受 DPI 缩放影响。
/// </para>
/// <para>
/// 找不到就是失败：不会「假装点过了」——交给步骤的失败策略（继续下一步 / 中止），
/// 报错里带上识别到的文字，方便发现是 OCR 没认出来还是文字写错了。
/// 需要等按钮出现时把「找不到时最多等」调大（内部会反复截图重试），不用自己在流程里套循环。
/// </para>
/// </remarks>
internal static class TextClickAction
{
    /// <summary>等文字出现时的重试间隔（ms）。OCR 本身耗时远大于它。</summary>
    private const int PollMs = 300;

    public static VarValue Run(ActionDto a, RunContext? ctx)
    {
        string needle = (a.Text ?? "").Trim();
        if (needle.Length == 0)
            throw new InvalidOperationException($"「{a.Name}」还没有指定要点击的文字");

        var (rx, ry, rw, rh) = ScreenRegion.Resolve(a.Region ?? ImageRegion.foreground, a.Rect);
        if (rw <= 0 || rh <= 0)
            throw new InvalidOperationException($"「{a.Name}」的区域无效：{rx},{ry} · {rw}×{rh}");

        int timeout = Math.Clamp(a.TimeoutMs ?? 0, 0, 600_000);
        long started = Environment.TickCount64;

        // 自动化指示器是置顶窗口，会盖住目标并被截进画面：找字期间收起，点完再恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        try
        {
            while (true)
            {
                IReadOnlyList<OcrLineBox> lines;
                using (Bitmap bmp = ScreenCapture.Capture(rx, ry, rw, rh))
                    lines = OcrLayout.Read(bmp, a.OcrEngine);

                if (TryLocate(lines, needle, out int lx, out int ly, out string matched))
                {
                    int x = rx + lx, y = ry + ly;
                    MouseOps.Move(x, y);
                    Thread.Sleep(10);   // 让光标先落定，再发按键（与「鼠标操作」一致）
                    MouseOps.Perform(a.MouseAction ?? MouseActionKind.click, a.WheelDelta ?? 120);

                    AppLog.Info("TextClick", $"「{a.Name}」点到「{matched}」：{x},{y}（{rw}×{rh} 区域内）");
                    return new VarValue { Text = $"{x},{y}", X = x, Y = y, Flag = true };
                }

                int elapsed = (int)(Environment.TickCount64 - started);
                if (elapsed >= timeout)
                    throw new InvalidOperationException(MissMessage(a.Name, needle, lines, timeout));

                int wait = Math.Min(PollMs, timeout - elapsed);
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
    }

    // ============ 找字 ============

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
    private static string Compact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>找不到时的报错：等没等、区域里到底认出了什么，一句话说清。</summary>
    private static string MissMessage(string name, string needle, IReadOnlyList<OcrLineBox> lines, int timeout)
    {
        var seen = new StringBuilder();
        foreach (OcrLineBox line in lines)
        {
            if (seen.Length > 0) seen.Append(" / ");
            seen.Append(line.Text);
            if (seen.Length > 80) { seen.Append('…'); break; }
        }

        string wait = timeout > 0 ? $"，等了 {timeout} 毫秒" : "";
        string got = seen.Length > 0 ? $"；识别到的文字：{seen}" : "；区域里没有识别到文字";
        return $"「{name}」在区域里没有找到「{needle}」{wait}{got}";
    }
}
