# 自动触发（阶段方案）

> 状态：**一期已实现**（定时 / 启动后 + 地基，见 §3）；**二期已实现**（2026-11-11）——三个事件源（窗口 / 剪贴板 / 文件）、事件输出变量 / `ActionParam` / `SkipFurther`、完整 Cron、触发记录全部落地，构建 0 错 0 警 + 无头冒烟 66 PASS，见 §13。本文只写规格，不含实现代码。
> 决策（2026-11-11）：分两期 · 首批事件源「窗口/进程切换、剪贴板变化、文件系统变化」· 叫「自动触发」· 安全用「总开关 + 紧急禁用」。
> UI（2026-11-11）：单页表格（页头总开关 + `DataGrid` + 独立编辑对话框）；编辑对话框改为 **760 宽左右分栏**，见 §9。交互便捷性（2026-11-11）：能 GUI 点选的字段一律给选择入口——窗口 / 进程走「点击窗口选取」（边缘渐变高亮）、目录用文件夹选择框、数字 / 时刻 / Cron / 筛选 / 事件 / 格式用可编辑下拉或勾选、动作参数给「插入变量 ▾」，手填永远兜底；同类选择方式在动作编辑器 / 快捷键里统一走同一套共享入口，见 §9。
> 参照研究：本机 Quicker 2.3.33.0（`Quicker.dll` SHA-256 `646c6fb4…`）的 REA 静态分析 + Quicker 官方文档。

## 1. 范围与边界

- **自动触发**的定义见 `CONTEXT.md`：不需要按键或点面板，由时间或系统里发生的事满足设定要求后让动作**运行**。
- 只做自动触发。手动触发（组合键 / 面板 / 悬浮项 / 内置功能入口）已有，不在本模块内；外部启动（命令行、URI、Windows 计划任务）不在本期。
- 一次动作仍叫**运行**，别用「触发」代指（「运行」词条已禁用）。
- 代码标识符统一用 `Trigger*` / `triggers`（`TriggerRule`、`TriggerRegistry`…），与术语一致。
- **词条已定稿**（见 `CONTEXT.md`）：触发规则、事件源、触发方式、条件摘要（一期）+ 事件类型、事件参数、事件输出变量、命中、触发记录（二期，§13.10 已补）。别在需求 / 文档里给它们起第二种叫法。

## 2. 参照：Quicker 的自动触发（研究结论）

Quicker 把触发分成 `group.autoTriggers` 与 `group.manualTriggers` 两个设置分组；自动触发只含两个设置页：

| 设置页 | 内容 |
| --- | --- |
| 自动运行动作 | Cron 定时运行（秒 分 时 日 月 周几，支持 `*/5`，用 `Cronos` 解析）；启动后运行（延迟秒数、限定主机、仅每日第一次启动、仅开机自启时、动作参数、启用）；`c:\qk_disable_auto.txt` 空文件临时全局禁用 |
| 事件触发 | 约 25 种事件：窗口获得 / 失去焦点、进程启动 / 退出、网络连接 / 断开、蓝牙 4 种、磁盘插入、音频设备激活 / 断开、按键锁定状态切换、浏览器网址变更、文件系统事件、剪贴板内容变化、显示设置 / 电源 / 注销 / 会话 / 锁定 / 解锁 / 用户偏好、**定时重复 / 设备闲置 / 连续使用**；规则 = 事件类型 + 说明 + 条件参数（AND，单值 / 分号多值 / `regex:` / 事件过滤表达式）+ 执行设置（动作、动作参数插值、延迟、限定主机、启用、触发后不再判断后续规则） |

数据模型（REA 读 `Quicker.Common.dll` 元数据）：

```
AutoTriggerSettings
├─ AutoRunTaskList : AutoRunTask[]
│    TaskType(NA/Start/Timer), Data(Cron), ActionIdOrName, ActionParam, Note,
│    IsEnabled, ValidForMachines, DelaySeconds, OnlyFirstStartInSameDay, OnlyAutoRunQuicker
└─ TriggerTasks : CommonTriggerTask[]
     Id, Note, IsEnabled, EventType, Params, DebounceMs, ThrottleMs, DelayMs,
     EventFilterExpression, ActionIdOrName, ActionParam, SkipFurtherTasks,
     ValidForMachines, LastEditTimeUtc, TryGetParamValue()
```

实现机制（`Quicker.PInvoke.dll` 710 个 P/Invoke 声明）：`SetWinEventHook`（窗口焦点）、`CreateToolhelp32Snapshot` / WMI（进程）、`AddClipboardFormatListener` / `SetClipboardViewer`（剪贴板）、`RegisterPowerSettingNotification`（电源）、`GetLastInputInfo`（空闲）、`BluetoothApis.dll`（蓝牙）、`Cronos.CronExpression`（Cron）、`ScheduledTask`（调度器）。

**证据边界**：主 DLL 元数据 50 MB 超过 REA 64 MB 表示预算，成员 / 方法体读不出，REA 只认证了程序集身份与 49 个触发相关类型存在；事件名称清单来自官方文档（页面自注「部分内容与最新版有出入」）；用户规则库 `quicker.db` 是加密 SQLite（带 `.qkey`），读不到实际规则。官方文档：<https://www.getquicker.net/KC/Manual/Doc/event-triggers>、<https://www.getquicker.net/KC/Manual/Doc/settings-autorunactions>。

## 3. 分期

| 期 | 目标 | 交付 |
| --- | --- | --- |
| 一期 | 定时 / 启动后（地基） | 新配置域、完整规则模型、运行期骨架、设置页、引用回收、术语、安全总开关 |
| 二期 | 3 个事件源 + §11 推来的 4 项 | 窗口 / 进程切换 → 剪贴板变化 → 文件系统变化；事件输出参数 / `SkipFurther` / 完整 Cron / 触发记录**全部纳入**（执行清单见 §13） |

一期**不改数据结构**：`TriggerRule` 一次把二期要用的字段（`EventType` / `Params` / `SkipFurther`…）与事件源接口留好；一期 UI 只暴露定时 / 启动后相关的字段。

