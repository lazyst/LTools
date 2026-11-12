using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Features.Builtin;
using LTools.Views;

namespace LTools.Features.Floating;

/// <summary>
/// 悬浮动作 / 悬浮分组的结构操作与**偏好**读写（设计见 <c>docs/FLOATING.md</c>）。
/// </summary>
/// <remarks>
/// <para>
/// **悬浮项无状态**（与 Quicker 一致）：<see cref="Items"/> 只在内存里，退出即消失，
/// 不写进 <c>configs/floating.json</c>、重启也不恢复。落盘的只有 <see cref="Prefs"/>（中键关闭、
/// 吸附阈值、默认尺寸这些设置）。
/// </para>
/// <para>
/// **一个动作只允许一个悬浮实例**：同一动作再次悬浮时只是把已有窗口显示出来，
/// 不会叠出第二个按钮（按动作 Id 去重）。
/// </para>
/// </remarks>
internal static class FloatingLibrary
{
    private const string LogSource = "Floating";

    private static string _path = "";

    /// <summary>当前偏好（永远非 null；落盘）。</summary>
    public static FloatingConfig Current { get; private set; } = new();

    /// <summary>偏好（等价于 <c>Current.Preferences</c>，读起来短一点）。</summary>
    public static FloatingPreferencesDto Prefs => Current.Preferences;

    /// <summary>桌面上的悬浮项（**运行期，只有内存**；顺序即创建顺序）。</summary>
    public static List<FloatingItemDto> Items { get; } = new();

    /// <summary>绑定配置路径并读入偏好（启动时由 <see cref="App"/> 调用）。</summary>
    public static void Initialize(string path)
    {
        _path = path;
        Reload();
    }

