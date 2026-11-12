# 桌面悬浮：悬浮动作 / 悬浮分组

> 参考 Quicker 2.3 的「悬浮动作」与「悬浮分组」。本文记录已确认的范围、数据模型、
> 交互规格、生命周期与已知限制。实现若偏离，先改本文再改代码。

## 0. 目标与边界

**做什么**

- 把动作（或面板里的一个分组）放到桌面上变成**置顶按钮**：按钮上**图标 + 动作名**，单击运行，拖动移动，右键出菜单。
- 两个入口：「悬浮此动作」与「悬浮此分组」。
- 分组有两条路进来：**悬浮已有分组**，或把两个悬浮按钮**拖到一起**自动成组。
- 悬浮项**全局常驻**：建出来就一直在桌面上，不按前台进程显隐（一期已确认，不做进程绑定）。
- 悬浮项**无状态**（照 Quicker）：只在本次运行里存在，退出即消失，重启不会自己回来。

**不做什么（一期明确排除）**

- 不做「悬浮动作页」（整页面板置顶）、不做悬浮触发按钮 / 屏幕边缘浮标（那是二、三期）。
- 不做悬浮布局的保存 / 备份 / 恢复：**布局本身不落盘**（无状态），只落盘偏好设置。
- 不做「动作里打开 / 关闭某个悬浮分组」的动作单元（Quicker 的 `ControlFloatingGroupPayload`）。
- 不做折叠成圆点、双击识别（`__dbclick`）、透明度滑杆、拖出成员变独立项（用右键「拆分为独立悬浮」代替）。
- 不做数量 / 收费限制，只留防呆上限 `MaxItems = 32`。

## 1. 已确认的决策

| # | 决策 | 结论 |
|---|---|---|
| 1 | 一期范围 | 只做「悬浮动作」+「悬浮分组」，不做悬浮动作页 |
| 2 | 分组来源 | 两条路都做：拖到一起成组 + 悬浮已有分组 |
| 3 | 显示范围 | **全局常驻**，不做绑定进程机制（一期定稿：用户不要按进程显隐） |
| 4 | 悬浮项存哪 | **只在内存**（`FloatingLibrary.Items`），不写 `LTools.json`；落盘的只有偏好 |
| 5 | 一个动作几个悬浮实例 | 只允许一个（`Normalize` 按动作 Id 去重） |
| 6 | 单成员算不算分组 | 不算：成员 = 1 时窗口是单按钮，`Name` 也会被清空 |
| 7 | 按钮形状 | **统一正方形**：边长 = 图标 + 名称行（44 → 62 DIP），长名截断 + ToolTip |
| 8 | 方向 | 分组可切**横向一排 / 竖向一列**（右键菜单「方向」，单按钮无此菜单项） |
| 9 | 靠近另一个悬浮项 | **合并成分组**（不是「贴住一起移动」） |
| 10 | 拆分 | 右键两种：「拆分为独立悬浮（该动作）」与「拆分为独立悬浮（全部）」 |
| 11 | 新项落点 | **鼠标指针处**（照 Quicker）：按钮左上角 = 指针左上 40 DIP；被占就按格子错开 |
| 12 | 悬浮后面板 | **不关**（照 Quicker），可以连着悬浮多个动作 |

第 4 条是用户明确要求的「无状态」：Quicker 的悬浮按钮退出就没了，重开程序不该自己冒出一堆卡片。
偏好（中键关闭、吸附、默认尺寸等）仍然落盘，那是设置不是状态。

## 2. 数据模型

`Config/FloatingConfig.cs`：

```
AppConfig.Floating : FloatingConfig?          // null = 从没用过悬浮（偏好为默认值）
  Preferences : FloatingPreferencesDto
    MiddleClickClose     = true    // 中键关闭
    ShowTooltip          = true    // 悬停显示动作名（名称被截断时有用）
    SnapToEdges          = true    // 拖动吸附屏幕边缘
    SnapDistance         = 12      // 靠近合并阈值（DIP；贴边另有 EdgeSnapDistance = 24）
    DefaultSize          = 44      // 新项按钮边长
    DefaultOpacity       = 1.0
    MaxItems             = 32      // 防呆上限
```

运行期模型（**不落盘**，`FloatingLibrary.Items`）：

