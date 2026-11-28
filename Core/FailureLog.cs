using System.Runtime.InteropServices;
using System.Text;
using LTools.Core.Actions;

namespace LTools.Core;

/// <summary>
/// 失败报告：把「最近一次动作失败」的轨迹与相关日志写成 <c>logs/last-failure.log</c>。
/// </summary>
/// <remarks>
/// <para>
/// 日志一天一个文件、多个动作交错，事后很难从几千行里捞出「这次到底跑了哪几行」；
/// 而 <see cref="StepTrace"/>（逐步耗时 / 解析后的参数 / 输出 / 失败位置）原本只有编辑器试运行与 MCP
/// 拿得到，跑完就没了。这里把两者合起来单独落一份：用户点失败提示直接打开它，
/// 贴给 AI 的也是这一份。
/// </para>
/// <para>
/// 每次失败覆盖写，滚动保留最近 <see cref="Keep"/> 份（<c>last-failure.1.log</c> 起顺延），
/// 不按时间堆一堆文件——那样又回到「一堆文件里找哪一个」。
/// </para>
/// </remarks>
internal static class FailureLog
{
    /// <summary>保留几份 `last-failure.*.log` 轮转副本（含最新的 <c>last-failure.log</c>）。</summary>
    public const int Keep = 5;

    /// <summary>每运行一份报告，最多留这么多份。</summary>
    public const int KeepPerRun = 20;

    private static readonly object Gate = new();

    /// <summary>最新一份报告的路径；失败提示在拿不到本次运行自己的报告时退回它。</summary>
    public static string LatestPath => Path.Combine(AppPaths.LogsDir, "last-failure.log");

    /// <summary>每运行一份报告放这里（<c>logs/failures/&lt;runId&gt;.log</c>）。</summary>
    public static string Dir => Path.Combine(AppPaths.LogsDir, "failures");

    /// <summary>某次运行的报告路径（还没写过也可能返回路径）。</summary>
    public static string PathFor(string runId) =>
        Path.Combine(Dir, SafeName(runId) + ".log");

    /// <summary>
    /// 写一份失败报告（失败时调用；写不出来只记一条警告，不影响失败提示）。
    /// </summary>
    /// <returns>本次运行自己的报告路径；写失败时退回 <see cref="LatestPath"/>。</returns>
    public static string Write(string runId, string actionName, ActionRunResult result,
        IReadOnlyList<StepTrace> traces)
    {
        string path = PathFor(runId);
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                AppLog.Flush();   // 先让日志落盘，报告里的「相关日志」才不会落后几行
                string text = Build(runId, actionName, result, traces);

                // 每运行一份：并发两个失败不会互相覆盖，toast 点开的就是自己那一份。
                File.WriteAllText(path, text, Utf8NoBom);

                // 最新那份仍然复制到 last-failure.log（现有文案 / 入口都指向它），旧副本顺延。
                Shift();
                File.WriteAllText(LatestPath, text, Utf8NoBom);

                PrunePerRun();
            }
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Flow", $"写失败报告失败：{ex.Message}");
            return LatestPath;
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static string SafeName(string runId)
    {
        var sb = new StringBuilder(runId.Length);
        foreach (char ch in runId)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        string name = sb.ToString();
        return name.Length == 0 ? "run" : name;
    }