**一期不做**（已全部纳入二期，见 §13）：三个事件源、完整 Cron、事件输出参数 / `ActionParam`、`SkipFurther`、触发记录。

**二期仍不做**：空闲 / 连续使用、浏览器网址、网络 / 蓝牙 / 音频 / 电源、按键锁定、显示设置 / 注销 / 会话等其它 Quicker 事件（§2 只是参照清单）。

## 4. 规则数据模型

`configs/triggers.json`（形状）：

```
TriggersFile
├─ Enabled        bool           全局总开关，默认 false
├─ Rules          TriggerRule[]  规则列表（顺序 = 匹配优先级）
└─ SchemaVersion  int            只作标注，读时不按版本分支

TriggerRule（一期定义完整，UI 分期暴露）
├─ Id              string         规则 Id
├─ Note            string         说明
├─ Enabled         bool           单条启用
├─ Kind            enum           一期：Timer / Startup；二期：Event
├─ IntervalMs      int?           Timer：固定间隔
├─ DailyAt         string?        Timer：每天 HH:mm
├─ DelaySeconds    int            Startup：启动后延迟
├─ OnlyFirstStartToday bool       Startup：仅每日第一次启动时运行
├─ OnlyOnAutoStart     bool       Startup：仅由开机自启任务拉起时运行
├─ Cron            string?        二期：完整 Cron 表达式（见 §13.8）
├─ EventType       string?        二期：事件类型
├─ Params          dict?          二期：事件源各自的参数
├─ Scope           AppFilter?     作用范围（复用 Config/Hotkeys.cs 的 AppFilter；空 = 不过滤）
├─ ActionRef       string         动作 Id 或内置功能引用（FeatureRef `id#key=value`）
├─ ActionParam     string?        二期：动作参数模板（见 §13.3）
├─ DelayMs         int            命中后延迟执行
├─ DebounceMs      int            防抖
├─ ThrottleMs      int            节流
├─ SkipFurther     bool           二期：命中后不再判断后续规则（见 §13.3）
└─ LastEditTimeUtc long?          可选
```

- 触发目标与快捷键 / 面板 / 悬浮一致：只存 Id（或内置功能引用），动作本体在 `actions/`。
- **作用范围**复用已有 `AppFilter`，不另写一套匹配（面板场景那套 `PanelMatchDto` 语义不同，别混）。
- 一期不引第三方 Cron 库；「固定间隔 + 每天时刻」由后台计时器算下次触发时间。
- 两个启动选项由 `Core/AutoStartService.cs` 的任务 XML 加 `--autostart` 参数（旧任务由 `HealIfStale` 补写）与 `state/auto-trigger-last-start.txt` 的当天日期判定；`TriggerScheduler.Rebuild` 里条件不满足整条不排程，两条件同时勾选是 AND。

## 5. 配置域 `triggers`

新增第七个域，必改位置（照 `docs/ARCHITECTURE.md` P1/P3/P5 同步文案）：

| 文件 | 改动 |
| --- | --- |
| `Config/ConfigDomain.cs` | 加 `triggers = 1 << 7`，并入 `all` |
| `Config/Triggers.cs`（新建） | `TriggerRule` / `TriggersFile`（带 `SchemaVersion`）+ `Clone` / `Normalize` / `NewId` |
| `Config/AppConfig.cs` | 加 `Triggers` 属性；同步「6 个域」注释 |
| `Config/ConfigBundle.cs` | `TriggersPath` → **`DomainPaths` 必须同步加**（否则 `IsFullySplit` 永远 false）→ `LoadCore` 读 + 种子回填 → `SaveDomains` 分支 → `WriteTriggers` → `ToTriggers` |
| `Config/DomainStores.cs` | 加 `TriggerStore.Modify` 两个重载 |
| `LTools.example.json` | 顶层加 `"Triggers"`（聚合形状种子） |
| `CONTEXT.md` | 实现时把「配置域」词条的「六个文件」改成七个；`自动触发` 词条已补 |
| `AGENTS.md` / `docs/ARCHITECTURE.md` | 同步域表、store 清单、备份白名单文案 |

`Core/ConfigIO.cs`、`Config/ConfigLocator.cs` 无需改；`configs/` 无条件进备份，`Core/BackupService.cs` 不用改（`BackupDomain` 只管目录）。**不做向后兼容**：不写迁移器、不认旧字段、不留别名；首次写入时 `IsFullySplit` 检测不齐会自动扩成 `all` 补齐七个域。

## 6. 运行期骨架

```
TriggerRegistry     静态只读表，照 HotkeyRegistry：Load(TriggersFile) / Rebuild()
TriggerScheduler    后台 System.Threading.Timer（照 Features/Flow/PaddleOcr.cs 的 _idleTimer），
                    算每条规则的下次触发时间；**不用 DispatcherTimer 干重活**
