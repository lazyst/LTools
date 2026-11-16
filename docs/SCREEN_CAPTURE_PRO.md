# 截图 Pro：截图覆盖层 · 标注 · 工具栏

> 复刻 Quicker「截图 Pro」的截图/标注能力（模块 ① 截图与标注工具栏、② 完成动作、③ 贴图、④ 识别、
> ⑤ 长截图、⑥ 录屏、⑦ 采集历史、⑧ 屏幕选字、⑨ 取色），UI **借布局换风格**：同样的覆盖层与工具栏
> 结构，配色与图标按 LTools 主题。
>
> 功能来源是 REA 对 `Quicker.dll` v2.3.36.0 的静态分析 + Quicker 自带的步骤文档与数据目录；
> 交互参考 `github.com/mg-chao/snow-shot`（C++/Qt/Rust，GPL-3.0）的模块划分，**不搬它的代码**
> （语言与许可证都不同）。
>
> 本文是规格：实现若偏离，先改本文再改代码。`AGENTS.md` 只留约束与踩过的坑。

## 0. 目标与边界

**做什么**

- 一个**截图覆盖层**：冻结屏幕 → 拖框（可吸附窗口/控件，可再微调）→ 在图上**标注** → 复制 / 贴图 / 保存 / 关闭。
- 一套**浮动工具栏**：13 个工具常驻 + 选中工具后的**工具参数条**，可拖动位置、双击复位。
- 覆盖层同时服务三个入口：`screen.capture` 的交互模式、内置功能「截图」（`common.capture`，快捷键 / 面板 / 悬浮）、
  以后的贴图与识别都从同一个标注文档取图。

**不做什么（整体明确排除）**

- 一切依赖云的能力：在线 OCR、Mathpix 公式、百度/有道图片翻译、AI 改图、发送到工作台。
- 用户自定义工具栏（布局 JSON / JS 脚本 / 自定义按钮）：需求方未勾选，工具栏是固定的一套。
- 快速截图（按住鼠标键 + 引导键直接截图）与第三方截图工具桥接。

## 1. 已确认的决策

| # | 决策 | 结论 |
|---|---|---|
| 1 | 入口 | **升级现有 `screen.capture` 的交互框选**，不新增动作单元；`foreground` / `screen` / `rect` 三种非交互模式行为不变 |
| 2 | 快捷键入口 | 用现有内置功能「截图」（`common.capture`，范围=框选），**不新增 `ltools.capture`**：功能 Id 是唯一身份，同一件事不挂两个 Id |
| 3 | UI 风格 | 借布局换风格：结构与 Quicker 一致，配色 / 图标用 LTools 主题；图标自绘几何，不新增未验证的 Segoe 码点 |
| 4 | 完成动作 | 复制到剪贴板（默认）/ 贴图 / 另存为 / 关闭；先不加配置域字段 |
| 5 | 出图 | 显示与导出**共用一条渲染路径**（`MarkRenderer`），不用「截屏再裁」 |
| 6 | 标注的可编辑性 | P0：选中（改为当前工具）/ 拖动移动 / `Delete` 删除 / 文字双击改字；**缩放手柄留后续** |
| 7 | 橡皮 | 整笔删除（删掉整个标注元素），不做像素级擦除 |
| 8 | 贴图 | P1 已落地：无激活 / 拖动 / 滚轮缩放 / 不透明度 / 置顶 / 锁定 + 贴图组登记处；**局部显示与成组移动留后续** |
| 9 | 识别 | P2-1 已落地：文字 + 二维码 + 本地近似表格（PP-OCRv6 词框聚类出 TSV/HTML），**不做公式与翻译** |
| 10 | 录屏 | P4，随包 ffmpeg（与 AutoHotkey 同一套：`runtime/` + `tools/fetch-*.ps1` + `licenses/`） |
| 11 | 采集历史 | P2，新数据根 `captures/`（`catalog.json` + `assets/` + `thumbnails/`），进备份域白名单 |

## 2. 覆盖层结构

### 2.1 窗口与坐标系

- `Views/CapturePro/CaptureOverlayWindow`：无边框、`Topmost`、`ShowInTaskbar=False`、
  `WindowSizing.Mode="Overlay"`（完全不干预尺寸与位置）。
- 盖住**所有显示器的并集**（跨屏选区，P3-1）：`ScreenShot.Bounds()` = 逐块显示器矩形的外接矩形，
  窗口按「区域 ÷ 光标下那台显示器的 DPI」给初值，`Loaded` 里再用 `SetWindowPos` 按**物理像素**钉死；
  底图按 `ActualWidth / 像素宽` 校准一次（`CaptureDocument.SetScale`），跨屏 + 混合 DPI 下不会被错铺。
- **冻结画面逐块显示器抓帧再拼**（`ScreenShot.Capture`）：混合 DPI 下一次 `BitBlt` 跨屏可能拿到
  被系统缩放过的内容；每块只抓落在取景范围内的那一块，再按物理坐标贴回一张 Bgra32 位图。
  单屏时直接返回那一块，不复制。
- **选区与标注几何一律用「物理像素」**（与截图 / 找图 / 鼠标注入同一坐标系）；
  只有绘制和 WPF 命中测试才换成 DIP。换算只在 `ToDip` / `ToPhysical` 两个方法里，别处不许乘除 DPI。
- 输出：`Area` = 屏幕绝对物理像素矩形（`x/y/w/h`），`file` = 成品 png 路径。
- 窗口打开前：`TransientMinimize.MinimizeAll(owner)`（把自己的窗口收起来）→ `Thread.Sleep(SettleMs)` →
  `ScreenShot.Capture(ScreenShot.Bounds())` 抓帧。关闭时 `finally` 还原窗口。
- 打开期间：`MouseHook.SetWindowPickerActive(true)`（让低级鼠标钩子让路）、
  `HotkeyRegistry.Suspended = true`（单键绑定不许吞掉框选里的按键）。
- 吸附线除了窗口 / 控件边，还带**每块显示器的四边**：跨屏拖框能贴住屏幕交界、也能直接框住某一整块屏。
- 贴图的缩放比按**这块选区所在显示器**算（`ScreenShot.ScaleOf`），不用覆盖层自己那块屏的比例。
- `Views/ScreenPickerWindow`（取模板图 / 取点 / 取色）仍是**光标所在那一块屏**，与覆盖层不是同一套，
  别顺手改成整屏：那边的几何按「1 像素 = 1 DIP 的冻结图」算。

### 2.2 图层（自下而上）

| 层 | 内容 |
|---|---|
| 底图 + 标注 | `CaptureCanvas`（自定义 `FrameworkElement`，`OnRender` 画底图与全部标注） |
| 压暗遮罩 | `Path`（`FillRule=EvenOdd`）：选区/悬停目标挖洞，其余 40% 黑 |
| 选区框 | 深色描边 + 白色细描边 + 8 个手柄（四角 + 四边） |
| 吸附高亮 | 琥珀色矩形 + 标签（窗口/控件名） |
| 尺寸信息条 | `宽 × 高`（物理像素）+ 光标坐标；跟随光标，贴边翻转 |
| 工具栏 / 参数条 | `CaptureToolbar`（贴选区下方，放不下翻到上方，再不行放到选区内侧） |
| 就地文字输入 | `TextBox`（只编辑时出现，`Esc` 取消、`Ctrl+Enter` 或点外部提交） |
| 取色色卡 | `Border`（只取色工具下出现，色块 + `#RRGGBB · rgb()`，跟着光标、贴边翻转） |

