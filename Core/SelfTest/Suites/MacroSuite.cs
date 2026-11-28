using System.Collections.Generic;
using LTools.Features;

namespace LTools.Core.SelfTest;

/// <summary>
/// 键鼠宏「回放内容」（全部 / 仅键盘 / 仅鼠标）：只测纯逻辑（解析 / 中文名 / 事件归类 / 计数），
/// 真回放由回放器负责、冒烟碰不到输入注入。过滤语义写死在 <see cref="MacroPlaybackContents"/>，
/// 回放器只调它——这里断言的就是回放器唯一依赖的那几件事。
/// </summary>
internal static class MacroSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("macro", "回放内容：英文键解析与回写", ParseAndId));
        cases.Add(new("macro", "回放内容：中文名", Labels));
        cases.Add(new("macro", "回放内容：事件归类（按键 / 鼠标）", Acception));
        cases.Add(new("macro", "回放内容：过滤后的事件计数", Counting));
    }

    private static void ParseAndId()
    {
        Check.Equal(MacroPlaybackContent.All, MacroPlaybackContents.Parse(null), "null = 全部");
        Check.Equal(MacroPlaybackContent.All, MacroPlaybackContents.Parse(""), "空串 = 全部");
        Check.Equal(MacroPlaybackContent.All, MacroPlaybackContents.Parse("  "), "只有空白 = 全部");
        Check.Equal(MacroPlaybackContent.All, MacroPlaybackContents.Parse("全部"), "认不出的写法退回全部（别静默变成仅键盘）");

        Check.Equal(MacroPlaybackContent.Keyboard, MacroPlaybackContents.Parse("keyboard"), "keyboard");
        Check.Equal(MacroPlaybackContent.Mouse, MacroPlaybackContents.Parse("mouse"), "mouse");
        Check.Equal(MacroPlaybackContent.Mouse, MacroPlaybackContents.Parse(" MOUSE "), "大小写与空白都容错");

        Check.Equal("all", MacroPlaybackContents.Id(MacroPlaybackContent.All), "写回 all");
        Check.Equal("keyboard", MacroPlaybackContents.Id(MacroPlaybackContent.Keyboard), "写回 keyboard");
        Check.Equal("mouse", MacroPlaybackContents.Id(MacroPlaybackContent.Mouse), "写回 mouse");

        // 往返：配置里写什么都能原样读回来
        foreach (var content in new[]
                 {
                     MacroPlaybackContent.All, MacroPlaybackContent.Keyboard, MacroPlaybackContent.Mouse,
                 })
        {
            Check.Equal(content, MacroPlaybackContents.Parse(MacroPlaybackContents.Id(content)),
                $"往返保持：{MacroPlaybackContents.Label(content)}");
        }
    }

    private static void Labels()
    {
        Check.Equal("全部", MacroPlaybackContents.Label(MacroPlaybackContent.All), "全部");
        Check.Equal("仅键盘", MacroPlaybackContents.Label(MacroPlaybackContent.Keyboard), "仅键盘");
        Check.Equal("仅鼠标", MacroPlaybackContents.Label(MacroPlaybackContent.Mouse), "仅鼠标");
    }

    private static void Acception()
    {
        // 全部：什么都放
        foreach (MacroEventKind kind in Range(MacroEventKind.KeyDown, MacroEventKind.HWheel))
            Check.True(MacroPlaybackContents.Accepts(MacroPlaybackContent.All, kind), $"全部要放 {kind}");

        // 仅键盘：只放按下 / 抬起
        Check.True(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.KeyDown), "仅键盘放 KeyDown");
        Check.True(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.KeyUp), "仅键盘放 KeyUp");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.MouseMove), "仅键盘跳过鼠标移动");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.MouseDown), "仅键盘跳过按下鼠标");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.MouseUp), "仅键盘跳过抬起鼠标");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.Wheel), "仅键盘跳过滚轮");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Keyboard, MacroEventKind.HWheel), "仅键盘跳过横向滚轮");

        // 仅鼠标：鼠标一侧全放，按键全跳过（写成「不是按键」，以后加鼠标事件类型不会漏）
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Mouse, MacroEventKind.KeyDown), "仅鼠标跳过 KeyDown");
        Check.False(MacroPlaybackContents.Accepts(MacroPlaybackContent.Mouse, MacroEventKind.KeyUp), "仅鼠标跳过 KeyUp");
        foreach (MacroEventKind kind in Range(MacroEventKind.MouseMove, MacroEventKind.HWheel))
        {
            if (kind is MacroEventKind.KeyDown or MacroEventKind.KeyUp) continue;
            Check.True(MacroPlaybackContents.Accepts(MacroPlaybackContent.Mouse, kind), $"仅鼠标放 {kind}");
        }
    }

    private static void Counting()
    {
        var events = new List<MacroEvent>
        {
            new() { Kind = MacroEventKind.KeyDown, Vk = 0x41 },
            new() { Kind = MacroEventKind.MouseMove, X = 10, Y = 20 },
            new() { Kind = MacroEventKind.KeyUp, Vk = 0x41 },
            new() { Kind = MacroEventKind.MouseDown, Data = 1 },
            new() { Kind = MacroEventKind.MouseUp, Data = 1 },
        };

        Check.Equal(5, MacroPlaybackContents.CountAccepted(events, MacroPlaybackContent.All), "全部 = 5 条");
        Check.Equal(2, MacroPlaybackContents.CountAccepted(events, MacroPlaybackContent.Keyboard), "仅键盘 = 2 条");
        Check.Equal(3, MacroPlaybackContents.CountAccepted(events, MacroPlaybackContent.Mouse), "仅鼠标 = 3 条");

        Check.Equal(0, MacroPlaybackContents.CountAccepted(null, MacroPlaybackContent.All), "null 列表 = 0");
        Check.Equal(0, MacroPlaybackContents.CountAccepted(new List<MacroEvent>(), MacroPlaybackContent.Keyboard),
            "空列表 = 0");

        // 只有键盘的宏勾「仅鼠标」：0 ⇒ 回放器要明确报错，而不是假装放完了
        var keysOnly = new List<MacroEvent> { new() { Kind = MacroEventKind.KeyDown, Vk = 0x41 } };
        Check.Equal(0, MacroPlaybackContents.CountAccepted(keysOnly, MacroPlaybackContent.Mouse),
            "只有键盘的宏在「仅鼠标」下没有内容");
    }

    /// <summary>按枚举顺序取一段（含首尾），用来遍历所有事件类型。</summary>
    private static List<MacroEventKind> Range(MacroEventKind from, MacroEventKind to)
    {
        var list = new List<MacroEventKind>();
        for (int i = (int)from; i <= (int)to; i++) list.Add((MacroEventKind)i);
        return list;
    }
}
