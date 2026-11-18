using System;
using System.Collections.Generic;
using System.Numerics;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 从设置算出标称切分布局（等分 / 精确像素 / 精确百分比 / 宫格）。照上游
/// <c>calculateImageSplitLayout</c>；智能切分的「找线」在 <see cref="SplitSmart"/>。
/// </summary>
/// <remarks>
/// <para>
/// **百分比按十进制精确算**：上游用字符串乘法 + 首位小数进位，避免二进制浮点在「刚好半个
/// 像素」上抖到另一边。这里用 <see cref="BigInteger"/> 做同一件事（整数部分 + 首位被丢掉的
/// 小数位 ≥ 5 就进位），结果与上游逐像素一致。
/// </para>
/// <para>
/// 错误按字段名登记（<c>overlap</c> / <c>image</c> / <c>count</c> / <c>rows</c> /
/// <c>columns</c> / <c>pixels</c> / <c>percentage</c> / <c>type</c> / <c>direction</c>），
/// 界面把错误挂回对应输入框，而不是弹一句「切不了」。
/// </para>
/// </remarks>
internal static class SplitLayoutCalculator
{
    public const int MinimumPixels = 16;

    /// <summary>标称布局：方向 / 方式 / 张数 / 像素 / 百分比 / 宫格行列。</summary>
    public static SplitLayout Calculate(int width, int height, SplitSettings settings)
    {
        var layout = new SplitLayout(width, height) { Overlap = settings.Overlap };

        if (settings.Overlap < 0) layout.Errors["overlap"] = "请输入非负整数";
        if (width <= 0 || height <= 0)
        {
            layout.Errors["image"] = "图片不可用";
            return layout;
        }

        switch (settings.Direction)
        {
            case SplitDirection.Grid:
                Equal(width, settings.Columns, "columns", layout, layout.Vertical);
                Equal(height, settings.Rows, "rows", layout, layout.Horizontal);
                break;

            case SplitDirection.Vertical:
            case SplitDirection.Horizontal:
            {
                bool vertical = settings.Direction == SplitDirection.Vertical;
                int dimension = vertical ? width : height;
                var lines = vertical ? layout.Vertical : layout.Horizontal;
                switch (settings.Type)
                {
                    case SplitType.Equal:
                    case SplitType.Smart:
                        Equal(dimension, settings.Count, "count", layout, lines);
                        break;
                    case SplitType.Precise:
                        Precise(dimension, settings, layout, lines);
                        break;
                    default:
                        layout.Errors["type"] = "请选择切分方式";
                        break;
                }
                break;
            }

            default:
                layout.Errors["direction"] = "请选择切分方向";
                break;
        }

        return layout;
    }

    /// <summary>
    /// 百分比换成多少像素（只用于「单位切换」与表单提示）。照上游
    /// <c>imageSplitPercentagePixels</c>：**不限 5%–100%**，认不出就是 null。
    /// </summary>
    public static int? PercentagePixels(int dimension, string? percentage)
    {
        if (dimension <= 0) return null;
        var value = DecimalPercentage.TryParse(percentage);
        return value?.Boundary(dimension, 1);
    }

    /// <summary>像素换成百分比原文（两位小数，四舍五入）；转换不出来是空串。</summary>
    public static string PixelsToPercentage(int dimension, int pixels)
    {
        if (dimension <= 0 || pixels < 0) return "";
        long hundredths = ((long)pixels * 10000 + dimension / 2) / dimension;
        return $"{hundredths / 100}.{hundredths % 100:00}";
    }

    /// <summary>
    /// 等分：前 <c>extra</c> 段各多 1 像素，多出来的段按「先宽后窄」铺开（照上游
    /// <c>equalLines</c> 的两段等差数列）。
    /// </summary>
    private static bool Equal(int dimension, int count, string field, SplitLayout layout,
        SplitLines destination)
    {
        if (count <= 0)
        {
            layout.Errors[field] = "请输入正整数";
            return false;
        }

        if (count > dimension)
        {
            layout.Errors[field] = "每张图至少要有 1 像素宽高";
            return false;
        }

        if (count == 1) return true;

        int section = dimension / count;
        int extra = dimension % count;
        int leading = Math.Min(extra, count - 1);
        int remaining = count - 1 - leading;

        for (int i = 0; i < leading; i++) destination.Append((i + 1) * (section + 1));

        long start = (long)extra * (section + 1) + section;
        for (int i = 0; i < remaining; i++) destination.Append((int)(start + (long)i * section));

        return true;
    }