```
FloatingItemDto
  Id            // 悬浮项 Id（"f" + 8 位）
  Name          // 分组名；单成员为空
  X, Y          // 窗口左上角（DIP，虚拟桌面坐标，可为负）
  Size          // 按钮边长 28~96
  Opacity       // 0.3~1
  PositionLocked
  Vertical      // false = 横向一排，true = 竖向一列（单按钮无意义）
  Members : FloatingMemberDto[]
    ActionId    // 只存 Id；名称 / 图标每次现查，改动作后自动跟随
```

落盘只有一处：`FloatingLibrary.SavePreferences()`（`ConfigIO.Modify` 只写 `Floating.Preferences`）。
拖动、改尺寸、改方向、合并、拆分都只改内存；**没有** `Items` 写入、没有防抖 `SaveSoon`、没有退出 `Flush`。
旧版本的 `Floating.Items` / `RestoreOnStartup` 反序列化时被忽略（未知字段不报错），下次保存偏好时自然消失。

## 3. 交互规格

**窗口外观**（`Views/FloatWindow.xaml`）

- 无边框、置顶、不进任务栏、透明底 + 圆角卡片 + 阴影。
- 每个按钮 = **正方形**：上面图标区，下面一行动作名（11px、18 DIP 高、超宽按 `TextTrimming`
  截断并挂 ToolTip）。边长 = `size + 18`（44 → 62 DIP），**名称不参与量宽**——否则短名按钮扁、
  长名按钮宽，一排下来参差不齐（用户实测反馈的「第一个正常、后面全是长方形」）。
- 窗口尺寸 = 内容 + 20（阴影 6×2 + 内边距 3×2 + **边框 1×2**）。横向 n 个按钮：
  宽 = 20 + n×62 + (n-1)×2，高 = 82；竖向反过来（82 × 146 / 两成员）。
  尺寸计算只有一处（`FloatWindow.MemberSide` / `MeasureItem` / `Footprint`），
  管理器定位、`Split` 的落点与冒烟都调它。
- **边框那 2 DIP 别漏**：只算阴影 + 内边距时内容正好把窗口撑满，最外侧一条边框会被
  `UseLayoutRounding` 舍掉——表现是右边框和下边框被裁平、右下角连圆角都看不见（实测截图）。
- 多成员时圆角略大。按钮 36 / 44 / 52 / 64（正方形边长 54 / 62 / 70 / 82），透明度 100% / 80% / 60%。
- 动作被删时按钮画成红色缺省图标（但通常先被回收掉了）。

**新项落在哪**（`FloatingManager.DefaultPosition`，照 Quicker）

- 取**鼠标指针**：`GetCursorPos`（物理像素）→ 按该显示器的 DPI 换成 DIP（`MonitorFromPoint` +
  `GetDpiForMonitor`），再减 40 DIP → **按钮左上角在指针左上方 40 DIP**，指针因此落在按钮里偏右下
  （不是中心）。Quicker 的 `zEw7.FloatAction` 调
  `InternalAppHelper.GetTopLeftScreenPositionBasedOnMouseAndVisualOffset(ownerWindow, 40×40)`
  就是这个语义，证据见 §9。
- 这个落点已经被别的悬浮项占了就**按格子错开**（步长 = 卡片宽 / 高 + 8，最多 12 格：先往右、
  排满再往下一行），**不做「一建出来就合并」**——只有用户自己拖到一起才成组。
- 拿不到光标、或整块落在虚拟桌面外（拔了显示器）才回退到主屏右上角（`FallbackPosition`，
  与 `DefaultPosition` 分开，避免互相递归）。

**鼠标**

| 操作 | 行为 |
|---|---|
| 左键单击按钮 | 运行该动作 |
| 左键按住拖动 > 4px | 移动窗口（拖动本身由 `DragMove` 泵消息） |
| 松手 | 贴到**窗口所在那块显示器**的工作区边缘（离边 ≤ 24 DIP 就吸；贴的是可见卡片，透明阴影让到屏幕外）→ **靠近**（矩形间距 ≤ 12 DIP）或压住（重叠 > 被拖项 50%）另一个悬浮项则**合并成组** |
| 中键 | 关闭该悬浮项（可在设置里关掉） |
| 右键 | 弹出菜单 |

