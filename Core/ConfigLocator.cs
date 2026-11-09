using System.IO;
using LTools.Core.Storage.Migrations;

namespace LTools.Core;

/// <summary>
/// 配置路径定位 + 旧版配置文件名迁移。
/// 返回的是「锚点路径」：真正的配置在它同级的 <c>configs/</c> 目录（见 <see cref="Config.ConfigBundle"/>），
/// 尚未分域时锚点本身就是旧单文件 <c>LTools.json</c>。dev 上溯 3 级到仓库根，prod 取 exe 同级。
/// 发布包只携带 LTools.example.json（绝不携带用户配置），
/// 故本类只负责「找到已存在的用户配置 / 决定新建位置」。
/// </summary>
/// <remarks>
/// 版本 0 的改名 / ini 转换见 <see cref="LegacyFileStep"/>；拆域与动作目录化由
/// <see cref="MigrationRunner"/> 在拿到锚点后执行（启动时调用）。
/// 统一此处后，App / TerminalLauncher 不再各自拼路径
/// （原先 App 上溯 3 级、功能模块上溯 5 级，规则不一致）。
/// </remarks>
internal static class ConfigLocator
{
    public static string FindPath()
    {
        // 已存在（或能从未改名 / ini 迁移出）的配置：dev 仓库根优先，其次 exe 同级。
        string? promoted = LegacyFileStep.TryPromote(RepoRoot)
                           ?? LegacyFileStep.TryPromote(AppContext.BaseDirectory);
        if (promoted != null) return promoted;

        // 全新安装：dev（仓库根含项目文件）落在仓库根，否则落在 exe 同级。
        // 注意 prod 必须回到 exe 同级，不能沿用旧的 candidate[0]（否则会把配置写到 exe 上溯三级处）。
        string name = "LTools.json";
        return IsDevLayout
            ? Path.Combine(RepoRoot, name)
            : Path.Combine(AppContext.BaseDirectory, name);
    }

    /// <summary>仓库根（同 <see cref="AppPaths.RepoRoot"/>，统一在此委派）。</summary>
    private static string RepoRoot => AppPaths.RepoRoot;

    private static bool IsDevLayout => AppPaths.IsDevLayout;
}
