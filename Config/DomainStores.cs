using System;
using LTools.Core;

namespace LTools.Config;

/// <summary>
/// 按域读-改-写（P3）：每个 store 只碰自己那个文件，调用方拿到的也是该域的磁盘形状。
/// </summary>
/// <remarks>
/// <para>
/// 读仍然是「先按严格模式读整份聚合」（这样首次写入能带上示例模板 / 旧单文件里的种子，
/// 且坏文件不会被默认值覆盖），但**写只写自己那一个域**——改一次鼠标速度不会再扫一遍
/// <c>actions/</c>、也不会重写其它域的时间戳。
/// </para>
/// <para>
/// 所有写入共用 <see cref="ConfigIO"/> 的一把锁，与旧 <c>ConfigIO.Modify</c> 的串行语义一致。
/// 需要同时改多个域的（菜单组 + 动作清单）走 <see cref="ConfigIO.Modify(ConfigDomain, Action{AppConfig})"/>。
/// </para>
/// </remarks>
internal static class SettingsStore
{
    public static void Modify(Action<SettingsFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.settings, ConfigBundle.ToSettings, change, ConfigBundle.WriteSettings);

    public static void Modify(string path, Action<SettingsFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.settings, ConfigBundle.ToSettings, change, ConfigBundle.WriteSettings);
}

/// <summary><c>configs/ui.json</c>：导航页 / 最近使用 / OCR 偏好等界面记忆。</summary>
internal static class UiStore
{
    public static void Modify(Action<UiStateFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.ui, ConfigBundle.ToUi, change, ConfigBundle.WriteUi);

    public static void Modify(string path, Action<UiStateFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.ui, ConfigBundle.ToUi, change, ConfigBundle.WriteUi);
}

/// <summary><c>configs/menus.json</c>：快捷菜单组（项是动作 Id 引用）。</summary>
internal static class MenuStore
{
    public static void Modify(Action<MenuFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.menus, ConfigBundle.ToMenus, change, ConfigBundle.WriteMenus);

    public static void Modify(string path, Action<MenuFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.menus, ConfigBundle.ToMenus, change, ConfigBundle.WriteMenus);
}

/// <summary><c>configs/panel.json</c>：超级面板开关 / 长按阈值 / 场景与偏好。</summary>
internal static class PanelStore
{
    public static void Modify(Action<PanelFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.panel, ConfigBundle.ToPanel, change, ConfigBundle.WritePanel);

    public static void Modify(string path, Action<PanelFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.panel, ConfigBundle.ToPanel, change, ConfigBundle.WritePanel);
}

/// <summary><c>configs/floating.json</c>：悬浮项偏好（悬浮项本身不落盘）。</summary>
internal static class FloatingStore
{
    public static void Modify(Action<FloatingFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.floating, ConfigBundle.ToFloating, change, ConfigBundle.WriteFloating);

    public static void Modify(string path, Action<FloatingFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.floating, ConfigBundle.ToFloating, change, ConfigBundle.WriteFloating);
}

/// <summary><c>configs/macros.json</c>：宏设置 + 宏元数据（事件本体仍在 <c>macros/&lt;id&gt;.json</c>）。</summary>
internal static class MacroFileStore
{
    public static void Modify(Action<MacroFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.macros, ConfigBundle.ToMacros, change, ConfigBundle.WriteMacros);

    public static void Modify(string path, Action<MacroFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.macros, ConfigBundle.ToMacros, change, ConfigBundle.WriteMacros);
}
