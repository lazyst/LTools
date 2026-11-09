using System.IO;
using LTools.Config;

namespace LTools.Core.Storage.Migrations;

/// <summary>
/// 配置存储的结构版本（见 <c>docs/ARCHITECTURE.md</c> P4）。
/// <code>
/// 0  CapsLock++.json / .ini（改名前）
/// 1  单文件 LTools.json
/// 2  configs/ 六域齐全，动作还是 configs/actions.json
/// 3  configs/ 六域 + actions/&lt;id&gt;/ 动作目录（menus.json 还是快捷菜单组）
/// 4  hotkeys.json 取代 menus.json（快捷菜单已并入预设组）
/// </code>
/// </summary>
internal static class ConfigSchema
{
    /// <summary>当前程序写出的版本。</summary>
    public const int Current = 4;

    public const int LegacyFileName = 0;
    public const int SingleFile = 1;
    public const int SplitDomains = 2;
    public const int ActionDirectories = 3;
    public const int HotkeysDomain = 4;

    /// <summary>
    /// 从磁盘现状推断版本——不额外写「版本标记」文件，避免标记与实际内容不一致。
    /// 全新安装（什么旧文件都没有）直接按当前版本对待，启动时不写任何文件。
    /// </summary>
    public static int Detect(string anchorPath)
    {
        if (ConfigBundle.IsFullySplit(anchorPath))
            return ConfigSchema.HotkeysDomain;

        // 旧六域（含 menus.json、没有 hotkeys.json）——等 HotkeysStep 把它并成预设组。
        if (ConfigBundle.IsLegacySplit(anchorPath))
            return ActionStore.HasStore(anchorPath) ? ActionDirectories : SplitDomains;

        if (File.Exists(anchorPath)) return SingleFile;
        if (File.Exists(Path.Combine(Dir(anchorPath), "CapsLock++.json"))
            || File.Exists(Path.Combine(Dir(anchorPath), "CapsLock++.ini")))
            return LegacyFileName;
        return Current;
    }

    private static string Dir(string anchorPath) =>
        Path.GetDirectoryName(Path.GetFullPath(anchorPath)) ?? Directory.GetCurrentDirectory();
}
