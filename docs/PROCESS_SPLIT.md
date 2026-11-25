# 进程拆分：界面 asInvoker + 提权执行器

> 状态：**设计稿（未实施）**。起因是「把文件 / 文件夹拖进超级面板，生成启动文件 / 打开文件夹的动作」，
> 但它撞上的是一条结构性问题：**提权进程的窗口收不到资源管理器的拖放**（UIPI）。
> 本文把「界面搬进普通权限进程、只留执行器提权」的完整设计摆出来，供决策。

## 1. 问题与目标

### 1.1 直接需求

- 把文件 / 文件夹从资源管理器拖进超级面板，落下来就得到一个「用默认程序打开文件」或「打开文件夹」的动作。
- 多选 = 多个动作；落在哪个分组、哪两个磁贴之间，就插到那里。

### 1.2 为什么它不是一个「拖放处理器」的事

`LTools.exe` 是 `requireAdministrator`。资源管理器（中完整性）往高完整性窗口投递拖放时：

- OLE 拖放（WPF `AllowDrop`）被 UIPI 直接拦掉；
- 连老式 `WM_DROPFILES` 都不发（`tests/ShareSmoke -- --explorer` 有一条断言专门守着这件事，钩子数到 0）；
- 结果就是：**发布版里所有窗口的拖放都是死的**——面板、速记附件、贴图替换、拼图、图片编辑，一个都收不到。
  这些窗口写了 `AllowDrop`，只在「从资源管理器启动的非提权 Debug 进程」里能用，所以平时不容易发现。

共享窗口为此已经拆出了一个 `ShareHost.exe`（普通权限）。如果面板再拆一个「投放层」，就是第二套补丁；
以后每加一个要拖放的窗口，都要再决定一次「它在哪个进程」。**这是根因，不是症状。**

### 1.3 目标 / 非目标

| 目标 | 说明 |
|---|---|
| ✅ 拖放恢复正常 | 面板（以及以后所有窗口）一行 `AllowDrop` 就能收资源管理器的文件 |
| ✅ 管理员只给需要它的部分 | 全局钩子、键鼠注入、UIA、窗口操作、脚本；UI 不再要管理员 |
| ✅ 启动更快、UAC 更少 | UI 进程瞬时启动；提权靠任务计划静默拉起（见 §6） |
| ✅ 能收敛掉现有补丁 | `ShareHost.exe` / `Native/ShellToken` / `Views/FileDropBridge` 全部可以删除 |
| ❌ 不改动作模型 | 动作单元、校验器、编辑器、`StepText`、配置格式一律不动 |
| ❌ 不改用户可见的操作方式 | 面板、快捷键、悬浮、托盘、自动触发的用法不变 |
| ❌ 不做夸进程的插件/脚本生态 | 脚本仍在执行器进程内跑（见 §8 的安全面） |

## 2. 目标架构

一个 exe，两种模式（不新增可执行文件）：

```
LTools.exe                     （asInvoker，中完整性）
  ├─ 托盘 / 超级面板 / 悬浮 / 设置 / 速记 / 贴图 / 截图 Pro / 共享窗口
  ├─ Toast / 各种对话框 / 动作编辑器 / 动作选择器
  ├─ 配置与动作库的写属主（configs/ actions/ subactions/ staging/ share/）
  ├─ 自动触发（看窗口 / 剪贴板 / 文件）
  └─ MCP 服务（HTTP）与 stdio 桥、--selftest
            ⇅  命名管道：请求 / 响应 / 事件（JSON Lines，见 §5）
LTools.exe --worker            （requireAdministrator 由任务计划 / UAC 拉起）
  ├─ 全局键鼠钩子（HookDispatcher 专用线程，原样保留）
  ├─ 动作执行（ActionEngine / AutomationRunner / 子动作 / 宏回放）
  ├─ 键鼠注入、窗口操作、UIA、OCR、脚本（AHK / C#）
  ├─ 宏录制（需要看得见提权窗口里的按键）
  └─ 运行历史 / 失败报告 / 运行期截图（cache/screenshots）
```

### 2.1 职责矩阵

| 能力 | 现在 | 拆分后 |
|---|---|---|
| 托盘、面板、悬浮、设置、速记、贴图、截图覆盖层 | 提权进程 | **UI** |
| 拖放（面板 / 速记 / 贴图 / 拼图…） | 提权进程（死） | **UI**（恢复） |
| 配置 / 动作 / 草稿 / 备份 / 资源回收 | 提权进程 | **UI**（唯一写属主） |
| 自动触发（事件源 + 排程 + 投递） | 提权进程 | **执行器**（要跑动作，且不能被 UI 卡住） |
| 全局钩子、CapsLock 状态机、面板手势、边缘滚轮 | 提权进程 | **执行器**（不变） |
| 动作执行（引擎 / 运行锁 / 停止热键 / 运行历史） | 提权进程 | **执行器** |
| 动作里的交互（输入框 / 选择 / 确认 / 文本窗口 / 取点 / 框选 / 文件框） | 执行器所在进程的 UI 线程 | **UI**（执行器发请求，见 §5.3） |
| 快捷键录入、宏录制 | 钩子线程 + UI | **执行器**录，结果回 UI |
| 宏回放 / `input.*` / `window.*` / `ui.uia` | 提权进程 | **执行器** |
| 截图（GDI）/ 贴图 / 图片编辑 | 提权进程 | **UI** |
| MCP 服务 / stdio 桥 | 提权进程 | **UI**（要执行的工具转发给执行器） |
| `--selftest` | 提权进程 | **UI**（纯逻辑，不需要管理员） |
| `--perf` 输入延迟探针 | 提权进程 | **执行器模式**（要装钩子，单独进程跑完即退） |

## 3. 动作里的 UI：耦合清单（这是最大的一块）

现在引擎与 UI 是同一个进程，很多动作单元直接 `Application.Current.Dispatcher.Invoke(...)`。
拆分后这些必须变成「执行器 → UI」的交互请求。清单（全部实盘 grep 过）：

| 位置 | 今天怎么做 | 拆分后 |
|---|---|---|
| `Features/Units/Ui/UserInputUnit`（`ui.input`） | `Dispatcher.Invoke` 弹 `InputDialog` | 交互请求 `input` |
| `Features/Units/Ui/SelectUnit` / `ConfirmUnit` | `ChoiceWindowService` + 轮询 | 交互请求 `select` / `confirm`（超时、取消照旧） |
| `Features/Units/Ui/TextWindowUnit` | `TextViewWindow` + 轮询读写 / 关窗 | 交互请求 `textWindow`（open / read / close，带 key） |
| `Features/Units/Ui/ToastUnit` | `ToastService.Show` | 通知 `toast`（fire-and-forget） |
| `Features/Units/Files/SelectFileUnit` | `OpenFileDialog` / `OpenFolderDialog` | 交互请求 `selectFile` |
| `Features/Units/Screen/PickPointUnit` | `ScreenPicker.TryPickPoint`（`Dispatcher.Invoke`） | 交互请求 `pickPoint` |
| `Features/Flow/ScreenshotProbe` | 框选窗口 + `ToastService.SuppressForCapture` + 收起运行指示器 | 交互请求 `capture`（返回落盘路径） |
| `Features/Flow/TextLocator` / `Units/Screen/ScreenBitmap` | 同上（截图前收起指示器） | 同上（指示器由 UI 收，见下） |
| `Features/Flow/SelectionCapture` | 把 WPF 对象搬到 UI 线程做备份 / 还原 | 整体搬进 UI 侧 |
| `AutomationRunner` 运行提示 | `RecordingIndicatorWindow` + `ToastService` | 事件 `indicator` / `toast` |
| `AutomationRunner` 失败报告 | `FailureLog.Write` + `ToastService`（点开日志） | 执行器写文件；`toast` 里带「查看日志」动作 |
| `ActionExecutor.AskArguments` | `Ui.Post` 弹 `ParamInputDialog` | 交互请求 `askParams` |
| `Views/ActionEditor` 试运行 | 直接 `AutomationRunner.TryRunProgram` | `run` 请求（带 program JSON + 资源根），轨迹走事件流 |
| `Features/KeyWaiter`（`input.waitKey`） | 钩子线程里的等待者 | 不变（在执行器） |

**判定口径**：动作单元**执行**留在执行器；只有「必须跟用户交互」的那一步回 UI 做。
这条规则也解释了为什么 `ui.uia` 不搬：它要读提权窗口的控件树，留在执行器。

## 4. 状态镜像（钩子读的那份状态）

钩子回调里读的东西今天都在进程内：`AppState`（CapsLock 修饰键开关、面板开关、阈值）、
`HookMirror`（面板 / 悬浮菜单矩形、是否钉住、是否正在交互）、以及截图 Pro 的输入让路标志
（录制条 / 贴图 / 长截图 / 取点器 / 自动化运行 / 快捷键录入 / 宏录制）。

拆分后这些由 **UI 推给执行器**，执行器缓存成内存快照（钩子只读快照，仍然零阻塞）：

| 快照 | 谁推 | 何时 |
|---|---|---|
| `panel`（open / pinned / interacting / active / hwnd / 菜单矩形） | UI | 面板显示 / 隐藏 / 移动 / 菜单开关（已有 `PublishMirror` 的时机） |
| `float`（菜单矩形） | UI | 悬浮菜单开关 |
| `inputRoute`（录制条 / 贴图 / 长截图 / 取点器 / 自动化 / 录入 / 录制） | UI | 进入 / 退出这些状态时 |
| `settings`（CapsLock 修饰键、面板开关 / 阈值、边缘滚轮、黑白名单） | UI | 保存设置后 |
| `hotkeys` / `triggers` / `macros` | UI | 保存后发 `reload`，执行器自己读盘 |

延迟不敏感（都是「打开面板 / 开始录制」这种事件），推送失败就下一拍重发一次，丢了也不影响输入链路。

## 5. 接口契约

### 5.1 通道

- 命名管道：`\\.\pipe\LToolsWorker-<SessionId>`（实现里叫 `LToolsLink-<SessionId>`），
  **普通权限宿主做服务端，提权执行器做客户端**。
  - 为什么反着来：Windows 的强制完整性控制（MIC）不允许中完整性进程**写**高完整性的命名管道
    （连接直接 `Access denied`），而高完整性进程读写中完整性对象都放行。所以必须由中完整性的宿主
    建管道，提权的执行器连上去。
- 帧：4 字节小端长度 + UTF-8 JSON（不用行分隔，避免正文里的换行）。
- 双向：`request`（带 `id`）/ `response`（同 `id`）/ `event`（无 `id`）。**两个方向都能发请求**
  （`LinkChannel` 不分方向），所以「宿主发命令、执行器应答」与「执行器发事件 / 请求、宿主应答」共用一条连接。
- 超时：所有交互请求都带 `timeoutMs`；执行器侧的等待同时挂 `ctx.Token`（停止热键立刻返回）。
- 背压：轨迹事件按 120ms 合并批量发（编辑器已有这套攒批逻辑，直接复用语义）。
- 鉴权：管道 ACL 只给当前用户；握手带 `protocol` 版本 + 随机令牌（**提权进程**写进
  `state/worker.token`，仅当前用户可读；宿主只读它）。**这挡不住同用户的中完整性进程**——见 §8，这是结构性的。

### 5.2 UI → 执行器

| 方法 | 入参 | 返回 / 事件 | 说明 |
|---|---|---|---|
| `hello` | protocol、clientPid、token | workerPid、elevated、version | 握手；版本不一致就提示升级 |
| `run` | `actionId` + `source` + `cursor` + `initialVariables`（内置功能用 `featureRef`） | `runId` | 异步开始；参数框（如需）由执行器发 `askParams` 回 UI |
| `runDraft` | `program` JSON + `resourceRoot` + `dryRun` | `runId` | 编辑器试运行 / MCP 草稿；不落盘 |
| `stopRun` | `runId`（空 = 全部） | ok | 停止热键在执行器内部也会走同一条 |
| `queryRun` | — | `runId` / 状态 / 已跑步数 | UI 重启后恢复界面状态 |
| `captureChord` | `timeoutMs` | `chord` | 快捷键录入（替代现在的就地钩子捕获） |
| `record.start` / `record.stop` | 名称 / 目标宏 id | `macroId`、事件数、时长 | 宏录制；事件文件由执行器写 |
| `replay` | `macroId` + 回放参数 | `runId` | 试放 / 动作里的回放 |
| `reload` | `hotkeys` / `settings` / `actions` / `triggers` / `macros` | ok | 配置保存后通知（执行器丢缓存） |
| `panelSnapshot` / `floatSnapshot` / `inputRoute` / `settingsSnapshot` | 见 §4 | ok | 高频、幂等、可丢 |
| `feature` | `ltools.hotkeysToggle` / `common.windowPin` 等执行器侧内置功能 | ok | UI 触发的、需要提权的内置功能 |
| `shutdown` | `reason` | — | 退出 LTools |

### 5.3 执行器 → UI

