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
- 第三方依赖：`H.NotifyIcon.Wpf`（托盘图标）、`OpenCvSharp4`（图像匹配）、`FlaUI.UIA3`（UI Automation，读选中文本）、`RapidOcrNet`（PP-OCRv6 识别，ONNX Runtime + SkiaSharp）
- OpenCvSharp 的 `opencv_videoio_ffmpeg`（27.3 MB）只服务 `VideoCapture`/`VideoWriter`，本项目只用 imgproc（`ImRead`/`Resize`/`CvtColor`/`MatchTemplate`），csproj 有 target 在构建 / 发布后删除；要加视频功能时得同时去掉那个 target
- 开机自启用任务计划（`Core/AutoStartService.cs`）而非注册表 Run 键：`requireAdministrator` 的程序从 Run 键启动会弹 UAC，任务计划能静默提权。任务里存的是注册时的 exe 绝对路径，便携目录被挪走后会静默失效，故 `Query()` 比对路径、启动时 `HealIfStale()` 把任务对齐到当前 exe（一个任务名只服务一份程序，不做多拷贝共存）；路径用任务计划 COM 读，别用 `schtasks /query /xml`（控制台代码页会毁掉中文路径）
- **找图模板分成两个目录**：`resources\images\` 只放程序生成的框选截图（`shot-时间戳.png`），由 `ImageLibrary.CollectGarbage` 按引用回收；`resources\user\` 放用户自己的素材（文件选择器默认打开这里，选外部图片会复制进来），**整个目录永不自动删除**。回收边界靠目录而不是文件名前缀；配置里 images 下存文件名、user 下存数据根相对路径（`resources\user\a.png`），备份换机不断链。新增同类「程序生成 + 用户素材」的资源时照这个二分法来
- **框选截图（`Views/ScreenPickerWindow` + `Views/ScreenSnapTargets`）**：遮罩 40% 黑；确认栏贴选区下方、与右边缘对齐，放不下翻到上方；悬停探测窗口 / 控件用「窗口 z 序 + `ChildWindowFromPointEx` + UIA `FromHandle` 下钻」，**别用 UIA `FromPoint`**——截图窗口盖住全屏，只会命中自己。UIA 查询必须放后台线程并按位置缓存限流，不能放进鼠标移动路径（慢应用能卡几十毫秒）
- **引用型资源要能自回收**：宏文件（`MacroStore.CollectGarbage`）与模板截图（`ImageLibrary.CollectGarbage`）都在「启动」与「删除动作」时按引用清理；新增同类资源（图片 / 脚本 / 导出物）时照这个做法接上，否则删动作后会永久留在数据根里。备份导入的 `.bak-*`、配置损坏的 `.bad-*` 也各有「只留最近 3 份」的上限
- **流程里的提示一律用 `Core/ToastService`**（`ActionType.toast`）：右下角堆叠、默认 4 秒、点一下关，`Show()` 从任意线程调用都只投递到 UI 线程就返回，**不阻塞后续步骤**；窗口用 `WS_EX_NOACTIVATE`，自动化正在往别的窗口输入时不会被抢焦点。别在流程里弹 `MessageBox` / `ConfirmDialog`（会阻塞整个流程并抢前台）
- **流程图跑在后台线程，WPF 对象只能在 UI 线程上碰**：`Application.Windows`、窗口构造、`ShowDialog`、`ScreenPicker` 全部要放进 `Dispatcher.Invoke` 里再取；哪怕只是「先找 owner 再 Invoke 弹窗」也会在后台线程上抛「另一个线程拥有该对象」（`AskUser` 曾因此弹不出输入框）
- **窗口操作（`Features/WindowAction.cs` + `Features/WindowFinder.cs`）**：窗口匹配与条件里的「窗口」取值共用 `WindowFinder`（标题包含 + 类名精确，命中最上面那个），别各写一套。操作后**读回真实状态**再判断（`IsZoomed` / `IsIconic` / `GetForegroundWindow`），没达到就抛错交给步骤失败策略，**不假装成功**；切前台被前台锁拒绝时用 `AttachThreadInput` 兜底并轮询确认；`resize` 被应用最小尺寸夹住时只记 `AppLog.Warn`、照常继续，输出实际矩形
- **文字点击（`Features/TextClickAction.cs` + `Features/Flow/OcrLayout.cs`）**：动作类型 `clickText`，区域语义与「截图找图」共用 `ScreenRegion`；带坐标的 OCR 走 `OcrLayout`（系统 OCR 用 `OcrWord.BoundingRect`，PP-OCRv6 **必须** `ReturnWordBox = true`，否则只有整行框、只能点行中心）。匹配按包含、忽略大小写与词间空白，多命中取最上面（同高取最左），点命中文字的外接矩形中心。鼠标注入统一走 `Features/MouseOps.cs`（「鼠标操作」与文字点击共用，别再各写一套 `mouse_event`）。找不到文字就是这一步失败（抛错并带上区域内识别到的文字），不假装点过；等待出字用 `TimeoutMs` + `ctx.Wait` 轮询，才能响应停止热键。新建动作默认 PP-OCRv6 快速（`OcrEnginePreference.DefaultForNewProbe`）
- **超级面板用完即收（`Views/SuperPanel.xaml.cs`）**：面板只当启动器——执行动作、打开设置、新建 / 编辑 / 复制 / 删除动作、从动作池选择，操作结束后（**含取消**）一律 `SuperPanel.Close()`；留在面板上的只有钉住、翻页和拖动重排。新增这类入口时别只 `Rebuild()`
- 「截图」取值（`Features/Flow/ScreenshotProbe.cs`）：交互框选复用 `ScreenPicker` 的框选 / 吸附；前台窗口 / 整个屏幕 / 固定区域直接 `ScreenCapture.Capture`，其中前台窗口用 `WindowFinder.GetVisibleBounds`（DWM 可见范围，不含阴影边距），固定区域支持 `${变量}` 解析成 `x,y,w,h`。裁下来的 png 走 `ImageLibrary.SaveGenerated`（`BitmapSource` 与 `Bitmap` 两个重载）存进 `resources\images\`，因此和模板截图共用按引用回收，不需要单独清理。**运行期截图的生命周期**：它是「截图」这一步的输出，本次运行内后面还要用（`.file` 给「文字识别」/「打开文件」/ 剪贴板），所以**不在步骤结束时删**，也不在运行结束时删（外部程序可能刚拿到路径）；由 `FlowRunner.RunAction` 在**下一次运行开始时**调 `CollectGarbage(DateTime.Now)` 清掉上一次的，界线用开始时间保证本次新截的不被误删。编辑器「▶ 测试」试截的图用 `ImageLibrary.DeleteGenerated` 测完即删
- **截图与识字是两步，别再合回去**：`Features/Flow/OcrProbe.cs` 只读图片（`Probe.OcrImage`：`${截图}` 取它的 `file`，或 `${截图.file}` / 文件路径），**不自己截屏**；要识别某个区域就先用「截图」取图。旧版「截图 OCR」（自带前台窗口 / 整个屏幕 / 固定区域）已不兼容，运行时提示缺图片
- OCR 双引擎：「文字识别」默认 `PP-OCRv6 快速`（内置在 `Assets/Ocr/ppocrv6`）；可切 `Windows.Media.Ocr`（最快、最省内存）。选择记在 `LTools.json` 的 `lastOcrEngine`，见 `Features/OcrEnginePreference.cs`。`Features/Flow/PaddleOcr.cs` 是 PP-OCRv6 推理（small 下载到数据根 `ocr-models/`，见 `OcrModelStore`）。RapidOcrNet 的包内 targets 会把自带的 v5 模型拷进输出目录，csproj 里有 target 在构建 / 发布后清理

## 其他

- `速记/`（速记文件）和 `logs/` 运行时生成，已 gitignore
- UIA 走 `FlaUI.UIA3`（托管包）：.NET Core 的 MSBuild 不支持 `COMReference`/`ResolveComReference`，不要再加 `UIAutomationClient` 的 COM 引用
- `app.manifest`：`requireAdministrator` + 声明 Win10/11 兼容 + `PerMonitorV2` DPI 感知
- **DPI 感知双轨**：Release 由 `app.manifest` 在进程创建时设 PerMonitorV2；Debug 用 `-p:NoWin32Manifest=true` 剥了清单（免 UAC），改由 `Core/DpiInitializer.cs` 的 `[ModuleInitializer]` 在 Main 前、WPF 加载前 `SetProcessDpiAwarenessContext(-4)` 补设。修「全屏游戏切分辨率后托盘右键菜单错位」。