- 单击与拖动靠 4px 阈值区分，不用双击。按钮用 `Preview*` 事件（要在 `ButtonBase` 自己处理前接管），
  卡片底板用冒泡事件（按钮把事件标成 `Handled` 后底板就不会跟着抢）。
- **释放鼠标捕获前先置 `_dragging`**：`Mouse.Capture(null)` 会同步派发挂起的 `MouseMove`，
  否则会重复进拖动（同 AGENTS.md 拖动排序那条坑）。
- **靠近就合并**：判定用两矩形的最短间距（`FindMergeTarget`），不必压上去。新项落点按「指针处的格子」
  错开（见上），**不是**按已有数量往下排——所以两个刚建出来的项不会自动挨到合并距离以内。

**右键菜单**（`Features/Floating/FloatingMenus`，每次右键现建，勾选状态永远和当前状态一致）

- 单个：运行「名字」/ 编辑动作…；分组：「运行」子菜单（逐个成员）/「编辑成员…」
- 锁定位置（勾选，锁定后拖不动，防误触；切换时弹 toast）
- 按钮大小（36 / 44 / 52 / 64）、不透明度（100 / 80 / 60%），改完弹 toast
- 分组：**方向**（横向一排 / 竖向一列，勾选当前值）
- 分组：**拆分为独立悬浮（该动作）**（子菜单挑一个成员）/ **拆分为独立悬浮（全部）** / 关闭此分组；
  单个：关闭悬浮
- 关闭全部悬浮
- 没有「显示范围」：悬浮项全局常驻。
- **带子菜单的项点一下就展开**（`Submenu()` 挂 `Click → IsSubmenuOpen = true`），**悬停也立即展开**：
  WPF 默认走系统的「菜单延迟」（`SPI_GETMENUSHOWDELAY`，常见 400ms），滑过去要等半秒，跟手很差。
  `FloatingMenus.Submenu()` 在 `MouseEnter` 里直接展开（`HoverOpenMs = 0`）；
  想留一点容错（扫过父项时不闪开）把常量调到 60~100 即可，走同一个旋钮。
- **收起也得自己做**（`CollapseIfPointerOutside`）：子菜单一开，WPF 就把「离开父项 → 撤高亮 + 收子菜单」
  挂到 `MenuShowDelay`（默认 400ms）的计时器上；指针从父项**直接移出整个菜单**时它连计时器都不起，
  高亮和子菜单会一直挂着（实测移出 400ms 后 `IsHighlighted` 仍是 true，直到你点别处）。
  所以离开父项 / 离开子菜单时按坐标判断：指针不在「父项 + 自己的子菜单」里就立刻收起，
  并补发一次 `MouseLeave` 让 WPF 立刻撤销 `IsSelected`（否则 `IsHighlighted` 会留在最后一项上）。
  指针斜着进子菜单时，离开父项那一刻已经在子菜单矩形里（popup 与父项重叠 4px），所以不会被误收。

**菜单能不能点，取决于 `Themes/Modern.xaml` 的 MenuItem 模板**：模板里必须同时有
`PART_Popup`（子菜单）、勾选列（`IsChecked` → ✓）、箭头列（`HasItems` → ›）。
曾因为模板只有一个 `Border + ContentPresenter`，**全应用的子菜单都打不开**、勾选状态也看不见，
表现就是「菜单弹出来了，点带箭头的项没反应」。改模板后冒烟里有一条断言直接查 `PART_Popup`。

**菜单卡片不要加 `DropShadowEffect`**：`MenuCard` 每次 hover 变色都要重做一遍整卡模糊，
实测 5.1ms/帧（无阴影 1.7ms/帧，3 倍），在透明置顶 popup 上就是「hover 背景跟手慢半拍」。
层次感用 1px `BorderStrongBrush` 描边给。hover 高亮本身是跟着光标走的（真项之间移动实测 1~2 帧内换过去），
慢的不是颜色，而是子菜单收起——那条走的是上面的 `CollapseIfPointerOutside`，别去改 `IsHighlighted`。

