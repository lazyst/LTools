# 测试（tests/）

本目录放**仓库内**的端到端冒烟工程。产品自己的纯逻辑断言不在这里，在 `Core/SelfTest/`（`LTools.exe --selftest`）。

组织参照 snow-shot：每个模块旁边挂着 `#[cfg(test)] mod tests`（纯逻辑、随源码走），
跨模块的整链路用例放各 crate 的 `tests/`（真文件 / 真窗口 / 真进程）。对应到本项目：

| snow-shot | LTools | 跑法 |
|---|---|---|
| 模块内 `#[cfg(test)] mod tests` | `Core/SelfTest/Suites/*.cs`（按功能分文件：`CaptureProSuite.Drawing.cs`、`CaptureProSuite.Behavior.cs`…） | `LTools.exe --selftest`，单组 `--filter capturePro` |
| crate 的 `tests/` 集成用例 | `tests/<工程>/`（本目录，反射驱动 `LTools.dll`） | `dotnet run -- <段名>` |
| 人工真机验收 | 本文「人工验收」 | 手动 |

## 1. 自测（CI 门禁）

```powershell
dotnet build -c Release            # 或 Debug：dotnet build -p:NoWin32Manifest=true
LTools.exe --selftest              # 退出码非 0 = 失败，报告在 %TEMP%\ltools-selftest\report.txt
LTools.exe --selftest --filter capturePro --verbose
```

- 纯逻辑：几何、像素、归一化、解析、字节序列、序列化往返。
- **不碰真窗口、真鼠标、真光标、剪贴板、文件系统以外的东西**（临时文件可以有）。
- 新增功能时先在这里补断言：能变成纯函数的部分都搬成纯函数。

## 2. 冒烟（本目录，不进 CI）

`tests/` 下的工程**不进 CI**（Windows runner 没有交互桌面、没有真实光标，也会拖慢每次 push）。
它们是「构建 0 警 + selftest 全绿」之后的第三道门，日常改完截图 / 贴图 / 录屏要自己跑。

```powershell
cd tests\CaptureOverlaySmoke
dotnet build -v q
dotnet run --no-build                 # 全量（约 10 分钟）
dotnet run --no-build -- behavior     # 只跑一段（改哪块跑哪段）
```

输出写到 `%TEMP%\opencode\capture-overlay-smoke\out\`（截图 / PDF / 配置夹具都在这，不入库）。

### `CaptureOverlaySmoke` 的段

| 段名 | 覆盖 |
|---|---|
| `behavior` | 截图行为：指针 / Esc 退出确认 / 拖边框（快速选择修改 / 贴到指针）/ HiDPI 降采样 / PDF 纸张（真文件）/ 设置页七控件往返 |
| `drawing` | 绘制行为：默认值 / 工具清单窗口 / 自动选中 / 快速选择 / Delete 删字符 / 组首按钮 |
| `colorpresets` | 颜色预设：三族 15 色 / 设置页 / 跟随工具切换 / 取色弹窗（色板·色相条·透明度条·HEX/RGB/HSB） |
| `regiondrag` | 多块选区：拖命中的那一块 / 按住 Space 整体挪 / 中途换口径 / 外接框按并集 |
| `smart` | 智能选区：悬停预选 / 滚轮换层 / Tab 切整窗 / 单击确认 / 手框 / 右键退出（命中路径用 `SnapProbe.Pin` 注入，不依赖真实 UIA） |
| `historydisplay` | 采集历史轮播：底图落盘 / 每块屏裁图 / 卡片与大图翻张 / 重新编辑还原选区·外观·标注·撤销栈 |
| `split` / `collage` / `collagepdf` | 切图 / 拼图 / 拼图 PDF |
| `history` / `hidetotop` / `pinocr` / `clipboardmeta` | 采集历史 / 贴图收顶部 / 贴图识别 / 剪贴板元数据 |
| `record` / `webcam` / `autohide` / `render` / `globalscreen`(`globalcanvas`) | 录屏：区域框 / 选项 / 画中画 / 自动收起 / 后期渲染 / 全屏画布 |
| `recordsettings` | 录屏设置：设置页回显 / 改动落 `settings.json` / 归一 / 全局默认值进计划 |
| `keys` / `shortcuts` / `export` / `sound` / `captureEntry` / `releaseAction` | 键名 / 全局快捷键 / 导出 / 快门音 / 采集入口 / 释放动作 |
| `tray` | 托盘菜单：顶层条目与顺序 / 截图项收进二级菜单 / 真鼠标悬停展开与移开收起 |
| `nav` | 设置左栏「截图 Pro」分组：结构 / 九个子页可见性 / 记住上次子页 / 展开与当前页落盘 / **重开设置后按记录展开、点一下收起、再点一下展开** / 控件归属子页 |
| `recapture` | 重新截图（原位重抓）：选区 / 标注 / 撤销栈保留 / 只换底图 / 覆盖层不进画面 |
| `result` | 识别结果窗：页签与各页内容 / 最小化·最大化（铺满工作区）/ 原文栏（1:1 左右 · 上下 · 缩放 + 弹出）/ 排版 / 翻译 / 文档导出 |
| `picker` | 取色 / 坐标卡：选择工具下跟着光标 / 11×11 放大预览 / X·Y 坐标 / 色带与字色 / 显示时机 / Shift+P 相对坐标 |
| 不带参数 | 上面全部 + 覆盖层主链路（选区 / 标注 / 样式 / 多选 / 取色·坐标卡 / 贴图 / 识别 / 转换 / 翻译 / PDF…） |

### `MenuConeSmoke`（右键菜单「预测锥」）

```powershell
dotnet build -p:NoWin32Manifest=true      # 先构建产品：这个工程直接引用 bin\Debug 里的 LTools.dll
cd tests\MenuConeSmoke
dotnet build -v q
dotnet run --no-build                     # 15 条，约 5 秒
```

真 `ContextMenu` + `SetCursorPos` 斜着穿过相邻菜单项走向子菜单，断言「看得见的行为」：
悬停父项立即展开 / 斜穿 360ms 不换项、不冒出第二个子菜单 / 指针进子菜单后停留 600ms 仍打开 /
明确移到邻项才换 / 锥内短停 140ms 保持、停 660ms 让位 / 被划过的邻项底板不是 `HoverBg`（`#00FFFFFF`）/
横向离开菜单后子菜单收起。它会真的动鼠标，结束时把光标放回原处。

