using System.Text;
using System.Text.RegularExpressions;
using LTools.Config;
using LTools.Core.Actions;
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
        cases.Add(new("note", "速记：列表行摘要", Snippet));
        cases.Add(new("note", "速记：附件冗余清理（孤儿夹 / 没引用的图）", AttachmentGarbage));
        cases.Add(new("note", "速记：写速记带图片（复制进附件夹并改写正文）", WriteWithImages));
        cases.Add(new("note", "速记：写速记追加模式（时间标题 / 指定目标）", AppendLog));
        cases.Add(new("note", "速记：读速记带摘要", ReadCoreSnippet));
        cases.Add(new("note", "速记：置顶状态走 ui.json", TopmostPref));
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

    private static void Snippet()
    {
        WithRepo((repo, root) =>
        {
            string path = repo.Save(new NoteEntry
            {
                Title = "标题",
                Category = "未分类",
                Body = "# 标题\n\n第一段 **重点** 与 [链接](https://example.com)\n",
                Mtime = DateTime.Now,
            }, null);
            Check.Equal("第一段 重点 与 链接", repo.Snippet(path), "跳过与文件名重复的标题行，去掉行内记号");

            string bullet = repo.Save(new NoteEntry
            {
                Title = "二",
                Category = "未分类",
                Body = "- 只有一个列表项\n",
                Mtime = DateTime.Now,
            }, null);
            Check.Equal("只有一个列表项", repo.Snippet(bullet), "列表记号去掉");

            string longLine = repo.Save(new NoteEntry
            {
                Title = "三",
                Category = "未分类",
                Body = new string('长', 120),
                Mtime = DateTime.Now,
            }, null);
            Check.Equal(60, repo.Snippet(longLine).Length, "超长摘要截到 60 字");
        });
    }

    private static void AttachmentGarbage()
    {
        WithRepo((repo, root) =>
        {
            string notes = repo.Save(new NoteEntry
            {
                Title = "带图",
                Category = "未分类",
                Body = "![a](<带图.files/a.png>)\n",
                Mtime = DateTime.Now,
            }, null);

            string dir = NoteRepository.AttachmentsDirFor(notes);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.png"), "x");      // 正文引用过 → 留
            File.WriteAllText(Path.Combine(dir, "b.png"), "y");      // 没引用 → 搬
            File.WriteAllText(Path.Combine(dir, "说明.txt"), "z");    // 非图片 → 不动

            // 没有同名速记的附件夹（相当于在资源管理器里把 .md 删了）
            string orphan = Path.Combine(Path.GetDirectoryName(notes)!, "已经删掉的笔记.files");
            Directory.CreateDirectory(orphan);
            File.WriteAllText(Path.Combine(orphan, "c.png"), "w");

            // 未保存笔记的草稿夹：另有 7 天清理，这里不许碰
            string draft = Path.Combine(root, ".draft-20260101-000000.files");
            Directory.CreateDirectory(draft);

            string trash = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!, "trash");
            try
            {
                var (folders, files) = repo.CollectAttachmentGarbage();
                Check.Equal(1, folders, "没有同名速记的附件夹搬走一个");
                Check.Equal(1, files, "正文里没引用的图片搬走一张");
                Check.True(File.Exists(Path.Combine(dir, "a.png")), "正文引用过的图留着");
                Check.False(File.Exists(Path.Combine(dir, "b.png")), "没引用的图搬走了");
                Check.True(File.Exists(Path.Combine(dir, "说明.txt")), "非图片文件一概不动");
                Check.False(Directory.Exists(orphan), "孤儿附件夹搬走了");
                Check.True(Directory.Exists(draft), "草稿附件夹不碰");
                Check.True(Directory.Exists(trash), "都搬进数据根同级的 trash（只搬不删）");
            }
            finally
            {
                try { if (Directory.Exists(trash)) Directory.Delete(trash, recursive: true); } catch { }
            }
        });
    }

    private static void TopmostPref()
    {
        var cfg = new AppConfig { QuickNoteTopmost = true };
        Check.True(ConfigBundle.ToUi(cfg).QuickNoteTopmost, "置顶状态写进 ui.json（ToUi 带着它）");
        Check.True(cfg.QuickNoteTopmost && !new AppConfig().QuickNoteTopmost, "默认不置顶，勾过才置顶");
    }

    /// <summary>「写速记」带图片：复制进 <c>&lt;笔记名&gt;.files/</c>，正文里的运行期路径换成相对链接。</summary>
    private static void WriteWithImages()
    {
        WithRepo((repo, root) =>
        {
            string sourceDir = Path.Combine(Path.GetTempPath(), "ltools-selftest",
                "shots-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sourceDir);
            string shot = Path.Combine(sourceDir, "shot.png");
            File.WriteAllText(shot, "png-bytes");

            try
            {
                var result = NoteActions.Write(new NoteWriteRequest
                {
                    Body = "看这张图：\n\n![](" + shot + ")\n",
                    Title = "带图速记",
                    Category = "收集箱",
                    Images = new[] { shot },
                }, root);

                string attachments = NoteRepository.AttachmentsDirFor(result.File);
                string copied = Path.Combine(attachments, "shot.png");
                string link = NoteRepository.AttachmentLink(copied);

                Check.True(File.Exists(copied), "图片复制进了 <笔记名>.files/");
                Check.Equal(Path.GetDirectoryName(result.File), Path.GetDirectoryName(attachments),
                    "附件夹和笔记同目录");

                string body = File.ReadAllText(result.File);
                Check.Contains(body, link, "正文里的原路径换成了相对链接");
                Check.NotContains(body, shot, "不再留着那个运行期路径（否则清缓存就断链）");
                Check.Equal("带图速记", result.Title, "标题照填");

                // 另一条笔记带同一张图：各存一份（附件夹跟着笔记走）
                var second = NoteActions.Write(new NoteWriteRequest
                {
                    Body = "另一条",
                    Title = "带图速记二",
                    Category = "收集箱",
                    Images = new[] { shot },
                }, root);
                Check.True(File.Exists(Path.Combine(NoteRepository.AttachmentsDirFor(second.File), "shot.png")),
                    "第二条速记也有自己的 shot.png");

                // 正文里没写这个路径：补一行图片，别静默丢掉
                var third = NoteActions.Write(new NoteWriteRequest
                {
                    Body = "只在末尾",
                    Title = "补在末尾",
                    Category = "收集箱",
                    Images = new[] { shot },
                }, root);
                string thirdLink = NoteRepository.AttachmentLink(
                    Path.Combine(NoteRepository.AttachmentsDirFor(third.File), "shot.png"));
                Check.Contains(File.ReadAllText(third.File), "![shot](" + thirdLink + ")",
                    "正文没提到就补在末尾");

                // 源文件不存在：明确报错（用户给了图却悄悄丢掉更难查）
                Check.Throws<ActionException>(() => NoteActions.Write(new NoteWriteRequest
                {
                    Body = "x",
                    Title = "缺图",
                    Images = new[] { Path.Combine(sourceDir, "nope.png") },
                }, root), "图片不存在时报前置条件错误");
            }
            finally
            {
                try { Directory.Delete(sourceDir, recursive: true); } catch { }
            }
        });
    }

    /// <summary>「写速记」追加：默认接同分类最近一条，<c>stamp</c> 决定要不要时间标题行。</summary>
    private static void AppendLog()
    {
        WithRepo((repo, root) =>
        {
            var first = NoteActions.Write(new NoteWriteRequest
            {
                Body = "# 工作日志\n\n第一条",
                Title = "工作日志",
                Category = "日志",
            }, root);

            var appended = NoteActions.Write(new NoteWriteRequest
            {
                Body = "第二条",
                Category = "日志",
                Append = true,
                Stamp = true,
            }, root);

            Check.Equal(first.File, appended.File, "追加到同分类最近一条，不新建文件");
            Check.Equal("工作日志", appended.Title, "沿用目标速记的标题");

            string body = File.ReadAllText(appended.File);
            Check.Contains(body, "第一条", "旧内容还在");
            Check.Contains(body, "第二条", "新内容接上了");
            Check.True(Regex.IsMatch(body, @"## \d{4}-\d{2}-\d{2} \d{2}:\d{2}\r?\n\r?\n第二条"),
                "时间标题行写在追加内容前面：" + body);
            Check.True(body.EndsWith("\n", StringComparison.Ordinal), "结尾留一个换行");

            var noStamp = NoteActions.Write(new NoteWriteRequest
            {
                Body = "第三条",
                Category = "日志",
                Append = true,
                Stamp = false,
            }, root);
            Check.Equal(first.File, noStamp.File, "关掉时间标题仍是同一条");
            Check.Equal(1, Regex.Matches(File.ReadAllText(noStamp.File), @"\n## \d").Count,
                "只有第一次那条时间标题（第二次关掉了）");

            var byPath = NoteActions.Write(new NoteWriteRequest
            {
                Body = "按路径追加",
                Append = true,
                Stamp = false,
                Target = first.File,
            }, root);
            Check.Equal(first.File, byPath.File, "按 target 追到指定文件（分类留空也行）");
            Check.Contains(File.ReadAllText(first.File), "按路径追加", "指到的那条真的多了内容");

            Check.Throws<ActionException>(() => NoteActions.Write(new NoteWriteRequest
            {
                Body = "x",
                Append = true,
                Target = Path.Combine(root, "没有这个.md"),
            }, root), "追加目标不存在时报未找到（别偷偷新建把日志写丢）");

            var fresh = NoteActions.Write(new NoteWriteRequest
            {
                Body = "第一次",
                Category = "新分类",
                Append = true,
            }, root);
            Check.Equal("第一次", fresh.Title, "该分类还没有速记时照新建（标题取正文第一行）");
            Check.Contains(File.ReadAllText(fresh.File), "第一次", "新建出来的内容正常");

            // 没有标题的旧笔记（文件名只有日期时间）：追加不该给它改名
            string untitled = repo.Save(new NoteEntry
            {
                Title = "",
                Category = "日志",
                Body = "旧内容\n",
                Mtime = DateTime.Now,
            }, null);
            var appendedUntitled = NoteActions.Write(new NoteWriteRequest
            {
                Body = "新内容",
                Append = true,
                Target = untitled,
                Stamp = false,
            }, root);
            Check.Equal(untitled, appendedUntitled.File, "追加到没有标题的速记也不改名");
            Check.Contains(File.ReadAllText(untitled), "新内容", "内容照样接上");
            Check.Equal(2, repo.CountNotes("日志"), "日志分类还是两条（改名会多出一条）");
        });
    }

    /// <summary>「读速记」除了正文也给摘要（与列表同一行），并暴露成 <c>.snippet</c>。</summary>
    private static void ReadCoreSnippet()
    {
        WithRepo((repo, root) =>
        {
            repo.Save(new NoteEntry
            {
                Title = "读书笔记",
                Category = "收集箱",
                Body = "# 读书笔记\n\n今天读了 **第一章**，很有意思\n",
                Mtime = DateTime.Now,
            }, null);

            var hit = NoteActions.ReadCore("收集箱", "", root);
            Check.True(hit.Flag == true, "按分类能读到");
            Check.Equal("读书笔记", hit.Title, "标题");
            Check.Contains(hit.Text, "第一章", "正文是 Markdown 源码");
            Check.Equal("今天读了 第一章，很有意思", hit.Snippet,
                "摘要 = 列表那一行（跳过与标题重复的 # 行、去掉 ** 记号）");
            Check.Equal("今天读了 第一章，很有意思", hit.Property("snippet"), "${速记.snippet} 取得到摘要");
            Check.Equal(1d, hit.Number ?? 0, "count = 1");

            var miss = NoteActions.ReadCore("收集箱", "不存在的关键词", root);
            Check.False(miss.Flag == true, "没匹配到 = found false（不是错误）");
            Check.Equal(0d, miss.Number ?? 0, "count = 0");
            Check.Equal("", miss.Snippet, "没匹配时摘要是空的");
        });
    }

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
