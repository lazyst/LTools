using System.Collections.Generic;

namespace LTools.Features;

/// <summary>
/// 全局动作清单（对应计划 §2）。超级面板格子、CapsLock+数字菜单项、组合步骤
/// 都只存「动作 Id 引用」，执行时通过本表解析为 <see cref="ActionDto"/>。
/// 启动时从 <c>AppConfig.Actions</c> 加载（阶段 2）；阶段 1 由冒烟测试直接注册。
/// </summary>
internal static class ActionRegistry
{
    private static readonly Dictionary<string, ActionDto> _byId = new();

    /// <summary>清空清单（重新加载前调用）。</summary>
    public static void Clear() => _byId.Clear();

    /// <summary>注册或覆盖一个动作（以 Id 为键）。</summary>
    public static void Register(ActionDto action) => _byId[action.Id] = action;

    /// <summary>批量注册。</summary>
    public static void RegisterAll(IEnumerable<ActionDto> actions)
    {
        foreach (var a in actions) Register(a);
    }

    /// <summary>按 Id 查找动作；找不到返回 null。</summary>
    public static ActionDto? FindById(string id) =>
        _byId.TryGetValue(id, out var a) ? a : null;

    /// <summary>按 Id 取显示名；动作不存在时显示占位「(id)」。供菜单 / 设置面板共用。</summary>
    public static string DisplayName(string id)
    {
        var a = FindById(id);
        return a != null ? a.Name : $"({id})";
    }

    /// <summary>全部已注册动作（供编辑器列表展示）。</summary>
    public static IReadOnlyCollection<ActionDto> All => _byId.Values;

    /// <summary>从清单移除；返回是否确实存在并移除。</summary>
    public static bool Remove(string id) => _byId.Remove(id);

    /// <summary>生成不与现有 Id 冲突的新 Id（a1, a2, ...）。</summary>
    public static string NextId()
    {
        int max = 0;
        foreach (var k in _byId.Keys)
        {
            if (k.Length > 1 && k[0] == 'a' && int.TryParse(k[1..], out var n) && n > max) max = n;
        }
        return "a" + (max + 1);
    }
}