### `QuickNoteSmoke`（速记 Markdown 编辑器）

```powershell
dotnet build -p:NoWin32Manifest=true      # 先构建产品（直接引用 bin\Debug 里的 LTools.dll）
cd tests\QuickNoteSmoke
dotnet build -v q
dotnet run --no-build                     # 100 条，约 7 秒
```

用临时数据根构造真 `QuickNoteWindow`（反射驱动 internal 的 `NoteRepository`），断言「看得见的行为」：
正文已是 AvalonEdit / `WordWrap` 仍开 / 右键菜单六项都挂上命令 / Markdown 高亮按主题加载（围栏块**跨行**
且不漏到下一段、粗体带 `FontWeight`）/ 选区用主题 `EditorSelectionColor` / 占位文字随正文显隐 / 改正文标脏 /
**窗口默认不置顶、标题栏有图钉、点一下翻转并通知外面（`TopmostChanged`，`configs/ui.json` 由它落盘）** /
焦点在列表时 `Ctrl+Del` 才删速记 / `Ctrl+P` 的预览把源码藏起来且文档带源码行号、切回来文档被清空 /
**双击预览某一块才回源码（`TryJumpFromPreviewPoint`；单击不再跳）** /
**预览里选中文字还看得见（`SelectionOpacity < 1`，并逐像素数选区里的深色像素——选区色画在文字上面，
设 1 会把字整块盖住）** /
`JumpToLine` 定位到行首 / `Ctrl+S` 落盘正文一字不差；书写辅助直接调私有方法验文档：列表续写（含有序 +1、
待办变未勾、空项退出）、引用续写、`Wrap` 包住选区、`Tab` / `Shift+Tab`、行首记号开关、插入链接 / 表格、
工具栏 11 个按钮都不抢焦点；**预览里 Ctrl+F**：查找条露出、跨 `Run` 也命中（「1/2」）、命中处真被选中、
自己画的高亮框数量 / 深浅对得上（当前 0.6、其余 0.3，并取样那框底下的像素证明真的画在画面上）、
`StepFind` 走到第二处后实心框跟过去、关闭后查找条与高亮一起收起；预览里切另一条速记仍停在预览；列表行摘要（`NoteRepository.Snippet`
的断言在 `--selftest` 的 `note` 组，冒烟只验真的画出来了；附件冗余清理（孤儿 `.files` 夹 / 正文没引用的图 → `trash/`）、
「写速记带图」（复制进 `.files` 并改写正文，缺图报错）、「追加模式」（时间标题 / 指定目标 / 没有标题的旧笔记不改名）、
「读速记带摘要」（`.snippet`）也都在 `note` 组）；图片附件走真剪贴板，三种形态都验：DIB（`Clipboard.SetImage`）、
**只有 `PNG` 注册格式**（浏览器「复制图片」）、图片文件的 `FileDrop`（资源管理器里复制）→ 落盘到 `笔记名.files/` →
预览里变 `Image` → 预览里再粘一张会立刻重渲染 → 右键「粘贴」没图时照常贴文本 → 改名后目录与路径一起搬 → 删笔记连目录删掉。
最后渲染 `editor.png`、`editor-2x.png`、
`preview-2x.png`、`preview-find-2x.png`、`preview-image-2x.png`、`list-2x.png`、`empty.png` 到
`%TEMP%\opencode\quicknote-smoke\out\`，并逐像素确认代码色 / 链接色真的画在画面上。
**中文输入法的组字手感、预览里「点某一块跳回源码」的命中，自动化替不了，仍要真机点一遍。**

### `PanelClipboardSmoke`（复制引用 / 复制为副本 / 粘贴）

```powershell
dotnet build -p:NoWin32Manifest=true      # 先构建产品（同样直接引用 bin\Debug 里的 LTools.dll）
cd tests\PanelClipboardSmoke
dotnet build -v q
dotnet run --no-build                     # 21 条，约 2 秒
```

用临时数据根（`%TEMP%\ltools-panel-clip-*` 下的 `configs/` + `actions/`，跑完删掉）驱动真实的
`ConfigStore` / `PanelLibrary` / `PanelActionOps`，断言：复制引用后粘到另一个分组「是同一个动作、不新建」/
空剪贴板什么都不粘 / 复制为副本后粘到指定位置「新 Id + 名字加『副本』+ 自己的 `files/`」/
跨场景（上下文场景）粘贴 / 空剪贴板不挂「粘贴」而菜单树里能选到每个分组 / 源动作被删后粘不了并清空剪贴板。

### `ShareSmoke`（局域网共享：真 Kestrel + 真 HttpClient + 真窗口 + 真鼠标拖放）

```powershell
cd tests\ShareSmoke
dotnet run                                # 92 条，约 10 秒（中间有几次真的拖鼠标）
dotnet run -- --explorer                  # 再加一段：真开资源管理器，把文件拖进窗口（提权时收不到，断言的是这一点）
dotnet run -- --helper                    # 再加一段：拉起 ShareHost.exe，真拖一个文件进去（两个都开时共 103 条）
```

`--helper` 是**跨进程**那段：按产品那条路拉起 `ShareHost.exe`（`Native/ShellToken` 复制 Explorer
的令牌，普通权限；不是提权运行时退回 `explorer.exe`），断言「窗口出来 / `TokenElevation == 0` /
没把资源管理器叫到前台（窗口条数不变）/ 真资源管理器拖进去后 `share/items.json` 多一条」。
它会往真实共享区加一条，跑完把 `items.json` 原样还原。

临时数据根（`%TEMP%`），反射启动 `ShareServer`（随机端口），断言：页面与 `/api/state`、
清单外路径 404、原件下载字节一致并声明 `Accept-Ranges`、分片上传中断后带 `offset` 续传、
`sha256` 不匹配 422、重复 finish 404、暂停接收 403、令牌关闭 / 打开两种形态（根路径提示页、
无令牌 401、带令牌 200）；手机发来的文字只落 `inbox/`、**不进**「发出去的」。用
`FrameworkReference Microsoft.AspNetCore.App`，因为 Kestrel 的共享框架不在默认输出里。

后半段在**一条 STA 线程**上开真 `ShareWindow`（top-level 语句是 MTA，产品窗口要 STA +
`Application` + `Themes/Modern.xaml`），断言输入框（只占左栏）/ 两个图标按钮（字形是 MDL2）/
回车发送 / 右键菜单 / 拖放 / 剪贴板按钮 / 单实例 / 右栏平铺的设置项，以及：
- **真鼠标拖放**：先拿一个普通 `Window` 当对照组（证明自动化可用），再拖共享窗口。
  断言用 `AddHandler(..., handledEventsToo: true)`——产品自己的处理器会 `e.Handled = true`，
  普通 `+=` 永远数到 0（踩过）。`DragEventArgs` 只有 internal 构造函数，反射建。
- **提权通道**：`AllowDrop=False` + `WS_EX_ACCEPTFILES` + 手工造 `HDROP` 发 `WM_DROPFILES` 能收、
  同一文件再拖不重复。
- 窗口渲染成 `%TEMP%\opencode\share-smoke\window.png`。

`--explorer` 段：开资源管理器（临时目录）、用产品的 `ControlTargets.Snapshot` 找文件行、
真鼠标拖进共享窗口。**两条结论都靠它验过**：非提权（用 `explorer.exe <exe> --log --explorer`
从资源管理器启动这个冒烟）能拖进来；提权时 Explorer 连 `WM_DROPFILES` 都不发（钩子数到 0），
这是 UIPI 的系统限制，见 `docs/LAN_SHARE.md` §3.2.1。`--log` 把输出写进
`%TEMP%\opencode\share-smoke\report.txt`（从资源管理器启动时没有控制台可看）。

### `LinkSmoke`（进程间链路：真命名管道上的握手 / 请求 / 事件 / 超时）

```powershell
cd tests\LinkSmoke
dotnet run                                # 43 条，约 6 秒
```

真起一条 `LToolsLink-test-*` 管道，`LinkServer` + `LinkClient` 走完整流程：令牌不对被拒绝、
握手、请求 / 答复（中文 / 引号 / 反斜杠原样）、双向事件、**服务端向客户端发请求（反向角色，
生产里正是宿主做服务端）**、服务端抛异常变成失败答复、请求超时（150ms 的请求 156ms 返回，之后连接照常）、
**1 MB 负载**（帧切分与缓冲增长）、服务端停止后客户端看到关闭并收到断开事件、服务端重启后能重连。

后半段（`== 面板命令 ==`）在**临时数据根**上跑 `ConfigStore.Initialize`，再按生产方向
（`LinkServer` 发请求、`LinkClient` 用 `PanelHost.HandleAsync` 应答）驱动面板写属主：
`panel.snapshot` 带回动作与分组、`panel.replace` 同时改内存与 `configs/panel.json`、`action.add`
新 Id + 落盘、`action.duplicate` 新 Id + 名字带「副本」、`action.delete` 连动作目录与面板引用一起清、
未知命令回失败答复。最后一段（`== 磁贴转交 ==`）验 `PanelHostBridge`：把 `ActionExecutor.RunRef`
接管成 `run.action` 请求、把「设置 / 暂存区 / 面板场景」接管成 `open.window` 请求、
把 `PanelActionOps.AddNew` 接管成 `panel.op` 请求、把 `PanelLibrary.Save` 接管成 `panel.replace`，
断言引用 / 坐标 / 来源 / 目标 / 操作名 / 分组 / 整份配置都原样送到对端（宿主里绝不本地执行、也不本地写盘）。

链路层是 `internal`，这里用 `InternalsVisibleTo("LinkSmoke")` 直接驱动（异步委托用反射太难看）；
其它冒烟仍按惯例走反射。

### `PanelHostSmoke`（普通权限宿主的只读初始化）

```powershell
cd tests\PanelHostSmoke
dotnet run                                # 11 条，约 2 秒
```

`ConfigStore.InitializeReadOnly` 是宿主进程（界面进程）用的：它要把动作 / 面板 / 快捷键读进内存
（面板要显示），但**一个字都不写**（写入归提权主程序）。段落的做法是先用主程序的 `Initialize`
建一份临时数据根（这次会写盘），快照整个目录 → 调 `InitializeReadOnly` → 再快照，断言两次快照
**逐字节相同**（文件名 / 大小 / SHA-256 / 修改时间），并且动作 / 面板 / 种子快捷键都读得到。
再验只读进程里 `ConfigStore.Save` / `PanelLibrary.Save` 被拒绝（返回 false、目录不变），
最后拿一个**空数据根**调 `InitializeReadOnly`，断言它不会把默认面板凭空写出来。

### `TableSmoke`（表格识别 `screen.table`）

```powershell
cd tests\TableSmoke
dotnet run                                # 23 条，首次要构建产品 + 加载 PP-OCRv6，约 20 秒
```

**它用 `ProjectReference` 而不是 HintPath**：表格识别要 OpenCvSharp 的 native、ONNX Runtime 与
`Assets\Ocr\ppocrv6` 的内置模型，这些只有引用产品项目才会被带进输出；换成直接引用 `LTools.dll`
会以 `DllNotFoundException: OpenCvSharpExtern` 收场。

用 GDI+ 现画三张图（带框线 3×3 / 无线框 3×2 / 表头跨两列合并），真跑 `TableReader.Read`，
断言**结构**而不是文字（OCR 结果随机型 / 语言包略有差异）：`auto` 在有框线图走线检测、无线框图退回聚类 /
`lines` 在无线框图 `found=false` / 合并表 2 行 3 列且合并是 `0,0,1,2`、被盖住的格子不是锚点（`rows` 里留空）/
TSV 3 行每行 2 个制表符、CSV 按 CRLF 分 3 行、Markdown 4 行（表头 + 分隔行 + 2 行数据）、JSON 3×3。
测试图不入库（跑在 `%TEMP%` 下，结束删掉）。

### `UiaPickSmoke`（控件操作 `ui.uia` 的控件选择器）

```powershell
cd tests\UiaPickSmoke
dotnet run                                # 32 条，约 6 秒
```

起一个**子进程** WPF 窗口（两个带 AutomationId 的按钮），对它的真实 UIA 控件树跑
`ControlTargets.Snapshot` / `AtPoint` / `RollUp` / `CountSame`：跨进程取树、按 AutomationId 找到
「确定」按钮、光标落在按钮上命中后能抬回 Button、同名计数只有它自己、窗口外的点不命中。

同一份窗口也跑两条「懒下钻」链路（`SnapProbe` → `SnapTree` → FlaUI）：

- 截图吸附：`Resolve(expand: true)` 能一路钻到「按钮 · 确定」那一层并走完、路径深 → 浅、
  标签读得到、第二趟 `expand: false` 直接命中缓存；
- 点选器悬停（`ControlSnapSource`）：支路上每一层都带身份（名称 / AutomationId / 类型）、
  `PickOnPath` 跳掉末尾的窗口并挑中按钮那一层、同名计数走 provider 条件查找（1 / 1 / 0）。

再验运行时那条（`ui.uia` 的 `Find`）：把「最大深度 / 最多元素」都压到 1 也照样找得到「确定」
（走 provider 条件查找，BFS 兜底时才受这两个上限约束）、名称首尾空白不影响命中、名称对不上就找不到。

点选遮罩的鼠标交互（高亮、信息条、Esc / 右键、控件树对话框）仍要人工在真机上点一遍。

**必须分两个进程**：UIA 的 provider 在目标进程的 UI 线程上响应，同一个进程里父线程阻塞等结果
会死锁（消息循环发不出响应）。所以 `dotnet run` 不带参数是父进程，带 `host` 是子进程窗口。
子进程结束后父进程 `Kill(entireProcessTree: true)`，不会留下窗口。

### 写法约定

- `Check(条件, "中文断言文案")`：失败只记一条，不中断（最后汇总失败数）。
- 用 `Field` / `Member` / `Invoke` 反射摸 internal 类型，**不复制产品代码**。
- **调带可选参数的方法一律走 `CallStatic(type, "名字", 实参…)`**：反射不会替可选参数补默认值，
  `Capture(area, withCursor = false)` 用 1 个参数 `Invoke` 会抛 `TargetParameterCountException`
  （产品给方法加了可选参数、冒烟还按旧数量调用时，会跑到一半才炸）。
- 需要配置的段把 `ConfigStore.ConfigPath` 指到 `out\<段名>-config\`，跑完恢复原路径并 `Invalidate()` 缓存，
  不给后面的段留脏配置（见 `Program.Drawing.cs` / `Program.Behavior.cs` 的头尾）。
- 断言「看得见的行为」：几何数字、像素颜色、落盘文件字节、控件状态、光标 / 窗口真实响应。
- 每段自带 `-- <段名>` 入口：调试时只跑一段，别为了改一行等十分钟。

## 3. 人工验收

自动化覆盖不到的部分（真机 DPI、真鼠标手感、系统对话框、托盘、开机自启）改完仍要点一遍：

- 125% / 100% 两种缩放下截图与拖边框；
- 速记：微软拼音在正文里打中文（组字、候选框位置、撤销 / 重做）；
- 截图带指针（开关前后各截一张看指针在不在）；
- Esc 退出确认（开关前后）；
- 另存为 PDF（A4 竖 / 横 / 图片尺寸各存一份，用 Edge 打开看版面）；
- 长截图自动滚动间隔（改 300ms 看是否更慢）。