| 方法 | 说明 |
|---|---|
| `toast` | 卡片（标题 / 正文 / 等级 / 时长 / key / 按钮） |
| `indicator` | 运行指示器：`show(文字)` / `hide` |
| `askParams` | 参数框 → `{ values }` 或 `cancelled`（`ActionParamMemory` 由 UI 写） |
| `input` / `select` / `confirm` | `ui.*` 三个对话框 → 结果或 `cancelled` / `timeout` |
| `textWindow` | `open(key, title, text, mode, …)` / `read(key)` / `close(key)` |
| `selectFile` | 文件 / 文件夹对话框（多选、筛选、初始目录）→ 路径列表 |
| `pickPoint` / `capture` | 取点 / 框选截图 → 坐标或落盘路径（截图由 UI 存 `cache/screenshots`） |
| `openWindow` | 面板 / 暂存区 / 设置 / 速记 / 采集历史 / 贴图管理 / 共享 / 日志目录 |
| `trace` | 步骤轨迹（path / 状态 / 耗时 / 解析后的参数 / 输出 / 错误） |
| `runFinished` | `{ runId, status, error, steps, elapsedMs, variables }`（历史由执行器写盘） |
| `workerState` | 执行器退出 / 崩溃 / 重新拉起：UI 显示「钩子已暂停」并可一键重启 |

**约定**：交互请求的 `response` 必须回（用户关窗 = `cancelled`）；UI 若在等待中断线，
执行器按超时失败（性质 `timeout`），不会挂死。

### 5.4 断线、重启、孤儿

| 场景 | 行为 |
|---|---|
| 执行器崩溃 | 钩子失效 → UI 立刻提示（托盘气泡 + 设置页红字），提供「重启执行器」（走任务计划，静默） |
| UI 崩溃 / 被关 | 执行器**继续跑完当前动作**（不半路松键），随后退出（没有再拉起的意义）；宏录制立即停止 |
| UI 重启 | 执行器仍在 → UI 握手 + `queryRun` 恢复指示器；面板状态重新推一遍 |
| 两个都崩 | 下次启动重来；`state/` 里的运行历史 / 失败报告保留 |

## 6. 生命周期与提权来源

- **UI**：单实例互斥体 `Local\LToolsUi`（沿用现在的名字），托盘 + 钩子全部不在它这里。
- **执行器**：单实例互斥体 `Local\LToolsWorker`；UI 启动时若不在，按以下顺序拉起：
  1. `schtasks /run LToolsWorker`（任务计划，最高权限，**静默**）；
  2. 没有任务 → `ShellExecute(runas)` 弹一次 UAC；
  3. 用户拒绝 → UI 提示「快捷键 / 动作执行不可用，只有界面能用」。
- **任务计划两条**（`Core/AutoStartService` 扩成两套，带 `HealIfStale`）：
  - `LTools`（普通权限，登录时启动，参数 `--autostart`）；
  - `LToolsWorker`（最高权限，登录时启动，参数 `--worker --autostart`）。
- 退出：UI 托盘「退出」→ `shutdown` 请求 → 执行器停钩子 / 停运行 / 关管道 → 退出；UI 随后退出。
  执行器不残留（任务计划只负责「登录时启动」，不负责守护）。
- 看门狗：执行器内部保留 `CapsLockStateMachine.WatchdogTick`（输入状态自愈）；UI 侧新增「执行器心跳」
  （管道 `ping`，3 秒没响应算崩溃）。

## 7. 数据与文件属主

| 路径 | 属主（唯一写者） |
|---|---|
| `configs/*.json` | **UI** |
| `actions/` `subactions/` `staging/` `resources/` `速记/` `trash/` | **UI** |
| `share/`（清单 + 文件 + 收件箱） | **UI**（共享窗口回 UI 后） |
| `macros/<id>.json`（事件本体） | **执行器** |
| `configs/macros.json`（元数据：名字 / 热键 / 回放设置） | **UI** |
| `state/run-history.jsonl` `state/trigger-history.jsonl` `state/values.json` `state/auto-trigger-last-start.txt` | **执行器** |
| `state/action-params.json` | **UI** |
| `logs/ltools-<日期>.log` | **UI** |
| `logs/ltools-worker-<日期>.log` `logs/last-failure.log` | **执行器** |
| `cache/screenshots/` | 执行器（运行期截图）；UI 的交互截图写同一目录、文件名唯一 |

- `Core/LogFiles` 的「哪些文件算日志」要把 `ltools-worker-*` 一起认进去（滚动 / 清理口径不变）。
- 执行器**只读** `configs/`、`actions/`、`subactions/`、`macros/`；需要刷新时由 UI 发 `reload`。
- 「一个文件一个属主」这条老规矩不变，只是属主从「一个进程」变成「两个进程各管一摊」。

## 8. 安全面变化（必须知情）

这是 B 的固有代价，不是实现瑕疵：

- **现在**：LTools 整体提权。同用户的普通（中完整性）程序**无法驱动**它——UIPI 挡住注入和消息，
  所以恶意程序只能干中完整性的事。
- **拆分后**：UI 是中完整性（可被同用户程序注入），而它能通过管道让提权执行器「跑任意动作」。
  动作里可以跑 `script.ahk` / `script.csharp` / 启动程序，于是**同用户的普通程序可以借 LTools 提权执行**。
  这正是「管理员按需」类工具（Quicker 同构）共同的结构性弱点。

可做的缓解（按成本排序）：

1. 管道 ACL 只给当前用户 + 握手令牌 + 客户端进程身份校验（挡别的用户、挡误连，**挡不住同用户注入**）。
2. 命令面收窄：执行器只接受 `actionId`（磁盘上已有的动作）与「编辑器试运行」的 program JSON；
   不接受「任意命令行 / 任意脚本正文」。恶意程序仍可改动作库再调用——所以这只是抬高门槛。
3. 审计：执行器把每次 `run` 的来源（UI pid、入参摘要、动作名）写进 `logs/`，事后可查。
4. 可选「高安全模式」：执行器不常驻，`run` 时才用任务计划拉起（用完即退）；或对 `script.*` /
   `runCommand` / `launchApp` 这类「能执行任意代码」的单元在提权侧弹一次确认。
5. 若完全不接受这条风险 → **不要做 B**，停在 A（投放层）或走 D（签名 + `uiAccess`）。

> 备注：A（普通权限投放层）的风险面小得多——它只回传文件路径，不执行动作。
> 这是 A 唯一的、但很实在的优势。

## 9. 实施路径（两条路线，建议 R1）

### R1（推荐）：strangler —— 先建「普通权限 UI 宿主」，逐个窗口搬

目标架构就是 B，但不一次搬完：先让宿主跑**超级面板**（拖放就在这一步交付），
其余窗口暂时留在提权进程；每搬一个窗口，提权进程就瘦一圈，最后它只剩钩子 + 引擎。

| 阶段 | 内容 | 估时 | 交付 / 验收 |
|---|---|---|---|
| **P0 通道 + 执行器骨架** | 命名管道（请求/响应/事件/超时/取消）、`--worker` 模式、`hello` / `ping` / `shutdown`、单实例、任务计划两条 + UAC 兜底；钩子 + 引擎原样留在提权进程 | 1–1.5 天 | 两进程能起能退；`--selftest` 仍全绿；UI 功能不变 |
| **P1 宿主 + 面板** | 普通权限宿主进程（复用现有 `ShareHost` 的启动方式，先不删）承载 `SuperPanel`；宿主 → 执行器的命令面（`run` / `feature` / `openWindow` / `reload` / `toast` / `askParams`）；配置写属主暂留提权进程，宿主的写操作走命令（重命名分组 / 拖排 / 增删动作 / 场景）；**面板 `AllowDrop` + 落点建动作** | 2–3 天 | **拖放需求交付**；面板所有既有冒烟段全绿；`PanelClipboardSmoke` 全绿 |
| **P2 交互服务** | `askParams` / `input` / `select` / `confirm` / `textWindow` / `selectFile` / `pickPoint` / `capture` / `indicator` / `toast` 全部走 RPC；`ActionParamMemory` / 交互截图归 UI；执行器侧删掉 `Dispatcher.Invoke` 依赖 | 1.5–2 天 | 动作里 6 个 `ui.*` + 取点 + 截图 + 文件框逐条冒烟；停止热键能中断等待 |
| **P3 钩子侧能力** | `panelSnapshot` / `inputRoute` / `settingsSnapshot` 推送；快捷键录入改 `captureChord`；宏录制 / 回放跨进程；自动触发搬执行器；MCP 工具按需路由；`--perf` 独立进程 | 2–3 天 | `MenuConeSmoke` / `PanelClipboardSmoke` / 录制回放段 / 快捷键段全绿；新 `SplitSmoke` |
| **P4 收尾** | 设置 / 编辑器 / 速记 / 贴图 / 截图 Pro / 共享窗口全部搬进普通权限宿主；**删 `ShareHost` / `Native/ShellToken` / `Views/FileDropBridge`**（所有窗口默认 `AllowDrop`）；日志前缀分家；打包只剩一个 exe | 2–3 天 | 全量冒烟 + 真机点一遍；发布包少一个 exe |

合计 **9–13 天**（含冒烟），风险集中在前两阶段。

### R2：一次性搬完整个 UI

不设中间态，直接按 §2 落地。代码更干净（不会出现「部分窗口在 A 进程」的过渡期），
但在切换完成前程序不可用，出问题回滚成本高。估时 8–11 天，**不推荐**。

### R3（不推荐）：只做投放层

即「A2 / A3」那套：投放层把路径回传，面板直接建动作。2–3 天见效，但 P1–P4 全都还要再做一遍，
而且这套投放层在 B 里会被删掉。适合「先要功能、架构以后再说」，不适合现在这个决策。

## 10. 回归清单

**已有（全部必须继续绿）**

- `LTools.exe --selftest`（373 用例 / 18421 断言）：校验收敛为「UI 进程跑」
- `tests/ShareSmoke`（默认 / `--explorer` / `--helper`）：共享服务与窗口；P4 后 `--helper` 段退役，
  换成「共享窗口在 UI 进程里能收拖放」
- `tests/CaptureOverlaySmoke`（`nav` / `search` / `history` / `tools` / `record` 等段）
- `tests/MenuConeSmoke`、`tests/PanelClipboardSmoke`、`tests/TableSmoke`、`tests/UiaPickSmoke`
- 仓库外：`sizing-*-smoke`、`floating-smoke`、`param-form-smoke`、`var-row-smoke`、`record-e2e`

**新增**

| 冒烟 | 断言 |
|---|---|
| `tests/SplitSmoke` | 两进程都能起；UI `elevated=0`、worker `elevated=1`；握手 + 令牌；`run` 往返；`stopRun`；UI 断开后 worker 跑完再退；worker 被杀后 UI 提示并能重启 |
| `tests/PanelDropSmoke` | 真资源管理器把文件 / 文件夹 / 多选拖进面板 → 动作被创建（名称 / 图标 / 步骤参数对）；落在两个磁贴之间 → 插入位置对；同一路径再拖 → 不重复；提权与否都成立（宿主是普通权限） |
| `tests/InteractSmoke` | 执行器请求 UI 弹参数框 / 输入框 / 选择框 / 取点 → 结果回传；用户取消 / 超时 / 按停止热键三种出口都回来 |
| 快捷键录入 | `captureChord` 能录到 CapsLock 组合与单键；Esc 取消 |

**真机清单（每阶段末尾手点一遍）**：CapsLock 轻按 Esc / 长按修饰、面板长按右键、快捷键触发、
钉住面板、悬浮、截图、录屏、速记、共享窗口拖放、动作编辑器试运行、停止热键、退出。

## 11. 风险

| 风险 | 缓解 |
|---|---|
| 钩子延迟回退（跨进程推送慢了） | 钩子只读本地快照，快照推送失败不阻塞；输入链路不新增任何同步等待（`docs/THREADING.md` 的约束继续有效） |
| 交互请求把流程挂死 | 每个交互都带超时 + `ctx.Token`；UI 断线按超时失败；停止热键在执行器本地优先 |
| 宏录制数据跨进程丢事件 | 录制期间不推进 UI 事件（已有「录制时隐藏窗口」），事件写盘在执行器；UI 只看摘要 |
| 两个进程同时写一个文件 | §7 的属主表 + 冒烟断言；新增文件先定属主 |
| 安全面下降（§8） | 见 §8 的五条缓解；最坏退路是停在 A |
| 过渡期两套 UI 状态 | R1 的 P1 只让面板进宿主，其余不动；命令面先按「宿主是瘦客户端」设计 |

## 12. 第一个交付（与本文档配套的产品行为）

落在面板上的文件 / 文件夹：

| 情形 | 生成的动作 |
|---|---|
| 文件（`*.xlsx` / `*.exe` / `*.*`） | 1 步「用默认程序打开文件」：`path` = 绝对路径，`workdir` = 文件所在目录 |
| 文件夹 | 1 步「在资源管理中定位 / 打开文件夹」：`operation=open`，`path` = 目录 |
| 多选 | 每个一项，一个动作；文件夹排前面 |
| 名称 | 文件 / 文件夹名（去扩展名），`ToolTip` 显示全路径 |
| 图标 | 按扩展名映射 `IconCatalog`（exe→app、图片→image、md/txt→note、html/pdf→file、文件夹→folder，兜底 file） |
| 去重 | 同一路径已有动作 → 不新建，toast「面板里已经有它了」 |
| 落点 | 拖到磁贴之间 → 插到该位置；拖到分组 → 组末尾；空白 → 当前分组末尾 |
| 失败 | 路径不可读 / 不支持的类型 → 跳过并汇总一条 toast，其余照建 |

同时补两条不依赖拖放的入口（不额外花时间，P1 顺带）：

- 面板 `Ctrl+V`：读剪贴板文件列表（`CF_HDROP`）→ 同上；
- 面板右键「从文件 / 文件夹创建动作…」→ `OpenFileDialog` / `OpenFolderDialog`（可多选）。

