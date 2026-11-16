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

- 一个**截图覆盖层**：冻结屏幕 → 拖框（可吸附窗口/控件，可再微调）→ 在图上**标注** → 复制 / 保存 / 关闭。
- 一套**浮动工具栏**：主栏 +「更多」溢出 + 选中工具后的**工具参数条**，可拖动位置、双击复位。
- 覆盖层同时服务三个入口：`screen.capture` 的交互模式、内置功能 `ltools.capture`（快捷键 / 面板 / 悬浮）、
  以后的贴图与识别都从同一个标注文档取图。

**不做什么（整体明确排除）**

- 一切依赖云的能力：在线 OCR、Mathpix 公式、百度/有道图片翻译、AI 改图、发送到工作台。
- 用户自定义工具栏（布局 JSON / JS 脚本 / 自定义按钮）：需求方未勾选，工具栏是固定的一套。
- 快速截图（按住鼠标键 + 引导键直接截图）与第三方截图工具桥接。
- 跨屏选区：P0 覆盖层只盖**光标所在显示器**，跨屏与长截图一起做（P3）。

## 1. 已确认的决策

| # | 决策 | 结论 |
|---|---|---|
| 1 | 入口 | **升级现有 `screen.capture` 的交互框选**，不新增动作单元；`foreground` / `screen` / `rect` 三种非交互模式行为不变 |
| 2 | 快捷键入口 | 新增内置功能 `ltools.capture`（`FeatureCatalog` 一条），播种进「CapsLock 经典」组（`SeedVersion` 4） |
| 3 | UI 风格 | 借布局换风格：结构与 Quicker 一致，配色 / 图标用 LTools 主题；图标自绘几何，不新增未验证的 Segoe 码点 |
| 4 | 完成动作 | P0：复制到剪贴板（默认）/ 另存为 / 关闭；先不加配置域字段 |
| 5 | 出图 | 显示与导出**共用一条渲染路径**（`MarkRenderer`），不用「截屏再裁」 |
| 6 | 标注的可编辑性 | P0：选中（改为当前工具）/ 拖动移动 / `Delete` 删除 / 文字双击改字；**缩放手柄放 P1** |
| 7 | 橡皮 | 整笔删除（删掉整个标注元素），不做像素级擦除 |
| 8 | 贴图 | P1（本次只留出口，不实现贴图窗口） |
| 9 | 识别 | P2：文字 + 二维码 + 本地近似表格（PP-OCRv6 词框聚类出 TSV/HTML），**不做公式与翻译** |
| 10 | 录屏 | P4，随包 ffmpeg（与 AutoHotkey 同一套：`runtime/` + `tools/fetch-*.ps1` + `licenses/`） |
| 11 | 采集历史 | P2，新数据根 `captures/`（`catalog.json` + `assets/` + `thumbnails/`），进备份域白名单 |

## 2. 覆盖层结构

### 2.1 窗口与坐标系

- `Views/CapturePro/CaptureOverlayWindow`：无边框、`Topmost`、`ShowInTaskbar=False`、
  `WindowSizing.Mode="Overlay"`（完全不干预尺寸与位置）。
- 盖住**光标所在显示器**：`Left/Top/Width/Height` = 显示器物理矩形 ÷ 该屏 DPI 缩放
  （与 `ScreenPickerWindow` 完全同一套，别另写一份）。
- **选区与标注几何一律用「物理像素」**（与截图 / 找图 / 鼠标注入同一坐标系）；
  只有绘制和 WPF 命中测试才换成 DIP。换算只在 `ToDip` / `ToPhysical` 两个方法里，别处不许乘除 DPI。
- 输出：`Area` = 屏幕绝对物理像素矩形（`x/y/w/h`），`file` = 成品 png 路径。
- 窗口打开前：`TransientMinimize.MinimizeAll(owner)`（把自己的窗口收起来）→ `Thread.Sleep(SettleMs)` →
  `ScreenCapture.Capture(monitor)` 抓帧。关闭时 `finally` 还原窗口。
- 打开期间：`MouseHook.SetWindowPickerActive(true)`（让低级鼠标钩子让路）、
  `HotkeyRegistry.Suspended = true`（单键绑定不许吞掉框选里的按键）。

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

### 2.3 状态机

