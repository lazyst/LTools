using System;
using Cronos;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// Cron 表达式的统一解析 / 求下次触发（<c>docs/AUTO_TRIGGER.md</c> §13.8）。
/// <see cref="TriggerScheduler"/> 排程与 <c>TriggerRuleEditDialog</c> 的保存校验都调这里，
/// 别各写一套解析（否则界面放行的表达式运行期才炸）。引擎用 Quicker 同款 <c>Cronos</c>。
/// </summary>
/// <remarks>
/// 支持 5 段（分 时 日 月 周）与 6 段含秒（秒 分 时 日 月 周），按空白段数自动选
/// <see cref="CronFormat.Standard"/> / <see cref="CronFormat.IncludeSeconds"/>；时区用本地时间。
/// </remarks>
internal static class CronSchedule
{
    /// <summary>解析 Cron；失败返回 false 并给出可直接显示的中文错误文案。</summary>
    public static bool TryParse(string? text, out string error) => Parse(text, out error) != null;

    /// <summary>相对 <paramref name="now"/>（本地时间）的下一次本地触发时间；解析失败 / 无下次返回 null。</summary>
    public static DateTime? Next(string? text, DateTime now)
    {
        var cron = Parse(text, out _);
        if (cron == null) return null;
        try
        {
            // Cronos 只吃 UTC / DateTimeOffset，本地时区要显式传，DST 由它自己处理。
            var from = new DateTimeOffset(now.ToUniversalTime());
            return cron.GetNextOccurrence(from, TimeZoneInfo.Local)?.DateTime;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Triggers", "Cron 求下次触发失败：" + ex.Message);
            return null;
        }
    }

    private static CronExpression? Parse(string? text, out string error)
    {
        error = "";
        string expr = (text ?? "").Trim();
        if (expr.Length == 0) { error = "表达式为空"; return null; }
        try
        {
            int fields = expr.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            var format = fields >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard;
            return CronExpression.Parse(expr, format);
        }
        catch (CronFormatException ex) { error = ex.Message; return null; }
        catch (Exception ex) { error = ex.Message; return null; }
    }
}
