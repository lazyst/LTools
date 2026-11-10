# 自动触发（阶段方案）

> 状态：路线已定，**待实现**。本文只写规格，不含实现代码。
> 决策（2026-11-11）：分两期 · 首批事件源「窗口/进程切换、剪贴板变化、文件系统变化」· 叫「自动触发」· 安全用「总开关 + 紧急禁用」。
> UI（2026-11-11）：单页表格（页头总开关 + `DataGrid` + 独立编辑对话框），见 §9。
> 参照研究：本机 Quicker 2.3.33.0（`Quicker.dll` SHA-256 `646c6fb4…`）的 REA 静态分析 + Quicker 官方文档。

## 1. 范围与边界

- **自动触发**的定义见 `CONTEXT.md`：不需要按键或点面板，由时间或系统里发生的事满足设定要求后让动作**运行**。
- 只做自动触发。手动触发（组合键 / 面板 / 悬浮项 / 内置功能入口）已有，不在本模块内；外部启动（命令行、URI、Windows 计划任务）不在本期。
- 一次动作仍叫**运行**，别用「触发」代指（「运行」词条已禁用）。
- 代码标识符统一用 `Trigger*` / `triggers`（`TriggerRule`、`TriggerRegistry`…），与术语一致。
- **待补词条**（暂用、确认后再进 `CONTEXT.md`）：触发规则、事件源、触发方式。别在需求 / 文档里给它们起第二种叫法。

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
| 二期 | 用户选定的 3 个事件源 | 窗口 / 进程切换 → 剪贴板变化 → 文件系统变化（按现有能力复用度排序，顺序可调） |

一期**不改数据结构**：`TriggerRule` 一次把二期要用的字段（`EventType` / `Params` / `SkipFurther`…）与事件源接口留好；一期 UI 只暴露定时 / 启动后相关的字段。

