# LTools 更新日志

所有项目的重要更改都将记录在此文件中。

## \[v2.2.4\] - 2026-10-02

### 🐛 修复

- **设置窗口置顶死锁**：从超级面板 / 快捷菜单打开动作选择器等对话框时，若设置窗口开着，会出现「对话框被设置窗口盖住、设置窗口又被模态对话框禁用」的死锁。根因：`SuperPanel.RunDialog` 弹对话前撤销了面板 `Topmost`，而设置窗口此前自身 `Topmost="True"`。修复：对话框一律继承 Owner 的 `Topmost`（`ConfirmDialog` / `InputDialog` / `ChordPickerDialog` / `ActionPoolPicker` / `ActionEditor` 各 `Show` 助手），`SuperPanel.RunDialog` 不再撤下面板 `Topmost`；设置窗口本身去掉 `Topmost`。至此仅速记 / 帮助面板 / 超级面板 / 快捷菜单 / 鼠标提示保持置顶。
- **设置页列表悬停行高跳动**：动作 / 菜单组 / 菜单项列表的悬停删除按钮原以 `Visibility` `Collapsed`↔`Visible` 切换，组 / 项单行内容约 19px、被 24px 按钮撑高 → 鼠标扫过时行高跳动、列表抖动。改为 `Hidden`（保留布局位），行高恒定，悬停只让按钮显现。

### ✨ 新增

- **列表行内悬停删除**：动作 / 菜单组 / 菜单项每行悬停显示 `✕` 单条删除（带确认），取代原底部「删除」按钮——就地删更直观。
- **`sendKeys` 输入条目拖动重排**：行首把手 `≡` 拖动排序，幽灵跟随 + 插入指示线 + 松手滑到位，与组合动作 / 菜单项编辑器同款交互（弃用 OLE DoDragDrop，避免系统 drag image 与自绘幽灵叠加）。原「上移 / 下移」按钮移除。
- **`sendKeys` 输入条目双击编辑**：双击步骤行（或点 `✎`）即可改——组合键走选键对话框（预填已有 stroke）、内联文本 / 延时走输入框，不必删掉重建。
- **菜单页快捷新建动作**：菜单项工具栏新增「新建动作…」「新建组合动作…」，新建后自动加入当前菜单组（当前无组则先建「新组」），与「从动作池添加」同一处闭环。

### 🎨 界面

- **快捷菜单移除「上移 / 下移」**：菜单项本就有拖动重排，两按钮重复且占工具栏空间。
- **默认配置精简**：全新解压的示例只保留 4 个动作——**ipconfig**（运行命令）、**设置**（`settings.toggle`，新增内部命令入口）、**速记**、**帮助面板**；超级面板 4×4 首行放这 4 项，快捷菜单 CapsLock+1「常用」同样这 4 项，其余留空。移除原示例的 ping / 浏览器 / 输入问候 / 全选复制 / 组合动作「查IP并打开浏览器」。

## \[v2.2.3\] - 2026-10-02

### 🐛 修复

- **启动瞬间鼠标卡顿**：`App.OnStartup` 原先在装好低级鼠标钩子后才做重活（JSON 解析、动作注册、速记文件迁移、弹启动提示窗），而低级钩子要靠本线程泵消息才被调用——`OnStartup` 在 Dispatcher 开始泵消息之前同步执行，主线程不泵消息致鼠标事件挂起→启动瞬间卡一下。改为重活前置、钩子最后装、启动提示改在装钩子前显示（首个 WPF 窗口的首帧渲染栈初始化落在无钩子阶段）；并在装钩子前预热回调 JIT（`MouseHook/KeyboardHook.WarmUp`，以非动作 nCode 调一次促使整方法体提前编译），消除首个输入事件触发 JIT 的一次性停顿。顺带保证 AppState / 动作清单在钩子首次回调前已就绪。
- **动作池选择器未屏幕居中**：`ActionPoolPicker` 原 `WindowStartupLocation="CenterOwner"`，从超级面板（贴光标处）打开时贴着面板而非屏幕居中。改为 `CenterScreen`，与 `ActionEditorDialog` 等其它对话框统一；设置页 3 处调用点位置基本不变（owner 本身已居中）。

## \[v2.2.2\] - 2026-10-02

### 🐛 修复

