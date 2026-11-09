using System;
using System.IO;
using LTools.Config;
using LTools.Core;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 配置迁移入口：按 <see cref="ConfigSchema"/> 的版本逐级执行。
/// </summary>
/// <remarks>
/// <para>
/// 启动时由 <c>App.OnStartup</c> 在 <see cref="ConfigLocator.FindPath"/> 之后、<c>AppConfig.Load</c> 之前调用。
/// 每一步只做一件事（拆域 / 落动作目录），执行前重新探测版本——前一步可能顺带完成了后面的写入。
/// </para>
/// <para>
/// 失败只记录并停在原地：配置读取本就有「缺哪个域就用种子回填」的兜底，
/// 不能因为迁移失败让程序起不来；下一次启动会从同一版本重试。
/// </para>
/// </remarks>
internal static class MigrationRunner
{
    private static readonly IMigrationStep[] Steps =
    {
        new SplitDomainsStep(),
        new ActionDirectoriesStep(),
        new HotkeysStep(),
    };

    /// <summary>推进到当前版本；返回结束时的版本（用于日志 / 自检）。</summary>
    public static int RunPending(string anchorPath)
    {
        int start = ConfigSchema.Detect(anchorPath);
        if (start >= ConfigSchema.Current) return start;

        AppLog.Info("Migration", $"配置版本 {start} → {ConfigSchema.Current}（{Path.GetFileName(anchorPath)}）");
        foreach (var step in Steps)
        {
            if (ConfigSchema.Detect(anchorPath) != step.From) continue;

            try
            {
                step.Run(anchorPath);
                AppLog.Info("Migration", $"已完成 {step.From}→{step.To}：{step.Name}");
            }
            catch (Exception ex)
            {
                CrashLog.Write("Migration." + step.GetType().Name, ex);
                AppLog.Error("Migration",
                    $"{step.From}→{step.To} {step.Name} 失败（下次启动重试）：{ex.Message}");
                break; // 后面的步骤依赖这一步，别硬着头皮往下走
            }
        }

        int end = ConfigSchema.Detect(anchorPath);
        if (end < ConfigSchema.Current)
            AppLog.Warn("Migration", $"配置仍停在版本 {end}，本次按现有文件继续启动");
        return end;
    }
}
