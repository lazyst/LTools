using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.Builtin;

/// <summary>内置项分组：常用功能（作用于系统）/ LTools 功能（作用于 LTools 自身）。</summary>
internal enum FeatureGroup
{
    Common,
    Ltools,
}

internal enum FeatureParamKind
{
    Text,
    Number,
    Choice,
    Region,
}

/// <summary>下拉参数的一项：<see cref="Value"/> 写进引用，<see cref="Label"/> 给人看。</summary>
internal sealed record FeatureOption(string Value, string Label);

/// <summary>一个内置功能的参数声明（界面按它自动生成表单）。</summary>
internal sealed record FeatureParam(
    string Key,
    string Label,
    FeatureParamKind Kind,
    string Default = "",
    FeatureOption[]? Options = null,
    string? Hint = null,
    string? VisibleWhenKey = null,
    string? VisibleWhenValue = null,
    string? Unit = null)
{
    /// <summary>按 <paramref name="values"/> 判断这一行是否该显示（没写条件就显示）。</summary>
    public bool IsVisible(IReadOnlyDictionary<string, string> values)
    {
        if (VisibleWhenKey == null) return true;
        return values.TryGetValue(VisibleWhenKey, out string? v) && v == VisibleWhenValue;
    }
}

/// <summary>运行一个内置功能时拿到的上下文：光标坐标 + 解析好的参数（已带上默认值）。</summary>
internal sealed class FeatureRunContext
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public FeatureRunContext(int cursorX, int cursorY, IReadOnlyDictionary<string, string> values)
    {
        CursorX = cursorX;
        CursorY = cursorY;
        _values = values;
    }

    /// <summary>唤起时的光标坐标（0,0 表示没记录，调用方自行取 <c>GetCursorPos</c>）。</summary>
    public int CursorX { get; }

    public int CursorY { get; }

    public string Get(string key) => _values.TryGetValue(key, out string? v) ? v : "";

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), out int n) ? n : fallback;

    public IReadOnlyDictionary<string, string> Values => _values;
}

/// <summary>
/// 一个内置功能：可放到面板 / 绑快捷键 / 悬浮，但**不是动作、不是动作单元**。
/// </summary>
/// <remarks>
/// <para>
/// 引用写法是 <c>id</c> 或 <c>id#key=value&amp;key2=value2</c>（见 <see cref="FeatureRef"/>）；
/// 动作 Id（<c>a1</c>）里不会出现 <c>#</c>，两者共用一个字符串字段也不会混。
/// </para>
/// </remarks>
internal sealed class FeatureDef
{
    /// <summary>稳定 Id：<c>common.*</c> 或 <c>ltools.*</c>。</summary>
    public required string Id { get; init; }

    public required FeatureGroup Group { get; init; }

    /// <summary>常用功能下的分类（工具 / 光标移动 / 文本选择 / 删除与撤销 / 剪贴板 / 系统 / 媒体 / 屏幕）；LTools 功能留空。</summary>
    public string Category { get; init; } = "";

    public required string Name { get; init; }

    public required string Icon { get; init; }

    public string Description { get; init; } = "";

    public FeatureParam[] Params { get; init; } = Array.Empty<FeatureParam>();

    public required Action<FeatureRunContext> Run { get; init; }

    public bool HasParams => Params.Length > 0;

    /// <summary>带上全部默认值的引用（放进面板 / 快捷键时用）。</summary>
    public string DefaultReference
    {
        get
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in Params)
            {
                if (p.Default.Length > 0) values[p.Key] = p.Default;
            }
            return FeatureRef.Build(Id, values);
        }
    }

    /// <summary>引用的参数 + 声明里的默认值，合并成运行时能用的一份。</summary>
    public IReadOnlyDictionary<string, string> Resolve(IReadOnlyDictionary<string, string> stored)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in Params)
        {
            values[p.Key] = stored.TryGetValue(p.Key, out string? v) ? v : p.Default;
        }
        return values;
    }
}
