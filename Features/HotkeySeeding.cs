using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features.Builtin;

namespace LTools.Features;

/// <summary>
/// 首次启动（以及以后每次加预设）把「CapsLock 经典」预设组播种进配置。
/// </summary>
/// <remarks>
/// <para>
/// 高频入口直接绑到内置功能引用（<c>ltools.*</c> / <c>common.*</c>），不再生成预置动作：
/// 内置功能不是步骤单元，绑在面板 / 快捷键 / 悬浮里和动作共用同一套引用字段（见 <see cref="FeatureRef"/>）。
/// 光标移动这类拆得出的手势仍是一个 <c>input.sendKeys</c> 预置动作。
/// </para>
/// <para>
/// 幂等：预置动作按「<see cref="ActionDto.Preset"/> + 同名」复用，绑定按「组内同快捷键」复用；
/// 播种完成后写 <see cref="HotkeyFile.SeedVersion"/>，用户删掉的条目不会再被塞回来。
/// </para>
/// </remarks>
internal static class HotkeySeeding
{
    private const int Version = 2;

    private const string GroupName = "CapsLock 经典";

    /// <summary>一条功能种子：快捷键 → 内置功能引用（参数用默认值）。</summary>
    private sealed record FeatureSeed(string Chord, string Reference, string Note);

    /// <summary>一条动作种子：快捷键 + 动作名 + 图标 + 模拟按键。</summary>
    private sealed record KeySeed(string Chord, string Name, string Icon, string[] Keys);

    private static readonly FeatureSeed[] FeatureSeeds =
    {
        new("capslock+t", "ltools.panel", "预置：打开超级面板"),
        new("capslock+n", "common.note", "预置：打开速记"),
        new("capslock+q", "common.quickSearch", "预置：快速搜索"),
        new("capslock+tab", "common.magnifier", "预置：切换放大镜"),
        new("capslock+backslash", "ltools.settings", "预置：打开设置"),
        // capslock+space 故意留空（旧「鼠标模式」已移除）。
    };

    private static readonly KeySeed[] KeySeeds =
    {
        // —— 光标移动与选择（模拟按键一步到位）——
        new("capslock+s", "CapsLock · 左移一字符", "keyboard", new[] { "left" }),
        new("capslock+d", "CapsLock · 下移一行", "keyboard", new[] { "down" }),
        new("capslock+e", "CapsLock · 上移一行", "keyboard", new[] { "up" }),
        new("capslock+f", "CapsLock · 右移一字符", "keyboard", new[] { "right" }),
        new("capslock+w", "CapsLock · 移到行首", "keyboard", new[] { "home" }),
        new("capslock+r", "CapsLock · 移到行尾", "keyboard", new[] { "end" }),
        new("capslock+j", "CapsLock · 选中左一字符", "keyboard", new[] { "shift+left" }),
        new("capslock+k", "CapsLock · 选中下一行", "keyboard", new[] { "shift+down" }),
        new("capslock+i", "CapsLock · 选中上一行", "keyboard", new[] { "shift+up" }),
        new("capslock+l", "CapsLock · 选中右一字符", "keyboard", new[] { "shift+right" }),

        // —— 删除与撤销 ——
        new("capslock+comma", "CapsLock · 退格", "keyboard", new[] { "backspace" }),
        new("capslock+period", "CapsLock · 删除", "keyboard", new[] { "delete" }),
        new("capslock+backspace", "CapsLock · 删除整行", "keyboard",
            new[] { "home", "shift+end", "delete" }),
        new("capslock+z", "CapsLock · 撤销", "keyboard", new[] { "ctrl+z" }),
        new("capslock+y", "CapsLock · 重做", "keyboard", new[] { "ctrl+y" }),
    };