    /// <summary>每运行一份的报告只留最近 <see cref="KeepPerRun"/> 份（按写入时间）。</summary>
    private static void PrunePerRun()
    {
        try
        {
            string[] files = Directory.GetFiles(Dir, "*.log");
            if (files.Length <= KeepPerRun) return;

            foreach (string file in files
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(KeepPerRun))
            {
                try { File.Delete(file); } catch { /* 被占用就下次再说 */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Debug("Flow", $"清理旧失败报告失败：{ex.Message}");
        }
    }

    private static string Build(string runId, string actionName, ActionRunResult result,
        IReadOnlyList<StepTrace> traces)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# LTools 动作失败报告");
        sb.AppendLine();
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"运行: {runId}");
        sb.AppendLine($"动作: {actionName}");
        if (!string.IsNullOrWhiteSpace(result.FailedStepLabel))
            sb.AppendLine($"位置: {result.FailedStepLabel}");
        if (result.ErrorKind != ActionErrorKind.unknown && result.ErrorKind != ActionErrorKind.aborted)
            sb.AppendLine($"性质: {ActionErrors.Label(result.ErrorKind)}");
        sb.AppendLine($"错误: {result.Error ?? "（无错误文本）"}");
        sb.AppendLine($"步数: {result.Steps} · 耗时: {result.Elapsed.TotalMilliseconds:0} ms");

        sb.AppendLine();
        sb.AppendLine("## 环境");
        sb.AppendLine($"版本: {Version()}");
        sb.AppendLine($"系统: {RuntimeInformation.OSDescription} · {(Environment.Is64BitProcess ? "64 位" : "32 位")}");
        sb.AppendLine($"运行时: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"数据根: {AppPaths.DataRoot}");
        sb.AppendLine($"日志: {(AppLog.Enabled ? "开启" : "关闭")} · 级别 {AppLog.Level}");

        sb.AppendLine();
        sb.AppendLine($"## 轨迹（{traces.Count} 步）");
        if (traces.Count == 0) sb.AppendLine("（没有轨迹：失败发生在第一步之前，或引擎没开轨迹）");
        foreach (var trace in traces)
            AppendTrace(sb, trace);

        AppendRelatedLog(sb, runId);
        return sb.ToString();
    }

    private static void AppendTrace(StringBuilder sb, StepTrace trace)
    {
        string status = trace.Status switch
        {
            StepTraceStatus.ok => "ok",
            StepTraceStatus.failed => "FAILED",
            StepTraceStatus.continued => "继续",
            _ => "中止",
        };

        sb.AppendLine($"  {status,-6} {trace.ElapsedMs,6} ms  {trace.Label}  {trace.UnitName}");
        if (trace.Error is { Length: > 0 })
            sb.AppendLine($"         └ {trace.Error}");

        // 参数 / 输出只在失败那一步展开：成功的步骤输出可能很大，报告要能一眼看完。
        // 参数是**变量展开后**的值（<see cref="StepTrace.Inputs"/>），排查时看的就该是这个。
        if (trace.Status != StepTraceStatus.failed) return;

        int shownInputs = 0;
        foreach (var (key, value) in trace.Inputs)
        {
            if (shownInputs++ >= 8)
            {
                sb.AppendLine($"         └ 参数…（还有 {trace.Inputs.Count - 8} 项）");
                break;
            }
            sb.AppendLine($"         └ {key} = {Shorten(SafeText(value))}");
        }

        if (trace.Outputs.Count == 0) return;

        int shown = 0;
        foreach (var (key, value) in trace.Outputs)
        {
            if (shown++ >= 12)
            {
                sb.AppendLine($"         └ …（还有 {trace.Outputs.Count - 12} 项）");
                break;
            }
            sb.AppendLine($"         └ {key} = {Shorten(SafeText(value))}");
        }
    }

    private static string SafeText(Value value)
    {
        try { return value.AsText(); }
        catch { return "(无法显示)"; }
    }

    /// <summary>按 run id 过滤今天日志里的相关行（最多 200 行），放在报告末尾。</summary>
    private static void AppendRelatedLog(StringBuilder sb, string runId)
    {
        string file = Path.Combine(AppPaths.LogsDir, $"ltools-{DateTime.Now:yyyyMMdd}.log");
        string[] lines = LogFiles.ReadTail(file, 5000);

        // 多行异常栈 / 多行内容没有时间戳，跟着它上面那条记录一起留（否则报告里只剩半截）。
        var related = LogFiles.KeepEntries(lines, l => l.Contains($"[{runId}]", StringComparison.Ordinal));
        if (related.Count > 200) related = related.TakeLast(200).ToList();

        sb.AppendLine();
        sb.AppendLine($"## 这次运行的相关日志（{related.Count} 行）");
        if (related.Count == 0)
        {
            sb.AppendLine("（没有相关行：运行日志已关闭、级别太高，或日志还没落盘）");
            return;
        }

        foreach (string line in related) sb.AppendLine(line);
    }

    /// <summary>滚动：删掉最旧一份，其余依次改名，给新的 <c>last-failure.log</c> 让位。</summary>
    private static void Shift()
    {
        if (File.Exists(Older(Keep - 1))) File.Delete(Older(Keep - 1));

        for (int n = Keep - 2; n >= 1; n--)
        {
            if (File.Exists(Older(n))) File.Move(Older(n), Older(n + 1), overwrite: true);
        }

        if (File.Exists(LatestPath)) File.Move(LatestPath, Older(1), overwrite: true);
    }

    private static string Older(int n) => Path.Combine(AppPaths.LogsDir, $"last-failure.{n}.log");

    private static string Version() =>
        typeof(FailureLog).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>单行化 + 截断：输出里的换行会把报告排版打散。</summary>
    private static string Shorten(string text)
    {
        string flat = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}
