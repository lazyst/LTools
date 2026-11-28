# 动作运行期互操作与并发（最终形态 · 已实施）

> **状态：已实施**（2026-11-28 B1–B4 一轮落地，记录见 §17）。本文是这套机制的唯一规格：
> 改运行内核 / 钩子让路 / 输入闸 / 可观测之前先读它，改完把决定补进 §16、把验收补进 §17。
> 术语沿用 `CONTEXT.md`（「运行」「停止运行热键」「运行指示器」「内置功能」「试运行」…），
> 新词只在 §2.2 提出、**等确认后才进 `CONTEXT.md`**；实现约束已进 `AGENTS.md`。
>
> 输入：2026-11-28 的讨论——「执行动作时 LTools 能继续用」+「也要能同时跑多个动作」+
> 「同一动作也要能并发」+「上限尽可能多，最终形态现在定好，不留过渡方案」。
> 参考实现：本机 Quicker 2.3.36（`C:\Program Files\Quicker\Quicker.dll`，SHA-256
> `6d939f4f…2cf4`）的托管元数据，用 REA 读的类型 / 方法 / IL / 用户字符串，见 §1.2。

---

## 0. 最终形态

一次「运行」从「进程级独占的一段流程」变成**可并存的会话**。最终形态由六块组成，
**一次设计到位**，落地只是接线顺序（§12），不换结构、不留临时 API：

| # | 模块 | 一句话 |
|---|---|---|
| 1 | **运行注册表** `RunningActions` | 运行按 `runId` 登记；`IsRunning` = 有任何一个在跑 |
| 2 | **准入 + 队列** `RunAdmission` / `RunQueue` | 能开就开（上限 32），开不了排队，排不下才拒 |
| 3 | **钩子分层** `RunHookPolicy` | 停止 > 等待按键 > 显式模式 > 热键 > CapsLock > 面板；运行中不再整段让路 |
| 4 | **输入闸** `InputGate` | 注入按原语串行（一次按键 / 点击 / 拖动 / 剪贴板），试运行整段独占 |
| 5 | **按运行隔离** | 按住键、日志 runId、截图清空、失败报告、toast、指示器、试运行 token 各归各 |
| 6 | **可观测与管理** | 指示器栈 + 「运行中的动作」窗口 + 托盘计数，逐项停 / 全部停 |

默认开箱即最终形态：

```jsonc
"ActionRun": {
  "Mode": "concurrent",   // 运行中照常使用 + 并发（默认）
  "MaxConcurrent": 32,    // 1–32，默认 = 上限（等于「不人为限制」）
  "MaxQueued": 8          // 0–32；32 个都在跑时才用得上
}
```

### 0.1 四档（同一套内核，只是准入参数不同）

| 档 | `Mode` | 运行中的 LTools | 再触发一个动作 | 停止热键 |
|---|---|---|---|---|
| 独占（回退档） | `exclusive` | 全部屏蔽（今天的行为） | 拒绝 | 停当前 |
| 互操作 | `interop` | 照常可用 | 拒绝 + 提示 | 停当前 |
| 排队 | `queue` | 照常可用 | 排队，前一个结束自动开始 | 停当前 + 清队列 |
| **并发（默认）** | `concurrent` | 照常可用 | 未满就**同时跑**（同一动作也可以），满了排队 | 停全部 |

四档都是**产品选项**（尤其 `exclusive` 是「专心跑」和「出问题回退」的开关），不是过渡方案。

### 0.2 上限与默认值（唯一一处数字）

| 常量 | 值 | 说明 |
|---|---|---|
| `ActionRunLimits.MaxConcurrentCeiling` | **32** | 1 个运行 = 1 条专用线程 + 最多 5000 条轨迹 + 引擎状态。32 是「最坏情况几十 MB」的可接受点，也是注入被串行化之后再多也没用的点 |
| `DefaultMaxConcurrent` | **32** | 默认 = 上限。每次运行都要人显式触发（自动触发在运行中丢弃，见 §6.7），没有「自己涨到 32」的路径 |
| `MaxQueuedCeiling` | **32** | 队列上限 |
| `DefaultMaxQueued` | **8** | 32 个都在跑时才用得上队列 |
| `TraceCapPerRun` | 5000 | 沿用现状（`AutomationRunner.MaxTraces`），失败报告用 |
| 指示器可见行数 | 4 | 其余折叠「还有 N 个」，完整列表在「运行中的动作」窗口 |
| 失败报告保留份数 | 20 | `logs/failures/`，按文件数裁 |
| 退出等待上限 | 1500ms | 关闭时 `CancelAll` 之后最多等这么久，线程是后台线程，不等也能退 |

> **为什么是 32 而不是无限**：注入被 `InputGate` 串行化之后，100 个并发的「输入型」动作
> 并不会更快，只是多 100 条线程与 100 份轨迹；真正的收益在**等待型**动作（等窗口 / 延迟 /
> HTTP / OCR），32 个等待者已经远超实际场景。要更高只改 `MaxConcurrentCeiling` 一行，
> 注册表 / 队列 / 输入闸 / 隔离逻辑全都不用动——**这是常量，不是结构限制**。

### 0.3 三条贯穿全篇的设计

1. **钩子从「运行中整段 return」改成「按优先级分层判定」**（§6）。
2. **单实例锁换成「注册表 + 准入纯函数 + 队列」**；四档只是参数（§3）。
3. **注入按原语串行**，不做整段运行互斥——否则并发没有意义（§5）。

---

## 1. 现状与参照

### 1.1 LTools（只列能指到代码行的）

| # | 事实 | 证据 |
|---|---|---|
| 1 | 一次运行 = 一把全局互斥（`Interlocked`），第二个直接拒 | `Features/AutomationRunner.cs:74-78`、`TryRunProgram:101`、`IsRunning:42` |
| 2 | 运行中键盘钩子整段让路：CapsLock 一律吞、全部热键 / 状态机 / 面板跳过 | `Hooks/KeyboardHook.cs:148-162` |
| 3 | 运行中鼠标钩子整段让路：长按右键面板、边缘滚轮音量都失效 | `Hooks/MouseHook.cs:146-148` |
| 4 | 停止热键在让路之前，成对吞 keydown / keyup | `Hooks/KeyboardHook.cs:140-143`、`AutomationRunner.cs:446-462` |
| 5 | 运行中只保留两条通道：停止热键、`input.waitKey` | `KeyboardHook.cs:153`、`Features/KeyWaiter.cs:28,65`（**单等待者**） |
| 6 | 运行指示器只有一行文字、没有按钮 | `Views/RecordingIndicatorWindow.xaml`（Dot + TextBlock） |
| 7 | 运行期截图是全局目录，**每次运行开始整体清空** | `Features/Flow/ImageLibrary.cs:33`、`AutomationRunner.cs:135-139` |
| 8 | 「按住不放」的兜底是**全局**集合 | `Features/InputHoldState.cs:25-35`，`RunCore` 的 `finally` 调 `ReleaseAll` |
| 9 | 日志的 `[run-xxxx]` 前缀是**静态字段**，注释写明「同一时间只有一个运行」 | `Core/AppLog.cs:73,131-143` |
| 10 | 失败报告覆盖写同一个 `logs/last-failure.log`，**没有锁** | `Core/FailureLog.cs:38-50` |
| 11 | 运行历史按 `runId` 一行，落盘有锁 → 并发安全 | `Core/RunHistory.cs:154`、`Core/Storage/JsonlHistory.cs` 的 `_gate` |
| 12 | 「停止回放」热键注册**已按引用计数** → 并发安全 | `Features/MacroHotkey.cs:83-131` |
| 13 | 自动触发在运行中丢弃动作引用（只记日志） | `Features/Triggers/TriggerService.cs:323` |
| 14 | 编辑器 / MCP 试运行共用同一把锁，且 `RunHost` 只存**一个** `_token` | `Core/Link/RunHost.cs:29,57-66`、`ProgramRunBridge.cs:25` |
| 15 | 宏回放**不**屏蔽热键（只挂停止热键）——与动作运行不一致 | `KeyboardHook.cs:140-162` 只判 `AutomationRunner.IsRunning` |
| 16 | 所有入口都经 `ActionExecutor.RunRef` | `Features/ActionExecutor.cs:36-50`、`Core/Link/PanelHost.cs:217` |
| 17 | 一次运行占一条线程池线程（`Task.Run`），引擎是**同步阻塞**的 | `ActionExecutor.cs:126`、`AutomationRunner.cs:80,103` |
| 18 | 交互类动作单元的窗口在宿主，按 `dialogId` 隔离 | `Core/Link/UiBridge.cs:28,54,74` |

