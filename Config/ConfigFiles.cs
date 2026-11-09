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

    /// <summary>CapsLock 当快捷键修饰键（短按发 Esc）。</summary>
    public bool CapsLockModifier { get; set; } = true;

    /// <summary>鼠标停在屏幕边缘时滚轮调系统音量。</summary>
    public bool EdgeWheelVolume { get; set; } = true;

    /// <summary>边缘滚轮音量算哪条边。</summary>
    public WheelVolumeEdge EdgeWheelVolumeEdge { get; set; } = WheelVolumeEdge.bottom;

    /// <summary>终端路径覆盖（如 gitbash）。</summary>
    public Dictionary<string, string> TerminalPaths { get; set; } = new();

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

    /// <summary>动作编辑器左栏已折叠的单元分类。</summary>
    public List<string> ToolboxCollapsedGroups { get; set; } = new();

    /// <summary>动作选择器的「最近使用」。</summary>
    public List<string>? RecentActionIds { get; set; }
}

/// <summary><c>configs/panel.json</c>：超级面板（开关 / 长按阈值 / 面板结构）。</summary>
public sealed class PanelFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    public SuperPanelConfig SuperPanel { get; set; } = new();

    /// <summary>面板结构；null = 全新安装（由 <c>PanelLibrary</c> 建默认场景）。</summary>
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