- **超级面板 / 菜单弹窗文字发糊**：`SuperPanel` 与 `MenuPopup` 原用 `AllowsTransparency="True"`（透明窗口），WPF 对透明窗强制灰度抗锯齿、禁用 ClearType，再叠加 `DropShadowEffect` 软件渲染与亚像素错位，小字号下肉眼发糊。改为不透明 `WindowStyle="None"` 窗口（与本应用所有对话框一致），移除 `DropShadowEffect` 与 `Opacity` 淡入（淡入会触发 `WS_EX_LAYERED` 分层窗、再禁 ClearType），加 `UseLayoutRounding` + `SnapsToDevicePixels`；ClearType 恢复、硬件加速文本渲染回归。圆角改由卡片 `Border CornerRadius` 在同色底上自绘（白底圆角描边），无阴影留白故窗口宽度同步收窄（面板 406→386、菜单 300→280，格子 / 内容尺寸不变）。`MouseTipWindow`（单行深色提示）保留透明圆角不动。
- **快捷菜单关闭黑屏闪一下**：`MenuPopup` 关闭走 `Opacity` 淡出，不透明窗口上 `Opacity<1` 会把窗口临时变成 `WS_EX_LAYERED` 分层窗，而 DWM 销毁分层窗口时会短暂闪烁——与本应用 v1.4.3 AHK 版「菜单关闭闪烁修复」同一根因（彼时以 `WinHide` 规避，WPF 移植版未移植该步）。移除淡出 `Opacity` 动画，窗口全程不透明、非分层，销毁无黑闪。

### 🎨 界面

- **全局字号调大 +1 并集中为资源**：正文 13→**14**（对齐 Windows 11 Fluent / WinUI 3 标准 body）、次要 12→**13**、提示 11→**12**、标题 15→**16**，全应用统一（超级面板 / 快捷菜单 / 设置 / 帮助面板 / 速记 / 各对话框）。字号提为 `Modern.xaml` 的 `FontSizeBody` / `FontSizeSecondary` / `FontSizeHint` / `FontSizeTitle` 四个资源，此后调整只需改这四行。此前 `SuperPanel` / `MenuPopup` 的 Window 未设字号、文字继承 WPF 默认 12（比别处小一号），一并修正。字号为 DIP，随 PerMonitorV2 系统缩放自动放大，无需额外适配。

## \[v2.2.1\] - 2026-10-01

### 🐛 修复

- **托盘右键菜单错位**：全屏游戏（如英雄联盟）切回后，托盘右键菜单不贴鼠标、重启恢复。根因：`app.manifest` 此前无 DPI 感知声明，进程以 System DPI Aware 运行、不处理 per-monitor DPI 变化，全屏游戏切显示环境后 H.NotifyIcon 的物理像素光标坐标→WPF 设备无关像素换算比例陈旧。修复：manifest 加 `PerMonitorV2` DPI 感知（Release 进程创建即生效）；Debug（`-p:NoWin32Manifest=true` 剥了清单）改由 `Core/DpiInitializer` 的 `[ModuleInitializer]` 在 Main 前、WPF 加载前 `SetProcessDpiAwarenessContext` 补设。主屏 125% 外观不变，差别仅跨显示器 / DPI 变化时按当前屏正确重算。

## \[v2.2.0\] - 2026-10-01

### ✏️ 应用改名 LTools

动作系统落地后功能远超「CapsLock 增强」，原名 CapsLock++ / 仓库 CapsLock-Pro 名实不符，全面改名 **LTools**：

- **覆盖范围**：应用显示名（托盘提示 / 启动提示 / 退出菜单 / 帮助面板标题）、`LTools.exe`、C# 根命名空间 `LTools`、GitHub 仓库 [`lazyst/LTools`](https://github.com/lazyst/LTools)（旧地址自动跳转）、发布包 `LTools-v2.2.0-win-x64.zip`、自启计划任务名、单实例互斥体、崩溃日志 `LTools-crash.log`。
- **配置自动迁移**：旧 `CapsLock++.json` 首启自动同目录改名为 `LTools.json`（内容原样保留）；更旧的 `CapsLock++.ini` 迁移链不变。发布包只带 `LTools.example.json`，升级解压不覆盖用户配置。
- CapsLock 键位与热键不受影响（CapsLock 单击 / CapsLock+Esc / CapsLock+空格……那是键名，不是应用名）。

### ✨ 新增：动作系统

本版本核心，设计与阶段记录见 `docs/ACTION_SYSTEM_PLAN.md`：

