using System;
using LTools.Core;

namespace LTools.Config;

/// <summary>
/// 按域读-改-写（P3）：每个 store 只碰自己那个文件，调用方拿到的也是该域的磁盘形状。
/// </summary>
/// <remarks>
/// <para>
/// 读仍然是「先按严格模式读整份聚合」（这样首次写入能带上示例模板的种子，
/// 且坏文件不会被默认值覆盖），但**写只写自己那一个域**——改一次鼠标速度不会再扫一遍
/// <c>actions/</c>、也不会重写其它域的时间戳。
/// </para>
/// <para>
/// 所有写入共用 <see cref="ConfigIO"/> 的一把锁，写操作串行。
/// 需要同时改多个域的（动作清单 + 快捷键）走 <see cref="ConfigIO.Modify(ConfigDomain, Action{AppConfig})"/>。
/// </para>
/// </remarks>
internal static class SettingsStore
{
    /// <summary>只读 settings 域（不扫 <c>actions/</c>），供设置页切页回填用。</summary>
    public static SettingsFile Read(string path) => ConfigBundle.ReadSettings(path);

    public static void Modify(Action<SettingsFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.settings, ConfigBundle.ToSettings, change, ConfigBundle.WriteSettings);

    public static void Modify(string path, Action<SettingsFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.settings, ConfigBundle.ToSettings, change, ConfigBundle.WriteSettings);
}

/// <summary><c>configs/ui.json</c>：导航页 / 最近使用 / OCR 偏好等界面记忆。</summary>
internal static class UiStore
{
    /// <summary>只读 ui 域（不扫 <c>actions/</c>），供设置窗口构造时恢复导航状态用。</summary>
    public static UiStateFile Read(string path) => ConfigBundle.ReadUi(path);

    public static void Modify(Action<UiStateFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.ui, ConfigBundle.ToUi, change, ConfigBundle.WriteUi);

    public static void Modify(string path, Action<UiStateFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.ui, ConfigBundle.ToUi, change, ConfigBundle.WriteUi);
}

/// <summary><c>configs/hotkeys.json</c>：预设组 + 绑定 + 默认名单（绑定只存动作 Id）。</summary>
internal static class HotkeyStore
{
    public static void Modify(Action<HotkeyFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.hotkeys, ConfigBundle.ToHotkeys, change, ConfigBundle.WriteHotkeys);

    public static void Modify(string path, Action<HotkeyFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.hotkeys, ConfigBundle.ToHotkeys, change, ConfigBundle.WriteHotkeys);
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

/// <summary><c>configs/triggers.json</c>：自动触发规则（只存动作 Id / 内置功能引用）。</summary>
internal static class TriggerStore
{
    public static void Modify(Action<TriggersFile> change)
        => ConfigIO.ModifyDomain(ConfigDomain.triggers, ConfigBundle.ToTriggers, change, ConfigBundle.WriteTriggers);

    public static void Modify(string path, Action<TriggersFile> change)
        => ConfigIO.ModifyDomain(path, ConfigDomain.triggers, ConfigBundle.ToTriggers, change, ConfigBundle.WriteTriggers);
}
