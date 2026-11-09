using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Config;

/// <summary>一条快捷键的生效范围（相对全局默认名单）。</summary>
public enum HotkeyScope
{
    /// <summary>跟随全局默认（设置里那份白 / 黑名单）。</summary>
    inherit,

    /// <summary>所有窗口都生效，忽略全局默认。</summary>
    all,

    /// <summary>只在白名单里命中时生效（使用本条的名单）。</summary>
    whitelist,

    /// <summary>黑名单里命中时不生效（使用本条的名单）。</summary>
    blacklist,
}

/// <summary>
/// 进程 / 窗口条件。空列表 = 不限制；任一条命中即算命中。
/// 写法与面板场景一致：进程名可写 <c>chrome.exe</c>、<c>chrome*</c>、完整路径；标题 / 类名支持 <c>*</c> / <c>?</c>。
/// </summary>
public sealed class AppFilter
{
    /// <summary>进程名或完整路径（不区分大小写，支持 * 与 ?）。</summary>
    public List<string> Processes { get; set; } = new();

    /// <summary>窗口标题（包含匹配，支持 * 与 ?；没有 * 时按「包含」算）。</summary>
    public List<string> Titles { get; set; } = new();

    /// <summary>窗口类名（精确匹配，支持 * 与 ?）。</summary>
    public List<string> Classes { get; set; } = new();

    public bool IsEmpty => Processes.Count == 0 && Titles.Count == 0 && Classes.Count == 0;

    public AppFilter Clone() => new()
    {
        Processes = new List<string>(Processes),
        Titles = new List<string>(Titles),
        Classes = new List<string>(Classes),
    };

    /// <summary>把空串 / 纯空白条目去掉（保存前调用，避免出现永远匹配不上的空规则）。</summary>
    public void Normalize()
    {
        Processes = Clean(Processes);
        Titles = Clean(Titles);
        Classes = Clean(Classes);
    }

    private static List<string> Clean(List<string>? source) => (source ?? new List<string>())
        .Select(x => (x ?? "").Trim())
        .Where(x => x.Length > 0)
        .ToList();
}

/// <summary>预设组里的一条绑定：一个快捷键 → 一个动作。</summary>
public sealed class HotkeyBinding
{
    /// <summary>稳定 Id（组内唯一），用于界面定位与撤销。</summary>
    public string Id { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>canonical 写法：<c>capslock+ctrl+h</c> / <c>ctrl+alt+k</c> / <c>f9</c>。</summary>
    public string Chord { get; set; } = "";

    /// <summary>动作 Id 引用（动作清单里的 Id，删除动作时按引用清理）。</summary>
    public string ActionId { get; set; } = "";

    /// <summary>备注（可空）。</summary>
    public string Note { get; set; } = "";

    /// <summary>生效范围；<see cref="HotkeyScope.inherit"/> 时跟随全局默认。</summary>
    public HotkeyScope Scope { get; set; } = HotkeyScope.inherit;

    /// <summary>本条的名单（<see cref="Scope"/> 为 inherit / all 时不看它）。</summary>
    public AppFilter? Filter { get; set; }

    public HotkeyBinding Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Chord = Chord,
        ActionId = ActionId,
        Note = Note,
        Scope = Scope,
        Filter = Filter?.Clone(),
    };
}

/// <summary>预设组：一组「快捷键 + 动作」，整组可启停、组内每项也可单独启停。</summary>
public sealed class HotkeyGroup
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>整组开关；关闭后组内绑定一律不生效（配置保留）。</summary>
    public bool Enabled { get; set; } = true;

    public List<HotkeyBinding> Bindings { get; set; } = new();

    public HotkeyGroup Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Bindings = Bindings.Select(b => b.Clone()).ToList(),
    };
}

/// <summary><c>configs/hotkeys.json</c>：全局快捷键（预设组 + 绑定 + 全局默认名单）。</summary>
public sealed class HotkeyFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    /// <summary>全局默认名单（每条绑定可以覆盖）。</summary>
    public AppFilter DefaultFilter { get; set; } = new();

    /// <summary>全局默认怎么用这份名单：all = 所有窗口 / whitelist = 只在这些里 / blacklist = 排除这些。</summary>
    public HotkeyScope DefaultScope { get; set; } = HotkeyScope.all;

    /// <summary>
    /// 默认预设组的播种版本。0 = 还没播种；升级时只在 0 时写入种子，
    /// 因此用户删掉的默认绑定不会在下次启动时被塞回来。
    /// </summary>
    public int SeedVersion { get; set; }

    /// <summary>预设组（顺序 = 运行期匹配优先级）。</summary>
    public List<HotkeyGroup> Groups { get; set; } = new();

    public HotkeyFile Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        DefaultFilter = DefaultFilter?.Clone() ?? new AppFilter(),
        DefaultScope = DefaultScope,
        SeedVersion = SeedVersion,
        Groups = Groups.Select(g => g.Clone()).ToList(),
    };

    public void Normalize()
    {
        DefaultFilter ??= new AppFilter();
        DefaultFilter.Normalize();
        Groups ??= new List<HotkeyGroup>();

        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !groupIds.Add(group.Id))
            {
                group.Id = NewId("g");
                groupIds.Add(group.Id);
            }
            group.Name = string.IsNullOrWhiteSpace(group.Name) ? "未命名组" : group.Name.Trim();

            var bindingIds = new HashSet<string>(StringComparer.Ordinal);
            group.Bindings ??= new List<HotkeyBinding>();
            foreach (var binding in group.Bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Id) || !bindingIds.Add(binding.Id))
                {
                    binding.Id = NewId("b");
                    bindingIds.Add(binding.Id);
                }
                binding.Chord = Core.HotkeyChord.Normalize(binding.Chord);
                binding.ActionId = (binding.ActionId ?? "").Trim();
                binding.Note ??= "";
                binding.Filter?.Normalize();
            }
        }
    }

    /// <summary>生成一个不与现有 Id 冲突的新 Id（组 / 绑定共用前缀）。</summary>
    public string NewId(string prefix)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in Groups ?? new List<HotkeyGroup>())
        {
            used.Add(group.Id);
            foreach (var binding in group.Bindings ?? new List<HotkeyBinding>()) used.Add(binding.Id);
        }

        int n = 1;
        while (used.Contains(prefix + n)) n++;
        return prefix + n;
    }
}