## 13. 待你决定的点

1. 走哪条路线：R1（推荐）/ R2 / R3（先功能）？—— **已定：R1**。
2. §8 的安全面下降能不能接受？不能的话就停在 A 或走 D。—— **已定：接受（加缓解）**。
3. 执行器的提权来源：默认走任务计划（首次安装要写一条任务），没有任务时允许 UAC 兜底吗？
4. 「高安全模式」（执行器不常驻、每次运行提权）要不要做，还是等有人需要再说？
5. **拖放这一传怎么走**：投放层（1 天，P4 删掉）还是直接搬面板（3–5 天，无返工）？

## 14. 实施记录

**2026-11-24 · 先落地「不依赖传输层」的那半（P1 的一部分）**

- `Features/Panel/PanelDrop.cs`：一个路径 = 一个 1 步动作
  - 文件夹 → `file.explorer`（`operation=open`）；文件 → `file.openFile`（`workdir` = 所在目录）
  - 名称去扩展名、按扩展名挑 `IconCatalog` 图标、文件夹排在文件前
  - 同批去重 + 已有动作去重（扫 `file.openFile` / `file.explorer` 的字面量路径）+ 找不到的跳过并计数
  - `Insert` 负责落位（分组指定下标）→ `ConfigStore.AddAction` → `action.json` / 面板配置落盘
- 面板入口（都不依赖进程拆分，提权版今天就能用）：
  - 分组菜单 / 空白菜单 →「从文件创建动作…」「从文件夹创建动作…」（`OpenFileDialog` 多选 / `OpenFolderDialog`）
  - `Ctrl+V` → `NativeClipboard.TryGetFileList`，落到光标下的分组（不在分组里就第一个）
- 测试
  - `--selftest` 的 `panelDrop` 组：12 用例 / 51 断言（含一条**真路径**用例）
  - `tests/PanelClipboardSmoke` 新增一段：`Insert` 落位 / 顺序 / 动作体 Id 对齐 / `action.json` 落盘（27 断言全绿）
  - 真面板探针（仓库外）：3 个动作进分组、磁贴出现在面板上、同一路径再添加不重复
- 踩到的坑：`Build` 的默认探测一开始写成 `(File.Exists, Directory.Exists)`，把「目录」判成「不存在」；
  假探测的自测全绿，真路径才暴露。现在默认探测是「目录存在 → Exists = true」，并有一条真路径用例守着。

**2026-11-24 · P0 链路层（协议 + 收发）**

- `Core/Link/LinkProtocol.cs`：消息形状（`id` / `kind` / `method` / `ok` / `error` / `payload`）、
  帧格式 `[4 字节长度][UTF-8 JSON]`、`TryRead` 三态（切出一条 / 还不够 / 帧坏了）、管道名（裸名，按会话）、协议版本。
- `Core/Link/LinkChannel.cs`：一条连接 = 一个 `Stream`；读循环单读者、写有锁、请求按 `id` 配对、
  超时抛 `TimeoutException`、断开时在等的请求立即失败；对方请求丢线程池处理，**不堵读循环**。
- `Core/Link/LinkTransport.cs`：`LinkServer`（一次一个客户端，断了下一条接上；第一条必须是 `hello`，
  协议 / 令牌不对就拒绝）与 `LinkClient`（握手 + `ConnectWithRetryAsync`）。
- 自测 `link` 组：7 用例 / 51 断言（形状 / 负载 / 粘包 / 拆包 / 坏帧 / 前缀 / 管道名）。
- 冒烟 `tests/LinkSmoke`：14 条，真管道上验令牌拒绝、握手、请求 / 答复、双向事件、
  服务端异常 → 失败答复、超时（150ms 请求 156ms 返回且连接照常）、1 MB 负载、断开事件、重启重连。
  `InternalsVisibleTo("LinkSmoke")`（其它冒烟仍走反射）。
- 注意：服务端**同一时刻只服务一个客户端**（`maxNumberOfServerInstances = 1`），所以「令牌不对」这类
  测试要在没有客户端连着的时候做——第二条连接会一直等实例，表现为超时。
- 尚未接线：`LinkServer` 还没在 `App.OnStartup` 里启动（等宿主真正来连的时候再接，见下一步）。

**2026-11-24 · P1b-1 只读初始化（宿主读得到、写不了）**

- `ConfigStore.InitializeReadOnly(path)`：加载动作库 / 面板 / 快捷键 / 宏元数据，**不修引用、不播种快捷键、
  不清 trash / 孤儿、不写任何文件**；置 `ConfigStore.ReadOnly`。
- `ConfigStore.Save` 与 `PanelLibrary.Save` 在只读进程里直接拒绝（记一条 Warn、返回 false）——
  两个进程抢着写同一个 `actions/` / `configs/` 是拆分后的头号风险，先在这一层堵住。
- `PanelLibrary` 抽出 `Load(persist)`：`Reload()` = 会补默认面板，`LoadReadOnly()` = 不补。
- 冒烟 `tests/PanelHostSmoke`：11 条。先按主程序 `Initialize` 建临时数据根（会写），快照整个目录 →
  `InitializeReadOnly` → 再快照，断言**逐字节相同**（名字 / 大小 / SHA-256 / 修改时间），动作 / 面板 /
  种子快捷键都读得到；只读进程里两个 `Save` 被拒且目录不变；空数据根不凭空生成默认面板。
- 尚未接线：宿主（`ShareHost.exe`）还没调它，也还没接 `LinkClient`（下一步：面板进宿主）。

**2026-11-24 · P1b-2 面板命令面（提权侧的写属主）**

- `Core/Link/LinkCommands.cs`：命令名常量 + DTO（`ActionSummary` / `PanelSnapshot` / `ActionAddRequest` /
  `ActionIdRequest` / `PanelShowRequest`）。**这一轮只落「面板 / 动作」这一段**，`run` / `toast` / `trace`
  等真正搬执行器时再补，补的时候只加常量 + DTO。
- `Core/Link/PanelHost.cs`：提权侧处理 `panel.snapshot` / `panel.replace` / `action.add` /
  `action.delete` / `action.duplicate`；全部「校验 → 改数据 → 落盘」，任何异常翻成失败答复，不抛回读循环。
  删除沿用 `ConfigStore.RemoveAction` 的既有收尾（它内部已落盘，**别再补一次 Save**）。
- `PanelLibrary.Replace(panel)`：整份替换 + 规范化 + 落盘；宿主只提交整份配置，不逐个建写命令。
- 自测 `link` 组加 1 个用例（方法名互不相同 / 新建·定位·显示面板负载往返 / 64 位句柄不丢精度 /
  快照整份面板往返 / 未知命令回失败答复），总 393 用例 / 18549 断言。
- 冒烟 `tests/LinkSmoke` 追加「面板命令」段（14 → 31 条）：临时数据根上真管道驱动快照 / 替换 / 新建 /
  复制 / 删除，断言内存与磁盘一致、删动作连面板引用一起清。
- 尚未接线：`App.OnStartup` 还没起 `LinkServer`，宿主也还没接这个处理函数（下一步：宿主连 `LinkClient`
  + 加载面板）。

**2026-11-24 · P1b-3 真机接线（宿主做服务端、提权进程做客户端）**

- **方向反了，而且必须反**：Windows 的强制完整性控制不允许中完整性进程写高完整性的命名管道
  （实测宿主连接提权侧的服务端 → `UnauthorizedAccessException: Access to the path is denied`）。
  改成「中完整性宿主做服务端、提权进程做客户端」两个方向都放行。§5.1 已改。
- `Core/Link/LinkToken.cs`：令牌 `state/worker.token`（32 位小写十六进制），**提权进程写、宿主读**
  （提权侧一定写得了，宿主在 Program Files 之类目录可能写不了）。
- `Core/Link/LinkWorker.cs`：提权侧的客户端 + 退避重连循环（250ms → 最多 5s），连上后用
  `PanelHost.HandleAsync` 应答命令，握手后发 `client.ready`。
- `LinkClient` 支持传 handler（`LinkChannel` 本来就不分方向），并暴露 `WhenClosed`。
- `ShareHost/App.xaml.cs`：起 `LinkServer`（宿主侧，stub 应答），收到 `client.ready` 后拉
  `panel.snapshot` 存进 `Snapshot`（下一步用它渲染面板）；后台 `ConfigStore.InitializeReadOnly`。
- **每个进程写自己的日志文件**：`AppLog.Configure` 加可选 `fileTag`，宿主写 `logs/ltools-host-<日期>.log`。
  两个进程用同一个文件时，后打开的那个 `StreamWriter` 会一直因共享冲突写失败、错误被吞掉，**表面就是
  「一行日志都没有」**（这次踩到）。`LogFiles` 的 `ltools-` 前缀已经覆盖它。
- 真机实测（dev 构建、提权 shell）：清掉令牌 → 起 `LTools.exe` → 提权侧写 32 位令牌 → 自动拉起
  `ShareHost.exe`（普通权限）→ 宿主日志：`链路服务端已启动` / `主程序已连接` / `面板快照：8 个动作、1 个全局场景`。
- 回归：`LinkSmoke` 改成生产方向（服务端发请求、客户端 `PanelHost` 应答）并加一条「反向角色」断言（32 条）；
  `--selftest` 393 用例 / 18549 断言；`PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。

**2026-11-24 · P1b-4 面板在宿主里渲染（只读预览 + 磁贴转交）**

- 唤起入口收口到 `Features/PanelLauncher`（`Show` / `Toggle`）：右键长按手势、`CapsLock+T`、
  托盘菜单、内置功能 `ltools.panel` 全走它。迁移开关 `PanelRouting.Enabled`（环境变量
  **`LTOOLS_PANEL_HOST=1`**，默认关）打开且宿主连着 → 发 `panel.show` / `panel.toggle`；否则本地开。
  开关打开时 `App.OnStartup` 也会把宿主随主程序拉起来（它同时是共享助手）。
- `panel.show` 负载带光标坐标（`PanelShowRequest(Hwnd, X, Y)`），宿主用
  `Features.SuperPanel.Show(hwnd, x, y)` 在**唤起瞬间的光标处**定位，而不是宿主当前光标。
- **宿主里绝不本地执行动作**：`ActionExecutor.RunRef` 第一句问 `PanelHostBridge.TryForwardRun`
  （只在宿主里 attach）。连不上主程序时**拦下**（返回 true + 一条 Warn），不退回本地——动作里
  可能有需要提权的注入 / 脚本。提权侧 `PanelHost` 新增 `run.action`，落到 `Ui.Post(() => RunRef(...))`。
- **只读预览**（`SuperPanelWindow._preview = ConfigStore.ReadOnly`）：隐藏左栏「暂存区 / 设置」、
  强制动作页、右键菜单一概不开、`Ctrl+V` 建动作不响应、分组改名不响应；磁贴 / 截图仍可点（转交）。
  暂存区（草稿读写）、编辑器、设置都还是提权进程的事，下一步用 `open` 命令接过去再翻默认值。
- 真机实测（dev、提权 shell、固定令牌 + 探针发 `panel.show`）：宿主（**中等完整性**）真的把面板画出来了
  ——截图里能看到场景「默认」、14 个磁贴、上下文区 `通用 / LTools.exe / OpenChamber.exe`，
  左栏只剩动作面板 / 截图两个入口（暂存区与设置已按预览隐藏）。宿主日志：
  `显示面板（普通权限窗口）`；断开后 `主程序已断开`。
- 回归：`LinkSmoke` 加「磁贴转交」段（35 条）；`--selftest` 393 用例 / 18556 断言；
  `PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。
- **默认仍是旧路径**：不设 `LTOOLS_PANEL_HOST=1` 时行为与以前完全一致（面板在提权进程、宿主只在共享时起）。

**2026-11-24 · P1b-5 `open.window`（设置 / 暂存区回提权进程）**

- 新增 `open.window` + `OpenWindowRequest(Target, Arg)`；`LinkTargets` = `settings` / `panel.stash` /
  `panel.scene`。提权侧 `PanelHost.OpenWindow` 一律 `Ui.Post` 到自己的 UI 线程开窗：
  `ConfigHelper.Open()` / `SuperPanel.ShowStash()` / `ConfigHelper.OpenPanelScene(id)`。
- 宿主侧 `PanelHostBridge.TryOpen`：连不上时**拦下并记 Warn**（不本地开，否则会在中等完整性进程里
  出现一个写不了配置的设置窗口）。
- 预览里左栏「暂存区 / 设置」不再隐藏，改成转给主程序：点暂存区 = 收起宿主面板 → 主程序开它的暂存区；
  点设置 = 主程序开设置窗口。
- 真机实测（探针发 `panel.show`）：截图里左栏回到「动作面板 / 暂存区 / 截图」三个入口、标题栏有齿轮。
  面板可用的部分：磁贴执行、搜索、截图、钉住 / 拖动、打开设置 / 暂存区（后两者跳到主程序窗口）。
- 仍是**只读预览**：右键菜单（新建 / 编辑 / 删除 / 复制 / 分组 / 场景）还关着——下一步把
  `PanelActionOps` 的写操作接成命令（`panel.op.*`），再翻默认值。