### 2.3 状态机

```
Idle(未框选) --拖动--> Selecting --松手--> Selected
Selected --点击工具--> Drawing(可连续画)
Selected/Drawing --Esc--> Selected(取消工具) --Esc--> Idle --Esc--> 关闭
Selected --Enter/双击/✓--> 完成（复制）
Selected --Ctrl+T / 贴图--> 完成（贴到屏幕上）
Selected --Ctrl+R / 识别--> 完成（开识别结果窗，也返回这张图）
Selected --取色工具 + 单击--> 复制颜色（留在覆盖层）
```

## 3. 标注模型

`Features/CapturePro/Annotations.cs`：**不可变**（record / 只读属性 + `With…` 方法），
撤销栈只存「元素列表快照」，不需要每类自己实现撤销。

| 类型 | 关键字段 | 参数范围 |
|---|---|---|
| `StrokeMark` | 点列、颜色、线宽、荧光笔开关、透明度 | 线宽 1–50；荧光笔透明度 20–70% |
| `ShapeMark`（矩形 / 椭圆） | 起止点、描边色、线宽、虚线 | 线宽 1–50 |
| `ArrowMark` | 起点、终点、颜色、线宽、箭头大小 | 线宽 1–50 |
| `PolylineMark` | 点列、颜色、线宽、是否带箭头 | ≥2 点 |
| `TextMark` | 原点、文本、字号、颜色、外描边宽度/颜色、背景不透明度、圆角 | 字号 8–72（默认 20）；描边 0–12（默认 3）；背景 0–100%（默认 85）；圆角 0–50（默认 4） |
| `SerialMark` | 圆心、序号、颜色、样式（实心圆 / 空心圆 / 方形）、字号 | 序号从 1 起，删除不重排（与 Quicker 一致） |
| `MosaicMark` | 矩形、块边长 | 块 4–24 DIP（默认 8）；**P0 只有矩形**，笔刷形状放 P1 |
| `SpotlightMark` | 矩形、暗度 | 40–80%（默认 55%） |

排序 = 列表顺序（后画的在上面）。`CaptureDocument` 负责：
选区、标注列表、当前工具、当前样式、撤销 / 重做（快照列表，上限 50）、`Dirty`。

## 4. 渲染与导出

- `Features/CapturePro/MarkRenderer.cs`：`Draw(DrawingContext dc, Mark mark, BitmapSource baseImage)`。
  **显示与导出都调它**，禁止第二套绘制代码（否则「所见」与「所得」必然分叉）。
- **马赛克**：取 `CroppedBitmap(baseImage, 物理矩形)` → `TransformedBitmap(ScaleTransform(1/block))`
  → 按原矩形绘制，`BitmapScalingMode.NearestNeighbor`。结果按「标注对象 + 块大小」缓存
  （`ConditionalWeakTable`），否则拖动时每帧都在重采样。
- **聚光**：`CombinedGeometry(Exclude, 全屏, 选区)` 当裁剪区，再画 55% 黑；它**压暗的是已画的内容**，
  之后画的标注不受影响（与「聚光 + 继续标注」的直觉一致）。
- **文字**：`FormattedText.BuildGeometry` 取轮廓 → 先描边后填充（外描边），背景是带圆角的半透明矩形。
- **导出**：新建 `DrawingVisual`，`PushTransform(new ScaleTransform(scale, scale))`，
  按 DIP 画「底图 + 标注」，再 `RenderTargetBitmap(物理宽, 物理高, 96, 96, Pbgra32)`。
  DPI 用 96 + 缩放变换，纹理尺寸就是物理像素，文字与描边是矢量重绘（不是放大位图）。

## 5. 工具栏与参数条

`Views/CapturePro/CaptureToolbar.xaml`（`CaptureIcons.xaml` 放自绘几何图标）。

**主栏**（默认）：

```
选择 | 画笔 荧光笔 | 矩形 椭圆 箭头 | 文字 序号 | 马赛克 | 撤销 重做 | 保存 复制 ✓完成 ✕
```

**更多**（`⋯` 溢层）：折线、聚光、橡皮。

**工具参数条**（选中工具后出现在主栏上方，同一行显示，超宽才折行）：

| 工具 | 参数 |
|---|---|
| 画笔 | 颜色（8 色）、线宽（1/2/4/8/12 + 数字框） |
| 荧光笔 | 颜色、线宽、透明度（20/35/50/70） |
| 矩形 / 椭圆 / 箭头 / 折线 | 颜色、线宽、实线 / 虚线 |
| 文字 | 字号（14/20/28/36 + 数字框）、颜色、外描边（关 / 1–12）、背景（无 / 半透明） |
| 序号 | 颜色、样式（实心圆 / 空心圆 / 方）、起始号 |
| 马赛克 | 块大小（4/8/16） |
| 聚光 | 暗度（40/55/70） |
| 橡皮 | 无 |
| 取色（P1） | 无（色卡跟着光标显示色值） |

规则：

- 工具栏**可拖动**（按住空白处拖，位置只记在本次会话），**双击复位**到「贴选区下方居中」。
- 颜色与线宽等样式**按工具各自记住**（切回来还是上次的值），与 Quicker 的「每种工具独立继承」一致。
- 工具栏与参数条都不许挡住选区中心 40%：放不下时依次试「下方 → 上方 → 选区内侧底部」。

**P0 落地（2026-11-16）与本节的差别**（先跑通，视觉细化留给后续阶段）：

- 13 个工具（P1 加取色）**全部常驻主栏**（没有 `⋯` 溢层，放不下就由 `WrapPanel` 折行）；按钮由 `ToolCatalog.All` 生成。
- 参数条在主栏**下方**（工具在上、参数在下：贴屏幕底时参数不会顶到手腕）。
- 样式是**一份共享状态** `ToolStyle`（颜色 / 线宽 / 字号 / 块大小 / 透明度），切工具沿用同一份，不按工具各记一套。
- 参数只开「这个工具真正用得到」的几格：文字 = 颜色 + 字号（外描边固定「与字色相反」的 0.75）；序号 = 颜色（圆形、从 1 起、删除不重排）；形状 = 颜色 + 线宽（虚线开关留给 P1）；马赛克 = 块大小；聚光 = 暗度；荧光笔 = 颜色 + 线宽 + 透明度。
- 默认落点是**屏幕底部居中**（选区可能贴着屏幕底，跟选区走会被挤到屏幕外），双击复位也回那里；用户拖过之后才按用户的位置，只在贴边时收回可见区。

## 6. 交互规格

**鼠标**

- 拖动 = 框选；松手可继续拖手柄 / 拖选区内部移动。
- 未框选时单击 = 选中光标下吸附到的窗口/控件区域。
- 滚轮 = 切换吸附层级（窗口 ↔ 内部控件），已框选时滚轮不改变选区。
- 右键 = 与 `Esc` 同一套逐级退出（折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 退出）。
- 双击 = 完成（选择工具下；正在作图时双击留给折线收尾）。
- 选中画笔/形状工具后，拖 / 单击即在选区内作图；画的元素超出选区时按选区裁剪。
- 选择工具下点中已有标注 = 选中（画一圈虚线）+ 可直接拖动；拖动中不往撤销栈里塞中间帧。
- 取色工具下单击 = 把光标下的颜色复制成 `#RRGGBB`，**不落标注、不关覆盖层**（可以连着取）。
- 选区外的按下永远重新框选（任何工具下都能改框），八个手柄在任何工具下都能用。