### 1.2 Quicker 2.3.36 的做法（REA 取证）

| 事实 | 证据（托管类型 / 方法 / 字符串） |
|---|---|
| 运行状态是**集合 + 按动作 id 的租约计数**，不是全局开关 | `Quicker.Domain.Services.RunningActionMgr`：`Add` / `Remove` / `GetAllRunningActions` / `Count` / `TryAcquireActionSlot` / `ReleaseActionSlot` / `GetActionLeaseCount`（同一个 `ConcurrentDictionary`，计数到 0 才移除） |
| 同一个动作**可以**并行多份 | 同上：租约是计数而不是布尔 |
| 停止粒度到「某一个动作」 | `RunningActionMgr.StopActionByIdOrName` / `Stop` / `TryStopByContextId` / `StopAll`；消息 `StopRunningActionMessage` |
| 运行期间 Quicker **继续可用** | 设置项 `functionHotKeys.keysForCancelRunningTasks`：「停止运行中的动作」——「立即停止所有正在运行的动作。建议设置此快捷键，以便中止运行不正常的动作。」 |
| 运行可见性 | 托盘选项「在托盘图标上显示运行中的动作个数」——「有动作在**后台运行**时…便于发现长时间运行的动作。」 |
| 唯一的「全局静默」是**显式暂停**，且可配保留哪些热键 | `hotkeyModeAfterPause`：「暂停 Quicker 时保留快捷键」：保留所有 / 仅保留暂停恢复 / 清除所有 |
| 停止还有一张白名单 | `actionExecution.stopAllWhiteList`：「停止运行中的动作的额外白名单」 |

**结论**：Quicker 把一次运行当**后台任务**（可多个、可列表、可单独停、托盘计数）。
本方案在这一点上**照抄**，但不照抄「一跑就静默」与「没有输入闸」——那两处 LTools 要自己收口。

### 1.3 根子差异

| | Quicker | LTools 现在 |
|---|---|---|
| 运行的身份 | 可并存的任务（按 id 租约） | 进程级独占的一段流程 |
| 谁让路 | 只有「暂停 Quicker」 | 一运行就整段让路 |
| 第二个动作 | 允许（同动作也能并行） | 拒绝，且钩子已经吞了键 → 「按了没反应」 |
| 停止 | 停全部 + 停单个 + 白名单 | 只有停止热键 |

---

## 2. 不变量、术语、配置

### 2.1 不变量（四档都不许破）

1. **停止第一优先**：停止热键永远先判、永远能停；同一次按键的 keydown / keyup 必须成对吞。
2. **注入不回流**：`LLKHF_INJECTED` / `LLMHF_INJECTED` + `dwExtraInfo` 标记过滤保持，任何新入口都不得绕过。
3. **钩子回调不阻塞、不碰 WPF**：判定是纯数据，副作用一律 `Ui.Post`，日志走 `AppLog`。
4. **普通物理键照常下传**：运行中用户仍能在目标程序里打字（除 `input.waitKey` 正等着的那一个键）。
5. **不假装成功**：被拒 / 排队 / 停止 / 启动失败都要有可见结果，不静默。
6. **一次运行的日志、轨迹、运行历史、失败报告、按住键状态按 `runId` 归档**，互不污染。

### 2.2 术语（**待确认**，确认后进 `CONTEXT.md`）

| 提议叫法 | 意思 | 不要用 |
|---|---|---|
| **运行期互操作** | 动作运行期间 LTools 自己的入口照常可用 | 边跑边用、共存模式 |
| **运行队列** | 已提交、等着开跑的运行，先进先出 | 待办、等待列表 |
| **并发的运行** | 同一时刻多于一个运行在执行 | 多开、多线程运行 |
| **运行中的动作** | 注册表里当前在跑的那批运行（窗口/托盘文案用） | 任务、进程、实例 |

> 现有词条只改一条：`CONTEXT.md` 的「**运行**」写着「有单实例保护」，
> 实施后要改成「按 `ActionRun.Mode` 决定独占 / 排队 / 并发」——**实施时再改**。

### 2.3 配置（`configs/settings.json`）

- **一个枚举而不是三个布尔**：三档是互斥的产品档位，布尔组合会拼出「排队但不互操作」这种没人要的状态。
- `MaxConcurrent` 是**运行总数上限（含同一动作的多份）**，不是「不同动作的种数」；
  只在 `concurrent` 档参与判定，其它档按 1 算（界面上置灰）。
- 建议值 / 范围见 §0.2；越界一律夹取（`Math.Clamp`），不报错。
- 不写迁移器、不认旧字段（照项目「不向后兼容」）。

设置页（`Views/ConfigHelper.xaml` 的「通用」页）在「运行提示」卡片下面加一组「动作运行时」：

| 控件 | 文案 | 行为 |
|---|---|---|
| 下拉 `ActionRunModeCombo` | 独占（旧行为）/ 继续使用 LTools / 继续使用 + 排队 / **并发运行（推荐）** | 只写 `settings` 域，立刻刷 `AppState.ActionRun` |
| 数字 `ActionRunMaxCombo` | 同时最多 [1–32] 个 | 只在「并发运行」档可用 |
| 数字 `ActionRunQueueCombo` | 超额时最多排队 [0–32] 个 | 同上 |
| 灰字 | 「运行中打开面板 / 速记会抢走前台，动作后面的按键可能打到新窗口；并发时鼠标类步骤也会互相影响。停止热键 Ctrl+F3 随时可用，托盘右键可看「运行中的动作」。」 | 说明，不可编辑 |

生效链路：`Config/ConfigFiles.cs` 的 `SettingsFile.ActionRun` → `AppConfig` / `ConfigBundle`（`ToSettings` / `LoadCore`）/ `DomainStores` / `LTools.example.json`；
运行期缓存 `AppState.ActionRun`（照 `AppState.Record` 的写法），钩子只读这份；
**`Core/RuntimeRefresh` 必须登记**（`settings.json` → `AppState.ActionRun`）。

---

## 3. 运行内核（最终）

### 3.1 模块划分（一次到位）

