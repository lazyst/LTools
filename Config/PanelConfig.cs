using System.Collections.Generic;

namespace LTools.Config;

/// <summary>
/// 超级面板（动作面板）配置。设计见 <c>docs/PANEL_REDESIGN.md</c>。
/// 动作本体仍在 <see cref="AppConfig.Actions"/>，这里只存动作 Id 引用。
/// 为 null 表示「还没有迁移过旧 <see cref="SuperPanelConfig.Pages"/>」。
/// </summary>
public sealed class PanelConfig
{
    /// <summary>外观与行为偏好。</summary>
    public PanelPreferencesDto Preferences { get; set; } = new();

    /// <summary>全局区：与前台窗口无关，可有多套场景。</summary>
    public PanelAreaDto Global { get; set; } = new();

    /// <summary>上下文区：跟随前台窗口；<see cref="PanelAreaDto.Common"/> 为未匹配时的兜底分组。</summary>
    public PanelAreaDto Context { get; set; } = new();

    /// <summary>禁止弹出面板的进程（进程名或完整路径，忽略大小写）。</summary>
    public List<string> Blocked { get; set; } = new();

    /// <summary>是否已关掉「当前程序未配置场景」的提示。</summary>
    public bool HintDismissed { get; set; }
}

/// <summary>面板外观 / 行为偏好。</summary>
public sealed class PanelPreferencesDto
{
    /// <summary>窗口宽（DIP），关闭时记忆。</summary>
    public double Width { get; set; } = 560;

    /// <summary>窗口高（DIP），关闭时记忆。</summary>
    public double Height { get; set; } = 620;

    /// <summary>是否显示分组名。</summary>
    public bool ShowGroupTitles { get; set; } = true;

    /// <summary>紧凑磁贴（更矮、更小图标）。</summary>
    public bool CompactTiles { get; set; }

    /// <summary>全局区占上下两区总高度的比例（0.15~0.85），分割条拖动后记住。</summary>
    public double GlobalRatio { get; set; } = 0.5;

    /// <summary>
    /// 上次停留的页：<c>actions</c>（动作面板）/ <c>stash</c>（暂存区）。
    /// 只在窗口关闭时写回，切页不落盘。
    /// </summary>
    public string LastPage { get; set; } = "actions";
}

/// <summary>一个区（全局 / 上下文）的场景集合。</summary>
public sealed class PanelAreaDto
{
    /// <summary>扁平场景列表；子场景用 <see cref="PanelSceneDto.ParentId"/> 指向父场景。</summary>
    public List<PanelSceneDto> Scenes { get; set; } = new();

    /// <summary>上次选中的场景（全局区标签；上下文区仅作记忆）。</summary>
    public string? ActiveSceneId { get; set; }

    /// <summary>未匹配到场景时的兜底分组（上下文区使用）。</summary>
    public List<PanelGroupDto> Common { get; set; } = new();
}

/// <summary>面板场景。</summary>
public sealed class PanelSceneDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "app";
    public bool Enabled { get; set; } = true;

    /// <summary>窗口子场景指向父场景的 Id；普通场景为 null。</summary>
    public string? ParentId { get; set; }

    /// <summary>上下文匹配条件；全局场景忽略。</summary>
    public PanelMatchDto Match { get; set; } = new();

    public List<PanelGroupDto> Groups { get; set; } = new();
}

/// <summary>场景匹配条件。</summary>
public sealed class PanelMatchDto
{
    /// <summary>进程完整路径（优先，完全相等）。</summary>
    public string? Path { get; set; }

    /// <summary>进程名，如 <c>WeChat.exe</c>。</summary>
    public string? Exe { get; set; }

    /// <summary>窗口标题包含（忽略大小写）。</summary>
    public string? TitleContains { get; set; }

    /// <summary>窗口类名（完全相等）。</summary>
    public string? ClassName { get; set; }
}

/// <summary>场景内的一个分组。</summary>
public sealed class PanelGroupDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>动作 Id 列表（顺序即磁贴顺序）。</summary>
    public List<string> Actions { get; set; } = new();
}
