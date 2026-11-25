using System;
using System.Collections.Generic;
using LTools.Features;

namespace LTools.Features.Builtin;

/// <summary>
/// 绑定引用的统一解析：动作（<c>a1</c>）与内置功能（<c>common.*</c> / <c>ltools.*</c>）
/// 在面板 / 快捷键 / 悬浮里是同一个字符串字段，界面显示与存活判断都从这里走。
/// </summary>
internal static class BindingRef
{
    public static bool IsFeature(string? reference) => FeatureCatalog.IsFeatureRef(reference);

    /// <summary>引用是否还有效（动作还在 / 功能还在目录里）。</summary>
    public static bool Exists(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (FeatureCatalog.IsFeatureRef(reference)) return true;
        return ActionRegistry.FindById(reference) != null;
    }

    /// <summary>按一份动作 Id 清单判定存活（磁盘修引用时用，内存动作库可能还没加载）。</summary>
    public static bool IsLive(string? reference, ISet<string> liveActionIds)
    {
        if (string.IsNullOrWhiteSpace(reference)) return false;
        if (FeatureCatalog.IsFeatureRef(reference)) return true;
        return liveActionIds.Contains(reference);
    }

    public static string Name(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        if (FeatureCatalog.IsFeatureRef(reference)) return FeatureCatalog.Name(reference);
        return ActionRegistry.FindById(reference)?.Name is { Length: > 0 } name
            ? name
            : $"（{reference}）动作已删除";
    }

    /// <summary>图标键（<see cref="IconCatalog"/> 里的名字）。</summary>
    public static string Icon(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return IconCatalog.Default;
        var def = FeatureCatalog.Find(FeatureRef.IdOf(reference));
        if (def != null) return def.Icon;
        return ActionRegistry.FindById(reference)?.Icon is { Length: > 0 } icon ? icon : IconCatalog.Default;
    }

    /// <summary>给提示 / 工具条用：「名称」或「名称 · 参数摘要」。</summary>
    public static string Describe(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        string name = Name(reference);
        string summary = FeatureCatalog.IsFeatureRef(reference) ? FeatureCatalog.ParamSummary(reference) : "";
        return summary.Length == 0 ? name : $"{name} · {summary}";
    }

    /// <summary>
    /// 悬停提示用：在 <see cref="Describe"/> 之后补上动作自己的**说明**（<c>description</c>），
    /// 以及「这个动作运行前会要参数」（<see cref="ActionParams.Describe(ActionDto?)"/>）。
    /// </summary>
    /// <remarks>
    /// 只给 tooltip 用，菜单标题 / 日志仍走 <see cref="Describe"/>——把说明塞进菜单项会让每个
    /// 菜单项长成两行。说明 / 参数都为空时与 <see cref="Describe"/> 完全相同。
    /// 面板磁贴、悬浮按钮、面板管理行的悬停提示都走这里，所以「要填参数」只在**这一处**加。
    /// </remarks>
    public static string Detail(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return "";
        if (FeatureCatalog.IsFeatureRef(reference)) return Describe(reference);

        var action = ActionRegistry.FindById(reference);
        var lines = new List<string> { Name(reference) };

        string description = action?.Program?.Description?.Trim() ?? "";
        if (description.Length > 0) lines.Add(description);

        string parameters = ActionParams.Describe(action);
        if (parameters.Length > 0) lines.Add(parameters);

        return string.Join("\n", lines);
    }
}