- 回归：`LinkSmoke` 38 条（新增 `open.window` 转交断言）；`--selftest` 393 用例 / 18561 断言；
  `PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。

**2026-11-24 · P1b-6 `panel.op.*` + 结构保存转交（写属主真正搬过去）**

- **面板结构改动**（拖磁贴、分组改名、增删分组 / 场景、切活动场景…）在宿主里照常改内存，
  `PanelLibrary.Save()` 在只读进程里不再简单拒绝：有链路就改成**整份 `panel.replace`** 交给提权进程落盘
  （`PanelHostBridge.TryPushPanel`）；连不上时拦下并 Warn，不本地写。一个 choke point 覆盖所有结构入口。
- **动作级操作**（`PanelActionOps` 的会建 / 删动作或开编辑器的入口）加 `panel.op`：
  `addNew` / `addExisting` / `edit` / `delete` / `paste` / `export` / `toSubAction`。
  - 宿主侧：`PanelHostBridge.TryActionOp` 转过去并**当已处理**（返回 null / false），不在宿主开编辑器、不建文件。
  - 提权侧：`PanelHost.HandleActionOp` 用 `Ui.Post` 排到 UI 线程（编辑器 / 确认框都是模态），完成后
    给宿主推 `actions.changed`。
  - 粘贴要带剪贴板条目（剪贴板是进程内的）：`ActionOpRequest` 带 `Kind/Reference/Name`，
    提权侧调 `PanelActionOps.PasteEntry`（宿主与提权共用的那一份实现）。
- `PanelLibrary.FindGroup(id)`：按分组 Id 在全局 / 上下文所有场景（含 `Common`）里定位，供 RPC 用。
- **宿主收到 `actions.changed`**：在 UI 线程 `ConfigStore.InitializeReadOnly` 重读磁盘，然后
  `SuperPanel.Refresh()` 重建打开着的面板（场景 / 磁贴 / 动作名都跟着变）。
- 仍是**只读预览**：右键菜单故意还关着——里面还有「悬浮 / 取消悬浮」「回放宏」「编辑场景…（开设置）」
  「从文件建动作」这些走 `FloatingManager` / `MacroPlayer` / `ConfigHelper` / `PanelDrop` 的入口，
  在宿主里跑会落到错误的进程。下一步把它们接成命令后再放开菜单。
- 回归：`LinkSmoke` 43 条（新增 `panel.op` / `panel.replace` 转交）；`--selftest` 393 用例 / 18569 断言；
  `PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。

**2026-11-24 · P1b-7 右键菜单放开（安全子集）**

- `panel.op` 新增 `dropPaths`（`ActionOpRequest.Paths`）：宿主把「从文件 / 文件夹创建动作」与 `Ctrl+V`
  的路径列表转给提权进程，`PanelDrop.Build` + `Insert` 在主程序里跑（它要写 `actions/`）。
- 场景 / 设置入口收口到 `OpenScenePage(sceneId)`：预览里 `open.window panel.scene` 让主程序开设置，
  不再在宿主里开。
- **右键菜单在预览里放开**，但先裁掉落到错误进程的三类：宏「试放 / 重新录制」（要钩子与注入）、
  「在暂存区编辑…」（写 `staging/`）、「悬浮 / 悬浮整组」（悬浮窗在提权进程）。
  保留的可选项全部已接线：编辑 / 复制引用 / 复制为副本 / 粘贴 / 导出 / 另存为子动作 / 复制名称 / Id /
  从此组移除 / 删除动作；分组与场景菜单（新建 / 重命名 / 删除 / 增删场景 / 从文件建动作）也全部通过
  `panel.replace` / `panel.op` 落到主程序。
- 分组改名、`Ctrl+V` 建动作重新可用（结构改动走 `panel.replace`、建动作走 `dropPaths`）。
- 真机实测（探针 `panel.show` + 真右键点磁贴截图）：宿主里的菜单正常弹出，可见
  「编辑… / 复制引用 / 复制为副本 / 导出动作… / 另存为子动作… / 复制名称 / 复制 Id / 从此组移除 / 删除动作…」，
  宏与悬浮项按预期没有出现。
- 回归：`--selftest` 393 用例 / 18574 断言；`LinkSmoke` 43；`PanelHostSmoke` 11；
  `ShareSmoke -- --explorer --helper` 103。
- **默认开关仍关**：`LTOOLS_PANEL_HOST=1` 才走宿主；下一步把「悬浮 / 宏 / 暂存区编辑」接完，
  再把默认值翻过来。

**2026-11-24 · P1b-8 菜单不再裁剪：悬浮 / 宏 / 暂存区编辑也走 panel.op**

- `panel.op` 补齐最后五类：`float`（`Id`）/ `floatGroup`（`GroupId`）/ `replayMacro`（`Id`）/
  `recordMacro`（`Id` 为空 = 新录，带 `Id` + `Name` = 重新录制到那个宏）/ `editInStash`（`Id`）。
  它们都要钩子、键鼠注入、悬浮窗或 `staging/` 写入，**只在提权进程里做**；宿主侧 `PanelHostBridge.TryActionOp`
  转过去并当已处理。三个菜单入口（试放 / 重录 / 在暂存区编辑）在预览里**先收起宿主的面板**再转发——
  录制期间钩子透明，留着面板会把面板上的点击录进去（与本地那条一致）。
- **悬浮状态进快照**：`PanelSnapshot` 多一个 `Floating`（正在悬浮的动作引用），
  `PanelHostBridge.SetFloating` / `IsFloating` 在宿主里存一份；菜单标签照它显示「悬浮此动作 / 取消悬浮」
  （宿主里没有悬浮库，`FloatingLibrary.Items` 是空的）。
- **快照必须先于面板重建拉**：右键菜单是**重建面板时**一并建好的（不是每次右键现建），
  先 `SuperPanel.Refresh()` 再拉快照会让菜单一直停在旧标签。`ReloadAndRefreshAsync` 现在的顺序是
  「重读配置 → 拉快照（灌悬浮状态）→ 刷新面板」。
- 真机实测（宿主里真右键 + 真点击，跑的是主程序里的 `FloatingManager`）：
  点「悬浮此动作」→ 提权进程真的建出悬浮按钮、宿主收到 `actions.changed` 后菜单变成「取消悬浮」；
  再点「取消悬浮」→ 快照回到 0 个悬浮、标签回到「悬浮此动作」。
- 回归：`--selftest` 393 用例 / 18583 断言（新增面板操作名唯一、快照悬浮引用往返）；
  `LinkSmoke` 53 条（新增五个操作转交 + 悬浮状态缓存 + 快照带悬浮引用）；`PanelHostSmoke` 11；
  `ShareSmoke -- --explorer --helper` 103。
- **默认开关仍关**：`LTOOLS_PANEL_HOST=1` 才走宿主。下一步：把暂存区页 / `Ctrl+V` 之外的其余入口扫一遍，
  然后翻默认值、删开关。

**2026-11-24 · P1b-9 暂存区页搬进宿主（草稿写入仍归提权进程）**

- 左栏「暂存区」在宿主里**不再整块转回主程序**：`StashPane` 加上 `PreviewMode`，卡片照常列出
  （`DraftStore.List` / `Read` 本来就是只读的），写操作全部走 `panel.op`：
  `stashOpen` / `stashRun` / `stashPromote`（`Kind` = `new` / `replace`）/ `stashDelete` / `stashClear`。
- **一份实现**：卡片背后那五件事抽到 `Features/Panel/StashOps.cs`（打开草稿 / 试运行 / 采纳 / 删除 / 清空），
  提权侧的 `panel.op` 与面板里的 `StashPane` 都调它；两边只有「要不要转交」这一处分叉。
- 「打开草稿目录」与「刷新」留在宿主本地（纯读 / 开资源管理器）；「AI 接入设置…」走
  `open.window ai`（新目标 `LinkTargets.Ai`）。
- `LinkTargets.PanelStash` 随之退役（宿主自己就能看暂存区了，没人再需要「让主程序打开它的暂存区」）。
- 真机实测：宿主里点左栏暂存区 → 4 张卡片（无效 id 的旧目录按规则跳过）；卡片右键菜单齐全；
  点「在编辑器中打开」→ 提权进程收到 `stashOpen（id=…）` 并在**它那里**开出「编辑草稿 · …」窗口。
- 回归：`--selftest` 393 用例 / 18593 断言；`LinkSmoke` 60 条（新增五个暂存区操作转交 + `open.window ai`）；
  `PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。
- **默认开关仍关**：`LTOOLS_PANEL_HOST=1` 才走宿主。下一步：翻默认值、删开关与 `PanelRouting` 分叉。

**2026-11-24 · P1b-10 默认值翻过来：面板默认在普通权限宿主里**

- `PanelRouting.Enabled` 从「设 `LTOOLS_PANEL_HOST=1` 才开」翻成「**默认开**，设 `LTOOLS_PANEL_HOST=0`
  才退回提权进程」——留这一版做回退口，下一版连它一起删。
- 随之常驻：`App.OnStartup` 现在默认拉起 `ShareHost.exe`（`run` 意图，窗口隐藏；共享服务仍按
  `Share.Running` 决定开不开），主程序退出时 `ShareHelper.Stop()` 一并停掉，不留孤进程。
- **面板永远唤得出来**：`PanelLauncher` 仍保留本地兜底——宿主没起来 / 启动那几秒还没连上时，
  面板在提权进程里开（功能与以前一致），连上之后就一直走宿主。
- 宿主「先按唤起、后读完配置」的竞态：`LoadReadOnlyConfig` 读完在 UI 线程 `SuperPanel.Refresh()`
  一次，避免那一份面板停在「没有任何场景 / 磁贴」的空状态。
- 真机实测（**不设任何环境变量**）：起 `LTools.exe` → 自动拉起 `ShareHost.exe` → 宿主日志
  `共享助手已启动（run…）` / `链路服务端已启动` / `主程序已连接` / `面板快照：8 个动作、1 个全局场景`；
  关掉主程序时宿主一起退出。
- 回归：`--selftest` 393 用例 / 18593 断言；`LinkSmoke` 60；`PanelHostSmoke` 11；
  `ShareSmoke -- --explorer --helper` 103。

**2026-11-24 · P1b-11 面板只有一个家：删掉 `PanelRouting`**

- `PanelRouting`（`LTOOLS_PANEL_HOST`）整体删除。`PanelLauncher` 只转发：
  `LinkWorker.RequestShow` / `RequestToggle` / `RequestStash`——宿主还没连上（主程序刚启动那几秒）
  就把这一下**记着，连上补发**（同一件事只留最后一次），不再在提权进程里开第二份面板。
- 宿主由 `App.OnStartup` **无条件**拉起（`ShareHelper.Start()`，窗口隐藏；它同时是共享助手），
  主程序退出时 `ShareHelper.Stop()` 一并停掉。
- **点击外部关闭改由宿主承担**：面板在宿主里是普通顶层窗口，`SuperPanelWindow.OnWindowDeactivated`
  关（菜单 / 面板对话框打开期间不关，排到 Background 再确认一次激活态）。
  以前这条由提权进程的鼠标钩子按面板矩形判定——钩子看不到宿主进程的窗口。
- 钩子那边的「长按右键唤起面板」手势要知道面板开着：宿主在面板状态变化时推 `panel.state`
  （`PanelStateEvent(Open)`，只在开 / 关真的变了时发，新客户端连上补一次），提权侧存
  `PanelHostBridge.RemotePanelOpen`，`MouseHook` 的手势分支据此让开。
- 「在暂存区编辑…」：提权侧建完草稿发 `panel.stash` 事件，宿主 `SuperPanel.ShowStash()` 切到暂存区页
  （以前是提权进程自己开面板）。
- 真机实测（宿主面板 + 真鼠标）：右键磁贴弹菜单时面板不关；点菜单里的「复制名称」面板也不关；
  点面板外面（另一个窗口）第一次点击就把面板关掉。
- 回归：`--selftest` 393 用例 / 18599 断言（面板状态往返 / 开关两态）；`LinkSmoke` 60；
  `PanelHostSmoke` 11；`ShareSmoke -- --explorer --helper` 103。

**2026-11-24 · P1b-12 点悬浮按钮也关面板（`panel.close`）**

- 面板搬进宿主后，「点在悬浮按钮上」不再算「点到面板外面」（悬浮窗是 `WS_EX_NOACTIVATE`，
  点它不会让宿主面板失去激活）——旧行为（关面板再跑动作）靠的是提权进程钩子按矩形判定。
- 补 `panel.close` 事件：`FloatWindow.RunAction` 先 `PanelLauncher.Close()` 再跑动作。
  **不排队**（宿主没连上就丢）：排到连上再关会关掉后面新开的那一扇面板。
- 真机实测：宿主面板开着 → 右键磁贴「悬浮此动作」→ 面板保持不变（标签翻成「取消悬浮」）→
  点悬浮按钮 → 动作开始跑（日志 `点击悬浮按钮` + `[run-…] 开始执行`）且**宿主那边面板窗口数变 0**。
- 回归：`--selftest` 393 用例 / 18599 断言；`LinkSmoke` 60；`PanelHostSmoke` 11。




**2026-11-24 · P2-M1-a 交互服务：`ui.input` / `ui.confirm` / `ui.select` 在宿主里弹**

- 新通道（**执行器 → 宿主**的请求，与面板那批方向相反）：
  `Core/Link/UiCommands.cs`（`ui.input` / `ui.choice` / `ui.cancel` + 请求 / 回复 DTO）、
  `Core/Link/UiBridge.cs`（执行器侧：能转就转，转不了就本地弹）、
  `Core/Link/UiHost.cs`（宿主侧：`Ui.Post` 建窗 + 等 `Closed` 回值）。
- `LinkWorker.RequestAsync`：执行器也能发请求；宿主 `LinkServer` 的 handler 改成
  「先问 `UiHost`，不是交互方法才回『宿主暂不支持该命令』」。
- 三个动作单元改成「先问宿主」：`ui.confirm` / `ui.select` 走 `ChoiceWindow`，
  `ui.input` 走 `InputDialog` 的**非模态**新入口 `ShowNonModal`（模态会把宿主 UI 线程卡在自己的
  消息循环里，`ui.cancel` / 停止热键就关不掉它——`Ok_Click` 里按 `_nonModal` 决定
  `DialogResult` 还是 `Close()`）。
- **踩到并修掉的坑**：链路层把 `timeoutMs <= 0` 当成 `Math.Max(1, …)`（1ms），
  「一直等」传 0 会立刻超时 → 执行器回落本地弹窗，而宿主那边的窗口已经开出来了，出现**两个对话框**。
  现在「一直等」给链路 12 小时上限，真正超时由宿主那侧算；任何失败 / 取消都先发 `ui.cancel` 收掉对端窗口。
- 真机实测：MCP `staging_run` 跑一份 `ui.confirm` + `ui.input` 的草稿 →
  确认框与输入框都出现在 **ShareHost（普通权限）**，提权进程一个窗口都没有；
  点「确定」后进入输入框，输入后「确定」流程正常走完（慢步骤告警照常记「等待用户输入」）。
- 回归：`--selftest` 393 用例 / 18620 断言（交互 DTO 往返 + 方法名）；`LinkSmoke` 62 条
  （新增「执行器 → 宿主」的 `ui.choice` 往返）；`PanelHostSmoke` 11；`ShareSmoke` 103。

**2026-11-24 · P2-M1-b 交互服务：`ui.textWindow` / `file.select` 也搬进宿主**

- 文本窗口：`ui.textWindow` 走 `UiTextWindowRequest` / `UiTextWindowReply`。宿主用
  `TextWindowService.Open` 开窗，「显示且不等」立刻回内容、「读取 / 等关闭」挂到 `Closed` 上再回值；
  `mode=close` 直接 `TextWindowService.Close(key)`。取消只关**这一扇**（不能按 key 空值关全部）。
- 文件 / 文件夹选择：`file.select` 走 `ui.selectFile`。系统对话框**只有一份实现** `Features/FilePicker.cs`
  （动作单元与 `UiHost` 都调它，`SelectFileUnit` 里那份重复代码删掉）。
- 两个单元都是「先问宿主、没连上再本地弹」，与 `ui.input` 同一条口径。
- **已知限制**：系统文件框是模态的、程序关不掉。停止热键在这一步只会让本次运行结束，
  框会留在宿主里直到用户按 Esc（与截图遮罩同一个限制，写进了 `FilePicker` 的注释）。
- 真机实测（MCP `staging_run`）：
  - `ui.textWindow` 显示 + 读取 → 两扇窗口都在 **ShareHost**（`LTools` 进程一个窗口都没有），
    改完点「确定」后 `found=true`、内容回填到变量，运行 `state=ok`；
  - `ui.textWindow` `mode=close` → 宿主里的窗口关掉（`EnumWindows` 里 `M1-b` 归零）；
  - `file.select`（文件夹）→ 系统框（类名 `#32770`）在 **ShareHost** 里且是前台，
    按 Esc 取消后 `found=false`、`path=""`，运行 `state=ok`（取消不是失败）。
