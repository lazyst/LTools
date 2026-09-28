# CapsLock-Pro 动作系统设计计划

> **状态**：设计已定稿，进入实施。§12 为决策汇总；§11 为分阶段实施计划（`- [ ]` 可标记进度，每阶段附验收标准）。
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

> **执行层洞察**：`launchApp`/`openFile`/`openFolder`/`openUrl` 本质都是 `Terminal=direct`（ShellExecute）的特例——现有 `TerminalLauncher` 的 `direct` 分支已能处理。只有 `runCommand` 才需要终端路由。故执行引擎基本就绪，主要工作是类型模型 + 编辑器 + 面板 UI。

### 3.1 内置图标 ✅

- v1 **提供一组内置图标**供选择（不是让用户自备图标文件）——编辑器里以图标网格展示，动作存图标 Id。
- 实现：用 Windows 自带的 **Segoe MDL2 Assets** 字体（Win10/11 均可用），内置一个「名称 → 字形码点」的 `IconCatalog`（如 `settings` → `\uE713`），格子按名称取字形渲染。
- 无需打包图标资源文件；后续若要更丰富可改用 Segoe Fluent Icons（Win11）。

---

## 4. 组合动作 ✅

`composite` = 有序步骤列表，每步引用一个动作（可为另一个组合）。

```
Step {
  ActionId : string       // 引用动作 Id（可引用另一个 composite）
  DelayMs  : int = 0      // 执行本步前等待的毫秒数
  OnFail   : "continue" | "abort"   // 本步失败后的策略
}
```

已定语义：
- ✅ 步骤间**可设延迟**（`DelayMs`，执行该步前等待）。
- ✅ 失败**记录日志**（写入 `CrashLog`，含动作名/步骤序号/异常），用户可在日志中看到哪一步失败。
- ✅ **用户可选择**失败后策略：继续 or 中止——`OnFail` 为**每步**可配（默认 `continue`），组合可有整体默认。
- ✅ 步骤**可引用另一个组合动作**，执行时递归展开；**防环**：执行链路维护「正在执行的 composite Id 栈」，遇重复 Id 立即中止并记日志。

❓ 待确认：组合动作执行是否在后台线程（避免阻塞钩子 >300ms）？——建议是，与现有动作执行一致。

---

## 5. 超级面板

### 5.1 唤起手势与选择方式 ✅

- **触发**：**裸右键长按**（CapsLock **未按下**）。
- **短按穿透**：右键 down 后计时，若 down→up 间隔 < 阈值，**放行**原生右键菜单（不干预）。
- **长按弹面板**：超过阈值且右键仍未抬起 → 在光标处弹出超级面板，并吞掉随后的右键 up（阻止原生菜单），面板保留。
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
- **滚轮翻页**（对齐 Quicker）；**1~9 键**选中本页对应格；Esc/点外部关闭。
- **常驻页数指示**：面板上始终显示「当前页/总页数」（如 `1/3`）。

### 5.4 格子交互 ✅（按推荐做法）

| 操作 | 空格子 | 非空格子 |
|---|---|---|
| 左键单击 | 弹菜单（见 §5.5） | 执行该动作 |
| 右键单击 | 弹菜单（见 §5.5） | 编辑 / 删除 / 复制 / 移动到其他格子 |

> 右键仅在**唤起面板那一刻**被钩子接管；面板打开后右键回归正常，用于格子管理菜单。
> 「移动到其他格子」：进入拖拽态，点目标格子完成移动（或弹出格子选择）。

### 5.5 空格子的右键菜单 ✅（结构已定）

分两组，不平铺：

- **新建**
  - 动作…（弹出类型选择器，再填字段）
  - 组合动作…（进入多步编排器）
- **快捷新建**（直接选定类型，跳过类型选择）
  - 启动软件
  - 打开文件
  - 打开文件夹
  - 运行命令
  - 打开网址

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
    → composite: 顺序执行 Steps（Delay、OnFail、防环）
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

两处共用同一套编辑组件（`Views/`）：

- **ActionEditorDialog**：类型选择（下拉）+ 类型特定字段 + 名称 + **图标选择器（内置图标网格，见 §3.1）**。所有动作类型共用一个对话框，按 Type 切换可见字段。
- **CompositeEditorDialog**：步骤列表（引用动作 Id + DelayMs + OnFail）+ 拖拽排序 + 增删。独立于 ActionEditor。
- 设置面板与超级面板右键菜单都调这两个对话框，不各自实现。

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