| 文件 | 职责 |
|---|---|
| `Features/Running/ActionRunSession.cs` | 一次运行的全部状态（下表） |
| `Features/Running/RunningActions.cs` | 注册表：登记 / 摘除 / 快照 / 计数 / 按 `runId` 停 / 全停 |
| `Features/Running/RunAdmission.cs` | **纯函数**：这次提交是「开跑 / 排队 / 拒」 |
| `Features/Running/RunQueue.cs` | 队列：入队 / 同名去重 / 上限 / 出队 / 清空 |
| `Features/Running/RunCoordinator.cs` | 唯一入口：`Submit`、`Drain`、`Cancel`、`CancelAll`、`Snapshot`，以及**给每个运行开专用线程** |
| `Features/Running/RunLimits.cs` | §0.2 的常量（唯一的数字来源） |
| `Features/AutomationRunner.cs` | 保留：**一次运行怎么跑**（引擎、轨迹、结果、收尾）——不再持有全局锁 |

`AutomationRunner.IsRunning` 保留为 `RunningActions.IsAny`（`MacroRecorder` / `TriggerService` /
`ActionParams` 等调用点语义不变，只是从「唯一」变成「任一」）。

### 3.2 `ActionRunSession`

```csharp
internal sealed class ActionRunSession
{
    public string RunId { get; }            // run-xxxx（IdFactory.ShortId）
    public string ActionId { get; }
    public string Name { get; }
    public int CopyIndex { get; }           // 同动作的第几份；1 = 不带后缀，2+ = 「#2」
    public RunSource Source { get; }
    public CancellationTokenSource Cts { get; }
    public InputHoldState Hold { get; } = new();   // 每运行一份
    public Thread Thread { get; }
    public long StartedTick { get; }
    public int Step { get; set; }           // 指示器 / 列表显示
    public bool StopRequested { get; set; }
    public bool ExclusiveInput { get; }     // 试运行 = true
    public string Label => CopyIndex > 1 ? $"{Name} #{CopyIndex}" : Name;
}
```

### 3.3 注册表

```csharp
internal static class RunningActions
{
    static bool IsAny { get; }
    static int Count { get; }
    static int CountOf(string actionId);              // 算 CopyIndex 用
    static IReadOnlyList<ActionRunSession> Snapshot(); // 按开始时间排序
    static bool TryAdd(ActionRunSession session);      // 只在准入说 Start 后调
    static void Remove(ActionRunSession session);
    static bool Cancel(string runId);                 // 单个停
    static int CancelAll();                           // 停止热键 / 退出
}
```

- 内部一个 `ConcurrentDictionary<string, ActionRunSession>`（`runId` 为键）+ 一个
  `Interlocked` 计数；`Snapshot` 排序后返回数组（调用方不能改内部集合）。
- **不按 `ActionId` 做键**：同一动作的 2、3 份是不同 `runId`，各自独立停。
- `CancelAll()`：对每个会话 `Cts.Cancel()`，再 `MacroPlayer.Stop()`；返回条数给提示用。

### 3.4 准入（纯函数）

```csharp
internal enum AdmissionOutcome { Start, Queued, RefuseQueued, RefuseFull }

internal static class RunAdmission
{
    public static AdmissionOutcome Decide(
        ActionRunMode mode, int maxConcurrent, int maxQueued,
        int runningCount, int queuedCount, bool sameActionQueued,
        RunSource source, int maxConcurrentCeiling);
}
```

判定顺序（**顺序就是优先级**）：

1. `source` 是 `Editor` / `Mcp` / `SubAction` 且 `runningCount > 0` → `RefuseFull`（试运行不排队，见 §16）。
2. `source == Trigger` 且 `runningCount > 0` → `RefuseFull`（自动触发不排队，§6.7）。
3. `runningCount == 0` → `Start`。
4. `mode == concurrent` 且 `runningCount < min(maxConcurrent, maxConcurrentCeiling)` → `Start`——
   **不看是不是同一个动作**：同一动作也可以有 2、3…份（§16 决策）。
5. `sameActionQueued` → `RefuseQueued`（队列里已经有一条一样的，别越按越多）。
6. `maxQueued > 0` 且 `queuedCount < maxQueued` → `Queued`。
7. 其余 → `RefuseFull`（`exclusive` / `interop` 按 `maxQueued = 0` 算，直接落到这里）。

### 3.5 队列

```csharp
internal sealed record RunRequest(
    string ActionId,      // 只存 Id，不存 ActionDto 引用
    string DisplayName,   // 入队那一刻的名字，只用于提示
    int CursorX, int CursorY,
    RunSource Source,
    bool NeedsAsk);       // 入队时算一次，轮到它时才弹参数框
```

- **不存 `ActionDto` / `ActionProgram`**：排队期间动作可能被改 / 被删。
  出队时按 Id 从 `ActionRegistry` 重取；取不到 → 跳过 + toast「「X」已被删除」。
- **不存参数值**：轮到它时才在 UI 线程弹一次参数框（此时前一个已经结束）。
- **同名去重只作用于「还在排队的」**：正在跑的副本不算（它们本来就允许并存）。
- 上限 `MaxQueued`；入队 / 出队 / 清空全在锁内，纯数据。
- 进程退出 / 切回 `exclusive` / 停止热键 → `Clear()`。

### 3.6 提交与出队

```
ActionExecutor.RunRef(reference)
  → 内置功能？FeatureRunner 立刻跑（从不排队）
  → 动作？RunCoordinator.Submit(action, cursor, source)
        └ RunAdmission.Decide(...)
             ├ Start    → 建 session（分配 CopyIndex）→ TryAdd → 开专用线程跑 RunCore
             ├ Queued   → RunQueue.Enqueue → toast「已排队（第 N 个）」
             ├ RefuseQueued → toast「已排队」
             └ RefuseFull   → toast + 「停止运行」按钮
  → return RunSubmission（Feature / Started / Queued / Rejected / NotFound）
```

- `RunCore` 的 `finally`：写运行历史 → 放自己的按住键 → `Remove(session)` → `RunCoordinator.Drain()`。
  `Drain` 在**槽位释放之后**才取下一个（`MaxConcurrent = 1` 时否则永远卡住），并且**给下一个开新线程**
  ——「一个运行一条线程、运行结束线程退出」，线程生命周期与运行一致，`session.Thread` 才有意义。

### 3.7 每个运行一条专用线程（不是线程池）

- 引擎是**同步阻塞**的（OCR / UIA / 鼠标拖动 / 延迟都占着线程）。并发 32 条 `Task.Run`
  会把线程池的阻塞线程占满，线程池注入新线程是每秒 1–2 条，配置写入 / 其他 `Task.Run`
  会跟着变慢。
- 最终形态：`RunCoordinator` 给每个运行 `new Thread(RunCore) { IsBackground = true, Name = runId }`。
  后台线程 → 进程退出不被它拖住；`Thread` 引用存在 session 里，但退出时**不等它**（见 §9：
  运行线程可能正卡在「等 UI 线程的参数框」上，UI 线程等它就是死锁）。
- 不再有 `ActionExecutor` 外层那次 `Task.Run`（`ActionExecutor.cs:126`），提交本身只做登记 + 起线程。
- 引擎内部仍是同步代码：`AppLog.BeginRun` 用 `AsyncLocal`（§4），线程自己的 ExecutionContext 天然隔离。

### 3.8 生命周期

```
提交 ──Start──▶ RunningActions + 线程 ──RunCore──▶ 结果 / 历史 / toast
   │                                   │
   └─Queued─▶ RunQueue ──出队──────────┘
                                       └─ Remove → Drain 下一个
停止热键 / 逐项停 ──▶ Cts.Cancel() ──▶ 当前步骤返回后收尾（≤ 500ms，不可取消的长调用除外）
退出 ──▶ CancelAllAndRelease（取消 + 放按住键，不等线程） → HookDispatcher.Stop()
```

