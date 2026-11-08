using System;
using System.IO;
using System.Text;
using System.Text.Json;
using LTools.Config;
using LTools.Core;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 版本 0：改名前的旧配置 → 同目录 <c>LTools.json</c>。
/// <list type="bullet">
/// <item><c>CapsLock++.json</c>：直接改名（内容原样保留，可回滚）。</item>
/// <item><c>CapsLock++.ini</c>：读入后写成 <c>LTools.json</c>，ini 改名 <c>.migrated</c> 备份。</item>
/// </list>
/// </summary>
/// <remarks>
/// 只负责生成单文件，不拆域——拆域是下一步（<see cref="SplitDomainsStep"/>）。
/// 由 <see cref="ConfigLocator.FindPath"/> 在定位配置时调用（路径本身取决于迁移结果）。
/// </remarks>
internal static class LegacyFileStep
{
    /// <summary>
    /// 在 <paramref name="dir"/> 里找可用的 <c>LTools.json</c>：已有就直接返回，
    /// 否则尝试从旧文件名迁移。没有任何配置或迁移失败时返回 null。
    /// </summary>
    public static string? TryPromote(string dir)
    {
        string target = Path.Combine(dir, "LTools.json");
        if (File.Exists(target)) return target;

        string legacyJson = Path.Combine(dir, "CapsLock++.json");
        if (File.Exists(legacyJson))
        {
            try
            {
                File.Move(legacyJson, target);
                AppLog.Info("Migration", "旧配置 CapsLock++.json 已改名为 LTools.json");
                return target;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Migration", $"旧配置改名失败（继续尝试其它迁移）：{ex.Message}");
            }
        }

        string ini = Path.Combine(dir, "CapsLock++.ini");
        if (!File.Exists(ini)) return null;

        try
        {
            var cfg = AppConfig.MigrateFromIni(ini);
            string json = JsonSerializer.Serialize(cfg, ConfigBundle.Options);
            File.WriteAllText(target, json, new UTF8Encoding(false));
            try { File.Move(ini, ini + ".migrated"); } catch { /* 备份失败不阻断 */ }
            AppLog.Info("Migration", "旧配置 CapsLock++.ini 已转换为 LTools.json");
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Migration", $"旧 ini 转换失败：{ex.Message}");
            return null;
        }
    }
}