**键盘**

| 键 | 作用 |
|---|---|
| `Esc` / 右键 | 逐级退出：折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 关闭 |
| `Enter` | 完成（复制）；折线进行中 = 收尾；文字输入中 = 提交（`Shift+Enter` 换行） |
| `Ctrl+C` / `Ctrl+S` | 复制 / 另存为 |
| `Ctrl+T` | 贴图（把成品钉到屏幕上并关闭覆盖层） |
| `Ctrl+R` | 识别（文字 / 表格 / 二维码，结果窗在覆盖层关掉之后开） |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `Ctrl+A` | 选整个屏幕 |
| `Delete` / `Backspace` | 删除选中的标注 |
| 方向键（`Shift` 加速） | 微调选区 1px / 10px |
| `Q P H R E A L T N M S X C` | 选择 / 画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 折线 / 文字 / 序号 / 马赛克 / 聚光 / 橡皮 / 取色（打字时全部让给输入框） |

## 7. 完成动作

| 出口 | 行为 |
|---|---|
| 复制（默认，`Enter` / 双击 / ✓） | 导出 png → `NativeClipboard.SetImage`（同时写 `PNG` 与 `CF_DIB`）→ 关闭 → toast「已复制」 |
| 贴图（`Ctrl+T` / 动作栏 / 工具栏图钉） | 导出 png → 关闭覆盖层 → `CapturePinManager.Pin(...)` 钉到屏幕上（正好盖住选区）；**不动剪贴板** |
| 识别（`Ctrl+R` / 动作栏 / 工具栏扫描框） | 导出 png → 关闭覆盖层 → 开识别结果窗（文字 / 表格 / 二维码，见 §10）；**不动剪贴板** |
| 长截图（`Ctrl+L` / 动作栏） | 关闭覆盖层 → 滚屏拼接（见 §12）→ 成品替换选区的图 → 复制 + toast；取消 = 什么都不要 |
| 保存（`Ctrl+S` / 另存为…） | `CaptureSave.TrySave`：另存为对话框（默认目录「图片」，文件名 `截图-yyyyMMdd-HHmmss.png`）→ 关闭；取消 = 留在覆盖层继续改 |
| 关闭 / `Esc` | 丢弃，什么都不做 |

作为 `screen.capture` 的一步时，「完成」= 返回这张图（`file` 落 `ImageLibrary.SaveCapture`）——
选「贴图」也一样：贴一份到屏幕上，同时照常返回；
「取消」= `found = false`，**不报错**（与现有截图步骤同一口径）。

## 8. 接入点

| 位置 | 改动 |
|---|---|
| `Features/Flow/ScreenshotProbe.cs` | `interactive` 分支改调 `CaptureSession.TryCapture`（在 UI 线程 `Dispatcher.Invoke` 里开窗），其余三种模式不动；取消仍是 `found = false` |
| `Features/Units/Screen/CaptureScreenUnit.cs` | 不动（参数与输出都不变；交互模式只是「取图方式」的一种） |
| `Features/Builtin/FeatureCatalog.cs` | 「截图」功能（`common.capture`）的范围=框选改走同一个覆盖层；**不新增 `ltools.capture`**：功能 Id 是唯一身份，同一件事不挂两个 Id |
| `Features/HotkeySeeding.cs` | 不动（种子仍是 20 条）；「截图」由用户在快捷键页自行绑定，或在面板 / 悬浮里放 |
| `Features/Flow/ImageLibrary.cs` | 不改；成品仍走 `SaveCapture`（`cache/screenshots/`，下次运行清空） |
| 数据根 | P0 **不新增**目录；`captures/` 随 P2 的采集历史一起加 |

## 9. 贴图与取色（P1）

### 9.1 贴图

出口两处，行为一致（都先按标注文档重绘成品，再建贴图窗口）：

| 入口 | 行为 |
|---|---|
| 动作栏「贴图」/ 工具栏图钉 / `Ctrl+T` | 关掉覆盖层 → 把成品钉到屏幕上，正好盖住刚才框选的那块 |
| 作为 `screen.capture` 的一步 | 同上，并且照常返回这张图（`found = true` / `file`） |

窗口行为（`Views/CapturePro/CapturePinWindow.xaml(.cs)`、`Features/CapturePro/PinMath.cs`）：

| 操作 | 结果 |
|---|---|
| 拖动 | 移动窗口；至少留 40 DIP 在虚拟桌面里（拖出去也找得回来） |
| 滚轮 | 缩放 10%–400%，一格 10%，**锚在光标下**（缩放前后光标下的内容不动） |
| `Ctrl` / `Shift` + 滚轮 | 不透明度 20%–100%，一格 5% |
| 双击 | 关闭 |
| 悬停 | 右上角出现 ✕ |
| 右键 | 复制图片 / 另存为… / 缩放（100·50·200·400%）/ 不透明度（100·80·60·40%）/ 置顶 / 锁定位置 / 关闭 / 关闭全部贴图 |

- **不抢焦点**：`ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`（与悬浮窗同一套）。
  点它不改变前台，紧接着的键鼠注入仍打在原来的程序上。
- 代价是收不到键盘事件（`Esc` 关不掉），所以出口全部做成鼠标可达：双击 / ✕ / 右键菜单。
- 锁定 = 不能拖动、不能缩放、不能改不透明度，边框变蓝；解锁仍走右键菜单。
- 出场 100%：窗口 = 选区物理像素 ÷ 该屏缩放，正好压住刚截的那块。
- **贴图组**（`Features/CapturePro/CapturePinManager.cs`）：所有还开着的贴图的登记处——
  `Count` / `All` / `CloseAll()` / `RaiseAll()`；关窗由窗口自己 `Forget` 摘掉。
  贴图**不进配置、重启不恢复**（与悬浮项同口径：它是临时视图，不是用户资产）。
- P1 不做「局部显示」与「成组一起移动」：前者要另做裁剪模式，后者要成组选择与联动，各留一轮。

### 9.2 取色

标注工具栏第 13 个工具（`CaptureTool.Picker`，字母 `C`）：

| 行为 | 说明 |
|---|---|
| 移动 | 光标旁跟一块色卡：色块 + `#RRGGBB · rgb(r, g, b)`；贴边自动翻转 |
| 单击 | 把 `#RRGGBB` 复制到剪贴板 + 1600ms toast；**不关覆盖层**，可以连着取几个 |
| 读取来源 | 冻结底图（`CaptureDocument.Base`），标注与压暗不参与；不要求先框选 |
| 越界 | 光标在窗口边缘算出界只是「这一下没取到」，不报错 |
| 退出 | `Esc` 退回选择工具（与其他工具同一套逐级退出） |

纯函数在 `Features/CapturePro/PickerColor.cs`（`Read` / `Hex` / `Rgb` / `Describe`），`--selftest` 直接断言。

## 10. 识别（P2）

入口三处，行为一致（都先按标注文档重绘成品，再关覆盖层，然后开结果窗）：
动作栏「识别」/ 工具栏扫描框 / `Ctrl+R`；作为 `screen.capture` 的一步时也开，并且照常返回这张图。

结果窗（`Views/CapturePro/CaptureResultWindow.xaml(.cs)`）三个页签，每页各有复制：

