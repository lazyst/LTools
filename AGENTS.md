# AGENTS.md

LTools（原 CapsLock-Pro / CapsLock++）：Windows 桌面工具，把 CapsLock 重映射为 vim 式修饰键。C# / .NET 10 / WPF 单项目单解决方案，**无测试项目**。

## 构建

```bash
# 开发构建（Debug，免提权快速启动）
dotnet build -p:NoWin32Manifest=true

# Release 构建（带 requireAdministrator 清单，全局钩子需管理员）
dotnet build -c Release

# 发布独立部署（内嵌运行时，用户免装 .NET）
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

- 输出：`bin/Debug/net10.0-windows10.0.19041.0/LTools.exe`
- TFM 带 Windows SDK 投影（`net10.0-windows10.0.19041.0`），OCR 用 `Windows.Media.Ocr`；改动 TFM 会改变输出目录
- 全局钩子需管理员权限；Debug 加 `-p:NoWin32Manifest=true` 免提权调试
- 构建必须 **0 错 0 警**
- 无测试项目，不要找 `dotnet test`

## CI

- `build.yml`：push/PR 到 `master` → `dotnet build -c Release`
- `release.yml`：打 `v*` tag → 自包含发布 zip 上传到 GitHub Release

## 架构

单 STA 线程模型：WPF Dispatcher 泵送 Win32 消息，低级钩子回调在主 UI 线程派发（模型不变，勿改线程模型）。

- `App.xaml.cs` — 入口：单实例互斥体 → 托盘 → 装钩子 → 加载配置 → 看门狗
- `Hooks/` — `WH_KEYBOARD_LL` / `WH_MOUSE_LL`，纯 P/Invoke，框架无关
- `Native/` — Win32 调用、InputHelper、NativeClipboard、FolderPicker
- `Core/` — AppState(全局状态)、CapsLockStateMachine、ConfigLocator、TrayService、CrashLog、AutoStartService（任务计划自启 + 路径自愈）
- `Features/` — 功能模块（TextEditor、MouseMode、MenuSystem、QuickNote 等），非 UI 逻辑
  - 键鼠宏：`MacroRecorder`（录制）/ `MacroPlayer`（回放）/ `MacroStore`（`macros/*.json` 事件 + `Macros.Items` 元数据）/ `MacroSettings`（热键与全局参数）/ `MacroHotkey`（录制 / 回放期间的系统热键）/ `HotkeyCapture`（录制式热键录入）
- `Views/` — WPF 窗体（XAML + code-behind）；`Views/Controls/` 为共用控件（`TitleBar`、`ActionKindSwitch` 等）
- `Themes/Modern.xaml` — ResourceDictionary 配色与控件样式
- `Config/` — AppConfig(JSON 模型)、IniFile(仅旧 ini 迁移用)

## 钩子关键约束

修改 `Hooks/` 或在钩子回调路径上调用代码时必须遵守：

- 回调**不得阻塞 >300ms**（否则系统自动卸载钩子）
- 阻塞操作（SendInput / Sleep / 剪贴板）走后台线程
- 忽略 `LLKHF_INJECTED` 事件（防自注入递归）
- 吞掉某键的 keydown 必须同时吞掉配对 keyup（保持事件平衡，见 `AppState.SwallowedVks`）

## 配置

- `LTools.json`（gitignored）— 用户配置，由应用自身读写（设置面板/快捷键），不面向手改；首次保存才生成，升级解压不会覆盖
- `LTools.example.json`（已提交）— 默认模板，随包分发；`AppConfig.Load` 在用户配置缺失时回退读同目录示例（故发布包只带示例、不带实际配置，避免覆盖用户配置）
- `ConfigLocator.FindPath()`：dev（仓库根含 `LTools.csproj`）落仓库根，prod 落 exe 同级。首启自动迁移旧配置：`CapsLock++.json` 同目录改名 `LTools.json`；更旧的 `CapsLock++.ini` 读入后改名 `.migrated` 备份
- 配置格式为 JSON（`Actions`、`SuperPanel`、`MenuGroups[10]`、`Macros`、`TerminalPaths`、`MouseModeSpeed`，见 `Config/AppConfig.cs`），**非旧的 INI 分节格式**；README §10 已同步为 JSON 字段说明，手改时以代码为准
- 宏事件存 `<数据根>/macros/<id>.json`（已 gitignore），元数据在 `LTools.json` 的 `Macros.Items`；**一个宏 = 一个 `macro` 动作**，删动作即删宏文件，启动回收孤儿宏

## WPF 约定

- `GlobalUsings.cs` 已把 `Button`/`Key`/`MessageBox` 等同名类型统一到 WPF 命名空间——不要引用 WinForms 类型
- 文件夹选择用 WPF 原生 `Microsoft.Win32.OpenFolderDialog`，不引用 WinForms
- **`DataGrid` 行高是全局写死的 30**（`Themes/Modern.xaml` 的 `DataGrid` 样式）：`DataGridTemplateColumn` 里别再堆两行（名称 + 副标题），第二行会被裁掉。动作管理的「动作」列就是单行——名称（正常字色）+ 摘要（12px 浅色）同行，长文本靠 `TextTrimming` + `ToolTip`；真需要两行时在该表格实例上设 `RowHeight="Auto"` 并给内容 `MinHeight`，**别改全局样式**（终端路径那个表格靠 30 行高）
- 第三方依赖：`H.NotifyIcon.Wpf`（托盘图标）、`OpenCvSharp4`（图像匹配）、`FlaUI.UIA3`（UI Automation，读选中文本）、`RapidOcrNet`（PP-OCRv6 识别，ONNX Runtime + SkiaSharp）
- OpenCvSharp 的 `opencv_videoio_ffmpeg`（27.3 MB）只服务 `VideoCapture`/`VideoWriter`，本项目只用 imgproc（`ImRead`/`Resize`/`CvtColor`/`MatchTemplate`），csproj 有 target 在构建 / 发布后删除；要加视频功能时得同时去掉那个 target
- 开机自启用任务计划（`Core/AutoStartService.cs`）而非注册表 Run 键：`requireAdministrator` 的程序从 Run 键启动会弹 UAC，任务计划能静默提权。任务里存的是注册时的 exe 绝对路径，便携目录被挪走后会静默失效，故 `Query()` 比对路径、启动时 `HealIfStale()` 把任务对齐到当前 exe（一个任务名只服务一份程序，不做多拷贝共存）；路径用任务计划 COM 读，别用 `schtasks /query /xml`（控制台代码页会毁掉中文路径）
- **找图模板分成两个目录**：`resources\images\` 只放程序生成的框选截图（`shot-时间戳.png`），由 `ImageLibrary.CollectGarbage` 按引用回收；`resources\user\` 放用户自己的素材（文件选择器默认打开这里，选外部图片会复制进来），**整个目录永不自动删除**。回收边界靠目录而不是文件名前缀；配置里 images 下存文件名、user 下存数据根相对路径（`resources\user\a.png`），备份换机不断链。新增同类「程序生成 + 用户素材」的资源时照这个二分法来
- **框选截图（`Views/ScreenPickerWindow` + `Views/ScreenSnapTargets`）**：遮罩 40% 黑；确认栏贴选区下方、与右边缘对齐，放不下翻到上方；悬停探测窗口 / 控件用「窗口 z 序 + `ChildWindowFromPointEx` + UIA `FromHandle` 下钻」，**别用 UIA `FromPoint`**——截图窗口盖住全屏，只会命中自己。UIA 查询必须放后台线程并按位置缓存限流，不能放进鼠标移动路径（慢应用能卡几十毫秒）
- **引用型资源要能自回收**：宏文件（`MacroStore.CollectGarbage`）与模板截图（`ImageLibrary.CollectGarbage`）都在「启动」与「删除动作」时按引用清理。**引用从新动作体（`ActionDto.Program`）里扫**：`Core/Actions/ProgramWalker` 枚举所有步骤（含动作内子动作、嵌套块与参数里的内联取值步骤），收集 `image` / `macroId` 这类参数的字面量；旧字段（`ActionDto.Image` / `Steps` / `Flow`）只兜底还没迁移的动作。新增「会引用文件」的单元参数时，在 `ImageLibrary.CollectAction` / `MacroStore.CollectRefs` 里按参数名接上（漏了就会被当成孤儿删掉）；新增同类资源（图片 / 脚本 / 导出物）时照这个做法接上，否则删动作后会永久留在数据根里。备份导入的 `.bak-*`、配置损坏的 `.bad-*` 也各有「只留最近 3 份」的上限
- **流程里的提示一律用 `Core/ToastService`**（`ActionType.toast`）：右下角堆叠、默认 4 秒、点一下关，`Show()` 从任意线程调用都只投递到 UI 线程就返回，**不阻塞后续步骤**；窗口用 `WS_EX_NOACTIVATE`，自动化正在往别的窗口输入时不会被抢焦点。别在流程里弹 `MessageBox` / `ConfirmDialog`（会阻塞整个流程并抢前台）
- **流程图跑在后台线程，WPF 对象只能在 UI 线程上碰**：`Application.Windows`、窗口构造、`ShowDialog`、`ScreenPicker` 全部要放进 `Dispatcher.Invoke` 里再取；哪怕只是「先找 owner 再 Invoke 弹窗」也会在后台线程上抛「另一个线程拥有该对象」（`AskUser` 曾因此弹不出输入框）
- **窗口操作（`Features/WindowAction.cs` + `Features/WindowFinder.cs`）**：窗口匹配与条件里的「窗口」取值共用 `WindowFinder`（标题包含 + 类名精确，命中最上面那个），别各写一套。操作后**读回真实状态**再判断（`IsZoomed` / `IsIconic` / `GetForegroundWindow`），没达到就抛错交给步骤失败策略，**不假装成功**；切前台被前台锁拒绝时用 `AttachThreadInput` 兜底并轮询确认；`resize` 被应用最小尺寸夹住时只记 `AppLog.Warn`、照常继续，输出实际矩形
- **文字点击 / 找文字（`Features/TextClickAction.cs` + `Features/Flow/TextLocator.cs` + `Features/Flow/OcrLayout.cs`）**：动作类型 `clickText` 与取值器 `textSearch` 共用 `TextLocator`（区域语义与「截图找图」共用 `ScreenRegion`；带坐标的 OCR 走 `OcrLayout`，系统 OCR 用 `OcrWord.BoundingRect`，PP-OCRv6 **必须** `ReturnWordBox = true`，否则只有整行框、只能点行中心）。匹配按包含、忽略大小写与词间空白，多命中取最上面（同高取最左），点命中文字的外接矩形中心。鼠标注入统一走 `Features/MouseOps.cs`（「鼠标操作」与文字 / 图片点击共用，别再各写一套 `mouse_event`）。**点击**找不到目标就是这一步失败（抛错并带上区域内识别到的文字 / 最高相似度），不假装点过；**取值器**找不到只是 `found = false`，两种语义别混。等待出字用 `TimeoutMs` + `ctx.Wait` 轮询，才能响应停止热键。新建动作默认 PP-OCRv6 快速（`OcrEnginePreference.DefaultForNewProbe`）
- **图片点击（`Features/ImageClickAction.cs`）**：动作类型 `imageClick`，定位直接复用「截图找图」的 `Features/Flow/ImageProbe.cs`（OpenCV 模板匹配 + 多缩放候选 + 可等待），模板图与阈值的语义完全一致。**别为它另写一套匹配**；`ImageProbe.Run` 的 `ctx` 允许为 null（从面板直接触发时没有流程上下文），等待走 `Thread.Sleep`。模板图编辑器统一用 `Views/Controls/TemplateImagePicker`（图片点击动作与「找图」取值单元共用），路径语义仍由 `ImageLibrary` 决定；它通过 `Captured` 事件把框选区域交给调用方收紧搜索范围
- **超级面板用完即收（`Views/SuperPanel.xaml.cs`）**：面板只当启动器——执行动作、打开设置、新建 / 编辑 / 复制 / 删除动作、从动作池选择，操作结束后（**含取消**）一律 `SuperPanel.Close()`；留在面板上的只有钉住、翻页和拖动重排。**打开模态编辑器 / 对话框前直接 `Close()`，不要把隐藏的面板当 Owner**：隐藏的 Owner 在模态对话框出现时会被 DWM 画成整块黑窗闪一下（实测占满面板矩形），而且面板仍算「已打开」→ 鼠标钩子的长按右键手势被拦住，编辑器开着时唤不出面板。无 Owner 的对话框由 `ActionEditor.Show` / `ActionPoolPicker` 按「无 Owner = 置顶」处理（`owner?.Topmost ?? true`），照样不会被其它窗口盖住。新增这类入口时别只 `Rebuild()`
- **设置「面板管理」是主导航 + 下拉子页**（`Views/ConfigHelper.xaml(.cs)` + `Views/Controls/PanelManageView.xaml(.cs)`）：父项 `NavSuperPanel`（`GroupName="nav"`）带 `SuperPanelSubNavHost` / `SuperPanelSubNav` 三个子项（常规 / 场景 / 禁止弹出，`GroupName="subnav"`），点父项进「常规」并展开下拉；`ApplyNavPage` 里父项与子项一起点亮、切换 `PanelManageView.ShowPage("general" | "scenes" | "blocked")`，程序化改 `IsChecked` 必须包在 `_syncingNav` 里，否则会二次导航。`LastNavPage` 存子页 `Tag`（旧值 `superpanel` 仍恢复到常规）。场景页是**左列表 + 右详情**：匹配条件默认折叠成一行摘要（`_rulesExpanded`，点整条才展开表单），**动作管理（分组 + 磁贴）常驻**；切场景重置折叠态，新建上下文 / 子场景直接展开。别把三页重新合成一个长页，也别在别处另写一套场景列表。**分组名必须可见地可编辑**：名字是 TextBox（悬停显出边）+ 铅笔按钮 + 卡片右键「重命名分组」，Enter 保存 / Esc 还原 / 失焦保存；别再退回成只读 TextBlock 或纯文本
- **面板空白拖动 + 右键菜单（`Views/SuperPanel.xaml.cs`）**：照 Quicker——左键在空白处（不在磁贴 / 按钮 / 输入框 / 滚动条 / 分割条上）按下并移动 >4px 就 `DragMove()`；中键点空白关闭面板。右键统一走 `ContextMenuOpening` 兜底：空白 / 分组 → 区菜单或分组菜单（新建动作、新建分组、重命名 / 删除分组、管理场景、钉住、设置、关闭），场景标签 → 编辑 / 新建 / 删除场景，标题栏 / 左栏 → 面板菜单。**有自己菜单的控件（磁贴、场景标签、分组导航条）继续挂 `ContextMenu`**，`FindContextMenuOwner` 保证不抢它们的菜单；新增空白区域时只改窗口兜底，别再给每个容器重复挂菜单。分组改名在面板内就地完成（`BeginRenameGroup`：Enter 保存 / Esc 取消 / 失焦保存）
- **面板上下文取「按下时鼠标下的窗口」并先把它激活（`Hooks/MouseHook.cs` + `Features/SuperPanel.cs` + `Native/Win32.cs`）**：右键**不会自动激活后台窗口**，所以前台可能还停在别的程序上——典型场景是全屏播放器在后面、资源管理器在前面（前台），在播放器露出的区域长按右键，Quicker 会把**播放器**激活并显示它的上下文（此时 `GetForegroundWindow()` 给的是资源管理器）。做法：长按手势在 `WmRbuttondown` 那一刻用 `Win32.TopLevelWindowAtCursor()`（`WindowFromPoint` + `GetAncestor(GA_ROOT)`）记下句柄；`SuperPanel.Show(hwnd)` 先 `WindowAction.ActivateNoWait(hwnd)` 激活目标（面板关掉后焦点回到它、后续动作也作用在它身上），再按它解析场景。要点：①短按不走这条路径（注入的原生右键本来就会激活）；②键盘入口（CapsLock+T）传 `IntPtr.Zero`，仍用前台窗口（光标可能停在无关窗口上）；③桌面 / 任务栏（`Progman` / `WorkerW` / `Shell_TrayWnd`…）只当上下文、不激活（`ForegroundApp.IsShellWindow`）；④激活目标后前台锁可能让面板抢不回前台，`Show` 末尾要再用 `ActivateNoWait(面板句柄)` 兜底一次；⑤`ActivateNoWait` **不等待**（`Thread.Sleep` 会卡住 UI / 钩子），不要换成 `WindowAction.Activate` 那套轮询版
- **分组导航（`Views/SuperPanel.xaml.cs` 的 `FillZone` / `ScrollToGroup` / `UpdateZonePad` / `SyncRail`）**：面板每个区的分组是**从上到下连续平铺**的，左缘那一列小横条**只是滚动锚点**（照 Quicker）：悬停某一条就把那个分组的**标题滚到视图顶部**，不切页、不重建、不写盘；高亮由 `SyncRail` 按「视口顶部所在分组」同步（滚到底算最后一个）。导航条末尾「＋」新建分组。四个坑：①导航条重建后光标可能正好落在新条目上（点「＋」时整列下移），MouseEnter 会在 250ms 内被忽略；②内容装得下时**最后一个分组本来滚不到顶部**（`ScrollableHeight` 不够），所以 `FillZone` 在每个区末尾塞一块 `Border` 留白（`UpdateZonePad`：留白 = 视口高 − 最后一块高），单分组 / 搜索时高度归零，否则会平白多一条滚动条；③刚新建的分组还没量好高度时 `ScrollableHeight` 是 0，滚动目标会被夹成 0，只能**重试一次**（50ms 定时器）；**绝对不要用 `BeginInvoke` 把自己排回队列**——内容一直装得下（不可滚动）时会在 Loaded 优先级无限自我排队，消息泵再也空闲不下来，表现就是「悬停锚点 UI 直接卡死」；④留白高度依赖布局结果，`ScrollToGroup` 在读数前**再算一次**，窗口尺寸变化（`SizeChanged`）也重算。**搜索时导航条隐藏**（筛选后分组位置会变，锚点没有意义）
- **拖动排序统一规格（`Views/Controls/TileDragSort.cs` / `ListDragSort.cs` + `DragFx.cs` / `DragVisuals.cs`）**：4px 起拖阈值、源位置虚线占位、幽灵跟随、实时插入、220ms 让位、Esc 取消；面板磁贴、面板管理磁贴、快捷菜单分组 / 菜单项、sendKeys 序列全部走这套，别再各写一份（动作编辑器的步骤列表是重建的扁平行，另有轻量实现：幽灵 + 插入线，松手才提交）。两条踩过的坑：①`Window.ReleaseMouseCapture()` 会**同步派发挂起的 MouseMove** → 清理状态必须放在释放捕获**之前**，提交 / 结束要有 `_finishing` 重入保护，否则一次松手会提交两次（把后面的磁贴也挪走）；②磁贴幽灵**不要用 `RenderTargetBitmap` 快照**（个别环境会得到空白图），由 `TileGhostBody` 用 `TileContent` 现建同款内容，列表行仍用快照且带 `VisualBrush` 兜底
- 「截图」取值（`Features/Flow/ScreenshotProbe.cs`）：交互框选复用 `ScreenPicker` 的框选 / 吸附；前台窗口 / 整个屏幕 / 固定区域直接 `ScreenCapture.Capture`，其中前台窗口用 `WindowFinder.GetVisibleBounds`（DWM 可见范围，不含阴影边距），固定区域支持 `${变量}` 解析成 `x,y,w,h`。裁下来的 png 走 `ImageLibrary.SaveGenerated`（`BitmapSource` 与 `Bitmap` 两个重载）存进 `resources\images\`，因此和模板截图共用按引用回收，不需要单独清理。**运行期截图的生命周期**：它是「截图」这一步的输出，本次运行内后面还要用（`.file` 给「文字识别」/「打开文件」/ 剪贴板），所以**不在步骤结束时删**，也不在运行结束时删（外部程序可能刚拿到路径）；由 `AutomationRunner.Run` 在**下一次运行开始时**调 `CollectGarbage(DateTime.Now)` 清掉上一次的，界线用开始时间保证本次新截的不被误删。编辑器「▶ 测试」试截的图用 `ImageLibrary.DeleteGenerated` 测完即删
- **截图与识字是两步，别再合回去**：`Features/Flow/OcrProbe.cs` 只读图片（`Probe.OcrImage`：`${截图}` 取它的 `file`，或 `${截图.file}` / 文件路径），**不自己截屏**；要识别某个区域就先用「截图」取图。旧版「截图 OCR」（自带前台窗口 / 整个屏幕 / 固定区域）已不兼容，运行时提示缺图片
- OCR 双引擎：「文字识别」默认 `PP-OCRv6 快速`（内置在 `Assets/Ocr/ppocrv6`）；可切 `Windows.Media.Ocr`（最快、最省内存）。选择记在 `LTools.json` 的 `lastOcrEngine`，见 `Features/OcrEnginePreference.cs`。`Features/Flow/PaddleOcr.cs` 是 PP-OCRv6 推理（small 下载到数据根 `ocr-models/`，见 `OcrModelStore`）。RapidOcrNet 的包内 targets 会把自带的 v5 模型拷进输出目录，csproj 里有 target 在构建 / 发布后清理。**偏好只补新步骤**：新建 OCR 取值时由创建处预填 `OcrEnginePreference.DefaultForNewProbe`（「文字识别」取值单元与参数表单从别的取值方式切到 OCR 两处），改下拉时 `Save`；引擎参数为空表示「系统 OCR」（老配置也这样存），**别**在参数表单加载时把空值当成「没设置」去套偏好，否则会悄悄改掉已保存步骤的引擎
- **写速记 / 读速记（`Features/NoteActions.cs`）**：「写速记」单元 `data.noteWrite` 与「读速记」单元 `data.noteRead` 都复用 `NoteRepository` + `AppPaths.NotesDir`，目录与 CapsLock+N 完全一致；分类为空 = 未分类，`Save` 会自动建目录。**速记是用户资产，不接 `ImageLibrary` 的按引用回收**（只有用户在速记窗口里删才会消失）。读速记取「最近修改的一条」，没匹配只是 `found = false` / `count = 0`，不是错误；结果属性 `.title` / `.time` / `.path` / `.count` 在 `UnitValues.Member` 与单元的输出声明两处同步维护
- **变量重新赋值 / 追加（`flow.set` 的 `append`）**：同名变量本来就允许反复赋值（运行时直接覆盖当前作用域）；`append = true` 时把右值文本接在旧值后面（变量不存在当空串），右值先求值再拼接，所以 `a += ${a}` 是复制自己。追加后一律退化为纯文本；参数表单的「追加」开关、步骤摘要的 `=` / `+=`、`SetUnit` 三处要一起改
- **动作执行链（v2，W5 起是唯一路径）**：所有动作都先变成 `ActionProgram`——`Features/Migration/ActionPrograms.For` 对旧格式懒迁移并缓存在 `ActionDto.Program`。`ActionExecutor` 只是入口：`composite` 交给 `Features/AutomationRunner`（单实例保护、停止热键、运行指示器、运行期截图回收），单步动作直接在后台线程执行；真正的解释由 `Core/Actions/ActionEngine` 完成，单元来自 `StepRegistry.Default`（`Features/Units` + `Core/Actions/FlowUnits` + `sub.run`）。**别再往 `ActionExecutor` 加按类型分支，也别另写解释器**：新功能 = 新增一个 `IStepUnit`。旧编辑器 / 解释器（`ActionEditorDialog`、`CompositeActionDialog`、`FlowNodeDialog`、`ProbeEditor`、`ConditionEditor`、`FlowRunner`、`ProbeEvaluator`、`ProbeTest`）已在 W5a 删除，别照旧文档把它们加回来；`FlowTypes` / `FlowMigration` 与 `Features/Flow/*Probe` 仍是迁移器和新单元的底层实现，保留。

## 其他

- `速记/`（速记文件）和 `logs/` 运行时生成，已 gitignore
- UIA 走 `FlaUI.UIA3`（托管包）：.NET Core 的 MSBuild 不支持 `COMReference`/`ResolveComReference`，不要再加 `UIAutomationClient` 的 COM 引用
- `app.manifest`：`requireAdministrator` + 声明 Win10/11 兼容 + `PerMonitorV2` DPI 感知
- **DPI 感知双轨**：Release 由 `app.manifest` 在进程创建时设 PerMonitorV2；Debug 用 `-p:NoWin32Manifest=true` 剥了清单（免 UAC），改由 `Core/DpiInitializer.cs` 的 `[ModuleInitializer]` 在 Main 前、WPF 加载前 `SetProcessDpiAwarenessContext(-4)` 补设。修「全屏游戏切分辨率后托盘右键菜单错位」。
