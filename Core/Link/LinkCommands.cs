using System.Collections.Generic;
using LTools.Config;
using LTools.Features;

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

    /// <summary>新建一个动作（整份定义），返回补过 Id / 图标的完整 <c>ActionDto</c>。</summary>
    public const string ActionAdd = "action.add";

    /// <summary>覆盖同 Id 的动作（整份定义），返回保存后的 <c>ActionDto</c>。</summary>
    public const string ActionUpdate = "action.update";

    /// <summary>删除动作（含快捷键 / 面板 / 触发 / 悬浮引用与资源回收）。</summary>
    public const string ActionDelete = "action.delete";

    /// <summary>复制动作（新 Id + 名称「副本」+ 素材一并带上）。</summary>
    public const string ActionDuplicate = "action.duplicate";

    /// <summary>执行一个动作 / 内置功能引用（宿主的磁贴点击转给提权进程真正执行）。</summary>
    public const string RunAction = "run.action";

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
    /// 打开一份截图相关窗口（采集历史 / 贴图管理 / 图片编辑 / 切图 / 拼图 / 贴图命令 / 全屏画布）。
    /// 负载 <see cref="CaptureWindowEvent"/>，目标取值见 <c>CaptureWindows</c>；
    /// <see cref="CaptureWindowEvent.Arg"/> / <see cref="CaptureWindowEvent.Hwnd"/> 给「贴图快捷键」这类带参数的。
    /// </summary>
    public const string EventCaptureWindow = "capture.window";

    /// <summary>
    /// 全局快捷键「整体挂起 + 只放行一条」（宿主 → 提权进程）：画布开着时全挂起、只留它自己那条。
    /// 负载 <see cref="InputSuspendEvent"/>。
    /// </summary>
    public const string EventInputSuspend = "input.suspend";

    /// <summary>
    /// 快捷键录入框开始 / 结束等按键（宿主 → 提权进程）：录入窗口在宿主里，钩子在这里，
    /// 录入期间钩子要让路（否则按 CapsLock+… 会直接触发那个热键）。负载 <see cref="InputCaptureEvent"/>。
    /// </summary>
    public const string EventHotkeyCapture = "input.capture";

    /// <summary>
    /// 一张贴图接 / 不接管键盘了（宿主 → 提权进程）：钩子按它决定要不要把键投给这张贴图。
    /// 负载 <see cref="PinStateEvent"/>。
    /// </summary>
    public const string EventPinState = "input.pin";

    /// <summary>
    /// 录屏条当前的快捷键快照（宿主 → 提权进程）：指针在不在条上、录制处在什么状态。
    /// 负载 <see cref="RecordKeysEvent"/>。
    /// </summary>
    public const string EventRecordKeys = "record.keys";

    /// <summary>提权进程连上并握手完成（宿主收到后拉快照 / 重建界面）。</summary>
    public const string EventClientReady = "client.ready";

    /// <summary>动作库变了（新建 / 删除 / 改名 / 采纳草稿），宿主该重拉快照。</summary>
    public const string EventActionsChanged = "actions.changed";

    /// <summary>
    /// 执行器把磁盘配置整份重读了（导入备份 / 设置页「重新加载」）：宿主的只读副本已经过期，
    /// 要跟着重读一遍，否则面板还显示旧磁贴、点下去是「动作不存在」。
    /// </summary>
    /// <remarks>
    /// 没有负载：这几条路径都是**整份**替换（备份按 include 整体换目录），按域区分只会在
    /// 以后需要时再加，现在多一个字段只会让两边各写一套「哪个域该刷什么」。
    /// </remarks>
    public const string EventConfigReload = "config.reload";

    /// <summary>
    /// 宿主写过 <c>configs/</c>（它是写者，M2-a / M3-5c-3）：执行器要重读运行期状态
    /// （快捷键匹配表、自动触发规则、宏设置、截图 / 录屏的默认值这些缓存）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="EventConfigReload"/> 方向相反：那条是执行器整份换过磁盘、宿主跟着重读；
    /// 这条是宿主写完一个域、执行器跟着刷新。只带一句原因，不带域清单——执行器那边就是一次
    /// 「从磁盘重读各域」，按域区分只会让两边各写一套「哪个域该刷什么」。
    /// </remarks>
    public const string EventConfigChanged = "config.changed";
}

/// <summary>宿主要在网格里画的一个磁贴（只带画磁贴需要的字段）。</summary>
internal sealed record ActionSummary(string Id, string Name, string Icon, int Steps);

/// <summary>面板快照：整份配置 + 动作摘要表 + 正在悬浮的动作引用（宿主据此重建界面与菜单标签）。</summary>
internal sealed record PanelSnapshot(PanelConfig Panel, List<ActionSummary> Actions, List<string> Floating);

/// <summary>
/// 动作级写入：加 / 改带整份 <see cref="Dto"/>，删 / 复制带 <see cref="Id"/>。
/// </summary>
/// <remarks>
/// 四条命令共用一个形状：它们都是「宿主里的面板 / 编辑器要动一个动作」，
/// 区别只在执行器做哪一件事。拆成两套请求只会让调用点各记一次。
/// </remarks>
internal sealed record ActionEditRequest(ActionDto? Dto = null, string? Id = null);

/// <summary>
/// 动作级写入的答复：<see cref="Action"/> 是保存后的动作（补过 Id / 图标；删除时为 null），
/// <see cref="Error"/> 说明为什么没改成（宿主据此提示，别假装成功）。
/// </summary>
internal sealed record ActionEditReply(bool Ok, ActionDto? Action, string? Error);