**本期不做**：系统事件、空闲 / 连续使用、浏览器网址、网络 / 蓝牙 / 音频、完整 Cron 语法（见待决项 1）、触发记录页（可选）。

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
├─ Cron            string?        待决项 1，先留空
├─ EventType       string?        二期：事件类型
├─ Params          dict?          二期：事件源各自的参数
├─ Scope           AppFilter?     作用范围（复用 Config/Hotkeys.cs 的 AppFilter；空 = 不过滤）
├─ ActionRef       string         动作 Id 或内置功能引用（FeatureRef `id#key=value`）
├─ ActionParam     string?        动作参数（待决项 2）
├─ DelayMs         int            命中后延迟执行
├─ DebounceMs      int            防抖
├─ ThrottleMs      int            节流
├─ SkipFurther     bool           命中后不再判断后续规则
└─ LastEditTimeUtc long?          可选
```

- 触发目标与快捷键 / 面板 / 悬浮一致：只存 Id（或内置功能引用），动作本体在 `actions/`。
- **作用范围**复用已有 `AppFilter`，不另写一套匹配（面板场景那套 `PanelMatchDto` 语义不同，别混）。
- 一期不引第三方 Cron 库；「固定间隔 + 每天时刻」由后台计时器算下次触发时间。

## 5. 配置域 `triggers`

新增第七个域，必改位置（照 `docs/ARCHITECTURE.md` P1/P3/P5 同步文案）：

| 文件 | 改动 |
| --- | --- |
| `Config/ConfigDomain.cs` | 加 `triggers = 1 << 7`，并入 `all` |
| `Config/ConfigFiles.cs` | 加 `TriggersFile`（带 `SchemaVersion`）+ `TriggerRule` / `TriggersFile` 形状；或拆 `Config/Triggers.cs` |
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
| 紧急禁用 | 数据根下一个空文件（对齐 `c:\qk_disable_auto.txt`），启动时与每次 `Fire` 前检查；托盘菜单「暂停自动触发」勾选项（待决项 4） |
| 失败提示 | `AutomationRunner.NotifyFinished` 的常驻错误卡 + 「查看日志」 |
| 运行通知 | 沿用 `AppConfig.RunNotice`（默认 `onFailure` 静默），不另加提示 |
| 抢焦点 | 自动触发的动作避免弹界面 / 抢前台（Quicker 文档明确警告）；规格里对动作内容不做限制，由待决项 6 定 |

## 8. 二期事件源实现要点

| 事件源 | 复用 | 需新增 | 坑 |
| --- | --- | --- | --- |
| 窗口 / 进程切换 | `Core/Hotkeys/ForegroundAppCache.cs`（唯一窗口切换来源）、`Native/ForegroundApp.cs`、`Core/Hotkeys/AppFilterMatch.cs` | 给它暴露静态订阅事件（在后台解析完成后触发），或另挂一条 `SetWinEventHook`（照 `Features/WindowPinFrames.cs`）；把 `AppFilterMatch.Matches` 提为 internal 或包一层 | 回调 private 不可订阅；`Current == null` 表示解析没跟上，要定义放行语义；`IsOwnWindow` 一律排除；WinEvent 回调不查 `Process.MainModule`；委托字段防 GC、退出 `UnhookWinEvent` |
| 剪贴板变化 | `Features/MacroHotkey.cs` 的 `HwndSource` + `HWND_MESSAGE` 消息窗、`Native/NativeClipboard.cs` | `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE` P/Invoke 到 `Native/Win32.cs` | 必须忽略 LTools 自己写入引起的变更（在 `NativeClipboard` 写路径打标 / 时间窗）；剪贴板全局独占，`OpenClipboard` 已有 5×15ms 重试；别在消息回调里同步读剪贴板内容做重活 |
| 文件系统变化 | `Views/Controls/StashPane.xaml.cs`（轮询 + 签名） | `FileSystemWatcher`（或轮询）；`Params` 里放路径 / 筛选器 / 包含子目录 | 路径不存在 / 无权限时规则直接停用并提示，不要静默失败；事件洪泛靠 `DebounceMs` 收；网络盘 / 回收站移动要防抖 |

**事件输出参数怎么传给动作**（待决项 2）：窗口切换带新旧窗口、剪贴板带文本、文件变化带路径。现有 `Core/Actions/ActionEngine.cs` 的 `ActionRunOptions` 没有「初始变量」入口，需定方案（扩展 `ActionRunOptions` / 新增取值器 / 写进变量表默认值），**规格阶段不擅自定**。

## 9. 设置页 UI（布局已定：单页表格）

### 落位

- 设置左栏新增一级页「自动触发」：改 `Views/ConfigHelper.xaml(.cs)` 的 `FirstLevelNav` 白名单、`NormalizeSavedNav`、XAML 的 RadioButton、`ApplyNavPage`；`AppConfig.LastNavPage` 注释同步。导航行沿用 `NavRow`（38 高、圆角 6、选中 `NavSelectedBgBrush` + 3px 竖条）。
- 页面用新控件 `Views/Controls/TriggersView.xaml(.cs)`，编辑用新窗口 `Views/TriggerRuleEditDialog.xaml(.cs)`（仿 `Views/HotkeyEditDialog.xaml`）。

### 主页面（单页表格，不分组、不左列表）

- 页头：标题「自动触发」+ 右侧**总开关**（`TriggersFile.Enabled`，默认关，关时显示「已关闭」）+ 一行灰字说明（用术语定义，别写成教程）。
- 工具条：搜索框（按说明 / 动作 / 触发方式筛选）+ 右侧 `BtnPrimary`「＋ 新建规则…」。
- `DataGrid`（白卡 `CornerMd` 6 + 1px 边框）：列固定为 **启用 / 说明 / 触发方式 / 条件摘要 / 动作 / 操作**，列表顺序即规则优先级。
  - 行高沿用全局 **30**；**每行只一行文字**（名称 + 条件摘要同行靠 `TextTrimming` + `ToolTip` 裁），别堆两行（动作管理表格踩过）。
  - **每列写 `MinWidth`**，`ScrollViewer.HorizontalScrollBarVisibility="Auto"`；否则「启用」列被压成空矩形，看不见也点不到。
  - 「启用」勾选框照 `HotkeysView` 的 `BindingEnabled_Click`：在 `PreviewMouseLeftButtonDown` 里先 `cell.Focus()`，按 `IsChecked` 直接写回 `TriggerRule.Enabled` 再 `Save()`，别只靠 TwoWay 绑定的更新时机。
  - 「触发方式」列显示「定时 / 启动后 / 事件」；二期事件行带灰徽标「事件 · 二期」，停用行整体浅灰。
  - 「条件摘要」由规则字段拼单行文本（间隔 30 分钟 / 延迟 10 秒 · 仅每日首次 / 剪贴板内容变化），照 `StepSummary` 的写法。
  - 「操作」列 `⋯` 菜单：编辑 / 复制 / 删除 / 上移 / 下移。
- 底部：灰字提示「规则按列表顺序匹配 · 总开关关闭时全部不生效」+ `MiniBtn`（26 高）「紧急禁用…」「复制」「粘贴」。

### 编辑对话框（470 宽、`SizeToContent=Height`、`TitleBar` + `BtnGhost` / `BtnPrimary`）

按 `HotkeyEditDialog` 的 `SectionLabel` 分组 + `MinHeight` 30/32 输入框：

1. **说明** `TextBox`
2. **触发方式** `ComboBox`（定时 / 启动后 / 事件）——按 `VisibleWhen` 语义收起其它参数区；枚举**只翻译显示层**（`OptionLabels`），存英文键
3. **参数区**（随触发方式切换）：定时 = 间隔分钟 / 每天 HH:mm；启动后 = 延迟秒数 + 「仅每日第一次启动」「仅开机自启时」；事件（二期）= 事件类型下拉 + 对应参数
4. **动作**：只读框 + 「选择动作…」→ 复用 `Views/ActionPoolPicker.xaml(.cs)`（左列表 + 右预览），别另写选择器
5. **作用范围**：直接复用 `HotkeyEditDialog` 的 `ScopeBox` + `FilterPanel`（跟随全局默认 / 全部窗口 / 白名单 / 黑名单 + 进程 / 标题 / 类名），不另写一套匹配
6. **高级参数（N）** 折叠：延迟 / 防抖 / 节流 / 命中后不再判断后续规则（照 `InputSpec.Advanced` 的「设过才自动展开」）
7. 灰字提示 + 底部右对齐「取消 / 确定」

- 二期切到「事件」时，**同一个对话框**里按事件类型收起 / 展开三种参数表单（窗口/进程切换、剪贴板变化、文件系统变化），不另建窗口。
- 保存：`TriggerRegistry.Load(file)` 立即生效（不必重启）+ `TriggerStore.Modify(path, …)` 只写 `configs/triggers.json`（照 `HotkeysView.Save()`）。
- 不做「保存并继续添加」（那是步骤编辑器的连续编辑，规则不需要）。

### 不做的

- 不做左分类栏、不做「左列表 + 右详情」就地编辑；不弹模态之外的第二套编辑 UI；一期不做「触发记录」列 / 页（二期可选）。

## 10. 引用回收与备份

- `Core/Storage/ActionRemoval.cs` 的 `Remove(id)` 顺序固定「内存清单 → **快捷键绑定 + 面板槽位 + 触发规则** → 动作目录 → 宏 / 模板图 / 悬浮回收」；`RepairReferences` 里按磁盘动作清单幂等清掉悬空触发引用（`Features/Builtin/BindingRef.cs` 的 `IsLive` 判内置功能引用是否还活着）。
- `configs/` 无条件进备份，`BackupService` 不用改；删规则不留孤儿。

## 11. 待决项（不擅自定）

1. **Cron**：一期只做「固定间隔 + 每天 HH:mm」，还是引入 `Cronos` / 自实现 6 段 Cron？Quicker 用 `Cronos.CronExpression`。
2. **动作参数 / 事件输出参数**：`ActionParam` 怎么传、事件输出怎么进动作变量（扩展 `ActionRunOptions`？新增取值器？）。
3. **规则优先级与 `SkipFurther`**：是否按列表顺序取第一条命中即停；哪些事件源不适用（Quicker 说目录监控 / 空闲 / 定时重复不适合）。
4. **紧急禁用的形式**：空文件 / 托盘菜单 / 热键，还是都要；文件放数据根还是固定路径。
5. **Quicker 那几个启动选项**：「仅每日第一次启动」「仅开机自启时」「限定主机」要不要照搬。
6. **动作内容限制**：自动触发的动作是否允许注入键鼠 / 弹界面（无人值守时可能打断用户）。
7. **触发记录与调试入口**：要不要、放哪。
8. **待补词条**：触发规则 / 事件源 / 触发方式（确认后进 `CONTEXT.md`）。

## 12. 验收

- 构建 **0 错 0 警**（构建前 `Stop-Process -Name LTools`）；Debug 用 `-p:NoWin32Manifest=true`。
- 七个域文件齐全；改自动触发只动 `configs/triggers.json`，其它域 mtime 不变。
- 真机：最小间隔定时、每天时刻、启动后延迟、全局总开关关闭、紧急禁用生效、删动作后触发引用被清掉、连续命中不炸（并发单飞）。
- 二期：三个事件源各一个最小用例（切窗口、复制文本、新建文件），且不会因 LTools 自己的剪贴板写入自触发。
