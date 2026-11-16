using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features.InfoHelper;

namespace LTools.Core.SelfTest;

/// <summary>
/// 辅助工具（取值卡）的纯逻辑：坐标文案格式与卡片正文的形状。
/// 不碰 Win32 / COM / 屏幕——真机取窗口、框选、截图仍靠仓库外的 UI 冒烟与手点。
/// </summary>
/// <remarks>
/// 卡片正文就是 <c>InfoCard.Text</c>（窗口里显示它、⧉ 也复制它），所以这里断言的是
/// 「一个字段一行、标签：值」这个形状——别再断言一套只在界面上画的两列数据。
/// </remarks>
internal static class InfoHelperSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("infoHelper", "坐标文案格式（矩形 / 区域参数 / 点 / 颜色 / 状态 / 缩放 / 句柄）", Formats));
        cases.Add(new("infoHelper", "颜色解析：#RRGGBB 大小写 / 非法值不猜", Colors));
        cases.Add(new("infoHelper", "窗口卡：七个字段各占一行、进程带 .exe", WindowCard));
        cases.Add(new("infoHelper", "区域 / 坐标卡：区域参数与 x,y", RegionAndPoint));
        cases.Add(new("infoHelper", "屏幕环境卡：每块显示器一行、缩放带百分号", ScreenCard));
        cases.Add(new("infoHelper", "选中的文件卡：全部路径一条一行（不截断）", SelectionCard));
        cases.Add(new("infoHelper", "截图卡：方式 / 文件 / 区域都进正文", ShotCard));
    }

    private static void Formats()
    {
        Check.Equal("x=100, y=200, w=640, h=480", InfoText.Rect(100, 200, 640, 480), "矩形文案");
        Check.Equal("100,200,640,480", InfoText.RegionParams(100, 200, 640, 480), "区域参数（逗号串）");
        Check.Equal("x=-1920, y=0", InfoText.Point(-1920, 0), "负坐标也照写");
        Check.Equal("#0A1B2C", InfoText.Hex(10, 27, 44), "十六进制大写补零");
        Check.Equal("r=10, g=27, b=44", InfoText.Rgb(10, 27, 44), "RGB 文案");
        Check.Equal("0x1F", InfoText.Handle(new IntPtr(31)), "句柄带 0x");

        Check.Equal("普通", InfoText.WindowState(false, false), "普通状态");
        Check.Equal("最大化", InfoText.WindowState(true, false), "最大化");
        Check.Equal("最小化", InfoText.WindowState(false, true), "最小化优先（最小化时不再是最大化）");

        Check.Equal("100%", InfoText.Scale(96), "96 DPI = 100%");
        Check.Equal("125%", InfoText.Scale(120), "120 DPI = 125%");
        Check.Equal("150%", InfoText.Scale(144), "144 DPI = 150%");
        Check.Equal("100%", InfoText.Scale(0), "拿不到 DPI 按 100%");

        Check.Equal("3 项", InfoText.Count(3), "数量文案");
        Check.Equal("a\nb\nc", InfoText.Lines("a", "b", "c"), "多行拼接");
        Check.Equal("a\nc", InfoText.Lines("a", "", "c"), "空行不参与拼接");
        Check.True(InfoText.Unit.Contains("物理像素", StringComparison.Ordinal), "页首单位说明写明物理像素");
    }

    private static void Colors()
    {
        Check.True(InfoText.TryRgb("#0a1b2c", out int r, out int g, out int b), "小写也能解析");
        Check.Equal(10, r, "红色通道");
        Check.Equal(27, g, "绿色通道");
        Check.Equal(44, b, "蓝色通道");

        Check.True(InfoText.TryRgb("0A1B2C", out _, out _, out _), "不带 # 也认");
        Check.False(InfoText.TryRgb("#FFF", out _, out _, out _), "三位简写不猜");
        Check.False(InfoText.TryRgb("#GGGGGG", out _, out _, out _), "非十六进制字符拒绝");
        Check.False(InfoText.TryRgb(null, out _, out _, out _), "空值拒绝");
        Check.False(InfoText.TryRgb("  ", out _, out _, out _), "空白拒绝");
    }

    private static void WindowCard()
    {
        InfoCard card = InfoCards.Window("无标题 - 记事本", "Notepad", "notepad.exe",
            new IntPtr(0x10A2C), (100, 200, 800, 600), (108, 231, 784, 561), false, false);

        Check.Equal(InfoKind.Window, card.Kind, "类别");
        Check.Equal(new IntPtr(0x10A2C), card.Handle, "句柄留给「再取」");
        Check.Equal("窗口 · 无标题 - 记事本", card.Title, "卡片标题带窗口标题");

        string[] lines = Lines(card);
        Check.Equal(8, lines.Length, "抬头 + 七个字段，各占一行（可逐行划选）");
        Check.Equal("标题：无标题 - 记事本", lines[1], "第一行是标题");
        Check.Equal("类名：Notepad", Line(card, "类名"), "类名");
        Check.Equal("进程：notepad.exe", Line(card, "进程"), "进程名带 .exe（名单匹配写的就是它）");
        Check.Equal("矩形：x=100, y=200, w=800, h=600", Line(card, "矩形"), "矩形是屏幕物理像素");
        Check.Equal("客户区：x=108, y=231, w=784, h=561", Line(card, "客户区"), "客户区已换算成屏幕坐标");
        Check.Equal("状态：普通", Line(card, "状态"), "状态");
        Check.Equal("句柄：0x10A2C", Line(card, "句柄"), "句柄");
        Check.Contains(card.Text, "物理像素", "正文写明单位");

        InfoCard noTitle = InfoCards.Window("", "CabinetWClass", "explorer.exe",
            new IntPtr(1), (0, 0, 10, 10), (0, 0, 10, 10), true, false);
        Check.Contains(noTitle.Title, "explorer.exe", "没标题时用进程名兜底");
        Check.Equal("状态：最大化", Line(noTitle, "状态"), "最大化");
    }

    private static void RegionAndPoint()
    {
        InfoCard region = InfoCards.Region(-100, 50, 300, 200);
        Check.Equal(InfoKind.Region, region.Kind, "区域类别");
        Check.Equal<(int, int, int, int)?>((-100, 50, 300, 200), region.Area, "区域留给「再取」");
        Check.Equal("区域参数：-100,50,300,200", Line(region, "区域参数"), "区域参数可直接填进「固定区域」");
        Check.Equal("矩形：x=-100, y=50, w=300, h=200", Line(region, "矩形"), "负坐标照写");

        InfoCard point = InfoCards.Point(1234, 567);
        Check.Equal(InfoKind.Point, point.Kind, "坐标类别");
        Check.Equal(2, Lines(point).Length, "抬头 + 一行 x,y（没有多余的 X / Y 两行）");
        Check.Contains(point.Text, "x=1234, y=567", "正文是 x,y");

        InfoCard color = InfoCards.Color(10, 20, "#0a1b2c");
        Check.Equal("屏幕取色 · #0A1B2C", color.Title, "标题带规范化后的颜色");
        Check.Equal("颜色：#0A1B2C", Line(color, "颜色"), "颜色大写");
        Check.Equal("RGB：r=10, g=27, b=44", Line(color, "RGB"), "RGB 三元组");

        InfoCard bad = InfoCards.Color(0, 0, "oops");
        Check.Equal("RGB：（无法解析）", Line(bad, "RGB"), "解析不了就明说，不伪造三元组");
    }

    private static void ScreenCard()
    {
        var screens = new[]
        {
            new InfoScreen(1, true, 96, 0, 0, 1920, 1080, 0, 0, 1920, 1040),
            new InfoScreen(2, false, 120, -1920, 0, 1920, 1080, -1920, 0, 1920, 1040),
        };
        InfoCard card = InfoCards.Screen(-1920, 0, 3840, 1080, screens, 100, 200);

        Check.Equal(5, Lines(card).Length, "抬头 + 虚拟桌面 + 光标 + 两块屏");
        Check.Contains(card.Text, "显示器 1（主屏，100%）", "主屏标注");
        Check.Contains(card.Text, "显示器 2（副屏，125%）", "副屏标注与缩放");
        Check.Contains(card.Text, "x=-1920, y=0, w=1920, h=1080", "副屏矩形（负数左缘）");
        Check.Equal("光标：x=100, y=200", Line(card, "光标"), "光标位置");
        Check.Equal("屏幕环境 · 2 块屏", card.Title, "标题带屏数");
    }

    private static void SelectionCard()
    {
        var few = new[] { @"C:\a\b.txt", @"C:\a\c.txt", @"C:\a\d" };
        InfoCard card = InfoCards.Selection(@"C:\a", few);

        Check.Equal(InfoKind.Selection, card.Kind, "类别");
        Check.Equal("选中的文件 · 3 项", card.Title, "标题带数量");
        Check.Equal(5, Lines(card).Length, "抬头 + 文件夹 + 三项路径，一条一行");
        Check.Equal(@"文件夹：C:\a", Line(card, "文件夹"), "文件夹");
        foreach (string path in few) Check.Contains(card.Text, path, "正文含每一项");

        List<string> many = Enumerable.Range(1, 14).Select(i => $@"C:\a\f{i}.txt").ToList();
        InfoCard big = InfoCards.Selection(@"C:\a", many);
        Check.Equal(16, Lines(big).Length, "14 条路径全在正文里（不截断，才能自己挑行）");
        Check.Contains(big.Text, @"f11.txt", "第 11 条也在");
        Check.Contains(big.Text, @"f14.txt", "最后一条也在");
        foreach (string path in many) Check.Contains(big.Text, path, "每一条都在");

        InfoCard none = InfoCards.Selection("", Array.Empty<string>());
        Check.Equal(1, Lines(none).Length, "没文件夹、没选中项时只剩抬头");
        Check.Contains(none.Text, "选中的文件（0 项）", "0 项也照实写");
    }

    private static void ShotCard()
    {
        InfoCard card = InfoCards.Shot("整个屏幕", @"D:\data\cache\info-helper\shot-1.png",
            -1920, 0, 3840, 1080, null);

        Check.Equal(InfoKind.Shot, card.Kind, "类别");
        Check.Equal("整个屏幕", card.How, "方式留给「再取」");
        Check.Equal(@"D:\data\cache\info-helper\shot-1.png", card.FilePath, "文件路径留给删除");
        Check.Equal<(int, int, int, int)?>((-1920, 0, 3840, 1080), card.Area, "矩形留给「再取」");
        Check.Equal("截图 · 整个屏幕", card.Title, "标题");

        Check.Equal(@"文件：D:\data\cache\info-helper\shot-1.png", Line(card, "文件"), "正文带绝对路径");
        Check.Equal("区域：x=-1920, y=0, w=3840, h=1080", Line(card, "区域"), "正文带矩形");
        Check.True(card.Preview is null, "没传预览时就是 null（截图卡才会带缩略图）");
    }

    /// <summary>正文按行拆开（<c>\n</c> 是我们自己拼的，不夹 CRLF）。</summary>
    private static string[] Lines(InfoCard card) => card.Text.Split('\n');

    /// <summary>正文里以「标签：」开头的那一行；没有就返回空串（断言会直接报出来）。</summary>
    private static string Line(InfoCard card, string label) =>
        Lines(card).FirstOrDefault(l => l.StartsWith(label + "：", StringComparison.Ordinal)) ?? "";
}