- 回归：`--selftest` 393 用例 / 18636 断言（新增两组 DTO 往返）；`LinkSmoke` 66 条
  （新增文本窗口 / 选择文件两条反向请求）；`PanelHostSmoke` 11；`ShareSmoke` 103。

**2026-11-24 · P2-M1-c 交互服务：屏幕取点也在宿主里弹**

- `screen.pickPoint` 走 `ui.pickPoint`（`UiPickPointRequest{DialogId, Hint}` → `UiPickPointReply{Ok, X, Y}`，
  坐标是**虚拟桌面物理像素**，与截图 / 找图 / 鼠标注入同一套）。
- 宿主侧 `ScreenPicker.TryPickPoint` 原样复用：它自己 `TransientMinimize.MinimizeAll` 收起宿主窗口、
  `ToastService.SuppressForCapture` 藏卡片、抓冻结画面、结束恢复。
- **执行器那边的置顶窗口也得先收**：遮罩在宿主里拍冻结画面，执行器的自动化指示器与提示卡片会被一起拍进去。
  所以取点单元在发请求**之前**就 `HideAutomationForCapture()` + `SuppressForCapture()`，`finally` 里恢复——
  这样就不需要「跨进程抓屏抑制」那套东西（原计划的 `ui.suppress` 删掉）。
- **已知限制**：遮罩是 `ShowDialog`，程序关不掉。停止热键只结束本次运行，遮罩留到用户单击 / 右键 / Esc。
- 真机实测（MCP `staging_run`）：
  - 遮罩窗口（标题「屏幕取点」，1920×1080）在 **ShareHost**，`GetForegroundWindow()` 就是它，提示语渲染正常；
  - 点 (700,400) → `x=700, y=400`（物理像素，1:1）；
  - 按 Esc → 遮罩消失、`x=0` / `y=0`（取消不是失败，`state=ok`）。
- 回归：`--selftest` 393 用例（新增取点两组 DTO 往返）；`LinkSmoke` 68 条（新增取点反向请求）。

**2026-11-24 · P2-M2-a 写属主翻转：`configs/*.json` 的物理写者变成宿主**

- 做法：**不动任何调用点**，只翻转「最后落盘那一步」——
  - `ConfigBundle.WriteIfChanged` 写之前先问 `ConfigBundle.RemoteWriter`（`App.OnStartup` 挂上
    `DataBridge.TryWriteConfig`），执行器发 `data.write { Name, Json }`；
  - 宿主 `DataHost` 用 `ConfigBundle.TryWriteDomainFile` 落盘（临时文件 + 整体替换，
    名字必须命中 `ConfigBundle.DomainFiles` 的七个域文件，带路径 / 不在名单里直接拒）；
  - 宿主没起 / 出错 / 被拒 → 返回 false，`ConfigBundle` 照旧本地写（降级只影响「谁写」，不丢改动）；
  - `ConfigBundle.WriteJson` 是唯一落盘实现（本地写与转发写共用）。
- 为什么这么翻：`ConfigIO.ModifyDomain` / `DomainStores` 是所有配置写入的唯一收口，把落盘那一步转走
  就完成了「一个文件一个写者」，**不必给设置页每个控件造 RPC**（那会像 `panel.op` 一样越铺越大）。
- **翻转范围**：运行期写入（设置开关 / 托盘开关 / 面板结构保存 / 快捷键页 / 触发规则 / 窗口几何…）。
  启动期的 `HotkeySeeding` 播种、`ActionRemoval.RepairReferences` 修引用、`OrphanFiles` 清理仍在执行器里跑
  ——那时链路还没连上、宿主也不写，所以不冲突；搬到宿主是 M2-c。
- 宿主收到转发写入**只落盘、不重读自己的内存配置**：这些域的属主（设置页 / 快捷键 / 触发规则）此刻还在执行器，
  宿主那份只读副本不需要跟着变；等页面搬过去、宿主成为属主时再改成「谁改谁重读」（`data.changed`）。
- 真机实测（普通权限宿主 / 提权进程各一）：
  - 在执行器的设置窗口里点「通用」→ `UiStore.Modify(LastNavPage=general)` → 宿主日志
    `[Data] 已写入 ui.json（替主程序落盘）`，`configs/ui.json` 的 mtime 从 11-23 变成 11-24、
    内容正常（`LastNavPage = general`）——**文件确实是宿主写的**；
  - 执行器日志里没有任何「改成本进程写」的降级告警。
  - 降级（宿主被掐掉 → 本地写）没单独真机验：自动化环境里后续点击没送达（`GetCapture=0`、
    `WindowFromPoint` 命中的就是设置窗，但 WPF 不响应），这条路径只有代码上的
    `LinkWorker.IsConnected` 一处判断兜底。
- 回归：`--selftest` 393 用例 / 18665 断言（新增 `data.write` DTO 往返 + 白名单 8 条 + 真实落盘 3 条）；
  `LinkSmoke` 70（新增一条 `data.write` 反向请求）。

**2026-11-24 · P2-M3-1 速记搬进普通权限宿主（窗口 + `速记/` 一起翻）**

- 窗口：`QuickNote.Toggle()` 现在看进程身份——宿主（`ConfigStore.ReadOnly`）开本地窗口；
  提权进程只发 `note.toggle`（`LinkWorker.RequestNote()`，没连上先记着、连上补发，与面板同一套）。
  托盘菜单、`CapsLock+N`、面板磁贴（内置功能 `ltools.note`）全都走这一条，**不再有第二个家**。
- 域：`速记/` 的写者归宿主。`data.noteWrite` 动作单元在提权进程里跑，但写走 `note.write`
  （`NoteBridge` → 宿主 `NoteHost` → `NoteActions.Write`）。读（`data.noteRead`）仍在本地读——
  两边读的是同一批文件，读不需要跨进程。
- 启动维护（建目录 / `MigrateRootScatteredNotes` / `MigrateTxtToMd` / `CleanDraftAttachments` /
  `CollectAttachmentGarbage`）跟着窗口搬到宿主（`ShareHost.LoadReadOnlyConfig`）；
  提权进程 `App.OnStartup` 不再 `QuickNote.Initialize`。
- 失败语义：转出去了但宿主报错**直接抛错、不回退**（可能已经写了一半，再本地写会多出一条速记 / 重复图片）；
  只有「没连上」才走本地写（降级）。
- 真机实测（宿主 pid 5456 / 提权 7680）：
  - MCP `staging_run` 跑 `data.noteWrite` → 宿主日志 `[Note] 替主程序写入速记：…\速记\M3-1 冒烟\2026-11-24 M3-1 冒烟.md`，
    文件真的落在 `速记/M3-1 冒烟/`（65 字节）；
  - 托盘右键 →「速记 (CapsLock+N)」→ 窗口 `pid=5456`（**宿主**），列表渲染正常；
    再点一次 → 窗口关闭（`EnumWindows` 里「速记」归零），提权进程从头到尾没建过速记窗口。
- 回归：`--selftest` 393 用例 / 18682 断言（新增 `note.toggle` / `note.write` 方法名 + 写请求 / 结果 / 失败原因往返）；
  `LinkSmoke` 72（新增一条 `note.write` 反向请求）；`PanelHostSmoke` 11；`ShareSmoke` 103。

**2026-11-24 · P2-M3-2 辅助工具搬进宿主（顺带补上「点选器让钩子让路」的跨进程通道）**

- 窗口：`InfoHelperWindow.Toggle()` 按进程身份分流——宿主开本地窗口，提权进程只发 `info.toggle`
  （`LinkWorker.RequestInfoHelper()`，排队补发）。托盘「辅助工具…」还是同一条入口。
- **踩到的坑**：全屏点选器（窗口 / 控件 / 框选 / 取色）靠 `MouseHook.SetWindowPickerActive(true/false)`
  让「裸右键长按 = 面板」的手势让路。钩子在提权进程、点选器现在在宿主——宿主里调它等于调了个空函数，
  遮罩会先被钩子吞掉右键、再注入一次，用户要连点两次才退得出去（这个 bug 以前修过一次）。
  补一条状态通道：`Views/PickerHook.SetActive`（唯一入口）→ 宿主走 `InputBridge` 发
  `input.picker` 事件 → 执行器 `HandleHostEvent` 调 `MouseHook.SetWindowPickerActive`。
  断线重连时 `InputBridge.PushCurrent()` 补发当前状态。`PickerSession` / `CaptureSession` /
  `GlobalCanvasWindow` 三处调用点统一改成 `PickerHook.SetActive`（截图 Pro 还在提权进程时它直接走本地）。
- 真机实测（宿主 9764 / 提权 18612）：
  - 托盘右键 →「辅助工具…」→ 窗口 `'辅助工具' pid=9764`（**宿主**），提权进程 0 个窗口；
  - 窗口里点「窗口信息」→ 全屏遮罩 `'选择窗口' pid=9764`（宿主）且是前台；
  - 在遮罩上**按住右键 800ms**（足以触发「长按右键 = 面板」）→ 遮罩关闭、焦点回到辅助工具，
    **没有弹出面板**（宿主窗口数仍是 1）——说明钩子确实让路了，右键取消一次就生效。
- 回归：`--selftest` 393 用例 / 18687 断言（新增 `input.picker` 方法名 + 状态往返）；
  `LinkSmoke` 72；`PanelHostSmoke` 11；`ShareSmoke` 103。

**2026-11-24 · P2-M2-b 动作库写入翻给宿主（`actions/`）**

- 做法与 configs 同一个思路：**只翻「谁落盘」**。`ActionLibrary.Save` 是动作清单唯一的写入口
  （`AddAction` / `UpdateAction` / `RemoveAction` / `DuplicateAction` / AI 采纳 / 宏挂载 / 删除全都经它），
  所以把整份清单交给宿主 `action.saveAll`，由宿主 `ActionStore.SaveAll(anchor, snapshot)` 写到磁盘
  （一个动作一个目录、内容没变的目录不重写、清单里没有的目录搬 `trash/`、自动历史快照也一并走）。