| 页签 | 内容 | 复制 |
|---|---|---|
| 文字 | OCR 的行文本（换行分隔） | 复制文字 |
| 表格 | 本地**近似表格**：词框按行 / 列聚类，2 行 2 列以上才显示 | 复制 TSV / 复制 HTML |
| 二维码 | 解码内容 + 格式（`QR_CODE`） | 复制二维码内容 |

- **窗口先开再算**：识别要一两秒，`Loaded` 里把 `CaptureRecognize.Run` 丢到后台线程，算完回 UI 线程填内容；
  失败（如系统没装 OCR 语言包）在状态行说明原因，窗口照常可用。
- 引擎取「上次选用的 OCR 引擎」（`OcrEnginePreference.DefaultForNewProbe`，默认 PP-OCRv6 快速），
  与「文字识别」取值同一套；状态行写明引擎名与用时。
- 底层复用 `OcrLayout.Read`（带坐标的行 / 词）与 `QrCodeAlgorithms.Read`（ZXing，只认 QR），
  **不另写识别实现**；表格聚类是纯函数 `OcrTable.Build`（`Features/CapturePro/OcrTable.cs`）。
- 不做公式识别与翻译（Quicker 那两条都靠云）。

## 11. 采集历史（P2-2）

截图 / 识别的落点：数据根 `captures/`（`catalog.json` + `assets/<id>.png` + `thumbnails/<id>.png`），
进备份域白名单（`BackupService.BuildPlan`），换机后历史还在。

- 一条 = 一次**已经完成**的截图：`Kind` = `shot`（复制 / 贴图 / 另存为）或 `recognize`（识别）；
  `Source` 记完成动作（复制 / 贴图 / 识别 / 另存为），`CreatedAt` 固定 `yyyy-MM-ddTHH:mm:ss`，
  `Width` / `Height` 是成品图的物理像素。
- 取消覆盖层不记；另存为在对话框里取消也不记（那一次没完成）。
- 识别条目先记一条 `State = pending`（列表显示「识别中…」），识别结果窗算完后 `SetText` 回填文字并改成
  `done`；用户提前关掉结果窗也照样回填。程序中途退出留下的 `pending` 十分钟后按「没有识别到文字」显示。
- **保留策略**：最多 200 条、最多 30 天，超出连同两张图一起删；启动时后台 `Maintain()` 一次，
  每次新增也顺带剪枝。纯函数 `PlanPrune` / `ThumbnailSize` 由 `--selftest` 直接断言。
- 缩略图长边 240（小图不放大）；预览按宽 1600 解码，复制 / 另存为时再读原图，列表不把整张大图常驻内存。
- 窗口（`Views/CapturePro/CaptureHistoryWindow`）：三页签（全部 / 截图 / 识别）+ 搜索（150ms 防抖，认识别文字）
  + 左列表 + 右预览 + 识别文字只读框；操作：复制图片 / 另存为… / 复制识别文字 / 删除 / 清空全部 / 打开目录 / 关闭。
  双击整行 = 复制图片（`ShouldCopyOnDoubleClick` 纯判定，滚动条 / 行内按钮 / 表头空白都不算）。
- 入口：托盘菜单「采集历史…」（单实例，再点一次激活已开的窗口）。

## 12. 长截图（P3-2）

出口 = 动作栏「长截图」/ `Ctrl+L`。**覆盖层关掉之后、自己的窗口还原之前**才跑，所以收起的 LTools 窗口
不会被滚进画面，热键与低级鼠标钩子也还没恢复（自动滚屏不会撞上它们）。

- 拼接模型（`Features/CapturePro/ScrollStitch.cs`）：滚动是纯垂直平移，所以按**行**对齐——
  上一帧第 `r + shift` 行 = 这一帧第 `r` 行。候选取最小位移（比较行最多），
  两帧各自的顶部 15% / 底部 8% 不参与比对（固定页眉页脚），行内抽样 + 每通道 ±2 容差，
  允许 2% 的差异行（光标闪烁）；重叠少于 24 行、高度不一致、整段对不上 → 认不出（`null`，循环收尾）。
- 位移 0 = 画面没动：再加大幅度试一次，连着两次还是 0 才当「到底」。
  位移由滚动幅度自适应：超过帧高 92% 减小、低于 30% 加大（3 格起，1–8 格，一格 = 120）。
- 高度上限 `MaxRows = 12000` 行 / 96 MB（按行累加，超了就停），参数是「到高度上限」而不是失败。
- 循环（`Views/CapturePro/CaptureScrollWindow`，后台线程）：
  `滚一屏 → 抓帧到「连续两帧一样」→ 认重叠 → 把新露出来的行接到下面 → 刷预览`。
  抓帧走 `ScreenShot.Capture`（物理像素，与覆盖层同一套）；预览是**抽样缩小**，不从整张大图上再缩。
- 小面板：左上角标题、中间实时预览（越来越长的缩略图）、状态行「已拼接 N 帧 · 高 M px」、「完成」「取消」。
  落点优先选区**右侧** → 下方 → 上方 → 左侧（`Features/CapturePro/ScrollLayout.cs`，纯函数），
  都放不下才塞工作区右下角（这时抓帧前把自己 `Opacity = 0` 让开）。
  **不抢焦点**（`ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`）：一激活滚轮就打到自己身上了；
  代价是收不到 `Esc`，出口只有两个鼠标按钮。
- 滚动前把光标挪到选区中心、把那个窗口调到前台（滚轮只打给前台窗口）；**光标停在面板上 = 暂停**，
  不滚也不把光标从按钮上拽走，好让人点「完成」。
- 成品：`CaptureDocument.RenderOnto(stitched)` 把覆盖层里画的标注叠到长图顶部（没有标注就直接用长图），
  替换选区的图 → 复制到剪贴板 + toast；采集历史记一条 `Kind = shot`、`Source = 长截图`。
- 已知限制：子像素滚动（个别应用按 0.5 像素平移）会让整帧都「看起来不同」，这时认不出重叠、会提示重来一次。

## 13. 屏幕选字（P3-3）

入口 = 内置功能「屏幕选字」（`common.screenText`，屏幕分类）：放面板 / 绑快捷键 / 悬浮都行，没有动作单元。
一次会话 = 冻结整块桌面（与截图覆盖层同一套取景，盖住所有显示器）→ 拖一个框 → 识别 → 候选条。

- 覆盖层（`Views/CapturePro/CaptureTextSelectWindow`）：四块矩形压暗、选区那块留着原亮度 + 蓝框；
  候选条画在**同一个窗口**里（不用第二个窗口，`Esc` 始终有效），落点走 `ScrollLayout.PlaceIn`
  （右侧 → 下方 → 上方 → 左侧 → 夹回窗口右下角）。
- 识别：`OcrLayout.Read`（与「文字识别」共用同一个引擎偏好 `OcrEnginePreference.DefaultForNewProbe`），
  在后台线程跑；失败只写在状态行上，窗口不崩。
- 候选（`Features/CapturePro/ScreenTextCandidates`，纯函数）：按行 → 句末标点（标点留在句尾）→
  逗号类标点 → 长度上限（120 字）硬切；空白行丢掉，完全相同的候选只留第一条。
- 出口：点一条候选 = 复制那一条（`NativeClipboard.SetText` + toast + 关窗）；「复制全部」= 复制整段；
  整段文字另有一个只读框，可以自己拖选一部分按 `Ctrl+C`；「重新框选」换一块；`Esc` / 右键 / 关闭退出。
