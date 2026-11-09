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
    private sealed record FeatureSeed(string Chord, string Reference, string Note);

    private static readonly FeatureSeed[] FeatureSeeds =
    {
        // —— CapsLock 高频入口 ——
        new("capslock+t", "ltools.panel", "预置：打开超级面板"),
        new("capslock+n", "ltools.note", "预置：打开速记"),
        new("capslock+q", "common.quickSearch", "预置：快速搜索"),
        new("capslock+tab", "common.magnifier", "预置：切换放大镜"),
        new("capslock+backslash", "ltools.settings", "预置：打开设置"),
        // capslock+space 故意留空（旧「鼠标模式」已移除）。

        // —— 光标移动 ——
        new("capslock+s", "common.key.left", "预置：左移一字符"),
        new("capslock+d", "common.key.down", "预置：下移一行"),
        new("capslock+e", "common.key.up", "预置：上移一行"),
        new("capslock+f", "common.key.right", "预置：右移一字符"),
        new("capslock+w", "common.key.home", "预置：移到行首"),
        new("capslock+r", "common.key.end", "预置：移到行尾"),

        // —— 文本选择 ——
        new("capslock+j", "common.select.left", "预置：选中左一字符"),
        new("capslock+k", "common.select.down", "预置：选中下一行"),
        new("capslock+i", "common.select.up", "预置：选中上一行"),
        new("capslock+l", "common.select.right", "预置：选中右一字符"),

        // —— 删除与撤销 ——
        new("capslock+comma", "common.edit.backspace", "预置：退格"),
        new("capslock+period", "common.edit.delete", "预置：删除"),
        new("capslock+backspace", "common.edit.deleteLine", "预置：删除整行"),
        new("capslock+z", "common.edit.undo", "预置：撤销"),
        new("capslock+y", "common.edit.redo", "预置：重做"),
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

            var group = file.Groups.FirstOrDefault(g => g.Name == GroupName);
            if (group == null)
            {
                group = new HotkeyGroup { Id = file.NewId("g"), Name = GroupName, Enabled = true };
                file.Groups.Add(group);
            }

            foreach (var seed in FeatureSeeds)
                Bind(group, file, seed.Chord, seed.Reference, seed.Note);

            file.SeedVersion = Version;
            c.Hotkeys = file;
            changed = true;
        });

        if (!changed) return;

        var reloaded = AppConfig.Load(configPath);
        ActionLibrary.LoadFrom(reloaded);
        HotkeyRegistry.Load(reloaded);
        AppLog.Info("Hotkeys",
            $"已播种预设组「{GroupName}」（{FeatureSeeds.Length} 条内置功能绑定）");
    }

    /// <summary>组内按快捷键复用绑定；空引用的绑定就地补上。</summary>
    private static void Bind(HotkeyGroup group, HotkeyFile file, string chordText,
        string reference, string note)
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

        if (binding.ActionId.Length == 0)
            binding.ActionId = reference;
    }
}
