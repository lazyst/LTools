using System.Collections.Generic;
using LTools.Core;
using LTools.Core.Running;
using LTools.Core.Share;
using LTools.Features;
using LTools.Features.CapturePro;

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

    /// <summary>动作运行时的档位与上限（独占 / 互操作 / 排队 / 并发），见 <c>docs/RUN_INTEROP.md</c>。</summary>
    public ActionRunFile ActionRun { get; set; } = new();

    /// <summary>「提示弹窗」的默认落点。</summary>
    public ToastPosition ToastPosition { get; set; } = ToastPosition.bottomRight;

    /// <summary>左键双击托盘图标的行为（存 <see cref="TrayDoubleClickAction"/> 的英文键）。</summary>
    public TrayDoubleClickAction TrayDoubleClick { get; set; } = TrayDoubleClickAction.settings;

    /// <summary>运行日志配置。</summary>
    public LogConfig Logging { get; set; } = new();

    /// <summary>录屏的默认开关：声音 / 摄像头 / 键鼠特效 / 编码器 / 录完裁剪。</summary>
    public RecordOptions Record { get; set; } = new();

    /// <summary>局域网共享：端口 / 是否在共享 / 接收方式 / 上传上限 / 可选令牌。</summary>
    public ShareOptions Share { get; set; } = new();

    /// <summary>识别结果的在线翻译（OpenAI 兼容端点）。空端点 / 空模型 = 没配。</summary>
    public TranslationFile Translation { get; set; } = new();

    /// <summary>文字识别的默认排版 / 标点（进文字编辑时先应用一次）。</summary>
    public TextRecognitionFile TextRecognition { get; set; } = new();

    /// <summary>采集历史的保留策略（开关 / 永久保留 / 天数 / 条数 / 磁盘上限）。</summary>
    public CaptureHistoryFile CaptureHistory { get; set; } = new();

    /// <summary>贴图历史的保留策略（开关 / 永久保留 / 天数 / 条数 / 磁盘上限）。</summary>
    public PinnedHistoryFile PinnedHistory { get; set; } = new();

    /// <summary>悬浮工具条（开关 / 模式 / 不透明度 / 拖放行为 / 隐藏规则 / 落点 / 布局）。</summary>
    public FloatingToolbarFile FloatingToolbar { get; set; } = new();

    /// <summary>贴图文字样式的默认值（最大宽 / 字体 / 字号 / 字色 / 底色）。</summary>
    public PinTextFile PinText { get; set; } = new();

    /// <summary>贴图的行为开关（现在只有「拖动抖动隐藏同组其他贴图」）。</summary>
    public PinToScreenFile PinToScreen { get; set; } = new();

    /// <summary>拼图默认设置（方向 / 尺寸方式 / 外边距 / 间距 / 背景色 / 宫格行列）。</summary>
    public CollageFile Collage { get; set; } = new();

    /// <summary>截图 Pro 的全局设置（「延时截图」的等待秒数 + 覆盖层指针动作）。</summary>
    public ScreenshotFile Screenshot { get; set; } = new();

    /// <summary>截图 Pro 的四张快捷键表（覆盖层 / 绘图 / 贴图 / 录屏），只存改过的项。</summary>
    public CaptureShortcutsFile CaptureShortcuts { get; set; } = new();

    /// <summary>绘制行为（自动选中 / 快速选择 / 记住上次工具 / 工具组首按钮）。</summary>
    public DrawingFile Drawing { get; set; } = new();

    /// <summary>截图输出：格式 / 质量 / 压缩 / 目录 / 文件名模板 / 另存为对话框 / 自动保存。</summary>
    public ExportFile Export { get; set; } = new();
}

/// <summary>
/// <c>settings.json</c> 的 <c>actionRun</c>：动作运行时的档位与上限（见 <c>docs/RUN_INTEROP.md</c> §2.3）。
/// </summary>
/// <remarks>
/// **一个枚举而不是三个布尔**：四档是互斥的产品档位，布尔组合会拼出没人要的状态。
/// 读盘 / 写盘都走这里，运行期缓存是 <see cref="AppState.ActionRun"/>（钩子只读那份）。
/// </remarks>
public sealed class ActionRunFile
{
    /// <summary>档位：独占（旧行为）/ 互操作 / 排队 / 并发。</summary>
    public ActionRunMode Mode { get; set; } = ActionRunConfig.Default.Mode;

    /// <summary>同时最多几个运行（1–32；只在「并发」档参与判定）。</summary>
    public int MaxConcurrent { get; set; } = RunLimits.DefaultMaxConcurrent;

