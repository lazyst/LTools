using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Features.Builtin;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图 Pro 的全局快捷键（P7-c，照 Snow Shot 的 <c>GlobalShortcutManager</c> + 设置里的
/// 「全局快捷键」页）：动作清单 / 读写 / 播种 / 摘要 / 冲突，以及暂停总开关。
/// </summary>
/// <remarks>
/// 只碰纯数据：真正的按键命中、写盘与「按下 F1 真的弹覆盖层」在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>shortcuts</c> 段。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ShortcutCases()
    {
        // 动作清单：Id / 引用都不重复，引用必须是真实存在的内置功能
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in CaptureShortcuts.All)
        {
            Check.True(ids.Add(action.Id), $"动作 Id 唯一：{action.Id}");
            Check.True(refs.Add(action.Reference), $"引用唯一：{action.Reference}");
            Check.True(FeatureCatalog.Find(action.Reference) != null,
                $"引用必须能跑：{action.Reference}");
            Check.True(action.Label.Length > 0 && action.Hint.Length > 0,
                $"动作有名字与说明：{action.Id}");
        }

        // 「暂停 / 恢复全局快捷键」自己也得在清单里：否则暂停之后再也按不回来
        Check.True(CaptureShortcuts.FindByReference("ltools.hotkeysToggle") != null,
            "暂停总开关本身可绑定");

        // 默认值：都能解析、最多两条、彼此不重复（照 configurationschema 的 F1 / Ctrl+F1 / F3 / Ctrl+F3）
        var defaults = new List<string>();
        foreach (var action in CaptureShortcuts.All)
        {
            Check.True(action.Defaults.Length <= CaptureShortcuts.MaxPerAction,
                $"默认最多 {CaptureShortcuts.MaxPerAction} 条：{action.Id}");
            foreach (string chord in action.Defaults)
            {
                Check.True(HotkeyChord.TryParse(chord, out var parsed, out _),
                    $"默认值可解析：{action.Id} → {chord}");
                Check.Equal(HotkeyChord.Normalize(chord), parsed.ToText(),
                    $"默认值已是规范写法：{chord}");
                defaults.Add(chord);
            }
        }
        Check.Equal(defaults.Count, defaults.Distinct(StringComparer.Ordinal).Count(),
            "默认值之间不撞车");
        Check.Equal(4, defaults.Count, "首次播种四条（截图 / 截图并复制 / 贴剪贴板 / 恢复上一张）");
        Check.True(defaults.Contains("f1") && defaults.Contains("ctrl+f1") &&
                   defaults.Contains("f3") && defaults.Contains("ctrl+f3"),
            "四条默认值就是上游那四个");

        // 播种：只播一次，播种后组里正好是那四条，再播不改动
        var seeded = new HotkeyFile { SeedVersion = 99, CaptureSeedVersion = 0 };
        Check.True(CaptureShortcuts.Seed(seeded), "首次播种返回 true");
        Check.Equal(1, seeded.CaptureSeedVersion, "播种版本记到 1");

        var group = CaptureShortcuts.GroupOf(seeded);
        Check.True(group != null, "播种建出「截图 Pro」组");
        Check.Equal(4, group!.Bindings.Count, "组里正好四条默认绑定");
        Check.False(CaptureShortcuts.Seed(seeded), "第二次播种什么都不做");
        Check.Equal(4, group.Bindings.Count, "第二次播种不改绑定");

        // 已经播过（版本 = 1）就完全不碰文件，也不建组
        var done = new HotkeyFile { CaptureSeedVersion = 1 };
        Check.False(CaptureShortcuts.Seed(done), "版本已足够 → 不播种");
        Check.True(CaptureShortcuts.GroupOf(done) == null, "不播种就不建空组");

        // 用户自己先绑了「截图」：播种不覆盖、也不重复加
        var custom = new HotkeyFile { CaptureSeedVersion = 0 };
        var shot = CaptureShortcuts.Find("screenshot")!;
        CaptureShortcuts.SetChords(custom, shot, new[] { "f9" });
        CaptureShortcuts.Seed(custom);
        Check.Equal(1, CaptureShortcuts.ChordsOf(custom, shot).Count, "已有绑定不被播种覆盖");
        Check.Equal("f9", CaptureShortcuts.ChordsOf(custom, shot)[0], "保留用户自己的 F9");
        Check.Equal(4, CaptureShortcuts.GroupOf(custom)!.Bindings.Count,
            "只补没绑的三条，总数仍是四条");

        // SetChords：去重、丢掉空 / 解析不了的、最多两条、按顺序
        var file = new HotkeyFile();
        Check.Equal(2, CaptureShortcuts.SetChords(file, shot,
            new[] { "ctrl+f2", "CTRL+f2", "f2" }), "同一组合的两种写法只留一条");
        Check.Equal(2, CaptureShortcuts.ChordsOf(file, shot).Count, "上限两条");
        Check.Equal("ctrl+f2", CaptureShortcuts.ChordsOf(file, shot)[0], "按写入顺序");
        Check.Equal("f2", CaptureShortcuts.ChordsOf(file, shot)[1], "第二条在后面");

        Check.Equal(2, CaptureShortcuts.SetChords(file, shot,
            new[] { "f5", "f6", "f7" }), "超出的第三条被丢掉");
        Check.Equal("f5", CaptureShortcuts.ChordsOf(file, shot)[0], "替换后第一条是 F5");
        Check.Equal("f6", CaptureShortcuts.ChordsOf(file, shot)[1], "替换后第二条是 F6");

        Check.Equal(0, CaptureShortcuts.SetChords(file, shot, new[] { "", "不认识" }),
            "空与非法写法什么都不写");
        Check.Equal(0, CaptureShortcuts.ChordsOf(file, shot).Count, "清空之后没有绑定");

        // SetChords 只动这一个动作
        var copy = CaptureShortcuts.Find("screenshot_copy")!;
        CaptureShortcuts.SetChords(file, shot, new[] { "f1" });
        CaptureShortcuts.SetChords(file, copy, new[] { "ctrl+f1" });
        CaptureShortcuts.SetChords(file, copy, new[] { "ctrl+f2" });
        Check.Equal(1, CaptureShortcuts.ChordsOf(file, shot).Count, "改别的动作不影响这一条");
        Check.Equal("f1", CaptureShortcuts.ChordsOf(file, shot)[0], "F1 还在");
        Check.Equal(2, CaptureShortcuts.GroupOf(file)!.Bindings.Count, "组里只留两条");

        // 摘要：没有绑定 = 「还没设置」，有绑定 = 「F1 截图 · …」，超出上限收尾省略号
        Check.Equal("还没设置", CaptureShortcuts.Summary(new HotkeyFile()), "没绑时摘要");
        Check.True(CaptureShortcuts.Summary(file).Contains("截图", StringComparison.Ordinal),
            "摘要带动作名：" + CaptureShortcuts.Summary(file));
        Check.True(CaptureShortcuts.Summary(file).StartsWith("F1", StringComparison.Ordinal),
            "摘要带格式化后的键：" + CaptureShortcuts.Summary(file));
        Check.True(CaptureShortcuts.Summary(file, 1).EndsWith("…", StringComparison.Ordinal),
            "超过上限时省略");
        Check.False(CaptureShortcuts.Summary(file, 99).EndsWith("…", StringComparison.Ordinal),
            "没超上限不加省略号");

        // 冲突：两条绑定同一个键 → 两个动作都标出来；换个键就没有冲突
        var clash = new HotkeyFile();
        CaptureShortcuts.SetChords(clash, shot, new[] { "f1" });
        CaptureShortcuts.SetChords(clash, copy, new[] { "f1" });
        var conflicts = CaptureShortcuts.Conflicting(clash);
        Check.True(conflicts.Contains(shot.Id) && conflicts.Contains(copy.Id),
            "同键的两个动作都算冲突");
        Check.Equal(2, conflicts.Count, "只有这两个");

        CaptureShortcuts.SetChords(clash, copy, new[] { "ctrl+f1" });
        Check.Equal(0, CaptureShortcuts.Conflicting(clash).Count, "换个键就不冲突");

        // 同一个动作的两条不同键不算冲突
        CaptureShortcuts.SetChords(clash, shot, new[] { "f1", "f2" });
        Check.Equal(0, CaptureShortcuts.Conflicting(clash).Count, "同一动作的两条不算冲突");

        // 组停用之后不再参与冲突判定（运行期也不触发）
        CaptureShortcuts.GroupOf(clash)!.Enabled = false;
        CaptureShortcuts.SetChords(clash, copy, new[] { "f1" });
        Check.Equal(0, CaptureShortcuts.Conflicting(clash).Count, "停用的组不参与冲突");

        // 规范化往返：Normalize 之后 Id 都在、绑定都在（写盘前那一拍不能丢东西）
        var round = new HotkeyFile();
        CaptureShortcuts.Seed(round);
        round.Normalize();
        Check.Equal(4, CaptureShortcuts.GroupOf(round)!.Bindings.Count, "Normalize 不丢绑定");
        Check.True(CaptureShortcuts.GroupOf(round)!.Bindings.All(b => b.Id.Length > 0),
            "Normalize 补齐绑定 Id");
        Check.True(round.CaptureSeedVersion == 0 || round.CaptureSeedVersion == 1,
            "种子版本是 0 / 1 两种取值");

        // 暂停总开关：默认开着，关掉能恢复（进程内状态，不落盘）
        Check.True(HotkeyRegistry.GlobalEnabled, "全局快捷键默认启用");
        HotkeyRegistry.GlobalEnabled = false;
        try
        {
            Check.False(HotkeyRegistry.GlobalEnabled, "关得掉");
        }
        finally
        {
            HotkeyRegistry.GlobalEnabled = true;
        }
        Check.True(HotkeyRegistry.GlobalEnabled, "还能恢复");
    }
}