- **统一动作模型（阶段 1-2）**：8 种动作类型——启动程序 / 打开文件 / 打开文件夹 / 打开网址 / 运行命令 / 发送文本 / 模拟按键 / 组合动作；菜单项与超级面板槽位一律改为引用动作 Id，`ActionExecutor` 统一执行，菜单与面板共用一套动作池。
- **设置面板重做（阶段 3）**：左侧导航 + 动作 / 超级面板编辑器，导航记忆上次页面，动作列表拖动排序。
- **超级面板（阶段 4-7）**：4×4 格子（原 3×3）+ 多页 + 槽位动作引用；长按右键唤起手势；唤起时光标居中于格子；翻页循环 + 过渡动画 + 记住上次页（跨重启持久化）；格子拖动重排（交换语义 + 跨页 + 幽灵跟随）。
- **sendText 发送文本（阶段 9）**：`auto` 模式纯 ASCII 键入、否则 `KEYEVENTF_UNICODE` 注入（绕过中文输入法吞字）；`paste` 模式粘贴后 150ms 恢复原剪贴板，不弄乱复制历史。
- **sendKeys 模拟按键（阶段 10）**：`KeyStroke` 解析器 + 录制 UI，条目支持 chord 组合键 / 内联文本 / 延时，如 `ctrl+a` → 100ms → `ctrl+c`。
- **组合动作**：多步骤串行 + 失败策略（继续 / 中止）；专属编辑窗口（步骤拖动重排、工作目录浏览、屏幕居中）。阶段 11 收尾：基础动作组 8 种、快捷新建入口、example 示例动作，新用户开箱可玩。

### 🎨 设置页与交互

- **拖动重排全面统一**：菜单组 / 菜单项 / 动作列表 / 组合步骤 / 超级面板槽位（跨卡片=跨页）全部支持拖动——幽灵跟随、占位、松手滑到位动画、插入指示线，共享 `Views/Controls/DragFx.cs`。
- **菜单组改名**「CapsLock快捷菜单」；动作管理新增「新建组合」入口；组合动作按钮对齐主样式。
- **组合键编辑**改专用选键对话框：修饰键复选 + 特殊键表格 + 预览实时校验双向同步，取代按键捕获。
- **自动保存扩展**：动作 / 面板 / 菜单编辑沿用 800ms 防抖落盘，切页冲刷未保存改动。
- **右键点外关闭**：超级面板 / 菜单组 / 帮助面板三处覆盖式弹层统一。
- **组合步骤名非必填**（空名回退类型名），普通动作名称仍必填；步骤行两行响应式布局，编辑 / 删除按钮常驻。

### ⬆️ 平台

- **升级 .NET 8 → .NET 10**（LTS），`net10.0-windows`，WPF 跟进最新。

### 🛡 稳定性与性能

- **钩子回调兜底**：键盘 / 鼠标钩子回调体 try/catch——异常外泄会中断 `CallNextHookEx` 链导致按键被吞或丢失，现记崩溃日志后放行。
- **钩子路径不落盘**：鼠标速度保存改后台线程写配置（原同步 IO 可能超 300ms 被系统摘钩子）。
- **全局异常兜底**：`AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` 记入崩溃日志。
- **热路径优化**：吞键集合 ArrayList → HashSet（O(1) 无装箱）；`MouseTip` 复用单个 DispatcherTimer；超级面板关闭停计时器。

### 🐛 修复

- 发布包只携带示例配置，升级解压不再覆盖用户配置。
- 超级面板：菜单开着点其他格子只关菜单不触发；右键长按不卡键、面板紧贴光标；短按注入改后台线程（修系统鼠标卡死 / 进程不可结束）；格子悬停高亮 / 执行后不关 / 右键菜单无反应 / 悬停光标闪烁；翻页动画期间高度暴涨；删除页无反应（闭包捕获越界）。
- 速记：新建分类后列表 / 编辑区联动（新建即进入新分类）；「全部」视图列表项显示所属分类标签。
- 组合步骤行布局（两行拆分 / 单行态内容偏上 / 宽度响应式）；编辑窗口屏幕居中；托盘菜单状态点。

> **升级说明**：v2.1 及更早用户首次启动会自动把 `CapsLock++.json` 改名为 `LTools.json`，无需手动操作；若旧配置与动作系统 schema 不兼容则回退读示例配置。

## \[v2.1.0\] - 2026-09-28

### 🎨 视觉与交互重构

全局视觉风格重做，去除「AI 工具味」，转向中性灰黑克制配色（Zinc 系），更精致、简洁、清新。

