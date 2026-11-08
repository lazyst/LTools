using LTools.Config;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 版本 1 → 2：单文件 <c>LTools.json</c> 拆成 <c>configs/</c> 六个域。
/// </summary>
/// <remarks>
/// 用 <see cref="ConfigBundle.SaveDomains"/> 而不是只写六域：同一次调用里把动作也落成目录，
/// 之后才归档旧单文件。否则「先归档旧文件、再建动作目录」中途崩溃就会把动作清单弄丢。
/// 因此这一步跑完磁盘通常已经是版本 3。
/// </remarks>
internal sealed class SplitDomainsStep : IMigrationStep
{
    public int From => ConfigSchema.SingleFile;

    public int To => ConfigSchema.SplitDomains;

    public string Name => "单文件拆成 configs/ 六域";

    public void Run(string anchorPath)
    {
        var cfg = AppConfig.Load(anchorPath);
        ConfigBundle.SaveDomains(anchorPath, cfg, ConfigDomain.all);
    }
}
