using System.Collections.Generic;
using System.IO;
using LTools.Config;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 版本 3 → 4：旧「CapsLock 快捷菜单」并成一个「旧菜单」预设组，写 <c>hotkeys.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 只迁移**已有内容**：动作引用保留、快捷键留空（数字菜单本来就没有快捷键）。
/// 默认预设组「CapsLock 经典」由 <c>Features.HotkeySeeding</c> 在启动时按 <c>SeedVersion</c> 播种，
/// 不放在这一步——迁移只做一次，播种要按版本号判断，两者语义不同。
/// </para>
/// <para>
/// 可重入：<c>hotkeys.json</c> 已存在就直接跳过；写入后 <c>CompleteMigration</c> 会把
/// <c>menus.json</c> / 旧 <c>actions.json</c> 改名归档。
/// </para>
/// </remarks>
internal sealed class HotkeysStep : IMigrationStep
{
    public int From => ConfigSchema.ActionDirectories;

    public int To => ConfigSchema.HotkeysDomain;

    public string Name => "快捷菜单并入快捷键（hotkeys.json）";

    public void Run(string anchorPath)
    {
        string path = Path.Combine(ConfigBundle.ConfigDir(anchorPath), "hotkeys.json");
        if (File.Exists(path))
        {
            ConfigBundle.CompleteMigration(anchorPath);
            return;
        }

        List<MenuGroupDto?> legacy = ConfigBundle.ReadLegacyMenuGroups(anchorPath);
        var file = new HotkeyFile { Groups = ConfigBundle.ConvertLegacyMenus(legacy) };
        ConfigBundle.WriteHotkeys(anchorPath, file);
        ConfigBundle.CompleteMigration(anchorPath);
    }
}