---

## 4. 按运行隔离

| # | 现在共享的东西 | 并发会怎样 | 最终做法 |
|---|---|---|---|
| 1 | `ImageLibrary.ClearCaptures()` 每次运行开始清空全局目录 | 新运行删掉旧运行还要用的 `.file` | **只在「加入前 `Count == 0`」时清**；启动时照旧清一次 |
| 2 | `InputHoldState` 全局集合 | A 结束把 B 按着的键放了 | 每会话一份 `session.Hold`，`ReleaseAll` 只放自己的（静态方法改实例方法） |
| 3 | `AppLog._runId` 静态字段 | 两行日志串成一个 runId | 改 `AsyncLocal<string>`；`CurrentRunId` 签名不变 |
| 4 | `FailureLog` 覆盖写 `last-failure.log`，无锁 | 两份报告互相覆盖、内容截断 | **每运行一份** `logs/failures/<runId>.log`（保留 20 份）+ 最新一份复制到 `last-failure.log`（兼容现有文案 / 入口）；写入加锁；toast 打开该运行自己那份 |
| 5 | `ToastService` 的 key 是 `run:<动作名>` | 同名动作互相顶掉 | `run:<runId>`；失败提示各带各的报告路径 |
| 6 | 指示器只有一行文字 | 只能看见一个 | §8.1 栈式 + 逐行 ✕ |
| 7 | `RunHost._token` / `ProgramRunBridge._token` 都是单值 | 两个编辑器同时试运行互相吞事件 | **试运行同一时刻只有一次**（有运行在跑就拒），所以两侧都按「当前 token」过滤、新一轮覆盖旧的即可；关键是**结果回调必须每次都发**（`RunCore` 的 `finally` 兜底），否则残留 token 会让后续试运行永远被判「已有动作运行」。「停止」只停试运行（`RunCoordinator.CancelTrials`），不碰用户动作 |
| 8 | 同名多副本分不清 | 「X 在跑」有两份，停哪个？ | `CopyIndex` → 指示器 / toast / 列表显示 `X #2`；停止、逐项停一律按 `runId`，不按名字 |
| 9 | `ImageLibrary.NextShotPath` 是「查存在 → 再创建」的 TOCTOU | 同一秒两张开抢同一个文件名（后一个 `File.Create` 抛共享冲突） | `NextShotPath` 内加静态锁 + `FileMode.CreateNew` 重试 |
| 10 | 「写速记 · 追加」是读-改-写（`NoteActions.ComposeAppend`） | 同动作两份同时追加会丢一次 | `NoteRepository` 按路径加锁（锁在写入口一处）；其它 `file.write` / `state/values.json` 同键按「最后一个赢」，文档写明 |
| 11 | 同一动作两份会写同一个目标窗口 / 文件 / 状态键 | 结果互相打架 | 这是**语义冲突**，不是状态串：允许（用户要的），靠 `#2` 标签 / 运行历史可见；要避免就绑两个动作或用「目标窗口」参数错开 |
| 12 | `input.waitKey` 单等待者 | 第二个等待者顶掉第一个 | `KeyWaiter` 支持多个等待者：按 arm 顺序，先命中者独占该键（含它的自动重复 / 抬起），其余继续等；修饰键从不吞 |
| 13 | 交互类动作单元的窗口（`ui.input` 等）可以同时弹两个 | 用户分不清哪个属于哪个动作 | §7.1 `UiPromptQueue` 串行显示 |

---

## 5. 输入闸（`Features/InputGate.cs`）

**语义：原语级互斥**，不是整段运行互斥——否则并发没有意义。

| 包住 | 粒度 |
|---|---|
| `InputHelper.Tap / Down / Up / Combo / ModifiedSequence` | 一次调用 |
| `Features/MouseOps.cs` 的 `Perform / Down / Up / Move / MoveRelative` | 一次调用 |
| `Features/TextSender.Send`（含剪贴板粘贴路径） | 一次发送 |
| `Native/NativeClipboard` 的快照 / 写 / 还原 | 整段「改剪贴板」操作 |
| `MacroPlayer.PlaySync` | **整段回放**（两段宏交错会变成乱码） |
| `input.mouse` 的 `drag`（down → 移动 → up） | 整段拖动 |

```csharp
public static IDisposable Enter(InputKind kind, CancellationToken token = default);
public static IDisposable EnterRunExclusive(string runId, CancellationToken token);  // 试运行整段
```

- 实现：一把 `_exclusive(1,1)` + 一把 `_primitive(1,1)`。普通原语 = 先 `_exclusive`、再 `_primitive`；
  试运行 = 整段只占 `_exclusive`。这样试运行期间用户动作的原语会等在 `_exclusive` 上，
  而不是两套注入交错。
- **可取消**：`SemaphoreSlim.Wait(token)`，按停止热键立刻返回；试运行等锁时也能被停止。
- 先来先得（`SemaphoreSlim` 近似 FIFO），不设超时——等待中的运行仍然能被停止。
- **不**包住：UIA 读、OCR、OpenCV、文件、HTTP（它们不注入键鼠、也不改剪贴板）。

---

## 6. 钩子与入口

### 6.1 键盘的唯一顺序

| 优先级 | 分支 | 运行中是否变 |
|---|---|---|
| 1 | 关窗抬起吞键 / 注入过滤 / 长截图 / `KeyUp` 清锁存 | 不变 |
| 2 | **停止热键**（自动化 + 宏回放都问） | 不变（仍在录入拦截之前） |
| 3 | 运行中：`KeyWaiter.HandleKey`（多等待者） | 不变 |
| 4 | 热键录入 / 宏录制 / 录屏面板 / 贴图 / 全屏画布挂起 | 不变（显式模式优先） |
| 5 | **全局快捷键** `HotkeyRegistry.TryFire` | **不再跳过**：命中内置功能照常跑；命中动作按准入结果吞键 + 处理 |
| 6 | **CapsLock 状态机** | **不再跳过**：只关掉「轻按补 Esc」（§6.3） |
| 7 | 面板 Esc / 已吞 keyup | 不变 |

实现不是「把 `if (IsRunning)` 删掉」，而是**只保留一条回退分支** + 一层新判定：

```csharp
// 回退档（exclusive）：保持今天的行为——整段让路，钩子在这里就返回。
if (AppState.ActionRun.Mode == ActionRunMode.exclusive && RunningActions.IsAny)
{
    if (KeyWaiter.HandleKey(vk, isDown, isUp)) return (IntPtr)1;
    if (isUp && AppState.SwallowedVks.Remove((int)vk)) return (IntPtr)1;
    if (vk == Win32.VkCapital) { if (isDown) MarkSwallowed(vk); return (IntPtr)1; }
    return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
}

// 其余三档：分层。
if (KeyWaiter.HandleKey(vk, isDown, isUp)) return (IntPtr)1;
if (isDown && HotkeyRegistry.TryFire(vk) is { } reference)
{
    // 准入与提示都在 RunRef 里（单一来源），钩子只负责「命中就吞」。
    MarkSwallowed(vk);
    ActionExecutor.RunRef(reference, 0, 0, RunSource.Hotkey);
    return (IntPtr)1;
}
```

> 准入**不在钩子里**判：钩子只做「这个键归 LTools」，开跑 / 排队 / 拒绝 / 提示
> 全在 `RunCoordinator.Submit` 一处（§3.6），避免两套判定漂移。

### 6.2 判定收口：`Features/RunHookPolicy.cs`（纯函数）

