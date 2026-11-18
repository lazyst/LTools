using System;
using System.Globalization;

namespace LTools.Features.CapturePro;

/// <summary>测距单位（顺序与线名照上游 <c>DistanceUnit</c>，默认厘米）。</summary>
internal enum DistanceUnit
{
    Px,
    Cm,
    M,
    Km,
    Mm,
    Pt,
    In,
    Ft,
    Um,
}

/// <summary>测角类型（上游 <c>AngleType</c>：Directed = 360° 有向角，Interior = 180° 内角）。</summary>
internal enum AngleKind
{
    Directed,
    Interior,
}

/// <summary>测角单位（上游 <c>AngleUnit</c>）。</summary>
internal enum AngleUnit
{
    Degrees,
    Radians,
}

/// <summary>测距的一整套参数（上游 <c>DistanceAnnotation</c> + 端点样式）。</summary>
internal readonly record struct DistanceSpec(
    DistanceUnit Unit,
    double Factor,
    int Decimals,
    double EndpointScale,
    Arrowhead Endpoint)
{
    /// <summary>上游默认：厘米 / 系数 1 / 0 位小数 / 端点比例 1 / 横杠端点。</summary>
    public static readonly DistanceSpec Default =
        new(DistanceUnit.Cm, 1.0, 0, 1.0, Arrowhead.Bar);

    /// <summary>系数范围（上游 <c>validate_distance_annotation</c>：0.01–1000）。</summary>
    public DistanceSpec Clamped() => new(
        Unit,
        double.IsFinite(Factor) ? Math.Clamp(Factor, 0.01, 1000) : 1.0,
        MeasureLabel.ClampDecimals(Decimals),
        double.IsFinite(EndpointScale) ? Math.Clamp(EndpointScale, ArrowInk.MinRatio, ArrowInk.MaxRatio) : 1.0,
        Endpoint);
}

/// <summary>测角的一整套参数（上游 <c>AngleAnnotation</c> 的显示部分）。</summary>
internal readonly record struct AngleSpec(AngleKind Kind, AngleUnit Unit, int Decimals)
{
    public static readonly AngleSpec Default = new(AngleKind.Directed, AngleUnit.Degrees, 0);

    public AngleSpec Clamped() => new(Kind, Unit, MeasureLabel.ClampDecimals(Decimals));
}

/// <summary>
/// 测距 / 测角的数值与文案（移植 <c>document/src/distance.rs</c>、<c>angle.rs</c>）：
/// 一个数字 + 一个小数位规则 + 一个单位后缀，绘制与自检共用。
/// </summary>
internal static class MeasureLabel
{
    /// <summary>上游小数位上限（3）。</summary>
    public const int MaxDecimals = 3;

    /// <summary>注释字号 = 20 × √(线宽 / 2)（上游 <c>distance_label</c> / <c>angle_label</c>）。</summary>
    public static double FontSize(double strokeWidth) =>
        Math.Clamp(20.0 * Math.Sqrt(Math.Max(strokeWidth, 0.1) / 2.0), 6, 160);

    /// <summary>单位的显示后缀（<c>Um</c> 是 <c>μm</c>）。</summary>
    public static string Suffix(DistanceUnit unit) => unit switch
    {
        DistanceUnit.Px => "px",
        DistanceUnit.Cm => "cm",
        DistanceUnit.M => "m",
        DistanceUnit.Km => "km",
        DistanceUnit.Mm => "mm",
        DistanceUnit.Pt => "pt",
        DistanceUnit.In => "in",
        DistanceUnit.Ft => "ft",
        _ => "μm",
    };

    public static int ClampDecimals(int decimals) => Math.Clamp(decimals, 0, MaxDecimals);

    /// <summary>
    /// 测距数值：长度（画布 DIP）× 物理缩放 × 系数（上游 <c>distance_value</c>：
    /// <c>hypot(dx·px, dy·px) × factor</c>，屏幕上就是「物理像素 × factor」）。
    /// </summary>
    public static double DistanceValue(double lengthDip, double pixelScale, double factor) =>
        Math.Max(0, lengthDip) * (pixelScale <= 0 ? 1 : pixelScale) * factor;

    /// <summary>测距标签：<c>"{值} {单位}"</c>（上游固定一个空格，`μm` 也一样）。</summary>
    public static string Distance(double lengthDip, double pixelScale, double factor, DistanceUnit unit,
        int decimals)
    {
        double value = DistanceValue(lengthDip, pixelScale, factor);
        string number = value.ToString("F" + ClampDecimals(decimals), CultureInfo.CurrentCulture);
        return number + " " + Suffix(unit);
    }

    /// <summary>测角弧度：Interior 把大于 180° 的有向角折成内角（上游 <c>2π − sweep</c>）。</summary>
    public static double InteriorRadians(double radians)
    {
        double sweep = Math.Abs(radians) % (Math.PI * 2);
        return sweep > Math.PI ? Math.PI * 2 - sweep : sweep;
    }

    /// <summary>
    /// 测角标签：度带 <c>°</c>（无空格）、弧度带 <c>" rad"</c>（前导空格），
    /// 小数位由参数决定（上游 <c>format_angle_value</c>）。
    /// </summary>
    public static string Angle(double radians, AngleKind kind, AngleUnit unit, int decimals)
    {
        double sweep = kind == AngleKind.Interior ? InteriorRadians(radians) : radians;
        int places = ClampDecimals(decimals);
        return unit == AngleUnit.Radians
            ? sweep.ToString("F" + places, CultureInfo.CurrentCulture) + " rad"
            : (sweep * 180.0 / Math.PI).ToString("F" + places, CultureInfo.CurrentCulture) + "°";
    }

    // ============ 参数行文案（胶囊 / tooltip 共用） ============

    public static string Chip(DistanceUnit unit) => Suffix(unit);

    public static string Name(DistanceUnit unit) => "单位：" + Suffix(unit);

    public static string Chip(AngleKind kind) => kind == AngleKind.Interior ? "内角" : "有向";

    public static string Name(AngleKind kind) => "角度类型：" + Chip(kind);

    public static string Chip(AngleUnit unit) => unit == AngleUnit.Radians ? "弧度" : "度";

    public static string Name(AngleUnit unit) => "单位：" + Chip(unit);

    /// <summary>端点 / 箭头样式的短名（14 种，参数行胶囊放不下长名就用符号）。</summary>
    public static string Chip(Arrowhead head) => head switch
    {
        Arrowhead.None => "无",
        Arrowhead.Arrow => "箭头",
        Arrowhead.Bar => "横杠",
        Arrowhead.Dot => "圆点",
        Arrowhead.Circle => "实圆",
        Arrowhead.CircleOutline => "空圆",
        Arrowhead.Triangle => "三角",
        Arrowhead.TriangleOutline => "空三角",
        Arrowhead.Diamond => "菱形",
        Arrowhead.DiamondOutline => "空菱形",
        Arrowhead.CrowfootOne => "一趾",
        Arrowhead.CrowfootMany => "多趾",
        Arrowhead.CrowfootOneOrMany => "多趾杠",
        Arrowhead.Square => "方块",
        Arrowhead.InvertedTriangle => "倒三角",
        _ => "凹三角",
    };

    public static string Name(Arrowhead head) => head == Arrowhead.None ? "端点：无" : "端点：" + Chip(head);
}
