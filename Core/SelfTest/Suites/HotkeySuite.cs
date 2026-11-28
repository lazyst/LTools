using System;
using System.Collections.Generic;
using LTools.Config;
using LTools.Core;
using LTools.Native;

namespace LTools.Core.SelfTest;

/// <summary>
/// 快捷键的纯逻辑：生效范围的优先级（绑定 &gt; 组 &gt; 全局默认）、归一口径、显示文案，
/// 以及带 <c>capslock</c> 前缀的组合键解析（录入框修复的地基）。
/// 钩子怎么放行 CapsLock 属于真机行为，靠手按验证；这里只测能直接断言的纯函数。
/// </summary>
internal static class HotkeySuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("hotkeys", "生效范围优先级：绑定 > 组 > 全局默认", Priority));
        cases.Add(new("hotkeys", "组默认值：跟随全局默认 / 所有窗口 / 白 / 黑名单", GroupDefaults));
        cases.Add(new("hotkeys", "组的归一口径（名单只在白 / 黑名单时保留）", GroupNormalize));
        cases.Add(new("hotkeys", "生效范围的显示文案", Describe));
        cases.Add(new("hotkeys", "capslock 前缀的组合键解析", CapsLockChord));
        cases.Add(new("hotkeys", "内置预设组不可删除（BuiltIn 标记 / 默认名兜底 / 克隆保留）", BuiltInGroups));
    }

    private static readonly ForegroundApp Chrome = new(
        IntPtr.Zero, @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        "chrome.exe", "某页面 - Google Chrome", "Chrome_WidgetWin_1");

    private static readonly ForegroundApp Notepad = new(
        IntPtr.Zero, @"C:\Windows\System32\notepad.exe", "notepad.exe", "无标题 - 记事本", "Notepad");

    private static HotkeyGroup Group(HotkeyScope scope, params string[] processes) => new()
    {
        Id = "g1",
        Name = "组",
        Scope = scope,
        Filter = new AppFilter { Processes = new List<string>(processes) },
    };

    private static HotkeyBinding Binding(HotkeyScope scope, params string[] processes) => new()
    {
        Id = "b1",
        Chord = "f9",
        ActionId = "act1",
        Scope = scope,
        Filter = scope is HotkeyScope.whitelist or HotkeyScope.blacklist
            ? new AppFilter { Processes = new List<string>(processes) }
            : null,
    };

    /// <summary>用户报的那个预期：单个快捷键的生效范围优先级高于组（组只是默认值）。</summary>
    private static void Priority()
    {
        var file = new HotkeyFile();   // 全局默认 = 所有窗口

        // 绑定显式设了范围 → 组完全不参与
        var groupChrome = Group(HotkeyScope.whitelist, "chrome.exe");
        var bindingAll = Binding(HotkeyScope.all);
        Check.True(AppFilterMatch.Allows(groupChrome, bindingAll, file, Notepad),
            "绑定写「全部窗口」时不受组限制（绑定优先）");
        Check.True(AppFilterMatch.Allows(groupChrome, bindingAll, file, Chrome), "绑定优先：Chrome 也放行");

        var groupInherit = Group(HotkeyScope.inherit);
        var bindingNotepad = Binding(HotkeyScope.whitelist, "notepad.exe");
        Check.True(AppFilterMatch.Allows(groupChrome, bindingNotepad, file, Notepad),
            "组限 Chrome + 绑定限记事本：记事本里按绑定放行");
        Check.False(AppFilterMatch.Allows(groupChrome, bindingNotepad, file, Chrome),
            "组限 Chrome + 绑定限记事本：Chrome 里被绑定挡掉");
        Check.True(AppFilterMatch.Allows(groupInherit, bindingNotepad, file, Notepad), "组不设时按绑定");

        // 绑定「跟随组」→ 看组；组也跟随 → 全局默认
        var bindingInherit = Binding(HotkeyScope.inherit);
        Check.True(AppFilterMatch.Allows(groupChrome, bindingInherit, file, Chrome), "跟随组：Chrome 放行");
        Check.False(AppFilterMatch.Allows(groupChrome, bindingInherit, file, Notepad), "跟随组：记事本挡掉");
        Check.True(AppFilterMatch.Allows(groupChrome, bindingInherit, file, null),
            "取不到前台程序时放行（与绑定同一条口径）");

        // 全局默认是白名单记事本：组能盖住它，绑定也能盖住它
        file.DefaultScope = HotkeyScope.whitelist;
        file.DefaultFilter = new AppFilter { Processes = new List<string> { "notepad.exe" } };
        Check.True(AppFilterMatch.Allows(groupInherit, bindingInherit, file, Notepad), "都跟随：全局默认放行记事本");
        Check.False(AppFilterMatch.Allows(groupInherit, bindingInherit, file, Chrome), "都跟随：全局默认挡掉 Chrome");
        Check.True(AppFilterMatch.Allows(groupChrome, bindingInherit, file, Chrome),
            "组设了 Chrome → 盖住「全局默认只放行记事本」");
        Check.True(AppFilterMatch.Allows(Group(HotkeyScope.all), bindingInherit, file, Chrome),
            "组写「所有窗口」→ 不再看全局默认名单");
        Check.True(AppFilterMatch.Allows(groupInherit, Binding(HotkeyScope.all), file, Chrome),
            "绑定写「全部窗口」→ 盖住全局默认名单");

        // 黑名单同样按优先级
        var groupBlack = Group(HotkeyScope.blacklist, "chrome.exe");
        Check.False(AppFilterMatch.Allows(groupBlack, bindingInherit, file, Chrome), "组黑名单：Chrome 挡掉");
        Check.True(AppFilterMatch.Allows(groupBlack, bindingInherit, file, Notepad), "组黑名单：记事本放行");
        Check.True(AppFilterMatch.Allows(groupBlack, bindingAll, file, Chrome), "绑定「全部窗口」盖住组黑名单");

        // 老入口（不传组）行为不变：只看绑定自己
        file.DefaultScope = HotkeyScope.all;
        Check.True(AppFilterMatch.Allows(bindingInherit, file, Chrome), "不传组：跟随全局默认 = 全部窗口");
    }

    private static void GroupDefaults()
    {
        var file = new HotkeyFile();
        var bindingInherit = Binding(HotkeyScope.inherit);

        Check.Equal(HotkeyScope.inherit, new HotkeyGroup().Scope, "组的默认档是「跟随全局默认」");

        var white = Group(HotkeyScope.whitelist, "chrome.exe");
        Check.True(AppFilterMatch.Allows(white, bindingInherit, file, Chrome), "组白名单：Chrome 放行");
        Check.False(AppFilterMatch.Allows(white, bindingInherit, file, Notepad), "组白名单：记事本挡掉");
        Check.False(AppFilterMatch.Allows(Group(HotkeyScope.whitelist), bindingInherit, file, Chrome),
            "组白名单为空 = 整组不生效");

        var black = Group(HotkeyScope.blacklist, "chrome.exe");
        Check.False(AppFilterMatch.Allows(black, bindingInherit, file, Chrome), "组黑名单：Chrome 挡掉");
        Check.True(AppFilterMatch.Allows(black, bindingInherit, file, Notepad), "组黑名单：记事本放行");
        Check.True(AppFilterMatch.Allows(Group(HotkeyScope.blacklist), bindingInherit, file, Chrome),
            "组黑名单为空 = 等于所有窗口");

        Check.True(AppFilterMatch.Allows(Group(HotkeyScope.all), bindingInherit, file, Chrome), "组「所有窗口」放行");
        Check.True(AppFilterMatch.Allows(Group(HotkeyScope.inherit), bindingInherit, file, Chrome),
            "组「跟随全局默认」+ 全局默认所有窗口 = 放行");

        // 组白名单 + 绑定黑名单这种组合：绑定赢
        var mixed = Binding(HotkeyScope.blacklist, "notepad.exe");
        Check.True(AppFilterMatch.Allows(white, mixed, file, Chrome), "绑定黑名单记事本：Chrome 放行（组不参与）");
        Check.False(AppFilterMatch.Allows(white, mixed, file, Notepad), "绑定黑名单记事本：记事本挡掉");
    }

    private static void GroupNormalize()
    {
        var file = new HotkeyFile
        {
            Groups = new List<HotkeyGroup>
            {
                new() { Id = "g1", Name = "默认", Scope = HotkeyScope.inherit,
                        Filter = new AppFilter { Processes = new List<string> { "chrome.exe" } } },
                new() { Id = "g2", Name = "白名单", Scope = HotkeyScope.whitelist },
                new() { Id = "g3", Name = "所有窗口", Scope = HotkeyScope.all,
                        Filter = new AppFilter { Processes = new List<string> { "x.exe" } } },
                new() { Id = "g4", Name = "去空白", Scope = HotkeyScope.blacklist,
                        Filter = new AppFilter { Processes = new List<string> { " chrome.exe ", "", "  " } } },
            },
        };

        file.Normalize();

        Check.Equal(HotkeyScope.inherit, file.Groups[0].Scope, "组的「跟随全局默认」原样保留");
        Check.True(file.Groups[0].Filter == null, "跟随默认不保留名单（改了范围不留旧规则）");
        Check.True(file.Groups[1].Filter != null, "白名单缺名单时补空表");
        Check.True(file.Groups[2].Filter == null, "「所有窗口」的名单被丢掉");
        Check.Equal(1, file.Groups[3].Filter!.Processes.Count, "空白 / 纯空格条目被去掉");
        Check.Equal("chrome.exe", file.Groups[3].Filter!.Processes[0], "首尾空白被去掉");

        // 幂等：重复归一不换 Id、不改生效范围
        string before = file.Groups[3].Id;
        file.Normalize();
        Check.Equal(before, file.Groups[3].Id, "重复归一不换 Id");
        Check.Equal(HotkeyScope.blacklist, file.Groups[3].Scope, "重复归一不改生效范围");

        // Clone 带上组生效范围（保存 / 编辑副本都靠它）
        var clone = file.Groups[3].Clone();
        Check.Equal(HotkeyScope.blacklist, clone.Scope, "Clone 带生效范围");
        Check.Equal("chrome.exe", clone.Filter?.Processes[0], "Clone 带名单");
    }

    private static void Describe()
    {
        Check.Equal("跟随全局默认", AppFilterMatch.DescribeGroupScope(Group(HotkeyScope.inherit)), "组：跟随默认");
        Check.Equal("所有窗口", AppFilterMatch.DescribeGroupScope(Group(HotkeyScope.all)), "组：所有窗口");
        Check.Equal("仅 chrome.exe", AppFilterMatch.DescribeGroupScope(Group(HotkeyScope.whitelist, "chrome.exe")),
            "组：白名单短标签");
        Check.Equal("排除 chrome.exe", AppFilterMatch.DescribeGroupScope(Group(HotkeyScope.blacklist, "chrome.exe")),
            "组：黑名单短标签");

        Check.Contains(AppFilterMatch.DescribeGroupScopeLong(Group(HotkeyScope.whitelist, "chrome.exe")),
            "绑定 > 组", "组的长文案写明优先级");
        Check.Contains(AppFilterMatch.DescribeGroupScopeLong(Group(HotkeyScope.whitelist)),
            "整组都不生效", "白名单为空时提示整组不生效");

        var file = new HotkeyFile();
        var binding = Binding(HotkeyScope.inherit);
        Check.Equal("跟随组",
            AppFilterMatch.DescribeScope(binding, Group(HotkeyScope.whitelist, "chrome.exe"), file),
            "绑定跟随组：短标签");
        Check.Contains(AppFilterMatch.DescribeScopeLong(binding, Group(HotkeyScope.whitelist, "chrome.exe"), file),
            "仅在组匹配的窗口生效", "绑定跟随组：长文案带出组的名单");
        Check.Contains(AppFilterMatch.DescribeScopeLong(binding, Group(HotkeyScope.all), file),
            "组不限制窗口", "绑定跟随组：组写所有窗口");
        Check.Contains(AppFilterMatch.DescribeScopeLong(Binding(HotkeyScope.all), Group(HotkeyScope.whitelist, "chrome.exe"), file),
            "不受组和全局默认名单限制", "绑定写全部窗口：明说组也挡不住");
    }

    private static void CapsLockChord()
    {
        Check.True(HotkeyChord.TryParse("capslock+h", out var caps, out _), "capslock+h 能解析");
        Check.True(caps.CapsLock, "带 CapsLock 前缀");
        Check.Equal((ushort)'H', caps.Vk, "主键 H");
        Check.False(caps.IsBare, "带 CapsLock 前缀不算单键（不会吞掉一个字符）");
        Check.Equal("capslock+h", caps.ToText(), "canonical 写法");
        Check.Equal("CapsLock+H", HotkeyChord.Format("capslock+h"), "显示写法");

        Check.True(HotkeyChord.TryParse("capslock+ctrl+shift+f4", out var mixed, out _), "多修饰键也能解析");
        Check.Equal("capslock+ctrl+shift+f4", mixed.ToText(), "多修饰键保持固定顺序");

        // 只写 capslock 时它是「主键 CapsLock」（和 f9 一样是单键），不是「CapsLock 前缀」：
        // 录入框不会产出这种写法（按 CapsLock 只当前缀），手写配置才可能遇到。
        Check.True(HotkeyChord.TryParse("capslock", out var bareCaps, out _), "只有 capslock 时解析成单键");
        Check.True(bareCaps.IsBare && bareCaps.Vk == Win32.VkCapital, "它是主键 CapsLock，不是前缀");
    }

    /// <summary>内置预设组：播种的两个组不可删除，用户自建的组可以删。</summary>
    private static void BuiltInGroups()
    {
        Check.True(LTools.Features.HotkeySeeding.IsBuiltInGroup(new HotkeyGroup { Name = "CapsLock 经典" }),
            "种子的「CapsLock 经典」是内置组");
        Check.True(LTools.Features.HotkeySeeding.IsBuiltInGroup(new HotkeyGroup { Name = "截图 Pro" }),
            "「截图 Pro」组是内置组");
        Check.False(LTools.Features.HotkeySeeding.IsBuiltInGroup(new HotkeyGroup { Name = "我的热键" }),
            "用户自建的组不是内置组");

        // 改过名的内置组靠 BuiltIn 标记仍然认得出（不能因为改名就变回可删）
        Check.True(LTools.Features.HotkeySeeding.IsBuiltInGroup(new HotkeyGroup { Name = "我的经典键", BuiltIn = true }),
            "改过名的内置组靠 BuiltIn 标记认得出");

        // 编辑副本（设置页保存走 Clone）不能把这个标记丢掉
        var file = new HotkeyFile
        {
            Groups = new List<HotkeyGroup> { new() { Name = "CapsLock 经典", BuiltIn = true } },
        };
        Check.True(file.Clone().Groups[0].BuiltIn, "克隆保留 BuiltIn（编辑副本保存后仍不可删）");
    }
}
