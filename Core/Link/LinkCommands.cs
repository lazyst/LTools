using System.Collections.Generic;
using LTools.Config;

namespace LTools.Core.Link;

/// <summary>
/// 链路两端约定的方法名与负载形状（见 <c>docs/PROCESS_SPLIT.md</c> §5）。
/// </summary>
/// <remarks>
/// 这一轮只落「面板 / 动作」这一小段（拆分后第一个搬进宿主的窗口）。
/// 其它方法（<c>run</c> / <c>toast</c> / <c>trace</c> …）在真搬执行器时再补，
/// 补的时候**只加常量 + DTO**，别把收发逻辑写进这里。
/// </remarks>
internal static class LinkMethods
{
    // —— 宿主 → 提权进程（请求 / 答复，面板与动作的读写）——

    /// <summary>要一份当前面板 + 动作摘要（宿主首次显示 / 断线重连后重建）。</summary>
    public const string PanelSnapshot = "panel.snapshot";

    /// <summary>宿主把编辑后的整份面板配置交回，由提权进程落盘。</summary>
    public const string PanelReplace = "panel.replace";

    /// <summary>新建一个空动作（名称 / 图标），返回完整 <c>ActionDto</c>。</summary>
    public const string ActionAdd = "action.add";

    /// <summary>删除动作（含快捷键 / 面板 / 触发 / 悬浮引用与资源回收）。</summary>
    public const string ActionDelete = "action.delete";

    /// <summary>复制动作（新 Id + 名称「副本」+ 素材一并带上）。</summary>
    public const string ActionDuplicate = "action.duplicate";

    /// <summary>执行一个动作 / 内置功能引用（宿主的磁贴点击转给提权进程真正执行）。</summary>
    public const string RunAction = "run.action";

    /// <summary>在提权进程里打开一个窗口（宿主点「设置 / 暂存区 / 面板场景」时转过去）。</summary>
    public const string OpenWindow = "open.window";

    /// <summary>面板的动作级操作（新建 / 添加已有 / 编辑 / 删除 / 粘贴 / 导出 / 另存为子动作）。</summary>
    public const string ActionOp = "panel.op";

    // —— 提权进程 → 宿主（事件，这一轮先定义好，宿主侧接线时用）——

    /// <summary>显示面板（鼠标手势 / 快捷键在提权侧的钩子里触发）。负载：<see cref="PanelShowRequest"/>。</summary>
    public const string EventPanelShow = "panel.show";

    /// <summary>切换面板显示 / 关闭（CapsLock+T 这类「开 / 关」入口）。</summary>
    public const string EventPanelToggle = "panel.toggle";

    /// <summary>提权进程连上并握手完成（宿主收到后拉快照 / 重建界面）。</summary>
    public const string EventClientReady = "client.ready";

    /// <summary>动作库变了（新建 / 删除 / 改名 / 采纳草稿），宿主该重拉快照。</summary>
    public const string EventActionsChanged = "actions.changed";
}

/// <summary>宿主要在网格里画的一个磁贴（只带画磁贴需要的字段）。</summary>
internal sealed record ActionSummary(string Id, string Name, string Icon, int Steps);

/// <summary>面板快照：整份配置 + 动作摘要表（宿主据此重建界面）。</summary>
internal sealed record PanelSnapshot(PanelConfig Panel, List<ActionSummary> Actions);

/// <summary>新建动作：名称必填，图标可空（空则由服务端补默认）。</summary>
internal sealed record ActionAddRequest(string Name, string? Icon);

/// <summary>按 Id 定位一个动作（删除 / 复制共用）。</summary>
internal sealed record ActionIdRequest(string Id);

/// <summary>
/// 显示面板。窗口句柄是「按下右键时鼠标所在的顶层窗口」（会话上下文按它算），
/// 用 <see cref="long"/> 而不是 <c>IntPtr</c>，跨 JSON 直接走整数。
/// 光标坐标是唤起瞬间的屏幕物理像素（面板据此定位；0/0 表示让宿主自己取当前光标）。
/// </summary>
internal sealed record PanelShowRequest(long Hwnd, int X, int Y);

/// <summary>
/// 执行一个动作 / 内置功能。引用形如 <c>01abc…</c>（动作 Id）或 <c>common.capture</c>（内置功能）。
/// <c>Source</c> 是 <c>RunSource</c> 的枚举名（宿主不直接引用它，字符串最省事）。
/// </summary>
internal sealed record RunActionRequest(string Reference, int X, int Y, string Source);

/// <summary>在提权进程里打开一个窗口。<see cref="Target"/> 取值见 <see cref="LinkTargets"/>。</summary>
internal sealed record OpenWindowRequest(string Target, string? Arg);

/// <summary>「打开窗口」的目标（宿主里这些窗口都还在提权进程）。</summary>
internal static class LinkTargets
{
    /// <summary>设置窗口（停在上次记住的页）。</summary>
    public const string Settings = "settings";

    /// <summary>超级面板 · 暂存区页（草稿读写、采纳都在提权进程）。</summary>
    public const string PanelStash = "panel.stash";

    /// <summary>设置 → 面板管理 → 场景（<see cref="OpenWindowRequest.Arg"/> = 场景 Id）。</summary>
    public const string PanelScene = "panel.scene";
}

/// <summary>面板的动作级操作（<>PanelActionOps</> 里那些会建 / 删动作或开编辑器的入口）。</summary>
internal static class PanelOps
{
    public const string AddNew = "addNew";
    public const string AddExisting = "addExisting";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Paste = "paste";
    public const string Export = "export";
    public const string ToSubAction = "toSubAction";

    /// <summary>从文件 / 文件夹创建动作（宿主把路径列表转过去，主程序建动作再落盘）。</summary>
    public const string DropPaths = "dropPaths";
}

/// <summary>
/// 一次面板动作操作。字段按操作取用：新建 / 添加已有要 <see cref="GroupId"/>；
/// 编辑 / 删除 / 导出 / 另存为子动作要 <see cref="Id"/>；粘贴要 <see cref="Kind"/> + <see cref="Reference"/>；
/// 从文件建动作要 <see cref="GroupId"/> + <see cref="Paths"/>。
/// </summary>
internal sealed record ActionOpRequest(
    string Op,
    string? Id = null,
    string? GroupId = null,
    string? Kind = null,
    string? Reference = null,
    string? Name = null,
    string[]? Paths = null);
