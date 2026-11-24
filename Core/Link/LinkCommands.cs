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

    /// <summary>显示面板并停在「暂存区」页（「在暂存区编辑…」）。</summary>
    public const string EventPanelStash = "panel.stash";

    /// <summary>面板状态变了（宿主 → 提权进程）：负载 <see cref="PanelStateEvent"/>。</summary>
    public const string EventPanelState = "panel.state";

    /// <summary>关掉面板（点悬浮按钮时用：那一下算「点到面板外面」）。</summary>
    public const string EventPanelClose = "panel.close";

    /// <summary>开关「辅助工具」窗口（窗口归宿主，托盘入口在提权进程）。</summary>
    public const string EventInfoHelper = "info.toggle";

    /// <summary>
    /// 全屏点选器开 / 关（宿主 → 提权进程）：鼠标钩子靠它让开「裸右键长按 = 面板」手势。
    /// 钩子在提权进程里，点选器在宿主里，所以这条状态要过链路。负载 <see cref="PickerActiveEvent"/>。
    /// </summary>
    public const string EventPickerActive = "input.picker";

    /// <summary>
    /// 遮罩 / 覆盖层关窗时「还按着的键，抬起要吞掉」（宿主 → 提权进程）。负载 <see cref="SwallowInputEvent"/>。
    /// </summary>
    public const string EventInputSwallow = "input.swallow";

    /// <summary>截图覆盖层（宿主里跑）：框选 / 标注 / 工具条。负载 <see cref="CaptureRunRequest"/>。</summary>
    public const string CaptureRun = "capture.run";

    /// <summary>直接截图（不弹覆盖层）：抓一块 → 复制 → 记采集历史。负载 <see cref="CaptureDirectRequest"/>。</summary>
    public const string CaptureDirect = "capture.direct";

    /// <summary>录屏跑完了（宿主 → 提权进程）：提权进程据此恢复被临时收起的自动化指示器。</summary>
    public const string EventCaptureRecordDone = "capture.recordDone";

    /// <summary>
    /// 打开一份截图相关窗口（采集历史 / 贴图管理 / 图片编辑 / 切图 / 拼图 / 贴图命令）。
    /// 负载 <see cref="CaptureWindowEvent"/>，目标取值见 <c>CaptureWindows</c>。
    /// </summary>
    public const string EventCaptureWindow = "capture.window";

    /// <summary>提权进程连上并握手完成（宿主收到后拉快照 / 重建界面）。</summary>
    public const string EventClientReady = "client.ready";

    /// <summary>动作库变了（新建 / 删除 / 改名 / 采纳草稿），宿主该重拉快照。</summary>
    public const string EventActionsChanged = "actions.changed";
}

/// <summary>宿主要在网格里画的一个磁贴（只带画磁贴需要的字段）。</summary>
internal sealed record ActionSummary(string Id, string Name, string Icon, int Steps);

/// <summary>面板快照：整份配置 + 动作摘要表 + 正在悬浮的动作引用（宿主据此重建界面与菜单标签）。</summary>
internal sealed record PanelSnapshot(PanelConfig Panel, List<ActionSummary> Actions, List<string> Floating);

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

/// <summary>面板状态（宿主 → 提权进程）：钩子据此决定要不要起「长按右键唤起面板」手势。</summary>
internal sealed record PanelStateEvent(bool Open);

/// <summary>全屏点选器状态（宿主 → 提权进程）：true = 遮罩开着，鼠标钩子让开「长按右键 = 面板」。</summary>
internal sealed record PickerActiveEvent(bool Active);

/// <summary>
/// 关窗那一刻的输入状态（宿主 → 提权进程）：哪几个键物理上还按着，抬起时吞掉。
/// <see cref="Key"/> = 虚拟键号（Esc / Enter），0 = 没有。
/// </summary>
internal sealed record SwallowInputEvent(bool Left, bool Right, bool Middle, int Key);

/// <summary>
/// 交互截图：<see cref="Pending"/> 是 <c>CaptureOverlayWindow.PendingAction</c> 的枚举名
/// （<c>None</c> = 普通框选，其余 = 「选完就做」的快捷入口）；<see cref="WantImage"/> = 调用方还要成品图
/// （宿主把它存成 png，返回 <see cref="CaptureRunReply.File"/>）。
/// </summary>
internal sealed record CaptureRunRequest(string Pending, bool WantImage);

/// <summary>
/// 交互截图的结果。<see cref="Exit"/> = <c>captured</c> / <c>cancelled</c> / <c>recording</c>
/// （录屏不是取消：那一次没有成品图）；<see cref="File"/> 只在要图时有，是 <c>cache/screenshots</c> 里的 png。
/// </summary>
internal sealed record CaptureRunReply(string Exit, int X, int Y, int W, int H, string? File);

/// <summary>直接截图（当前显示器 / 前台窗口）：物理像素矩形 + 场景键 + 来源文案。</summary>
internal sealed record CaptureDirectRequest(int X, int Y, int W, int H, string Source, string Scenario);

/// <summary>直接截图的结果；失败原因原样带回来（调用方是功能入口，由 <c>FeatureRunner</c> 弹提示）。</summary>
internal sealed record CaptureDirectReply(bool Ok, string? Error);

/// <summary>要打开的截图相关窗口 / 要执行的那条贴图命令（取值见 <c>CaptureWindows</c>）。</summary>
internal sealed record CaptureWindowEvent(string Target);

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

    /// <summary>设置 → 面板管理 → 场景（<see cref="OpenWindowRequest.Arg"/> = 场景 Id）。</summary>
    public const string PanelScene = "panel.scene";

    /// <summary>设置 → AI 接入（暂存区页的「AI 接入设置…」）。</summary>
    public const string Ai = "ai";
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

    /// <summary>悬浮 / 取消悬浮一个动作（悬浮窗在提权进程）。</summary>
    public const string Float = "float";

    /// <summary>整组悬浮到桌面。</summary>
    public const string FloatGroup = "floatGroup";

    /// <summary>试放一个宏。</summary>
    public const string ReplayMacro = "replayMacro";

    /// <summary>开始录制宏（带 <see cref="ActionOpRequest.Id"/> = 重新录制到那个宏）。</summary>
    public const string RecordMacro = "recordMacro";

    /// <summary>把动作复制进暂存区（草稿写 staging/）并打开暂存区。</summary>
    public const string EditInStash = "editInStash";

    /// <summary>暂存区：在编辑器里打开草稿（<see cref="ActionOpRequest.Id"/> = 草稿 Id）。</summary>
    public const string StashOpen = "stashOpen";

    /// <summary>暂存区：试运行草稿。</summary>
    public const string StashRun = "stashRun";

    /// <summary>暂存区：采纳草稿（<see cref="ActionOpRequest.Kind"/> = <c>new</c> / <c>replace</c>）。</summary>
    public const string StashPromote = "stashPromote";

    /// <summary>暂存区：删除一份草稿。</summary>
    public const string StashDelete = "stashDelete";

    /// <summary>暂存区：清空全部草稿。</summary>
    public const string StashClear = "stashClear";
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
