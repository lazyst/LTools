using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Panel;

namespace LTools.Core.SelfTest;

/// <summary>
/// 简单动作的纯逻辑：目录、一步动作的构造 / 读回、默认名称、标记与「一步即用」。
/// 对话框与右键菜单靠手点与面板冒烟；这里只断言「给一组参数，建出来的是什么」。
/// </summary>
internal static class SimpleActionSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("simpleAction", "目录：七种预设，键唯一、单元都在", Catalog));
        cases.Add(new("simpleAction", "打开文件：一步 file.openFile + 路径", OpenFile));
        cases.Add(new("simpleAction", "打开文件夹：一步 file.explorer（operation=open）", OpenFolder));
        cases.Add(new("simpleAction", "启动程序：一步 app.launch + 参数", LaunchApp));
        cases.Add(new("simpleAction", "打开网址：一步 web.open", OpenUrl));
        cases.Add(new("simpleAction", "回放宏：一步 input.macro", ReplayMacro));
        cases.Add(new("simpleAction", "运行命令：窗口 / 后台两种方式", RunCommand));
        cases.Add(new("simpleAction", "模拟按键：一步 input.sendKeys（短按 + 修饰键）", SendKeys));
        cases.Add(new("simpleAction", "每种预设建出来的步骤都通过校验（回放宏只差宏文件）", AllKindsValidate));
        cases.Add(new("simpleAction", "读回：Build → ValuesOf 往返一致", RoundTrip));
        cases.Add(new("simpleAction", "默认名称：文件名 / 文件夹 / 网址主机 / 命令首行 / 组合键", DefaultNames));
        cases.Add(new("simpleAction", "编辑保留表单不管的参数（工作目录）", PreserveUntouched));
        cases.Add(new("simpleAction", "标记：Clone 带着走，未知标记 = 组合动作", Marker));
        cases.Add(new("simpleAction", "宏动作与简单动作标记共存（结构判定仍认得出）", MacroCoexists));
    }

    private static void Catalog()
    {
        Check.Equal(7, SimpleActions.Catalog.Count, "七种预设");
        Check.Equal(7, SimpleActions.Catalog.Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            "键不重复");
        foreach (var kind in SimpleActions.Catalog)
        {
            Check.False(string.IsNullOrWhiteSpace(kind.Name), $"{kind.Key} 有名称");
            Check.False(string.IsNullOrWhiteSpace(kind.Icon), $"{kind.Key} 有图标");
            Check.False(string.IsNullOrWhiteSpace(kind.Unit), $"{kind.Key} 有动作单元");
            Check.False(string.IsNullOrWhiteSpace(kind.Description), $"{kind.Key} 有说明");
        }

        Check.Equal(SimpleActions.OpenFile, SimpleActions.Find("openfile")?.Key, "Find 大小写不敏感");
        Check.True(SimpleActions.Find("nope") == null, "未知键返回 null");
    }

    private static void OpenFile()
    {
        Step step = BuildOne(SimpleActions.OpenFile, Values(("path", @"C:\Work\季度报表.xlsx")));

        Check.Equal("file.openFile", step.Unit, "用「打开文件」");
        Check.Equal(@"C:\Work\季度报表.xlsx", step.In["path"].Literal.AsText(), "path = 文件");
        Check.Equal(1, step.In.Count, "只有 path");
    }

    private static void OpenFolder()
    {
        Step step = BuildOne(SimpleActions.OpenFolder, Values(("path", @"C:\Work\项目A")));

        Check.Equal("file.explorer", step.Unit, "用「打开文件夹」");
        Check.Equal("open", step.In["operation"].Literal.AsText(), "operation=open");
        Check.Equal(@"C:\Work\项目A", step.In["path"].Literal.AsText(), "path = 文件夹");
    }

    private static void LaunchApp()
    {
        Step step = BuildOne(SimpleActions.LaunchApp,
            Values(("target", @"C:\Tools\app.exe"), ("args", "--fast")));

        Check.Equal("app.launch", step.Unit, "用「启动程序」");
        Check.Equal(@"C:\Tools\app.exe", step.In["target"].Literal.AsText(), "target = 程序");
        Check.Equal("--fast", step.In["args"].Literal.AsText(), "args = 参数");
    }

    private static void OpenUrl()
    {
        Step step = BuildOne(SimpleActions.OpenUrl, Values(("url", "https://example.com/page")));

        Check.Equal("web.open", step.Unit, "用「打开网址」");
        Check.Equal("https://example.com/page", step.In["url"].Literal.AsText(), "url 原样");
    }

    private static void ReplayMacro()
    {
        Step step = BuildOne(SimpleActions.ReplayMacro, Values(("macroId", "m1")));

        Check.Equal("input.macro", step.Unit, "用「回放宏」");
        Check.Equal("m1", step.In["macroId"].Literal.AsText(), "macroId 原样");
    }

    private static void RunCommand()
    {
        Step window = BuildOne(SimpleActions.RunCommand,
            Values(("command", "echo hi"), ("terminal", "cmd"), ("capture", "false")));

        Check.Equal("app.runCommand", window.Unit, "用「运行命令」");
        Check.Equal("echo hi", window.In["command"].Literal.AsText(), "command 原样");
        Check.Equal("cmd", window.In["terminal"].Literal.AsText(), "终端可选");
        Check.False(window.In["capture"].Literal.AsBool(), "窗口方式 = 不捕获");
        Check.True(window.In["keepWindow"].Literal.AsBool(), "窗口方式 = 保留窗口");

        Step capture = BuildOne(SimpleActions.RunCommand,
            Values(("command", "dir"), ("terminal", "direct"), ("capture", "true")));
        Check.True(capture.In["capture"].Literal.AsBool(), "后台方式 = 捕获");
        Check.False(capture.In["keepWindow"].Literal.AsBool(), "后台方式没有窗口");
    }

    private static void SendKeys()
    {
        Step step = BuildOne(SimpleActions.SendKeys, Values(("key", "s"), ("mods", "ctrl+shift")));

        Check.Equal("input.sendKeys", step.Unit, "用「模拟按键」");
        Check.Equal("s", step.In["key"].Literal.AsText(), "主键");
        Check.Equal("ctrl+shift", step.In["mods"].Literal.AsText(), "修饰键");
        Check.Equal("tap", step.In["action"].Literal.AsText(), "默认短按");
    }

    private static void AllKindsValidate()
    {
        foreach (var kind in SimpleActions.Catalog)
        {
            ActionProgram program = SimpleActions.Build(kind, Sample(kind));
            program.Name = kind.Name;
            ActionValidationResult result = ActionValidator.Validate(program);

            // 「回放宏」会去磁盘上找宏文件；自测不写用户数据，所以这里只接受「宏不存在」这一条。
            var errors = result.Issues
                .Where(i => i.Severity == IssueSeverity.error)
                .ToList();
            if (kind.Key == SimpleActions.ReplayMacro)
            {
                Check.Equal(1, errors.Count, "回放宏：只有「宏不存在」一条错误");
                Check.Contains(errors.FirstOrDefault()?.Message, "宏不存在", "回放宏：错在宏文件");
                errors.Clear();
            }

            Check.Equal(0, errors.Count, $"{kind.Name}：没有校验错误");
            if (errors.Count > 0)
            {
                foreach (var issue in errors.Take(3))
                    Check.Notes.Add($"{kind.Name}：{issue.Message}");
            }
        }
    }

    private static void RoundTrip()
    {
        foreach (var kind in SimpleActions.Catalog)
        {
            Dictionary<string, string> values = Sample(kind);
            ActionDto action = new()
            {
                Name = kind.Name,
                Icon = kind.Icon,
                SimpleKind = kind.Key,
                Program = SimpleActions.Build(kind, values),
            };

            Dictionary<string, string> read = SimpleActions.ValuesOf(kind, action);
            foreach (var pair in values)
            {
                string expected = pair.Value;
                // capture 在表单里是 true/false 文本，读回也是
                Check.Equal(expected, read.TryGetValue(pair.Key, out string? got) ? got : "",
                    $"{kind.Name}：{pair.Key} 往返一致");
            }
        }
    }

    private static void DefaultNames()
    {
        Check.Equal("季度报表", Name(SimpleActions.OpenFile, ("path", @"C:\Work\季度报表.xlsx")), "文件去扩展名");
        Check.Equal("项目A", Name(SimpleActions.OpenFolder, ("path", @"C:\Work\项目A")), "文件夹名");
        Check.Equal("app", Name(SimpleActions.LaunchApp, ("target", @"C:\Tools\app.exe")), "程序去扩展名");
        Check.Equal("example.com", Name(SimpleActions.OpenUrl, ("url", "https://example.com/page")), "网址主机");
        Check.Equal("echo hi", Name(SimpleActions.RunCommand, ("command", "echo hi")), "命令首行");
        Check.Equal("Ctrl+Shift+S",
            Name(SimpleActions.SendKeys, ("key", "s"), ("mods", "ctrl+shift")), "组合键");
        Check.Equal("我的宏", SimpleActions.DefaultName(
            SimpleActions.Find(SimpleActions.ReplayMacro)!, new Dictionary<string, string>(), "我的宏"),
            "宏名优先");
    }

    private static void PreserveUntouched()
    {
        var first = new ActionProgram();
        first.Steps.Add(Step.Of("file.openFile")
            .WithText("path", @"C:\a\one.txt")
            .WithText("workdir", @"C:\a"));

        ActionProgram rebuilt = SimpleActions.Build(
            SimpleActions.Find(SimpleActions.OpenFile)!,
            Values(("path", @"C:\b\two.txt")),
            first);

        Step step = rebuilt.Steps.Single();
        Check.Equal(@"C:\b\two.txt", step.In["path"].Literal.AsText(), "路径换成新的");
        Check.Equal(@"C:\a", step.In["workdir"].Literal.AsText(), "工作目录保留");
    }

    private static void Marker()
    {
        var action = new ActionDto
        {
            Id = "a1",
            Name = "打开报表",
            SimpleKind = SimpleActions.OpenFile,
            Program = SimpleActions.Build(SimpleActions.Find(SimpleActions.OpenFile)!,
                Values(("path", @"C:\a.xlsx"))),
        };

        ActionDto copy = action.Clone();
        Check.Equal(SimpleActions.OpenFile, copy.SimpleKind, "Clone 带着标记");
        Check.Equal(SimpleActions.OpenFile, SimpleActions.KindOf(copy)?.Key, "KindOf 认得出");

        copy.SimpleKind = null;
        Check.True(SimpleActions.KindOf(copy) == null, "清掉标记 = 组合动作");

        copy.SimpleKind = "unknown";
        Check.True(SimpleActions.KindOf(copy) == null, "未知标记不算简单动作");
    }

    private static void MacroCoexists()
    {
        var action = new ActionDto
        {
            Name = "回放宏",
            SimpleKind = SimpleActions.ReplayMacro,
            Program = MacroAction.ProgramFor("m1"),
        };

        Check.Equal("m1", MacroAction.MacroIdOf(action), "结构判定仍认得出宏");
        Check.Equal(SimpleActions.ReplayMacro, SimpleActions.KindOf(action)?.Key, "同时是简单动作");
    }

    // ============ 样例 ============

    private static Step BuildOne(string key, Dictionary<string, string> values)
    {
        var kind = SimpleActions.Find(key);
        Check.True(kind != null, $"找到预设「{key}」");
        ActionProgram program = SimpleActions.Build(kind!, values);
        Check.Equal(1, program.Steps.Count, $"{kind!.Name}：只有一步");
        return program.Steps[0];
    }

    private static Dictionary<string, string> Sample(SimpleActions.Kind kind) => kind.Key switch
    {
        SimpleActions.OpenFile => Values(("path", @"C:\Work\季度报表.xlsx")),
        SimpleActions.OpenFolder => Values(("path", @"C:\Work\项目A")),
        SimpleActions.LaunchApp => Values(("target", @"C:\Tools\app.exe"), ("args", "--fast")),
        SimpleActions.OpenUrl => Values(("url", "https://example.com/page")),
        SimpleActions.ReplayMacro => Values(("macroId", "m1")),
        SimpleActions.RunCommand => Values(("command", "echo hi"), ("terminal", "cmd"), ("capture", "false")),
        SimpleActions.SendKeys => Values(("key", "s"), ("mods", "ctrl+shift")),
        _ => Values(),
    };

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) values[key] = value;
        return values;
    }

    private static string Name(string key, params (string Key, string Value)[] pairs) =>
        SimpleActions.DefaultName(SimpleActions.Find(key)!, Values(pairs));
}