- 三道闸与截图一致：`TransientMinimize.MinimizeAll`、`MouseHook.SetWindowPickerActive`、
  `HotkeyRegistry.Suspended`，`finally` 成对还原；自动化指示器在抓帧前收起（否则会被截进冻结画面）。
- 已知限制：引擎识不出的字（艺术字 / 图标字体 / 低分辨率）就是识不出；屏幕选字**不进采集历史**
  （它只产出文字，没有成品图）。

## 14. 阶段路线

| 阶段 | 模块 | 内容 |
|---|---|---|
| **P0** ✅ | ①② | 覆盖层 + 12 个标注工具 + 工具栏/参数条 + 复制/保存/关闭 + 内置功能「截图」 |
| **P1** ✅ | ③⑨ | 贴图窗口（无激活 / 拖动 / 滚轮缩放 / 不透明度 / 置顶 / 锁定）+ 贴图组登记处 + 取色（第 13 个工具）。**局部显示与成组移动留后续** |
| **P2-1** ✅ | ④ | 识别结果窗（文字 / 二维码 / 本地近似表格）+ 覆盖层「识别」出口 |
| **P2-2** ✅ | ⑦ | 采集历史中心（`captures/` + 三页签 + 保留策略） |
| **P3-1** ✅ | — | 跨屏选区：覆盖层盖住所有显示器的并集，逐块抓帧拼一张冻结画面 |
| **P3-2** ✅ | ⑤ | 长截图（滚动触发 + 重叠检测 + 拼接 + 预览窗） |
| **P3-3** ✅ | ⑧ | 屏幕选字（拖选识别 + 候选条） |
| **P4** | ⑥ | 录屏（选区 / 主屏 / 全屏 + 倒计时 + 暂停继续 + MP4/GIF）+ 录屏历史 |

## 15. 验收

- `--selftest` 的 `capturePro` 组：标注几何（箭头端点、折线命中、序号递增、马赛克块计算、
  聚光排除几何）、撤销栈（含重做与上限）、导出尺寸 = 选区物理像素、工具 ↔ 参数条映射、工具栏布局项、
  取色（BGRA 读反 / 越界 / 十六进制与 rgb 文本）、贴图（缩放与不透明度步进 / 上限 / 光标锚点 / 出场落点）、
  识别（词框 → 表格的行 / 列聚类、中文逐字拼接、TSV / HTML、单行单列不算表格、引擎名字、位图转 GDI+）、
  采集历史（保留策略的数量 / 天数剪枝、缩略图长边与不放大、类型 / 尺寸 / 时间 / 摘要文案）、
  跨屏取景（显示器并集含负坐标 / 空表 / 空矩形；逐块抓帧的交集与贴边不算相交）、
  长截图（行位移检测：正常位移 / 小幅位移 / 没动 / 内容对不上 / 高度不一致 / 页脚不动；
  拼接只接新露的行、预览按比例缩、到高度上限自己停；面板落点右侧 → 下方 → 塞回工作区）、
  屏幕选字（按句末标点 / 逗号 / 长度上限切候选、空白与完全重复丢掉、切完字符总数不变、
  候选条落点右侧 → 下方 → 夹回窗口右下角）。
- 仓库外 `capture-overlay-smoke`：真窗口里框选 → 选矩形工具画一个 → 撤销 → 导出，断言导出的像素
  （按颜色找得到矩形的描边、画之前找不到）与尺寸，并验工具栏 13 个按钮 / 动作栏 6 个按钮（含贴图 / 识别 / 长截图）/
  文字工具开框提交 / 清选区后收起；
  取色色卡取到底图色块的颜色且不落标注、不关覆盖层；贴图按 100% 盖住选区、200% 与 400% 上限、
  锁定、右键菜单 10 项（含缩放 / 不透明度两个子菜单与「关闭全部贴图」）、`CloseAll` 清空贴图组；
  识别结果窗灌一份结果后：状态行 / 文字页 / 表格 2 列 2 行 / 二维码格式、切页签的显隐与复制按钮文案；
  采集历史：真写两条（截图 + 识别）后开窗，断言 catalog / 原图 / 缩略图落盘、列表 2 条、
  三页签过滤、搜索命中识别文字、预览大图与识别文字、复制按钮可用、双击判定、删除与清空后的空态；
  跨屏取景：枚举到的显示器并集 = 取景范围、真抓一帧尺寸对得上、局部抓帧是 40×30 且不是全透明、读得到缩放比；
  长截图：动作栏 6 个按钮（含「长截图」）、用合成帧驱动整套「滚一屏 → 抓帧 → 认重叠 → 拼接」到 200×900 且逐行
  对得上、取消时没有结果、面板视觉截图、面板落在选区右侧（`Left = 选区右 + 12`）；
  屏幕选字：灌一份识别结果后候选条 3 条 / 整段文字进只读框 / 条落在选区右侧并对齐顶边 / 视觉截图，
  点一条候选复制的是那一条、复制全部拿到整段、取消不算完成。
- 真机：三个入口（`screen.capture` 交互 / 快捷键 / 面板）都能唤出；`Esc` 逐级退出不卡；
  覆盖层期间按单键绑定不会触发别的动作；框选里输入的字母能正常用（不会被钩子吞）。
- 真机（P1）：点贴图不改变前台（紧接着的注入仍打在原程序上）；滚轮缩放时**光标下的内容不动**；
  `Ctrl`+滚轮改不透明度；锁定后拖不动也滚不动；双击 / ✕ / 右键菜单 / 「关闭全部贴图」四条出口都可用；
  取色复制到的 `#RRGGBB` 与色卡一致。
- 真机（P2-1）：三处「识别」入口都能开结果窗；窗口先出现「正在识别…」再填内容（不卡界面）；
  文字 / 表格 / 二维码三页可切、复制到剪贴板的内容与显示一致；截一段没有二维码的图只是「没找到」，不算失败。
- 真机（P2-2）：截图复制 / 贴图 / 另存为 / 识别之后托盘「采集历史…」都能看到那一条；识别文字在结果窗算完后
  出现在列表摘要里（不必手动刷新）；复制图片粘到聊天工具里是原分辨率；删除 / 清空后 `captures/` 里的图同时消失；
  重启 LTools 后历史还在（`captures/` 进备份）。
- 真机（P3-1，多屏才有意义）：框选能跨过屏幕交界，跨屏那一块内容不错位、不缩放；
  两块屏缩放比不同时，冻结画面与被截的桌面 1:1 对齐；贴图贴到副屏那块区域时位置正确；
  单屏机器上行为与之前完全一致（覆盖层只盖那一块屏）。
- 真机（P3-2）：截一个能滚的文本页 / 网页，选「长截图」后一路滚到底能拼出整页；
  到底自动停（连着两次画面没动就收尾，不用手点）；光标停在面板上时暂停滚动、能稳稳点「完成」；
  拼接过程中滚轮不会把光标从面板上拽走；成品复制到聊天工具里是一整张长图，底部没有重复的一截。
- 真机（P3-3）：把「屏幕选字」绑一个键，在浏览器 / 聊天窗口 / 图片上拖一块文字，
  候选条出现在选区外面、点一条就粘贴得出来；「复制全部」拿到整段；拖选只读框里的两行按 `Ctrl+C`
  只复制那两行；`Esc` / 右键能退出；拖到副屏或跨屏也能框。
- 构建 0 错 0 警。

## 16. 坑（实现时逐条对照）

