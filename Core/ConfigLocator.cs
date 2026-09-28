using System.IO;
using CapsLockPro.Config;

namespace CapsLockPro.Core;

/// <summary>
/// 配置路径定位 + 旧版 ini 一次性迁移。
/// 用户配置固定为 CapsLock++.json：dev 上溯 3 级到仓库根，prod 取 exe 同级。
/// 发布包只携带 CapsLock++.example.json（绝不携带 CapsLock++.json），
/// 故本类只负责「找到已存在的用户配置 / 决定其新建位置」；
/// 配置缺失时的默认值由 <see cref="AppConfig.Load"/> 回退读同目录示例提供，
/// 这样用户用新版 zip 解压覆盖旧版时，不会冲掉已有的 CapsLock++.json。
/// 首启检测：json 不存在但旧 CapsLock++.ini 存在 → 自动迁移（读 ini→写 json），
/// 旧 ini 改名 .migrated 备份，避免误用两套配置。
/// 统一此处后，App / MenuSystem / TerminalLauncher / MouseMode 不再各自拼路径
/// （原先 App 上溯 3 级、MouseMode 上溯 5 级，规则不一致）。
/// </summary>
internal static class ConfigLocator
{
    public static string FindPath()
    {
        // 已存在的用户配置：dev 仓库根优先，其次 exe 同级
        string devPath = Path.Combine(RepoRoot, "CapsLock++.json");
        string prodPath = Path.Combine(AppContext.BaseDirectory, "CapsLock++.json");
        if (File.Exists(devPath)) return devPath;
        if (File.Exists(prodPath)) return prodPath;

        // 迁移旧 ini（与 ini 同目录生成 json）
        foreach (var ini in new[]
        {
            Path.Combine(RepoRoot, "CapsLock++.ini"),
            Path.Combine(AppContext.BaseDirectory, "CapsLock++.ini"),
        })
        {
            if (!File.Exists(ini)) continue;
            var cfg = AppConfig.MigrateFromIni(ini);
            string jsonPath = Path.Combine(Path.GetDirectoryName(ini)!, "CapsLock++.json");
            cfg.Save(jsonPath);
            try { File.Move(ini, ini + ".migrated"); } catch { /* 备份失败不阻断 */ }
            return jsonPath;
        }

        // 全新安装：dev（仓库根含项目文件）落在仓库根，否则落在 exe 同级。
        // 注意 prod 必须回到 exe 同级，不能沿用旧的 candidate[0]（否则会把配置写到 exe 上溯三级处）。
        return IsDevLayout ? devPath : prodPath;
    }

    /// <summary>仓库根 = exe 目录上溯 3 级（bin/&lt;Config&gt;/net8.0-windows/）。</summary>
    private static string RepoRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    private static bool IsDevLayout =>
        File.Exists(Path.Combine(RepoRoot, "CapsLockPro.csproj"));
}