**点菜单外面要能关掉，只能靠鼠标钩子**：悬浮窗永远不是前台窗口（`WS_EX_NOACTIVATE`），
它的 ContextMenu popup 拿不到**有效**的鼠标捕获——Windows 只让前台窗口捕获「窗口之外」的
点击，实测点外部一条鼠标消息都收不到，WPF 自带的点外关闭在它身上不可能触发。
所以照超级面板 / 快捷菜单的做法，在 `Hooks/MouseHook` 里按坐标判定
（`FloatingManager.IsMenuOpen` / `MenuContains` / `CloseMenuIfOutside`，菜单矩形取 popup 的
`GetWindowRect`，物理像素）：左键点菜单外关掉并**放行**这一下点击；右键点菜单外关掉并
成对吞掉 down + up（不放行，免得漏出原生右键菜单）；点击落在菜单里则不进长按手势。

**`MenuContains` 必须把已展开的子菜单也算进来**：子菜单是**另一个 popup 窗口**，只查根菜单矩形时，
点「按钮大小 / 方向 / 拆分为…」的子项会被当成「点菜单外面」——钩子先把菜单关了，这一下点击随之丢掉，
表现就是「子菜单里的项点了没反应」（方向调不动、拆分该动作无效）。实现是根菜单矩形 + 所有
`IsSubmenuOpen` 项 `PART_Popup.Child` 的窗口矩形；冒烟里专门有「真实钩子 + 真实鼠标」点这两项。