- **配色重构（`Themes/Modern.xaml`）**：背景 `#FAFAFA`、文字 `#18181B`、边框 `#E4E4E7`；主操作按钮改为黑底白字（`#18181B`），不再使用 Tailwind 蓝；危险按钮改为透明底红字红边；选中态用浅灰底（`#F4F4F5`）而非蓝色高亮。
- **控件模板补全**：自定义 TextBox（聚焦变深灰边框）、ComboBox（自定义下拉 + 圆角弹层）、CheckBox（黑底勾选）、ListBox/ListViewItem（圆角选中态）、DataGrid、细窄 ScrollBar、暗色 ToolTip、ContextMenu/MenuItem、带细线的 GridSplitter。
- **统一圆角与字号**：按钮 5px、卡片 8px；正文 13px、辅助 12px，移除隐式 `TextBlock.FontSize` 以恢复容器字号继承。
- **自定义标题栏（`Views/Controls/TitleBar`）**：所有窗口改用 `WindowStyle="None"` + `WindowChrome`，统一自绘标题栏（拖动 / 最小化 / 最大化 / 关闭），关闭按钮悬停变红。标题文字绑定窗口 `Title`。
- **最大化修正（`WindowChromeHelper`）**：挂钩 `WM_GETMINMAXINFO` 钳制到当前显示器工作区，避免最大化时内容溢出；对话框禁止双击标题栏最大化。
- **应用图标重设计**：旧青绿渐变 + "++" 图标替换为暖橙渐变圆角底 + 奶白上升箭头（方案「Ascend」），含 7 尺寸（16/24/32/48/64/128/256），16px 托盘尺寸下对比清晰；csproj 新增 `<ApplicationIcon>`，exe 嵌入图标。

### ✨ 新增

- **速记列表右键删除**：右键列表项（或选中后 **Ctrl+Del**）弹出「删除」菜单，删除的是列表条目本身，只有恰好是当前编辑中的那条才会清空编辑区；沿用红色「确认删除」对话框。焦点在标题/正文时 Ctrl+Del 留给编辑（删到文末），不误删。
- **设置实时自动保存**：菜单组/菜单项的增删改后防抖 800ms 自动写回 JSON，移除「保存配置」按钮，底部状态栏显示「保存中… / 已自动保存」；关闭窗口前冲刷未落盘改动。
- **速记未保存保护**：切换笔记或关闭窗口时若有未保存改动，弹「保存 / 不保存 / 取消」三选对话框（`ConfirmDialog.ConfirmDiscard`），防止误丢。

### 🔧 改进

- **设置面板性能**：列表改用 `ObservableCollection` 绑定替代全量 `Clear+Add`，移除 `_suppressGroupSelection` / `_itemDisplayToIndex` 等手动状态维护。
- **速记窗口重构**：去掉行号 gutter 与 GridView，改用自定义列表项模板（标题 + 缩短为 `M/d HH:mm` 的时间）；左栏精简为分类下拉（右键管理分类）+ 搜索占位符 + 新建；空列表显示「暂无速记」引导；右侧标题与正文以细线分隔的无边内联编辑器呈现。
- **设置/速记布局**：双列改用 GridSplitter 细线分隔取代双卡片边框，减少视觉噪音。
- **菜单弹出**：卡片圆角 8、阴影更柔、标题字号降、菜单项改用幽灵按钮、按钮高度 42→36。
- **帮助面板**：Accent 蓝色标题栏改为中性灰，统一自定义标题栏风格。
- **对话框统一**：InputDialog / ConfirmDialog / MenuItemEditDialog / TerminalPathsDialog 均改用自定义标题栏与统一样式；TerminalPathsDialog 去掉冗余「关闭」按钮。

---

## \[v2.0.5\] - 2026-09-15

### 🐛 修复

- **切换菜单组后点击外部无法关闭**：`MenuPopupWindow` 依赖 `Deactivated` 事件关闭，但切换组时因旧窗口淡出与前台权限竞争，新菜单间歇性未能激活，导致点击外部不触发关闭。
  - `MenuSystem.Show` 改为 `Dispatcher.BeginInvoke` 延迟创建窗口并显式 `Activate`（与 HelpPanel 同一竞态修复思路）；
  - `MouseHook` 左键按下加入坐标判定：菜单打开时点击窗口矩形外确定性调用 `CloseCurrent()`，彻底规避激活竞态。
- **底部滚轮调音量触发抖音网页快捷键**：`InputHelper.Tap` 发送 `VK_VOLUME_UP/DOWN` 时未带 `KEYEVENTF_EXTENDEDKEY`，扫描码 0x30/0x2E/0x20 被浏览器误判为字符键 `KeyB/KeyC/KeyD`，触发抖音收藏/评论等网页快捷键。
  - 将 0xAD/0xAE/0xAF 加入 `IsExtendedKey`，确保 SendInput 发送 E0 扩展前缀，浏览器正确识别为 `VolumeUp/VolumeDown` 媒体键，网页快捷键不再匹配。

