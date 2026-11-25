using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Panel;

namespace LTools.Core.SelfTest;

/// <summary>
/// 面板「从文件 / 文件夹创建动作」的纯逻辑：名称、图标、步骤参数、去重、跳过、排序。
/// 落位与落盘在 <see cref="PanelDrop.Insert"/> 里，靠手点与面板冒烟；这里只断言「一个路径变成什么动作」。
/// </summary>
internal static class PanelDropSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("panelDrop", "文件 → 1 步「打开文件」（路径 / 工作目录 / 名称 / 图标）", FileAction));
        cases.Add(new("panelDrop", "文件夹 → 1 步「打开文件夹」", FolderAction));
        cases.Add(new("panelDrop", "文件夹排在文件前面，其余按输入顺序", Order));
        cases.Add(new("panelDrop", "同一批里重复的路径只留一个（大小写不敏感）", BatchDuplicates));
        cases.Add(new("panelDrop", "已经有动作的路径 → 跳过并计数", Known));
        cases.Add(new("panelDrop", "不存在的路径 → 跳过并计数", Missing));
        cases.Add(new("panelDrop", "真路径：文件夹不会被当成找不到（默认探测）", RealProbe));
        cases.Add(new("panelDrop", "路径带引号 / 末尾分隔符也能认", Quoted));
        cases.Add(new("panelDrop", "扩展名 → 图标", Icons));
        cases.Add(new("panelDrop", "名称去扩展名（.gitignore / 无扩展名 / 文件夹名）", Names));
        cases.Add(new("panelDrop", "规范化：去末尾分隔符、盘符根保留", Normalize));
        cases.Add(new("panelDrop", "空输入 → 什么都不建", EmptyInput));
    }

    private static void FileAction()
    {
        PanelDrop.Result result = BuildOne(@"C:\Work\季度报表.xlsx", isDirectory: false);

        Check.Equal(1, result.Actions.Count, "建了 1 个动作");
        ActionDto action = result.Actions[0];
        Check.Equal("季度报表", action.Name, "名称去扩展名");
        Check.Equal("file", action.Icon, "xlsx 用通用文件图标");
        Check.Equal(1, action.Program?.Steps.Count ?? 0, "只有 1 步");

        Step step = action.Program!.Steps[0];
        Check.Equal("file.openFile", step.Unit, "用「打开文件」");
        Check.Equal(@"C:\Work\季度报表.xlsx", step.In["path"].Literal.AsText(), "path = 绝对路径");
        Check.Equal(@"C:\Work", step.In["workdir"].Literal.AsText(), "workdir = 所在目录");
        Check.Equal(2, step.In.Count, "只有 path / workdir 两个参数");
        Check.Equal(@"C:\Work\季度报表.xlsx", action.Program.Description, "说明里放全路径");
        Check.Equal(action.Name, action.Program.Name, "动作体名称与外层一致");
        Check.Equal(SimpleActions.OpenFile, action.SimpleKind, "是简单动作（打开文件）");
    }

    private static void FolderAction()
    {
        PanelDrop.Result result = BuildOne(@"C:\Work\项目A", isDirectory: true);

        Check.Equal(1, result.Actions.Count, "建了 1 个动作");
        ActionDto action = result.Actions[0];
        Check.Equal("项目A", action.Name, "名称 = 文件夹名");
        Check.Equal("folder", action.Icon, "文件夹图标");

        Step step = action.Program!.Steps[0];
        Check.Equal("file.explorer", step.Unit, "用「打开文件夹」");
        Check.Equal("open", step.In["operation"].Literal.AsText(), "operation=open");
        Check.Equal(@"C:\Work\项目A", step.In["path"].Literal.AsText(), "path = 目录");
        Check.Equal(2, step.In.Count, "只有 operation / path");
        Check.Equal(SimpleActions.OpenFolder, action.SimpleKind, "是简单动作（打开文件夹）");
    }

    private static void Order()
    {
        var fs = new FakeFs
        {
            [@"C:\a\one.txt"] = false,
            [@"C:\a\folder"] = true,
            [@"C:\a\two.txt"] = false,
            [@"C:\a\another"] = true,
        };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\a\one.txt", @"C:\a\folder", @"C:\a\two.txt", @"C:\a\another" },
            probe: fs.Probe);

        Check.Equal(4, result.Actions.Count, "4 个都建了");
        Check.Equal("folder", result.Actions[0].Name, "文件夹在前（1）");
        Check.Equal("another", result.Actions[1].Name, "文件夹在前（2）");
        Check.Equal("one", result.Actions[2].Name, "文件按输入顺序（1）");
        Check.Equal("two", result.Actions[3].Name, "文件按输入顺序（2）");
    }

    private static void BatchDuplicates()
    {
        var fs = new FakeFs { [@"C:\a\one.txt"] = false };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\a\one.txt", @"C:\a\one.txt", @"c:\A\ONE.TXT" },
            probe: fs.Probe);

        Check.Equal(1, result.Actions.Count, "同一路径只建一个");
        Check.Equal(0, result.Duplicates.Count, "批内重复不算「已有动作」");
    }

    private static void Known()
    {
        var fs = new FakeFs { [@"C:\a\one.txt"] = false, [@"C:\a\two.txt"] = false };
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"c:\a\one.txt" };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\a\one.txt", @"C:\a\two.txt" },
            known.Contains, fs.Probe);

        Check.Equal(1, result.Actions.Count, "只建没有的那个");
        Check.Equal("two", result.Actions[0].Name, "留下的是 two");
        Check.Equal(1, result.Duplicates.Count, "已有的记一条");
    }

    private static void Missing()
    {
        var fs = new FakeFs { [@"C:\a\one.txt"] = false };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\a\one.txt", @"C:\a\gone.txt" }, probe: fs.Probe);

        Check.Equal(1, result.Actions.Count, "只建存在的");
        Check.Equal(1, result.Missing.Count, "找不到的记一条");
        Check.True(result.Missing[0].EndsWith("gone.txt", StringComparison.OrdinalIgnoreCase), "记的是找不到的那条");
    }

    private static void RealProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-panel-drop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            string folder = Path.Combine(root, "目录");
            Directory.CreateDirectory(folder);
            string file = Path.Combine(root, "a.txt");
            File.WriteAllText(file, "x");

            PanelDrop.Result result = PanelDrop.Build(new[] { folder, file });
            Check.Equal(2, result.Actions.Count, "目录 + 文件都认");
            Check.Equal(0, result.Missing.Count, "没有找不到的");
            Check.Equal(0, result.Duplicates.Count, "也没有重复");
            Check.Equal("folder", result.Actions[0].Icon, "目录排在前、用文件夹图标");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    private static void Quoted()
    {
        var fs = new FakeFs { [@"C:\a\one.txt"] = false };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { "  \"C:\\a\\one.txt\"  ", @"C:\a\" }, probe: fs.Probe);

        // 第二项是目录 C:\a，FakeFs 只登记了文件 → 找不到；这里只验第一项被正确剥引号。
        Check.Equal(1, result.Actions.Count, "带引号的路径能认");
        Check.Equal(@"C:\a\one.txt", result.Actions[0].Program!.Steps[0].In["path"].Literal.AsText(), "路径已规范化");
    }

    private static void Icons()
    {
        var fs = new FakeFs
        {
            [@"C:\x\app.exe"] = false,
            [@"C:\x\shot.PNG"] = false,
            [@"C:\x\readme.md"] = false,
            [@"C:\x\run.ps1"] = false,
            [@"C:\x\link.lnk"] = false,
            [@"C:\x\page.html"] = false,
            [@"C:\x\data.bin"] = false,
        };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\x\app.exe", @"C:\x\shot.PNG", @"C:\x\readme.md", @"C:\x\run.ps1", @"C:\x\link.lnk", @"C:\x\page.html", @"C:\x\data.bin" },
            probe: fs.Probe);

        var icons = result.Actions.Select(a => a.Icon).ToArray();
        Check.Equal("app", icons[0], "exe → app");
        Check.Equal("image", icons[1], "png → image（大小写不敏感）");
        Check.Equal("note", icons[2], "md → note");
        Check.Equal("script", icons[3], "ps1 → script");
        Check.Equal("link", icons[4], "lnk → link");
        Check.Equal("globe", icons[5], "html → globe");
        Check.Equal("file", icons[6], "未知扩展名 → file");
    }

    private static void Names()
    {
        var fs = new FakeFs
        {
            [@"C:\x\.gitignore"] = false,
            [@"C:\x\LICENSE"] = false,
            [@"C:\x\dir.v2"] = true,
        };

        PanelDrop.Result result = PanelDrop.Build(
            new[] { @"C:\x\.gitignore", @"C:\x\LICENSE", @"C:\x\dir.v2" }, probe: fs.Probe);

        Check.Equal(".gitignore", result.Actions.Single(a => a.Name.Contains("gitignore")).Name, ".gitignore 原样保留");
        Check.Equal("LICENSE", result.Actions.Single(a => a.Name == "LICENSE").Name, "无扩展名原样");
        Check.Equal("dir.v2", result.Actions.Single(a => a.Icon == "folder").Name, "文件夹名不剥扩展名");
    }

    private static void Normalize()
    {
        Check.Equal(@"C:\a\b", PanelDrop.Normalize(@" C:\a\b\ "), "末尾分隔符去掉");
        Check.Equal(@"C:\", PanelDrop.Normalize(@"C:\"), "盘符根保留");
        Check.Equal(@"C:\a\upper", PanelDrop.Normalize(@"c:\A\..\a\upper"), "相对段被规范化");
    }

    private static void EmptyInput()
    {
        PanelDrop.Result result = PanelDrop.Build(Array.Empty<string>());
        Check.True(result.Empty, "空输入什么都不建");
        Check.Equal(0, result.Missing.Count + result.Duplicates.Count, "也没有跳过项");
    }

    private static PanelDrop.Result BuildOne(string path, bool isDirectory) =>
        PanelDrop.Build(new[] { path }, probe: new FakeFs { [path] = isDirectory }.Probe);

    /// <summary>假文件系统：只认登记过的路径（自测不碰真实磁盘）。</summary>
    private sealed class FakeFs : Dictionary<string, bool>
    {
        public (bool Exists, bool IsDirectory) Probe(string path) =>
            TryGetValue(path, out bool dir) ? (true, dir) : (false, false);
    }
}
