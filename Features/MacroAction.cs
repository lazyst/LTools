using System;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Features;

/// <summary>
/// 键鼠宏与动作之间的唯一桥梁：**一个宏就是一个「1 步 · 回放宏」的动作**。
/// </summary>
/// <remarks>
/// <para>
/// 以前有一套独立的「宏动作」类型（<c>ActionType.macro</c> + <c>ActionDto.MacroId</c>），
/// 它没有动作体，任何入口触发都跑不动；编辑器一保存还会把它改成普通动作。
/// 现在没有第二种形状：宏动作就是普通动作，只是步骤列表恰好只有 <c>input.macro</c> 一步。
/// </para>
/// <para>
/// 识别方式是结构判定（<see cref="MacroIdOf(ActionDto)"/>）：单步 + 单元是 <c>input.macro</c>
/// + <c>macroId</c> 是字面量。判定只用于界面（显示宏卡片、摘要、试放、重录），
/// 执行不区分——它本来就是普通动作。
/// </para>
/// </remarks>
internal static class MacroAction
{
    /// <summary>回放宏的单元键。</summary>
    public const string UnitKey = "input.macro";

    /// <summary>宏动作的默认图标。</summary>
    public const string Icon = "record";

    /// <summary>动作是不是「一个宏」：返回它引用的宏 Id，不是则返回 null。</summary>
    public static string? MacroIdOf(ActionDto? action) => MacroIdOf(action?.Program);

    /// <summary>动作体是不是「一个宏」：单步 + 回放宏单元 + 字面量 macroId。</summary>
    public static string? MacroIdOf(ActionProgram? program)
    {
        if (program is not { Steps.Count: 1 }) return null;
        var step = program.Steps[0];
        if (!string.Equals(step.Unit, UnitKey, StringComparison.OrdinalIgnoreCase)) return null;
        if (!step.In.TryGetValue("macroId", out var arg)) return null;
        if (arg.Kind != StepArgKind.literal) return null;
        string id = arg.Literal.AsText().Trim();
        return id.Length > 0 ? id : null;
    }

    /// <summary>动作体：只有一步「回放宏」。</summary>
    public static ActionProgram ProgramFor(string macroId) => new()
    {
        Steps = { Step.Of(UnitKey).WithText("macroId", macroId) },
    };

    /// <summary>
    /// 把录好的宏挂成动作：已有动作引用同一个宏就只更新名称，否则新建一个。
    /// </summary>
    /// <returns>动作 Id；宏无效时返回 null。</returns>
    public static string? Attach(string macroId, string name)
    {
        if (string.IsNullOrWhiteSpace(macroId)) return null;

        var existing = ActionRegistry.All.FirstOrDefault(a =>
            string.Equals(MacroIdOf(a), macroId, StringComparison.Ordinal));
        string display = string.IsNullOrWhiteSpace(name) ? "键鼠宏" : name.Trim();

        if (existing != null)
        {
            existing.Name = display;
            existing.Icon = Icon;
            existing.Program = ProgramFor(macroId);
            ConfigStore.UpdateAction(existing);
            ConfigStore.Save();
            RefreshPanels();
            return existing.Id;
        }

        var action = new ActionDto
        {
            Name = display,
            Icon = Icon,
            Program = ProgramFor(macroId),
        };
        if (!ConfigStore.AddAction(action)) return null;
        ConfigStore.Save();
        RefreshPanels();
        return action.Id;
    }

    /// <summary>把宏动作放进面板「当前全局场景」的第一个分组；已在任意分组里就不动。</summary>
    /// <returns>放进的分组名；没放（已在别处 / 面板不可用）返回 null。</returns>
    public static string? PlaceOnPanel(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return null;
        if (PanelLibrary.FindReferences(actionId).Count > 0) return null;

        var scene = PanelLibrary.ActiveGlobalScene();
        if (scene == null)
        {
            scene = PanelLibrary.NewScene("常用", "star");
            PanelLibrary.Current.Global.Scenes.Add(scene);
            PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
        }
        if (scene.Groups.Count == 0) scene.Groups.Add(PanelLibrary.NewGroup("录制"));
        var group = scene.Groups[0];
        group.Actions.Add(actionId);
        PanelLibrary.Save();
        RefreshPanels();
        return group.Name;
    }

    /// <summary>本动作引用的宏是否还存在（文件被删 / 拷到别的机器时提醒）。</summary>
    public static bool MacroExists(string? macroId) =>
        !string.IsNullOrWhiteSpace(macroId) && MacroStore.Exists(macroId);

    /// <summary>动作显示用的一行宏摘要，如「12.3 秒 · 428 事件」；不是宏返回 null。</summary>
    public static string? SummaryOf(ActionDto? action)
    {
        string? id = MacroIdOf(action);
        if (id == null) return null;
        var meta = MacroStore.MetaOf(id);
        if (meta == null) return $"宏文件缺失（{id}）";
        return $"{meta.DurationText} · {meta.EventCount} 事件";
    }

    /// <summary>面板窗口开着的时候，数据改了要把磁贴重建一次。</summary>
    private static void RefreshPanels()
    {
        try { FloatingManager.RefreshAll(); }
        catch (Exception ex) { AppLog.Warn("MacroAction", $"刷新悬浮项失败：{ex.Message}"); }
    }
}