    /// <summary>重新从磁盘读偏好并夹紧取值范围。</summary>
    public static void Reload()
    {
        try
        {
            var cfg = AppConfig.Load(_path);
            if (cfg.LoadFailed)
            {
                // 配置坏掉时保留内存态（同 PanelLibrary）
                AppLog.Warn(LogSource, "配置读取失败，悬浮偏好保留当前内存值");
                return;
            }
            Current = cfg.Floating ?? new FloatingConfig();
            ClampPreferences(Current);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogSource, "读取悬浮偏好失败：" + ex.Message);
        }
    }

    /// <summary>偏好改动后落盘（悬浮项本身不落盘）。失败只记日志。</summary>
    public static bool SavePreferences()
    {
        if (string.IsNullOrEmpty(_path)) return false;
        try
        {
            FloatingStore.Modify(_path, f => f.Floating = Current);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogSource, "保存悬浮偏好失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>新建一个悬浮项 Id（ULID，生成时顺手排掉内存里已有的项）。</summary>
    public static string NewItemId() => IdFactory.NewId(id => Items.Any(i => i.Id == id));

    // —— 查询 ——

    public static FloatingItemDto? Find(string? itemId) =>
        string.IsNullOrEmpty(itemId) ? null : Items.FirstOrDefault(i => i.Id == itemId);

    /// <summary>某个动作现在悬浮在哪儿（没有则 null）。</summary>
    public static FloatingItemDto? FindByAction(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return null;
        return Items.FirstOrDefault(i => i.Members.Any(m => m.ActionId == actionId));
    }

    public static bool IsFloating(string? actionId) => FindByAction(actionId) != null;

    /// <summary>是否为多成员分组（单成员悬浮项不算分组）。</summary>
    public static bool IsGroup(FloatingItemDto item) => item.Members.Count > 1;

    // —— 创建 / 删除 ——

    /// <summary>
    /// 悬浮一个动作。已在悬浮则原样返回已有项（不重复创建）。
    /// 返回 null 表示到上限了。
    /// </summary>
    public static FloatingItemDto? FloatAction(string actionId, double x, double y)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;

        var existing = FindByAction(actionId);
        if (existing != null) return existing;

        if (Items.Count >= Math.Max(1, Prefs.MaxItems))
        {
            AppLog.Warn(LogSource, $"悬浮项已达上限 {Prefs.MaxItems}，未创建");
            return null;
        }

        var item = new FloatingItemDto
        {
            Id = NewItemId(),
            X = x,
            Y = y,
            Size = Prefs.DefaultSize,
            Opacity = Prefs.DefaultOpacity,
            Members = new List<FloatingMemberDto> { new() { ActionId = actionId } },
        };
        Items.Add(item);
        return item;
    }

    /// <summary>
    /// 悬浮一个面板分组：成员 = 分组里的动作（去重、跳过已在别处悬浮的动作）。
    /// 分组为空或全都被别处占用时返回 null。
    /// </summary>
    public static FloatingItemDto? FloatGroup(PanelGroupDto group, double x, double y)
    {
        if (group == null) return null;
        if (Items.Count >= Math.Max(1, Prefs.MaxItems))
        {
            AppLog.Warn(LogSource, $"悬浮项已达上限 {Prefs.MaxItems}，未创建");
            return null;
        }

        var members = new List<FloatingMemberDto>();
        foreach (string id in group.Actions)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (members.Any(m => m.ActionId == id)) continue;
            if (FindByAction(id) != null) continue;   // 一个动作只悬浮一次
            members.Add(new FloatingMemberDto { ActionId = id });
        }
        if (members.Count == 0) return null;

        var item = new FloatingItemDto
        {
            Id = NewItemId(),
            Name = members.Count > 1 ? group.Name : "",
            X = x,
            Y = y,
            Size = Prefs.DefaultSize,
            Opacity = Prefs.DefaultOpacity,
            Members = members,
        };
        Items.Add(item);
        return item;
    }

    /// <summary>关闭一个悬浮项（整组）。</summary>
    public static void RemoveItem(string? itemId)
    {
        var item = Find(itemId);
        if (item != null) Items.Remove(item);
    }

    /// <summary>把某个成员从悬浮项里移除；移除后没有成员就删掉整项。</summary>
    public static void RemoveMember(string itemId, string actionId)
    {
        var item = Find(itemId);
        if (item == null) return;
        item.Members.RemoveAll(m => m.ActionId == actionId);
        if (item.Members.Count == 0) Items.Remove(item);
        else if (item.Members.Count == 1) item.Name = "";
    }

    /// <summary>
    /// 把 <paramref name="fromId"/> 合并进 <paramref name="intoId"/>（拖到一起 = 建组）。
    /// 返回合并后的项，失败返回 null（同一项 / 已不存在 / 成员重复）。
    /// </summary>
    public static FloatingItemDto? Merge(string fromId, string intoId)
    {
        if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(intoId) || fromId == intoId) return null;
        var from = Find(fromId);
        var into = Find(intoId);
        if (from == null || into == null) return null;

        int added = 0;
        foreach (var m in from.Members.ToList())
        {
            if (into.Members.Any(x => x.ActionId == m.ActionId)) continue;
            into.Members.Add(new FloatingMemberDto { ActionId = m.ActionId });
            added++;
        }
        if (added == 0) return null;

        if (into.Members.Count > 1 && string.IsNullOrWhiteSpace(into.Name))
            into.Name = from.Name;      // 先保留被拖那一方的组名（可能为空）

        Items.Remove(from);
        return into;
    }

    /// <summary>
    /// 把 <paramref name="actionId"/> 从分组里拆出来，变成独立悬浮项（Quicker 的「解除分组」）。
    /// 拆出来的项放在分组旁边；分组只剩一个成员时自动退化成单个悬浮项。
    /// </summary>
    public static FloatingItemDto? SplitMember(string itemId, string actionId)
    {
        var item = Find(itemId);
        if (item == null || item.Members.Count < 2) return null;
        if (!item.Members.Any(m => m.ActionId == actionId)) return null;

        var (w, h) = FloatWindow.MeasureItem(item);
        var single = new FloatingItemDto
        {
            Id = NewItemId(),
            X = item.Vertical ? item.X : item.X + w + 6,
            Y = item.Vertical ? item.Y + h + 6 : item.Y,
            Size = item.Size,
            Opacity = item.Opacity,
            PositionLocked = item.PositionLocked,
            Vertical = false,
            Members = new List<FloatingMemberDto> { new() { ActionId = actionId } },
        };

        item.Members.RemoveAll(m => m.ActionId == actionId);
        if (item.Members.Count == 1) item.Name = "";
        Items.Add(single);
        return single;
    }

    /// <summary>
    /// 拆分为独立悬浮项（Quicker 的「解除分组（保留悬浮动作）」）。
    /// 成员按原窗口位置、沿分组的方向依次排开。
    /// </summary>
    public static List<FloatingItemDto> Split(string itemId)
    {
        var result = new List<FloatingItemDto>();
        var item = Find(itemId);
        if (item == null) return result;

        double step = FloatWindow.MemberSide(item.Size) + 6;
        int index = 0;
        foreach (var member in item.Members.ToList())
        {
            var single = new FloatingItemDto
            {
                Id = NewItemId(),
                X = item.Vertical ? item.X : item.X + index * step,
                Y = item.Vertical ? item.Y + index * step : item.Y,
                Size = item.Size,
                Opacity = item.Opacity,
                PositionLocked = item.PositionLocked,
                Members = new List<FloatingMemberDto> { new() { ActionId = member.ActionId } },
            };
            Items.Add(single);
            result.Add(single);
            index++;
        }

        Items.Remove(item);
        return result;
    }

    // —— 回收 ——

    /// <summary>
    /// 按引用清理：动作被删后，成员消失；成员清空的悬浮项整项删除。
    /// 由 <see cref="ConfigStore.RemoveAction"/> 与启动流程调用。
    /// </summary>
    public static bool CollectGarbage()
    {
        bool changed = false;
        foreach (var item in Items.ToList())
        {
            int removed = item.Members.RemoveAll(m => !BindingRef.Exists(m.ActionId));
            if (removed > 0) changed = true;
            if (item.Members.Count == 0)
            {
                Items.Remove(item);
                changed = true;
            }
            else if (item.Members.Count == 1 && item.Name.Length > 0)
            {
                item.Name = "";
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>夹紧偏好的取值范围（幂等）。</summary>
    internal static void ClampPreferences(FloatingConfig cfg)
    {
        cfg.Preferences ??= new FloatingPreferencesDto();

        var p = cfg.Preferences;
        p.SnapDistance = Math.Clamp(p.SnapDistance, 4, 48);
        p.DefaultSize = Math.Clamp(p.DefaultSize, 28, 96);
        p.DefaultOpacity = Math.Clamp(p.DefaultOpacity, 0.3, 1);
        p.MaxItems = Math.Clamp(p.MaxItems, 1, 200);
    }
}