    /// <summary>超额时最多排队几个（0–32；只在排队 / 并发档参与判定）。</summary>
    public int MaxQueued { get; set; } = RunLimits.DefaultMaxQueued;

    /// <summary>转成运行期配置（夹取越界值）。</summary>
    public ActionRunConfig ToConfig() => new ActionRunConfig(Mode, MaxConcurrent, MaxQueued).Normalized();

    /// <summary>从运行期配置写回（设置页保存时用）。</summary>
    public static ActionRunFile From(ActionRunConfig config)
    {
        ActionRunConfig c = config.Normalized();
        return new ActionRunFile { Mode = c.Mode, MaxConcurrent = c.MaxConcurrent, MaxQueued = c.MaxQueued };
    }
}

/// <summary><c>config/ui.json</c>：界面状态（丢了不影响功能，只是记忆丢失）。</summary>
public sealed class UiStateFile
{
    public int SchemaVersion { get; set; } = ConfigBundle.SchemaVersion;

    /// <summary>设置窗口上次所在的导航页。</summary>
    public string LastNavPage { get; set; } = "actions";

    /// <summary>二级下拉导航中展开的父项。</summary>
    public List<string>? NavExpandedGroups { get; set; }

    /// <summary>动作编辑器左栏已折叠的动作单元分类。</summary>
    public List<string> ToolboxCollapsedGroups { get; set; } = new();

    /// <summary>动作选择器的「最近使用」。</summary>
    public List<string>? RecentActionIds { get; set; }

    /// <summary>动作编辑器左栏收藏的动作单元键（<c>UnitSpec.Key</c>）。</summary>
    public List<string>? FavoriteUnits { get; set; }

    /// <summary>动作编辑器左栏「最近使用」的动作单元键，最近的在前。</summary>
    public List<string>? RecentUnits { get; set; }

    /// <summary>动作编辑器右栏「参数抽屉」的宽度（DIP）；0 / 缺省 = 默认 440。</summary>
    public double EditorDrawerWidth { get; set; }

    /// <summary>贴图管理页每页几条（0 / 缺省 = 默认 20）。</summary>
    public int PinPageSize { get; set; }

    /// <summary>采集历史页每页几条（0 / 缺省 = 默认 20）。</summary>
    public int CaptureHistoryPageSize { get; set; }

    /// <summary>速记窗口是否置顶（标题栏那个图钉；默认不置顶，改了记住）。</summary>
    public bool QuickNoteTopmost { get; set; }

    /// <summary>文本窗口是否置顶（标题栏图钉；默认不置顶，改了记住，下一扇文本窗口沿用）。</summary>
    public bool TextWindowTopmost { get; set; }

    /// <summary>
    /// 截图工具栏的格子布局：一行一格、格内逗号分隔工具键（如 <c>pen,highlighter</c>）；
    /// null = 全新安装（按 <c>ToolCatalog</c> 默认，每个工具一格）。
    /// </summary>
    public List<string>? CaptureToolGroups { get; set; }

    /// <summary>截图工具栏里被收起来的工具键（不占格子，可在「隐藏的工具」里放回去）。</summary>
    public List<string>? CaptureToolHidden { get; set; }

    /// <summary>
    /// 截图工具条「完成动作」的分组布局：一行一组、组内逗号分隔动作键
    /// （如 <c>recognize,qrcode</c>）；null = 全新安装（按默认分组）。
    /// 见 <c>Features/CapturePro/CaptureActionLayout</c>。
    /// </summary>
    public List<string>? CaptureActionGroups { get; set; }

    /// <summary>截图工具条里被收起来的完成动作键（不占位置，可在编辑器里放回去）。</summary>
    public List<string>? CaptureActionHidden { get; set; }

    /// <summary>上次用过的绘图工具（上游 <c>lastDrawingTool</c>；「记住上次工具」打开时开新会话用它）。</summary>
    public string? CaptureLastTool { get; set; }

    /// <summary>截图 Pro 的样式记忆：每个工具家族一套（见 <c>Features/CapturePro/ToolStyleSet</c>）。</summary>
    public CaptureStylesFile? CaptureStyles { get; set; }

    /// <summary>截图 Pro 的选区设置：圆角 / 阴影 / 边框 / 比例锁 / 快速集（见 <c>Features/CapturePro/SelectionStore</c>）。</summary>
    public CaptureSelectionFile? CaptureSelection { get; set; }
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