- **宿主侧刻意不走 `ConfigIO.Modify(ConfigDomain.actions, …)`**：那会先 `AppConfig.LoadStrict` 把整个
  `actions/` 扫一遍解析（每次保存都白扫一次），而且采纳草稿时执行器刚建好 `actions/<新id>/files/`、
  `action.json` 还没写，扫描会刷一条「忽略没有 `action.json` 的目录」的假警告。
  `ActionStore.SaveAll` 就是 `SaveDomains` 里那一段的唯一实现，直接调用语义完全一样。
- 宿主写完把**只读**内存副本换成发来的这一份（面板显示动作名 / 暂存区预览靠它），但走 `Ui.Post`
  挪到 UI 线程做（链路线程直接 `Clear` + 重填会和正在读它的面板快照撞车）；**不整份 `Reload()`**——
  那会连面板库一起重读，把正在拖磁贴的用户打断。
- 不做「动作级 RPC」（新增 / 改名 / 删除 / 复制各一条）：那样每个调用点都得记得转一次，漏一个就是两个写者。
  整份清单转过去，调用点一行都不用改。超时 5 秒：宿主卡住时快速退回本地写，别把界面吊在一条链路上
  （比 `data.write` 的 2 秒宽一点，整份清单比单个域大）。
- 唯一仍由执行器写的：`actions/<id>/files/`（编辑器挑图时 `ActionStore.CopyFilesFrom` 复制素材）。
  与宿主写的 `action.json` / `history/` 是**不同文件**，不冲突；等编辑器搬进宿主（M3-4）再收口。
- 真机实测（宿主 25400 / 提权 24444）：
  - MCP `ltools_staging_promote`（`mode=new`）→ 宿主日志 `[Action] 已写入动作库（9 个动作，替主程序落盘）`，
    `actions/<新id>/action.json` 562 字节、名称正确；
  - 再从该动作 `copyFromActionId` 建草稿改说明、`mode=replace` 采纳 → 宿主照样写，并留下
    `actions/<id>/history/20261124-183817-672-d8778a58.json`（586 字节，快照确实由宿主产生）；
  - 删掉目录重启 → 宿主面板快照回到 8 个动作（启动时 `RepairReferences` 收拾了悬空引用）。
- 回归：`--selftest` 393 用例 / 18698 断言（新增 `action.saveAll` 方法名 + 清单往返 + 失败原因）；
  `LinkSmoke` 74（新增一条 `action.saveAll` 反向请求）。

**2026-11-24 · P2-M3-3a 截图覆盖层搬进宿主（框选 / 标注 / 工具条 / 结果窗 / 贴图 / 采集历史）**

- 入口只有一条缝：`CaptureSession.TryCapture` 按进程身份分流——**宿主里本地跑**（覆盖层的真正实现在
  `RunLocal`，一个字没改），**提权进程里转给宿主**（`CaptureBridge` → `capture.run` → `CaptureHost`）。
  宿主没连上 / 出错就地降级在本进程跑一遍，截图键不会按了没反应。
- **成品图不再跨进程传**：调用方要图时（「截图」取值器）传 `wantImage`，宿主把成品存进
  `cache/screenshots/` 并把路径带回来（`CaptureOutcome.File`）；复制 / 贴图 / 识别 / 记采集历史
  这些出口本来就在覆盖层交回之前做完，一张 png 都不用走链路（8 MB 的帧上限也不该拿来传 4K 截图）。
- `CaptureDirect`（截取当前显示器 / 前台窗口）同样分流，同样**不传图**（今天没有调用方要那张图）；
  `captures/` 的写入方因此只剩宿主：`CaptureCatalog.Maintain` 与 `RecordRecovery.Sweep` 从
  `App.OnStartup` 挪到宿主启动（`ShareHost.BootCaptureHistory`）。
- **踩到的坑**：`capture.direct` 一开始写成「宿主没连上就抛异常」，等于把「宿主挂了」变成功能不可用；
  改成返回 `false` → 本地兜底，与其它链路一致。
- 设置也要跟过去：`AppState.ApplySettings(AppConfig)` 成为唯一实现（`App.OnStartup` 与
  `ConfigStore.InitializeReadOnly` 都调它），否则宿主那份延时秒数 / 录屏默认开关 / 导出场景永远是默认值；
  宿主自己写完 `configs/settings.json` / `ui.json` 后再刷一次相关缓存（`DataHost.RefreshRuntime`：
  `ScreenshotBehavior` / `OverlayUiSettings` / `CaptureSoundSettings` / 工具条布局 / 标注样式），
  不然「设置里改了，截图还是老样子」。
- **顺手补掉 M3-2 留下的一个真 bug**：点选器 / 覆盖层关窗时要把「还按着的键」的抬起吞掉
  （`ExitInput`），而钩子只在提权进程——宿主里调 `MouseHook.SwallowPendingReleases` 等于没调，
  右键取消遮罩之后配对的那一下抬起会落到光标下面的程序上（资源管理器弹右键菜单）。补 `input.swallow`：
  **采样在关窗的那个进程做**（`GetAsyncKeyState` 当场读），只把结果送过去上膛
  （`MouseHook.SwallowReleases(bool,bool,bool)` / `KeyboardHook.ArmRelease(vk)`）——跨进程重新采样会读成「已经松开」。
- 录屏交给宿主之后，提权进程那个「自动化指示器」要等**宿主录完**才能恢复：`capture.recordDone` 事件 +
  `CaptureBridge.OnRecordingFinished`（回调在发请求前就登记，录屏秒失败也不会漏）。
- 真机实测（宿主 21672 / 提权 27188）：
  - 托盘 →「截图」→「截图…」→ 覆盖层 `'截图' 1920×1080` 在 **ShareHost**；拉框 + Enter →
    剪贴板里是图，`captures/` 多一条 `501×401 · 复制`（`assets/` `thumbnails/` `displays/` 三份都由宿主写）；
  - 再开一次覆盖层按 Esc → 窗口关掉、采集历史仍是 4 条（没多记）；
  - 动作里的「截图」取值（MCP 跑一份草稿，`wantImage` 路径）：宿主存
    `cache/screenshots/shot-20261124-190102.png`（701×551），执行器的 `${shot}` 拿到这个路径，
    提示卡片显示正确；
  - 辅助工具 →「窗口信息」→ 遮罩上右键取消 → 遮罩关闭、**下面没有冒出右键菜单**（`input.swallow` 生效；
    这条在 M3-2 时是坏的）。
- 回归：`--selftest` 393 用例 / **18723** 断言（新增 `capture.run` / `capture.direct` / `input.swallow` /
  `capture.recordDone` 的方法名与往返）；`LinkSmoke` 74；`PanelHostSmoke` 11；
  `tests/CaptureOverlaySmoke` 的 `captureEntry` / `releaseAction` / `tray` 三段全绿；
  `ShareSmoke` 99/103——**四条「真鼠标拖放」在本轮基线上同样失败**（桌面上压着 `Shell_SystemDialog / Windows 安全中心`，
  对照组窗口的 `DoDragDrop` 都收不到 Drop），与本次改动无关，按「未验证」记一笔。
- 过渡期的两个写者（下一步收口）：采集历史窗口（删除 / 重新编辑）与贴图窗口仍在提权进程，
  所以 `captures/` 的删除、`state/pins.json`、`TryReopen` / `TryEdit` 这几条还留在那边——
  贴图 / 拼图 / 切图 / 录屏条与采集历史窗口搬完（M3-3b）就没有了。

**2026-11-24 · P2-M3-3b-1 采集历史 / 贴图管理 / 图片编辑 / 切图 / 拼图搬进宿主**

- 入口收成一处：`Features/CapturePro/CaptureWindows` 的 `Open(target)`（`history` / `pinManager` /
  `imageEdit` / `split` / `collage` / `pinRestoreLast` / `pinToggleAll` / `pinRefresh`）——宿主里直接开，
  提权进程过链路（`capture.window` 事件，与 `info.toggle` 同一套），没连上才就地降级。
  托盘菜单、内置功能、设置页三处都改成调它，**别在调用点各写一次 `ConfigStore.ReadOnly`**。
- 顺手把「贴图设置改了就刷新已经贴着的图」也接上：设置页在执行器，贴图窗口在宿主，
  `CapturePinManager.RefreshRecognition()` 改走 `capture.window` 的 `pinRefresh` 分支
  （不接的话「打开自动识别」要等下次贴图才生效）。
- `pins/` 跟着贴图窗口归宿主：`PinStore.Maintain` 从 `App.OnStartup` 挪到
  `ShareHost.BootCaptureHistory`（与采集历史、录屏残片一起）。**贴图窗口本身在 M3-3a 就已经在宿主里产生**
  （覆盖层的「贴图」出口、`capturePin` 入口），这一步只是把它们的入口 / 维护 / 设置联动补齐。
- 真机实测（宿主 16112 / 提权 19332）：托盘 →「截图」二级菜单里
  **采集历史 / 贴图管理 / 图片编辑 / 切图 / 图像拼接** 五个窗口全部 `pid=ShareHost`（宿主日志
  `打开截图窗口（history|pinManager|imageEdit|split|collage）`）；采集历史里 ⋯ →「重新编辑截图…」
  在宿主里重开覆盖层（502×401 那条的选区 `702×552` 原样还原）、Esc 正常退出；
  `pics/` 里那条 0 字节的旧贴图素材只打了原有的一条 WIC 警告，不影响列表。
- 回归：`--selftest` 393 用例 / **18728** 断言（新增 `capture.window` 方法名 + 往返）；
  `LinkSmoke` 74；`PanelHostSmoke` 11；`CaptureOverlaySmoke -- tray` 通过。
- 还没搬（3b-2）：录屏条 / 录屏选项窗（现在由覆盖层的「录屏」出口在宿主里创建，但托盘与设置页的
  入口仍在执行器）、全屏画布（`GlobalCanvasSession`，它靠 `HotkeyRegistry.TryFireOnly` 切点击穿透，
  要另开一条「只派发这个热键」的通道）。

**2026-11-24 · P2-M3-3b-2 全屏画布搬进宿主 / 贴图与录屏条的快捷键过链路**

- 全屏画布与其它截图窗口同一条入口：`CaptureWindows.GlobalCanvas`（内置功能 `ltools.globalCanvas`
  只调 `CaptureWindows.Open`），宿主里 `OpenLocal` → `GlobalCanvasSession.Activate()`；
  「再按一次 = 切点击穿透」在执行器只转意图（`ToggleClickThrough` → `capture.window globalCanvas`）。
- **快捷键挂起不再看窗口在不在**：`GlobalCanvasSession.IsOpen` / `_open` 已删，状态搬进
  `Core/InputGuard`（`Suspended` / `OnlyRef` / `SetSuspended` / `Apply` / `PushCurrent` / `Reset`，
  事件 `input.suspend`）。`KeyboardHook` 只看 `InputGuard.Suspended && OnlyRef`（命中就吞掉并
  `GlobalCanvasSession.ToggleClickThrough()`），提权进程里根本没有画布窗口也没有本地画布状态。
- **贴图快捷键（3a 留下的回归）**：贴图窗口搬到宿主后 `PinShortcutRoute` 在提权进程里一张贴图都没有，
  B / L / Esc 这些键全没反应。补 `input.pin`：宿主在登记 / 点过 / 指针离开 / 关窗时把
  `(hwnd, engage|release|closed)` 推过去，执行器只留「影子 target」；`Handle` 命中后经
  `capture.window pinShortcut`（`arg` = 动作名、`hwnd` = 哪张贴图）转回宿主，`FireLocal` 调真正的按钮。
- **录屏条快捷键同理**（3a 之后同样失效）：宿主 `RecordShortcutRoute.Publish` 把快照
  （`Hot` + Recording / Paused / Busy / …）**只在变了时**推过去（`record.keys`；这方法是每帧调的，
  全推会白占链路），执行器按它匹配，命中转 `capture.window recordShortcut`。
- `capture.window` 的负载扩成 `(Target, Arg, Hwnd)`（`CaptureWindowEvent`），
  `CaptureWindowBridge.TryShow` 更名 `Show`。
- **断开清理**：`InputGuard.Reset()` + `PinShortcutRoute.DropRemote()` + `RecordShortcutRoute.DropRemote()`
  都只摘「宿主推来的」那一份——不清的话宿主一崩，全局快捷键就永远停在挂起、贴图句柄还可能被别的窗口复用。
- 验证（跨进程这段只能真机点）：
  - 贴图：托盘 → 采集历史 → ⋯ →「贴到屏幕」→ 点一下贴图 → 按 `B`：宿主日志 `打开截图窗口（pinShortcut）`
    + 执行 `ShowBorder`，贴图左上角像素变化 69/1600（边框真的画上了）。
  - **注入的键会被钩子的「忽略注入事件」挡住**，所以验证时临时只把注入事件放行到 `PinShortcutRoute`
    一处（验完立刻还原）；真机手按走的是同一条 `Handle`。
  - 画布 / 录屏条的键同样没法用注入事件验（同一个过滤）。跨进程这一段与贴图共用
    `capture.window` + 通知通道，本地行为由 `CaptureOverlaySmoke` 的 `globalcanvas` / `keys` 段守着。