```
Idle(未框选) --拖动--> Selecting --松手--> Selected
Selected --点击工具--> Drawing(可连续画)
Selected/Drawing --Esc--> Selected(取消工具) --Esc--> Idle --Esc--> 关闭
Selected --Enter/双击/✓--> 完成（复制）
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

规则：

- 工具栏**可拖动**（按住空白处拖，位置只记在本次会话），**双击复位**到「贴选区下方居中」。
- 颜色与线宽等样式**按工具各自记住**（切回来还是上次的值），与 Quicker 的「每种工具独立继承」一致。
- 工具栏与参数条都不许挡住选区中心 40%：放不下时依次试「下方 → 上方 → 选区内侧底部」。

**P0 落地（2026-11-16）与本节的差别**（先跑通，视觉细化留给后续阶段）：

- 12 个工具**全部常驻主栏**（没有 `⋯` 溢层，放不下就由 `WrapPanel` 折行）；按钮由 `ToolCatalog.All` 生成。
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
- 选区外的按下永远重新框选（任何工具下都能改框），八个手柄在任何工具下都能用。

**键盘**

| 键 | 作用 |
|---|---|
| `Esc` / 右键 | 逐级退出：折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 关闭 |
| `Enter` | 完成（复制）；折线进行中 = 收尾；文字输入中 = 提交（`Shift+Enter` 换行） |
| `Ctrl+C` / `Ctrl+S` | 复制 / 另存为 |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `Ctrl+A` | 选整个屏幕 |
| `Delete` / `Backspace` | 删除选中的标注 |
| 方向键（`Shift` 加速） | 微调选区 1px / 10px |
| `Q P H R E A L T N M S X` | 选择 / 画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 折线 / 文字 / 序号 / 马赛克 / 聚光 / 橡皮（打字时全部让给输入框） |

## 7. 完成动作

| 出口 | 行为 |
|---|---|
| 复制（默认，`Enter` / 双击 / ✓） | 导出 png → `NativeClipboard.SetImage`（同时写 `PNG` 与 `CF_DIB`）→ 关闭 → toast「已复制」 |
| 保存 | 另存为对话框（默认目录「图片\LTools 截图」，文件名 `截图_yyyyMMdd_HHmmss.png`）→ 关闭 |
| 关闭 / `Esc` | 丢弃，什么都不做 |

作为 `screen.capture` 的一步时，「完成」= 返回这张图（`file` 落 `ImageLibrary.SaveCapture`）；
「取消」= `found = false`，**不报错**（与现有截图步骤同一口径）。

## 8. 接入点

| 位置 | 改动 |
|---|---|
| `Features/Flow/ScreenshotProbe.cs` | `interactive` 分支改调 `CaptureSession.TryCapture`（在 UI 线程 `Dispatcher.Invoke` 里开窗），其余三种模式不动；取消仍是 `found = false` |
| `Features/Units/Screen/CaptureScreenUnit.cs` | 不动（参数与输出都不变；交互模式只是「取图方式」的一种） |
| `Features/Builtin/FeatureCatalog.cs` | 「截图」功能（`common.capture`）的范围=框选改走同一个覆盖层；**不新增 `ltools.capture`**：功能 Id 是唯一身份，同一件事不挂两个 Id |
| `Features/HotkeySeeding.cs` | `SeedVersion` 3 → 4，把「截图」加进种子（避开已占用的组合键） |
| `Features/Flow/ImageLibrary.cs` | 不改；成品仍走 `SaveCapture`（`cache/screenshots/`，下次运行清空） |
| 数据根 | P0 **不新增**目录；`captures/` 随 P2 的采集历史一起加 |

## 9. 阶段路线

| 阶段 | 模块 | 内容 |
|---|---|---|
| **P0** | ①② | 覆盖层 + 12 个标注工具 + 工具栏/参数条 + 复制/保存/关闭 + `ltools.capture` |
| **P1** | ③⑨ | 贴图窗口（无激活 / 缩放 / 不透明度 / 置顶 / 锁定 / 局部显示）+ 贴图组 + 取色接进工具栏 |
| **P2** | ④⑦ | 识别结果窗（文字 / 二维码 / 本地近似表格）+ 采集历史中心三页签 + 保留策略 |
| **P3** | ⑤⑧ | 长截图（滚动拼接）+ 屏幕选字（拖选识别 + 候选条）+ 跨屏选区 |
| **P4** | ⑥ | 录屏（选区 / 主屏 / 全屏 + 倒计时 + 暂停继续 + MP4/GIF）+ 录屏历史 |

## 10. 验收（P0）

- `--selftest` 的 `capturePro` 组：标注几何（箭头端点、折线命中、序号递增、马赛克块计算、
  聚光排除几何）、撤销栈（含重做与上限）、导出尺寸 = 选区物理像素、工具 ↔ 参数条映射、工具栏布局项。
- 仓库外 `capture-overlay-smoke`：真窗口里框选 → 选矩形工具画一个 → 撤销 → 导出，断言导出的像素
  （按颜色找得到矩形的描边、画之前找不到）与尺寸，并验工具栏按钮数 / 文字工具开框提交 / 清选区后收起。
- 真机：三个入口（`screen.capture` 交互 / 快捷键 / 面板）都能唤出；`Esc` 逐级退出不卡；
  覆盖层期间按单键绑定不会触发别的动作；框选里输入的字母能正常用（不会被钩子吞）。
- 构建 0 错 0 警。

## 11. 坑（实现时逐条对照）

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

## 12. 实施记录

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
  工具栏按目录生成 12 个按钮、切到矩形画一条 → 导出里按颜色找得到它（画之前找不到作对照）、
  撤销 / 重做、文字工具开框 / 提交 / 空白不落盘、清掉选区后工具栏收起；
  渲染 `out/overlay.png`、`out/export-annotated.png` 供人眼对照。
