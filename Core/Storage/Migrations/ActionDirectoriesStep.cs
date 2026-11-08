using LTools.Config;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 版本 2 → 3：<c>configs/actions.json</c> 里的动作落成 <c>actions/&lt;id&gt;/action.json</c>。
/// </summary>
/// <remarks>
/// 只处理「六域已齐、还没有动作目录」的升级路径；单文件时代（版本 1）由
/// <see cref="SplitDomainsStep"/> 一次性写全。旧清单归档由 <see cref="ConfigBundle.CompleteMigration"/> 完成。
/// </remarks>
internal sealed class ActionDirectoriesStep : IMigrationStep
{
    public int From => ConfigSchema.SplitDomains;

    public int To => ConfigSchema.ActionDirectories;

    public string Name => "动作清单落成 actions/<id>/ 目录";

    public void Run(string anchorPath)
    {
        var cfg = AppConfig.Load(anchorPath);
        ConfigBundle.SaveDomains(anchorPath, cfg, ConfigDomain.actions);
    }
}
