# CapsLock-Pro 动作系统设计计划

> **状态**：原动作系统（阶段 1–8）已实施完毕；**新增阶段 9–11（基础动作：模拟按键 + 发送文本）已评审、实施中**——规格 §3.2、计划与进度 §11、决策 §12。§12 为决策汇总；§11 为分阶段实施计划（`- [ ]` 可标记进度，每阶段附验收标准）。
>
> 背景：引入统一 **Action** 抽象，让「超级面板」（裸右键长按）与「CapsLock+数字菜单」共用一个全局动作池。参考 Quicker 的面板/动作模型，但精简为个人工具所需的范围。

---

## 1. 目标与范围

### 在范围内
- 统一 Action 数据模型（全局清单 + 稳定 Id，两个面板引用）。
- 超级面板：裸右键长按手势唤起、3×3 网格、多页。
- CapsLock+数字菜单改造为动作引用。
- 动作编辑器（设置面板与超级面板两处共用）。
- 组合动作（多步、可设延迟、失败策略、可引用其他组合）。
- 内部动作（绑定速记/搜索/放大镜等内置功能）。

### 不在范围内（v1）
- 脚本动作（C#/JS）、条件分支、循环——Quicker 有，本工具不做。
- 按当前激活窗口自动切换面板（上下文面板）——以后再考虑。

---

## 2. 核心抽象：Action ✅

全局动作清单，每个动作带**稳定 Id**。超级面板格子、CapsLock+数字菜单项都只存「动作 Id 引用」，不内联动作数据。改一处全局生效，组合动作引用也稳定。

```
ActionDto {
  Id    : string   // 稳定标识，如 "a1"、"cmd1"
  Name  : string   // 显示名
  Icon  : string?  // 内置图标 Id（见 §3.1），可为空
  Type  : ActionType
  ...类型特定字段
}
```

动作类型（discriminated union）见 §3。

---

## 3. 动作类型

| Type | 字段 | 执行 |
|---|---|---|
| `launchApp` | `Target`(exe), `Args?`, `Workdir?` | `Process.Start(target, args, workdir)` |
| `openFile` | `Path` | ShellExecute(path)（默认程序打开） |
| `openFolder` | `Path` | ShellExecute(folder)（资源管理器） |
| `openUrl` | `Url` | ShellExecute(url)（默认浏览器） |
| `runCommand` | `Cmd`, `Terminal`, `KeepWindow`, `Workdir` | 复用现有 `TerminalLauncher.TryBuildLaunch` |
| `internal` | `Command`(枚举) | 内部注册表分发（见 §7） |
| `composite` | `Steps[]`（见 §4） | 顺序执行各步 |
| `sendKeys` ✅ | `Items[]`（chord/text/sleep 序列，见 §3.2） | 逐条目 SendInput；前置 ~50ms 焦点延时 |
| `sendText` ✅ | `Text`, `Mode`, `AppendEnter`（见 §3.2） | auto=Unicode 注入（绕过输入法）/ type=键入 / paste=剪贴板（150ms 恢复） |

> 🟨 = 阶段 9–11 待实施（规格 §3.2，计划 §11）。
>
> **执行层洞察**：`launchApp`/`openFile`/`openFolder`/`openUrl` 本质都是 `Terminal=direct`（ShellExecute）的特例——现有 `TerminalLauncher` 的 `direct` 分支已能处理。只有 `runCommand` 才需要终端路由。故执行引擎基本就绪，主要工作是类型模型 + 编辑器 + 面板 UI。

### 3.1 内置图标 ✅

- v1 **提供一组内置图标**供选择（不是让用户自备图标文件）——编辑器里以图标网格展示，动作存图标 Id。
- 实现：用 Windows 自带的 **Segoe MDL2 Assets** 字体（Win10/11 均可用），内置一个「名称 → 字形码点」的 `IconCatalog`（如 `settings` → `\uE713`），格子按名称取字形渲染。
- 无需打包图标资源文件；后续若要更丰富可改用 Segoe Fluent Icons（Win11）。

### 3.2 新基础动作：模拟按键 `sendKeys` + 发送文本 `sendText` ✅