## \[v2.0.2\] - 2026-09-13

### 🔧 重构

- **配置文件从 INI 迁移到 JSON**（`CapsLock++.ini` → `CapsLock++.json`）：菜单组/终端路径/鼠标速度统一为单个 JSON 对象，由 `System.Text.Json` 读写。配置仅由应用自身修改（设置面板/快捷键），不面向手改
- **统一配置路径定位**（`Core/ConfigLocator`）：消除原先 App 上溯 3 级 / MouseMode 上溯 5 级的路径规则不一致；首启自动把旧 `CapsLock++.ini` 迁移为 JSON（旧文件改名 `.migrated` 备份）
- **移除 WinForms 依赖**：文件夹选择改用 .NET 8 WPF 原生 `Microsoft.Win32.OpenFolderDialog`（内部走现代 `IFileOpenDialog`，与 WinForms `FolderBrowserDialog` 同款资源管理器 UI + 自定义标题），csproj 去掉 `<UseWindowsForms>`，`Native/FolderPicker.cs` 不再依赖 WinForms
- 保留 `Config/IniFile.cs` 仅供一次性迁移使用，运行时不再引用

> 升级说明：已有 `CapsLock++.ini` 的用户，首次启动会自动迁移为 `CapsLock++.json`，原文件改名 `CapsLock++.ini.migrated` 保留备份，无需手动操作。

---

## \[v2.0.1\] - 2026-09-13

### ✨ 新增

- **启动提示**: 应用启动时在鼠标旁边显示「CapsLockPro 已启动」（复用 MouseTip，约 1.8s 自动隐藏）
- **开机自启开关**: 设置窗口新增「开机自启」开关，基于任务计划程序（`schtasks /create /sc ONLOGON /rl HIGHEST`）实现——本应用为提权程序，任务计划可静默以最高权限自启，避免开机弹 UAC。状态以任务计划实际存在与否为准，开关失败回滚并提示

### 🎨 改进

- **「配置助手」更名为「设置」**: 托盘菜单、窗口标题、帮助面板、README 及代码注释统一更名
- **设置窗口菜单组显示序号**: 菜单组列表加 `1.`~`9.`/`0.` 前缀（第10组为 `0.`），与 `CapsLock+1~0` 唤起对应菜单组的热键一致（菜单项列表此前已显示组内 `1. 名称` 序号）

---

## \[v2.0.0\] - 2026-09-13

### 🔥 重构

- **C# / .NET 8 / WPF 全面重构**: 由 AutoHotkey v2 脚本重构为 C# / .NET 8 / WPF 桌面程序，功能行为与 AHK 版本一致，性能与可维护性显著提升
  - 全局键盘/鼠标钩子、托盘、速记、配置助手、快捷菜单、帮助面板、鼠标模式等全部重写
  - AHK 原版代码归档至 `ahk-legacy` 分支保留
- **仓库结构扄平化**: 取消 `csharp/` 子目录，所有源码/`.csproj`/`.sln` 提升到仓库根，新增 `CapsLockPro.sln` 解决方案文件
- **清理 AHK 拥留**: 删除 `CapsLock++.{ahk,exe}`、`lib/`（19 个 `.ahk`）、`窗口信息收集工具.{ahk,exe}`、`Icon/QuickNote.ico` 等 AHK 专属文件

### ⚡ 性能

- **速记列表搜索优化**: `NoteRepository.List` 拆分 `BuildEntryLight`（不读正文）+ `TryReadBody`（懒读）；无过滤时零正文 IO，过滤时标题先匹配短路仅不命中才读正文；搜索框加 300ms 防抖
- **拖动分隔条不再卡顿**: 行号更新加 50ms 防抖，拖动期间不重建行号，停顿后更新一次（大正文时不再每像素 O(N) 重建）

### 🎨 UI

- **速记窗按钮就近归属**: 取消全局顶栏，左列顶栏放分类/搜索/新建速记（操作列表），右列顶栏放保存/删除/关闭（操作详情），中间分隔线贯通
- **速记列表单击打开**: 由双击载入改为 `PreviewMouseLeftButtonUp` 单击载入
- **速记搜索框裁切修复**: 去掉固定 `Height=28`（被模板 Padding 裁切），改 `MinHeight=32` + 垂直居中，CJK 文字完整可见
- **速记标题/正文输入框对齐**: 「标题」「正文」标签与两输入框左侧统一偏移 42px 与行号栏宽度对齐
- **配置助手工作目录选择按钮**: 新增「选择...」按钮，弹出现代文件夹选择器（左侧含快速访问导航栏），路径正确回填
- **修改时间列完整显示**: 列宽调整，`yyyy-MM-dd HH:mm` 不再裁切

