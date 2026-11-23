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