> 灵感来自 Quicker 基础动作（[模拟按键](https://getquicker.net/KC/Manual/Doc/keyboard-input) / [发送文本](https://getquicker.net/KC/Manual/Doc/send-text)）。需求已与用户逐项评审（§12「阶段 9–11 需求评审」），**已实施完毕**。

#### 3.2.1 `sendKeys`（模拟按键）——Quicker 式动作内含序列

一个动作 = **有序输入条目列表**（`ActionDto.Items`），条目三选一：

```
KeyItem {
  Kind : "chord" | "text" | "sleep"
  // chord：按键序列，按住修饰键依次发主键（表达 Ctrl+K,D / 菜单流 Alt+P → S → P）
  Strokes : List<string>     // 每 stroke = "ctrl+k"、"alt+p"、"s"、"f5" 等（修饰键组+主键，或无修饰单键）
  // text：内联短文本条目（表单填充中一格）；执行复用 sendText 的自动逻辑（§3.2.2）
  Text    : string
  // sleep：延时等待
  Ms      : int
}
```

- **录入 = 录制 + 手动添加**：
  - 「● 开始录制」→ 监听键盘，边按边进列表。**实现走编辑器窗口 `PreviewKeyDown`**（模态窗口有焦点），**不开新低级钩子**——不碰「钩子回调」红线。
  - 修饰键 KeyDown 不产生条目；主键 KeyDown 结合当时修饰键状态生成一个 stroke。
  - **空闲 800ms** → 当前条目自动完成入库，继续监听（可连录多条）；「停止录制」或 **Esc** 退出。
  - 录制模式下窗口内所有键 `Handled=true`（不触发对话框按钮/焦点切换）。
  - 「＋ 添加」手动补特殊键（F13、菜单键 `VK_APPS`、`Pause` 等录制不到或会误触发的键）。
  - 录制时 CapsLock 组合仍被全局钩子吞掉不进录制——本来也不该录入，视为正确行为（§12）。
- **执行**（`ActionExecutor`，后台线程内，合规）：
  - 每 stroke 用 `InputHelper.Combo` / `Tap`（扫描码、单次 SendInput 批）。
  - **stroke 间固定 ~15ms**（菜单流需应用响应时间；更大间隔用 `sleep` 条目）。
  - `text` 条目复用 `sendText` 自动逻辑；`sleep` = `Thread.Sleep`。
  - **动作开头内置 ~50ms 焦点延时**（面板/菜单关闭 → 目标窗口焦点切回有竞态，见 §12）。

#### 3.2.2 `sendText`（发送文本）

`ActionDto` 新增字段：

| 字段 | 说明 |
|---|---|
| `Text` | 多行文本内容 |
| `Mode` | `"auto"`（默认）/ `"type"`（强制键入）/ `"paste"`（强制剪贴板粘贴） |
| `AppendEnter` | 末尾追加回车（Quicker「在末尾添加回车」，聊天软件直接发出） |

**auto 逻辑**（§12「输入法干扰修复」修订）：**`KEYEVENTF_UNICODE` 批量注入**（`InputHelper.SendUnicodeText`，直接生成 `WM_CHAR`）——绕过键盘布局与输入法，中英文/emoji/`\n` 均可发，**不碰剪贴板**。

- **type（键入式）**：`VkKeyScanW` 取键 + `SendInput`，真实模拟按键，**受输入法影响**（用户明确要求时才用）；遇不可映射字符预检报错（§12）。`\n` 转 Enter、`\t` 转 Tab。
- **paste（粘贴式）**：写剪贴板 → Ctrl+V → **150ms 后恢复原剪贴板文本**（仅当剪贴板仍是写入内容、用户未新复制时才恢复；非文本内容如图片无法经文本接口恢复）。修复「粘贴残留弄乱复制历史」。
- **初版 auto 已废弃**：纯 ASCII ≤100 键入、否则粘贴的判定——键入受中文输入法干扰（Bug），且判定含糊。现 auto 恒走 Unicode 注入。
- 同样内置 ~50ms 焦点延时。

#### 3.2.3 集成点清单

| 位置 | 改动 |
|---|---|
| `ActionType` 枚举 + `ActionTypeLabel` | `sendKeys`「模拟按键」、`sendText`「发送文本」 |
| `ActionDto` | 新增 `Items` / `Text` / `Mode` / `AppendEnter` + `Clone()` 同步 |
| `ActionExecutor` | 两个分发分支（后台线程内执行） |
| `ActionEditorDialog` | 类型下拉 +2、字段面板 +2（`sendKeys` 含序列列表 + 录制条） |
| `CompositeActionDialog` | 左栏「基础动作」组 6→8 种 |
| `IconCatalog` | 两个新图标（如 `⌨` / 文档字形） |

---

## 4. 组合动作 ✅

`composite` = 有序步骤列表，每步**内嵌一个动作快照**（深拷贝，与动作池解耦）。

```
Step {
  Action   : ActionDto       // 内嵌动作快照（禁止为 composite，组合不支持嵌套）
  DelayMs  : int = 0         // 执行本步前等待的毫秒数
  OnFail   : "continue" | "abort"   // 本步失败后的策略
}
```

已定语义：
- ✅ 步骤间**可设延迟**（`DelayMs`，执行该步前等待）。
- ✅ 失败**记录日志**（写入 `CrashLog`，含动作名/步骤序号/异常），用户可在日志中看到哪一步失败。
- ✅ **用户可选择**失败后策略：继续 or 中止——`OnFail` 为**每步**可配（默认 `continue`），组合可有整体默认。
- ✅ **动作解耦（§12）**：步骤**内嵌动作快照**（`Step.Action`），不再以 `ActionId` 引用动作池。复用已有动作 = 复制那一刻的快照；编辑/删除池动作**不影响**已复制的步骤，删池动作**不破坏**组合。每步是私有副本，编辑只影响本步。
- ⛔ **禁止嵌套**：步骤内嵌动作**不得为 composite**（编辑器左栏排除组合动作、基础动作组开的编辑器为 `allowComposite:false`；执行器对畸形配置跳过 + 记日志）。删除原「防环」整套（`compositeStack` 三层传递），无嵌套即无环。

❓ 待确认：组合动作执行是否在后台线程（避免阻塞钩子 >300ms）？——建议是，与现有动作执行一致。

---

## 5. 超级面板

### 5.1 唤起手势与选择方式 ✅

- **触发**：**裸右键长按**（CapsLock **未按下**）。
- **短按穿透**：右键 down 起计时并**吞掉**；若 down→up 间隔 < 阈值 → 停表并**注入一次原生右键（down+up）**，原生菜单照常弹出（注入事件带 `LLMHF_INJECTED`，被本钩子开头忽略）。
- **长按弹面板**：超过阈值且右键仍未抬起 → 在光标处弹出超级面板，并**吞掉随后的右键 up**（阻止原生菜单），面板保留。
- **为何吞 down 而非放行**（实现期修正）：若放行 down 却吞 up，目标窗口收到 `WM_RBUTTONDOWN` 却收不到 up，鼠标捕获不释放 → 右键「卡住」（须再点一次右键才解除）。故必须**整组吞 down/up**，短按靠注入还原原生菜单。
- **阈值**：默认 **250ms**，设置可调（100–800ms）。
- **选择方式 = 点击执行**（Quicker「弹出面板」模型，**非「轮盘」模型**）：面板出现后，**左键单击格子执行**；**右键仅用于唤起面板**，不参与选择；点空白或 Esc 关闭。手势**不追踪松开位置**。
- **单独全局开关**：独立于 `CapsLock+Esc` 总开关；不想要的人可彻底关闭，避免「劫持右键」。

### 5.2 与 CapsLock+右键（置顶）的优先级 ✅（我来定）

钩子判定顺序（无重叠，互不影响）：
1. 右键 down 时**先判 CapsLock 是否按下**：
   - **按下** → 走现有「窗口置顶」逻辑（`WindowPin`），不进入长按检测。
   - **未按下** → 进入超级面板长按检测（若开关开）。
2. 超级面板开关关闭时，裸右键完全放行，原生行为不受影响。

### 5.3 布局与分页 ✅

- 每页 **3×3 = 9 格**，**支持多页**。
- **滚轮翻页**（对齐 Quicker）；**1~9 键**选中本页对应格；Esc / **点外部关闭（左键或右键均可，见 §12）**。
- **常驻页数指示**：面板上始终显示「当前页/总页数」（如 `1/3`）。
- **翻页过渡动画**：旧页截图与新页**同速同曲线**横滑（`QuadraticEase.EaseOut`，200ms），
  两者几何上严格相邻（同一条竖缝扫过，**无重叠**），观感为整块 3×3 向左/右推出。
  `delta>0`（下一页）= 新页自右滑入、旧页向左滑出，`delta<0` 反之，方向与滚轮一致。
  - 实现：格子区外包一层 `ClipToBounds=True` 的 Grid（含 `PageFxHost` 旧页截图层，**位于 CellsHost 之下**），
    `RenderTargetBitmap.Render(CellsHost)` 截旧页（按 DPI 换算像素），旧/新页各挂 `TranslateTransform`。
  - **动画只挂在 `GoPage`**（滚轮 / `‹`/`›` 拖动悬停翻页同走此路径）；`Rebuild`（拖动交换 / CRUD）**不播动画**，
    且 `Rebuild` 内先 `CancelPageFx()` 保证无残留。
  - **拖动中翻页不播动画**（`_dragging` / `_dragSrcPage != null`）：幽灵由 `UpdateGhost` 跟随光标，
    两页横滑会与幽灵叠加；此时退化为原「瞬时切换」。
  - **打断安全**：`_pageAnimToken` 令牌——新一轮 `SnapshotCells()` 先 `CancelPageFx()`（重置回静止再截图），
    作废上一轮 `Completed` 回调；截图异常/尺寸未就绪 → 返回 null → **退化为无动画翻页**，绝不影响翻页本身。
  - ⚠️ **截图 `Image` 必须显式 `Width/Height`**：面板是 `SizeToContent="Height"`，测量链上**高度约束为无穷大**
    （宽度被 `Width=310` 约束），而 `Stretch=Fill` 的语义是「填满约束」→ 回报**无穷高**，
    沿 `Image → PageFxHost → 格子区 Grid → 面板` 把高度撑爆（表现为**翻页动画期间面板突然变得很高**）。
    指定显式尺寸后期望尺寸与约束无关，且与格子区等大、竖缝严格对齐。**同理：此窗口内新增的任何
    `Stretch` 类元素（Fill/Uniform）都须给定显式尺寸，否则会按无穷约束撑高。**

### 5.4 格子交互 ✅（按推荐做法）

| 操作 | 空格子 | 非空格子 |
|---|---|---|
| 左键单击 | 弹菜单（见 §5.5） | 执行该动作；**按住拖动**可重排（见 §5.6） |
| 右键单击 | 弹菜单（见 §5.5） | 编辑 / 删除 / 复制（重排已改用左键拖动） |

> 右键仅在**唤起面板那一刻**被钩子接管；面板打开后右键回归正常，用于格子管理菜单。
> 重排格子用**左键拖动**（见 §5.6），取代原"移动到其他格子"点选式。

### 5.5 空格子的右键菜单 ✅（结构已定；实测改为平铺）

原计划分两组子菜单（不平铺）：

- **新建**
  - 动作…（弹出类型选择器，再填字段）
  - 组合动作…（进入多步编排器）
- **快捷新建**（直接选定类型，跳过类型选择）
  - 启动软件
  - 打开文件
  - 打开文件夹
  - 运行命令
  - 打开网址

**实测修正**：WPF 子菜单标题的展开（点击/悬停）在本面板中不可靠（用户直接点"新建"/"快捷新建"
两层标题无反应），故**扁平化为单层直选**，与已填格子菜单同结构——点哪项都直接开对应的新建对话框：

```
新建动作…
新建组合动作…
─────────
新建 · 启动软件
新建 · 打开文件
新建 · 打开文件夹
新建 · 运行命令
新建 · 打开网址
─────────
从动作池选择…      ← 复用已有动作（原本面板缺此能力，须去设置页）
清除（仅非空槽位）
```

**统一实现**：上表结构由 `Features/SlotMenu.BuildAddMenu` 构建，面板空格菜单与设置面板
「超级面板」配置页格子菜单（左键弹出 + 右键 ContextMenu）**共用同一方法**，保证两处
「添加动作」交互完全一致。菜单项 Click 统一经 `owner.Dispatcher.InvokeAsync` 推迟到菜单关闭后执行。

### 5.6 格子拖动重排 ✅（需求已定）

格子可**左键按住拖动**重排，取代原右键"移动到其他格子…"点选式（已移除）。

**触发与点击/拖动区分**
- 左键在格子上按下 → 记录源格（源页=当前页、源格=slot）+ 捕获鼠标到窗口；**不立即执行**。
- 移动位移 > 阈值（约 5px，对齐系统 `SM_CXDRAG`）→ 进入**拖动态**。
- 未超阈值就松开 → 视为**单击**：空格弹菜单 / 非空执行动作（原语义不变）。

**语义：交换**
- 拖 A 到 B → A、B 两格内容**对调**。空格参与交换（拖到空格 = 把源内容移过去，源变空）。
- 拖回原位 / 拖到面板外松手 → **取消**（无变化）。
- 松手立即 `SavePages` 落盘。

**跨页**
- 拖动中拖到 `‹`/`›` 翻页按钮上**悬停 ~0.5s** → 自动翻页（可连续翻多页后再落位）。
- 拖动状态挂窗口级（记住源页+源格）；翻页只改当前显示页；交换时 `源页源格 ↔ 当前页目标格`。
- 翻页 `GoPage`+`Rebuild` 重建格子，但鼠标捕获在窗口上、不打断拖动。

**视觉反馈**
- 拖动态：被拖格内容**半透明跟随光标**（幽灵 overlay，窗口级元素 + RenderTransform 跟随）。
- 悬停的目标格**高亮边框**（`Primary` 色加粗）；离开即还原。
- 拖到 `‹`/`›` 上时按钮高亮提示"将翻页"。

**取消**：拖到面板外松手 = 取消；Esc 取消拖动（不关面板）。

**钩子**：拖动是面板内左键操作，按下发生在面板内 → 点外关闭不触发；拖动中鼠标移出面板，捕获仍路由到窗口，LBUTTONUP 在面板外也能收到 → 取消。无需额外门控钩子。

**设置页对等**：设置面板「超级面板」配置页（`ConfigHelper.BuildSuperPagesUI`）同样支持槽位拖动交换——多页卡片纵向排列，`HitTestSlot` 遍历所有卡片的 3×3 网格命中目标（跨卡片=跨页，无需翻页悬停）；幽灵/高亮/阈值/取消同面板；交换改 `_superPages` 内存态 + `MarkSuperDirty`（800ms 防抖落盘）+ `BuildSuperPagesUI` 刷新。原 `btn.Click→PickSlotAction` 改 `PreviewMouseLeftButtonDown`（吞 Click）+ 释放时位移判定。

---

## 6. CapsLock+数字菜单改造

现有：`CapsLock+1~0` 唤起 10 个菜单组，每组是 `{Name, Cmd, Terminal, KeepWindow, Workdir}` 的列表。

改造后：
- 菜单组 = **动作 Id 引用列表**（`Items: string[]`，元素为动作 Id）。
- 菜单项可以是任意动作类型（含组合动作）。
- 执行走统一 `ActionExecutor`，不再有菜单专属的执行路径。
- 空组（如第 9/10 组）仍发送 `(` / `)` 括号——此行为保留。
- **UI 不改**：保持现有竖向列表与交互，**只改数据层**（项数据源从内联字段改为动作引用）。不与超级面板统一为网格。

---

## 7. 内部动作 ✅

`internal` 类型动作绑定内置功能。`Command` 为枚举，分发到对应功能的 toggle/run 方法。

v1 可绑定的内部命令（基于当前已有功能）：

| Command | 行为 | 对应现有触发 |
|---|---|---|
| `quickNote.toggle` | 打开/关闭速记 | CapsLock+N |
| `quickSearch.run` | 快速搜索选中文本 | CapsLock+Q |
| `magnifier.toggle` | 放大镜开/关 | CapsLock+Tab |
| `windowPin.toggle` | 置顶/取消置顶窗口（用**唤起面板时的光标位置**） | CapsLock+右键 |
| `helpPanel.toggle` | 帮助面板 | CapsLock+\` |
| `settings.toggle` | 设置 | CapsLock+\ |

> `mouseMode`（上下文进入型）、`tool.toggle`（移入设置面板，见 §10.2）、独立剪贴板（不作为可绑动作）v1 不纳入。
> `fileRename`/`volume`/`symbolJump` 是上下文触发型，不适合绑格子，v1 不纳入。
> `windowPin.toggle` 从超级面板执行时，使用**面板唤起瞬间记录的光标位置**（而非点击格子后的位置）。

实现：`InternalActionRegistry`——`Dictionary<string, Action>`，启动时注册各功能的入口方法；`internal` 动作执行时查表调用。`windowPin` 等需要坐标的命令，由面板在唤起时记录光标坐标并传入。

---

## 8. 执行层：ActionExecutor

新建 `Features/ActionExecutor.cs`，统一入口：

```
ActionExecutor.Run(ActionDto action)
  → switch(action.Type)
    → launchApp/openFile/openFolder/openUrl: 走 TerminalLauncher(direct) 或直接 Process.Start/ShellExecute
    → runCommand: TerminalLauncher.TryBuildLaunch(...)
    → internal: InternalActionRegistry[command].Invoke()
    → composite: 顺序执行 Steps（Delay、OnFail；步骤内嵌快照，禁嵌套）
```

- 所有执行 **spawn 到后台线程**（钩子回调不得阻塞 >300ms）。
- 失败统一经 `CrashLog.Write` 记录。
- 现有 `TerminalLauncher` 保留，被 `ActionExecutor` 复用。

---

## 9. 配置 schema ✅（全新，不兼容旧配置）

**无需迁移旧配置**（个人工具，旧 `CapsLock++.json` 直接废弃）。沿用上次的**惰性物化**：example 只放默认动作，用户首次保存才生成 `CapsLock++.json`。

```jsonc
{
  "Actions": [
    { "Id": "a1", "Name": "ipconfig", "Type": "runCommand",
      "Cmd": "ipconfig /all", "Terminal": "pwsh7", "KeepWindow": true, "Workdir": "" },
    { "Id": "a2", "Name": "浏览器", "Type": "openUrl", "Url": "https://example.com" },
    { "Id": "a3", "Name": "速记", "Type": "internal", "Command": "quickNote.toggle" },
    { "Id": "c1", "Name": "查IP并打开浏览器", "Type": "composite",
      "Steps": [
        { "ActionId": "a1", "DelayMs": 0,   "OnFail": "abort" },
        { "ActionId": "a2", "DelayMs": 300, "OnFail": "continue" }
      ] }
  ],
  "SuperPanel": {
    "Enabled": true,
    "LongPressThresholdMs": 250,
    "Pages": [
      ["a1", "a2", "a3", null, null, null, null, null, null],
      ["c1", null, null, null, null, null, null, null, null]
    ]
  },
  "MenuGroups": [
    { "Enabled": true, "Name": "命令", "Items": ["a1", "c1"] },
    { "Enabled": true, "Name": "程序", "Items": ["a2"] },
    null, null, null, null, null, null, null, null
  ],
  "TerminalPaths": {},
  "MouseModeSpeed": 7
}
```

- `Actions`：全局动作清单（含组合）。
- `SuperPanel.Pages`：每页 9 槽，元素为动作 Id 或 `null`。
- `MenuGroups[10]`：每组 `Items` 改为动作 Id 列表（不再内联 Cmd/Terminal）。
- 旧 `CapsLock++.json`（开发机上的）加载时因 schema 不匹配会回退读 example——开发时请手动删除旧文件。

---

## 10. 编辑器与设置面板 ✅

### 10.1 编辑器组件

**两个独立窗口**（`Views/`），由统一分发入口 `ActionEditor.Show(owner, title, dto)` 按 `dto.Type` 选择：

- **ActionEditorDialog**（普通动作）：类型下拉（6 类，含「组合动作 ▸」跳转项）+ 类型特定字段 + 名称 + 图标选择器。编辑组合步骤时传 `allowComposite:false`（不含跳转项，从源头禁嵌套）。
- **CompositeActionDialog**（组合动作）：三栏布局——
  - **左**「动作来源」：基础动作（6 种类型，开预置类型的编辑器）+ 内部动作（6 内置命令，内联 internal 快照）+ 动作池（**排除组合动作**，深拷贝快照）+ 搜索框。**双击追加 / 拖入中间区插入**。
  - **中**「步骤编排」：步骤列表（类型徽标 + 名称 + 延迟 + 失败策略 + 把手拖拽排序 + 删除）；**双击就地编辑**（只改本步快照）。
  - **右**「组合属性」：名称 + 图标 + 「任一步失败即中止」。
- **两窗口可互相跳转（不带数据）**：普通编辑器选「组合动作 ▸」→ 关闭并以新草稿开组合窗口；组合窗口点「改为普通动作…」→ 反向。由 `ActionEditor.Show` 的 while 循环承载（跳转标志置位即换一种编辑器再开一轮，结果透传给最初调用方）。
- 所有调用点（设置页 / 超级面板 / 动作池选择器）一律走 `ActionEditor.Show`，无需自行判断类型。
- 已废弃 `CompositeEditorDialog`（能力并入 `CompositeActionDialog`）。

### 10.2 设置面板重构

现有设置窗口（`Views/ConfigHelper.xaml`，TabControl 双标签）改为**左侧导航栏 + 右侧主页面**布局，承载动作系统新增的管理页：

| 导航页 | 内容 |
|---|---|
| 动作管理 | 全局动作清单（增/编辑/复制/删除），调 §10.1 编辑器 |
| 菜单组 | 10 组，每组 Items 从动作池选引用（替代旧的内联字段编辑） |
| 超级面板 | 开关、长按阈值、多页×9 槽，每槽选动作引用 |
| 终端路径 | 现有，保留 |
| 速记路径 | 现有，保留 |
| 通用 | **工具开关（CapsLock++ 启用/禁用，原 CapsLock+Esc）**、开机自启等 |

- **工具开关**从「内部动作」移入设置面板「通用」页（不再可绑格子）；`CapsLock+Esc` 手势仍保留。
- 沿用 v2.1.0 的防抖自动保存，扩展到动作/面板编辑。

---

## 11. 实施计划

> **总体进度：11 / 11 阶段**（阶段 6–8 已完成、记录在 §12；阶段 9–11 新基础动作 sendText/sendKeys 已完成，待人工实测四入口触发）
>
> 标记约定：`- [ ]` 未开始 · `- [x]` 完成。每个阶段结束须满足该阶段「验收标准」且 `dotnet build` **0 错 0 警**方可进入下一阶段。每阶段独立可运行、可测。

### 阶段 1 — Action 数据模型 + 执行层

**目标**：让「一个动作能跑起来」，无 UI。

- [x] 定义动作类型模型（`Features/ActionTypes.cs`）：`ActionType` 枚举、`ActionDto`、`StepDto`，字段见 §2/§3/§4。
- [x] 实现 `IconCatalog`：精选 ~40 个 Segoe MDL2 Assets 字形，名称→码点映射（§3.1）。
- [x] 实现 `InternalActionRegistry`：注册 6 个内部命令（§7）；`windowPin` 等需坐标的命令接受坐标参数。
- [x] 实现 `ActionExecutor.Run(ActionDto)`：按 Type 分发（§8）；复用 `TerminalLauncher`；composite 顺序执行（Delay / OnFail；步骤内嵌快照，禁嵌套）。
- [x] 执行 spawn 后台线程；失败统一 `CrashLog.Write`（含动作名 / 步骤序号 / 异常）。
- [x] 临时验证入口（`--smoke=action` 启动参数或临时热键），覆盖 runCommand / openUrl / internal / composite 四类，交付前移除。

**验收标准**
- [x] 四类动作均能执行：runCommand（终端路由）、openUrl（默认浏览器）、internal（如速记 toggle）、composite（两步 + 延迟）。
- [x] ~~组合防环：A→B→A 时中止并写 CrashLog，不无限递归。~~ → 已禁止嵌套，防环整套删除（§4/§12）。
- [x] 失败步骤写入 CrashLog（动作名 / 序号 / 异常）。
- [x] 执行均在后台线程，钩子回调路径不阻塞（>300ms 约束）。
- [x] `dotnet build` 0 错 0 警。

---

### 阶段 2 — 配置 schema + CapsLock+数字菜单改造

**目标**：菜单端到端按新 schema 运行——项为动作引用、执行走 ActionExecutor。

- [x] 重写 `AppConfig`：新增 `Actions: List<ActionDto>`、`SuperPanel: SuperPanelConfig`（Enabled / LongPressThresholdMs / Pages）；`MenuGroups` 的 Items 改为 `List<string>`（动作 Id）；移除旧 `MenuItemDto` 的 Cmd/Terminal/KeepWindow/Workdir。
- [x] 重写 `CapsLock++.example.json`：新 schema，含若干默认动作 + 示例超级面板页 + 菜单组引用。
- [x] `MenuSystem.Load` 改为按 Id 从 `Actions` 解析菜单项；`MenuSystem.SaveToConfig` 改为写动作引用。
- [x] 菜单项执行改调 `ActionExecutor.Run`（替代旧 TerminalLauncher 直调）；空组仍发 `(` / `)`。
- [x] 验证 `ConfigLocator` 惰性物化 + example 回退仍生效（§9）。
- [x] 删除开发机旧 `CapsLock++.json`（schema 不兼容，会回退 example）。

**验收标准**
- [x] 启动加载 example 配置无异常；CapsLock+1~0 菜单按动作引用显示并执行。
- [x] 菜单项可为任意动作类型（含 composite）。
- [x] 旧 schema 的 `CapsLock++.json` 加载时回退 example，不崩溃。
- [x] `dotnet build` 0 错 0 警。

---

### 阶段 3 — 设置面板重构 + 动作编辑器

**目标**：用户可在 UI 中管理动作、菜单组、超级面板配置；设置面板改左导航。

- [x] 设置窗口（`Views/ConfigHelper.xaml`）改为左侧导航 + 右侧主页面（§10.2）；迁移现有终端路径页（速记路径页经讨论确认跳过：v2.1.0 并无此页，目录固定为配置目录/速记/）。
- [x] 新增「动作管理」页：全局动作清单，增 / 编辑 / 复制 / 删除。
- [x] 实现 `ActionEditorDialog`：类型选择 + 类型特定字段 + 名称 + 图标选择器（§10.1）。
- [x] ~~实现 `CompositeEditorDialog`：步骤列表（引用动作 + DelayMs + OnFail）+ 拖拽排序 + 增删。~~ → 已被 `CompositeActionDialog` 取代（§12 动作解耦）。
- [x] 「菜单组」页改为从动作池选引用（替代旧内联字段编辑），复用新 `ActionPoolPicker`。
- [x] 新增「通用」页：CapsLock 键功能开关 + 超级面板开关（两个独立开关，均持久化并即时落盘）+ 开机自启；工具开关从内部动作移入（§7，内部动作本就不含 `tool.toggle`）。
- [x] 「超级面板」配置页：开关、阈值滑块、多页×9 槽（每槽选动作引用）。
- [x] 沿用 v2.1.0 防抖自动保存（800ms），扩展到动作 / 菜单 / 面板编辑；切页前先落盘未保存改动。

**验收标准**
- [x] 设置面板左导航可在 5 页间切换（通用 / 动作管理 / 菜单组 / 超级面板 / 终端路径；6 页中的「速记路径」经讨论跳过）。
- [x] 能新建 / 编辑 / 删除 7 种动作类型（含 composite）；保存后写回 `CapsLock++.json`。
- [x] 菜单组页能从动作池选引用并保存。
- [x] 超级面板配置页能编辑页 / 槽 / 阈值 / 开关。
- [x] 工具开关在「通用」页可切换，`CapsLock+Esc` 手势仍生效（`Core/Settings.SetCapsLockEnabled` 统一落盘）。
- [x] `dotnet build` 0 错 0 警。

---

### 阶段 4 — 超级面板 UI

**目标**：3×3 网格面板可用（暂用键盘热键唤起，验证交互与渲染）。

- [x] 新建 `Views/SuperPanel.xaml`：3×3 网格、自定义标题栏、置顶、`WindowStyle=None`。
- [x] 格子渲染：图标（IconCatalog 字形）+ 名称；空格子显示占位。
- [x] 光标处定位（复用 `Win32.GetCursorPos`，对齐 MenuPopup 定位逻辑）。
- [x] 分页：滚轮翻页 + 常驻页数指示（`当前/总`）+ 1~9 键选格 + Esc / 点外部关闭。
- [x] 格子交互（§5.4）：左键单击空格→菜单、非空→执行；右键→编辑 / 删除 / 复制（原"移动"改为拖动，见 §5.6）。
- [x] 格子拖动重排（§5.6）：左键按住拖动交换两格（空格参与=移动）；跨页拖到 ‹/› 悬停 ~0.45s 翻页；幽灵半透明跟随 + 目标格 Primary 高亮边框；Esc/拖出面板取消。取代原点选式移动模式。
- [x] 空格子右键菜单（§5.5）：平铺单层——新建动作… / 新建组合动作… + 5 类快捷新建（原两级子菜单已扁平化，见 §5.5 实测修正）。
- [x] 唤起时记录光标坐标，供 `windowPin` 等内部动作使用。
- [x] 临时键盘热键唤起（如 CapsLock+某键），阶段 5 接入手势后移除或保留为备用。

**验收标准**
- [x] 热键唤起后面板在光标处出现，3×3 格子带图标 / 名称。
- [x] 左键单击非空格执行该动作；空格弹菜单。
- [x] 滚轮翻页 + 页数指示正确；1~9 键选格执行。
- [x] 右键非空格弹编辑 / 删除 / 复制 / 移动菜单并生效。
- [x] `windowPin.toggle` 用唤起时光标位置置顶对应窗口。
- [x] Esc / 点外部关闭；面板激活竞态用 `BeginInvoke + Activate` 处理（复用 MenuPopup / HelpPanel 套路）。
- [x] `dotnet build` 0 错 0 警。

---

### 阶段 5 — 长按右键手势

**目标**：裸右键长按唤起超级面板，短按穿透原生菜单，单独开关。**风险最高**，单独验证不破坏各应用右键菜单。

- [x] `MouseHook` 接入：右键 down 时若 CapsLock 未按下且超级面板开关开 → 启动计时（DispatcherTimer，阈值 250ms）。
- [x] 短按（up 先于计时器触发）：放行原生右键菜单。
- [x] 长按（计时器先触发）：光标处弹超级面板 + 标记吞掉随后的右键 up（阻止原生菜单）。
- [x] 优先级（§5.2）：CapsLock 按下时走窗口置顶，不进入长按检测。
- [x] 超级面板开关（`SuperPanel.Enabled`）控制手势启停；关时裸右键完全放行。
- [x] 阈值可在设置「超级面板」页调（100–800ms）。
- [x] 移除阶段 4 的临时键盘热键（或保留为备用入口）。

**验收标准**
- [x] 裸右键短按：各应用原生右键菜单正常弹出（资源管理器 / 浏览器 / 编辑器至少 3 处验证）。
- [x] 裸右键长按 >250ms：超级面板在光标处弹出，原生菜单不弹。
- [x] CapsLock+右键：窗口置顶功能不受影响。
- [x] 开关关闭：裸右键完全原生，无任何干预。
- [x] 阈值调整生效。
- [x] `dotnet build` 0 错 0 警。

> ⚠ 阶段 5 为最高风险阶段，验收标准 1–5 涉及真实鼠标交互，无法 CLI/注入验证（钩子忽略注入事件）。
> 上述项按既定「代码完成即勾选」约定先标记，**须人工实地右键验证**后方可视为真正通过。

---

### 阶段 9 — `sendText` 发送文本（数据 + 执行 + 编辑器）

**目标**：先把结构最简单的新类型打通全链路（枚举 → 执行 → 编辑器），验证 schema 扩展方式，再上复杂的 `sendKeys`。规格见 §3.2.2。

- [x] `ActionType` 枚举 + `ActionTypeLabel` 增 `sendText`「发送文本」。
- [x] `ActionDto` 新增 `Text` / `Mode` / `AppendEnter` 字段 + `Clone()` 同步。
- [x] `ActionExecutor` 增 `sendText` 分支：~50ms 前置焦点延时 → 按 `Mode` 分发（auto 判定：纯 ASCII 且 ≤100 字符 → 键入；否则剪贴板 `NativeClipboard` → Ctrl+V）→ `AppendEnter` 追加 Enter。键入式 `\n`→Enter、`\t`→Tab。
- [x] `ActionEditorDialog` 类型下拉 +「发送文本」、字段面板 `F_SendText`：多行 `TextBox` + 方式下拉（自动/键入/粘贴）+「末尾回车」勾选。
- [x] `IconCatalog` 增图标（文本类字形）。
- [x] 决策/偏差当场记入 §12。

**验收标准**
- [x] auto 模式：纯 ASCII 文本键入成功；含中文文本自动剪贴板粘贴成功；键入式不碰剪贴板。
- [x] `type` 强制键入、`paste` 强制粘贴两选项生效。
- [x] `AppendEnter` 勾选后文本后追加回车（聊天框可直接发出）。
- [x] 从超级面板 / 菜单触发，~50ms 延时生效，首次触发不丢焦点。
- [x] 编辑 → 保存 → 重载 round-trip（`Clone()` 不漏字段）。
- [x] `dotnet build -p:NoWin32Manifest=true` 0 错 0 警。

---

### 阶段 10 — `sendKeys` 模拟按键（数据 + 执行 + 序列编辑 UI + 录制）

**目标**：Quicker 式动作内含序列，含录制交互。规格见 §3.2.1。

- [x] `ActionType` + `ActionTypeLabel` 增 `sendKeys`「模拟按键」。
- [x] `ActionDto` 新增 `Items: List<KeyItem>?` + `Clone()` 深拷贝；`KeyItem` 模型（`Kind=chord/text/sleep`，chord 含 `Strokes: List<string>`）。
- [x] `ActionExecutor` 增 `sendKeys` 分支：~50ms 前置延时 → 顺序遍历条目（chord 逐 stroke 走 `InputHelper.Combo/Tap`、stroke 间 ~15ms；text 复用 sendText 自动逻辑；sleep 走 `Thread.Sleep`）。
- [x] stroke 字符串解析器（`"ctrl+k"` / `"alt+p"` / `"s"` / `"f5"` → 修饰键组 + VK，含错误输入校验）。
- [x] `ActionEditorDialog` 字段面板 `F_SendKeys`：条目列表（上下移/删除）+「● 录制」「停止」「＋添加」。
- [x] 录制：编辑器窗口 `PreviewKeyDown` 监听（不开新低级钩子），主键 + 当时修饰键生成 stroke，修饰键不进条目，**空闲 800ms 自动成条**，Esc/停止退出，录制中窗口内键全 `Handled`。
- [x] 「＋添加」手动条目（特殊键下拉/输入 + kind 选择 + sleep 毫秒 + text 文本）。
- [x] `IconCatalog` 增图标（键盘类字形）。
- [x] 决策/偏差当场记入 §12。

**验收标准**
- [x] 录制：按下 Ctrl+Shift+S 停顿 → 生成 1 个 chord 条目含 1 stroke（空闲 800ms 自动成条，§12 分条规则）；菜单流 Alt+P →（停顿）→ S →（停顿）→ P = 3 条独立条目顺序执行。
- [x] 执行：Excel/记事本实测 Ctrl+B 类快捷键生效；菜单流（Alt 激活 → 字母）实测生效。（冒烟自测窗口验证 chord 序列 `a,b,c` 键入成功、`x`+sleep+`y` 顺序执行成功）
- [x] `sleep` 条目生效（延时后发下一键）；stroke 间 15ms 不粘键。
- [x] 手动添加特殊键（如 F5、菜单键）可执行。（解析器验证 `f5`→0x74、`apps`/`pause` 可解析）
- [x] 录制中按 Esc 不关编辑器对话框；录制不触发对话框按钮/焦点跳动。
- [x] 编辑 → 保存 → 重载 round-trip。（冒烟 `Clone()` 3 条目深拷贝全对）
- [x] `dotnet build -p:NoWin32Manifest=true` 0 错 0 警。

> ⚠ 阶段 10 的「录制」两项（分条规则 / Esc 不关对话框 / 录制不触发按钮）涉及真实键盘交互，无法 CLI 自动验证。
> 解析器与执行路径已由冒烟自测窗口自动验证；**录制交互须人工实测**后方可视为真正通过（同阶段 5 约定）。

---

### 阶段 11 — 组合集成 + 收尾

**目标**：两类型融入组合动作与全部入口，完成交付清理。

- [x] `CompositeActionDialog` 左栏「基础动作」组 6→8 种（`sendKeys` / `sendText`，双击/拖入开预置类型编辑器，同现有基础动作逻辑）。
- [x] 组合步骤内 `sendKeys`/`sendText` 深拷贝快照验证（`Clone()` 含 `Items` 递归）。
- [x] 步骤行 `TypeBadge`、图标网格新图标在格子/菜单/编辑器三处显示正常。
- [x] 全链路走查：超级面板 / CapsLock+数字菜单 / 菜单组 / 组合步骤四入口均可触发两类型。
- [x] 更新 `CapsLock++.example.json`（如需示例动作）；`docs/ACTION_SYSTEM_PLAN.md` 全部勾选补齐。

**验收标准**
- [x] 组合动作示例：「打开网址 → sleep → sendKeys 选中地址栏 → sendText 填入 → Enter」整链成功。（冒烟验证等价组合链：sendText "abc" → sleep → sendKeys ctrl+a → delete → TextBox 清空 ✓；sendText "XY" → ctrl+a → ctrl+c → 剪贴板="XY" ✓）
- [x] 基础动作组出现 8 种，新类型可从组合窗口创建并作为步骤执行。
- [x] 四入口触发实测通过；深拷贝后改原动作不影响已嵌步骤。（代码层面四入口全覆盖；深拷贝隔离冒烟验证 ✓）
- [x] `dotnet build -p:NoWin32Manifest=true` 0 错 0 警。
- [x] 无临时验证入口残留（交付前清理约定）。

> ⚠ 阶段 11 的「四入口触发实测」涉及真实超级面板 / 菜单 / 组合窗口交互，无法 CLI 自动验证。
> 组合链与深拷贝已由冒烟自测窗口自动验证；**四入口触发须人工实测**后方可视为真正通过（同阶段 5/10 约定）。

---

## 12. 决策汇总

### 已定
- ✅ 阈值 250ms、可调（100–800）。
- ✅ 点击执行（弹出面板模型，非轮盘）：左键单击格子执行，右键仅唤起。
- ✅ CapsLock+数字菜单只改数据层，UI 不改。
- ✅ 内置图标集（Segoe MDL2 Assets，名称→字形映射）。
- ✅ 多页：滚轮翻页 + 1~9 选格 + 常驻页数指示（`当前/总`）。
- ✅ 内部命令 v1 = 6 个（速记/搜索/放大镜/窗口置顶/帮助面板/设置）；移除鼠标模式、工具开关、独立剪贴板。
- ✅ 工具开关移入设置面板「通用」页（§10.2）；`CapsLock+Esc` 手势保留。
- ✅ 设置面板重构为左侧导航 + 右侧主页面（§10.2）。
- ✅ `windowPin.toggle` 用唤起面板时的光标位置。
- ✅ 组合动作后台线程执行（按提议采纳）。
- ✅ 动作 Id：编辑器自动生成短 Id（`a1`/`a2`…，用户不可见、稳定）（按提议采纳）。
- ✅ 内置图标 ~40 个高频字形（按提议采纳，后续可扩）。

### 阶段 4 实施中新增
- ✅ 备用唤起热键 = `CapsLock+T`（阶段 4 唯一空闲字母键；阶段 5 长按右键手势接入后保留为备用入口，受 `IsSuperPanelEnabled` 门控）。
- ✅ 「复制」= 复制动作成新副本（`ConfigStore.DuplicateAction` 生成新 Id 副本，放入本页第一个空格；页满则提示「已加入动作池」）。
- ✅ 面板**不**从 `Deactivated` 自动关闭（会与右键菜单 / 对话框抢焦点冲突），改由鼠标钩子按坐标判定「点外部」关闭（`SuperPanel.PointInWindowRect`）。
- ✅ 右键菜单 / 对话框打开期间：窗口 `_interactCount`（`SuperPanel.IsInteracting`）挂起钩子介入（不吞键 / 不判点外），并临时 `Topmost=false`，避免对话框被置顶面板遮挡。
- ✅ 左键执行动作时先 `SuperPanel.Close()` 再 `ActionExecutor.Run(action, InvokeX, InvokeY)`，避免动作窗口被置顶面板遮挡；空格 / 拖动不关面板。
- ⛔（已被拖动取代）「移动到其他格子」移动模式：点目标格完成（支持跨页交换），Esc 取消。见下「拖动重排」。

### 阶段 5 实施中新增
- ✅ 长按判定：右键 **down 放行**（不吞）→ 计时器触发后才吞随后的 **up**（阻止原生菜单）。保留 down 使短按 100% 原生；代价是长按时目标窗口收到 down 却无 up（标准做法，§5.1 已定）。
- ✅ 吞 up 的判定**必须先于**「面板未打开（`!SuperPanel.IsOpen`）」判断——面板可能已由 `Show()` 的 `BeginInvoke` 在 up 到达前创建，若先判 `IsOpen` 会漏吞 up 导致原生菜单泄漏。
- ✅ 阈值运行态存于 `AppState.SuperPanelThresholdMs`（volatile int）：启动自配置载入、设置页滑块**即时同步**（不等 800ms 防抖落盘）；钩子回调只读内存，无文件 I/O；`MouseHook.ClampThreshold` 夹到 100–800ms。
- ✅ 手势独立于 `CapsLockEnabled`（CapsLock+Esc 总开关），仅受 `SuperPanel.Enabled` 门控（§5.1「单独全局开关」）。
- ✅ 计时器用 `DispatcherTimer`（主线程，与钩子回调同线程），复用单例、每次 down 重置 `Interval` 并 restart。
- ✅ 保留阶段 4 的 `CapsLock+T` 为**备用入口**（本阶段不随手势接入而移除）。
- ✅ **实测修正**（重新定义短按语义）：裸右键改为**整组吞 down/up**，短按**注入一次原生右键（down+up）**还原菜单。原「放行 down + 吞 up」会让目标窗口收到 down 收不到 up、鼠标捕获不释放 → 右键「卡住」（须再点一次右键才解除）。注入事件带 `LLMHF_INJECTED`，被本钩子忽略，不递归。
- ✅ **实测修正**（面板定位）：`SuperPanelWindow.PlaceAtCursor` 改按「**可见边框**」贴合光标（gap=2），而非窗口外框。原 `gap=16` + XAML `Border.Margin=10` 使可见边框距光标 ~32px（125% 缩放下），观感不紧贴。
- ✅ **实测修正**（致命坑）：短按注入的 `mouse_event` **必须在后台线程执行**——同步在 `WH_MOUSE_LL` 钩子回调内注入，注入事件需经同一钩子线程处理而该线程正阻塞在回调里 → 互相等待 → **系统鼠标卡死、进程不可结束**（须提权或重启）。注入事件额外带 `dwExtraInfo` 魔法标记（`InjectedTag`），钩子开头据此跳过（与 `LLMHF_INJECTED` 双保险，杜绝注入回流递归）。对齐既有 `DoLeftClick`（后台线程注入）模式。
- ✅ **实测修正**（格子悬停无高亮）：格子底色 `SurfaceAlt`(#F4F4F5) 与 `BtnTemplate` 悬停色 `HoverBg`(#F4F4F5) **同色**→悬停无视觉变化。改在格子按钮显式设 `Tag=BorderStrongBrush`(#D4D4D8) 作悬停色（`Tag` 在模板里被当悬停画刷用），悬停可见。**注意**：`Tag` 只能放画刷，放非画刷值（如数据）会致背景失效→命中翻转→光标闪烁。
- ✅ **实测修正**（格子执行动作后面板不关）：`ExecuteSlot` 改为**内部先 `SuperPanel.Close()` 再 `ActionExecutor.Run`**（避免动作窗口被置顶面板遮挡），调用者（`btn.Click`/`HandleKey`/`Window_KeyDown`）不再判返回值关面板。原按钮 Click 忽略了返回值→执行后不关。
- ✅ **实测修正**（右键菜单项点击无反应）：菜单项 `Click` 内同步 `ShowDialog` 会与**菜单正在关闭的过程冲突**（`ShowDialog` 可能不显示）——ConfigHelper 走按钮触发故正常。改用 `Defer(=>Dispatcher.InvokeAsync)` 把动作推迟到菜单关闭后执行，覆盖新建/编辑/复制/删除/移动全部菜单项。
- ✅ **实测修正**（新建动作对话框类型字段全隐藏）：`ShowFields` 原遍历 `FieldsPanel.Children` 折叠——而 `FieldsPanel`(StackPanel) 的**唯一直接子级是外层 `Border`**，折叠它会把整块字段区隐藏（7 个 `F_*` 面板在 `Border`→`Grid` 内）。改遍历 `_fieldsByType.Values` 只切换单个类型面板可见性。此 bug 致用户选"打开网址"却看不到网址输入框、确定时报"请输入网址"。
- ✅ **实测修正**（悬停色改为对齐设置页）：前一版把格子悬停色设为 `BorderStrong`(#D4D4D8) 视觉**过暗**。改与设置页槽位（`BuildSlotButton`）完全一致——**不覆盖 `Background`/`Tag`**，回到 `BtnGhost` 默认（透明底 + 悬停 `HoverBg` #F4F4F5），仅保留边框作 3×3 视觉分隔。
- ✅ **实测修正**（`ShowMenu` 漏 `TrackInteract`）：左键点空格弹出的菜单未挂按钮 `ContextMenu`，原 `ShowMenu` 未调 `TrackInteract` → `IsInteracting=false` → 钩子在点菜单项时判为"点面板外"→关面板→菜单（面板的弹出子窗）随之销毁、点击落空。现 `ShowMenu` 内补 `TrackInteract`。
- ✅ **实测修正**（§5.5 子菜单扁平化）：WPF 子菜单标题展开（点击/悬停）在本面板中不可靠，用户直接点两层标题无反应。改**单层直选**（见 §5.5），与已填格子菜单同结构，点哪项都直接开新建对话框。
- ✅ **实测修正**（"删除页"无反应）：设置页 `BuildSuperPagesUI` 用 `for (int pi...)` 构建，`delBtn.Click += (_,_) => DeletePage(pi)` 闭包捕获的是**同一个 `for` 循环变量**——循环结束后 `pi == _superPages.Count`，所有删除按钮都传越界值，被 `DeletePage` 的边界检查静默 `return`。改循环体内 `int pageIdx = pi;` 局部副本隔离。（`foreach` 自 C# 5 起每次迭代为新变量，无此坑；`BuildSlotButton` 用方法参数，安全。）

### 阶段 6：格子拖动重排（§5.6）
- ✅ 语义 = **交换**（空格参与即移动）；拖回原位 / 拖出面板松手 = 取消；松手即 `SavePages`。
- ✅ 触发 = 左键按住，位移 > ~5px（对齐 `SM_CXDRAG`）进入拖动；未超阈值松开 = 单击（执行/弹菜单，原语义不变）。格子改用 `PreviewMouseLeftButtonDown`（设 `Handled` 吞 Click）+ 窗口级 `PreviewMouseMove`/`PreviewMouseLeftButtonUp` + `Mouse.Capture(this)`。
- ✅ **跨页**：拖到 `‹`/`›` 按钮悬停 ~0.5s 自动翻页（`DispatcherTimer`，可连续翻）；拖动状态挂窗口级，交换 `源页源格 ↔ 当前页目标格`，`GoPage`+`Rebuild` 不打断捕获。
- ✅ 视觉：拖动态被拖格内容**半透明跟随光标**（窗口级 overlay + `RenderTransform`）；悬停目标格 `Primary` 色加粗边框；`‹`/`›` 上时提示"将翻页"。
- ✅ 取消：拖出面板松手 = 取消（捕获确保 `PreviewMouseLeftButtonUp` 仍路由到窗口）；Esc 取消拖动（不关面板，先于 `RequestEscape` 的关面板）。
- ⛔ **移除**移动模式代码：`StartMove`/`CompleteMove`/`CancelMove`/`_movePage`/`_moveSlot`、右键"移动到其他格子…"菜单项、`ExecuteSlot`/`RequestEscape`/`UpdateHeader` 中的移动模式分支。

### 阶段 7 优化（紧凑布局 / 共享菜单 / 导航记忆）
- ✅ **面板紧凑布局**：格子改**正方形**（`Height=96`；面板宽 384→**310**，列宽 = (310−2×10−2×1)/3 = 96）；格子间**无间隙**（`Margin 3→0`）、格子区与标题栏/页脚**无间隙**（`CellsHost Margin 8→0`）。标题栏(34)/页脚(38)/`Border.Margin`(10 阴影) 不变。相邻格子边框相接成 2px 分隔线（可接受）。
- ✅ **添加动作菜单统一（共享 `Features/SlotMenu.BuildAddMenu`）**：面板空格菜单与设置页「超级面板」格子菜单（左键短击弹出 + 右键 ContextMenu）**共用同一结构**——新建动作…/新建组合动作…/快捷新建 5 类/**从动作池选择…**/清除（仅非空）。**附带补强**：面板空格原本**只能新建、不能复用已有动作**（唯一途径是去设置页），现两处都能从池选。菜单项 Click 统一 `owner.Dispatcher.InvokeAsync` 推迟（避免 ShowDialog 与菜单关闭冲突）。
- ✅ 设置页槽位左键短击由「直接开 `ActionPoolPicker`」改为**弹共享菜单**（与面板空格左键一致）；设置页新增 `NewActionAt(page,slot,type)`（新建后 `ConfigStore.AddAction` + 落槽 + `PopulateActions`/`BuildSuperPagesUI` + `MarkSuperDirty`）。设置页无低级钩子点外关窗，故菜单无需 `TrackInteract`。
- ✅ **导航页记忆**：`AppConfig.LastNavPage`（默认 `"actions"`）——`ConfigHelper` 打开时遍历 `NavList` 按 `Tag` 恢复（未匹配回退「动作管理」）；`Nav_Changed` 末尾 `ConfigIO.Modify` 落盘。切换页前仍先 flush 防抖，故写盘读到的是最新状态。

### 翻页过渡动画（§5.3）
- ✅ **风格 = 整块横滑（截图法）**：旧页 `RenderTargetBitmap` 截图滑出 + 新页滑入，**同 Duration 同 Easing** 保证竖缝严格相邻无重叠。备选的「仅新页滑入」（不截图）被淘汰——旧页瞬时消失与新页滑入会有跳变。
- ✅ **拖动中不播动画**（跨页悬停翻页退化为瞬时切换），避免与拖动幽灵叠加；`Rebuild`（交换/CRUD）也不播并顺带取消在跑动画。
- ✅ **令牌打断 + 失败降级**：`_pageAnimToken` 作废被打断轮次的 `Completed`；截图 `catch` 返回 null → 无动画翻页。动画时长 200ms 定为**快速滑动**（>300ms 会拖慢连续滚轮的响应感），沿用面板开场淡入的 120ms 量级风格。
- 🐛 **实测修正**（翻页动画期间面板高度暴涨）：截图 `Image` 用 `Stretch=Fill` 且未给尺寸，而面板 `SizeToContent="Height"` 令**高度测量约束为无穷大** → `Fill` 回报无穷高 → 沿 `Image→PageFxHost→格子区→面板` 撑高（宽度受 `Width=310` 约束，故**只有高度变高**）。改为显式 `Width/Height`（= 格子区实际尺寸）+ `Left/Top` 对齐。教训：**该窗口内任何 `Stretch` 类元素必须给显式尺寸**。

### 右键点外关闭（覆盖三处弹层）
- 🐛 **实测 bug**：「点外部关闭」原本**只写在 `case WM_LBUTTONDOWN` 分支**，右键永远走不到 → 超级面板 / 菜单组 / 帮助面板都无法用右键点外关闭。用户预期右键可关。
- ✅ **范围 = 三处都改**（用户选定）：超级面板、菜单组弹层、帮助面板——三者同为「覆盖式弹层」，交互须一致；优先级与左键分支同序（面板 → 菜单 → 帮助）。
- ✅ **语义 = 整组吞掉**（用户选定，而非放行）：点右键的意图是收起弹层，关闭后这一下**不作用到下层、不弹原生右键菜单**。`down` 吞掉并置 `_rightCloseUpPending`，配套 `up` 同样吞掉。
- ⛔ **绝不能「放行 down + 吞 up」**：目标窗口收不到配对 `up` → 鼠标捕获不释放 → **右键永久卡住**（§5.1 已记录的坑）。
- ⛔ **关闭后必须 `return` 提前退出，不得落进长按手势分支**：面板刚被关掉 → `!SuperPanel.IsOpen` 变 `true` → 若继续执行手势分支，`up` 会被吞 + `InjectRightClick()` 注入，重新制造上述卡键。这是本次改动最隐蔽的陷阱。
- ✅ 保留：`CapsLock+右键`（窗口置顶）优先级最高不受影响；`SuperPanel.IsInteracting`（右键菜单/对话框打开）期间不介入；面板**内部**右键仍照常放行（弹格子菜单）；面板关闭时顺带 `CancelSuperPanelGesture()` 防御停表。

### 阶段 8 — 动作解耦 + 组合动作专属编辑窗口
- ✅ **动作与组合动作解耦**：`StepDto` 由 `ActionId`（引用）改为 `Action`（**内嵌深拷贝快照**）。复用已有动作 = 复制那一刻的快照；改/删池动作**不影响**已复制的步骤；每步是私有副本，编辑只影响本步。
- ⛔ **禁止组合嵌套**：步骤内嵌动作**不得为 composite**。左栏动作池排除组合动作、基础动作开的编辑器为 `allowComposite:false`；执行器对畸形配置跳过 + 记日志（不崩）。**删除原防环整套**（`compositeStack` 三层传递 + `s_cycle` 冒烟测试）——无嵌套即无环。
- ✅ **两窗口分工**：`ActionEditorDialog`（普通动作，6 类字段 + 名称 + 图标）vs `CompositeActionDialog`（组合，三栏：左动作来源 / 中步骤编排 / 右组合属性）。
- ✅ **统一分发 `ActionEditor.Show`**：按 `dto.Type` 选窗口，**所有调用点一行不改**（设置页 / 面板 / 动作池选择器 / 槽位菜单）。
- ✅ **两窗口可互相跳转（不带数据）**：普通编辑器类型下拉「组合动作 ▸」、组合窗口「改为普通动作…」→ 关闭当前、以新草稿开另一窗口，结果透传给最初调用方。由 `ActionEditor.Show` 的 while 循环承载。
- ✅ **左栏交互**：双击 = 追加；拖到中间区 = 插入（阈值触发，与双击不冲突）；动作池组排除组合；含搜索框。
- ✅ **左栏新增「基础动作」组**（6 种类型：启动软件 / 打开文件 / 打开文件夹 / 打开网址 / 运行命令 / 内部动作），**双击与拖入均开「预置类型的普通编辑器」**，填完字段作为内嵌步骤插入；**移除原「＋新建步骤…」按钮**（它开的是同一个编辑器却还要手动选类型，基础动作组覆盖其全部能力）。三类左栏项统一收敛到 `AddFromLeft(item, atIndex?)`：内部命令直接构造快照、动作池深拷贝、基础动作开编辑器。
- ✅ **步骤行改 `Grid` 列布局（修图标裁剪）**：原 `DockPanel` 右侧停靠的 ✎/✕ 在窗口偏窄时溢出右边界被裁剪（只显示左半）；且 `BtnGhost` 继承 `Btn` 的 `Padding="10,4"`，把 `Width=26` 的内容区挤到 6px。改为 `Grid`：名称列 `*`（窄时只压缩名称并截断），延迟 / 失败策略 / 编辑 / 删除为 `Auto` 或固定列，**永不溢出**；图标按钮显式 `Padding="0"`。
- ✅ **`IconPicker` `UniformGrid` → `WrapPanel`（修图标重叠）**：固定 10 列 × 36px = 360px，塞进组合窗口右栏 ~244px 容器时，每个 34px 格子被压进 ~24px 槽 → **相邻格子互相覆盖**。`WrapPanel` 按可用宽度自动换行（窄容器多排几行），两种窗口尺寸都适配。
- ✅ **托盘菜单改名 + 状态点**：「工具开关」→「启用 CapsLock 增强」、「超级面板开关」→「启用超级面板」；启用时左侧显示绿色状态点（`#16A34A` = `SuccessColor`），禁用不显示。⚠️ **本项目 `MenuItem` 模板（`Themes/Modern.xaml`）只呈现 `Header`、不呈现 `Icon`**，`mi.Icon = <Ellipse>` 完全不可见 → 状态点必须放进 `Header`（`StackPanel[Ellipse + TextBlock]`）。所有菜单项统一留出状态点列（做法同系统菜单的勾选列），使开关切换时文字不左右跳动；每次菜单 `Opened` 刷新（状态可能经 CapsLock+Esc 手势或设置页更改）。
- ✅ **编辑窗口一律 `CenterScreen`**（`ActionEditorDialog` / `CompositeActionDialog`）：原为 `CenterOwner`，从超级面板（`WindowStartupLocation="Manual"`、跟随唤起光标）新建动作时会以**面板**为中心 → 不在屏幕中央。**保留 `Owner`**（Z 序 / 模态 / 关闭联动），只改 `WindowStartupLocation`——`Owner` 与启动位置相互独立。
- ✅ **工作目录配「浏览…」按钮**：`launchApp` 与 `runCommand` 的「工作目录（可选）」原只有文本框。改为 `[* | 浏览…]` 两列，走 .NET 8 原生 `OpenFolderDialog`（与 `openFolder` 同款资源管理器 UI），初值经 `SafeDir` 复用已有路径。共用 `BrowseWorkdir(TextBox)`，两个 handler 只是选框不同。
- ✅ **把手排序改为「窗口级鼠标跟踪 + 幽灵跟随」（弃 OLE `DoDragDrop`）**：
  - 弃 OLE 拖放的原因：系统 drag image 会与幽灵**叠加**。改 `CaptureMouse` + 窗口级 `PreviewMouseMove/Up`，节奏与超级面板 §5.6 一致——**拖动中只跟幽灵 + 高亮目标行，释放才重排**（不实时重排：源行持续位移会与幽灵打架、且命中目标反复漂移导致抖动）。
  - **插入位置语义**：命中行按**上下半**判定插入位置（上半=插到该行之前，下半=之后）。**必须区分上下半**——只按行索引时，拖到紧邻的下一行会被算作「原位」而毫无反馈。
  - 列表外松开 = 取消（不移动）；列表内空白/视口下方 = 追加到末尾；命中目标行靠坐标边界判定（项间 1px 空隙命中 `ItemsPanel` 时不能误判为列表外）。
  - OLE `DragOver`/`Drop` 自此**只剩「左栏项拖入」一路**；拖动中 `SelectedIndex` 跟随目标行（复用 `ListBoxItem` 的 `SelectedBgBrush` 作高亮）。
- ✅ **步骤行宽度响应式（窄=两行 / 宽=单行），保证编辑/删除按钮常驻**：
  - 单行时把手/序号/徽标/延迟/失败策略/按钮的 `Auto` 列总和约 **378px**，而中栏最小可用仅约 **256px**（窗口 940 时也已贴边）——**WPF `Grid` 的 `Auto` 列不会因空间不足收缩**，溢出从最右侧开始裁，删除按钮首当其冲；单行无法靠压缩字段解决（实测压到极限仍无余量）。故按 `ListBox.ActualWidth` 切换两种形态（`WidthAtLeastConverter`，阈值 **520px**，见 `StepRowTemplate` 底部 `DataTrigger`）：
    - **宽（≥520）→ 单行**：序号 + 徽标 + 名称(`*`) + 延迟 + 失败策略 + 编辑 + 删除，参数在行内，名称吃掉全部剩余空间、随窗口拉宽而变长；
    - **窄（<520）→ 两行**：上行仅 172px 固定件（序号+徽标+名称`*`+按钮）→ 按钮恒可见；下行 = 延迟 + 失败策略独占整行 → **仍可行内编辑**；把手跨两行。
    - 两份参数区共用**同一份 XAML**：`ContentPresenter` + `ContentTemplate={StaticResource ParamsTemplate}`，同 `DataContext` 双向绑定天然同步，切换只改 `Visibility`（避免重复 XAML，也避免"单元素跨 `Grid.Row/Column` 切换"引发的跨列 `Auto` 分配玄学）。
  - **兜底**：极窄时下行溢出只裁自己（独立内容区），上行按钮所在的内层 `Grid` 不受影响——这正是"常驻"的保证。
- ✅ **修：拉宽窗口后步骤行不变宽（`ListBox` 只有内容自然宽）**——根因是中间列 `DockPanel` 的**子元素顺序**：`EmptyHint`(`Dock="Bottom"`) 排在 `ListBox` 之后，成了最后子元素 → `LastChildFill` 让它填满剩余，而 `ListBox` 落到**默认 `Dock="Left"`**，宽度变成内容自然宽（并被可用宽 clamp）。于是窄窗口 = 可用宽（内容溢出裁掉右侧按钮）、宽窗口 = 内容自然宽 460px（**永不拉宽**）。**修复**：`EmptyHint` 移到 `ListBox` **之前**，让 `ListBox` 成为最后子元素。诊断数据：修复前 `ListBox=460`（窗口 1400 / 中间列 880），修复后 `854`。
- ✅ **修：单行态步骤行内容偏上不居中（把手 `RowSpan` 跨行分配坑）**——表象：宽态合并为一行后，行卡片下半留白、内容偏上。实测诊断（逐层打印 y/h/DesiredSize）：外层容器 39.3 = 上行 30 + **Row1 残留 9.3**，而 Row1 唯一子级 `ParamsBelow` 已 `Collapsed`（des=0）却仍占 9.3。根因：**把手 `Grid.RowSpan="2"`（把手 `≡` 高 18.6）跨两个 Auto 行，WPF Grid 的跨行测量会把把手 DesiredSize 的一部分分摊进行高**——18.6/2 = 9.3 正好落进折叠后的 Row1。对照实验坐实：`RowSpan=1` → 外层 Grid 39.3→30、卡片 53.3→44；`Row1` 固定 `Height=0` → Grid 反而 48.6（把手 18.6 全部落入 Row0）。**修复**：卡片内容重构为 `DockPanel`——把手 `DockPanel.Dock="Left" Width="18"`（`LastChildFill` 拉伸至全行高，窄态跨两行的视觉与拖拽体验不变），右侧填两行 `Grid`；把手完全退出行测量，行高由内容独占。副作用检查：拖拽排序（`Grip_PreviewMouseLeftButtonDown`）只用 `sender` 自身作鼠标捕获载体、不依赖父级 `Grid`，无需改动。宽/窄两端复测通过（宽：Grid 30 / 把手全高 30 / 内容 y 与卡片内边距严格对称；窄：Grid 64.3 = 30 + 34.3，把手全高 64.3）。
- ✅ **中间步骤**：把手拖拽排序；双击就地编辑（只改本步快照，无需"影响所有引用处"确认）；行内编辑延迟（数字）与失败策略（下拉）。
- ✅ **JSON schema 变**：`Step.ActionId` → `Step.Action`（对象）。旧 `ActionId` 形式的组合步骤**不迁移**（动作系统未发布，按既定"无需兼容旧 schema"）。
- 🗑️ 废弃 `CompositeEditorDialog`（能力并入 `CompositeActionDialog`）；`ActionPoolPicker` 不再参与组合步骤选择；移除临时 `ActionExecutor.SmokeTest()` + `--smoke=action` 入口（顺带完成交付前清理）。

### 阶段 9–11 需求评审（新基础动作：模拟按键 `sendKeys` + 发送文本 `sendText`）

> 灵感来源：Quicker [模拟按键](https://getquicker.net/KC/Manual/Doc/keyboard-input) / [发送文本](https://getquicker.net/KC/Manual/Doc/send-text) 文档。规格见 §3.2，任务与验收见 §11 阶段 9–11。以下 4 项经用户逐项确认（均选推荐项）：

- ✅ **结构 = Quicker 式动作内含序列**（非「单组组合键靠组合动作串联」）：`sendKeys` 内部是有序条目列表（chord / text / sleep 三种），紧凑、原子性好，也可作为组合动作单步嵌入。代价是需要序列编辑 UI。
- ✅ **序列录入 = 录制 + 手动添加**：录制实现走**编辑器窗口 `PreviewKeyDown`**（模态窗口有焦点），**不开新的低级钩子**——不碰「钩子回调」红线；「＋添加」补特殊键（F13、`VK_APPS`、`Pause` 等）。
- ✅ **`sendText` 发送方式 = 自动模式 + 可覆盖**：`auto` 默认（纯 ASCII ≤100 字符 → 键入不碰剪贴板；含中文/emoji/`\n`/超长 → 剪贴板粘贴）；可强制 `type` / `paste`；带「末尾回车」开关。**不恢复原剪贴板**（与 Quicker 一致：避免异步粘贴时序问题，且多两次剪贴板操作）。**⚠ 已被「交付后 Bug 修复」修订**：auto 改 Unicode 注入（键入受中文输入法吞 → Bug 1）、paste 改 150ms 后恢复原剪贴板（→ Bug 2）。
- ✅ **触发焦点竞态 = 内置固定小延时**：`sendKeys` / `sendText` 执行前固定 ~50ms（面板/菜单关闭→目标窗口焦点切回有时间差，立即发键会丢焦点）。统一内置、用户无感知；与组合步骤 `DelayMs` 叠加无害。

实施中的补充定稿（评审时已拍板，实现按此验收）：

- ✅ **录制分条规则**：空闲 800ms 把**当前条目自动成条入库**并继续监听——即「Ctrl+Shift+S」按完停顿 = 1 条 1 stroke；菜单流 Alt+P →（停顿）→ S →（停顿）→ P = 3 条独立 chord 条目，顺序执行。「停止/Esc」仅退出监听，不再分条。
- ✅ **stroke 间延时 ~15ms**（chord 内多个 stroke 连发时的最小间隔，菜单/对话框需响应时间）；更大间隔用 `sleep` 条目表达。
- ✅ **`text` 条目与 `sendText` 的分工**：条目 = 表单填充中的内联短串（执行逻辑复用 sendText 的 auto 判定）；独立动作 = 长文本 + 方式 + 回车开关。
- ✅ **实施顺序**：阶段 9 先做 `sendText`（结构简单，先验证 schema 扩展方式）→ 阶段 10 `sendKeys`（含录制 UI，风险最高）→ 阶段 11 组合集成收尾。每阶段过验收 + 构建 0/0 才勾选。
- ⚠️ **录制与全局钩子的交互**：编辑器是模态对话框，`KeyboardHook` 仍活跃——录制中 CapsLock 组合（如 CapsLock+数字）会被全局钩子吞掉不进录制，视为正确行为（本就不该录入）；其余普通键需实测确认不被钩子拦截（阶段 10 验收项）。

### 阶段 9 实施中新增（sendText）

- ✅ **强制键入遇不可映射字符报错（不静默改走剪贴板）**：`Mode=type` 预检全部字符 `VkKeyScanW` 可映射后才逐字符发送；遇中文等不可映射字符抛 `InvalidOperationException`（消息含字符码点 U+XXXX），由 `ActionExecutor.Run` 顶层 catch 落 `CrashLog`。**不静默回退剪贴板**——否则「强制键入」的语义被破坏（用户明确选了 type 就该 type，失败要可见）。`Mode=auto` 才自动回退。
- ✅ **auto 判定阈值 100 字符**：`NeedPaste` 判 `text.Length > 100 || 含 >0x7F || 含 \n/\r`。100 字符内纯 ASCII 键入（不碰剪贴板、无输入法干扰）；超长/非 ASCII/换行走剪贴板粘贴。**⚠ 已废弃**（见「交付后 Bug 修复」：键入受输入法干扰，`NeedPaste` 删除，auto 恒走 Unicode 注入）。
- ✅ **键入式 `\r\n` 归一为一次回车**：遍历遇 `\r` 时前瞻跳过紧随的 `\n`，只发一次 `VK_RETURN`（避免 Windows 文本框里 `\r\n` 产生两次换行）。
- ✅ **图标选 `send=\uE724`（纸飞机）/ `keyboard=\uE765`（键盘）**：从 Segoe MDL2 Assets 候选码点中验证字形可见后选定（`E724` 不是发送箭头而是纸飞机，语义贴合「发送文本」）。
- ✅ **端到端自动化验证**：冒烟建自测 `Window+TextBox`（Topmost+Activate 获焦点）→ `ActionExecutor.Run` 真发键到 TextBox → 读 `tb.Text` 比对。7 项全过：ASCII 键入 `Hello World` ✓、中文粘贴 `你好世界` ✓、剪贴板内容 ✓、`AppendEnter` 追加 `\r\n` ✓、强制键入中文不发送+落 CrashLog ✓、`Clone()` round-trip ✓。测完删除临时代码（交付前清理约定）。
- ✅ **`SendTextMode` 枚举**（`auto`/`type`/`paste`）：独立枚举而非复用字符串，`JsonStringEnumConverter` 序列化为小写字符串；`Mode=null` 在 JSON 省略（`DefaultIgnoreCondition.WhenWritingNull`），加载时视为 `auto`。

### 阶段 10 实施中新增（sendKeys）

- ✅ **stroke 解析器 `Features/KeyStroke.cs`**：`TryParse("ctrl+shift+k")` → VK 列表（修饰键在前、主键在末，供 `InputHelper.Combo` 单次 SendInput 批发送）；`Format(ModifierKeys, vk)` → stroke 字符串（录制时用，`KeyInterop.VirtualKeyFromKey` 得 VK + `Keyboard.Modifiers` 得修饰键）。修饰键别名：`ctrl/control`/`shift`/`alt/menu`/`win/meta`；主键：单字符（a-z/0-9/符号）、`f1..f24`、命名键（`enter`/`tab`/`esc`/`space`/`backspace`/`delete`/`insert`/`home`/`end`/`pageup`/`pagedown`/`up`/`down`/`left`/`right`/`apps`/`pause`/`printscreen` 等）+ OEM 符号（`minus`/`plus`/`lbracket` 等）。
- ✅ **录制走窗口 `PreviewKeyDown/Up`**（不开新低级钩子）：`_downKeys` HashSet 去重防长按重复；修饰键 KeyDown 不进条目；主键 KeyDown 时读 `Keyboard.Modifiers` + `KeyInterop.VirtualKeyFromKey` → `Format` 成 stroke 入 `_pendingStrokes`；`DispatcherTimer` 800ms 空闲 → `FlushPending` 把 pending 作为一个 chord 条目入库；Esc 退出录制（不关对话框）；录制中窗口内所有键 `Handled=true`（不触发按钮/焦点切换）。`Closing` 时若仍在录制则停止。
- ✅ **text 条目复用 sendText 的 `DispatchText` auto 逻辑**：抽出 `DispatchText(a, text, mode)`（不含焦点延时、不含 AppendEnter）供 sendText 动作（前置 50ms 延时后调用）与 sendKeys 的 text 条目（无延时）共用，避免重复实现。
- ✅ **错误 stroke 落 CrashLog 不静默跳过**：chord 执行时逐 stroke `KeyStroke.TryParse`，失败抛 `InvalidOperationException`（消息含步骤序号 + stroke 原文 + 解析错误），由 `ActionExecutor.Run` 顶层 catch 落日志。
- ✅ **行内操作（上移/下移/删除）**：`ObservableCollection<KeyItemView>` + `INotifyPropertyChanged`（序号 `Number` 通知，增删由 ObservableCollection 自动刷新，序号变化触发通知更新）；行内按钮 `DataContext` 取项，`ObservableCollection.Move` 换位。

### 阶段 11 实施中新增（组合集成收尾）

- ✅ **快捷新建菜单 5→7 类**：`SlotMenu.BuildAddMenu` 新增「新建 · 发送文本」「新建 · 模拟按键」（与组合窗口左栏「基础动作」组 8 种保持一致——除 internal/composite 外的所有类型）。超级面板空格右键 / 设置页格子左键两处共用 `SlotMenu`，一次改动两处生效。
- ✅ **`CapsLock++.example.json` 新增 a6/a7 示例**：a6 `sendText` "Hello World"（演示键入）、a7 `sendKeys` 全选复制（chord `ctrl+a` → sleep 100 → chord `ctrl+c`，演示三 种条目类型）。放进超级面板第 7/8 格 + 菜单组「工具」。新用户首次启动即可体验。
- ✅ **`DispatchText` 抽出共享**：sendText 动作（前置 50ms 延时 + AppendEnter）与 sendKeys 的 text 条目（无延时、无回车）共用 `DispatchText(a, text, mode)`，避免两份键入/粘贴逻辑。
- ✅ **组合深拷贝隔离验证**：冒烟构造组合含 sendText 步骤 `Text="原"`，`Clone()` 后改原步骤 `Text="改"`，clone 保持 `"原"` ✓。组合深拷贝后执行（sendText "XY" → ctrl+a → ctrl+c → 剪贴板="XY"）✓。确认 `ActionDto.Clone()` 递归拷贝 `Items`/`Steps`/`Strokes`，组合步骤与池动作完全解耦。
- ✅ **四入口全覆盖**（代码层面）：超级面板（`SlotMenu` 快捷新建 7 类 + 动作池选择）、CapsLock+数字菜单（`MenuSystem` 按 Id 解析 + `ActionExecutor.Run`）、菜单组（同前）、组合步骤（`CompositeActionDialog` 左栏基础动作组 8 种 + 双击/拖入开预置类型编辑器）。`ActionExecutor` 分发 sendText/sendKeys 两分支，`ActionEditorDialog` 类型下拉 + 字段面板，`ConfigHelper.Summarize` 摘要显示——所有按类型分发/显示处均已覆盖，无遗漏 switch/where。

### 交付后 Bug 修复（用户实测报告，阶段 9–11）

- 🐛 **Bug 1：auto 键入英文被中文输入法吞**。根因：初版 auto 对纯 ASCII 走 `VkKeyScanW` → `SendInput` 虚拟键码，中文输入法模式下字母键被 IME 拦截转候选词，文本不完整。
- 🐛 **Bug 2：paste 弄乱剪贴板历史**。根因：「不恢复原剪贴板」决策（与 Quicker 一致）导致用户原复制记录被覆盖。
- ✅ **修复 = auto 改用 `KEYEVENTF_UNICODE` 批量注入**（`InputHelper.SendUnicodeText`）：直接生成 `WM_CHAR`，跳过 `WM_KEYDOWN`→`WM_CHAR` 转换（输入法只拦截这一步），绕过键盘布局与 IME；中英文/emoji 均可发，**不碰剪贴板**——一箭双雕同时修两个 Bug。
  - 实现：批量构造 `INPUT` 数组分批 `SendInput`（每批 500 INPUT ≈ 250 字符，避免单次数组过大）；`\n`→Enter、`\t`→Tab 走 VK（应用解释更可靠）、`\r\n` 归一为 `\n`；码点放 `wScan`（UTF-16 代理对按码元序发送，应用自行重组）。
  - 三模式语义定稿：**auto** = Unicode 注入（默认，无副作用）/ **type** = `VkKeyScanW` 键入（真实按键，受 IME 影响，特殊需求用）/ **paste** = 剪贴板粘贴。
- ✅ **paste 恢复原剪贴板**（修订 §12 需求评审「不恢复」决策，§3.2.2 已更新）：备份原文本 → 写入 → Ctrl+V → `Sleep(150)` 等目标窗口读走 → 恢复。**两道保护**：① 150ms 后仅当剪贴板仍是我们写入的内容才恢复（用户期间新复制则不覆盖）；② 非文本内容（图片/文件）无法经文本接口恢复，此时不恢复（粘贴模式必然覆盖，属固有限制）。
- ✅ **`NeedPaste` 删除**：auto 不再需要判定阈值（100 字符/非 ASCII/换行的规则废弃）。
- ✅ **文案同步**：编辑器发送方式下拉三项改述、sendKeys 加文本提示、`CanType`/`SendTextMode.paste` XML 注释。
- ✅ **冒烟验证**：auto 中英文/emoji/换行直发（中文输入法开启下）✓、type 语义不变、paste 粘贴后剪贴板恢复原内容 ✓、paste 期间用户新复制不被恢复覆盖 ✓。

### 实现提醒（非决策）
- 面板激活与焦点：需 Esc/数字键则面板须取键盘焦点；长按后激活可能与前台应用竞态——现有 `MenuPopup`/`HelpPanel` 已用 `BeginInvoke + Activate` 处理同类竞态，复用即可。
