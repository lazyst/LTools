# 线程模型与输入链路（2026-11 阶段 2）

> 本文回答三件事：钩子回调现在跑在哪条线程、为什么改、以及「设置窗口独立 UI 线程 / 进程」为什么暂不做、什么条件下才做。
>
> 涉及的内部机制一律用代码标识符指代（`HookDispatcher` / `HookDispatcher.Post` / `Ui.Post` / `HookMirror`），**不另造中文简称**：`CONTEXT.md` 是产品术语的唯一来源，线程模型属于实现约束，不新增产品词。实现约束与踩过的坑另见 `AGENTS.md` 的「架构」「钩子关键约束」。

---

## 1. 现状（阶段 2 起）

两条 STA 线程，职责严格分开：

| 线程 | 消息泵 | 负责 |
| --- | --- | --- |
| 主 UI 线程 | WPF Dispatcher | 全部 WPF 窗口 / 菜单 / Popup、托盘、动作库读写、配置落盘、`ForegroundAppCache` 通知的分发、看门狗 `CapsLockStateMachine.WatchdogTick` |
| `HookDispatcher` 线程 | 纯 Win32 `GetMessage` / `TranslateMessage` / `DispatchMessage` | 安装 / 卸载 `WH_KEYBOARD_LL` / `WH_MOUSE_LL`、派发两个 `HookCallback`、同步吞 / 放判定 |

`WH_*_LL` 的回调由**安装它的线程**的消息循环派发。以前钩子装在主 UI 线程上，回调和 WPF 渲染 / 切页挤在同一个 Dispatcher 队列里：设置窗口首帧渲染、切页这类同步重活会把输入消息顶在队列里。现在把「安装 + 回调派发」整体搬到 `HookDispatcher`，它只有纯 Win32 泵，队列里不再有 WPF 的活。

### 1.1 三条跨线程通道

- **`HookDispatcher.Post(Action)`（UI → 钩子）**：`ConcurrentQueue<Action>` + `PostThreadMessage(WmAppHookCommand)` 唤醒泵。命令与回调在同一条线程上串行执行，所以改「只属于钩子线程」的状态（`SetWindowPickerActive` / `CancelPendingGesture` / 长按计时成立）不需要加锁。线程启动时先 `PeekMessage(PM_NOREMOVE)` 强制建消息队列，否则 `PostThreadMessage` 会失败；卸载也必须在安装线程，泵收尾（`WM_QUIT`）时执行。
- **`Ui.Post(Action)`（钩子 → UI）**：`Dispatcher.BeginInvoke`，**只排队、不等待**。回调里绝不 `Invoke` / `Join` / 等 UI 线程，否则输入延迟又绑回「UI 忙不忙」。已在 UI 线程时同步执行，保持 UI 代码自己调 `Post` 的直觉语义；`Ui.Initialize` 在 `App.OnStartup` 最前面调一次。
- **`HookMirror`（UI → 钩子的只读状态）**：主 UI 线程在状态变化时 `PublishPanel` / `PublishFloat` 整份替换不可变快照，钩子线程 `Volatile.Read` 一次拿引用后只做纯字段 / 数组遍历。**回调里绝不碰任何 WPF 对象**（跨线程访问窗口 / 菜单 / Popup 会抛「另一个线程拥有该对象」）。面板与悬浮各一份独立快照，互不覆盖；面板窗口矩形不进快照，只存 HWND，判「点外部」时钩子线程自己 `GetWindowRect`（纯 Win32），窗口移动 / 缩放自动反映。

### 1.2 状态归属

| 状态 | 所有者 | 同步方式 |
| --- | --- | --- |
| `AppState.SwallowedVks`、CapsLock 手势态、`HotkeyRegistry.Fired` 锁存 | `HookDispatcher` 线程独占 | 无需并发容器 |
| 长按计时 / 手势 pending / 点选器让路 | `HookDispatcher` 线程 | `HookDispatcher.Post` 串到同一线程 |
| 面板 / 悬浮是否打开、钉住、交互中、菜单矩形 | 主 UI 线程写，`HookMirror` 发布 | `Volatile.Write/Read` 整份替换 |
| `AppState` 标量、`HotkeyRegistry` 的 `_file/_entries/_suspended`、`ActionRegistry` | 跨线程共享 | `volatile` / `ConcurrentDictionary` |
| `MacroRecorder` 的录制状态与事件列表 | 双线程（开始 / 停止可能由 UI 发起，观察由钩子发起） | `Gate` 锁；WPF 副作用与生命周期事件走 `Ui.Post` |

`MouseHook` 的长按计时器从 `DispatcherTimer` 改成 `System.Threading.Timer`——钩子线程没有 WPF Dispatcher 泵。看门狗仍留在主 UI 线程，它不属于输入链路。

### 1.3 镜像推送点

