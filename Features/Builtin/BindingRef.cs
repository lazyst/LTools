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
}