- 回归：`--selftest` 393 用例 / **18759** 断言（新增 `input.suspend` / `input.pin` / `record.keys` 的
  方法名与状态往返、影子和宿主断开清理、`ApplyState` 后 `Handle` 真的按宿主状态匹配）；
  `LinkSmoke` 74；`PanelHostSmoke` 11；`CaptureOverlaySmoke` 的 `tray` / `keymap` / `keys` /
  `globalcanvas` 四段；`ShareSmoke` 92/0。
- 3b-2 剩下的只有**入口**：录屏条的托盘 / 设置页入口跟设置页一起搬（M3-5）；录屏条本体与选项窗
  3a 起就在宿主里。

**2026-11-25 · P2-M3-4a 全局子动作库落盘翻给宿主**

- 只翻「谁落盘」：`SubActionStore.Save` / `Delete` 先问 `Core/Link/SubActionBridge`
  （`subaction.save` / `subaction.delete`），宿主 `SubActionHost` 用同一份 `SubActionStore`
  写 / 删 `subactions/<id>.json`；宿主没起 / 出错 / 3 秒超时 → 本地写，降级只影响「谁写」，不丢改动。
- **按份转，不转整库**：子动作是一个文件一个定义、也没有「清单」，整库快照会让两边的索引缓存互相覆盖。
  读仍然各读各的（`sub.run` 在执行器里跑，必须在那边读得到），写完各自 `Invalidate()`。
- 校验 / 引用扫描 / CAS（`Revision` = 文件 sha256）全留在调用侧；`createNew` 撞车这类「本来就会失败」的
  情况由宿主拒绝后本地再写一次并抛同样的错，行为与单进程一致。
- 真机（宿主 / 提权）：MCP `ltools_subactions_write` → 宿主日志
  `[SubAction] 已写入子动作（0smoke-sub-1，替主程序落盘）`，`subactions/0smoke-sub-1.json` 137 字节；
  `ltools_subactions_delete` → `已删除子动作`，文件消失。
- 回归：`--selftest --filter link` 304 断言（方法名 / 一份定义与变量的往返 / 失败原因）；
  `LinkSmoke` 80（新增写入与删除各一发一收 + 整份定义原样）。

> 4a 用的是与 `configs/` / `actions/` 同一个「只翻落盘那一步」的手法；`staging/` `resources/`
> 仍跟编辑器 / 暂存区走（4b）。

**2026-11-25 · P2-M3-5a 配置整份重读的通知（`config.reload`）**

- 动手前先纠正一处顺序：**编辑器没法单独搬**。`panel.op` 在提权进程里执行 `PanelActionOps`，
  它才是「新建 / 编辑 / 复制动作」打开编辑器的调用方；设置页也在提权进程里开同一个编辑器。
  把窗口搬过去就得连 `PanelActionOps`、动作内容写权、`staging/` 草稿与设置入口一起翻——那正是第 5 步。
  所以 4b / 4c 并进第 5 步做，先补它依赖的两块：**整份重读的通知**（5a）与**跨进程试运行**（5b）。
- `config.reload`（执行器 → 宿主，无负载）：`ConfigStore.Reload()` 是「磁盘整份换过」的唯一入口
  （导入备份、设置页「重新加载」都走它），在那里广播一次；宿主收到就走既有的
  `ReloadAndRefreshAsync`（`InitializeReadOnly` → 拉面板快照 → 刷已开着的速记 / 贴图 / 面板）。
- `InitializeReadOnly` 补了 `SubActionStore.Invalidate()`：导入会整份换 `subactions/`，
  将来编辑器在宿主里读时不能拿旧索引。
- **顺手修掉的真问题**：导入备份后宿主的只读副本一直是旧的——面板还显示导入前的磁贴，
  点下去是「动作不存在」，用户只能重启。现在导入完宿主立刻跟上。
- 真机：托盘 → 设置 → 左下「重新加载」→ 宿主日志 `执行器重读了配置，宿主跟着刷新` +
  `面板快照：8 个动作…`（重新拉了一份）。
- 回归：`--selftest --filter link` 305 断言（方法名）；`LinkSmoke` 81（新增一条
  「配置重读通知送到了宿主」——通知走 `onEvent`，别塞进请求处理器）。

**2026-11-25 · P2-M3-5b 跨进程试运行（`run.program` / `run.stop` / 轨迹事件）**

- 编辑器「▶ 测试」的唯一入口变成 `Features/ProgramRunner`：**永远由提权进程跑**
  （要注入键鼠、与真实运行共用互斥、停止热键也只在那侧）。执行器里直接
  `AutomationRunner.TryRunProgram`，宿主转给执行器再摊回本地回调——回调签名与本地那套完全一致，
  编辑器只换调用名（`AutomationRunner.*` → `ProgramRunner.*`，共三处）。
- 协议：请求 `run.program`（`Token` / 程序本体 / 起始路径 / 资源根 / 干跑 / 来源）/ `run.stop`；
  事件 `run.trace`（**攒 120 ms、最多 200 条**推一批）、`run.slow`、`run.done`。
  全部带 `Token`：点得快的时候上一轮迟到的轨迹要能丢掉。
- **`LinkJson` 挂上了 `ValueJsonConverter`**：值模型只有这个转换器能把内容带全（否则过链路只剩
  `kind`），试运行要传变量默认值与轨迹里的参数 / 输出。加别的「带 `Value` 的负载」也靠它。
- 执行器一次只接一个运行（`AutomationRunner` 本身单实例）；宿主连不上时编辑器直接显示原因
  （不是含糊的「已有动作正在运行」）。
- 回归：`--selftest --filter link` 331 断言（五个方法名、程序本体与三种变量默认值的往返、
  轨迹批次的值往返、结果里的失败位置 / 步数 / 变量）；`LinkSmoke` 86（新增「宿主发程序 →
  执行器跑 → 轨迹与结果按事件回来」，值原样）。

**2026-11-25 · P2-M3-5c-0 进程身份与写权分开（`Core/AppRole`）**

- 5c 的第一步是拆掉一个一直靠巧合的约定：到处用 `ConfigStore.ReadOnly` 兼职「我是宿主」。
  写权正在往宿主翻（5c-1 起宿主是写者、执行器只剩读缓存），`ReadOnly` 的含义会整个反过来，
  而「窗口 / 交互归谁」不变——不拆的话那次翻转会把所有身份判断一起弄反。
- 新增 `Core/AppRole`：`IsUiHost`（只有 `ShareHost.App.OnStartup` 调 `MarkUiHost()`，默认 false）
  回答身份；`ConfigStore.ReadOnly` 只留「能不能写配置」。22 处里 15 处身份判断换成
  `AppRole.IsUiHost`（截图 / 速记 / 辅助工具 / 点选 / 面板预览 / 挂起 / 试运行 / 贴图与录屏条状态…），
  剩下 7 处（`ActionBridge` / `DataBridge` / `NoteBridge` / `SubActionBridge` / `ConfigReloadBridge` /
  `ConfigStore.Save`）本来就是写权，等 5c-1 一起翻。
- `PanelHostSmoke` 改成**显式**换身份（`MarkUiHost`）：它先按执行器建数据根，再切到宿主验
  「一个字节都不写」——以前是靠 `InitializeReadOnly` 顺带把身份也定了。
- 真机：启动 → 托盘「速记」→ 窗口在 `ShareHost`（身份判断生效）、面板快照正常。
- 回归：`--selftest` 393 用例 / 18803 断言；`LinkSmoke` 86；`PanelHostSmoke` 11；
  `CaptureOverlaySmoke` 的 `tray` / `keys` 段。

**2026-11-25 · P2-M3-5c-1 动作级写入（`action.add` / `action.update` / `action.delete` / `action.duplicate`）**

- 搬编辑器之前先回答「谁写内容」。选择是**内容属主仍是执行器、宿主发四个原子命令**：
  动作本体的读写次序、引用回收、历史快照都在执行器里，宿主里的面板 / 编辑器只表达意图。
  不为每个控件造 RPC（那是 `panel.op` 的过渡做法），也不让两边各写一份内存（设置页、AI 采纳、
  宏挂载都还在执行器里改同一份清单）。
- 四条命令**共用一个形状** `ActionEditRequest(ActionDto? Dto, string? Id)` / `ActionEditReply(Ok, Action, Error)`：
  `action.add` 新建（Id 空由执行器补）、`action.update` 覆盖同 Id（先确认真的存在，别静默变成新建）、
  `action.delete` 走 `ConfigStore.RemoveAction`（连带快捷键 / 面板 / 触发 / 悬浮引用与资源回收）、
  `action.duplicate` 走 `ConfigStore.DuplicateAction`（新 Id + 「副本」命名 + 素材一起走）。
  原先 `action.add` 只收名称 / 图标、`action.delete` / `action.duplicate` 共用 `ActionIdRequest`，
  已整体换成这一个形状（旧的两个 record 已删，不留别名）。
- 执行器侧 `ActionEditHost`（`IsWrite` 一张名单，同步 `Handle` 与链路 `TryHandleAsync` 共用）：
  改内存 → `ConfigStore.Save()`（落盘那一步照旧 `action.saveAll` 转给宿主）→ 返回保存后的动作。
  写失败（超时 / 没连上 / 执行器拒绝）整条命令报错，宿主把原因直接显示出来，不假装成功。
  两条线程注意：`ActionRegistry` 是并发字典、`NotifyChanged` 的契约允许后台线程；宿主的
  `ActionHost` 落盘是同步文件写 + `Ui.Post` 刷内存副本，所以嵌套往返不会要宿主 UI 线程（不会死锁）。
- 宿主侧 `ActionEditBridge`（5 秒超时）：`Add` / `Update` / `Delete` / `Duplicate`。
- 回归：`--selftest --filter link` 347 断言（四条命令进「方法名互不相同」、`ActionEditHost.IsWrite`
  覆盖面、整份动作与答复 / 失败原因往返）；`LinkSmoke` 90（转发阶段真管道走一遍
  「宿主发 `action.add` → 执行器收到整份动作并回带 Id 的 DTO」「`action.update` 被拒时原因到手」；
  面板阶段的新建 / 复制 / 删除改用统一形状）；`PanelHostSmoke` 11。
  （真机跑出来的那个「嵌套往返」死锁在 5c-2 修掉了，见下一条。）

**2026-11-25 · P2-M3-5c-2 动作编辑器在宿主里（`ActionWrites` + 宏录制过链路）**

- 「搬编辑器」实际搬的是它的**调用方** `PanelActionOps`：新增 / 编辑 / 删除 / 复制 / 导出 /
  另存为子动作 / 从文件建动作，全部改成宿主里就地做（编辑器、确认框、动作池、导出都是本地窗口）。
  `PanelActionOps` 里那几条 `PanelHostBridge.TryActionOp(PanelOps.X)` 守卫整体删掉；
  `PanelOps` 只剩「提权进程才做得了的」十项（悬浮 / 整组悬浮 / 试放宏 / 录制宏 / 暂存区五项）。
- **UI 不再直接调 `ConfigStore.AddAction` / `UpdateAction` / `RemoveAction` / `DuplicateAction`**：
  统一走新门面 `Features/ActionWrites`（宿主 → `ActionEditBridge`，执行器 → 本地）。
  宿主的答复会把动作镜像进本地 `ActionRegistry`（执行器可能补了 Id / 默认图标）；
  真正涉及引用清理的**删除**由执行器推 `actions.changed`，宿主整份重读——新建 / 覆盖 / 复制
  不推，否则会和宿主紧接着的面板推送抢同一份 `panel.json`。
- 编辑器宏横幅的「重新录制」要钩子，新增 `macro.record` / `macro.stop` + `macro.stopped` / `macro.saved`
  事件（带 token，串台的通知直接丢）；`Features/MacroRecording` 是编辑器唯一入口，执行器分支直接用
  `MacroRecorder`。「键鼠宏设置」按钮改成 `open.window` 的 `LinkTargets.Macros`（设置窗还在执行器）。
- **两个必须守住的线程规则（真机踩出来的）**：
  1. **链路处理器不在读循环上跑**（`LinkChannel.Dispatch` 改成 `Task.Run(RespondAsync)`）：
     处理器常要再向对端发请求（`panel.replace` 之后落盘又转给宿主写 `panel.json`、
     `action.update` 之后 `action.saveAll`、删除时的 `hotkeys/panel/triggers` 三个域）。
     在读循环上同步等答复，等于一边等对端、一边把「收答复」的循环占住，实测 5 秒 / 2 秒超时后降级本地写。
  2. **要回 UI 线程就用 `Ui.Enqueue`**（没有 dispatcher 就地执行，绝不丢）：
     `ActionEditHost` 只把活排到 UI 线程并返回一个等它做完的 `Task`，不在读循环上 `Ui.Run`。
- 顺手修：`PanelActionOps.PasteEntry` 的「源已被删」以前只弹「剪贴板已清空」却没真清，现在真的清
  （副本分支只在源确实没了时清，链路超时留着还能再粘）。
- 真机验证（普通权限宿主里的窗口）：面板「编辑」→ 改名 → 确定 → `actions/<id>/action.json` 与
  `history/` 都由宿主写入、面板磁贴跟着改名；「新建动作」「复制为副本 + 粘贴」「删除动作」各自走通
  （删除把目录移进 `trash/`、`panel.json` 摘掉槽位、宿主收到 `actions.changed` 后面板刷新），
  执行器日志无 `action.saveAll` / `data.write` 超时。
