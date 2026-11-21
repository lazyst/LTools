using System.Text;
using System.Text.RegularExpressions;
using LTools.Features;

namespace LTools.Core.SelfTest;

/// <summary>
/// 速记数据层（<see cref="NoteRepository"/>）：<c>.md</c> 命名、标题 / 分类解析、同名避让、
/// 一次性 <c>.txt → .md</c> 迁移，以及「写速记」默认标题的 Markdown 记号剥离。
/// </summary>
/// <remarks>
/// 速记是用户资产：这些断言盯的是「文件别丢、别覆盖、别改名改歪」。
/// 全部跑在临时目录里，跑完删掉；不碰真实数据根。
/// </remarks>
internal static class NoteSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("note", "速记：保存命名与标题 / 分类解析", SaveAndParse));
        cases.Add(new("note", "速记：同名避让与改标题保留创建日", CollisionAndRename));
        cases.Add(new("note", "速记：列表按标题 / 正文搜索", Search));
        cases.Add(new("note", "速记：.txt → .md 一次性迁移", Migration));
        cases.Add(new("note", "速记：默认标题剥离 Markdown 记号", TitlePrefix));
    }

    private static void SaveAndParse()
    {
        WithRepo((repo, root) =>
        {
            var entry = new NoteEntry
            {
                Title = "第一条",
                Category = "收集箱",
                Body = "# 标题\n\n正文",
                Mtime = new DateTime(2026, 11, 20, 10, 30, 0),
            };
            string path = repo.Save(entry, null);

            Check.True(path.EndsWith(NoteRepository.Extension, StringComparison.Ordinal), "落盘是 .md：" + path);
            Check.True(Directory.Exists(Path.Combine(root, "收集箱")), "分类 = 一级子目录");
            Check.True(Regex.IsMatch(Path.GetFileName(path), @"^\d{4}-\d{2}-\d{2} 第一条\.md$"),
                "文件名 = 创建日 + 标题 + .md：" + Path.GetFileName(path));
            Check.Equal("# 标题\n\n正文", File.ReadAllText(path), "正文一字不差（UTF-8 无 BOM）");
            Check.Equal(new DateTime(2026, 11, 20, 10, 30, 0), File.GetLastWriteTime(path), "修改时间用传入的 Mtime");

            NoteEntry? loaded = repo.Load(path);
            Check.True(loaded != null, "Load 能读回来");
            Check.Equal("第一条", loaded?.Title, "标题从文件名解析");
            Check.Equal("收集箱", loaded?.Category, "分类从目录解析");

            Check.Equal("冒烟", NoteRepository.ParseTitleFromFileName("2026-11-22 冒烟.md"), "剥掉日期前缀");
            Check.Equal("", NoteRepository.ParseTitleFromFileName("2026-11-22_101010.md"), "无标题形式返回空");
            Check.Equal(1, repo.CountNotes("收集箱"), "分类计数只数 .md");
        });
    }

    private static void CollisionAndRename()
    {
        WithRepo((repo, root) =>
        {
            var first = new NoteEntry { Title = "重名", Category = "未分类", Body = "A", Mtime = DateTime.Now };
            string pathA = repo.Save(first, null);

            var second = new NoteEntry { Title = "重名", Category = "未分类", Body = "B", Mtime = DateTime.Now };
            string pathB = repo.Save(second, null);
            Check.True(pathB != pathA, "同名同日不覆盖：" + Path.GetFileName(pathB));
            Check.True(Path.GetFileName(pathB).Contains(" (2).md"), "第二份加 (2) 后缀：" + Path.GetFileName(pathB));
            Check.Equal("A", File.ReadAllText(pathA), "第一份内容没被动");

            // 改标题：日期前缀沿用旧文件名里的创建日，旧文件删除
            var renamed = new NoteEntry
            {
                Path = pathA,
                Title = "改过的名字",
                Category = "未分类",
                Body = "A2",
                Mtime = DateTime.Now,
            };
            string pathC = repo.Save(renamed, pathA);
            Check.True(Path.GetFileName(pathC).EndsWith("改过的名字.md", StringComparison.Ordinal),
                "新文件名带新标题：" + Path.GetFileName(pathC));
            Check.Equal(Path.GetFileName(pathA)[..10], Path.GetFileName(pathC)[..10], "创建日前缀保持不变");
            Check.False(File.Exists(pathA), "旧文件已删除");
            Check.Equal("A2", File.ReadAllText(pathC), "正文写进新文件");

            // 空标题 → 时间戳文件名
            var blank = new NoteEntry { Title = "", Category = "未分类", Body = "无标题", Mtime = DateTime.Now };
            string pathD = repo.Save(blank, null);
            Check.True(Regex.IsMatch(Path.GetFileName(pathD), @"^\d{4}-\d{2}-\d{2}_\d{6}\.md$"),
                "空标题用时间戳：" + Path.GetFileName(pathD));
            Check.Equal("", NoteRepository.ParseTitleFromFileName(Path.GetFileName(pathD)), "时间戳文件名没有标题");
        });
    }

    private static void Search()
    {
        WithRepo((repo, root) =>
        {
            repo.Save(new NoteEntry
            {
                Title = "会议记录", Category = "收集箱", Body = "讨论了 Markdown 迁移",
                Mtime = new DateTime(2026, 11, 20, 9, 0, 0),
            }, null);
            repo.Save(new NoteEntry
            {
                Title = "购物清单", Category = "收集箱", Body = "- 牛奶\n- 面包",
                Mtime = new DateTime(2026, 11, 21, 9, 0, 0),
            }, null);

            Check.Equal(2, repo.List(null, null).Count, "全部分类共两条");
            Check.Equal(1, repo.List(null, "购物").Count, "按标题命中");
            Check.Equal(1, repo.List(null, "牛奶").Count, "标题不命中时扫正文");
            Check.Equal(0, repo.List(null, "不存在的词").Count, "搜不到就是空");

            var all = repo.List(null, null);
            Check.Equal("购物清单", all[0].Title, "按修改时间倒序（最新在前）");
            Check.Equal("", all[0].Body, "列表只带标题 + mtime，不预读正文");
        });
    }

    private static void Migration()
    {
        WithRepo((repo, root) =>
        {
            string cat = Path.Combine(root, "未分类");
            Directory.CreateDirectory(cat);
            string legacy = Path.Combine(cat, "2026-01-02 旧笔记.txt");
            File.WriteAllText(legacy, "# 旧\n\n正文", new UTF8Encoding(false));

            int failed = repo.MigrateTxtToMd();
            Check.Equal(0, failed, "没被占用时全部改名成功");

            string target = Path.Combine(cat, "2026-01-02 旧笔记.md");
            Check.True(File.Exists(target), "改名成 .md：" + Path.GetFileName(target));
            Check.False(File.Exists(legacy), "旧 .txt 不在了");
            Check.Equal("# 旧\n\n正文", File.ReadAllText(target), "内容一个字都不动");
            Check.Equal("旧笔记", repo.Load(target)?.Title, "标题仍从文件名解析");

            Check.Equal(0, repo.MigrateTxtToMd(), "再跑一次是空操作");
            Check.Equal(1, repo.List(null, null).Count, "迁移后列表能看到它");

            // 目标已存在：两份都要留着，不能覆盖
            File.WriteAllText(Path.Combine(cat, "撞名.txt"), "旧内容", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(cat, "撞名.md"), "新内容", new UTF8Encoding(false));
            repo.MigrateTxtToMd();
            Check.Equal("新内容", File.ReadAllText(Path.Combine(cat, "撞名.md")), "已有 .md 不被覆盖");
            Check.Equal("旧内容", File.ReadAllText(Path.Combine(cat, "撞名 (2).md")), "迁移过来的一份加 (2)");

            // .迁移备份 这类点前缀目录不碰
            string backup = Path.Combine(root, ".迁移备份");
            Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "留着.txt"), "备份", new UTF8Encoding(false));
            repo.MigrateTxtToMd();
            Check.True(File.Exists(Path.Combine(backup, "留着.txt")), "点前缀目录里的 .txt 不动");
        });
    }

    private static void TitlePrefix()
    {
        Check.Equal("标题", NoteActions.StripMarkdownPrefix("# 标题"), "# 标题");
        Check.Equal("标题", NoteActions.StripMarkdownPrefix("###### 标题"), "六级标题");
        Check.Equal("条目", NoteActions.StripMarkdownPrefix("- 条目"), "- 条目");
        Check.Equal("条目", NoteActions.StripMarkdownPrefix("1. 条目"), "1. 条目");
        Check.Equal("条目", NoteActions.StripMarkdownPrefix("12) 条目"), "12) 条目");
        Check.Equal("待办", NoteActions.StripMarkdownPrefix("- [ ] 待办"), "待办项");
        Check.Equal("已完成", NoteActions.StripMarkdownPrefix("- [x] 已完成"), "勾上的待办");
        Check.Equal("引用", NoteActions.StripMarkdownPrefix("> 引用"), "引用");
        Check.Equal("嵌套", NoteActions.StripMarkdownPrefix("> - 嵌套"), "引用里的列表项");
        Check.Equal("-5 度", NoteActions.StripMarkdownPrefix("-5 度"), "负号不是列表记号");
        Check.Equal("1.5 倍", NoteActions.StripMarkdownPrefix("1.5 倍"), "小数点不是有序列表");
        Check.Equal("普通一行", NoteActions.StripMarkdownPrefix("普通一行"), "没有记号原样返回");
        Check.Equal("#", NoteActions.StripMarkdownPrefix("#"), "全是记号时退回原行");

        Check.Equal("标题", NoteActions.FirstLine("\n# 标题\n正文"), "空行后取第一行有效内容");
        Check.Equal("待办", NoteActions.FirstLine("- [ ] 待办\n详情"), "第一行剥记号后当标题");
        Check.Equal(new string('长', 40), NoteActions.FirstLine(new string('长', 60)), "超长标题截到 40 字");
    }

    // —— 工具 ——

    /// <summary>在临时数据根里跑一段，跑完整个删掉（速记是用户资产，自测不碰真实目录）。</summary>
    private static void WithRepo(Action<NoteRepository, string> body)
    {
        string root = Path.Combine(Path.GetTempPath(), "ltools-selftest", "notes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try { body(new NoteRepository(root), root); }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录，删不掉不影响断言 */ }
        }
    }
}