```csharp
internal static class RunHookPolicy
{
    // 运行中这条命中要不要接管（回退档 exclusive 整段让路，其余档一律接管）
    public static bool HandlesHotkey(ActionRunConfig run, int runningCount);

    // CapsLock 轻按要不要补 Esc（运行中不补）
    public static bool CapsLockTapSendsEsc(int runningCount);

    // 鼠标手势（长按右键面板 / 边缘滚轮音量）要不要让路
    public static bool MouseGesturesEnabled(ActionRunConfig run, int runningCount);

    // 被拒 / 已排队的提示节流（同一组合键 1 秒内只提示一次）
    public static bool ShouldNotifyBusy(long lastTick, long nowTick);
}
```

- 纯函数 → `--selftest` 直接断言，不必伪造钩子事件（照 `DoubleClickEditRow` / `MenuCone` 的路子）。
- 回退档 `exclusive` 是**产品回退**（与放开并发之前一模一样：运行中连内置功能都不接管），
  不是过渡方案；其余三档一律「接管」——接管 ≠ 一定开跑，动作还要过准入。
- 钩子里只做一次字典查找 + 一次快照读取，不做分析。
- 节流只压**快捷键来源**的「被拒 / 已排队」提示（按住连发会一秒刷几十条）；
  面板 / 悬浮点一下是人主动操作，照常提示。判定在 `RunCoordinator.ShouldNotifyBusy`。

### 6.3 CapsLock 轻按不补 Esc

运行中允许 CapsLock 当修饰键（否则 `CapsLock+T` 开不了面板），但**轻按不再发 Esc**
——那一下会打进目标程序，正好是动作最怕的干扰。

- `Core/CapsLockStateMachine.cs` 的 `InputHelper.Tap(VkEscape)`（`:103`）由调用方传入
  「是否允许补 Esc」（`TryHandle(..., tapEsc: RunHookPolicy.CapsLockTapSendsEsc(...))`）。
- `PrepareToRun()`（清 `CapsLockIsDown` / `OtherKeyPressed`）**只在注册表为空时**执行；
  并发时清别人的状态会让面板手势错乱。

### 6.4 第二个动作：吞键 + 一次可见提示

- 准入结果不是「立刻开跑」时：**照旧吞掉这个组合键**（避免 `CapsLock+O` 里的 `o` 漏进目标程序），
  但必须给一次提示——否则就回到「按了没反应」。
- 提示由 `RunCoordinator.Submit` 统一发（钩子 / 面板 / 悬浮 / 托盘共用这一处），
  `ToastService`，`key = "run:busy"`（就地替换、不刷屏），正文按结果：
  - 拒绝：「「X」正在运行，等它结束或按 Ctrl+F3 停止」；
  - 排队：「「X」已排队（第 2 个）」；
  - 并发档立刻开跑：**不**提示（指示器多一行，已经看得见）。
  按钮「停止运行」→ `RunCoordinator.CancelAll()`。
- 节流：同一组合键 1 秒内只提示一次（`RunHookPolicy.ShouldNotifyBusy`）。
- **不再用 `TrayService.Notify`**（跟鼠标、1.8 秒就消失，等于没提示）。
- `ActionExecutor.RunRef` 返回值从 `bool` 换成 `RunSubmission`
  （`Feature` / `Started` / `Queued` / `Rejected` / `NotFound`），钩子 / 面板 / 悬浮都按它提示。

### 6.5 鼠标

- 去掉 `MouseHook.cs:146-148` 的整段让路；长按右键面板、边缘滚轮音量照常。
- 保留「点击窗口选取」遮罩让路（`:143`）与滚动截图分支（`:125`）——它们是显式会话。
- `wParam` 的 down / up 同进同出规则不变（吞 down 必吞 up）。

### 6.6 停止键路由 + 宏回放归一

- 现状 `KeyboardHook.cs:140-142` 是三元表达式，宏与动作只能二选一；且宏回放不屏蔽热键、
  动作运行屏蔽 —— 两套语义。
- 新增 `Features/StopKeyRouter.cs`：先问 `AutomationRunner.HandleStopHotkey`（= 吞键 + `CancelAll`），
  没命中再问 `MacroPlayer.HandleStopHotkey`；吞键配对状态只有一份，避免两边各存一份导致 keyup 漏吞。
- 宏回放时命中动作快捷键：同样按准入处理（默认并发档 = 再开一份）。

### 6.7 自动触发不变

`TriggerService.cs:323` 的「运行中丢弃动作引用」保持原样——后台联动连环触发只会更难排查。
并发之后「运行中」不再等于「LTools 变砖」，丢弃的代价已经很小。

---

## 7. 并发下的交互、前台与光标

### 7.1 交互窗口串行：`Core/Link/UiDialogHost` 的闸 + 参数框的闸

- 宿主侧已按 `dialogId` 隔离（`UiBridge`），并发不会串值。
- **同时只显示一个交互框**：`ui.input` / `ui.confirm` / `ui.select` / `ui.textWindow`（读取 / 等关闭）/
  `file.select` / `screen.pickPoint` 走 `UiDialogHost` 的 `SemaphoreSlim(1,1)`；排队时标题后缀「（等前一个输入框）」。
  等待期间 `ui.cancel` 会把它记进 `Cancelled`，排到时直接给「取消」答复、不弹窗。
- `ui.textWindow` 的「显示」模式不排队（非阻塞、按 `key` 复用）；同一个 `key` 被两个运行用 = 共享一个窗口，
  这是它的语义（显示同一份信息）。
- 动作的**参数框**（运行前问输入）在 `AutomationRunner.AskParameters` 里另有一把闸（`ParamGate`）：
  一次只问一个，后到的运行等前一个关掉；按停止能把等待中的运行叫醒（`Wait(cts.Token)`）。
- 系统文件框 / 取点遮罩仍是「关不掉」的已知限制（停止只结束运行，框留到用户自己按 Esc）。

### 7.2 前台 / 光标冲突（说清楚，不假装能解决）

事实：`input.sendKeys` / `input.sendText` 打到**当前前台**，`input.mouse` 用**全局光标**。
两个并发动作都会动光标 = 互相拽；打开面板 = 前台变了，后续按键可能打到面板。

| 措施 | 说明 |
|---|---|
| **目标窗口**参数 | `input.sendKeys` / `input.sendText` / `input.mouse` 新增可选「目标窗口」（默认空 = 当前前台）。注入前 `WindowAction.ActivateNoWait(hwnd)`，让「用户切走了」不再打偏。取窗口走既有的 `WindowPickOverlay`（`PickerKind.window`） |
| 前台窗口入日志 | 运行开始 / 失败报告记「前台：<进程 · 标题>」，排查「为什么打到别处」 |
| 指示器 / 列表提示 | 并发时一行灰字：「鼠标类步骤会互相影响」 |
| 光标不虚拟化 | 不引入虚拟光标 / 后台注入（§15） |

### 7.3 剪贴板

- `NativeClipboard` 的快照 / 还原是「读全局 → 覆盖 → 还原」，两个运行同时用必然互踩。
  所有剪贴板改动进 `InputGate`（一次操作粒度）；快照 / 还原整段在闸内，另一个运行会等它结束。
- 仍有一个固有窗口：用户在 A 的「复制 → 粘贴」之间手动复制了别的东西，A 的还原会覆盖它
  （今天的单运行也有，并发只是更容易撞上）。文档写明，不做「虚拟剪贴板」。

---

## 8. 可观测与管理

### 8.1 指示器栈（`Views/RecordingIndicatorWindow`）