1. **DPI**：窗口尺寸按 `物理 / scale` 给 WPF，但选区与标注存物理像素；只有 `ToDip`/`ToPhysical` 能乘除。
2. **钩子**：打开前 `SetWindowPickerActive(true)` + `HotkeyRegistry.Suspended = true`，`finally` 里成对还原；
   漏一处就是「框选时按一下 P 直接触发了别的动作」。
3. **自己的窗口**：`TransientMinimize.MinimizeAll` 之后要等一拍再抓帧，否则截到半隐藏的窗口。
4. **线程**：覆盖层是 WPF 窗口，必须 `Dispatcher.Invoke`；动作在后台 MTA 线程上跑，别直接 new 窗口。
5. **导出**：必须用标注文档重绘，不能用「屏幕截图再裁」（会把压暗层、工具栏、光标一起截进去）。
6. **文字**：`FormattedText` 要显式给 `pixelsPerDip`（用窗口的 `VisualTreeHelper.GetDpi`），否则高分屏上字发虚。
7. **鼠标捕获**：拖动结束后要 `ReleaseMouseCapture()`，且清理状态要放在释放**之前**（释放会同步派发挂起的
   `MouseMove`，与 `TileDragSort` 那个坑同源）。
8. **马赛克缓存**：不缓存会导致拖动时每帧重建 `CroppedBitmap`，大图上明显掉帧。
9. **工具栏高度**：切工具会让参数条显隐，量尺寸前必须 `UpdateLayout()`——只 `Measure` 拿到的是旧的
   `DesiredSize`（子树的 `IsMeasureValid` 还是 true），于是按矮的高度贴底、底部参数条被画布裁掉。
10. **贴图不抢焦点 = 收不到键盘**：`ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` 之后
   `Esc` 根本到不了窗口，**出口必须做成鼠标可达**（双击 / 悬停 ✕ / 右键菜单）。别照着模态窗的思路
   只留一个 `Esc`，那等于关不掉。同理，`Ctrl+滚轮` 能读到 `Keyboard.Modifiers` 是因为那是**全局键状态**，
   不是窗口收到了键盘消息——别以为它能收到 `KeyDown`。
11. **贴图右键菜单要在按下时装好**：`ContextMenuOpening` 触发时 WPF 的菜单服务已经取过菜单，
    在那里现赋第一次右键只会存下菜单、不弹出（与步骤列表 `ContextMenu` 同一个坑）。
    在 `PreviewMouseRightButtonDown` 里 `ContextMenu = BuildMenu()`，每次现建。
12. **`Topmost` 改了要主动 `RaiseToTop`**：贴图与超级面板都在 `Topmost` 层，谁最后 `SetWindowPos` 谁在前；
    面板显示 / 激活会把贴图盖住。所以切 `Topmost`、点贴图、以及面板显示后都要
    `SetWindowPos(HWND_TOPMOST, SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` 顶回去（不抢焦点）。
    `Topmost=false` 时用 `HWND_TOP`（`IntPtr.Zero`，`Win32` 里没有 `HwndTop` 常量）。
13. **贴图出场位置用 Win32 钉一次**：混合 DPI 下 WPF 的 DIP → 物理像素换算可能差几个像素，
    贴图要「正好盖住刚才截的那块」，所以 `SourceInitialized` 里再 `SetWindowPos` 一次（物理像素，
    宽高 = DIP × 该屏缩放），与覆盖层同一套坐标系。
14. **页签的 `Checked` 会在 XAML 解析期先触发一次**：`IsChecked="True"` 是先于后面的兄弟控件写进属性系统的，
    在事件里直接碰 `TextView` / `TablePage` / `CopyBtn` 会 `NullReferenceException`（实测在建窗口时就炸）。
    处理函数开头判一下（`TextView == null` 直接 return），初始态交给 XAML 自己的默认值。
15. **识别结果窗要先开再算**：OCR 一两秒，同步跑会把 UI 顶住（低级鼠标钩子与 WPF 共用 UI 线程，
    鼠标会卡住）；窗口 `Loaded` 里 `Task.Run` 再回填，失败也只落在状态行。
    也别在窗口构造里就开算——`Loaded` 之前窗口还没量好尺寸，进度提示根本看不见。
16. **冒烟里开结果窗要先合并 `Themes/Modern.xaml`**：覆盖层 / 贴图窗全是内联样式，冒烟工程的空 `Application`
    也能建；结果窗用的是 `ModernWindow` + 主题笔刷，不合并资源字典会报「找不到 ModernWindow 资源」。
17. **列表选中高亮要画在卡片上**：采集历史的行卡片是不透明白底，会把 `ListBoxItem` 的选中底色完全盖住；
    照 `SubActionLibraryView` 的做法，`ItemContainerStyle` 换成透明 `ContentPresenter`，
    高亮由卡片模板的 `DataTrigger`（祖先 `ListBoxItem.IsMouseOver` / `IsSelected`）负责。
18. **采集历史的识别文字是异步回填的**：窗口要订阅 `CaptureCatalog.Changed`，回调里先
    `Dispatcher.CheckAccess()`（识别在后台线程算完回 UI 线程写条目）；识别失败 / 用户提前关掉结果窗
    也要写一次 `SetText`，否则那条永远停在「识别中…」（读盘时另有十分钟兜底）。
    图片落盘同理走后台，列表靠 `Changed` 再刷一次缩略图。
19. **冒烟的采集历史落在冒烟工程自己的 bin**：`AppPaths.DataRoot` 按 `AppContext.BaseDirectory` 算，
    冒烟没有 `LTools.csproj` 就不是 dev 布局，`captures/` 写在冒烟目录里——所以断言数量前先 `Clear()`，
    跑完再清一次，别让上一次的记录影响这一次。
20. **跨屏抓帧要逐块显示器做**：一次 `BitBlt` / `CopyFromScreen` 跨过 DPI 不同的两块屏，
    副屏那半可能拿到被系统缩放过的内容（框里的字变大变虚）。`ScreenShot.Capture` 每块只抓与取景范围相交的
    那一块，再按物理坐标贴回一张 Bgra32 位图；单屏时直接返回那一块（不复制，省一次大图搬运）。
21. **跨屏时窗口的 DPI 只有出来之后才知道**：窗口横跨两块屏、系统按它落在哪块给 DPI，
    构造时拿光标下那块屏的比例只是「初值」。`Loaded` 里先 `SetWindowPos` 按物理像素钉死，
    再按 `ActualWidth / 像素宽` 校准 `CaptureDocument.Scale`——漏了这一步，底图会按错的比例铺开，
    右边 / 下边露出一条窗口底色。**导出不受影响**：`Render()` 的像素尺寸只由选区算。
22. **贴图的缩放比不能沿用覆盖层的**：跨屏选区可能一半在主屏（125%）、一半在副屏（100%），
    贴图落位要按**这块区域所在显示器**算（`ScreenShot.ScaleOf`），否则贴到副屏上会整体偏一截。
23. **长截图必须两侧都跳过页脚**：固定页脚滚动时不动，只跳过「这一帧的底部」不够——
    对齐之后上一帧的页脚落在 `shift` 之后，也会被拿来比。比较区间是 `[top, height - footer - shift)`，
    两侧都避开；只避开一侧时，页脚越厚越容易把对齐拽到错误的位移上（自检里那条「页脚不动也能认出位移」
    就是守这个）。
