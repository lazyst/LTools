using System;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Features.Builtin;

namespace LTools.Features;

/// <summary>
/// 首次启动（以及以后每次加预设）把「CapsLock 经典」预设组播种进配置。
/// </summary>
/// <remarks>
/// <para>
/// **种子全部是内置功能引用**（<c>ltools.*</c> / <c>common.*</c>）：打开面板 / 速记 / 设置这类入口、
/// 光标移动 / 选择 / 删除这些模拟按键，都在 <see cref="FeatureCatalog"/> 里有对应条目。
/// 所以预设组里只有功能引用、不生成任何动作——同一份条目用户也能放进面板、悬浮、绑别的键。
/// </para>
/// <para>
/// 幂等：绑定按「组内同快捷键」复用；播种完成后写 <see cref="HotkeyFile.SeedVersion"/>，
/// 用户删掉的条目不会再被塞回来。
/// </para>
/// </remarks>
internal static class HotkeySeeding
{
    private const int Version = 3;

    private const string GroupName = "CapsLock 经典";

    /// <summary>一条种子：快捷键 → 内置功能引用（参数用默认值）。</summary>
    /// <remarks>
    /// 不写备注：列表里「动作」列已经写着功能名，同一句话再占一列是重复；
    /// 备注列留给用户自己填。
    /// </remarks>
    private sealed record FeatureSeed(string Chord, string Reference);

    private static readonly FeatureSeed[] FeatureSeeds =
    {
        // —— CapsLock 高频入口 ——
        new("capslock+t", "ltools.panel"),
        new("capslock+n", "ltools.note"),
        new("capslock+q", "common.quickSearch"),
        new("capslock+tab", "common.magnifier"),
        new("capslock+backslash", "ltools.settings"),
        // capslock+space 故意留空（旧「鼠标模式」已移除）。

        // —— 光标移动 ——
        new("capslock+s", "common.key.left"),
        new("capslock+d", "common.key.down"),
        new("capslock+e", "common.key.up"),
        new("capslock+f", "common.key.right"),
        new("capslock+w", "common.key.home"),
        new("capslock+r", "common.key.end"),

        // —— 文本选择 ——
        new("capslock+j", "common.select.left"),
        new("capslock+k", "common.select.down"),
        new("capslock+i", "common.select.up"),
        new("capslock+l", "common.select.right"),

        // —— 删除与撤销 ——
        new("capslock+comma", "common.edit.backspace"),
        new("capslock+period", "common.edit.delete"),
        new("capslock+backspace", "common.edit.deleteLine"),
        new("capslock+z", "common.edit.undo"),
        new("capslock+y", "common.edit.redo"),
    };

    /// <summary>播种（已播过就什么都不做）。</summary>
    public static void Ensure(string configPath, AppConfig cfg)
    {
        // 内置组标记（幂等）：老配置里已有的两个组也补上「不可删除」，之后不再重复写盘。
        // 放在版本门之前：升级过的用户 SeedVersion 已经是当前值，但他们的组还缺这个标记。
        StampBuiltIns(configPath, cfg);

        if (cfg.Hotkeys.SeedVersion >= Version && cfg.Hotkeys.CaptureSeedVersion >= 1) return;

        bool changed = false;
        int captureSeeds = 0;
        // 首次播种顺带补齐 configs/ 六个域；之后每次启动只读不写。
        ConfigIO.Modify(configPath, ConfigDomain.all, c =>
        {
            var file = c.Hotkeys ?? new HotkeyFile();
            file.Normalize();
            bool touched = false;

            if (file.SeedVersion < Version)
            {
                var group = file.Groups.FirstOrDefault(g => g.Name == GroupName);
                if (group == null)
                {
                    group = new HotkeyGroup { Id = file.NewId(), Name = GroupName, Enabled = true, BuiltIn = true };
                    file.Groups.Add(group);
                }

                foreach (var seed in FeatureSeeds)
                    Bind(group, file, seed.Chord, seed.Reference);

                file.SeedVersion = Version;
                touched = true;
            }

            // 「截图 Pro」快捷键组：与上面那组分开记播种版本，删掉的条目不会互相塞回来
            if (CapturePro.CaptureShortcuts.Seed(file))
            {
                captureSeeds = CapturePro.CaptureShortcuts.GroupOf(file)?.Bindings.Count ?? 0;
                touched = true;
            }

            if (!touched) return;

            c.Hotkeys = file;
            changed = true;
        });

        if (!changed) return;

        var reloaded = AppConfig.Load(configPath);
        ActionLibrary.LoadFrom(reloaded);
        HotkeyRegistry.Load(reloaded);
        AppLog.Info("Hotkeys",
            captureSeeds > 0
                ? $"已播种预设组「{GroupName}」（{FeatureSeeds.Length} 条）与「{CapturePro.CaptureShortcuts.GroupName}」（{captureSeeds} 条截图快捷键）"
                : $"已播种预设组「{GroupName}」（{FeatureSeeds.Length} 条内置功能绑定）");
    }

    /// <summary>组内按快捷键复用绑定；空引用的绑定就地补上。</summary>
    private static void Bind(HotkeyGroup group, HotkeyFile file, string chordText, string reference)
    {
        string chord = HotkeyChord.Normalize(chordText);
        var binding = group.Bindings.FirstOrDefault(b => b.Chord == chord);
        if (binding == null)
        {
            group.Bindings.Add(new HotkeyBinding
            {
                Id = file.NewId(),
                Enabled = true,
                Chord = chord,
                ActionId = reference,
            });
            return;
        }

        if (binding.ActionId.Length == 0)
            binding.ActionId = reference;
    }

    /// <summary>这个组是不是内置组（播种进来的两个）：内置组不可删除，只能停用 / 改名 / 改绑。</summary>
    public static bool IsBuiltInGroup(HotkeyGroup group)
        => group.BuiltIn || IsBuiltInName(group.Name);

    private static bool IsBuiltInName(string? name)
        => string.Equals(name, GroupName, StringComparison.Ordinal)
           || string.Equals(name, CapturePro.CaptureShortcuts.GroupName, StringComparison.Ordinal);

    /// <summary>
    /// 老配置里按默认名把两个内置组补上 <c>BuiltIn</c>（幂等，只在确实缺标记时写一次）。
    /// 不这么补的话，升级上来的用户那两个组永远显示「可删除」。
    /// </summary>
    private static void StampBuiltIns(string configPath, AppConfig cfg)
    {
        if (cfg.Hotkeys?.Groups == null) return;
        if (!cfg.Hotkeys.Groups.Any(g => !g.BuiltIn && IsBuiltInName(g.Name))) return;

        try
        {
            ConfigIO.Modify(configPath, ConfigDomain.hotkeys, c =>
            {
                if (c.Hotkeys?.Groups == null) return;
                foreach (var g in c.Hotkeys.Groups)
                    if (!g.BuiltIn && IsBuiltInName(g.Name)) g.BuiltIn = true;
            });

            foreach (var g in cfg.Hotkeys.Groups)
                if (!g.BuiltIn && IsBuiltInName(g.Name)) g.BuiltIn = true;

            // Ensure 是在 HotkeyRegistry.Load 之后调的：不重载一次，本次会话的设置页
            // 拿到的还是缺标记的那份快照（内置组照样显示可删，要等下次启动才对）。
            HotkeyRegistry.Load(cfg);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hotkeys", "内置组标记失败：" + ex.Message);
        }
    }
}