    /// <summary>播种（已播过就什么都不做）。</summary>
    public static void Ensure(string configPath, AppConfig cfg)
    {
        if (cfg.Hotkeys.SeedVersion >= Version) return;

        bool changed = false;
        // 首次播种顺带补齐 configs/ 六个域（与 SplitDomainsStep 做的事一致）；之后每次启动只读不写。
        ConfigIO.Modify(configPath, ConfigDomain.all, c =>
        {
            var file = c.Hotkeys ?? new HotkeyFile();
            file.Normalize();
            if (file.SeedVersion >= Version) return;

            var actions = c.Actions ?? new List<ActionDto>();

            // v1 播下的「LTools 内置功能」预置动作（internal.* 单元）已随单元一起下线，一次性清掉。
            // 只有预置动作会被清，用户自己建的动作不动（哪怕引用了旧单元也留给他们自己改）。
            var obsolete = actions
                .Where(a => a.Preset && UsesRemovedUnit(a))
                .Select(a => a.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (obsolete.Count > 0) actions.RemoveAll(a => obsolete.Contains(a.Id));

            var presetByName = actions
                .Where(a => a.Preset && !string.IsNullOrWhiteSpace(a.Name))
                .GroupBy(a => a.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var group = file.Groups.FirstOrDefault(g => g.Name == GroupName);
            if (group == null)
            {
                group = new HotkeyGroup { Id = file.NewId("g"), Name = GroupName, Enabled = true };
                file.Groups.Add(group);
            }

            foreach (var seed in KeySeeds)
            {
                if (!presetByName.TryGetValue(seed.Name, out var action))
                {
                    action = BuildAction(NextActionId(actions), seed);
                    actions.Add(action);
                    presetByName[seed.Name] = action;
                }

                Bind(group, file, seed.Chord, action.Id, "预置，可在动作管理里改", obsolete);
            }

            foreach (var seed in FeatureSeeds)
            {
                Bind(group, file, seed.Chord, seed.Reference, seed.Note, obsolete);
            }

            file.SeedVersion = Version;
            c.Actions = actions;
            c.Hotkeys = file;
            changed = true;
        });

        if (!changed) return;

        var reloaded = AppConfig.Load(configPath);
        ActionLibrary.LoadFrom(reloaded);
        HotkeyRegistry.Load(reloaded);
        AppLog.Info("Hotkeys",
            $"已播种预设组「{GroupName}」（{FeatureSeeds.Length} 条功能 + {KeySeeds.Length} 条按键）");
    }

    /// <summary>组内按快捷键复用绑定；空引用 / 指向已下线预置动作的绑定就地补上。</summary>
    private static void Bind(HotkeyGroup group, HotkeyFile file, string chordText,
        string reference, string note, HashSet<string> obsolete)
    {
        string chord = HotkeyChord.Normalize(chordText);
        var binding = group.Bindings.FirstOrDefault(b => b.Chord == chord);
        if (binding == null)
        {
            group.Bindings.Add(new HotkeyBinding
            {
                Id = file.NewId("b"),
                Enabled = true,
                Chord = chord,
                ActionId = reference,
                Note = note,
            });
            return;
        }

        if (binding.ActionId.Length == 0 || obsolete.Contains(binding.ActionId))
            binding.ActionId = reference;
    }

    /// <summary>这个动作的步骤还在用已删除的 <c>internal.*</c> 单元吗。</summary>
    private static bool UsesRemovedUnit(ActionDto action)
    {
        // 还没迁移的 v1 动作：类型是 internal（整条动作用的就是一个内置功能）
        if (action.Program == null
            && string.Equals(action.LegacyType, "internal", StringComparison.OrdinalIgnoreCase))
            return true;

        var steps = action.Program?.Steps;
        if (steps == null) return false;
        return Walk(steps);
    }

    private static bool Walk(IEnumerable<Step> steps)
    {
        foreach (var step in steps)
        {
            if (step.Unit.StartsWith("internal.", StringComparison.Ordinal)) return true;
            if (step.Then.Count > 0 && Walk(step.Then)) return true;
            if (step.Else.Count > 0 && Walk(step.Else)) return true;
            if (step.Body.Count > 0 && Walk(step.Body)) return true;
        }
        return false;
    }

    private static ActionDto BuildAction(string id, KeySeed seed)
    {
        var program = new ActionProgram
        {
            Id = id,
            Name = seed.Name,
            Icon = seed.Icon,
            Description = "LTools 预置的 CapsLock 快捷键动作（可编辑 / 可删除）",
        };

        var step = Step.Of("input.sendKeys");
        step.WithText("keys", string.Join("\n", seed.Keys));
        program.Steps.Add(step);

        return new ActionDto
        {
            Id = id,
            Name = seed.Name,
            Icon = seed.Icon,
            Type = ActionType.composite,
            Preset = true,
            Program = program,
        };
    }

    private static string NextActionId(List<ActionDto> actions)
    {
        int max = 0;
        foreach (var action in actions)
        {
            if (action.Id.Length > 1 && action.Id[0] == 'a' && int.TryParse(action.Id[1..], out int n) && n > max)
                max = n;
        }
        return "a" + (max + 1);
    }
}