24. **长截图的预览要抽样缩小，不能从大图上再缩**：拼接图到上限时上百 MB，每帧 `Render()` 一次整图再
    `TransformedBitmap` 会一边拼一边卡。`ScrollStitch.Preview` 直接按步长抽行抽列造小图，成本与预览尺寸成正比。
25. **长截图面板不抢焦点 = 收不到 `Esc`**：与贴图同一个坑，出口只有「完成」「取消」两个鼠标按钮；
    而且滚动前要把光标从面板上让开（`PanelContainsCursor` 时暂停），否则会自动把光标从按钮上拽到选区中心。
26. **非模态窗口的 `DialogResult` 会抛**：冒烟里 `ShowDialog` 没走过，赋 `DialogResult` 会
    `InvalidOperationException`；`CloseOnUi` 里兜一层 `Close()`，别让收尾抛在后台线程上。
27. **屏幕选字的尺寸要取内容而不是 `Window.ActualWidth`**：窗口还没 `Show`（冒烟直接排版内容）时
    `Window.ActualWidth/Height` 是 0，压暗矩形与候选条会按 0 算，`Canvas.Left` 落到 0。
    `CaptureTextSelectWindow` 的 `ViewWidth/ViewHeight` 先读 `Root.ActualWidth`，窗口真显示时两者一致。
28. **候选条画在覆盖层窗口里，别另开第二个窗口**：`Esc` 只需要挂一次（窗口 PreviewKeyDown），
    点候选条也不会被当成新框选（`IsOnBar` 沿视觉树往上找 `ResultBar`）；第二个窗口要么抢焦点、
    要么得自己同步关闭，反而更容易点不中。

## 17. 实施记录

- **P3-3 屏幕选字**（`Features/CapturePro/ScreenTextCandidates.cs`、`ScreenTextSession.cs`；
  `Views/CapturePro/CaptureTextSelectWindow.xaml(.cs)`；入口是内置功能 `common.screenText`）：
  冻结整块桌面（与截图覆盖层同一套取景）→ 拖框 → `OcrLayout` 在后台识别 → 候选条画在同一个窗口里
  （`ScrollLayout.PlaceIn` 落点：右侧 → 下方 → 上方 → 左侧 → 夹回右下角）。
  候选按行 → 句末标点 → 逗号 → 长度上限切分，空白与完全重复丢掉；点候选 / 复制全部走
  `NativeClipboard.SetText` + toast；整段文字另有只读框可手动拖选。
  自检补「选字：候选切分」与候选条落点；冒烟灌一份识别结果验候选条 / 落点 / 两条复制出口 / 取消。
- **P3-2 长截图**（`Features/CapturePro/ScrollStitch.cs`、`ScrollLayout.cs`；
  `Views/CapturePro/CaptureScrollWindow.xaml(.cs)`；接线在 `CaptureSession`、`CaptureOverlayWindow`、
  `CaptureDocument.RenderOnto`）：按**行**对齐相邻两帧（`FindShift` 位移搜索，两侧页脚跳过、抽样 + ±2 容差、
  允许 2% 差异行、重叠 ≥ 24 行），只把新露出来的行接到下面；位移 0 连着两次 = 到底，
  幅度按位移占帧高的比例自适应（1–8 格）；上限 12000 行 / 96 MB。
  面板是一块不抢焦点的小浮层（预览 + 状态行 + 完成 / 取消），落点优先选区右侧（`ScrollLayout.Place` 纯函数），
  光标停在面板上时暂停滚动。循环在后台线程、抓帧等「连续两帧一样」、预览抽样缩小。
  成品把覆盖层里的标注经 `RenderOnto` 叠到长图顶部，复制到剪贴板 + toast，采集历史记 `Source = 长截图`。
  自检补「长截图：行位移检测」「长截图：拼接与面板落点」（合成帧，不碰桌面）；
  冒烟注入 `FrameSource` / `ScrollBy` 驱动整轮拼接到 200×900 且逐行对得上，并导出面板视觉图。
- **P3-1 跨屏选区**（`Features/CapturePro/ScreenShot.cs`；接线在 `CaptureSession`、
  `CaptureOverlayWindow`、`CaptureDocument.SetScale`、`CapturePinManager`）：覆盖层盖住所有显示器的并集，
  冻结画面逐块显示器抓帧再拼（`Union` / `Intersect` 纯函数，自检直接断言）；
  吸附线带上每块显示器的四边；窗口按物理像素钉死后再校准一次 DIP 比例；
  贴图按区域所在显示器算缩放比。`ScreenPickerWindow`（模板图 / 取点 / 取色）仍按光标那一块屏，没动。
  自检补「跨屏：取景并集 / 交集」；冒烟补显示器枚举 / 取景尺寸 / 局部抓帧不是全透明 / 读得到缩放比。
- **P2-2 采集历史**（`Features/CapturePro/CaptureCatalog.cs`；
  `Views/CapturePro/CaptureHistoryWindow.xaml(.cs)`；接线在 `CaptureSession`、`CaptureResultWindow`、
  `App.xaml.cs` 托盘、`BackupService.BuildPlan`）：`captures/catalog.json` + `assets/<id>.png` +
  `thumbnails/<id>.png`，`Kind` = `shot` / `recognize`，`Source` 记完成动作。
  `Create` 先写元数据（很快），图片由 `Task.Run(SaveAssets)` 落盘；识别条目 `State = pending`，
  结果窗算完后 `SetText` 回填（关掉窗口也回填）。保留策略 200 条 / 30 天，`PlanPrune` 纯函数，
  启动时后台 `Maintain()`。
  窗口三页签 + 150ms 防抖搜索 + 左列表（缩略图 / 时间 / 尺寸 / 摘要）+ 右预览 + 识别文字只读框，
  操作复制图片 / 另存为 / 复制识别文字 / 删除 / 清空 / 打开目录；双击整行复制图片。
  页签样式从识别结果窗提到主题 `CaptureTab`，两个窗口共用。
  自检补「采集历史：剪枝 / 缩略图 / 文案」；冒烟补真写两条后的列表 / 页签 / 搜索 / 预览 / 双击判定 / 删除 / 清空。
- **P2-1 识别**（`Features/CapturePro/CaptureRecognize.cs`、`OcrTable.cs`；
  `Views/CapturePro/CaptureResultWindow.xaml(.cs)`；出口在 `CaptureOverlayWindow` 的
  `FinishKind.Recognize` / `RecognizeRequested` 与 `CaptureSession`）：动作栏「识别」/ 工具栏扫描框 /
  `Ctrl+R` 同一个出口——重绘成品 → 关覆盖层 → 回 UI 线程开结果窗，识别在窗口里后台跑。
  结果窗三页（文字 / 表格 / 二维码）各有复制：文字整段、表格 TSV 与 HTML、二维码内容；
  状态行写「识别完成 · N 行 · 表格 M 行 × K 列 · 找到二维码」与「引擎 · 用时」。
  引擎取「上次选用的 OCR 引擎」（与「文字识别」取值同一套），底层直接复用 `OcrLayout.Read` 与
  `QrCodeAlgorithms.Read`，**没有再写一份识别**。
  近似表格 `OcrTable.Build` 是纯函数：词框按纵向重叠聚行、按横向间隙聚列（容差按中位字高算），
  同一格里相邻词框按间距决定拼不拼空格（中文逐字框贴在一起），2 行 2 列以上才当表格；
  没有词框的行退化成整行一格。HTML 输出会转义 `& < > "`。
  自检补「识别：表格聚类 / TSV / HTML」；冒烟补动作栏 5 个按钮与结果窗三页（灌一份结果，不跑真 OCR）。