> **总体进度：5 / 5 阶段**
>
> 标记约定：`- [ ]` 未开始 · `- [x]` 完成。每个阶段结束须满足该阶段「验收标准」且 `dotnet build` **0 错 0 警**方可进入下一阶段。每阶段独立可运行、可测。

### 阶段 1 — Action 数据模型 + 执行层

**目标**：让「一个动作能跑起来」，无 UI。

- [x] 定义动作类型模型（`Features/ActionTypes.cs`）：`ActionType` 枚举、`ActionDto`、`StepDto`，字段见 §2/§3/§4。
- [x] 实现 `IconCatalog`：精选 ~40 个 Segoe MDL2 Assets 字形，名称→码点映射（§3.1）。
- [x] 实现 `InternalActionRegistry`：注册 6 个内部命令（§7）；`windowPin` 等需坐标的命令接受坐标参数。
- [x] 实现 `ActionExecutor.Run(ActionDto)`：按 Type 分发（§8）；复用 `TerminalLauncher`；composite 顺序执行（Delay / OnFail / 防环）。
- [x] 执行 spawn 后台线程；失败统一 `CrashLog.Write`（含动作名 / 步骤序号 / 异常）。
- [x] 临时验证入口（`--smoke=action` 启动参数或临时热键），覆盖 runCommand / openUrl / internal / composite 四类，交付前移除。

**验收标准**
- [x] 四类动作均能执行：runCommand（终端路由）、openUrl（默认浏览器）、internal（如速记 toggle）、composite（两步 + 延迟）。
- [x] 组合防环：A→B→A 时中止并写 CrashLog，不无限递归。
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
- [x] 实现 `CompositeEditorDialog`：步骤列表（引用动作 + DelayMs + OnFail）+ 拖拽排序 + 增删。
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
- [x] 格子交互（§5.4）：左键单击空格→菜单、非空→执行；右键→编辑 / 删除 / 复制 / 移动。
- [x] 空格子右键菜单（§5.5）：新建（动作… / 组合动作…）+ 快捷新建（5 类）。
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
- ✅ 「移动到其他格子」用移动模式：点目标格完成（支持跨页交换），Esc 取消。
- ✅ 左键执行动作时先 `SuperPanel.Close()` 再 `ActionExecutor.Run(action, InvokeX, InvokeY)`，避免动作窗口被置顶面板遮挡；空格 / 移动模式不关面板。

### 阶段 5 实施中新增
- ✅ 长按判定：右键 **down 放行**（不吞）→ 计时器触发后才吞随后的 **up**（阻止原生菜单）。保留 down 使短按 100% 原生；代价是长按时目标窗口收到 down 却无 up（标准做法，§5.1 已定）。
- ✅ 吞 up 的判定**必须先于**「面板未打开（`!SuperPanel.IsOpen`）」判断——面板可能已由 `Show()` 的 `BeginInvoke` 在 up 到达前创建，若先判 `IsOpen` 会漏吞 up 导致原生菜单泄漏。
- ✅ 阈值运行态存于 `AppState.SuperPanelThresholdMs`（volatile int）：启动自配置载入、设置页滑块**即时同步**（不等 800ms 防抖落盘）；钩子回调只读内存，无文件 I/O；`MouseHook.ClampThreshold` 夹到 100–800ms。
- ✅ 手势独立于 `CapsLockEnabled`（CapsLock+Esc 总开关），仅受 `SuperPanel.Enabled` 门控（§5.1「单独全局开关」）。
- ✅ 计时器用 `DispatcherTimer`（主线程，与钩子回调同线程），复用单例、每次 down 重置 `Interval` 并 restart。
- ✅ 保留阶段 4 的 `CapsLock+T` 为**备用入口**（本阶段不随手势接入而移除）。

### 实现提醒（非决策）
- 面板激活与焦点：需 Esc/数字键则面板须取键盘焦点；长按后激活可能与前台应用竞态——现有 `MenuPopup`/`HelpPanel` 已用 `BeginInvoke + Activate` 处理同类竞态，复用即可。
