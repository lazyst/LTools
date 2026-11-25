using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LTools.Core.SelfTest;

internal sealed record SelfTestCase(string Group, string Name, Action Body);

/// <summary>
/// <c>LTools.exe --selftest</c>：仓库内的回归入口（跑完用退出码报告结果）。
/// </summary>
/// <remarks>
/// <para>
/// 之所以做成程序内的命令行开关（而不是新建测试工程）：主项目在仓库根，默认 <c>**/*.cs</c>
/// 会把 <c>tools/**</c> 编进主程序；跨工程又拿不到 <c>internal</c>（要加 <c>InternalsVisibleTo</c>）。
/// 这里与 <c>--perf</c> / <c>--mcp-stdio</c> 同一套「分流 + <c>Shutdown(exitCode)</c>」模式，
/// CI 直接跑构建产物。决策与验收见 <c>docs/ACTION_SYSTEM_OPTIMIZATION.md</c> P0。
/// </para>
/// <para>
/// 约定：只跑纯逻辑——不创建窗口、不装钩子、不碰键鼠、不发网络请求、不读写用户配置。
/// 需要真实键盘 / 屏幕的验收仍然走真机点一遍。
/// </para>
/// </remarks>
internal static class SelfTestRunner
{
    /// <summary>命令行开关。</summary>
    public const string Switch = "--selftest";

    public static bool IsRequested(string[] args) =>
        Array.Exists(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        string? filter = ArgValue(args, "--filter");
        bool verbose = HasFlag(args, "--verbose");
        string reportPath = ArgValue(args, "--out") ?? DefaultReportPath();

        EnsureStdout();
        Check.Reset();

        var cases = new List<SelfTestCase>();
        EngineSuite.Register(cases);
        ModelSuite.Register(cases);
        ValidatorSuite.Register(cases);
        StepTextSuite.Register(cases);
        HelperSuite.Register(cases);
        TemplateSuite.Register(cases);
        DeclarationSuite.Register(cases);
        PuritySuite.Register(cases);
        StructureSuite.Register(cases);
        CSharpSuite.Register(cases);
        S2Suite.Register(cases);
        S3ASuite.Register(cases);
        S3BSuite.Register(cases);
        CategoryNavSuite.Register(cases);
        MenuConeSuite.Register(cases);
        PanelClipboardSuite.Register(cases);
        PanelDropSuite.Register(cases);
        TileLayoutSuite.Register(cases);
        LinkSuite.Register(cases);
        TableSuite.Register(cases);
        UiaPickSuite.Register(cases);
        SnapTreeSuite.Register(cases);
        KnowledgeSuite.Register(cases);
        InfoHelperSuite.Register(cases);
        CaptureProSuite.Register(cases);
        ShareSuite.Register(cases);
        ListSuite.Register(cases);
        NoteSuite.Register(cases);
        MarkdownSuite.Register(cases);
        JsonSuite.Register(cases);
        ContractSuite.Register(cases);
        ProcessSuite.Register(cases);
        SettingsSearchSuite.Register(cases);

        var lines = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        int executed = 0;

        foreach (SelfTestCase testCase in cases)
        {
            if (!Matches(testCase, filter)) continue;
            executed++;
            Check.Current = $"[{testCase.Group}] {testCase.Name}";
            int failedBefore = Check.Failed;
            try
            {
                testCase.Body();
            }
            catch (Exception ex)
            {
                string where = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
                Check.Fail("用例抛异常", $"{ex.GetType().Name}: {ex.Message}" + (where.Length == 0 ? "" : " @ " + where));
            }

            if (Check.Failed > failedBefore) lines.Add("FAIL " + Check.Current);
            else if (verbose) lines.Add("ok   " + Check.Current);
        }

        if (executed == 0)
        {
            Check.Current = "";
            Check.Fail("过滤条件没有命中任何用例", filter ?? "(空)");
        }

        stopwatch.Stop();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        var summary = new List<string>
        {
            $"LTools 自测 · v{version} · {RuntimeInformation.FrameworkDescription}",
            $"{lines.Count(l => l.StartsWith("FAIL", StringComparison.Ordinal))} 个用例失败 / 共 {executed} 个",
            $"断言 通过 {Check.Passed} / 失败 {Check.Failed} · 耗时 {stopwatch.ElapsedMilliseconds} ms",
        };

        if (Check.Failures.Count > 0)
        {
            summary.Add("");
            summary.Add("失败明细：");
            summary.AddRange(Check.Failures.Select(f => "  · " + f));
        }

        if (Check.Notes.Count > 0)
        {
            summary.Add("");
            summary.Add("提示：");
            summary.AddRange(Check.Notes.Select(n => "  · " + n));
        }

        var report = new StringBuilder();
        foreach (string line in lines.Concat(summary)) report.AppendLine(line);
        TryWriteReport(reportPath, report.ToString());

        Write(lines.Concat(summary).ToList());
        Console.WriteLine("报告：" + reportPath);

        return Check.Failed == 0 ? 0 : 1;
    }

    private static bool Matches(SelfTestCase testCase, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return testCase.Group.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || testCase.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultReportPath() =>
        Path.Combine(Path.GetTempPath(), "ltools-selftest", "report.txt");

    /// <summary>取 <c>--out=path</c> / <c>--out path</c> 的值。</summary>
    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    private static void TryWriteReport(string path, string text)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }
        catch
        {
            // 报告写不出来不影响退出码（CI 只看退出码）
        }
    }

    /// <summary>
    /// WinExe 默认没有控制台：挂到父进程（pwsh / CI）的控制台上再写，
    /// 否则 <c>Console.WriteLine</c> 什么也看不到。挂不上就只留报告文件。
    /// </summary>
    private static void EnsureStdout()
    {
        try
        {
            if (!AttachConsole(-1)) return;
            Stream stdout = Console.OpenStandardOutput();
            Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        }
        catch
        {
            // 没有父控制台（例如双击运行）：安静降级
        }
    }

    private static void Write(List<string> lines)
    {
        try
        {
            foreach (string line in lines) Console.WriteLine(line);
        }
        catch
        {
            // 输出流不可用：报告文件已经写了
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);
}
