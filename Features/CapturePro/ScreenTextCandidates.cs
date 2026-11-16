using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>
/// 屏幕选字的「候选」切分：把识别出来的整段文字切成一条一条，点哪条就复制哪条。
/// </summary>
/// <remarks>
/// <para>
/// 顺序与内容都**只由 OCR 文本决定**（纯函数，<c>--selftest</c> 直接断言）：先按行，再按句末标点
/// （标点留在句尾），太长的再按逗号类标点切，仍超长就硬切。空白行丢掉，完全相同的候选只留第一条
/// （OCR 有时会把同一行吐两遍）。
/// </para>
/// <para>
/// 不在这里做「智能摘句」：候选只需要短到一眼能看完、点得中，识别质量归 OCR 引擎。
/// </para>
/// </remarks>
internal static class ScreenTextCandidates
{
    /// <summary>单条候选的长度上限（再长就切）。</summary>
    public const int DefaultMaxLength = 120;

    private static readonly char[] SentenceEnders = { '。', '！', '？', '!', '?', '；', ';' };
    private static readonly char[] ClauseSeps = { '，', ',', '、', '：', ':' };

    /// <summary>按默认长度上限切分。</summary>
    public static IReadOnlyList<string> Build(IEnumerable<string> lines) =>
        Build(lines, DefaultMaxLength);

    /// <summary>切出候选列表：一条一句，句子太长再切短。</summary>
    public static IReadOnlyList<string> Build(IEnumerable<string> lines, int maxLength)
    {
        if (maxLength < 1) throw new ArgumentOutOfRangeException(nameof(maxLength));

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (string piece in SplitLine(raw.Trim(), maxLength))
            {
                string text = piece.Trim();
                if (text.Length == 0) continue;
                if (!seen.Add(text)) continue;
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>切一行：先按句末标点，再按逗号类标点，最后硬切。</summary>
    public static IEnumerable<string> SplitLine(string line, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(line)) yield break;

        foreach (string sentence in SplitAfter(line.Trim(), SentenceEnders))
        {
            if (sentence.Length <= maxLength)
            {
                yield return sentence;
                continue;
            }

            foreach (string clause in SplitAfter(sentence, ClauseSeps))
            {
                if (clause.Length <= maxLength)
                {
                    yield return clause;
                    continue;
                }

                for (int i = 0; i < clause.Length; i += maxLength)
                    yield return clause.Substring(i, Math.Min(maxLength, clause.Length - i));
            }
        }
    }

    /// <summary>在分隔符之后切开，分隔符跟着前一段（连续的分隔符一起带上）。</summary>
    private static IEnumerable<string> SplitAfter(string text, char[] separators)
    {
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (Array.IndexOf(separators, text[i]) < 0) continue;

            // 「？！」这类连续标点算同一句的结尾
            while (i + 1 < text.Length && Array.IndexOf(separators, text[i + 1]) >= 0) i++;

            string piece = text.Substring(start, i - start + 1).Trim();
            if (piece.Length > 0) yield return piece;
            start = i + 1;
        }

        if (start < text.Length)
        {
            string tail = text.Substring(start).Trim();
            if (tail.Length > 0) yield return tail;
        }
    }
}