- 最多显示 4 行：`动作名 · 第 N 步`（多副本 `动作名 #2 · 第 N 步`）+ 右侧 ✕（停该运行）；
  超出的折叠成「还有 N 个」+「全部…」（打开运行列表）；底部一行灰字提示停止热键与「鼠标类步骤可能相互影响」。
- **内容来自注册表**（`RunningActions.Snapshot()`，500ms 刷）：不是每个运行各推一份文本，
  并发时谁也不会把别人的行冲掉；`ShowAutomation(text)` 的 `text` 只在注册表为空时当兜底显示。
- 窗口仍是 `WS_EX_NOACTIVATE` + `ShowActivated=False`；**自动化态去掉 `WS_EX_TRANSPARENT`**（要能点 ✕），
  录制 / 倒计时态保持点击穿透。窗口接 `Views/StayClickable`（模态窗口打开时仍可点）。
- 点 ✕ → `RunningActions.Cancel(runId)`（UI 线程直接掉用，只是置取消位）。
- `SuppressForCapture` / `HideAutomationForCapture` 收整组（截图时不留半行）。

### 8.2 「运行中的动作」窗口（新 `Views/RunningActionsWindow`，在**执行器**）

指示器只有 4 行，32 个并发时没法逐项管理——最终形态给一个列表：

| 列 | 内容 |
|---|---|
| 动作 | `Label`（含 `#2`） |
| 来源 | 面板 / 快捷键 / 悬浮 / 触发 / 编辑器…（`RunHistory.SourceLabel`） |
| 已运行 | `mm:ss`（1 秒刷新） |
| 步骤 | `第 N 步`（停止中显示「正在停止」） |
| 操作 | 「停止」按钮（逐项） |

底部：「全部停止」+「运行历史…」（`LinkWorker.RequestSettings("open", "logs-history")`）+「关闭」。
空态：「现在没有正在运行的动作」。数据：窗口打开时 1 秒 `RunCoordinator.Snapshot()`，关窗即停。
入口：托盘右键「运行中的动作…（N）」（`TrayMenu`，N=0 置灰）、指示器的「全部…」。
**窗口与注册表在同一个进程**（托盘与指示器都在提权进程），所以不需要跨进程命令（见 §10）。

### 8.3 托盘

- 工具提示每 2 秒跟看门狗刷：「LTools · 正在运行 N 个动作」（照 Quicker 的托盘计数）。
- 菜单里同一项文字是「运行中的动作…（N）」，点开 `RunningActionsWindow`；`N = 0` 时置灰。

### 8.4 运行历史与失败报告

- 运行历史不变：每次运行一行（`runId` 已经是键），并发天然分开。
- 失败报告改「每运行一份」（§4 #4）：`logs/failures/<runId>.log`，保留 20 份；
  `last-failure.log` 仍是最新那份的副本（现有文案 / 入口不用改）；toast 点开的是自己那份。

---

## 9. 停止、退出与失败

| 入口 | 作用 |
|---|---|
| 停止热键（默认 Ctrl+F3） | **停全部**（含队列）+ 停宏回放；toast「已停止 3 个 · 清空队列 2 项」 |
| 指示器每行 ✕ / 运行列表「停止」 | 停那一个（`RunningActions.Cancel(runId)`） |
| 编辑器「停止」按钮 | 停它自己发起的那一个试运行（`RunCoordinator.CancelTrials`；跨进程走 `run.stop`） |
| 托盘「退出 LTools」 | `RunCoordinator.CancelAllAndRelease()`（取消 + 每会话 `Hold.ReleaseAll` + 兜底实例）→ `HookDispatcher.Stop()`；**不等线程**（后台线程不挡退出，且等 UI 线程会死锁） |

单个停止仍是「请求 + 等当前步骤返回」，指示器 / 列表显示「正在停止」；
不可取消的长调用（UIA / OCR / 大文件）只能等它收尾（`ACTION_SYSTEM_OPTIMIZATION.md` P1 的既有约束，不改成强杀）。

---

## 10. 跨进程（宿主 ↔ 执行器）

- **运行注册表只在执行器**（提权进程）：宿主不持有运行状态。
- **不新增「列表 / 逐项停」的链路命令**：托盘菜单、运行指示器、`Views/RunningActionsWindow` 都在执行器里，
  直接读 `RunCoordinator.Snapshot()` / `RunningActions.Cancel(runId)` 就行。多一套「宿主问执行器要列表」
  只会在两边各留一份运行状态（还要处理 1 秒轮询与断线），没有任何收益。
- 跨进程**只有试运行**这一条（`run.program` / `run.stop` + 轨迹 / 慢步骤 / 结果事件）：
  - 试运行同一时刻只有一次（有运行在跑就拒），所以两侧都按「当前 token」过滤、新一轮覆盖旧的即可；
  - **结果回调必须每次都发**（`RunCore` 的 `finally` 兜底），否则残留 token 会让后续试运行永远被判「已有动作运行」；
  - 「停止」只停试运行：`RunCoordinator.CancelTrials()`（编辑器 / MCP / 子动作），**不连带停用户动作**。
- 指示器、托盘计数在执行器本地直接读注册表，不走链路。

---

## 11. 风险矩阵

| 风险 | 触发条件 | 应对 |
|---|---|---|
| 两套注入交错成乱码 | 两个动作同时 `sendKeys` | `InputGate` 原语级互斥；宏回放整段占用 |
| 前台被抢后按键打到别处 | 用户运行中开面板 / 速记 | 目标窗口参数 + 日志记前台；文档写明 |
| 光标互相拽 | 两个动作都用 `input.mouse` | 知情提示 + 目标窗口；不虚拟化光标 |
| 剪贴板互踩 | 两个动作都用剪贴板 | 剪贴板操作进 `InputGate`；固有窗口写明 |
| 同一动作多份抢同一个目标 / 文件 / 状态键 | 用户连按同一个动作 | 允许（要的）；`#2` 标签可见；写速记按路径加锁；其余同键最后写赢 |
| 误触连按排出「幽灵动作」 | 连按同一个动作 | 队列里同名只留一条；停止热键连队列一起清 |
| 两个输入框同时弹 | 两个动作都要问用户 | `UiPromptQueue` 串行 |
| 指示器挤成一团 | 并发 8+ | 4 行 + 「还有 N 个」+ 运行列表窗口 |
| 日志串号 / 报告互相覆盖 | 并发运行 | `AsyncLocal` runId；失败报告每运行一份 |
| 停止不彻底 | 并发 + 按住键 | 每会话 `Hold` + `CancelAllAndRelease`（取消 + 放键，不等线程） |
| 运行期截图被另一个运行清空 | 并发开始新运行 | 只在「没有其它运行」时清 |
| 线程池被阻塞线程占满 | 32 个同步阻塞运行 | 每运行一条**专用线程**，不占线程池 |
| 32 个运行最坏内存 | 全部跑 5000 步 | 每运行轨迹上限 5000；上限是常量，可按实测调 |

---

## 12. 落地批次（同一个最终模型，只换接线顺序）

**没有临时结构**：每一步落地后，`ActionRunSession` / `RunningActions` / `RunAdmission` /
`RunQueue` / `RunCoordinator` / `InputGate` 的形状都不再变，后续批次只是「把更多入口接上去、
把默认档放开」。默认档在批次之间变化，是**发布开关**，不是设计债。