    /// <summary>精确切分：按像素（每张至少 16）或按百分比（5%–100%，可带小数）。</summary>
    private static bool Precise(int dimension, SplitSettings settings, SplitLayout layout,
        SplitLines destination)
    {
        if (settings.Unit == SplitUnit.Pixels)
        {
            if (settings.Pixels < MinimumPixels)
            {
                layout.Errors["pixels"] = "至少填 16 像素";
                return false;
            }

            // 可能一条线都没有：图比设定的每张尺寸还小 = 不切（照上游 `(dimension-1)/pixels`）
            int count = (dimension - 1) / settings.Pixels;
            for (int i = 0; i < count; i++) destination.Append((i + 1) * settings.Pixels);
            return true;
        }

        if (settings.Unit != SplitUnit.Percentage)
        {
            layout.Errors["percentage"] = "请选择像素或百分比";
            return false;
        }

        var percentage = DecimalPercentage.TryParsePercentage(settings.Percentage);
        if (percentage == null)
        {
            layout.Errors["percentage"] = "请输入 5% 到 100% 之间的百分比";
            return false;
        }

        var denominator = percentage.Denominator;
        if (percentage.Digits * dimension < denominator)
        {
            layout.Errors["percentage"] = "每张图至少要有 1 像素宽高";
            return false;
        }

        // 从 1 倍往 20 倍铺，铺满（digits*multiple >= 分母）就停；任何一段算不出 / 不递增
        // 都说明这个百分比在这张图上切不了（照上游 preciseLines 的循环）
        for (int multiple = 1; multiple <= 20; multiple++)
        {
            if (percentage.Digits * multiple >= denominator) break;

            int? boundary = percentage.Boundary(dimension, multiple);
            if (boundary == null || boundary.Value >= dimension)
            {
                layout.Errors["percentage"] = "每张图至少要有 1 像素宽高";
                return false;
            }

            int coordinate = boundary.Value;
            if (coordinate <= 0 ||
                (destination.Count > 0 && coordinate <= destination[destination.Count - 1]))
            {
                layout.Errors["percentage"] = "每张图至少要有 1 像素宽高";
                return false;
            }

            destination.Append(coordinate);
        }

        return true;
    }

    /// <summary>
    /// 解析好的十进制百分比：<see cref="Digits"/> = 百分比 × 10^<see cref="FractionalDigits"/>。
    /// 例：<c>12.5</c> → digits 125、小数 1 位；<c>50</c> → digits 50、0 位。
    /// </summary>
    private sealed class DecimalPercentage
    {
        private DecimalPercentage(BigInteger digits, int fractionalDigits)
        {
            Digits = digits;
            FractionalDigits = fractionalDigits;
        }

        public BigInteger Digits { get; }

        public int FractionalDigits { get; }

        /// <summary>100 × 10^小数位（百分比的上限，也是「铺满」的判据）。</summary>
        public BigInteger Denominator => BigInteger.Pow(10, FractionalDigits) * 100;

        /// <summary>按 <paramref name="multiple"/> 倍铺一格时，边界落在哪个像素。</summary>
        public int? Boundary(int dimension, int multiple)
        {
            BigInteger product = Digits * dimension * multiple;
            BigInteger scale = BigInteger.Pow(10, FractionalDigits + 2);
            BigInteger whole = product / scale;

            // 首位被丢掉的小数位 ≥ 5 就进位（上游也只认这一位，不看后面的）
            BigInteger firstDropped = product / BigInteger.Pow(10, FractionalDigits + 1) % 10;
            BigInteger result = whole + (firstDropped >= 5 ? 1 : 0);

            if (result > int.MaxValue) return null;
            return (int)result;
        }

        /// <summary>只认十进制原文（可带 <c>+</c> 与小数），不做 5–100 的取值校验。</summary>
        public static DecimalPercentage? TryParse(string? value)
        {
            var text = (value ?? "").Trim();
            if (text.Length == 0 || text.Length > 4096) return null;
            if (text[0] == '+') text = text[1..];

            int separator = text.IndexOf('.');
            string whole = separator < 0 ? text : text[..separator];
            string fractional = separator < 0 ? "" : text[(separator + 1)..];

            if (whole.Length == 0 || !AllDigits(whole) || !AllDigits(fractional)) return null;

            // 整数的前导零与小数末尾的零都不改变数值，但会改变小数位；照上游先规整
            whole = Normalize(whole);
            fractional = fractional.TrimEnd('0');

            var digits = BigInteger.Parse(Normalize(whole + fractional));
            return new DecimalPercentage(digits, fractional.Length);
        }

        /// <summary>切分用的百分比：<see cref="TryParse"/> + 5% ≤ 值 ≤ 100%。</summary>
        public static DecimalPercentage? TryParsePercentage(string? value)
        {
            var parsed = TryParse(value);
            if (parsed == null) return null;

            var minimum = BigInteger.Pow(10, parsed.FractionalDigits) * 5;
            return parsed.Digits < minimum || parsed.Digits > parsed.Denominator ? null : parsed;
        }

        private static bool AllDigits(string text)
        {
            foreach (char c in text)
            {
                if (c is < '0' or > '9') return false;
            }
            return true;
        }

        private static string Normalize(string digits)
        {
            int first = 0;
            while (first + 1 < digits.Length && digits[first] == '0') first++;
            return digits[first..];
        }
    }
}
