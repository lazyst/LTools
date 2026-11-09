using System.IO;

namespace LTools.Core;

/// <summary>
/// 配置路径定位：返回「锚点路径」，真正的配置在它同级的 <c>configs/</c> 目录
/// （见 <see cref="Config.ConfigBundle"/>）。dev 上溯 3 级到仓库根，prod 取 exe 同级。
/// </summary>
/// <remarks>
/// 发布包只携带 <c>LTools.example.json</c>（绝不携带用户配置），所以锚点只决定
/// 「配置写在哪」，文件本身不需要存在。
/// </remarks>
internal static class ConfigLocator
{
    public static string FindPath() => Path.Combine(
        AppPaths.IsDevLayout ? AppPaths.RepoRoot : AppContext.BaseDirectory, "LTools.json");
}