`HookMirror.Panel` 由 `Features.SuperPanel.PublishMirror()` 统一发布：`Show` / `Close` / `Activated` / `Deactivated` / `Closed`，加上窗口内 `SetPinned`（工具栏与右键菜单「钉住面板」两个入口）、菜单 `Opened` / `Closed` / `SubmenuOpened` / `SubmenuClosed`、手动关菜单。菜单矩形要等 Popup HWND 建好才能量，所以同步推一次让 `Interacting` 立刻生效、再 `DispatcherPriority.Background` 补一次矩形。

`HookMirror.Float` 由 `FloatingManager.PublishMirror()` 统一发布：窗口增删 / `Shutdown` / `RefreshAll` / `CloseOpenMenu` / `CloseMenuIfOutside` / 拖动合并，加上 `FloatWindow` 菜单开合与子菜单展开 / 收起。

**新增任何会改「面板 / 悬浮可见状态、菜单矩形、钉住态」的入口，都要补一次对应的 Publish**，否则钩子拿旧快照判错（点了菜单却被当点外部、钉住后 Esc 仍关面板）。

---

## 2. 为什么改（证据与门槛）

REA 结论：**不是框架差异，也不是 Quicker 干得少**。Quicker 的 `Quicker.PInvoke.dll` 暴露 `SetWindowsHookEx` / `CallNextHookEx` / `UnhookWindowsHookEx` / `GetMessageW` / `PeekMessageW` / `MsgWaitForMultipleObjectsEx` / `SendInput` 边界，日志里有 4 行 `[HookDispatcher]`、142 条 `[PERF-settings-page] Editor switched`——切页本身也有开销（`Reused=False` 133 条 avg 32.2ms / max 173ms，`CreateMs` max 166ms），但输入链路有独立线程 / 自有消息泵，切页的同步重活不会顶住输入。LTools 的全局低级键鼠钩子与 WPF UI 共用同一 STA 泵，这才是差异。

静态证据边界：`Quicker.dll` 因托管元数据超 64 MB 预算只能 partial 分析、React 前端语义分析超时、Quicker 运行时未启动，**没有「某条线程装了钩子」的直接运行时证据**；结论由 P/Invoke 边界 + Quicker 自身日志 + LTools 侧量化数据三方支撑。

阶段 0 门槛（`--perf`，有效基线 `cache/perf/perf-20261112-120908.json`）：开窗相位的输入延迟尖峰对齐 `open:total`，切页 `max 297ms` 已逼近系统自动摘钩的 300ms；而 `ctor:total` p50 只有 23.28ms，说明 `Show()` 首帧渲染是大头。→ 门槛通过，才动的线程架构（用户要求「允许改线程，但要先量化确认」）。

`perf-20261112-120609.json` 作废（`hookLatencyMs: []`，`dwExtraInfo` 归因不可靠），不要拿来对比。

---

## 3. 同尺复测数据

三份都用同一条命令、同一套探针：`bin\Debug\net10.0-windows10.0.19041.0\LTools.exe --perf`（自动开窗 / 切页 / 注入探针 / 导出 / 退出）。探针用 `MSLLHOOKSTRUCT.time` 与进入回调时的 `Environment.TickCount` 求差，按事件自带的 `time` 落在哪个相位窗口归因；`hookEventsSeen` 是钩子回调里的 `Interlocked.Increment`，用来排除「钩子没装上的假 0」。

| 指标 | 阶段 0 基线 | 阶段 1（同线程减负后） | 阶段 2（专用线程后） |
| --- | --- | --- | --- |
| 结果文件（`-20261112-`） | `120908` | `122512` | `130313` |
| `hookEventsSeen` | 1866 | 1873 | 2224 |
| 相位 1（开窗）p95 / p99 / max | 141 / 172 / 234 ms | 78 / 110 / 172 ms | **0 / 0 / 15 ms** |
| 相位 2（切页）p95 / p99 / max | 16 / 78 / 297 ms | 15 / 63 / 281 ms | **0 / 0 / 0 ms** |
| `open:total` p50 / p95 / max | 206.02 / 286.07 / 463.79 ms | 93.7 / 128.09 / 289.84 ms | 90.8 / 132.74 / 311.54 ms |
| `ctor:total` p50 | 23.28 ms | 14.13 ms | 14.21 ms |

读法：阶段 1 把开窗从 ~206ms 降到 ~94ms，但切页 `max 281ms` 仍贴近摘钩阈值，所以没有停在那里；阶段 2 之后**开窗 / 切页的 UI 耗时几乎没变（`open:total` 仍 ~91ms、max 311ms），输入延迟却整体归零**。这正说明两件事已经解耦：UI 该慢还是慢，但不再顶住输入。

结果都在 `cache/perf/`（运行缓存，不进备份）。探针默认关闭，只有 `--perf` / `LTOOLS_PERF=1` 启用，关闭时打点只有一次 volatile 读。

---

## 4. 阶段 3 决策：独立 UI 线程 / 进程暂不实施

**结论：不做。** 用户批准的 C 路线要求「阶段 2 后用同一把尺子复测，再决定是否需要后续按需架构改造」；复测结论是输入延迟已与 UI 开窗 / 切页解耦，阶段 3 要解决的痛点（输入被 UI 顶住）已经消失。