| 批次 | 接线内容 | 默认档 | 退出标准 |
|---|---|---|---|
| **B1 运行内核** | §3 全部 + §4 隔离 + §5 输入闸；入口只接 `ActionExecutor` | `exclusive`（行为与今天一致） | 构建 0 警；`runCore` 组绿；`RunConcurrencySmoke` 绿；真机跑一个动作与今天无差别 |
| **B2 钩子与入口** | §6 全部 + §2.3 设置页 | `interop` | `runHook` 组绿；真机 5 条（面板 / 提示 / CapsLock / 停止 / 等待按键） |
| **B3 并发与可观测** | §7、§8、§9、§10；默认 `MaxConcurrent = 32` | `concurrent` | `runAdmission` / `runQueue` / `runObservability` 组绿；真机并发 4 条 |
| **B4 目标窗口 + 收口** | §7.2 的「目标窗口」参数、§8.4 报告分文件、文档 / 术语 / CHANGELOG | `concurrent` | 三单元参数表 + 文档同步完成；全量 `--selftest` 绿 |

- B1 是「结构替换但行为不变」，最容易回归；B2 才让用户看见变化；B3 打开发并发；B4 是长尾。
- 任何一批停下来，仓库都可构建、可发版；回退只需把 `Mode` 调回 `exclusive`。

---

## 13. 验收

| 层 | 命令 / 位置 | 覆盖 |
|---|---|---|
| 纯逻辑 | `--selftest --filter runCore` | 注册表增删 / 计数 / 快照 / 按 runId 停 / 全停；每运行 Hold；`AsyncLocal` runId；截图清空规则；`NextShotPath` 并发；`NoteRepository` 加锁 |
| 纯逻辑 | `--selftest --filter runAdmission` | 准入七条（含同动作未满 → 再开、同动作已排队 → 拒、试运行 / 触发拒、上限 32 夹取） |
| 纯逻辑 | `--selftest --filter runQueue` | 入队 / 同名去重 / 上限 / 出队重取（动作被删）/ 清空 |
| 纯逻辑 | `--selftest --filter runHook` | `RunHookPolicy` 四函数、节流、CapsLock Esc、停止键路由（动作 + 宏同时） |
| 纯逻辑 | `--selftest --filter runGate` | `InputGate` 原语互斥不交错、试运行整段独占、等待可取消 |
| 纯逻辑 | `--selftest --filter runTarget` | 「目标窗口」留空不动、找不到报「未找到」（带动作单元名） |
| 纯逻辑 | `--selftest --filter runObservability` | 托盘提示文字随后台运行数变化 |
| 仓库内冒烟 | `tests/RunConcurrencySmoke`（`dotnet run --no-build`） | 真 `RunCoordinator` + 假动作：并发重叠 / 上限 / 同动作两份 / 队列 / 停止全部 / 停止单个 / 历史不串 / 默认档与托盘计数 |
| 真机 | 下方「真机清单」 | 钩子、前台、光标、面板、等待按键、指示器 ✕、托盘计数——**注入事件验不出，只能手按** |

### 13.1 真机清单

**B2（互操作）**
1. 跑一个 10 秒循环 → `CapsLock+T` 开面板（动作不停）、点内置功能（截图 / 速记）能开；
2. 按一个动作的快捷键 → 提示「已在运行 / 已排队」，目标窗口**收不到**那个组合键；
3. 运行中轻按 CapsLock → 目标窗口**收不到** Esc；
4. 停止热键、`input.waitKey` 行为与今天一致；
5. 切 `exclusive` → 回到旧行为（全屏蔽）。

**B3（并发 + 可观测）**
1. 两个不同动作同时跑（一个延迟循环、一个输入）→ 指示器两行、各自 ✕ 只停自己；
2. 同一动作连按两次（`MaxConcurrent = 32`）→ 出现 `X` 与 `X #2` 两份，独立停止、运行历史两行；
3. 连开 6 个以上 → 指示器 4 行 + 「还有 N 个」，托盘计数与「运行中的动作」窗口一致，窗口 1 秒刷新；
4. 四个动作同时用鼠标 / 键盘 → 不出现交错乱序（`InputGate`），停止全部 1 秒内退出；
5. 32 个都在跑时再触发一个 → 入队（`MaxQueued` 内）而不是拒；
6. 退出 LTools → 全部取消、按住键被释放、无残留线程卡住进程。

**B4（目标窗口）**
1. 动作写死「目标窗口」后，运行中把前台切到别处 → 按键仍打到目标窗口；
2. 目标窗口被关掉 → 这一步报「未找到」而不是打到别处；
3. 留空 → 与今天一致（当前前台）。

---

## 14. 文档 / 术语同步点（实施时一起改）

| 位置 | 现状 | 改成 |
|---|---|---|
| `CONTEXT.md`「运行」 | 「有单实例保护、停止热键、运行指示器与结果提示」 | 「按 `ActionRun.Mode` 决定独占 / 排队 / 并发；停止热键、运行指示器、结果提示不变」 |
| `CONTEXT.md` §2.2 四个新词 | — | 确认后补进去 |
| `AGENTS.md`「钩子关键约束」 | 运行期让路只写在代码注释里 | 加一条「运行期入口策略由 `RunHookPolicy` 决定；命中动作不吞键但必须提示」 |
| `AGENTS.md`「动作执行链」 | 「单实例保护」 | 「运行注册表 + 准入（独占 / 排队 / 并发），同一动作也可并发，队列里同名只留一条」 |
| `AGENTS.md`「日志系统」 | 失败报告 `logs/last-failure.log` | 加「每运行一份 `logs/failures/<runId>.log`，`last-failure.log` 是最新副本」 |
| `AGENTS.md` docs 列表 | 已有 `RUN_INTEROP.md` | 改成「最终形态」说法 |
| `docs/AUTO_TRIGGER.md:123` | 「`_running` 单实例保护…」 | 指向本文的准入表（自动触发仍不排队） |
| `docs/ACTION_EDITOR_REVIEW.md:138` | 「`AutomationRunner` 单实例」 | 同上 |
| `docs/PROCESS_SPLIT.md:883` | 「执行器一次只接一个运行」 | 改为「按 token 接多个试运行」 |
| `docs/AI_AGENT.md:184` | 「已有单实例互斥…」 | 指向本文 |
| `docs/ACTION_SYSTEM_OPTIMIZATION.md` §6 | 「并行执行」列为非目标 | 已改为「见 `docs/RUN_INTEROP.md`」 |
| `CHANGELOG.md` | — | 每批一条 `[未发布]` |
| `LTools.example.json` | — | `ActionRun` 段 |

---

## 15. 非目标

- **虚拟光标 / 后台注入**（不抢前台就给指定窗口发键）：那要换 UIA / `PostMessage` 那一套，
  与「模拟真实键鼠」是两种语义，不在这里混。
- 跨进程 / 远程的运行注册表、运行列表。
- 断点 / 单步调试（编辑器的试运行与轨迹是另一条线）。
- 自动触发排队（运行中仍丢弃）。
- 按动作配置并发白名单 / 黑名单（上限是全局一个数；要差异化用两个动作实现）。
- 运行状态的持久化（重启不恢复「上次没跑完的动作」）。

---

## 16. 决策记录

