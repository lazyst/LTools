using System.Collections.Generic;
using LTools.Core;
using LTools.Features;

namespace LTools.Config;

// 配置分域（见 docs/ARCHITECTURE.md §2）：一个文件一个属主。
// 这些类型只负责「磁盘形状」，字段含义仍以 AppConfig 上的注释为准。

/// <summary><c>config/settings.json</c>：全局行为配置。</summary>
public sealed class SettingsFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    /// <summary>CapsLock 键功能总开关。</summary>
    public bool CapsLockEnabled { get; set; } = true;

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

    /// <summary>鼠标模式速度 1..20。</summary>
    public int MouseModeSpeed { get; set; } = 1;

    /// <summary>动作运行结束后的提示策略。</summary>
    public RunNoticeMode RunNotice { get; set; } = RunNoticeMode.onFailure;

    /// <summary>「提示弹窗」的默认落点。</summary>
    public ToastPosition ToastPosition { get; set; } = ToastPosition.bottomRight;

    /// <summary>运行日志配置。</summary>
    public LogConfig Logging { get; set; } = new();
}

/// <summary><c>config/ui.json</c>：界面状态（丢了不影响功能，只是记忆丢失）。</summary>
public sealed class UiStateFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    /// <summary>设置窗口上次所在的导航页。</summary>
    public string LastNavPage { get; set; } = "actions";

    /// <summary>二级下拉导航中展开的父项。</summary>
    public List<string>? NavExpandedGroups { get; set; }

    /// <summary>上次选用的文字识别引擎。</summary>
    public string? LastOcrEngine { get; set; }

    /// <summary>组合动作编辑器左栏折叠的分组。</summary>
    public List<string> CompositeCollapsedGroups { get; set; } = new();

    /// <summary>动作选择器的「最近使用」。</summary>
    public List<string>? RecentActionIds { get; set; }
}

/// <summary>
/// 旧 <c>configs/actions.json</c>（P2 之前）：动作清单 + 菜单组。
/// 现在动作在 <c>actions/&lt;id&gt;/action.json</c>、菜单组在 <c>configs/menus.json</c>，
/// 这个类型只用来读迁移种子；首存后旧文件归档为 <c>actions.json.migrated</c>。
/// </summary>
public sealed class ActionLibraryFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public List<ActionDto> Actions { get; set; } = new();

    public List<MenuGroupDto?> MenuGroups { get; set; } = new();
}

/// <summary><c>configs/menus.json</c>：快捷菜单组（10 个槽位，项为动作 Id 引用）。</summary>
public sealed class MenuFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public List<MenuGroupDto?> MenuGroups { get; set; } = new();
}

/// <summary><c>config/panel.json</c>：超级面板（开关 / 长按阈值 / 旧页 / 新面板结构）。</summary>
public sealed class PanelFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>null = 尚未从旧 <see cref="SuperPanelConfig.Pages"/> 迁移。</summary>
    public PanelConfig? Panel { get; set; }
}

/// <summary><c>config/floating.json</c>：悬浮项偏好（悬浮项本身无状态，不落盘）。</summary>
public sealed class FloatingFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public FloatingConfig? Floating { get; set; }
}

/// <summary><c>config/macros.json</c>：宏设置 + 元数据（事件本体在 <c>macros/&lt;id&gt;.json</c>）。</summary>
public sealed class MacroFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public MacroConfig Macros { get; set; } = new();
}