**不抢焦点是硬要求**：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` + `ShowActivated=False`
（`SourceInitialized` 里补 ex-style）。否则点一下悬浮按钮，前台就变成悬浮窗自己，
接下来注入的键鼠会打到它身上（`ToastWindow` 踩过同类坑）。同理，点击只把动作投递给
`ActionExecutor`（自己 spawn 后台线程），UI 线程不做任何阻塞操作。

**永远显示在超级面板前面**（照 Quicker）：两边都是 `Topmost`，同一层里**谁最后
`SetWindowPos` 谁在前**——面板一显示 / 一激活就把悬浮按钮盖住了。所以
`SuperPanel.Show` 末尾、面板的 `Activated`（含录制后 `RestoreAfterRecording`）都调
`FloatingManager.RaiseAllAbove()` → 每个窗口 `SetWindowPos(HWND_TOPMOST,
SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)` 顶回最前面。`SWP_NOACTIVATE` 保证只改 z 序、
不会把焦点从面板抢走（悬浮窗本来就是 `WS_EX_NOACTIVATE`）。

还有两条「面板会把悬浮按钮盖住」的路径，一起堵掉：

- **在面板里按下鼠标**：面板即使已经是活动窗口，Windows 也会把它提到 topmost 带的最前面（不触发
  `Activated`）。典型场景就是「面板打开 → 悬浮一个动作 → 直接右键面板里的动作」——所以面板的
  `PreviewMouseDown` 也顶一次（左键 / 右键都覆盖）。
- **面板的右键菜单关掉之后**：焦点回到面板时可能顺手把它提到最前，`TrackInteract` 的
  `menu.Closed` 里再顶一次。

面板自己的右键菜单 / 对话框是另一个窗口，弹出时不在这些路径上，不会被无故顶下去（菜单打开时仍然
盖在悬浮按钮上面，方便点）。

## 4. 入口

| 位置 | 菜单项 |
|---|---|
| 面板磁贴右键 | 悬浮此动作 / 取消悬浮（分组里只摘掉自己）/ 显示悬浮 |
| 面板分组右键 / 分组标题右键 | 悬浮此分组（空组或动作都已在桌面时置灰） |
| 设置「面板管理 → 常规 → 悬浮动作」 | 四项偏好开关 + 关闭全部悬浮项 |
| 托盘 | 关闭所有悬浮项 |
| 悬浮窗右键 | 见上一节 |

悬浮项全局常驻，所以入口不需要知道前台程序。从面板点「悬浮此动作 / 悬浮此分组」后**面板不关**
（照 Quicker）：可以接着悬浮下一个，不用重新唤面板；要关面板照旧点空白处 / 中键 / Esc。

## 5. 生命周期

- 启动：`App.OnStartup` → `FloatingLibrary.Initialize(cfgPath)`（读偏好 + 夹紧取值范围）→
  `FloatingManager.Initialize()`（**什么都不建**：悬浮项无状态，等用户手动悬浮）。
- 全局常驻：没有进程轮询、没有显隐切换；窗口建出来就一直显示，只有用户关它才消失。
- 置顶优先级：悬浮窗始终在超级面板前面（面板 `Show` / `Activated` 时 `RaiseAllAbove()` 重新顶一遍，
  见 §3）。
- 删除动作：`ConfigStore.RemoveAction` → `FloatingManager.RefreshAll()` → `CollectGarbage`
  摘掉失效成员，成员清空的悬浮项整项删除（与宏文件 / 模板截图同一种「按引用回收」）。
- 窗口关闭（`OnClosed`）→ `FloatingManager.ForgetWindow`：字典里不留死窗口；
  同时关掉右键菜单，避免 popup 变成点不掉的孤儿。
- 退出：`FloatingManager.Shutdown()` 只关窗（没有要落盘的悬浮项）。
- 应用是 `ShutdownMode=OnExplicitShutdown`，关掉最后一个悬浮窗不会退出 LTools。

## 6. 已知限制

- **多屏 / 混合 DPI**：位置按 DIP 存在虚拟桌面坐标系里；贴边按窗口中心所在显示器的 `rcWork`
  （`MonitorFromPoint` + `GetMonitorInfo`，物理像素 → 窗口自己的 DIP 换算）算，多屏时中间那条边界
  也能吸；`rcWork` 天然避开任务栏。125% 缩放下 62 DIP = 77.5 px，窗口尺寸取整后回算成 62.4 DIP（正方形关系不变）。
- **贴边留白**：窗口四周留 6 DIP 透明阴影（`FloatWindow.Shadow`），所以贴边时窗口坐标会往屏幕外让
  这 6 DIP（`FloatingManager.CardInset`），贴住屏幕的是可见卡片而不是阴影。
- **拖出屏幕也会吸回来**：贴边距离是**有符号**的（`dLeft = 窗口 Left − 贴边位置`），越过边缘算 0 距离，
  所以把按钮推到屏幕外松手照样贴回边；只有「离边还差一点」才看 `EdgeSnapDistance = 24`。
- 拖出成员变独立项没做（用右键「拆分为独立悬浮」）。
- 方向只对分组有意义：单按钮横竖一样，菜单里不出现「方向」。
- 悬浮项没有「吸附到一起后一起移动」（Quicker 的 `BtnSnapLink`）：靠近直接合并成分组。
- 布局不保存：重启后要从面板菜单重新悬浮（这是「无状态」的预期行为，不是缺陷）。

## 7. 文件清单

| 文件 | 职责 |
|---|---|
| `Config/FloatingConfig.cs` | 偏好 DTO（唯一落盘部分）+ 运行期 `FloatingItemDto` / `FloatingMemberDto` |
| `Features/Floating/FloatingLibrary.cs` | 内存 `Items`、`SavePreferences`、结构操作（FloatAction / FloatGroup / Merge / Split / SplitMember）、`CollectGarbage`、`ClampPreferences` |
| `Features/Floating/FloatingManager.cs` | 窗口生命周期、`ToggleAction` / `FloatGroup` / `Close` / `CloseAll` / `RefreshAll` / `ForgetWindow`、默认落点 / 夹取 / 吸附 / 靠近合并判定 / 菜单点外关闭 / 置顶优先级（`RaiseAllAbove`） |
| `Features/Floating/FloatingMenus.cs` | 右键菜单（方向、两种拆分；子菜单父项点一下即展开） |
| `Views/FloatWindow.xaml(.cs)` | 正方形按钮（图标 + 名称）、尺寸计算（`MemberSide` / `MeasureItem`）、横/竖排布局、单击 vs 拖动、右键、中键、菜单生命周期、`RaiseToTop`（置顶优先级） |
| `Features/SuperPanel.cs` | 面板显示 / 激活后把悬浮项顶回最前面（`RaiseAllAbove`） |
| `Themes/Modern.xaml` | **全局 MenuItem 模板**（`PART_Popup` + 勾选列 + 箭头列）、`MenuCard`（**无阴影**） |
| `Views/SuperPanel.xaml.cs` | 磁贴 / 分组菜单入口 |
| `Views/Controls/PanelManageView.xaml(.cs)` | 设置里的悬浮偏好卡 |
| `Config/AppConfig.cs` | `Floating` 字段 |
| `App.xaml.cs` | 初始化 / 托盘 / 退出 |

## 8. 验证方式

无测试项目，用外部无头冒烟（`%TEMP%\opencode\floating-smoke`，反射驱动，**132 项断言**）覆盖：

- **无状态**：启动后 `Items` 为空；`FloatAction` / `FloatGroup` / 合并 / 拆分 / 回收之后
  `LTools.json` 里都不出现 `Items`；`SavePreferences` 仍然只写 `Preferences`。
- 结构：幂等、跳过已悬浮、上限、按引用回收、`ClampToScreen`。
- **默认落点**：把光标放到 (900, 520) 再悬浮，窗口左上角 = `指针/DPI缩放 − 40`（±2 DIP）；
  不移动光标再悬浮一个，两个窗口矩形不重叠（同落点会自动错开）。
- **尺寸**：单按钮 82×82；横排两成员 146×82、竖排 82×146；`Footprint` 是正方形；
  实际窗口尺寸 = 布局计算值；按钮 `ActualWidth == ActualHeight`；
  内容有没有被窗口裁掉（`MemberHost` 实际可用尺寸 ≥ 内容，旧版漏算边框这条会红）。
- **靠近合并**：间距 8 DIP 拖过去 → 合并成一个两成员分组、只剩一个窗口；相距 40 DIP 不合并。
- 窗口：`WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` 是否真加上、显示时没有抢前台、按钮上是否真有动作名。
- **菜单**：有「方向」且默认勾选横向，点「竖向一列」窗口立刻变 82×146、点「横向一排」变回去；
  「拆分为独立悬浮（该动作）」子菜单列出每个成员、点了真的拆开并建窗口；「（全部）」存在；
  单个菜单没有「方向」/「（全部）」，但「关闭悬浮」在。
- **真实鼠标点击**子菜单父项（断言 `PART_Popup` 存在、点一下就展开、点子项真的生效、点完菜单关闭）。
- **真实钩子 + 真实鼠标**点子菜单项（`RealSubmenuPhase`）：每次点击都先造一条非注入的
  `WM_LBUTTONDOWN` 喂给 `MouseHook.HookCallback`（这就是「点外关闭」的判定入口），再注入真实左键。
  断言点「方向」子菜单还在、点「竖向一列」窗口真变 82×146、点回「横向一排」、
  点「拆分为独立悬浮（该动作）」的成员真拆成两个单成员窗口。退回「只认根菜单矩形」的旧实现，
  这 5 条会红（已实测）。
- 「点外部关菜单」直接反射调真实的 `MouseHook.HookCallback`（造非注入的 `MSLLHOOKSTRUCT`），
  验菜单内不关 / 菜单外关 / 右键关时成对吞掉 down + up。
- **置顶优先级**（`ZOrderPhase`）：先造一个「后显示的同层 topmost 窗口」等价模拟面板（前置断言它确实
  在悬浮窗前面），调 `RaiseAllAbove` 后断言悬浮窗反压回去；再**真的打开超级面板**
  （`Features.SuperPanel.Show(IntPtr.Zero)`），用 `GetWindow(GW_HWNDPREV)` 走 z 序断言悬浮窗在面板
  前面，并把悬浮按钮挪到面板矩形里做一张 `BitBlt` 真·屏幕截图（`panel-with-float.png`，
  被盖住时只会截到面板自己的内容）。还覆盖用户实测的路径：`SetWindowPos` 把面板顶到最前（等价于
  「在面板里按了一下」）→ 断言面板在前 → 触发面板的 `PreviewMouseDown` → 断言悬浮窗又回到前面。
- **悬停开子菜单**：合成 `MouseEnter` 后 `IsSubmenuOpen` 立即为 true（`HoverOpenMs = 0`；系统菜单延迟
  是 400ms，走系统那条路就会红）。
- **悬停 / 移出**（`HoverPhase`，真实鼠标移动）：悬停父项 → 立即展开 + 高亮；挪到相邻项 →
  **子菜单与高亮立刻消失**（不等系统的 400ms）；指针斜着进子菜单 → 父项保持展开 + 高亮；
  **从子菜单直接移出整个菜单 → 立刻收起 + 撤高亮**（回归守卫：这一条在修好之前会红，
  表现为「鼠标早走了，高亮和子菜单还挂着」）。
- **点面板外部不吞鼠标事件**（`PanelOutsidePhase`，真钩子回调）：右键 / 左键点面板外部都要
  **关面板 + `HookCallback` 返回 0（放行）**，右键的配套 `up` 也必须是 0（`_rightPassUpPending`）。

渲染成 PNG 供人眼核对：`float-single.png` / `float-group.png` / `float-menu-vertical.png` /
`float-menu-size.png` / `menu-root.png` / `menu-submenu.png` / `settings-floating.png`，
另外三张是 **`BitBlt` 真·屏幕截图**（`float-single-screen.png` / `float-group-screen.png` /
`float-vertical-screen.png`，坐标取 `GetWindowRect` 的物理像素）——`RenderTargetBitmap`
画的是可视树、不受 HWND 裁切，看不出「边框被裁掉」和真实 DPI 取整。

## 9. Quicker 2.3 证据（逆向，非文档抄写）

本机 `C:\Program Files\Quicker\Quicker.dll`，sha256 `73bd4aa027da08d004630be3dd7c46ccb6156829fad5dc3afd2f66492fec6fd5`。

- 元数据里与本功能同属一套的类型：`Quicker.Domain.Floating.{FloatState, FloatItemState,
  FloatItemType, IFloatItemWindow, SnapLinkType, ViewMode, BtnSnapLink, BtnSnapLinkMgr}`、
  `Quicker.View.FloatButtonWindow`、`Quicker.View.Controls.{FloatActionMemberControl,
  FloatActionMemberRequestKind, FloatActionMemberRequestEventArgs, ViewModels.FloatButtonVm}`、
  `Quicker.Settings.Pages.FloatActionSettingsPage`、`Quicker.Domain.Messages.CloseAllFloatingActions`。
- `Quicker.Common.V2.Settings.FloatSettings` 的 7 个偏好字段：`GroupsEnabled`、`LoadButtonState`、
  `ButtonBindProcessByDefault`、`PanelPinnedByDefault`、`UseLeftButtonOnPanel`、
  `DisableMiddleClickClose`、`FixButtonSize`（前四项与中键关闭本仓库已对位）。
  `LoadButtonState` 是「启动时是否恢复悬浮按钮」，本仓库按用户要求**不恢复**（无状态），故未做。
- `ControlFloatingGroupPayload { Open, Close, Toggle, GroupName, Action }` = 动作里控制悬浮分组显隐，
  一期没做。
- UI 文案（#US 堆）：`此动作不支持悬浮。`、`请保存动作后再悬浮。`、`无法确定悬浮动作的位置。`、
  `将悬浮动作拖到一起即可创建分组`、`按当前位置拆分为独立悬浮动作，并保留分组的进程绑定。`、
  `超出 256 个动作…未创建分组`。→ 本仓库的上限取 32（防呆），拆组保留动作（不绑进程）。
- **新悬浮项的位置**（ilspycmd 反编译方法体，未用网页文档）：`zEw7.FloatAction(action, ownerWindow)`
  调 `JKzU.fKzu(action, InternalAppHelper.GetTopLeftScreenPositionBasedOnMouseAndVisualOffset(ownerWindow, new Point(40, 40)), …)`；
  helper 的实现是 `GetCursorPos()`（物理像素）再减去 `offset / TransformFromDevice.M11`（即按当前 DPI
  缩放换算的 40 DIP）→ **按钮左上角在指针左上 40 DIP**。新建窗口 `Location = P_1`（就落这个点），
  尺寸取上个分组成员尺寸或 `FloatSettings.ButtonSize`。另一处 `RERk(Guid)`（把分组布局收回屏幕）
  用的是 `Screen.FromPoint(Cursor.Position).WorkingArea + (32, 32)`——本仓库的贴边 / 回屏仍按虚拟桌面夹取，
  没有照抄这个 32 的偏移。
- 取证限制：REA 的托管提供者在 50 MB 的 `Quicker.dll` 上卡 64 MB 表示预算（只出类型清单），
  Ghidra 明确拒绝托管 PE，所以方法体只能用 `ilspycmd` 取；上述行为结论来自类型清单 + 同一制品的
  `#Strings`/`#US` 堆与打包 XAML 资源，未使用网页文档。