- 回归：`--selftest` 393 用例 / 18829 断言（`link` 组 357：宏录制四条方法名与负载往返）；
  `LinkSmoke` 93（新增「宿主发 `macro.record` → 执行器回开始了 → 结束 / 保存按 token 回来」）；
  `PanelHostSmoke` 11；`PanelClipboardSmoke` 27（含「源被删后剪贴板清空」）。

## 15. P2 方案：把剩下的界面也搬进普通权限宿主

> 这一节是**方案**，实施记录继续写在 §14（`P2-x` 条目）。
> 现状：P0 / P1 已完成——宿主随主程序常驻、**面板（动作页 + 暂存区 + 右键菜单 + 悬浮状态 + 宏试放 / 录制转交）
> 全部在宿主里跑**，`panel.snapshot` / `panel.op` / `panel.replace` / `run.action` / `open.window` / `panel.state` 就绪。
> 现在 62 个窗口的 XAML 里只有 `SuperPanel` 在宿主里，其余 61 个仍在提权进程。

### 15.1 三条依赖决定顺序（别按窗口大小排）

1. **交互类动作单元先要通道**。动作在提权进程里跑，但界面归宿主：`ui.input` / `ui.select` / `ui.confirm` /
   `ui.textWindow` / `file.select` / `screen.pickPoint` / `screen.capture`（交互框选）都在执行线程里建窗。
   不先接通道，窗口搬完动作照样在提权进程里弹框。
2. **写属主先翻，否则设置页要造几十条命令**。§7 已经定了 `configs/` `actions/` `subactions/` `staging/`
   `速记/` `resources/` 的属主是 **UI（宿主）**；不翻的话，设置 / 编辑器 / 暂存区每改一个字段都得新增一条 RPC，
   跟 `panel.op` 一样越铺越大（那是过渡期的临时手段，不该复制）。
3. **钩子类 UI 天然留在执行器**。快捷键录入、宏录制要钩子，只能「宿主弹窗 + 执行器录」。

### 15.2 M1 · 交互服务（`ui.*` 跨进程）

- 新方法（**执行器 → 宿主**的请求，宿主用 `Ui.Post` 弹窗 + `TaskCompletionSource` 回值）：
  `ui.input` / `ui.select` / `ui.confirm` / `ui.textWindow`(show/read/close) / `ui.selectFile` /
  `ui.pickPoint` / `ui.cancel`。
- 方向：执行器发请求（它在跑动作），宿主 `LinkServer` 的 handler 应答；`ui.cancel` 让等待中的窗口关掉
  （停止热键 → 引擎抛 `ActionAbortedException`，与今天本地那套语义一致）。
- 通道能力已具备：`LinkClient.RequestAsync` 与 `LinkServer` 的 handler 都支持反向请求（宿主 → 执行器现在就在用）。
- 资源归属随之定：交互截图写 `cache/screenshots/`（同名规则不变）、`ActionParamMemory` 留在宿主。
- 分四小步做（每步都能单独真机验）：
  - **M1-a（已完成）** `ui.input` / `ui.choice`（`ui.confirm` + `ui.select`）/ `ui.cancel`
  - **M1-b（已完成）** `ui.textWindow` / `ui.selectFile`（`file.select`，系统框唯一实现 `Features/FilePicker`）
  - **M1-c（已完成）** `ui.pickPoint`（`screen.pickPoint`）。执行器那边在发请求前先收起自己的
    指示器与提示卡片（`HideAutomationForCapture` + `SuppressForCapture`），`finally` 恢复
  - **M1-d（剩下的都并到 M3 / M4）**：`ui.toast` 与 `RecordingIndicatorWindow` 跟着**截图 Pro（M3-5）**
    一起搬，或者留到 M4 收尾一起搬。它们只有「抓屏入口搬过去」之后才需要跨进程收起，
    而每个抓屏入口的执行器那侧本来就会先收自己的卡片（同 M1-c），所以**不需要 `ui.suppress`**。
    交互式框选（`screen.capture` 的框选档，走 `CaptureSession` 整套覆盖层）也随 M3-5 一起搬。
- **已知限制**：系统文件框与取点 / 框选遮罩是模态的、程序关不掉。停止热键只结束本次运行，
  框留在宿主里等用户按 Esc——写进 `FilePicker`、`ScreenPicker` 与取点单元各自的注释，别再试图用定时器硬关。
- 验收：`ui.*` 与取点 / 文件框逐条真机；停止热键 500ms 内关掉窗口（模态框除外，见上）；
  `--selftest` 的 `contract` / `unit` 组仍绿。

### 15.3 M2 · 写属主翻转（按域翻，不一次性）

| 域 | 翻给谁 | 备注 |
|---|---|---|
| `configs/*.json`（七个域文件，含 `panel.json` / `macros.json` 元数据） | 宿主 | **已完成（M2-a）**：只翻落盘那一步——`ConfigBundle.RemoteWriter` → `data.write` → `DataHost` 写；名字走 `DomainFiles` 白名单 |
| `速记/` | 宿主 | **已完成（M3-1，跟着速记窗口一起翻）**：`data.noteWrite` 走 `note.write`；读仍在本地 |
| `actions/` `subactions/` `staging/` `resources/` | 宿主 | **`actions/` 已完成（M2-b）**：`ActionLibrary.Save` 把整份清单交给宿主 `action.saveAll` → `ActionStore.SaveAll` 写目录（唯一仍由执行器写的只有 `actions/<id>/files/` 的素材复制）。**`subactions/` 已完成（M3-4a）**：`SubActionStore.Save` / `Delete` 转给宿主 `subaction.save` / `subaction.delete`（按份转，读仍各读各的）。`staging/` `resources/` 仍**跟着窗口走**：编辑器 / 暂存区搬进宿主时（M3-4b）一起翻。**不单独做「目录文件系统 RPC」**：硬链接、搬 trash、孤儿回收转过去只会把「一个写者」做成空话 |
| `captures/` + `pins/` + `cache/screenshots/` | 宿主 | **跟着截图流程（M3-3a / 3b-1 / 3b-2）**：覆盖层 / 结果窗 / 贴图 / 采集历史 / 全屏画布都在宿主里跑，`capture.run` / `capture.direct` 只是入口；`Maintain` / `RecordRecovery.Sweep` / `PinStore.Maintain` 也跟宿主启动。钩子要读的状态（贴图 / 录屏条 / 画布挂起）全部过链路 |
| 启动期的播种 / 修引用 / 孤儿清理（`HotkeySeeding` / `RepairReferences` / `OrphanFiles`） | 宿主 | M2-c：跟着「宿主是写者」一起搬，执行器启动时改成只读 |
| `macros/<id>.json`（录制事件本体） | 执行器 | 录制只能在那儿；录完发 `macro.recorded` 事件，由宿主挂动作 + 放面板 |
| `state/run-history.jsonl` `trigger-history` `values` `auto-trigger-last-start` | 执行器 | 它才是产生者；宿主只读展示 |
| `logs/` | 分家 | `ltools-*`（宿主）/ `ltools-worker-*`（执行器），`LogFiles` 口径一起改 |

- **一个域一个写者**：单文件族（`configs/`）用「只翻落盘那一步」的方式提前翻；目录族（`actions/` 等）
  **跟着它的界面一起搬家**——界面搬过去时写入口自然就在宿主了，不必再造一套文件系统 RPC。
- **翻转的是「谁落盘」，不是「谁决定内容」**：`configs/` 的调用点照旧走 `ConfigStore` / `*Store.Modify` / `ConfigIO`，
  落盘时转给宿主。这样设置 / 编辑器搬进宿主时不必为每个控件造一条 RPC（`panel.op` 那种铺法是过渡期手段，不要复制）。
- 宿主成为某个域的属主后，才做「谁改谁重读」：写者写完发 `data.changed`，读者 `Reload()`。
  此刻宿主还不用重读 `configs`（属主仍是执行器）；`速记/` 也不需要（只有宿主在写）。

### 15.4 M3 · 窗口搬迁顺序（每个都是一次「搬 + 事件收尾 + 真机点一遍」）

| # | 窗口 | 除了搬还要做的 |
|---|---|---|
| 1 | 速记 `QuickNote` | **已完成（M3-1）**：`速记/` 写权翻给宿主、`note.toggle` / `note.write` 就绪、托盘 / `CapsLock+N` / 面板磁贴都只转意图 |
| 2 | 辅助工具 `InfoHelperWindow` | **已完成（M3-2）**：窗口在宿主（`info.toggle`）；取图 / 取色 / 选区直接用宿主里的 `ScreenPicker`；补了 `input.picker` 让鼠标钩子给点选器让路 |
| 3 | 截图 Pro（覆盖层 / 结果窗 / 编辑 / 拼图 / 切图 / 贴图 + 贴图管理 / 采集历史 + 录屏条） | **3a 已完成（2026-11-24）**：覆盖层 / 结果窗 / 贴图 / 采集历史随 `capture.run` → 宿主（`captures/` 与 `cache/screenshots/` 归宿主）。**3b-1 已完成**：采集历史 / 贴图管理 / 图片编辑 / 切图 / 拼图与贴图命令统一走 `CaptureWindows.Open`（`capture.window`），`pins/` 归宿主。**3b-2 已完成（2026-11-24）**：全屏画布进同一条入口、快捷键挂起走 `input.suspend`（`InputGuard`）、贴图与录屏条的快捷键走 `input.pin` / `record.keys` + `capture.window`。只剩录屏的托盘 / 设置页入口跟着设置页走 |
| 4 | 动作编辑器 / 子动作库 / 宏页 | `subactions/` **已完成（M3-4a）**：`Save` / `Delete` 转给宿主。**编辑器与宏页并进第 5 步**：打开编辑器的调用方（`PanelActionOps`）与设置页都在提权进程里，单独搬窗口就得连动作内容写权、`staging/`、设置入口一起翻——那正是第 5 步的范围（见 §14 `P2-M3-5a` 的顺序说明）。 |
| 5 | 设置 `ConfigHelper`（含 9 个截图 Pro 子页）+ 动作编辑器 + 子动作库 + 宏页 | **最后搬**：它是所有域的总控台（动作 / 子动作 / 快捷键 / 宏 / 触发 / 采集策略 / MCP / 备份），要等前四步的域都归宿主。**5a 已完成**：`config.reload`（导入 / 重新加载后宿主跟着重读）。**5b 已完成**：`run.program` / `run.stop` + 轨迹事件流（编辑器试运行过链路，`ProgramRunner` 是唯一入口）。**5c-0 已完成**：身份与写权分开（`AppRole.IsUiHost`）。**5c-1 已完成**：动作级写入四条命令（`action.add` / `action.update` / `action.delete` / `action.duplicate`，内容属主仍在执行器）。**5c-2 已完成**：动作编辑器 / 面板动作操作在宿主（`ActionWrites` 是唯一 UI 入口，宏录制过链路），链路处理器改到线程池上跑。**5c-3**：设置窗口（含子动作库 / 宏页）搬进宿主。**5d**：MCP 写、`data.changed` 收尾 |
| 6 | 共享窗口 | 把 `ShareHost` 并进同一个 exe 的「普通权限模式」，删 `Native/ShellToken` 那条第二启动路径 |

> **顺序更正（2026-11-24，看过代码之后）**：原方案把「采集历史 / 贴图管理」排在截图 Pro 前面，实际排不了——
> 那个窗口不是只读查看器：卡片上的「重新编辑 / 贴到屏幕 / 裁剪 / 拼接 / 切图 / 录制裁剪」全是截图 Pro 的功能，
> 搬早了就得把五六个功能再转回执行器（等于把 `panel.op` 那种铺法复制一遍）。`captures/` 的索引
> （`catalog.json`）也只有一份内存副本，创建在截图流水线、删除在历史窗口——**必须一起搬**才有唯一写者。
> 同理「设置」是最后一个：它同时编辑动作 / 子动作 / 快捷键 / 宏 / 触发 / 采集策略，前面任何一步没翻完，
> 它的某个页面就还得转回执行器。

> 步骤 3（截图 Pro）是唯一「搬过去比现在更麻烦」的一块：覆盖层、贴图、录屏条都是「压在所有窗口之上」的窗口，
> 搬过去之后**提权进程的窗口也要能被它盖住 / 被它临时收起**（现在是同进程内互相协调）。这一块单独估时、
> 分几小步做（覆盖层 → 结果窗 / 编辑 → 贴图 / 拼图 / 切图 → 录屏条 → 采集历史 / 贴图管理）。

### 15.5 M4 · 收尾

- 执行器的启动路径不再建任何窗口；`Ui.*` 里「要窗口才能做的事」逐条删掉或改成 `ui.*` 请求。
- `--worker` 模式（一个 exe 两种权限）：普通权限双击 = 界面；任务计划 / UAC 拉起 = 执行器。
- 发布包只剩一个 exe（`ShareHost.exe` 退役），`tools` / CI 的打包步骤同步。

### 15.6 每一步统一的验收口径

`dotnet build -p:NoWin32Manifest=true --no-incremental` 0 警 0 错；`--selftest` 全绿；
`tests/LinkSmoke` / `PanelHostSmoke` / `ShareSmoke` 全绿；本步涉及的窗口真机点一遍；
本文件补一条 `P2-x` 实施记录。

### 15.7 这一轮明确不做

- 不改动作引擎的语义（失败策略 / 重试 / 停止热键规则一律不动）。
- 不动 MCP / UIA 的能力边界（UIA 读提权窗口仍只有执行器能做，这块保持现状）。
- 不为「设置页每一个控件」新增 RPC——那是 M2 要避免的路。