- **P1 贴图**（`Features/CapturePro/PinMath.cs`、`CapturePinManager.cs`、`CaptureSave.cs`；
  `Views/CapturePro/CapturePinWindow.xaml(.cs)`；出口在 `CaptureOverlayWindow.xaml.cs` 的
  `FinishKind.Pin` / `PinRequested` 与 `CaptureSession`）：覆盖层的「贴图」按钮 / 工具栏图钉 /
  `Ctrl+T` 三处同一个出口——先按标注文档重绘成品，再关掉覆盖层，由 `CaptureSession` 在关窗之后
  `CapturePinManager.Pin(image, area, scale)` 建窗口（不在这里建，免得刚建好就被 `ShowDialog` 的还原动作拖走）。
  作为 `screen.capture` 的一步时**贴图与返回图并存**：贴一份到屏幕上，同时照常给 `found` / `file`。
  窗口不激活（`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`）、可拖动（至少留 40 DIP 在虚拟桌面里）、
  滚轮缩放 10%–400%（锚在光标下，`PinMath.AnchoredOrigin`）、`Ctrl`/`Shift`+滚轮改不透明度 20%–100%、
  双击关闭、悬停 ✕、右键菜单（复制图片 / 另存为 / 缩放 / 不透明度 / 置顶 / 锁定位置 / 关闭 / 关闭全部贴图）。
  **贴图不改剪贴板**（只有「复制图片」才写），所以「贴一下看看」不会把用户刚复制的东西冲掉。
  贴图组只有内存登记处（`Count` / `All` / `CloseAll` / `RaiseAll`），**不进配置、重启不恢复**；
  「局部显示」与「成组一起移动」明确留到后续轮次。
  「另存为 png」抽成 `CaptureSave.TrySave`，覆盖层的保存与贴图的右键菜单共用一份。
- **P1 取色**（`Features/CapturePro/PickerColor.cs`、`Views/CapturePro/CaptureOverlayWindow.Picker.cs`）：
  第 13 个工具（`CaptureTool.Picker`，字母 `C`），读**冻结底图**而不是成品（标注与压暗不参与），
  也不要求先框选。移动时光标旁跟一块色卡（色块 + `#RRGGBB · rgb(r, g, b)`，贴边翻转），
  单击复制 `#RRGGBB` 并给 1600ms toast，**不关覆盖层**（连着取几个是常态）、不落标注；
  切回选择工具时色卡收起。越界只是「这一下没取到」，不报错。
  `Ctrl+C` 仍是「复制截图」，`C` 是取色——判断顺序先判 Ctrl。
- **P0 标注工具 + 浮动工具栏**（`Features/CapturePro/ToolCatalog.cs`；`Views/CapturePro/CaptureIcons.xaml`、
  `CaptureToolbar.xaml(.cs)`；`Views/CapturePro/CaptureOverlayWindow.Tools.cs` / `.Annotate.cs` / `.Text.cs`）：
  12 个工具（选择 / 画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 折线 / 文字 / 序号 / 马赛克 / 聚光 / 橡皮）
  全部由 `ToolCatalog.All` 一张表驱动——工具栏按它生成按钮、参数条按 `Uses(tool, param)` 显隐、
  字母快捷键也读同表的 `Shortcut`，**新增工具只改这张表 + 一个绘制分支**。
  工具只决定「按下鼠标会发生什么」：拖一类在手势里画预览、松手才 `Add`（点一下不留 0 像素标注，
  `MinimumSize` 判）；点一类（序号 / 文字 / 折线）立即落；橡皮按命中删整条。
  拖动已有标注用 `Surface.SkipIndex` 藏原件 + `Preview` 代画，松手才 `ReplaceAt`，撤销栈里不留中间帧。
  选中的标注画一圈蓝虚线（`CaptureCanvas.SelectedIndex`，不参与导出），`Delete` / `Backspace` 删它。
- **文字工具**走覆盖层内的 `TextEntry`（不是弹窗）：`Enter` 提交、`Shift+Enter` 换行、`Esc` / 右键放弃、
  失焦即提交；提交才写 `Mark.Text`（字号 / 颜色取当前样式，外描边取「与字色相反」保证浅底深底都立得住）。
  输入期间窗口的 `PreviewKeyDown` 一律早退（否则方向键会去改选区、字母会去切工具）。
- **快捷键**：`Ctrl+C` / `Ctrl+S`（复制 / 另存为）、`Ctrl+Z` / `Ctrl+Y`、`Ctrl+A`、方向键微调，
  以及 `Q P H R E A L T N M S X` 选工具（先判 Ctrl，所以 `S` 是聚光、`Ctrl+S` 是另存为）。
  `Esc` 与右键**共用一套逐级退出**（折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 关闭）。
- **工具栏落点**：默认贴屏幕底部居中；用户拖过就按用户的位置（`UserMoved`），只在贴边时收回可见区。
  切工具会改高度（「只有一排」↔「再加参数条」），所以每次 `UpdateChrome` 都重算一次落点——
  量尺寸必须先 `UpdateLayout()`：参数条刚切显隐时子树的 `IsMeasureValid` 还是 true，
  直接 `Measure` 会拿到「只有一排」的旧 `DesiredSize`，于是按矮的高度贴底、底部参数条被画布裁掉。
- **P0 覆盖层骨架**（`Features/CapturePro/CaptureSession.cs`、`CaptureCanvas.cs`；
  `Views/CapturePro/CaptureOverlayWindow.*.cs`）：冻结抓帧 → 全屏窗（`WindowSizing.Mode=Overlay`）→
  框选（吸附 / 把手 / 内部移动 / 方向键 / Shift 锁正方形 / Ctrl 关吸附 / Ctrl+A 整屏）→
  尺寸条按物理像素 → `Enter` / 双击 / 「✓ 复制」= 复制并关闭，「另存为…」= 存 png，`Esc` / 右键逐级退出。
  打开前 `TransientMinimize` + `MouseHook.SetWindowPickerActive(true)` + `HotkeyRegistry.Suspended`，
  `finally` 成对还原；导出仍走 `CaptureDocument.Render`（按选区重绘）。
- **入口已接**：`screen.capture` 的交互模式与内置功能「截图」（范围=框选）都开这个覆盖层；
  `common.capture` 的整屏 / 前台窗口 / 固定区域三条不经过覆盖层（它们是纯取图）。
- **冒烟**：仓库外 `%TEMP%\opencode\capture-overlay-smoke`（假冻结画面 + 真窗口）：
  窗口按物理像素盖住显示器、选区后动作栏 / 尺寸条 / 八个把手出现、
  工具栏按目录生成 13 个按钮、切到矩形画一条 → 导出里按颜色找得到它（画之前找不到作对照）、
  撤销 / 重做、文字工具开框 / 提交 / 空白不落盘、清掉选区后工具栏收起；
  取色色卡取到底图色块的颜色（`#E15B4E`）、取色不落标注、切回选择收起色卡；
  贴图按 100% 盖住选区（物理 ÷ 缩放）、200% / 400% 上限、锁定、右键菜单 10 项与两个子菜单、
  `CloseAll` 后窗口消失且登记处清空；
  渲染 `out/overlay.png`、`out/overlay-picker.png`、`out/pin.png`、`out/export-annotated.png` 供人眼对照。