### 4.1 取舍

| 方案 | 能解决什么 | 代价 | 结论 |
| --- | --- | --- | --- |
| 现状（钩子专用线程 + 主 UI 线程） | 输入不再被 UI 顶住 | 无新增 | ✅ 采用 |
| 设置窗口独立 UI 线程 | `open:total` / 切页不再占主 UI 线程 | 第二套 Dispatcher + 资源字典 + 窗口所有权；托盘 `H.NotifyIcon.Wpf`、模态 Owner、剪贴板 STA、PerMonitorV2 DPI、`WindowSizing`、Toast / 悬浮置顶顺序都要重新验 | ⛔ 收益不对齐痛点 |
| 设置窗口独立进程 | 隔离最彻底，主进程 UI 完全不参与 | 跨进程 IPC、配置写入锁要在进程间重划、动作库 / 托盘 / 自动触发所有权重定、单实例互斥体语义变化 | ⛔ 成本最高且无必要 |

注意：若日后诉求是「设置窗口自己打开慢 / 切页卡」（`open:total` ~91ms、max 311ms 的**首帧渲染**），那是 UI 性能优化议题，不是输入链路问题，应另立议题，别借阶段 3 的名义动线程模型。

### 4.2 触发条件（任一成立才重新评估）

1. 同一 `--perf` 复测里，任一相位的 `hookLatencyMs` **p99 > 100ms 或 max ≥ 300ms**（摘钩阈值），且 `hookEventsSeen` 正常（排除钩子没装）。
2. 钩子回调里新增了无法挪走的同步重活（单次 > 50ms，且已确认不能走 `HookDispatcher.Post` / 后台线程），导致 `HookDispatcher` 自己的泵被顶住。
3. 真机出现「开窗 / 切页时输入肉眼可见卡顿」且 `--perf` 能复现相位尖峰。
4. 用户明确提出设置窗口要与主窗口物理隔离（不是因为输入卡顿）。

触发后先重跑阶段 1 那类「同线程减负」（惰性建页、只刷当前页、后台落盘），确认仍不达标，再评估独立 UI 线程 / 进程；**不要直接跳到独立进程**。

### 4.3 若真要实施，必须先补的地基

- 每条 UI 线程各自 `Dispatcher` + `Themes/Modern.xaml` 实例，明确 `Application.Current` 不再唯一。
- 托盘与 `SuperPanel` / `FloatWindow` / `ToastWindow` 的 `Topmost` 层级、`FloatingManager.RaiseAllAbove()` 跨线程调用改成消息传递。
- `Ui.Post` 要变成「按窗口找目标 Dispatcher」，不能只有一个全局 `_dispatcher`。
- 下节回归清单全部重跑，外加：模态 Owner 跨线程、剪贴板 STA 套间、多显示器 DPI、退出时各线程泵收尾顺序。

---

## 5. 回归清单

构建与自动复测能覆盖的：`dotnet build -p:NoWin32Manifest=true` 0 错 0 警、`git diff --check`、`--perf` 同尺三相位、日志里两条「键盘 / 鼠标钩子已安装（HookDispatcher）」、无 `ERROR` / `Exception` / 跨线程异常、无新 CrashLog。

**必须由真机点一遍**（助手无法代替真实输入验收）：

- 裸右键长按唤起面板、短按还原原生右键菜单、点外部关闭、钉住后点外部不关
- CapsLock 轻按发 Esc、按住当修饰键、`Ctrl+CapsLock` 切灯、按住期间按别的键不补 Esc
- 单键热键的「已触发」锁存只靠 keyup 清（`HotkeyRegistry.KeyUp` 仍须排在所有提前 return 分支之前）
- 宏录制期间钩子透明（录到的输入 = 目标应用实际收到的输入）、回放停止热键、达到事件上限自动停
- 点击窗口选取期间全局鼠标钩子让路（右键 / Esc /「✕ 取消」一次生效）
- 模态编辑器 / 对话框的 Owner 与置顶、隐藏面板不闪黑窗
- 浮窗不抢焦点、点一下不把注入的键鼠打在自己身上
- 托盘左键双击分发 6 种行为、右键菜单与状态点列
- 多显示器 DPI：面板定位、悬浮贴边吸附、点选器与框选的坐标换算
- 从托盘「退出 LTools」不卡死（钩子线程 `Stop()` 绝不能在调用线程上等关停）

---

## 6. 与历史文档的关系

`docs/ACTION_SYSTEM_PLAN.md` 与 `docs/WPF_MIGRATION_PLAN.md` 是阶段方案 / 迁移历史记录，其中「钩子回调在主 UI 线程派发、与 `DispatcherTimer` 同线程、无需改线程模型」等结论记录的是**阶段 2 之前**的模型，不要照抄进新代码。本文是当前口径；`docs/ARCHITECTURE.md` §6 与 `AGENTS.md` 的「架构 / 钩子关键约束」是同步更新后的入口。
