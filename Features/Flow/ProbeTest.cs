using System;
using System.Threading;

namespace LTools.Features;

/// <summary>
/// 取值器的「就地测试」：用当前屏幕 / 剪贴板等真实环境求值一次，返回可读结果。
/// </summary>
/// <remarks>
/// 只用于编辑器反馈，不影响流程定义；变量类取值在空上下文中测试会报「未定义」，
/// 这正是要提示用户的问题。图像等待上限压到 <see cref="MaxWaitMs"/>，避免点一下测试卡很久。
/// </remarks>
internal static class ProbeTest
{
    /// <summary>测试时图像匹配最长等待（实际流程仍用各自配置）。</summary>
    public const int MaxWaitMs = 5_000;

    /// <summary>求值并生成一行结果说明。可能阻塞（图像匹配 / 取选中文本），调用方放到后台线程。</summary>
    public static string Run(Probe probe)
    {
        if (probe.Kind == ProbeKind.user) return "「询问用户」只在流程运行时可用。";

        var test = probe.Clone();
        if (test.Kind == ProbeKind.image)
            test.TimeoutMs = Math.Clamp(test.TimeoutMs, 0, MaxWaitMs);

        var ctx = new RunContext(CancellationToken.None, 0, 0);
        var value = ProbeEvaluator.Evaluate(test, ctx);
        return Describe(test, value);
    }

    /// <summary>把取值结果变成一句人话（带坐标 / 相似度等关键信息）。</summary>
    public static string Describe(Probe probe, VarValue value)
    {
        switch (probe.Kind)
        {
            case ProbeKind.image:
                return value.Flag == true
                    ? $"找到 · 相似度 {value.Score ?? 0:0.00} · 位置 {value.X}, {value.Y}"
                    : $"没找到 · 最高相似度 {value.Score ?? 0:0.00}";

            case ProbeKind.color:
                return $"该点颜色 {value.Text}";

            case ProbeKind.ocr:
                return value.Text.Length == 0
                    ? "没有识别到文字"
                    : $"识别到 {value.Text.Split('\n').Length} 行：{Brief(value.Text)}";

            case ProbeKind.window:
                return value.Flag == true ? $"窗口存在：{Brief(value.Text)}" : "窗口不存在";

            case ProbeKind.process:
                return value.Flag == true ? $"进程正在运行（{value.Number ?? 0:0} 个）" : "进程未运行";

            case ProbeKind.cursor:
                return $"光标位置 {value.X}, {value.Y}";

            case ProbeKind.selection:
            case ProbeKind.clipboard:
                return value.Text.Length == 0 ? "读取成功，但内容为空" : $"读到：{Brief(value.Text)}";

            default:
                return value.Text.Length == 0 ? "结果为空" : $"结果：{Brief(value.Text)}";
        }
    }

    private static string Brief(string text)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 160 ? text : text[..160] + "…";
    }
}