### 🐛 修复与健壮性

- **弹窗统一为 `ConfirmDialog`**: 4 处 `MessageBox.Show` 统一为 `ConfirmDialog`（信息单按钮 / 危险操作红按钮）
- **`Debug.WriteLine` 清理**: 异常路径转 `CrashLog.Write`，信息性日志删除，冗余清理
- **删除死代码**: `NoteRepository.Root`、`CategoryExists`、`TrayService._tray/Init` 等无入口 public API；`Win32.DwmSetWindowAttribute` P-Invoke + 19 个未引用常量

### 🔧 工程与依赖

- **剪贴板备份/恢复改用 WPF Clipboard**: `System.Windows.Forms.Clipboard` → `System.Windows.Clipboard`（COM IDataObject 多格式备份，行为不变），后因 FolderBrowserDialog 需要重新启用 `<UseWindowsForms>` 并恢复 `GlobalUsings.cs` 消歧别名
- **GitHub Actions CI**: 新增 `.github/workflows/build.yml`（push master 跑构建验证）+ `release.yml`（打 `v*` tag 自动构建独立部署 zip 上传到 Release）
- **绿色发布**: v2.0.0 Release 提供 `CapsLockPro-v2.0.0-win-x64.zip`，独立部署内嵌 .NET 运行时，用户免装 .NET 解压即用
- **README 重写为 C#/.NET 8 WPF 版**: 构建方式、功能说明、配置助手、ahk-legacy 分支指引

---

## \[v1.7.1\] - 2026-05-23

### ✨ 新增

- **CapsLock+T 翻译助手**: 以 Edge 新窗口打开百度 AI 聊天翻译页面
  - 独立窗口，登录态完全隔离（`--user-data-dir` + `--disable-sync`）
  - 窗口 1400×920，自动居中
  - 每次全新会话，不残留任何浏览记录
  - 前关闭已打开的翻译窗口（防止重复）

---

## \[v1.7.0\] - 2026-05-23

### 🔥 移除

- **窗口切换功能移除**: 移除 CapsLock + 滚轮窗口切换功能
  - 删除 `lib/WindowSwitch.ahk` 整个模块（~319 行）
  - 移除 XButton1/XButton2 和 Alt+Escape 窗口切换热键
  - 清理相关全局变量和死代码（`StrHash`、`ShowKeyPressDebug` 等）

### ✨ 优化

- **启动提示精简**: 移除已不存在的窗口切换功能提示
- **帮助面板更新**: 同步移除窗口切换相关热键说明

---

## \[v1.6.0\] - 2026-05-23

### ✨ 新增

- **屏幕底部滚轮调音量**: 鼠标在屏幕底部（5px 范围）时，滚轮调节系统音量（每次 ±2）
  - 无需按 CapsLock，裸滚轮触发
  - 跟随 CapsLock+Esc 全局启用/禁用

### 🐛 修复

- **托盘菜单多余分隔线**: 移除"退出"上方的空白分隔线
- **音量步长过大**: 滚轮调音量从 ±4 调整为 ±2

---

## \[v1.5.0\] - 2026-05-23

### ✨ 新增

- **帮助面板**: 新增 `CapsLock + `` ` `` 热键，打开热键速查表
  - 展示所有热键及功能，按 9 个分类组织（基本功能、光标移动、文本选择、删除、编辑、窗口管理、鼠标模式、快捷菜单、实用工具）
  - 再次按热键 / Esc / 点击外部自动关闭
  - 与快捷菜单一致的 UI 风格（圆角、阴影、淡入淡出动画）

### 🐛 修复

- **CapsLock+Q 快速搜索**: 修复热键缺失问题（文档已描述但代码未实现）
  - 选中 URL → 浏览器直接打开
  - 选中绝对路径 → 资源管理器打开
  - 选中普通文本 → Bing 搜索（UTF-8 URL 编码）

---

## \[v1.4.3\] - 2026-05-23

### 🐛 修复

- **菜单关闭闪烁修复**: 修复按 Escape / 点击"关闭" / 点击菜单外部时窗口闪烁的问题
  - `FadeOutWindow` 淡出循环结束后增加 `WinHide`，防止 DWM 在销毁分层窗口时短暂闪烁
  - `ClearCapsLockAhkWindows` 增加 `WinExist` 检查 + 快速淡出（60ms），切换菜单组时不再突兀消失

---

## \[v1.4.2\] - 2026-05-23

### ✨ 新增