| 日期 | 决策 | 理由 |
|---|---|---|
| 2026-11-28 | 四档用**一个枚举** `ActionRun.Mode`，不是三个布尔 | 四档互斥；布尔组合会拼出「排队但不互操作」这种没人要的档 |
| 2026-11-28 | 默认 `concurrent` + `MaxConcurrent = 32` | 用户要的就是「运行中还能用 + 能同时跑多个」；默认等于「不人为限制」，要收敛就往下调 |
| 2026-11-28 | 上限 **32** 是常量，不是结构 | 注入被串行化后更多并发只增加线程与轨迹；要更高改 `MaxConcurrentCeiling` 一行 |
| 2026-11-28 | **同一动作允许并发**（初稿曾定「不并发」，同日按用户要求改） | 技术上只需把状态按运行隔离，同动作与不同动作没有本质差别；防误触靠「队列同名只留一条」+ `#2` 标签 |
| 2026-11-28 | 被拒时**吞键 + 提示**，不是放行 | 放行会让 `CapsLock+O` 的 `o` 漏进目标程序；「按了没反应」用提示解决，不是用放行 |
| 2026-11-28 | 停止热键 = **停全部** + 清队列 | 与 Quicker `StopAll` 一致；按停止时用户不想先挑一个 |
| 2026-11-28 | `InputGate` 是**原语级**，不是整段运行 | 整段互斥等于没有并发；交错风险只在注入原语 |
| 2026-11-28 | 每个运行一条**专用线程**，不用线程池 | 引擎同步阻塞，32 条 `Task.Run` 会占满线程池并拖慢配置写入 |
| 2026-11-28 | 自动触发运行中仍**丢弃**，不排队 | 后台联动连环触发只会更难排查（`AUTO_TRIGGER.md` 既有结论） |
| 2026-11-28 | 试运行（编辑器 / MCP / 子动作）**不排队**，忙时直接拒 + 整段独占输入 | 调试不该挤掉用户动作；独占避免与用户动作交错 |
| 2026-11-28 | 失败报告**每运行一份**，`last-failure.log` 只是最新副本 | 并发时一个文件必然互相覆盖，toast 会指错报告 |
| 2026-11-28 | 交互窗口**串行**显示 | 两个模态框同时出现，用户分不清归属 |

---

## 17. 实施记录

| 批次 | 日期 | 提交 | 结果 |
|---|---|---|---|
| B1 运行内核 | 2026-11-28 | 本轮提交 | **完成**。新增 `Core/Running/ActionRunMode.cs`（枚举 / `RunSubmission` / `RunLimits` / `ActionRunConfig`）、`Core/Running/RunAdmission.cs`、`Core/Running/RunQueue.cs`、`Core/InputGate.cs`、`Features/Running/{ActionRunScope,ActionRunSession,RunningActions,RunCoordinator}.cs`；`AutomationRunner` 按会话重写（`RunCore(ActionRunSession)`，无全局锁）；`ActionExecutor.RunRef` 返回 `RunSubmission`；隔离 11 项落地（截图清空只在 Count==0、每运行 `Hold`、`AsyncLocal` runId、`FailureLog` 每运行一份、toast `run:<runId>`、`#2` 副本号、`NextShotPath` 锁 + `CreateNew`、速记追加锁、`KeyWaiter` 多等待者）；`InputGate` 接进 `input.mouse` / `input.sendKeys` / `text.send` / 剪贴板四个单元 / `MacroPlayer` / 试运行整段独占；退出走 `CancelAllAndRelease`。验收：构建 0 错（9 处既有 `CS8604` 未增）、`--selftest` 454 例 / 19,445 断言全绿（新增 `runAdmission` / `runCore` / `runQueue` / `runGate` / `runLog` / `runKeyWaiter` 共 17 例）、`tests/RunConcurrencySmoke` 34 条全绿、`tests/LinkSmoke` 112 条全绿。默认档仍是 `exclusive`（行为与之前一致），B2 才切。 |
| B2 钩子与入口 | 2026-11-28 | 本轮提交 | **完成**。`Features/RunHookPolicy.cs`（四函数纯判定）+ `Features/StopKeyRouter.cs`（动作 / 宏共用一份吞键状态，删掉两边各存的 `_stopKeySwallowed`）；`KeyboardHook` 去掉「运行中整段让路」、只留回退档分支，`KeyWaiter` 提到 CapsLock 之前；`CapsLockStateMachine.TryHandle(..., tapEsc)` 运行中不补 Esc；`MouseHook` 不再整段让路；设置页「通用」加「动作运行时」卡（档位 + 两个上限，只写 settings 域并立刻刷 `AppState.ActionRun`），链路 `SettingsFile.ActionRun` → `AppConfig` / `ConfigBundle` / `AppState.ApplySettings` / `LTools.example.json`；「被拒 / 已排队」提示按快捷键来源 1 秒节流。验收：`runHook` 组 3 例绿。 |
| B3 并发与可观测 | 2026-11-28 | 本轮提交 | **完成**。默认档 `concurrent`（`MaxConcurrent = 32` / `MaxQueued = 8`）；指示器改成**运行栈**（最多 4 行 + 每行 ✕ + 「还有 N 个 · 全部…」，内容来自注册表 500ms 刷，自动化态去掉 `WS_EX_TRANSPARENT` 以便点 ✕）；新增 `Views/RunningActionsWindow`（逐项停 / 全部停 / 运行历史，1 秒刷新）与托盘项「运行中的动作…（N）」（N=0 置灰）、托盘提示「正在运行 N 个动作」（看门狗 2 秒刷）；停止热键提示「已停止 N 个 · 清空队列 M 项」。§10 收口：**不加** `runs.list` / `runs.stop` —— 窗口、指示器、托盘都在执行器，直接读注册表。验收：`RunConcurrencySmoke` 39 条绿（含默认档 + 托盘计数）。 |
| B4 目标窗口 + 收口 | 2026-11-28 | 本轮提交 | **完成**。`Features/RunTarget.cs` + `input.sendKeys` / `text.send` / `input.mouse` 的「目标窗口」参数（窗口标题包含匹配，填了就 `ActivateNoWait` + 300ms 短轮询确认，找不到报「未找到」、切不过去就失败而不是打到别处）；运行开始日志记「前台：进程 · 标题」；交互框串行落地（`UiDialogHost` 的闸 + `ActionParams` 的参数框闸，排队标题加「（等前一个输入框）」，等待期间可取消）；试运行「停止」只 `CancelTrials`，`RunCore` 的 `finally` 保证结果回调每次都发（修掉跨进程残留 token 的隐患）。验收：`runTarget` / `runObservability` 组绿，全量 `--selftest` 459 例 / 19,479 断言绿，`LinkSmoke` 112 条绿。 |

**B1 落地时顺手修掉的两个既有隐患**：① 试运行的结果回调以前只在「引擎正常返回」时发，参数框取消 / 引擎之外抛异常时编辑器永远停在「运行中」、跨进程那侧还会残留 token 让后续试运行被判「已有动作运行」——现在 `RunCore` 的 `finally` 统一补发（`doneSent` 只发一次）；② 编辑器「停止」/ 关窗以前调 `AutomationRunner.Stop()`（= 停全部），现在只 `RunCoordinator.CancelTrials()`，不连带停用户动作。

**本轮自动化验收（2026-11-28）**：`--selftest` 459 例 / 19,479 断言全绿（`run` 组 22 例）、`tests/RunConcurrencySmoke` 39 条绿、`tests/LinkSmoke` 112 条绿、Debug 与 Release 构建 0 错（9 处既有 `CS8604` 未增）、真机启动一次（钩子装好、托盘菜单含新项、无异常日志）。

**留给真机清单（注入事件验不出来，只能手按，见 §13.1）**：运行中开面板 / 速记、第二个动作被拒的提示与吞键、运行中轻按 CapsLock 不补 Esc、停止热键与 `input.waitKey`、指示器 ✕ 与托盘计数的实际点击、四档切换、目标窗口参数在实际程序上的切换与失败文案。
