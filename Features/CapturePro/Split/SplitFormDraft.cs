using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 表单上的原文草案：把「用户正在输入的字符串」解析成设置 + 逐字段错误。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不让控件直接绑数字：切图的数量 / 张数 / 像素都必须是正整数，而用户在输入过程中
/// 一定会经过空串、<c>1.5</c>、<c>0</c> 这些中间态。照上游 <c>draftSettings</c>：**原文保留**、
/// 当场给该字段挂错误，算不出合法设置就不动文档，界面上也不会把用户刚打的字抹掉。
/// </para>
/// <para>
/// 隐藏的字段（宫格时的张数、精确时的张数…）一律沿用上一次提交的设置——与上游一致：
/// 用户看不见的输入框不该偷偷改值。
/// </para>
/// </remarks>
internal sealed class SplitFormDraft
{
    private static readonly Regex IntegerPattern = new("^[0-9]+$", RegexOptions.Compiled);

    public string Direction { get; set; } = "vertical";

    public string Type { get; set; } = "smart";

    public string Unit { get; set; } = "percentage";

    public string Count { get; set; } = "2";

    public string Overlap { get; set; } = "0";

    public string Rows { get; set; } = "2";

    public string Columns { get; set; } = "2";

    public string Pixels { get; set; } = "16";

    public string Percentage { get; set; } = "50";

    public static SplitFormDraft From(SplitSettings settings) => new()
    {
        Direction = KeyOf(settings.Direction),
        Type = KeyOf(settings.Type),
        Unit = KeyOf(settings.Unit),
        Count = settings.Count.ToString(CultureInfo.InvariantCulture),
        Overlap = settings.Overlap.ToString(CultureInfo.InvariantCulture),
        Rows = settings.Rows.ToString(CultureInfo.InvariantCulture),
        Columns = settings.Columns.ToString(CultureInfo.InvariantCulture),
        Pixels = settings.Pixels.ToString(CultureInfo.InvariantCulture),
        Percentage = settings.Percentage,
    };

    public SplitDirection DirectionValue => ParseDirection(Direction);

    public SplitType TypeValue => ParseType(Type);

    public SplitUnit UnitValue => ParseUnit(Unit);

    /// <summary>当前单位控件对应哪个错误键（<c>pixels</c> / <c>percentage</c>）。</summary>
    public string SizeKey => UnitValue == SplitUnit.Pixels ? "pixels" : "percentage";

    /// <summary>
    /// 解析成设置；<paramref name="errors"/> 非空 = 有字段写得不对（键与
    /// <see cref="SplitLayout.Errors"/> 同一套）。<paramref name="committed"/> 是隐藏字段的取值来源。
    /// </summary>
    public (SplitSettings Settings, Dictionary<string, string> Errors) Resolve(
        SplitSettings committed, int width, int height)
    {
        var settings = committed.Clone();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);

        settings.Direction = DirectionValue;
        settings.Type = TypeValue;
        settings.Unit = UnitValue;

        ReadInteger("overlap", Overlap, 0, settings, errors, value => settings.Overlap = value);

        if (settings.Direction == SplitDirection.Grid)
        {
            ReadInteger("rows", Rows, 1, settings, errors, value => settings.Rows = value);
            ReadInteger("columns", Columns, 1, settings, errors, value => settings.Columns = value);
        }
        else if (settings.Type != SplitType.Precise)
        {
            ReadInteger("count", Count, 1, settings, errors, value => settings.Count = value);
        }
        else if (settings.Unit == SplitUnit.Pixels)
        {
            ReadInteger("pixels", Pixels, SplitLayoutCalculator.MinimumPixels, settings, errors,
                value => settings.Pixels = value);
        }
        else
        {
            // 百分比原文照存：精确十进制由布局计算负责（转成 double 再回写会改掉用户填的数）
            settings.Percentage = (Percentage ?? "").Trim();
        }

        if (errors.Count == 0)
        {
            var calculated = SplitLayoutCalculator.Calculate(width, height, settings);
            foreach (var pair in calculated.Errors) errors[pair.Key] = pair.Value;
        }

        return (settings, errors);
    }

    /// <summary>切换单位时换算「每张尺寸」：百分比 ⇄ 像素（照上游 <c>changeUnit</c>）。</summary>
    /// <returns>换算后的原文；空串 = 换算不出来（界面清空输入框）。</returns>
    public string ConvertSize(bool toPixels, int dimension)
    {
        if (toPixels)
        {
            int? pixels = SplitLayoutCalculator.PercentagePixels(dimension, Percentage);
            return pixels?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        if (!int.TryParse((Pixels ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture,
                out int value))
        {
            return "";
        }

        return SplitLayoutCalculator.PixelsToPercentage(dimension, Math.Max(0, value));
    }

    public static SplitDirection ParseDirection(string? value) => value switch
    {
        "horizontal" => SplitDirection.Horizontal,
        "grid" => SplitDirection.Grid,
        _ => SplitDirection.Vertical,
    };

    public static SplitType ParseType(string? value) => value switch
    {
        "equal" => SplitType.Equal,
        "precise" => SplitType.Precise,
        _ => SplitType.Smart,
    };

    public static SplitUnit ParseUnit(string? value) =>
        value == "pixels" ? SplitUnit.Pixels : SplitUnit.Percentage;

    public static string KeyOf(SplitDirection value) => value switch
    {
        SplitDirection.Horizontal => "horizontal",
        SplitDirection.Grid => "grid",
        _ => "vertical",
    };

    public static string KeyOf(SplitType value) => value switch
    {
        SplitType.Equal => "equal",
        SplitType.Precise => "precise",
        _ => "smart",
    };

    public static string KeyOf(SplitUnit value) => value == SplitUnit.Pixels ? "pixels" : "percentage";

    private static void ReadInteger(string key, string? text, int minimum, SplitSettings settings,
        Dictionary<string, string> errors, Action<int> apply)
    {
        string draft = (text ?? "").Trim();
        if (!IntegerPattern.IsMatch(draft) ||
            !long.TryParse(draft, NumberStyles.None, CultureInfo.InvariantCulture,
                out long value) ||
            value < minimum || value > int.MaxValue)
        {
            errors[key] = minimum switch
            {
                0 => "请输入非负整数",
                16 => "请输入不小于 16 的整数",
                _ => "请输入正整数",
            };
            return;
        }

        apply((int)value);
    }
}
