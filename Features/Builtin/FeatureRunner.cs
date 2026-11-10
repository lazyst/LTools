using System;
using System.Threading.Tasks;
using LTools.Core;

namespace LTools.Features.Builtin;

/// <summary>
/// 运行内置功能：解析参数 → 后台线程执行（与动作一样不阻塞钩子 / 面板）。
/// </summary>
/// <remarks>
/// 功能都是「一下就好」的短操作，不走 <see cref="AutomationRunner"/>（没有步骤轨迹、没有运行指示器）；
/// 失败用常驻提示卡片报出来，点一下打开日志目录。
/// </remarks>
internal static class FeatureRunner
{
    /// <summary>按引用运行；引用不是内置功能时返回 false（调用方接着按动作找）。</summary>
    public static bool TryRun(string reference, int cursorX, int cursorY)
    {
        var def = FeatureCatalog.Find(FeatureRef.IdOf(reference));
        if (def == null) return false;

        var values = def.Resolve(FeatureRef.Parse(reference));
        string name = def.Name;
        long started = Environment.TickCount64;

        Task.Run(() =>
        {
            try
            {
                def.Run(new FeatureRunContext(cursorX, cursorY, values));
                AppLog.Info("Feature", $"完成「{name}」· {Environment.TickCount64 - started} ms");
            }
            catch (Exception ex)
            {
                AppLog.Error("Feature", $"失败「{name}」", ex);
                CrashLog.Write($"Feature[{name}]", ex);
                ToastService.Show(
                    ex.Message,
                    $"功能失败 · {name}",
                    ToastService.StickyMs,
                    ToastKind.error,
                    onClick: () =>
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(
                                new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsDir}\"")
                                { UseShellExecute = true });
                        }
                        catch { /* 打不开就算了 */ }
                    },
                    actionText: "查看日志");
            }
        });
        return true;
    }
}