ITriggerSource      一期留接口，二期三个实现（Start/Stop + 统一投递）
Fire(rule)          → ActionExecutor.RunRef(rule.ActionRef, 0, 0)
```

- **唯一执行入口** `Features/ActionExecutor.cs` 的 `RunRef/Run`（后台 spawn，不阻塞调用方）；**不要**调 `Execute`（那是已位于后台线程的同步版），不要 `Task.Wait`。
- 生命周期：`App.OnStartup` 在 `ConfigStore.Initialize` 之后、`ForegroundAppCache.Start()` 附近 `Start()`；`ExitApplication` / `OnExit` 里 `Stop()`（照 `ForegroundAppCache.Stop`；别在 UI 线程等后台关停，见 `McpServer.Stop()` 的坑）。
- 线程纪律：碰动作库 / 配置 / WPF → `Core/Ai/AiUi.cs` 的 `AiUi.Run` 或 `Dispatcher.BeginInvoke`；STA COM → `Native/StaRunner.cs`；**不要在 UI 线程上等后台结果**。`ToastService.Show` 任意线程可调。
- **并发**：`Features/AutomationRunner.cs` 的 `_running` 单实例保护在已有运行时会静默 `TrayService.Notify("已有自动化正在运行")` 并返回——自动触发必须自己做排队 / 去重 / 单飞，别指望引擎。建议一条规则同时只跑一个实例，命中时若已在跑就丢弃并 `AppLog.Warn`。
- 通知 / 日志：只用 `Core/ToastService.cs`（带 `key`，失败常驻）、`Core/AppLog.cs`；失败交给 `AutomationRunner.NotifyFinished`，别自弹 `MessageBox`。
- 内置功能引用走 `Features/Builtin/FeatureRunner.cs`（不受 `_running` 限制），要注意它可能与动作运行叠加。

## 7. 安全策略（用户选定：总开关 + 紧急禁用）

| 机制 | 落点 |
| --- | --- |
| 全局总开关 | `TriggersFile.Enabled`，**默认 `false`**（用户主动开） |
| 单条启用 | `TriggerRule.Enabled` |
| 防抖 / 节流 / 延迟 | `DebounceMs` / `ThrottleMs` / `DelayMs`（对齐 Quicker `CommonTriggerTask`） |
| 紧急禁用 | 数据根 `disable-auto-trigger.txt` 空文件（对齐 `c:\qk_disable_auto.txt`），启动时与每次 `Fire` 前检查；托盘菜单「暂停自动触发」同源切换（§11-4 已定） |
| 失败提示 | `AutomationRunner.NotifyFinished` 的常驻错误卡 + 「查看日志」 |
| 运行通知 | 沿用 `AppConfig.RunNotice`（默认 `onFailure` 静默），不另加提示 |
| 抢焦点 | 自动触发的动作避免弹界面 / 抢前台（Quicker 文档明确警告）；一期不硬限制，编辑对话框给灰字提示（§11-6 已定） |

## 8. 二期事件源实现要点

> 本节是技术要点速览；二期的完整执行清单（契约 / 参数表 / UI / 验收 / 排期）见 §13，**冲突时以 §13 为准**。

| 事件源 | 复用 | 需新增 | 坑 |
| --- | --- | --- | --- |
| 窗口 / 进程切换 | `Core/Hotkeys/ForegroundAppCache.cs`（唯一窗口切换来源）、`Native/ForegroundApp.cs`、`Core/Hotkeys/AppFilterMatch.cs` | 给它暴露静态订阅事件（在后台解析完成后触发），或另挂一条 `SetWinEventHook`（照 `Features/WindowPinFrames.cs`）；把 `AppFilterMatch.Matches` 提为 internal 或包一层 | 回调 private 不可订阅；`Current == null` 表示解析没跟上，要定义放行语义；自己的窗口由事件源的 `excludeOwn` 排除（快捷键侧已不再一律排除）；WinEvent 回调不查 `Process.MainModule`；委托字段防 GC、退出 `UnhookWinEvent` |
| 剪贴板变化 | `Features/MacroHotkey.cs` 的 `HwndSource` + `HWND_MESSAGE` 消息窗、`Native/NativeClipboard.cs` | `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE` P/Invoke 到 `Native/Win32.cs` | 必须忽略 LTools 自己写入引起的变更（在 `NativeClipboard` 写路径打标 / 时间窗）；剪贴板全局独占，`OpenClipboard` 已有 5×15ms 重试；别在消息回调里同步读剪贴板内容做重活 |
| 文件系统变化 | `Views/Controls/StashPane.xaml.cs`（轮询 + 签名） | `FileSystemWatcher`（或轮询）；`Params` 里放路径 / 筛选器 / 包含子目录 | 路径不存在 / 无权限时规则直接停用并提示，不要静默失败；事件洪泛靠 `DebounceMs` 收；网络盘 / 回收站移动要防抖 |

**事件输出参数怎么传给动作**：窗口切换带新旧窗口、剪贴板带文本、文件变化带路径。`ActionRunOptions` 目前没有「初始变量」入口；方案（扩展 `ActionRunOptions.InitialVariables` + 变量名表 + 透传链路）见 §13.3 / §13.4，已定。

## 9. 设置页 UI（布局已定：单页表格）

### 落位

- 设置左栏新增一级页「自动触发」：改 `Views/ConfigHelper.xaml(.cs)` 的 `FirstLevelNav` 白名单、`NormalizeSavedNav`、XAML 的 RadioButton、`ApplyNavPage`；`AppConfig.LastNavPage` 注释同步。导航行沿用 `NavRow`（38 高、圆角 6、选中 `NavSelectedBgBrush` + 3px 竖条）。
- 页面用新控件 `Views/Controls/TriggersView.xaml(.cs)`，编辑用新窗口 `Views/TriggerRuleEditDialog.xaml(.cs)`（仿 `Views/HotkeyEditDialog.xaml`）。

### 主页面（单页表格，不分组、不左列表）

- 页头：标题「自动触发」+ 右侧**总开关**（`TriggersFile.Enabled`，默认关，关时显示「已关闭」）+ 一行灰字说明（用术语定义，别写成教程）。
- 工具条：搜索框（按说明 / 动作 / 触发方式筛选）+ 右侧 `BtnPrimary`「＋ 新建规则…」。
- `DataGrid`（白卡 `CornerMd` 6 + 1px 边框）：列固定为 **启用 / 说明 / 触发方式 / 条件摘要 / 动作 / 操作**，列表顺序即展示顺序（事件规则的优先级由 `SkipFurther` 决定，P2.0 起，见 §13.3）。
  - 行高沿用全局 **30**；**每行只一行文字**（名称 + 条件摘要同行靠 `TextTrimming` + `ToolTip` 裁），别堆两行（动作管理表格踩过）。
  - **每列写 `MinWidth`**，`ScrollViewer.HorizontalScrollBarVisibility="Auto"`；否则「启用」列被压成空矩形，看不见也点不到。
  - 「启用」勾选框照 `HotkeysView` 的 `BindingEnabled_Click`：在 `PreviewMouseLeftButtonDown` 里先 `cell.Focus()`，按 `IsChecked` 直接写回 `TriggerRule.Enabled` 再 `Save()`，别只靠 TwoWay 绑定的更新时机。
  - 「触发方式」列显示「定时 / 启动后 / 事件」；事件行显示「事件 · <事件类型中文短名>」（P2.0 起，短名登记在 `OptionLabels`，见 §13.6；一期的「事件 · 二期」灰徽标已去掉），停用行整体浅灰。
  - 「条件摘要」由规则字段拼单行文本（间隔 30 分钟 / 延迟 10 秒 · 仅每日首次 / 剪贴板 · 文本含 regex:^https?://），照 `StepSummary` 的写法；事件规则按 §13.5 拼参数，并给 `SkipFurther` 加「命中即停」。
  - **「操作」列 = 5 个常驻小图标**（2026-11-11 改，替换原来的 `⋯` 下拉菜单）：编辑 `✎` / 复制 `⧉` / 删除 `✕` / 上移 `↑` / 下移 `↓`，列宽 146。用户嫌「小心翼翼点那三个点」才改。5 个按钮共用 `RowAction_Click` 一个分发器，动作标识走 **`CommandParameter`**（`edit/copy/delete/up/down`）——**别用 `Tag`**：`BtnGhost` 把 `Tag` 当悬停色，占用会弄坏悬停样式。图标字形走 `Segoe UI Symbol`、`Focusable=False`（不吃键盘焦点）；删除用 `BtnGhostDanger`。
  - **双击整行 = 编辑**（`RuleGrid.MouseDoubleClick`）。判定抽成纯函数 `DoubleClickEditRow(DependencyObject?)`，避开三处：双击「启用」勾选框 / 操作列图标按钮（都各自有单击语义）、表头 / 空白（取不到 `DataGridRow`）——抽纯函数便于无头冒烟直接断言，不用伪造鼠标事件或弹模态框。
- 底部：灰字提示「总开关关闭时全部不生效」+ `MiniBtn`（26 高）「紧急禁用…」「复制」「粘贴」。

### 编辑对话框（760 宽左右分栏、`SizeToContent=Height`、`TitleBar` + `BtnGhost` / `BtnPrimary`）

早期是 470 宽单列长条，8 个区块依次堆下来、事件规则展开后近 900 DIP，看着很累。改为**左右分栏**：窗口 `Width=760`，内容区 `Grid` 三列——左栏固定 `252`、中间 1px 竖线、右栏 `*`。外层「标题栏 / `*` 滚动内容 / `Auto` 常驻按钮栏」由共用模板 `controls:DialogShell` 提供（内容仍包在 `ScrollViewer` 里、超屏滚动，按钮栏留在滚动区外）；`WindowSizing`（`Mode="Dialog"`）按所在显示器工作区动态限高并把窗口夹回可视区，切事件内容变高也不会被任务栏挡住。分栏后内容高约为原来的一半（定时 ≈ 266、事件 ≈ 517 DIP）。

- **左栏（每次都要填）**：说明 `TextBox` + 启用勾选、触发方式 `ComboBox`（定时 / 启动后 / 事件）、动作只读框 + 「选择动作…」、无人值守灰字提示、动作参数（事件专属）。
- **右栏（随触发方式变化）**：栏标题 `RightTitle`（定时设置 / 启动后设置 / 事件设置，随触发方式切）+ 三选一参数区 + 作用范围 `Expander` + 高级参数 `Expander`。
- **按钮栏**：错误提示放在这一行左侧（不在滚动区里，出错始终看得见）＋ 右对齐「取消 / 确定」。

参数区按触发方式切换（同一个对话框、不另建窗口），仍沿用 `SectionLabel` 分组 + `MinHeight` 30 输入框：

1. **定时**：间隔分钟 / 每天 HH:mm / Cron 三选一（优先级 Cron > 间隔 > 每天时刻，非法 Cron 保存时拦下，见 §13.8）
2. **启动后**：延迟秒数 + 「仅每日第一次启动」「仅开机自启时」
3. **事件**：事件类型下拉 + 按 `EventParams` 表动态生成的参数行 + 「可用变量」灰字清单；`ActionParam` / `SkipFurther` 只在事件规则显示（定时 / 启动后由 `ApplyKindVisibility` 收起，见 §13.5/§13.3）

枚举**只翻译显示层**（`OptionLabels`），存英文键。动作选择复用 `Views/ActionPoolPicker.xaml(.cs)`（左列表 + 右预览），别另写选择器。作用范围折叠区填进程 / 窗口标题 / 类名（每行一个，复用 `AppFilter`），留空 = 不过滤；命中时由 `AppFilterMatch.AllowsScope` 比对前台程序快照。高级参数（延迟 / 防抖 / 节流）照 `InputSpec.Advanced` 的「设过才自动展开」。

**交互便捷性：能 GUI 点选的一律给选择入口，手填永远兜底**（2026-11-11）：

- **窗口 / 进程 / 目录**：作用范围三行与事件参数里的 `title` / `class` / `process` 给「选择窗口…」「选择进程…」「浏览…」按钮，默认走 `Views/WindowPickOverlay` 的**点击窗口选取**——像截图吸附那样在屏幕上移动鼠标，光标下第一个可点选窗口亮起四边朝内淡出的渐变光晕 + 沿对角渐变的描边 + 信息条，单击选中，Esc / 右键 / 顶部「✕ 取消」退出；原可搜索列表只留作「从列表选…」次要入口。命中要跳过 cloaked / 工具层 / 不抢焦点 / 点击穿透 / 输入法辅助窗（否则会被常驻整屏的「Shell Handwriting Canvas」这类壳窗口锁死），鼠标移动节流、取消走幂等 `Finish`；点选期间用 `TransientMinimize.MinimizeAll` 收起 LTools 全部窗口、并让全局右键长按手势让路（`MouseHook.SetWindowPickerActive`），否则 down/up 被吞会「连点两次才退出」。统一由 `Views/SystemPickerDialog.PickWindow` / `PickProcess` / `AppendLine` 提供，动作编辑器 `ValueField`、快捷键编辑「生效范围」、快捷键页「全局默认名单」都复用同一套，别各写一份。
- **枚举 / 数字 / 多选**：间隔分钟 / 每天时刻 / Cron / 启动后延迟 / 高级毫秒用**可编辑 ComboBox + 常用预设**；`file.change` 的 `events`、`clipboard.change` 的 `formats` 用多选勾选，`filter` 用常用筛选下拉；都可手填兜底。
- **动作参数**：左栏「插入变量 ▾」按当前事件类型列出 `trigger*` 输出变量，点一下插进模板，不用手抄变量名。
- **仍手填**：说明、含 `regex:` / 分号多值的匹配文本、自定义 Cron / 任意数字——本质自由输入，不给假选择。

- 保存：`TriggerRegistry.Load(file)` 立即生效（不必重启）+ `TriggerStore.Modify(path, …)` 只写 `configs/triggers.json`（照 `HotkeysView.Save()`）。
- 不做「保存并继续添加」（那是步骤编辑器的连续编辑，规则不需要）。

### 不做的

- 设置页不做左分类栏、不做「左列表 + 右详情」就地编辑；不弹模态之外的第二套编辑 UI。触发记录是表格下方的只读 Expander，不另建页（见 §13.9）。

## 10. 引用回收与备份

- `Core/Storage/ActionRemoval.cs` 的 `Remove(id)` 顺序固定「内存清单 → **快捷键绑定 + 面板槽位 + 触发规则** → 动作目录 → 宏 / 模板图 / 悬浮回收」；`RepairReferences` 里按磁盘动作清单幂等清掉悬空触发引用（`Features/Builtin/BindingRef.cs` 的 `IsLive` 判内置功能引用是否还活着）。
- `configs/` 无条件进备份，`BackupService` 不用改；删规则不留孤儿。

## 11. 一期取舍（2026-11-11 决策，全部按推荐项定稿）

一期实现范围以本节为准；字段留在模型里、UI 分期暴露。

1. **Cron**：一期不引入，只做「固定间隔 + 每天 HH:mm」；`Cron` 字段留空。真要完整 Cron 放二期（`Cronos` 或自实现 6 段，另开一轮）。
2. **动作参数 / 事件输出参数**：一期不做。`ActionParam` 与事件输出字段留着，二期和三个事件源一起定「初始变量」入口（扩展 `ActionRunOptions`）。
3. **规则优先级与 `SkipFurther`**：一期不启用。定时 / 启动后各自独立到期；字段留着，二期按事件类型定适用范围（Quicker 说目录监控 / 空闲 / 定时重复不适合）。
4. **紧急禁用的形式**：保持现状 = 数据根空文件 `disable-auto-trigger.txt` + 托盘菜单同源切换；不加勾选框样式、不加切换热键。
5. **Quicker 启动选项**：「仅每日第一次启动」「仅开机自启时」已实现（`OnlyFirstStartToday` / `OnlyOnAutoStart`，两条件 AND）；「限定主机」不照搬。
6. **动作内容限制**：不硬限制，执行链不拦截；编辑对话框给一句灰字提示「无人值守时，动作里注入键鼠或弹出界面可能打断你正在做的事」。
7. **触发记录与调试入口**：一期不做。运行期已有 `AppLog`（启动状态 / 跳过原因）+ 失败常驻提示 + `last-failure.log`。
8. **术语**：触发规则 / 事件源 / 触发方式 / 条件摘要 四个叫法定稿，已补进 `CONTEXT.md`，`AGENTS.md` 与代码标识符同步。

## 12. 验收

- 构建 **0 错 0 警**（构建前 `Stop-Process -Name LTools`）；Debug 用 `-p:NoWin32Manifest=true`。
- 七个域文件齐全；改自动触发只动 `configs/triggers.json`，其它域 mtime 不变。
- 真机：最小间隔定时、每天时刻、启动后延迟、**仅每日第一次启动 / 仅开机自启时**、`--autostart` 旧任务补写、全局总开关关闭、紧急禁用生效、删动作后触发引用被清掉、连续命中不炸（并发单飞）。
- 二期：完整验收清单见 §13.11（三个事件源各一个最小用例、自触发、配置热更新、`SkipFurther`、事件输出变量、Cron、触发记录限流、退出清理等）。

## 13. 二期实施计划（2026-11-11 决策，已实现）

> 本节是二期的执行清单；一期取舍见 §11，事件源技术要点速览见 §8。**冲突时以本节为准**。
> 决策：先补计划再开工 · §11 推来的 4 项**全部纳入** · 事件源顺序「窗口 / 进程切换 → 剪贴板变化 → 文件系统变化」。

### 13.1 范围

**做**：
- 3 个事件源：① 窗口 / 进程切换 ② 剪贴板变化 ③ 文件系统变化
- §11 推来的 4 项：事件输出参数 / `ActionParam`、`SkipFurther`、完整 Cron、触发记录
- 为以上所需的运行期契约、UI、术语、验收

**不做**（留到以后，不进二期）：空闲 / 连续使用、网络 / 蓝牙 / 音频 / 电源、浏览器网址、按键锁定、显示设置 / 注销 / 会话、其它 Quicker 事件（§2 只是参照清单）。

### 13.2 阶段划分

| 阶段 | 内容 | 依赖 |
| --- | --- | --- |
| **P2.0 契约与地基**（已完成，2026-11-11） | 事件规则进 `Active`；`ITriggerSource` 扩展 `Reload`；统一事件上下文 + 批量投递 `FireEvent` + `SkipFurther`；`ActionRunOptions.InitialVariables` 注入链路；`EventType` / `Params` 的 UI 框架；作用范围与配置热更新语义；术语 / 文档同步 | 无，**必须最先做** |
| **P2.1 窗口 / 进程切换**（已完成，2026-11-11） | `window.foreground`（前台进程切换，见 §13.5 门控）+ 对应 Params 表单与输出变量 | P2.0 |
| **P2.2 剪贴板变化**（已完成，2026-11-11） | `clipboard.change` + 忽略自写入 + Params 表单与输出变量 | P2.0 |
| **P2.3 文件系统变化**（已完成，2026-11-11） | `file.change` + `FileSystemWatcher` + Params 表单与输出变量 | P2.0 |
| **P2.4 Cron + 触发记录**（已完成，2026-11-11） | 完整 Cron 解析 / 排程 / UI；触发记录落盘 + 只读页 | P2.0（与 2.1–2.3 解耦，可并行 / 最后收口） |

事件源严格按 2.1 → 2.2 → 2.3 顺序；P2.4 不与它们抢同一批文件。二期**不改数据结构**：`TriggerRule` 的 `EventType` / `Params` / `ActionParam` / `SkipFurther` / `Cron` 字段一期已留全。

### 13.3 运行期契约（P2.0）

1. **事件规则进投递表**：`TriggerRegistry.Rebuild()` 去掉 `r.Kind is timer or startup` 的硬过滤，改为「总开关开 + 单条启用 + 有 `ActionRef`」；`Kind` 只用于分流（`TriggerScheduler` 排 `timer` / `startup`，事件源投 `@event`）。
2. **`ITriggerSource` 扩展**：在 `Start(Action<TriggerRule> fire)` / `Stop()` 之外加 `void Reload()`；事件源按 `TriggerRegistry.Active` 里 `Kind == @event` 的规则重建订阅（路径 / 格式 / 事件类型变了要能重挂）。`TriggerService.Reload` 在 `TriggerRegistry.Load` + `_scheduler.Rebuild()` 之后遍历 `_sources` 调 `Reload()`；`Start` / `Stop` 生命周期不变。注册点：`_sources` 在 `Start` 里按顺序 new 出三个实现（窗口 → 剪贴板 → 文件），只建一次、`Stop` 后复用实例。
3. **统一事件上下文 + 批量投递**：新增 `TriggerEvent`（`EventType` / `OccurredAt` / `Values`（事件输出变量）/ `Foreground`（命中时的前台快照））。事件源不逐条 `Fire(rule)`，而是 `TriggerService.FireEvent(TriggerEvent ev)`：
   - 按 `Active` 列表顺序筛出 `Kind == @event` 且 `EventType` 匹配、`Params` 条件满足（AND，含 `regex:` / 分号多值）、`Scope` 允许的规则；
   - 每条复用 `Fire` 现有的总开关 / 紧急禁用 / 单条启用 / 作用范围 / 防抖 / 节流 / 延迟 / 并发单飞检查，**别写第二套**；
   - 成功投递后若该规则 `SkipFurther = true` → 停止遍历后续规则；
   - 定时 / 启动后仍走原 `Fire(rule)`，`SkipFurther` 对它们不适用（Quicker 说定时重复不适合）。
4. **作用范围语义**：`window.foreground` 用 `TriggerEvent.Foreground`（切换后的窗口）；剪贴板 / 文件事件用 `ForegroundAppCache.Current`（命中瞬间前台程序），并允许 `Params` 里按路径 / 文本另设条件。两者都在 §13.5 写明。
5. **事件输出变量 / `ActionParam` 注入**（§11-2 落地）：
   - `ActionRunOptions` 加 `IReadOnlyDictionary<string, Value>? InitialVariables { get; init; }`；
   - `ActionEngine.RunCore` 在 `SeedVariables(program.Variables, root)` 之后，对每个 `InitialVariables` 项：变量表里**已声明同名变量**才 `root.Set(key, CoerceInput(...))`；未声明的忽略并写一条诊断日志（老动作不因事件源加了变量而失败）；
   - 透传链路：`ActionExecutor.RunRef(reference, x, y, initialVariables)` → `Run(action, x, y, initialVariables)` → `AutomationRunner.Run(..., initialVariables)` → `ActionRunOptions.InitialVariables`。`TryRunProgram`（编辑器试运行）不传，行为不变；
   - **内置功能引用**（`FeatureRunner.TryRun`）不接变量——事件输出只对动作有效，编辑对话框里要提示；
   - `ActionParam` = 用户填的一段文本模板（支持 `${triggerWindowTitle}` 等插值），命中时插值后作为变量 `triggerParam` 一并注入，与事件输出变量走同一机制；
   - 动作作者需在变量表声明要接收的变量名（照「输出声明是唯一来源」）；编辑对话框显示「可用变量」灰字清单。
6. **配置热更新**：设置页 `Save()` → `TriggerService.Reload` → 事件源 `Reload()` 立即生效，不必重启；`ActionRemoval` 删动作后 `ReloadFromDisk` 同样触发。

### 13.4 事件输出变量表

变量名统一 `trigger*` 前缀、扁平（不嵌套），避免与动作自身变量撞名。

| 事件类型 | 注入的变量 | 说明 |
| --- | --- | --- |
| 通用（所有事件） | `triggerEventType` / `triggerEventTime` | 事件类型键 / `yyyy-MM-dd HH:mm:ss` 本地时间 |
| `window.foreground` | `triggerWindowTitle` / `triggerWindowProcess` / `triggerWindowClass` / `triggerWindowHandle` / `triggerPrevWindowTitle` / `triggerPrevWindowProcess` | 新窗口信息 + 上一个真实前台应用的标题 / 进程名（跳过桌面 / 任务栏 / LTools 自己） |
| `clipboard.change` | `triggerClipboardText` / `triggerClipboardFormats` | 文本（非文本格式为空串）/ 命中的格式清单（分号分隔） |
| `file.change` | `triggerFilePath` / `triggerFileName` / `triggerFileChange` / `triggerFileOldPath` | 全路径 / 文件名 / `created·changed·deleted·renamed` / 重命名前路径 |
| `ActionParam` | `triggerParam` | 模板插值后的文本 |

### 13.5 `Params` 字段表

`Params` 是 `Dictionary<string, string>`，键固定如下；界面按事件类型只显示对应的行。条件一律 AND；值支持分号多值与 `regex:` 前缀（照 §2 Quicker 的规则），空值 = 该项不限制。

| 事件类型 | 键 | 含义 / 校验 |
| --- | --- | --- |
| `window.foreground` | `title` / `process` / `class` | 新窗口的标题 / 进程名 / 类名匹配（缺省不限） |
| | `prevTitle` / `prevProcess` | 上一个**真实前台应用**的匹配（用于「从 A 切到 B」）；仅在**进程真的换了**时产生事件，同进程多窗口 / 桌面 / 任务栏 / LTools 自己的窗口切换不投递 |
| | `excludeOwn` | `true`（默认）= 排除 LTools 自己的窗口（`ForegroundApp.IsOwnWindow`）；自己的窗口仍产生事件但由本开关排除，且**不记为「上一个进程」** |
| `clipboard.change` | `text` | 文本内容匹配（包含 / `regex:`；非文本格式时此项不参与） |
| | `formats` | 只在这些格式变化时命中：`text` / `image` / `files`，分号分隔 |
| | `ignoreOwnWrites` | `true`（默认）= 忽略 LTools 自己写剪贴板引起的变更 |
| `file.change` | `path` | 监听目录（必填；不存在 / 无权限时规则停用并提示，**不静默失败**） |
| | `filter` | 文件名筛选器（如 `*.txt`，默认 `*.*`） |
| | `events` | `created;changed;deleted;renamed`（默认 `created;changed`） |
| | `includeSubdirectories` | `true` / `false`（默认 `false`） |
| | `pathMatch` | 全路径匹配（包含 / `regex:`） |

`Scope`（一期已有的进程 / 标题 / 类名名单）与 `Params` 不重复：前者管「命中时前台程序」，后者管「事件自身的条件」。

> **`window.foreground` 的「切换」按进程判定**（2026-11-11 修，用户报「没切过 explorer 也反复触发」）：事件源自己维护 `_last`（上一个**真实前台应用**），只在 `now.ExeName != _last.ExeName` 时投递一次，`prevProcess` / `triggerPrevWindow*` 一律取自 `_last`，不用 `ForegroundAppCache` 传来的旧快照。三类透明：① shell 窗口（`ForegroundApp.IsShellClassName`：桌面 / 任务栏 / 开始菜单 / 通知区，`explorer.exe` 既是 Shell 又是文件管理器，点任务栏 / Win+D / Alt+Tab 会让它短暂成为前台）不投递、也不推进 `_last`；② 解析失败 `now == null` 不投递；③ LTools 自己的窗口照常投递（受 `excludeOwn` 约束）但**不推进** `_last`，所以「Chrome → 面板 → Chrome」不会变成两次假切换。`ForegroundAppCache.ForegroundChanged` 按窗口句柄去重：`Current` 的补解析与系统对同一窗口重复发的 WinEvent 不再重复通知，启动时的初始解析静默。结果：`prevProcess: "explorer"` 只会在**真的从 explorer 进程切到别的应用**时命中；同进程多窗口切换按设计不投递，要这个粒度得另开事件类型。

### 13.6 UI 变更

- **编辑对话框**：`Ok_Click` 去掉「事件二期开放」的拦截；`EventPanel` 换成「事件类型下拉 + 对应参数表单」（按事件类型收起 / 展开，照 `VisibleWhen`，不另建窗口）；显示「可用变量」灰字清单；高级参数加 `SkipFurther` 开关；定时参数区加 Cron 输入（与间隔 / 每天时刻三选一，见 §13.8）。
- **表格**：去掉「事件 · 二期」灰徽标；「触发方式」显示「事件 · <事件类型中文名>」；「条件摘要」按 §13.5 拼单行文本（如 `剪贴板 · 文本含 regex:^https?://` / `文件 · D:\Inbox\*.txt · 新建`）。
- **触发记录**：设置页「自动触发」加一个只读 Expander / 子页（见 §13.9），不改单页表格主布局。
- 枚举只翻译显示层，配置存英文键；`OptionLabels` 补事件类型中文名。
- **就地微标记（2026-11-11，用户嫌纯文字墙丑后改）**：参数区**不放整块说明条**。文案仍由 `BuildClipboardSummary` 纯函数生成，但只在 `clipboard.change` 判定「文本内容被跳过」（剪贴板不含文本时 `EventMatcher` 跳过 `text` 条件，所以「填了文本 + 勾图片」不是 AND）时点亮两处轻量提示：① 「文本内容」输入框右侧一个 ⚠ 徽章（`_textBadge`，浅红底，完整语义 + 警告放在它的 ToolTip 里）；② 参数区底部一行极简状态（`SemanticMiniBar`：「共 N 个条件，1 个可能不生效 · 悬停徽章看详情」）。没有该语义就完全安静（两处都收起），输入框不禁用、不标灰、值照常保存。`ParamHint` 一行短提示随事件类型变化（剪贴板只讲填法、不承诺 AND；窗口 / 文件事件给一句准确语义），**不拼条件摘要**（`path` / `filter` / `excludeOwn` 是控制键、不参与条件匹配，写出来反而误导）。参数控件（文本框 / 勾选 / 可编辑下拉）统一由 `HookRefresh` 按逻辑树挂「值变即刷新」；切事件类型走 `RebuildParamRows` 一并刷新。包装徽章必须在把 `input` 挂进 `grid` **之前**做，否则同一元素有两个逻辑父（WPF 直接抛）。

