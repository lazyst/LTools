using System.Diagnostics;
using System.IO;
using LTools.Core.Actions;
using LTools.Features.Units.Apps;

namespace LTools.Core.SelfTest;

/// <summary>
/// <c>app.process</c> 的 <c>kill</c>：杀完读回真实状态（<c>docs/UNIT_CONTRACT_AUDIT.md</c> 建议 7）。
/// </summary>
/// <remarks>
/// 以前 <c>Kill</c> 外面套 <c>catch { }</c>，杀不掉也报成功。这里拿一份**自己名字的 PING 副本**
/// 当靶子（名字唯一，不会误伤别的进程；<c>CreateNoWindow</c> 不闪黑框）：
/// 杀完断言它真的没了，再杀一次断言「没有可杀的」也不算失败。
/// </remarks>
internal static class ProcessSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("process", "app.process kill 真的结束进程", KillReallyEndsProcess));
        cases.Add(new("process", "app.process 的 kill 说明了读回语义", KillDeclaration));
    }

    private static void KillReallyEndsProcess()
    {
        string name = "ltools-selftest-kill-" + Guid.NewGuid().ToString("N")[..8];
        string copy = Path.Combine(Path.GetTempPath(), name + ".exe");
        string source = Path.Combine(Environment.SystemDirectory, "PING.EXE");

        if (!File.Exists(source))
        {
            Check.Note("找不到 PING.EXE，跳过真实进程用例");
            Check.True(true, "跳过");
            return;
        }

        try
        {
            File.Copy(source, copy, overwrite: true);

            using (Process started = Process.Start(new ProcessStartInfo
                   {
                       FileName = copy,
                       Arguments = "-n 60 127.0.0.1",
                       CreateNoWindow = true,
                       UseShellExecute = false,
                   }) ?? throw new ActionException("测试进程起不来"))
            {
                // Process.Start 返回时可能还没登记进进程表，先等它出现。
                long deadline = Environment.TickCount64 + 5000;
                while (Count(name) == 0 && Environment.TickCount64 < deadline) Thread.Sleep(50);

                Check.Equal(1, Count(name), "靶子进程应已启动");

                var registry = TestRegistry.Create(new ProcessUnit());
                var program = new ActionProgram { Name = "结束进程" };
                program.Variables.Add(Var("count", ValueKind.number));
                program.Variables.Add(Var("found", ValueKind.@bool));
                program.Steps.Add(Step.Of("app.process").WithText("name", name)
                    .WithText("operation", "kill")
                    .Output("count", "count").Output("found", "found"));

                ActionRunResult killed = new ActionEngine(registry).Run(program);

                Check.True(killed.Ok, "结束进程该成功：" + killed.Error);
                Check.Equal(1d, killed.Variables["count"].AsNumber(), "杀之前匹配到 1 个");
                Check.Equal(true, killed.Variables["found"].AsBool(), "杀之前确实有它");
                Check.Equal(0, Count(name), "读回：进程真的没了");

                // 再杀一次：已经没有可杀的，不该报错（幂等）。
                ActionRunResult again = new ActionEngine(registry).Run(program);

                Check.True(again.Ok, "没进程可杀时不该失败：" + again.Error);
                Check.Equal(0d, again.Variables["count"].AsNumber(), "第二次匹配到 0 个");
                Check.Equal(false, again.Variables["found"].AsBool(), "found = false");
            }
        }
        finally
        {
            foreach (Process leftover in Process.GetProcessesByName(name))
            {
                try { leftover.Kill(entireProcessTree: true); } catch { /* 已经没了 */ }
                leftover.Dispose();
            }

            try { File.Delete(copy); } catch { /* 文件还被占着就留给系统清 */ }
        }
    }

    private static void KillDeclaration()
    {
        UnitSpec spec = StepRegistry.Default.Get("app.process").Spec;

        Check.True(spec.Description.Contains("退出"),
            "说明要写清「结束时等它真的退出」：" + spec.Description);

        string count = spec.Outputs.Single(o => o.Key == "count").Description ?? "";
        Check.True(count.Contains("结束"),
            "count 的说明要写清结束时是「发出结束请求之前的数量」：" + count);
    }

    private static int Count(string name)
    {
        Process[] list = Process.GetProcessesByName(name);
        try { return list.Length; }
        finally { foreach (Process process in list) process.Dispose(); }
    }

    private static VariableDef Var(string key, ValueKind kind, Value? value = null) =>
        new() { Key = key, Type = kind, Default = value };
}