- **终端选择与保持窗口**: 添加/编辑菜单项时可选择终端类型（PowerShell 7/5、CMD、Git Bash、WSL Bash）
  - "保持窗口"复选框自动拼接 `wt` + `-NoExit`/`/k`
  - 终端选择和保持窗口偏好自动记住，下次新建时默认回填
  - "命令"输入框只填原始命令（如 `ipconfig /all`），完整命令实时预览
  - 旧命令格式（无引号）自动识别终端类型
- **ListView 显示优化**: "命令"列只显示用户原始命令，不显示拼接后的完整命令

### ✨ 简化

- **命令系统精简**: 移除 `SendInput()`/`ActivateOrRun()` 动作格式支持，菜单项动作统一为纯命令字符串
  - 删除 `ActivateOrRun()` 函数（~110 行）和 `ResolvePathVariable()` 函数
  - `ExecuteCustomAction` 简化为 3 行直接执行
  - 旧 `RunCommand()` 格式自动迁移为纯命令
- **菜单项结构精简**: 移除图标和图标类型字段，每项仅保留名称+命令两列
  - 配置助手 ListView 4 列→2 列（名称、命令）
- **配置助手交互优化**: 移除"编辑"和"编辑命令"按钮，双击菜单项直接打开完整编辑对话框
  - 默认焦点在命令输入框
- **移除暗色模式**: 只保留亮色模式，删除 `ApplyMenuTheme` 函数和相关 UI 配置

### 🐛 修复

- **INI 写入修复**: 修复新条目与 section 头粘合的问题（`WriteIniValueUTF8` 缺少前置换行）
- **新条目顺序修复**: 新条目改为追加到末尾而非前插
- **残留条目清理**: 保存时自动清除已删除菜单项对应的旧 INI 条目
- **菜单阴影修复**: `GetClassLongPtr`→`GetClassLong` 修复 DLL 调用错误

---

## \[v1.4.1\] - 2026-05-23

### ✨ 菜单 UI 优化

- **菜单项改为 Win11 原生 Button 控件**: 每个菜单项使用圆角按钮，自带悬停高亮和点击反馈
- **无标题栏窗口** (`-Caption`): 移除原生标题栏和关闭按钮，界面更简洁
- **序号 + 名称布局优化**: 每个菜单项左侧显示数字序号，右侧为操作按钮，对齐更整齐
- **分组标题**: 菜单顶部居中显示组名称
- **底部关闭按钮**: 添加显式的"关闭"按钮，配合 Esc 和外部点击关闭

### 🐛 修复

- **修复右边距缺失**: 按钮宽度计算错误导致超出窗口右边界
- **移除不兼容的事件绑定**: 移除无法在 AHK v2 中使用的 `MouseEnter`/`MouseLeave` 和 `Gui.OnEvent("MouseMove")`

## \[v1.4.0\] - 2026-05-22

### 🔧 代码质量改进

- **拆分光标定位模块**: 将 `Utils.ahk` 中约 340 行的 `GetCaretPosition()` / `GetCaretPosEx()` 函数族提取为独立的 `lib/CaretPos.ahk`
  - `Utils.ahk` 从 556 行缩减至 204 行（-63%），职责更清晰
  - 新增 `lib/CaretPos.ahk` 专管光标位置获取（GUI Thread Info / MSAA / UIA / Hook 四种策略）

- **符号跳转模块重构 (`SymbolJump.ahk`)**:
  - 消除向前/向后搜索之间约 150 行重复代码，统一为 `_SearchInDirection(dir)` 函数
  - `Sleep(10)` 手动剪贴板轮询全部替换为 `ClipWait()`，减少 CPU 占用
  - 提取 `_ScanLine`、`_ReadLineContent`、`_CheckBoundary`、`_ClipWait` 等 6 个独立函数
  - 文件从 423 行缩减至 355 行（-17%）

- **清除死代码**: 删除 `Globals.ahk` 中 3 行无引用的全局变量（`settingsConfigPath`、`configMenusPath`、`settings`）

- **集中全局变量声明**: 将 `newNoteBtn`、`viewToggleBtn`、`configHelperGui` 3 个缺失的变量声明统一至 `Globals.ahk`

## \[v1.3.0\] - 2026-05-23

### ✨ 移除功能

- **动作系统移除**: 移除了 `ActionEngine.ahk` 和 `ActionEditor.ahk` 整个动作管理子系统
  - 菜单项动作简化为纯字符串格式，不再支持结构化动作对象
  - 配置助手移除"动作管理"Tab 及相关构建/编辑/转换函数
  - 菜单项动作可直接输入原始命令，无需 `RunCommand()` 前缀
  - 动作字符串直接作为命令执行