### 13.7 三个事件源技术要点

| 事件源 | 复用 | 需新增 | 坑 |
| --- | --- | --- | --- |
| P2.1 窗口 / 进程切换 | `ForegroundAppCache` 的 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`、`Native/ForegroundApp.cs`、`AppFilterMatch` | 在 `ForegroundAppCache` 暴露**静态订阅事件**（后台解析完成后触发，带新旧 `ForegroundApp?`）；`AppFilterMatch.Matches` 提为 internal 或包一层给条件筛选 | WinEvent 回调不准查 `Process.MainModule`、不阻塞 >300ms；委托字段防 GC、退出 `UnhookWinEvent`；`Current == null`（解析没跟上）时按放行；自己的窗口由 `excludeOwn` 默认排除（快捷键侧已取消硬排除）；**「切换」按进程判定**（`now.ExeName != _last.ExeName`），shell 窗口透明、`ForegroundAppCache.ForegroundChanged` 按 hwnd 去重，否则 `explorer.exe`（既是 Shell 又是文件管理器）会在没换程序时反复命中 `prevProcess`；「上一个真实应用」由事件源自己记 `_last` 快照 |
| P2.2 剪贴板变化 | `Features/MacroHotkey.cs` 的 `HwndSource` + `HWND_MESSAGE` 消息窗（或另建一个专用消息窗）、`Native/NativeClipboard.cs` | `Native/Win32.cs` 加 `AddClipboardFormatListener` / `RemoveClipboardFormatListener` / `WM_CLIPBOARDUPDATE` P/Invoke；`NativeClipboard` 写路径打标（或时间窗）以忽略自写入 | 剪贴板全局独占，`OpenClipboard` 已有 5×15ms 重试；**别在消息回调里同步读剪贴板内容做重活**，投递到后台线程；退出要 `RemoveClipboardFormatListener` + 释放 HwndSource |
| P2.3 文件系统变化 | —（`StashPane` 的 `DispatcherTimer` 轮询签名**不适用**：那是 UI 线程，与「事件源走后台」冲突） | `FileSystemWatcher`（后台线程），`Params` 按 §13.5 映射；路径失效 / 无权限 → 规则停用 + 提示 | 事件洪泛靠 `DebounceMs` 收；网络盘 / 回收站移动 / 一次保存产生多次 `changed` 要合并；`IncludeSubdirectories` 开在大目录上要限流；退出 `Dispose` |

> 进程启动 / 退出（`process.start` / `process.exit`）需要 WMI 或快照轮询，与「窗口切换」不是同一套机制。**二期 P2.1 只做 `window.foreground`**，进程事件另开一轮（见 §13.12）。

### 13.8 完整 Cron（§11-1 落地）

- 引擎：推荐 `Cronos`（Quicker 同款，新增 NuGet 依赖），`CronExpression.Parse` + `GetNextOccurrence`；若不加依赖则自实现 6 段（秒 分 时 日 月 周几，含 `*/5`），另说。
- `TriggerScheduler.FirstDue` / `Advance` 加 Cron 分支；`TriggersFile.Normalize` 已有 `Cron` 空白归零。`timer` 规则三种时间源（间隔 / 每天时刻 / Cron）**互斥且有优先级**：Cron > 间隔 > 每天时刻，编辑对话框按此校验并提示。
- 解析失败在保存时拦下（别等运行期才发现）；时区用本地时间。
- `SkipFurther` 不适用于定时（§11-3）。

### 13.9 触发记录（§11-7 落地）

- 落盘：推荐 `<数据根>/state/trigger-history.jsonl`（`state/` 是备份域；**别放 `logs/`**，那里只认 `ltools-*` / `action-*` 且会被 `LogFiles.Roll` 滚动清理）。
- 每行一条：`时间 / 规则 Id / 说明 / 事件类型 / 结果（运行 / 丢弃 + 原因）/ 耗时`；**上限 500 条**、按时间倒序、超出丢最旧；写穿用 `.tmp` 替换（照 `SubActionStore.Save`）。
- 高频事件（剪贴板 / 文件系统）要限流：同一规则同一秒内重复命中只记一条汇总（或按 `ThrottleMs` 收），避免写放大。
- UI：设置页「自动触发」加一个只读 Expander / 子页，按时间倒序列最近 N 条，带「清空」按钮；不做筛选 / 统计。

### 13.10 术语与文档同步

> 以下为实施时列的同步清单；二期收口时均已完成（2026-11-11）。

- 新增词条（开工前确认叫法后补 `CONTEXT.md`，`AGENTS.md` 同步）：**事件类型**、**事件参数**、**事件输出变量**、**命中**、**触发记录**。「触发」仍不能代指一次「运行」（§1）。
- 同步：`AGENTS.md` 的「自动触发」小节、`docs/ARCHITECTURE.md`（新增 `state/trigger-history.jsonl` 要进备份说明）、`docs/AUTO_TRIGGER.md` 顶部状态行改「二期实施中」/「二期已实现」。
- 修一期遗留文案：§7 表格的「（待决项 4）」「由待决项 6 定」、§5 表格的 `ConfigFiles.cs` 说法、§12 二期验收一行（已同步）。

### 13.11 验收

- 构建 **0 错 0 警**（`Stop-Process -Name LTools` 后 `dotnet build -p:NoWin32Manifest=true`）。
- 三个事件源各一个最小用例：切换窗口、复制一段文本、在监听目录新建文件，动作各跑一次。
- **不自触发**：LTools 自己写剪贴板（发送文本 / 粘贴 / 复制文件）不触发规则；排除自己窗口。
- **配置热更新**：改 `EventType` / `Params` / 启用态后立即生效，不必重启；删动作后事件源同步失效。
- **`SkipFurther`**：一次事件命中多条规则时按列表顺序取第一条即停；定时 / 启动后规则不受影响。
- **事件输出变量**：动作里 `${triggerWindowTitle}` / `${triggerClipboardText}` / `${triggerFilePath}` 能读到值；`triggerParam` 模板插值正确；内置功能引用不报错（只是忽略变量）。
- **Cron**：合法表达式按预期到期、非法在保存时拦下、与间隔 / 每天时刻互斥。
- **触发记录**：写入条数不超上限、高频事件不写放大、重启后仍在、备份包含它。
- 一期的总开关 / 紧急禁用 / 作用范围 / 防抖节流延迟 / 并发单飞对事件规则同样生效。
- 退出干净：`UnhookWinEvent` / `RemoveClipboardFormatListener` / `FileSystemWatcher.Dispose` 不卡死 UI 线程（照 `McpServer.Stop()` 的坑）。
- 二期仍只写 `configs/triggers.json`（触发记录写 `state/`，不动其它域）。

### 13.12 开工前一句话确认即可的小项

1. Cron 引擎：**`Cronos`**（推荐）还是自实现 6 段？
2. P2.1 是否只做 `window.foreground`（推荐，进程启动 / 退出另开一轮）？
3. 触发记录落 **`state/trigger-history.jsonl`**（推荐）还是 `logs/`？

未特别指出即按推荐执行。