/// <summary>
/// 显示面板。窗口句柄是「按下右键时鼠标所在的顶层窗口」（会话上下文按它算），
/// 用 <see cref="long"/> 而不是 <c>IntPtr</c>，跨 JSON 直接走整数。
/// 光标坐标是唤起瞬间的屏幕物理像素（面板据此定位；0/0 表示让宿主自己取当前光标）。
/// </summary>
internal sealed record PanelShowRequest(long Hwnd, int X, int Y);

/// <summary>
/// 面板状态（宿主 → 提权进程）：钩子据此决定要不要起「长按右键唤起面板」手势、要不要按
/// 「点了面板外面」关掉它，并据此放行 / 吞掉 Esc。
/// </summary>
/// <remarks>
/// <para>
/// 面板在宿主里，钩子看不到那扇窗，所以状态必须整份推过来——<see cref="Open"/> 单独一个字段时，
/// 钩子只能做到「别起手势」，钉住 / 点击外部 / 菜单 / 对话框全都判不了。
/// </para>
/// <para>
/// <see cref="Hwnd"/> 用 <see cref="long"/>（跨 JSON 直接是整数），钩子在跨进程窗口上用
/// <c>GetWindowRect</c> 算矩形，所以窗口拖动不必推送。
/// </para>
/// </remarks>
internal sealed record PanelStateEvent(
    bool Open,
    bool Pinned = false,
    bool Interacting = false,
    bool Active = false,
    long Hwnd = 0,
    List<PanelMenuRect>? Menus = null);

/// <summary>面板上开着的一块右键菜单（含已展开的子菜单），屏幕物理像素。</summary>
internal sealed record PanelMenuRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// 宿主写过配置（宿主 → 执行器）：<see cref="Reason"/> 只进日志（<c>settings.json</c> / <c>reload</c> …）。
/// </summary>
internal sealed record ConfigChangedEvent(string Reason);

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

/// <summary>
/// 要打开的截图相关窗口 / 要执行的那条贴图命令（<see cref="Target"/> 取值见 <c>CaptureWindows</c>）。
/// <see cref="Arg"/> / <see cref="Hwnd"/> 只给「贴图快捷键」用：动作名 + 哪张贴图。
/// </summary>
internal sealed record CaptureWindowEvent(string Target, string? Arg = null, long Hwnd = 0);

/// <summary>全局快捷键的挂起状态：<see cref="OnlyRef"/> = 唯一还放行的那条（null = 全挂起）。</summary>
internal sealed record InputSuspendEvent(bool Suspended, string? OnlyRef);

/// <summary>快捷键录入框在等按键（<see cref="Capturing"/>）：钩子整体让路，按键交给宿主的输入框。</summary>
internal sealed record InputCaptureEvent(bool Capturing);

/// <summary>一张贴图的状态变化（<see cref="State"/> 取值见 <c>PinStates</c>）。</summary>
internal sealed record PinStateEvent(long Hwnd, string State);

/// <summary>贴图接管状态里的三种变化。</summary>
internal static class PinStates
{
    /// <summary>点过这张贴图：键盘交给它。</summary>
    public const string Engage = "engage";

    /// <summary>指针离开 / 又点别处：不再接管。</summary>
    public const string Release = "release";

    /// <summary>这张贴图关了：把它从执行器的镜像里摘掉。</summary>
    public const string Closed = "closed";
}

/// <summary>
/// 录屏条发布的快捷键快照（宿主 → 提权进程）：<see cref="Hot"/> = 指针停在条上，
/// 其余是 <c>RecordShortcutState</c> 里决定「这个键能不能触发」的几个状态。
/// </summary>
/// <remarks>
/// 录屏条在宿主里、钩子在提权进程，所以匹配要用到的状态必须随快照送过来——
/// 别让钩子那边去读一份「自己猜的」默认状态（那样暂停 / 收尾期间会放行不该放行的键）。
/// </remarks>
internal sealed record RecordKeysEvent(
    bool Hot,
    bool Recording,
    bool Paused,
    bool Busy,
    bool HasLastRecord,
    bool ExportRowVisible,
    bool AreaHistory,
    bool CanUndo,
    bool CanRedo,
    bool DrawingAllowed);

/// <summary>
/// 执行一个动作 / 内置功能。引用形如 <c>01abc…</c>（动作 Id）或 <c>common.capture</c>（内置功能）。
/// <c>Source</c> 是 <c>RunSource</c> 的枚举名（宿主不直接引用它，字符串最省事）。
/// </summary>
internal sealed record RunActionRequest(string Reference, int X, int Y, string Source);

/// <summary>
/// 面板的结构与执行类操作（**只剩提权进程才做得了的**）。建 / 改 / 删 / 复制动作本身走
/// <c>action.add</c> 那一组（M3-5c-2 起面板与编辑器都在宿主里，不再绕 <c>panel.op</c>）。
/// </summary>
internal static class PanelOps
{
    /// <summary>悬浮 / 取消悬浮一个动作（悬浮窗在提权进程）。</summary>
    public const string Float = "float";

    /// <summary>整组悬浮到桌面。</summary>
    public const string FloatGroup = "floatGroup";

    /// <summary>关掉桌面上全部悬浮项（设置 → 面板管理那个「全部关闭」按钮）。</summary>
    public const string FloatCloseAll = "floatCloseAll";

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
/// 一次面板操作。字段按操作取用：悬浮整组 / 暂存区建草稿要 <see cref="GroupId"/>；
/// 悬浮 / 编辑草稿 / 试放宏 / 重新录制要 <see cref="Id"/>；暂存区采纳要 <see cref="Kind"/>；
/// 重新录制还要 <see cref="Name"/>（宏名）。
/// </summary>
internal sealed record ActionOpRequest(
    string Op,
    string? Id = null,
    string? GroupId = null,
    string? Kind = null,
    string? Name = null);