- **网站管理模块移除**: 移除了 `WebsiteLogin()` 及相关网站配置功能
  - 删除 `lib/WebsiteManage.ahk` 整个模块
  - 清理 `CapsLock++.ini` 中的 `[CommonWebsites]` 配置节
  - 清理菜单组3（原"网站"组）的菜单项

### 🐛 修复

- **修复缺失的源码文件**: 创建了 5 个之前缺失的存根模块，使源代码可正常运行：
  - `lib/core/Logger.ahk` — 日志系统
  - `lib/core/ConfigManager.ahk` — 配置管理器（从 INI 读写）
  - `lib/core/ActionEngine.ahk` — 动作执行引擎（后续已移除）
  - `lib/ui/MenuUI.ahk` — 菜单 UI
  - `lib/ui/ActionEditor.ahk` — 动作编辑器（后续已移除）
- **修复括号不匹配**: 删除代码后 `ConfigHelper.ahk` 中 `GetActionSummary` 和 `ConvertToOldActionString` 函数遗留的括号问题
- **修复缺失变量**: 添加 `iniFile` 全局变量定义
- **修复参数错误**: `MenuUI.ahk` 中 `MakeMenuItemHandler` 传递给 `ExecuteMenuItem` 的参数数量错误

## \[v1.2.0\] - 2025-10-28

### ✨ 移除功能

- **微信悄悄话功能移除**: 由于微信大版本更新, 悄悄话功能已不再适用, 且窗口裁切已覆盖该功能, 因此移除

### ✨ 调整功能

- **标签页切换调整**: 右键+滚轮操作中右键可能与某些自带右键菜单的软件冲突, 而强行安装钩子会导致恶性bug, 因此调整为滚轮按下+滚动

## \[v1.1.1\] - 2025-08-13

### ✨ 新增功能

- **双引号输入优化**: 新增 `CapsLock+` 发送双引号 `"` 功能，避免频繁在 CapsLock 与 Shift 间切换

## \[v1.1\] - 2024-12-19

### ✨ 新增功能

- **括号输入优化**: 新增 `CapsLock+[` 发送 `{` 功能，避免频繁在 CapsLock 与 Shift 间切换
- **括号输入优化**: 新增 `CapsLock+]` 发送 `}` 功能，避免频繁在 CapsLock 与 Shift 间切换
- **智能菜单系统**: 优化 `CapsLock+9/0` 逻辑
  - 当第9组菜单为空时，`CapsLock+9` 发送左括号 `(`
  - 当第10组菜单为空时，`CapsLock+0` 发送右括号 `)`
  - 当菜单非空时，正常显示菜单功能

### 🔧 技术改进

- 新增 `IsMenuGroupEmpty()` 函数用于检查菜单组是否为空
- 改进了菜单系统的逻辑判断机制

### 🎯 设计目标

这些更新的主要目的是减少用户在编程和文本编辑时频繁在 CapsLock 和 Shift 键之间切换的需求，提升输入效率。

## \[v1.01\] - 2024-12-19

### 🐛 修复

- **窗口操作错误处理**: 添加了完善的窗口操作错误处理机制
- 修复了在没有激活窗口时可能出现的 "target window not found" 错误
- 为以下函数添加了 try-catch 错误处理：
  - `IsActiveWindowClipped()`
  - `StartDragClippedWindow()`
  - `ShowClipInfo()`
  - `MoveClipRegion()`
  - `ResizeClipRegion()`

### 🔧 技术改进

- 所有窗口相关操作现在会优雅处理错误情况
- 错误处理采用静默模式，不影响用户体验

## \[v1.0\] - 初始版本

### 🚀 主要功能

- **CapsLock 重映射**: 单击发送 Esc，长按激活功能模式
- **vim 风格导航**: hjkl 方向键、单词跳转、行首行尾导航
- **应用启动菜单**: 10组可自定义的应用快速启动菜单
- **窗口管理**: 虚拟桌面、窗口移动、大小调整
- **文本处理**: 选择、复制、粘贴、搜索、翻译
- **鼠标控制**: 精确移动、点击、滚轮操作
- **系统功能**: 音量控制、亮度调整、电源管理
- **特殊功能**: 放大镜、文件重命名、窗口裁剪等

------------------------------------------------------------------------

## 版本说明

- **主版本号**: 重大功能更新或架构变更
- **次版本号**: 新功能添加
- **修订版本号**: 错误修复和小的改进

更多详细信息请参阅 [README.md](README.md)