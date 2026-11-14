using System.Text.Json;
using LTools.Config;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.SelfTest;

/// <summary>步骤文本（DSL）：写出 → 读回 → 再写出一致；错误给行号。</summary>
internal static class StepTextSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("steptext", "完整往返", RoundTrip));
        cases.Add(new("steptext", "缩进错误报行号", IndentError));
        cases.Add(new("steptext", "孤立的否则", OrphanElse));
        cases.Add(new("steptext", "注释与空行忽略", CommentsIgnored));
        cases.Add(new("steptext", "未知单元原样保留", UnknownUnit));
        cases.Add(new("steptext", "值里的井号不切注释", HashInsideValue));
        cases.Add(new("steptext", "重试属性往返与错误", RetryAttribute));
        cases.Add(new("steptext", "真实动作库逐份往返", RealActionLibrary));
    }

    private static void RoundTrip()
    {
        List<Step> steps = Fixture();
        string text = StepText.Write(steps);

        Check.Contains(text, "@note=", "备注应写成属性");
        Check.Contains(text, "@onFail=continue", "失败策略应写成属性");
        Check.Contains(text, "@delay=50", "步骤延迟应写成属性");
        Check.Contains(text, "@retry=2", "重试次数应写成属性");
        Check.Contains(text, "@retryDelay=800", "重试间隔应写成属性");
        Check.Contains(text, "@disabled", "跳过应写成属性");
        Check.Contains(text, "\n  ", "子步骤应缩进");

        Check.True(StepText.TryParse(text, out List<Step> back, out string? error), "应能读回：" + error);
        Check.Equal(text, StepText.Write(back), "读回再写出应与原文一字不差");
        Check.Equal(4, back.Count, "顶层步骤数");
        Check.Equal(1, back[0].Then.Count, "满足时分支");
        Check.Equal(1, back[0].Else.Count, "否则分支");
        Check.Equal("带 \"引号\" 与\n换行", back[1].Note, "备注里的引号与换行应原样返回");
        Check.Equal(StepArgKind.inline, back[3].In["value"].Kind, "内联取值应保留");
    }

    private static void IndentError()
    {
        bool ok = StepText.TryParse(
            "flow.set name=\"v\" value=\"1\"\n  flow.set name=\"v\" value=\"2\"\n",
            out _, out string? error);

        Check.False(ok, "没有子步骤的单元下面出现缩进行应报错");
        Check.Contains(error, "第 2 行");
    }

    private static void OrphanElse()
    {
        bool ok = StepText.TryParse("else\n", out _, out string? error);

        Check.False(ok, "孤立的否则应报错");
        Check.Contains(error, "第 1 行");
    }

    private static void CommentsIgnored()
    {
        const string text = "# 这一行是注释\n\nflow.set name=\"v\" value=\"1\"   # 行尾注释\n";
        bool ok = StepText.TryParse(text, out List<Step> steps, out string? error);

        Check.True(ok, "注释与空行应被忽略：" + error);
        Check.Equal(1, steps.Count, "只应有一步");
        Check.Equal("1", steps[0].In["value"].Literal.AsText(), "行尾注释不该进入值");
    }

    private static void UnknownUnit()
    {
        const string text = "my.custom a=1\n";
        Check.True(StepText.TryParse(text, out List<Step> steps, out string? error), "未知单元应能读：" + error);
        Check.Equal("my.custom", steps[0].Unit, "未知单元名保留");
        Check.Equal("my.custom a=1\n", StepText.Write(steps), "未知单元应原样写出");
    }

    private static void HashInsideValue()
    {
        const string text = "flow.set name=\"v\" value=\"a#b\"\n";
        Check.True(StepText.TryParse(text, out List<Step> steps, out string? error), "应能读：" + error);
        Check.Equal("a#b", steps[0].In["value"].Literal.AsText(), "引号里的 # 是值的一部分");
    }

    /// <summary>
    /// 本机有动作库就逐份往返（CI 里 <c>actions/</c> 为空，自动跳过）。
    /// 这是「真实数据」那条断言：语法改坏了这里最先红。
    /// </summary>
    private static void RealActionLibrary()
    {
        string dir = Path.Combine(AppPaths.DataRoot, "actions");
        if (!Directory.Exists(dir))
        {
            Check.Note("动作库为空，跳过真实动作往返（CI 里没有 actions/ 是正常的）");
            return;
        }

        int checkedActions = 0;
        foreach (string actionDir in Directory.GetDirectories(dir))
        {
            string file = Path.Combine(actionDir, "action.json");
            if (!File.Exists(file)) continue;

            ActionDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<ActionDto>(File.ReadAllText(file), ConfigBundle.Options);
            }
            catch (Exception ex)
            {
                Check.Note($"跳过坏动作文件 {Path.GetFileName(actionDir)}：{ex.Message}");
                continue;
            }

            if (dto?.Program == null) continue;

            string text = StepText.Write(dto.Program.Steps);
            if (!StepText.TryParse(text, out List<Step> back, out string? error))
            {
                Check.Fail("真实动作往返解析失败", $"{Path.GetFileName(actionDir)}：{error}");
                continue;
            }

            Check.Equal(text, StepText.Write(back), "真实动作往返：" + Path.GetFileName(actionDir));
            checkedActions++;
        }

        Check.Note($"真实动作库往返 {checkedActions} 份");
    }

    private static void RetryAttribute()
    {
        Check.True(StepText.TryParse("test.mark @retry=3 @retryDelay=200\n",
            out List<Step> steps, out string? error), "应能读：" + error);
        Check.Equal(3, steps[0].Retry, "重试次数");
        Check.Equal(200, steps[0].RetryDelayMs, "重试间隔");
        Check.Equal("test.mark @retry=3 @retryDelay=200\n", StepText.Write(steps), "写回应与原文一致");

        Check.False(StepText.TryParse("test.mark @retry=abc\n", out _, out string? bad),
            "重试次数写成非整数应报错");
        Check.Contains(bad, "@retry");

        Check.True(StepText.TryParse("test.mark\n", out List<Step> plain, out _), "应能读");
        Check.False(StepText.Write(plain).Contains("@retry"), "没设重试时不写重试属性");
    }

    // ============ 工具 ============
    private static List<Step> Fixture()
    {
        Step branch = Step.Of("flow.if")
            .With("left", StepArg.FromText("a"))
            .WithText("op", "equals")
            .With("right", StepArg.FromText("b"));
        branch.Then.Add(Step.Of("flow.set").WithText("name", "v").WithText("value", "1"));
        branch.Else.Add(Step.Of("flow.set").WithText("name", "v").WithText("value", "2"));

        Step loop = Step.Of("flow.repeat").With("count", StepArg.FromNumber(3));
        loop.Body.Add(Step.Of("input.mouse")
            .WithText("operation", "click")
            .With("x", StepArg.FromNumber(10))
            .With("y", StepArg.FromNumber(20)));
        loop.Body.Add(Step.Of("test.echo").WithText("text", "多行\n文本"));
        loop.Note = "带 \"引号\" 与\n换行";
        loop.OnFail = FailMode.@continue;
        loop.DelayMs = 50;
        loop.Retry = 2;
        loop.RetryDelayMs = 800;
        loop.Disabled = true;

        Step inline = Step.Of("flow.set")
            .WithText("name", "v")
            .With("value", StepArg.FromInline(Step.Of("test.echo").WithText("text", "hi")));

        return new List<Step> { branch, loop, Step.Of("flow.delay").With("ms", StepArg.FromNumber(100)), inline };
    }
}
