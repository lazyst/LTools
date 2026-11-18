# 截图 Pro × Snow Shot 复刻对照计划

上游基准：[mg-chao/snow-shot](https://github.com/mg-chao/snow-shot)（本机 `C:\main\APPS\_refs\snow-shot`，commit `637f8ca`）。
本文是**唯一进度账本**：每个模块一行，标出上游实现位置、LTools 现状、所属批次与状态。
批次完成即改状态并写「验收」记录；跨批次的临时决定写进 §6 变更记录。

## 1. 判定口径

- **1:1 复刻** = 行为（同样输入得到同样的像素 / 文本 / 文件）、交互（手势、快捷键、悬停、拖动、取消路径）、界面（布局、度量、配色、图标）三者都与上游一致；平台差异（Windows 无 macOS 权限引导）只在 §6 记差异，不降级。
- 每批次必须同时满足：构建 0 错 0 警 → `LTools.exe --selftest` 全绿 → 仓库外冒烟（`capture-overlay-smoke` / `record-e2e` 或新 harness）全绿 → 真机点一遍相关路径。
- 纯逻辑（几何 / 命中 / 序列化 / 样式回落）进 `--selftest` 的 `capturePro` 组；需要真窗口 / 真鼠标 / 真编码器的进仓库外 harness。
- 状态：`✅` 已 1:1；`🟡` 有但不等价（缺参数 / 缺交互 / 度量不同）；`❌` 完全没有；`⏳` 本批次进行中。

## 2. 模块对照表

### 2.1 画布与工具（P1 / P2）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 工具清单（32 项） | `include/.../screenshotcanvastools.inc`、`src/.../tools/screenshottoolpalette.cpp` | `Features/CapturePro/ToolCatalog.cs` **25 项**（矩形与椭圆合并成上游那样一个「形状」；余下是 P4 的识别 / 转换类：OCR / 翻译 / 文档 / 表格 / 二维码 / LaTeX / Markdown / HTML） | P1 / P2 / P4 | 🟡 |
| 移动工具 Move（拖整个选区窗口） | `screenshottoolpalette.cpp:5210` | 拖动选区 / 空白拖动窗格 | — | ✅ |
| 选择 Select（选中标注改样式 / 拖动 / 缩放） | 同上 | `CaptureOverlayWindow.Annotate.cs` + `Chrome.cs` | — | ✅ |
| 气泡 SpeechBubble（尾巴 / 思考气泡） | `speech_bubble/*` | `Mark.SpeechBubble`：身体轮廓取自形状族（矩形 / 椭圆 / 菱形 / 思考气泡云朵），尾巴根部由身体轮廓二分求出、身体边线不横穿尾巴；思考气泡是一串由大到小的圆点 | P1 / P2-b | ✅ |
| 形状 Shape（矩形 / 椭圆 / 圆角矩形 / 菱形…） | `SnowCanvasShapeKind`、`snow_canvas_render_geometry.cpp` 的 `roundedDiamondPath` | `CaptureTool.Shape`（`R`）+ `ShapeInk.Contour`：矩形（圆角上限 = 短边 / 2）/ 椭圆 / 圆角菱形（上限 = 短边 / 4）/ 思考气泡云朵（14 段贝塞尔） | P2-b | ✅ |
| 测距 Distance（单位 / 小数位 / 端点 / 实测值换算） | `buildDistanceFamily` `screenshottoolpalettestylecontrols.cpp:2904`、`distance.rs`、`arrow/render_core.rs` | `Mark.Distance`：主线 + **14 种端点**（`ArrowInk`，尺寸 / 张角 / 圆直径 / 比例全部照 `render_core.rs`）+ 中点纯文字标签（`值 单位`，字号 `20×√(线宽/2)`，与上游注释文字同为无背景、描边色）；工具栏 7 格 = 色 / 线宽 / **实测值**（写回 `系数 = 值 ÷ 实测长度`，钳 0.01–1000，没选中时置灰）/ 9 种单位 / 0–3 位小数 / 端点比例 0.5–3 / 端点样式 | P1 / P2-d1 | ✅ |
| 测角 Angle（滚轮调角度 / 内角 / 弧度） | `buildAngleFamily` `:2711`、`angle.rs` | `Mark.Angle`（两次拖拽成角，滚轮 ±5° / Shift ±1°）+ 类型有向 360° / **内角 180°**（`>180°` 折成 `2π−sweep`，圆弧与平分线一起翻边）+ 单位度 / 弧度（`°` 无空格、`" rad"` 前导空格）+ 0–3 位小数 | P1 / P2-d1 | ✅ |
| 括号 Bracket | `buildBracketFamily` `:2633`、`bracket.rs` | `Mark.Bracket` = **花括号**（三点 a/c/b + 四段三次贝塞尔 `BracketInk`）；拖拽 = 首尾落在起点 x 上纵向撑满、尖在终点 x 与中线，**横向拖拽退化为不生成**（与上游同） | P1 / P2-d1 | ✅ |
| 箭头 Arrow（类型 / 起止端点 / 杆型 / 外描边 / 比例） | `buildArrowFamily` `screenshottoolpalettestylecontrols.cpp:1926`、`arrow/data.rs`、`render_core.rs`、`shaft.rs` | `Mark.Arrow` = 点列 + `ArrowSpec`：**三种类型**（直线 / 曲线 = Catmull-Rom（tension 1，端点自重复）/ 折线 = 拐点 16 DIP 圆角）、**起止端点 14 种**（`ArrowInk`，方向按曲线 t=0.85 切线 / 末段弦）、**箭头比例 0.5–3**、**杆型**（等宽 / 渐宽 = ribbon 半径从次箭头肩宽/4 线性长到主箭头肩宽/4，实线连成一片、虚点线分段）、**外描边**（宽 0–72 + 色 `#FFCCC7`，垫在墨迹下面）；工具栏 11 格 = 色 / 线宽 / 线型 / 笔触 / 类型 / 外描边宽 / 外描边色 / 比例 / 起点 / 杆型 / 终点 | P2-e1 | ✅ |
| 选中标注后切到该工具的样式族 + 样式实时生效 | 各 `*Family` 的 selection 分支、`style.rs` 的 `style_toolbar_source` / `set_shape_style_patch` | `MarkStyler`（标注 ↔ 样式族）+ `ToolStyle.CopyChangedTo`：选中一条标注 → 参数行切到**它自己的样式族**（工具按钮仍停在当前工具）并显示**这一条的值**；改参数 = 整份写回这条标注 + **只把变过的字段**合并进该族的创建样式（看一条旧箭头不会改掉创建默认）；取消选中 / 换工具 = 参数行交还当前工具；18 种标注全覆盖（水印 / 恢复块不归族） | P2-e2 | ✅ |
| 多选标注 + 混合值显示（框选 / Shift 点选，参数行显示「多个值」） | `selection.rs` 的 `toggle_selection` / `marquee_candidate_elements`、`style.rs` 的 `style_toolbar_source`、`screenshottoolpalettestylecontrols.cpp` 的 `Mixed` 分支 | 多选模型：**Shift+点选 = 加入 / 移出**（上游 `toggle_selection`；橡皮 / 恢复块 / 水印 / 自动打码这几个「Shift 另有含义」的工具不给，照 `allow_shift_toggle`）、**Shift+空白拖动 = 框选标注**（上游 `MarqueeSelection`：与框**相交**即命中、Shift 拖着框 = 加选、拖不到东西 = 清空）。选中多条时：每条一圈虚线框（**八个把手只在单选时给**）、参数行按上游优先顺序挑族（气泡 → 画笔滤镜 → 矩形滤镜 → 马赛克 → 文字 → 序号 → 聚光 → 形状 → 矩形荧光 → 荧光笔 → 直线 → 折线 → 画笔 → 测距 → 括号 → 测角 → 箭头 → 放大镜），显示值取主选中项（不在该族时取该族第一个），**各条不一样的项显示「多个值」**（清空选中态 / 读数换成「多个值」/ 输入框留空 + 占位，悬停说明「改了会一起写成同一个值」）；改参数**只写回同族那几条**且只占一个撤销位、只把变过的字段合并进创建样式；拖动选区里任意一条 = **整组一起挪**（预览代画原件），Delete = 批量删除（一个撤销位）。**限制**：多选不给整体缩放 / 旋转（八个把手只在单选出现），LTools 的「选择」工具空白无修饰键拖动仍是「拖整个选区窗口」，框选挂在 **Shift+拖**（对应上游 `empty_canvas_action: MarqueeSelect` 的加选语义） | P2-e3 | ✅ |
| 直线 Line | `lineRequested` | `Mark.Line`（可虚线；横向拖拽不会因「高度为 0」被丢） | P1 | ✅ |
| 画笔 Brush（FreeDraw） | `brushRequested` | `CaptureTool.Pen` | — | ✅ |
| 矩形高亮 RectangleHighlight | `highlightRequested` | `CaptureTool.HighlightRect` → `Mark.Highlight`（半透明铺块） | P1 | ✅ |
| 画笔高亮 PenHighlight | `brushHighlightRequested` | `CaptureTool.Highlighter` → `Mark.Stroke(highlighter: true)` | — | ✅ |
| 放大镜 Magnifier（倍率 / 连接线 / 源框 / 移动手柄） | `magnifierRequested` + `SnowCanvasMagnifierStyle` + `magnifier.rs` / `hit_resolution.rs` | `Mark.Magnifier` + `MagnifierSpec`：镜头框 + 倍率（1–10）+ **连接方式（无 / 引导线 / 渐变）** + 源轮廓 + 引导线箭头（14 种端点 + 比例）+ 圆角 + 不透明度 1:1；源与镜头**各自独立定位**，拖动语义照上游 `hit_resolution.rs`：拖**源区域** = 两个中心一起平移，拖**镜头本体**（源之外）或镜头下缘 +24 DIP 的**移动手柄**（14×14、命中 ±9）= 只动镜头；选中时画源区域虚线轮廓（1 DIP、选中色）与六点移动手柄 | P1 / P2-d2 | ✅ |
| 聚光灯 Spotlight | `spotlightRequested` | `Mark.Spotlight` | — | ✅ |
| 橡皮 Eraser（点选删除 / 三模式切换） | `buildEraserFamily` `:4271` | `CaptureTool.Eraser` + 参数行「模式」选择器（橡皮 / 矩形橡皮 / 画笔橡皮，点另一个 = 换工具，与上游 `modeSelector` 同义） | P1 / P2-d1 | ✅ |
| 矩形橡皮 RectangleEraser | `rectangleEraserRequested` | `CaptureTool.RectEraser` → `Mark.Restore`：落一条**恢复原图块**（把这块像素还原成截图原样，盖掉下面的标注 / 滤镜），块本身不可选、不可删，只能撤销 | P1-b | ✅ |
| 画笔橡皮 BrushEraser | `brushEraserRequested` | `CaptureTool.BrushEraser` → `Mark.Restore`（笔迹遮罩，只有压过的那一条还原）+ 参数行「模式」+「线宽」（那一族新建时默认 30 DIP，上游 `brushEraserStyle.strokeWidth`） | P1-b / P2-d1 | ✅ |
| 自动打码 AutoFilter（视觉区域检测） | `screenshotautofiltercontroller.cpp` + `auto_filter_workflow.rs` + `auto_filter.rs` | `CaptureTool.AutoFilter`（V）+ `Features/CapturePro/AutoFilter/*`（检测 / 状态）+ `AutoFilterLogic`（命中 / 框选 / 填充事务 / 顺序归一）：进入工具即识别、扫描光带（1400ms）、结果闪红 300ms、hover 红 / 绿高亮、单击 toggle、拖动框选一批、按类型「填充区域」；新增 `FilterKind.Mosaic` | P1-c | ✅ |
| 矩形滤镜 RectangleFilter | `rectangleFilterRequested` | `Mark.Filter` + `CaptureTool.Filter` | — | ✅ |
| 画笔滤镜 BrushFilter | `brushFilterRequested` | `Mark.BrushFilter`（笔迹遮罩 + 同一套滤镜） | P1 | ✅ |
| 水印 Watermark（文本 / 模板 / 角度 / 间距 / 不透明度） | `SnowCanvasWatermark*` + `document.rs` 的模板展开 | `Mark.Watermark` + `WatermarkSpec`：文字 / 颜色 / 字号（12·16·24·30 预设 + 数字框）/ 字体 / 角度（−90–90）/ 间距（10–200）/ 不透明度 1:1；**格距 = max(间距 + 字号 × 4, 32)**、行高 1.2 × 字号；模板 `{text}` + `{YYYY}{MM}{DD}{HH}{mm}{ss}`（嵌套 / 未闭合 / 认不出的原样保留），模板可增删、存 `ui.json`；**图片水印**上游也没有，不做 | P1 / P2-d2 | ✅ |
| 文字 Text（富文本样式） | `textRequested`、`snow_canvas_text_render.cpp` | `Mark.Text` + `TextInk`：字号 / 字体族 / 粗斜 / 水平对齐 / 文字描边（宽 + 色）/ 背景胶囊（填充色 · 填充方式 · 圆角），排版照上游 `text_fill_outset`（行高 1.2×、内边距 0.32×/0.1×、圆角钳 `min(w,h)/2`），描边单独一遍 | P2-c | ✅ |
| 序号 SerialNumber（徽标样式 / 数字写法） | `SnowCanvasSerialNumber*` + `serial_number_format.rs` | `Mark.Serial` + `SerialSpec`：**5 种徽标**（空心圆 / 实心圆 / 空心方 / 实心方 / 纯圆圈不写数字）+ **5 种写法**（阿拉伯 / 罗马 1–3999 / 小写字母 / 大写字母 / 中文带万·亿·兆·京）+ 字号（6–256，默认 24）+ 字体 / 描边色 + 线宽 / 填充色 + 填充方式 / 不透明度；**徽标直径 = 1.68 × 字号**、方形圆角 = 0.2 × 字号、实心徽标用对比字色；参数行「序号」= 下一个号码，落一个自动 +1 | P2-d2 | ✅ |
| 取色 Picker | `canvasColorSamplingRequested` | `CaptureTool.Picker` | — | ✅ |
| 每工具样式记忆 + 落盘 | `screenshotcanvastoolstyles.cpp`、`configs` | `ToolStyleSet`：每个工具家族各一套（`StyleFamilies`，形状 / 气泡共用 `shape` 族）+ 共享颜色开关 + 落 `configs/ui.json` 的 `CaptureStyles`（只写碰过的族） | P2-a | ✅ |
| 样式预设 / 默认样式 | `screenshotdefaultstyles.h` | 默认值已对齐（描边 `#F5222D`、填充 `#FFCCC7`、圆角 6、线宽 2、`arrowType = Curve`）；**用户可存的样式预设**留 P2-d | P2-b / P2-d | 🟡 |
| 描边色 / 描边宽 / 填充 / 虚线 | `SnowCanvasShapeStyle` | `ShapeSpec` + `ShapeInk`：形状（矩形 / 椭圆 / 菱形 / 思考气泡）、描边实 / 虚 / 点、笔触风格（干净 / 手绘 / 涂鸦，同一 seed 重绘一致）、填充无 / 实心 / 斜线 / 交叉、填充色、圆角、直线 / 曲线、画笔压感 | P2-b | ✅ |
| 字体族 / 字号 / 粗斜 / 对齐 | `SnowCanvasTextStyle` | `TextSpec` + 工具栏「字号（数字 + S/M/L/XL 预设）/ 字体（可搜索弹层）/ B·I / 左中右对齐 / 描边宽 + 描边色 / 背景填充色 + 填充方式 + 圆角」；字号 6–256、默认 30，描边 0–72、默认 0；**文字族没有不透明度**（上游 `buildTextFamily` 只有这十格） | P2-c | ✅ |
| 滤镜类型与强度 | `SnowCanvasFilterType` | `FilterKind`：模糊 / 灰度 / 反相 / 浮雕 / 亮度 / **智能擦除**（`SmartErase.cs` 移植上游三级重建）+ 强度 | P1-b | ✅ |
| 工具栏自定义（显示哪些工具 / 顺序 / 分组） | `screenshotdrawingtoolbaroptions.h` | `ToolLayout` + `ToolLayoutStore` | — | ✅ |

### 2.2 选区（P3）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| X / Y / W / H 数值框 + px 单位 | `screenshotselectiontoolbarwidgets.cpp` | `SelBar` 上的 X · Y · 宽 × 高（物理像素，滚轮 ±1 px，单击开参数框） | P3-b | ✅ |
| 比例锁定 + 比例预设（16:9 等） | 同上 `Lock selection aspect ratio` | 信息条上的锁 + 比例下拉（1:1 / 3:2 / 4:3 / 16:9 / 2:3 / 3:4 / 9:16 + 自由）；锁着改宽 / 高按比例推、左上角锚定 | P3-c | ✅ |
| 圆角半径 | 同上 `Corner radius` | `SelectionStyle.CornerRadius`（DIP，上限 256 px），预览与导出都吃 | P3-a/b | ✅ |
| 阴影像素宽（无边框时） / 边框宽 | 同上 `Shadow width` / `Border width` | `SelectionStyle` 阴影（峰值 36% + `(1-d/w)^4`）/ 边框（外扩描边），上限 64 px；两份宽与颜色各自保留 | P3-a/b/c | ✅ |
| 选区预设 / 上次选区 | 同上 `Selection presets` `Previous selection` | 比例下拉里的「上次选区」+ 具名快速集（参数框里增删，落 `settings.json`） | P3-c | ✅ |
| 智能选区（UI 元素识别 + 过渡动画） | `core/screenshotintelligentselectionmodel.cpp`、`screenshotsmartselectiontransition.cpp` | `Features/CapturePro/SmartSelection.cs`（三档模式 + 命中路径 / 层级偏好 / Tab / 细化）+ `SmartSelectionTransition.cs`（Soft 101ms / Fast 67ms + 0.4 DIP 落位容差）+ `CaptureOverlayWindow.Smart.cs`（预选即真选区、单击确认、拖动改手框、换工具落成普通选区、右键退出）；候选来源仍是 `Views/ScreenSnapTargets`（Win32 窗口树 + UIA 子树） | P3-d | ✅ |
| 选区阴影渲染 | `overlay/screenshotselectionshadowrenderer.cpp` | `MarkRenderer` + `SelectionStyle`（预览与导出同一条路径） | P3-a | ✅ |
| 悬停窗口 / 控件高亮与切换 | `overlay/screenshotoverlaycanvaspresenter.cpp` | `CaptureOverlayWindow.Hover.cs` | — | ✅ |

### 2.3 识别与转换（P4）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 本地 OCR（系统 / PP-OCR） | `ocr/screenshotocrcontroller.cpp` | `Features/Flow/PaddleOcr.cs` + `CaptureRecognize.cs` | — | ✅ |
| OCR 结果编辑器（选中 / 编辑 / 复制 / 替换为文本） | `screenshotocrtexteditingsession.cpp`、`screenshotocrvisuals.cpp` | `OcrTextEditModel`（行文本 + 跨行字符选区 + 插入 / 删除 / 退格 / 整行替换 + 撤销 50 步）+ `CaptureOcrCanvas`（按源图像素画、逐行可点 / 拖选 / 双击选整行）+ `CaptureResultWindow` 键盘全集（Ctrl+A/C/X/V/Z/Y、Delete/Backspace、方向键、Enter 拆行） | P4-a | ✅ |
| OCR 原文预览窗 | `screenshotoriginalimagepreviewwindow.cpp` | `Views/CapturePro/CaptureOriginalPreviewWindow`（无边框置顶、100% 按屏幕缩放折算、可左右停靠） | P4-a | ✅ |
| 文字层配色（逐行估背景 / 字色） | `screenshotocrvisuals.cpp`（`estimateBackground` / `estimateTextColor` / `dominantColor`） | `Features/CapturePro/OcrPalette.cs`（OKLab 感知色 + 4 种子 2 步均值漂移 + 方向分族 + 对比度提纯；估不出时退回「浅底黑字 / 深底白字」）+ `OcrTextLayer.cs`（逐行组装 + 字号拟合） | P4-a | ✅ |
| 文本排版还原（行 / 段 / 字号） | `screenshotocrtextlayout.cpp`、`screenshotocrlayout.cpp` | 逐行拟合：字号 = 槽高 82%，超宽等比缩到 98% 槽宽（下限 6 px）；**没有段落信息**（只有逐行框），上游的「按段落重排 + 行内缩放」不复刻 | P4-a | 🟡 |
| 二维码识别 + 呈现 | `screenshotqrcontroller.cpp` | `QrCodeAlgorithms.Read` + 结果窗「二维码」页签（内容 / 格式 / 字数 / 复制） | P4-c | 🟡 |
| 表格识别 + 表格编辑器 | `tableRequested`（在线服务）、`screenshottabledocument.cpp`、`screenshottableeditor.cpp`、`screenshotrecognitionfileexport.cpp` | 识别走本地 OCR 派生（`OcrTable`）；编辑器**完整复刻**：`TableDocument`（锚点槽位 / 256 边 + 16384 格上限 / `Merge` / `Split` / `Clear` / `ExpandedRange` / `Paste` 四值结果 / `ToHtml` · `ToTsv` · `ToClipboard` / `FromGrid` · `FromTsv` · `FromHtml`）+ `CaptureTableGrid`（自绘网格、单击进编辑、拖动矩形选区、Ctrl 并集、AnyKeyPressed、方向键 / Enter / Tab / F2 / Esc、右键 Copy/Paste/Clear/Merge/Split）+ `CaptureResultWindow.Table.cs`（撤销 · 重做 50 步 / 合并 / 拆分 / 重置的 `can*` 可用性、HTML+文本双格式复制、`XlsxWriter` 导出 xlsx、打开表格 = 临时 xlsx 用默认程序打开） | P4-b | ✅ |
| 文档识别 | `documentRequested` | 无 | P4 | ❌ |
| LaTeX 识别 + 渲染 | `screenshotlatexrenderer.cpp` | 无 | P4 | ❌ |
| 图片转 Markdown | `markdownRequested` | 无 | P4 | ❌ |
| 图片转 HTML | `htmlRequested` | 无 | P4 | ❌ |
| 在线翻译 | `textTranslationRequested`、`settings/screenshottranslationsettings*` | 无 | P4 | ❌ |
| 识别结果贴回画布（作为文字标注 / 覆盖原图） | `screenshotocrtexttransform.cpp` | 无 | P4 | ❌ |

### 2.4 贴图（P5）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 贴图窗口（拖动 / 缩放 / 旋转 / 缩放快捷键） | `pinned/*` | `CapturePinWindow.*` | — | ✅ |
| 贴图分组（成组移动 / 只显示某组） | `services/pinnedwindowgroupmanager.cpp` | `PinGroups.cs` + 右键菜单 | — | ✅ |
| 贴图管理页 | `components/pinnedwindowmanagementpagewidget.cpp` | 只有右键菜单 | P5 | 🟡 |
| 贴图颜色取样板（取色 / 色板） | `services/pinnedcolorswatch.cpp` | 无 | P5 | ❌ |
| 贴图文字样式捕获 | `services/pinnedtextstylecapture.cpp` | 无 | P5 | ❌ |
| 贴图堆叠与层级协调 | `pinned/pinnedwindowstackingcoordinator.cpp` | `RaiseAllAbove` 部分等价 | P5 | 🟡 |
| 点击穿透 / 抖动隐藏 | `pinnedwindowselectioncontroller.cpp` | 无 | P5 | ❌ |
| 批量保存贴图 / 拖拽导出 | `screenshotfilepinbatch.h`、`screenshotcontentdrop.h` | 无 | P5 | ❌ |
| 贴图历史与恢复位置 | `services/historypinplacement.cpp` | 无 | P5 | ❌ |

### 2.5 图片编辑 / 拼图 / 切图（P6）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 图片编辑窗口（导入已有图片 → 画布标注 → 导出） | `imageediting/*` | 只有截图后的覆盖层 | P6 | ❌ |
| 拼图（多图拼接 / 行 / 间距 / 背景 / PDF） | `collage/*` | 无 | P6 | ❌ |
| 切图（网格 / 智能布局 / 导出） | `imagesplit/*` | `Features/CapturePro/Stitch` 只有长截图拼接 | P6 | ❌ |
| 全屏画布（在任意屏幕讲解 / 贴住） | `globalcanvas/*` | 无 | P8 | ❌ |

### 2.6 采集入口与设置（P7）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 区域截图 | `selector/*` | `CaptureSession` | — | ✅ |
| 焦点窗口 / 显示器 / 全屏 | `capture/screenshotcapturepolicy.cpp` | 面板「全屏」+ 悬停窗口选择 | 🟡 P7 | 🟡 |
| 延时截图（可配置秒数 / 倒计时） | `quick.screenshot-delay` | 无 | P7 | ❌ |
| 固定区域截图（记住矩形） | `quick.screenshot-fixed` | 无 | P7 | ❌ |
| 滚动长截图 | `capture/screenshotscrolling*` | `ScrollCaptureHookState` / `ScrollStitch` | — | ✅ |
| 鼠标释放即执行动作 | `services/mousereleaseactioncontroller.cpp` | 无 | P7 | ❌ |
| 全局快捷键（截图 / OCR / 翻译 / 滚动 / 复制 / 保存 / 快速保存 / 贴图…） | `services/globalshortcutmanager.cpp`、`settingscatalog.cpp` | 截图 Pro 只有覆盖层内快捷键 | P7 | 🟡 |
| 导出设置（格式 / 质量 / 文件名模板 / 快捷保存目录） | `screenshotencodingsettings.h`、`screenshotexportnaming.h` | `CaptureSave` + `CaptureSaveWindow` | P7 | 🟡 |
| 剪贴板外观（不透明 / 圆角 / 阴影） | `screenshotclipboardappearance.h`、`screenshotclipboardplacement.h` | 直接复制原图 | P7 | ❌ |
| 采集历史页（搜索 / 筛选 / 恢复贴图 / 批量） | `components/screenshothistorypagewidget.cpp`、`screenshothistoryservice.cpp` | `CaptureHistoryWindow` 基础版 | P7 | 🟡 |
| 云上传 / 分享 / AirDrop | `screenshotclouduploadservice.cpp` | 不复刻（依赖第三方服务） | — | ⛔ |
| 相机快门音 | `capture/camerashuttersound.cpp` | 无 | P7 | ❌ |

### 2.7 录屏（P8）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 区域 / 窗口 / 全屏录制 | `recording/*` | `RecordSession` + 区域框 | — | ✅ |
| 系统声音 / 麦克风 / 混音 | `recordingaudio*` | `RecordAudio` | — | ✅ |
| 音频增益 popover | `recordingaudiogainpopover.cpp` | 无 | P8 | ❌ |
| 硬件编码 / 回退 | `RecordEncoders`（已复刻） | — | ✅ |
| 键鼠特效 | `recordingeffect*` | `RecordEffects` | — | ✅ |
| 摄像头画中画 / 虚拟相机 | `recordingcamera*` | 仅开关参数 | P8 | 🟡 |
| 渲染进度对话框 | `recordingrenderdialog.cpp` | 只有 toast | P8 | ❌ |
| 录制快捷键控制器 | `screenrecordingshortcutcontroller.cpp` | 部分（面板按钮） | P8 | 🟡 |
| 工具条自动隐藏 / 悬停唤出 | `recordingtoolbarwindow.cpp` | 无 | P8 | ❌ |
| 崩溃残片恢复 | `recordingrecovery.h` | `RecordRecovery` | — | ✅ |

## 3. 批次与验收

| 批次 | 范围 | 状态 | 验收 |
| --- | --- | --- | --- |
| P0 | 已完成基线（区域 / 窗口 / 滚动 / 贴图 / 录屏五项 / 两行面板 / 区域框 / 工具布局） | ✅ | 见提交 `a61e15f` |
| P1 | 绘图工具补齐：气泡 / 直线 / 测距 / 测角 / 括号 / 放大镜 / 水印 / 矩形荧光 / 画笔滤镜 / 矩形橡皮 / 画笔橡皮（工具 14 → 25，默认工具栏 9 → 13 格） | ✅ | 构建 0 警；`--selftest` 209 用例 6210 断言全绿（`capturePro` 41 用例 1379 断言）；`capture-overlay-smoke` 536 条全绿（含 11 个新工具的真手势 + 导出逐像素对账） |
| P1-b | 橡皮与滤镜对齐上游：① 像素级智能擦除 `SmartErase` 滤镜（`snow-draw-engine-gpui/src/smart_erase` 的仿射曲面 → 周期纹理 → 多尺度块匹配三级求解）② 两把橡皮改成落 `RestoreBackground` 恢复原图块（`SnowCanvasTool::RectangleEraser` / `BrushEraser`） | ✅ | 构建 0 警；`--selftest` 211 用例 6240 断言全绿（`capturePro` 43 用例 1414 断言）；`capture-overlay-smoke` 549 条全绿 |
| P1-c | 自动打码 AutoFilter：移植 `visual-region-detector` 的检测管线（canny / 轮廓 / 形态学 / 对齐 / 文字行 / 媒体分类）+ `screenshotautofiltercontroller.cpp` 的闪烁提示与分类填充 | ✅ | 构建 0 警；`--selftest` 213 用例 6315 断言全绿；`capture-overlay-smoke` 552 条全绿（真窗口识别 10 块 / 点选 toggle / 框选保留 id 序 / 按类型填充替换 / 离开工具清结果） |
| P2-a | 样式记忆：每个工具家族各一套样式 + 共享颜色开关 + 落盘 `ui.json`（`CaptureStyles`） | ✅ | 构建 0 警；`--selftest` 214 用例 6383 断言全绿；`capture-overlay-smoke` 586 条全绿（切工具各自保留 / 矩形与椭圆同一份 / 共享颜色全改 / 参数条跟族刷新） |
| P2-b | 形状族参数：形状选择（矩形 / 椭圆 / 菱形 / 思考气泡）+ 描边实 / 虚 / 点 + 填充（实心 / 斜线 / 交叉 / 无）+ 圆角 + 笔触风格（干净 / 手绘 / 涂鸦）+ 直线 / 曲线 + 画笔压感 | ✅ | 构建 0 警；`--selftest` 215 用例 6513 断言全绿（`capturePro` 47 用例 1687 断言，新增「样式：形状族」一组）；`capture-overlay-smoke` 620 条全绿（真参数行胶囊 3 档形状 / 思考只在气泡出现 / 填充色单独一排 / 圆角默认 6 / 点胶囊改样式 / 填充与圆角与菱形与虚点线与手绘与思考气泡圆点逐像素对账） |
| P2-c | 文字族参数：字色 / 字号（6–256 + S·M·L·XL）/ 字体族（可搜索弹层）/ 粗体 / 斜体 / 水平对齐 / 文字描边（宽 0–72 + 色）/ 背景（填充色 · 填充方式 · 圆角 0–83），排版照上游 `text_fill_outset`（行高 1.2×、内边距 0.32×/0.1×、描边单独一遍）。上游文字族没有垂直对齐与不透明度，故不实现 | ✅ | 构建 0 警；`--selftest` 216 用例 6602 断言全绿（`capturePro` 48 用例 1776 断言，新增「文字族」一组 89 条）；`capture-overlay-smoke` 670 条全绿（真参数行 10 格 / 点 L·M 改字号 / 字体弹层搜索选中收起 / B·I 独立开关 / 对齐三胶囊 / 描边默认 0 + 换色 / 填充四档 / 圆角 6 / 形状无字号；逐像素：实心胶囊 · 无填充 · 圆角切角 · 斜线少于实心 · 描边外圈 · 粗体墨多 · 斜体与换字体像素不同 · 左中右对齐窄行位置） |
| P2-d1 | 端点 / 测距 / 测角 / 括号 / 橡皮：14 种端点（`ArrowInk`）+ 测距单位 · 小数位 · 端点比例 · 实测值换算 + 测角内角 · 弧度 · 小数位 + 括号改花括号（`BracketInk`）+ 橡皮三模式选择器 | ✅ | 构建 0 警；`--selftest` 217 用例 6693 断言全绿（`capturePro` 49 用例 1867 断言，新增「度量」一组）；`capture-overlay-smoke` 688 条全绿（真参数行 7 / 5 / 1 格 + 实测值置灰与显示 + 点「画笔橡皮」换工具 + 默认笔头 30；逐像素：花括号尖 / 肩 / 空脊线、测距无黑底 pill 的纯文字、端点比例 2 的箭头肩线） |
| P2-d2 | 放大镜（连接方式 / 源轮廓 / 引导线箭头 / 圆角）+ 序号（5 类型 / 5 写法 / 字号 + 字体 / 填充色 + 填充方式 / 描边）+ 水印（字体 + 字号预设 + 模板 + 角度 / 间距 / 不透明度 / 色） | ✅ | 构建 0 警；`--selftest` 218 用例 6795 断言全绿（`capturePro` 50 用例 1969 断言，新增「样式：序号徽标 / 水印模板 / 放大镜几何」一组）；`capture-overlay-smoke` 714 条全绿（真参数行序号 9 格 / 放大镜 9 格 / 水印 8 格 + 落号后「序号」自动 +1 + 放大镜三段拖动语义 + 源轮廓与移动手柄逐像素） |
| P2-e1 | 箭头族参数：箭头类型（直线 / 曲线 / 折线）+ 起止端点 14 种 + 箭头比例 0.5–3 + 杆型（等宽 / 渐宽）+ 外描边（宽 0–72 + 色）+ 几何照 `arrow/{data,render_core,shaft}.rs`（Catmull-Rom 曲线、拐角圆角、渐宽 ribbon、外描边垫底） | ✅ | 构建 0 警；`--selftest` 219 用例 6866 断言全绿（`capturePro` 51 用例 2040 断言，新增「样式：箭头族」一组）；`capture-overlay-smoke` 749 条全绿（真参数行 11 格 / 三类型胶囊 / 杆型胶囊 / 比例 1.0× / 外描边 0 / 起终点默认值；逐像素：直线杆、终点三角、曲线偏离弦、折线三段、14 种端点、双端箭头、外描边青圈 + 墨迹覆盖、渐宽杆变粗） |
| P2-e2 | 选中标注后切它自己的样式族 + 显示这一条的值 + 参数实时写回（形状 / 箭头 / 线 / 文字 / 序号 / 放大镜 / 测距 / 测角 / 括号 / 滤镜…，且只把变过的字段合并进创建样式） | ✅ | 构建 0 警；`--selftest` 220 用例 7042 断言全绿（`capturePro` 52 用例 2216 断言，新增「样式：选中标注后切族并写回」46 条：18 种标注 → 族 / 工具反向映射、捕获 → 写回逐字段等价、改一项不动几何与种子、`CopyChangedTo` 只搬变过字段且不收序号计数器）；`capture-overlay-smoke` 775 条全绿（真窗口：选中旧箭头时参数行显示它自己的线宽 7 而创建样式仍是 4、工具按钮停在「选择」、改色写回标注且创建样式只合并颜色、滚轮改线宽两个都变、取消选中参数行收起、画笔下选形状切形状族且工具不动、测距「实测值」按那一条自己的系数 2 显示） |
| P2-e3 | 多选标注（Shift+点选 / Shift+拖框选）+ 参数行「多个值」混合显示（`Mixed`）+ 整组拖动 / 批量删除 | ✅ | 构建 0 警；`--selftest` 221 用例 7102 断言全绿（`capturePro` 53 用例 2276 断言，新增「多选：选区切族 / 框选命中」一组：优先顺序挑族、显示值取主选中项 / 族内第一个、水印与空选区不归族、`InFamily` 只认同族、框选相交即命中（含零高度箭头）、加选不重复、抖动不算框、`AllowsShiftToggle` 逐工具表）；`capture-overlay-smoke` 809 条全绿（真窗口：三条线宽 3/5/7 的箭头整组选中 → 线宽显示「多个值」而颜色仍显示选中色块、只选一条回落到它自己的 5、滚轮改一次三条一起变 8 且创建样式同步、一次撤销三条各回原值且重新显示「多个值」、整组拖动三条一起挪一次撤销全回来、框选三条命中并提交选中、空框加选不清空、取消框选不动选区、Esc 清空多选、批量删三条一次撤销全回来；顺带修好自动打码「填充区域」按钮没被加进参数行） |
| P3-a | 选区外观模型：`Features/CapturePro/SelectionStyle.cs`（圆角上限 256 / 阴影与边框宽上限 64 / 阴影 `PeakAlphaScale = 0.36` + `(1-d/w)^4` 衰减 / 边框为外扩描边）+ `MarkRenderer` 与导出按「内容 + 两侧效果宽」出图（照 `screenshotselectionshadowrenderer.cpp`） | ✅ | 构建 0 警；`--selftest` 全绿（`capturePro` 新增「选区外观」一组：圆角切角 / 阴影四周渐隐 / 边框外扩不吃内容 / 导出尺寸 = 内容 + 2×效果宽）；`capture-overlay-smoke` 逐像素对账 |
| P3-b | 选区信息条（照 `screenshotselectiontoolbarwidget.cpp`）：`SelBar` 高 26 · 圆角 6 · 左右 8 / 上下 2；X · Y · 锁 · 宽 × 高 · 比例下拉 · 圆角 · 效果宽；数字全是**屏幕物理像素**，滚轮 ±1 px（`adjustFromToolbar` 口径），外观长度存 DIP、显示按 `_doc.Scale` 换算；摆位 `SelectionBarPlacement`（上方优先，放不下依次换右 / 左 / 下） | ✅ | 构建 0 警；`--selftest` 全绿（「选区信息条」一组：摆位四向 / 滚轮改值 / 锁比例联动 / 物理↔DIP 往返）；`capture-overlay-smoke` 864 条全绿 |
| P3-c | 选区参数框 + 快速集：`Views/CapturePro/SelectionParamsWindow`（X / Y / 宽 / 高 + 比例锁 + 圆角 + 阴影 \| 边框 + 效果宽 / 色板 + 快速集增删）、`SelectionStore` 落 `settings.json`、比例下拉里的「上次选区 / 具名快速集 / 8 个比例」 | ✅ | 构建 0 警；`--selftest` 227 用例 7247 断言全绿；`capture-overlay-smoke` 864 条全绿（信息条数字 / 摆位 / 滚轮 ±1 / 圆角与效果宽物理像素 / 锁 / 比例预设 / 下拉项高 28 / 参数框字段与联动 / 色板 / `ReadParams`）；真机 `selection-bar-e2e` 全绿（真鼠标框选 → 真滚轮 ±1 → 真点击锁（图标转 #1677FF、锁着改宽按比例推、左上角不动）→ 真点击下拉「1:1」（选区变正方形）→ 真键盘 Esc 退出） |
| P3-d | 智能选区：`Features/CapturePro/SmartSelection.cs`（`SmartSelectionMode` 三档 / `SmartSelectionTarget` 两级 / 命中路径「最深 → 整窗」/ 层级偏好与 Tab / 细化只增不减）+ `SmartSelectionTransition.cs`（按绝对时间插值、`p*(2-p)` 缓出、8ms 喂时间）+ `Views/CapturePro/CaptureOverlayWindow.Smart.cs`（预选就是真选区，主工具条与形状条收起、信息条只读、单击确认 / 拖动改手框 / 换工具落成普通选区 / 右键退出这次截图） | ✅ | 构建 0 警；`--selftest` 全量 7387 断言全绿（`capturePro` 61 用例 2561 断言，新增上游 16 组智能选区用例）；`capture-overlay-smoke` **911 条全绿**（新增 `[smart]` 50 条：预选几何与标签 / 工具条收起 / 信息条只读且报物理像素 / 滚轮换层走过渡并精确落位 / Tab 整窗↔子元素 / 真鼠标单击确认 / 换工具落成普通选区 / 真鼠标从按下点拖框 / 真右键退出；落图 `out/smart-preview.png`、`smart-window.png`、`smart-manual.png`） |
| P3 | 选区：XYWH / 比例 / 圆角 / 阴影 / 智能选区 / 预设 | ✅ | P3-a/b/c/d 全部落地 |
| P4-a | 识别结果窗（照 `screenshotrecognitionwindow` + `screenshotocrvisuals` / `screenshotocrtextlayout` / `screenshotocrtexteditingsession` / `screenshotoriginalimagepreviewwindow`）：无边框置顶、贴选区摆位；**文字层**按 OCR 行框重排（逐行估背景 / 字色、按槽高拟合字号），可点 / 拖选 / 双击选整行 / 直接改字（跨行选区、插入、删除、退格、整行替换、撤销重做 50 步）；「显示原图 / 填充背景 / 原文预览」三个开关；表格 / 二维码页签 | ✅ | 构建 0 警；`--selftest` 233 用例 **7452 断言全绿**（`capturePro` 65 用例 2626 断言，新增估色 / 行层与字号 / 编辑模型 / 命中四组）；`capture-overlay-smoke` **931 条全绿**（识别窗 21 条：画布按原图像素铺 320×80、页签与复制按钮文案、显示原图收起文字层并禁用填充、填充开关落到画布、原文预览窗开在识别窗旁、**真鼠标跨行拖选** → 状态行报「已选中 6 个字符 · 共 2 行」、选整行） |
| P4-b | 表格编辑器（照 `screenshottabledocument.cpp` + `screenshottableeditor.cpp` + `screenshotrecognitionfileexport.cpp`）：`Features/CapturePro/TableDocument.cs`（锚点槽位模型、256 边 / 16384 格上限、`Merge` / `Split` / `Clear` / `ExpandedRange`、粘贴的 `DoesNotFit` / `CutsMergedCell` 两个拒绝出口、`ToHtml` · `ToTsv` · `ToClipboard` 与 `FromGrid` · `FromTsv` · `FromHtml`）、`Views/CapturePro/CaptureTableGrid.cs`（自绘网格、按文字量算列宽、单击进编辑、拖动矩形选区 / Ctrl 并集、AnyKeyPressed、方向键 / Enter / Shift+Enter / Tab / Shift+Tab / F2 / Esc、右键 Copy·Paste·Clear·Merge·Split·全选·重置）、`Views/CapturePro/CaptureResultWindow.Table.cs`（撤销重做 50 步、`canUndo/canRedo/canMerge/canSplit/canReset/hasSelection` 可用性、HTML+文本双格式复制、`Features/CapturePro/XlsxWriter.cs` 导出 xlsx、打开表格 = 临时 xlsx 用默认程序打开） | ✅ | 构建 0 警；`--selftest` 237 用例 **7562 断言全绿**（`capturePro` 69 用例 2736 断言，新增槽位模型 / HTML·TSV 往返 / 粘贴规则 / xlsx 结构与转义四组）；`capture-overlay-smoke` **960 条全绿**（表格页：2×2 文档、真鼠标单击进编辑改字、真鼠标拖选一行两格、合并 / 撤销 / 重做 / 拆分、复制 TSV + HTML 双格式、重置回识别内容、按钮置灰矩阵；落图 `out/result-table.png`） |
| P4 | 识别与转换：OCR 编辑器 / 原文预览 / 表格 / 二维码 / LaTeX / Markdown / HTML / 文档 / 翻译 | ⏳ | P4-a、P4-b 已完成；余下 P4-c 二维码呈现 / P4-d Markdown·HTML / P4-e 文档 / P4-f 在线翻译 / P4-g 贴回画布 |
| P5 | 贴图：管理页 / 取色板 / 文字样式 / 点击穿透 / 抖动隐藏 / 批量保存 / 拖拽导出 / 历史恢复 | ❌ | 待填 |
| P6 | 图片编辑窗 / 拼图 / 切图 | ❌ | 待填 |
| P7 | 采集入口与设置：延时 / 固定区域 / 鼠标释放动作 / 全局快捷键 / 导出设置 / 剪贴板外观 / 历史页 | ❌ | 待填 |
| P8 | 全屏画布 + 录屏剩余：音频增益 / 渲染进度 / 快捷键 / 工具条自动隐藏 | ❌ | 待填 |

## 4. 每批次的实施顺序（固定套路）

1. 上游对照：把该批次涉及的上游文件读透，摘出**度量常量 / 交互状态机 / 默认值**，写进本文件对应行。
2. 数据模型：`Features/CapturePro/*` 加纯数据（能被 `--selftest` 直接断言），不碰 WPF。
3. 渲染：只改 `MarkRenderer.Draw`（显示与导出共用），必要时给 `CaptureCanvas` 加图层。
4. 交互：`CaptureOverlayWindow.Annotate.cs`（手势）/ `CaptureToolbar.xaml.cs`（UI）。
5. 图标：`Views/CapturePro/CaptureIcons.xaml` 加 `Icon*` 几何；字形只用已收录码点或自绘 Path。
6. 测试：`Core/SelfTest/Suites/CaptureProSuite.cs` 加断言；仓库外冒烟加真实交互断言。
7. 文档与进度：本文件状态改 ✅ / 🟡，`docs/SCREEN_CAPTURE_PRO.md` 补规格，提交信息按 AGENTS.md 先审批。

## 5. 与上游的已知平台差异

| 主题 | 上游 | LTools 处理 |
| --- | --- | --- |
| 权限引导（屏幕录制授权） | macOS TCC 引导页 | 不复刻（Windows 无此权限） |
| 云上传 / AirDrop | 第三方服务 | 不复刻，导出走本地文件 / 剪贴板 |
| 主题皮肤 | ant-design-qt 多主题 | 跟随 LTools `Themes/Modern.xaml` 灰阶主题，只复刻度量与交互 |
| MCP | 上游自带 MCP 服务 | 由 LTools 自身 MCP（`Core/Ai`）承担，截图 Pro 不另开一套 |

## 6. 变更记录

- 2026-11-18：**P0 基线**确认（提交 `a61e15f`），开 P1。
- 2026-11-18：**P1 完成**。工具 14 → 25：新增直线、测距（中点 px 标签）、测角（两段拖拽 + 滚轮 ±5° / Shift ±1°）、括号、气泡、放大镜（镜头框 + 倍率 1.2–8）、水印（整图斜向平铺，文字 / 角度 / 间距 / 不透明度）、矩形荧光、画笔滤镜（笔迹遮罩 + 同一套滤镜）、矩形橡皮、画笔橡皮。默认工具栏按上游分组重排成 13 格（选择 │ 形状 │ 箭直 │ 自由笔 │ 尺寸 │ 气泡文字 │ 荧光 │ 镜头与聚光 │ 序号 │ 打码 │ 水印 │ 橡皮 │ 取色）。
  - 修掉一处会挡新工具的真 bug：落盘门槛原来要求「宽和高都 ≥ 4 DIP」，水平直线 / 水平测距 / 括号被当成「没拖动」丢掉；现在一维工具（直线 / 测距 / 括号 / 箭头）只判长度（`ToolCatalog.IsLinear`）。
  - 上游「像素级智能擦除」这次没做（两把橡皮都是**按对象删除**），与自动打码一起另列 **P1-b**；放大镜的连接线 / 源框、水印的图片模板留 P2。
  - 验收：构建 0 错 0 警；`--selftest` 209 用例 / 6210 断言全绿；`capture-overlay-smoke` 536 条全绿（真手势画出 11 个新工具 + 合成底图逐像素对账：放大镜把 10×10 绿块放大到约 2 倍面积、画笔滤镜只把笔迹压过的红底反相成青色、测距标签里确有白字、水印左右两半都有白像素）。
- 2026-11-18：**P1-b 完成（像素级智能擦除 + 两把橡皮对齐）**。
  - **智能擦除**：`SnowCanvasFilterType::SmartErase` 在 LTools 落地成 `FilterKind.SmartErase`（滤镜类型胶囊多一个「智能」），矩形滤镜与画笔滤镜都走它。新文件 `Features/CapturePro/SmartErase.cs`（约 900 行，纯计算，不碰 WPF）：逐级移植上游 `smart_erase.rs` / `patch.rs` 的 CPU 路径——① 仿射曲面（洞外一圈已知像素拟合平面，5 轮 IRLS，97% 内点、色差 4/255 才算「纯色 / 渐变」）② 周期纹理（X / Y 找 2–64 像素重复周期）③ 多尺度块匹配（≤6 层金字塔、Lab 引导色 + 局部方差、`Dice(0x534e4f57)` 固定种子、32 个随机候选 + 邻域一致性投票）。种子固定 → 同输入同输出。`MarkRenderer.BuildSmartErase` 按上游 `initial_halo`（洞短边一半夹 16–96 再加 32，至少 64）向外扩一圈取素材，重建完**只把内圈贴回**；素材不足返回 null、渲染层不画（不假装擦掉）。与上游唯一差异：上游大洞交给 GPU，这里改用「洞边长 > 96 时降采样到 ≤96 再重建、结果双线性放大」，小洞逐像素一致。
  - **两把橡皮改成上游语义**：`RectangleEraser` / `BrushEraser` 不是按对象删除，而是各落一条 `RestoreBackground` 滤镜——把这块像素还原成截图原样、盖掉下面的标注 / 滤镜 / 打码。LTools 落成 `Mark.Restore`（矩形 / 笔迹两种），`CaptureDocument.HitTest` 点不中它（上游这种块不可选、旧橡皮也删不掉，只能 Ctrl+Z 撤销）。上一版「相交即删」的写法已删除（`HitByBrush` 一并删掉）。
  - 验收：构建 0 错 0 警；`--selftest` 211 用例 / 6240 断言全绿（`capturePro` 43 用例 / 1414 断言，新增 `SmartEraseCases` 27 条与 `RestoreMarks` 12 条纯像素断言）；`capture-overlay-smoke` 549 条全绿（滤镜胶囊含「智能」、合成底图逐像素：智能擦除洞左半边续红 / 右半边续蓝 / 洞外不动、恢复块露出原图而块外仍是标注、画笔恢复只还原笔迹压过的那一条）。
  - 上游自动打码（AutoFilter）依赖 `visual-region-detector`（约 150 KB Rust 检测管线），量大，另列 **P1-c**。
- 2026-11-18：**P1-c 完成（自动打码 AutoFilter）**。工具 25 → 26（`CaptureTool.AutoFilter`，快捷键 `V`，图标照上游 magic-wand），默认工具栏「打码」格变成 `马赛克 / 自动打码 / 滤镜 / 画笔滤镜`。
  - **检测管线**：`Features/CapturePro/AutoFilter/`（`DetectorCore` / `DetectorMorph` / `DetectorEdges` / `DetectorStages` / `AutoFilterDetector`）逐行照抄上游 `visual-region-detector`（准备证据 → 媒体候选 → 文字与最终分类），与本机 1920×1080 真实截图对账逐区域一致（189 vs 189，含 id / 类型 / 矩形）。8 连通、候选按面积降序稳定排序、NMS 按面积升序保留首次命中、`f as u8` 用 `Math.Truncate` + `Clamp`、`round_ties_even` 用 `MidpointRounding.ToEven`。
  - **交互照 `screenshotautofiltercontroller.cpp` + `auto_filter_workflow.rs`**：进入工具即识别（后台线程）→ 画布扫一道光带（`elapsed % 1400`）→ 结果闪红 300ms → hover 取光标下最小区域高亮（已用同类型打码 = 绿 `#52c41a`，否则红 `#ff4d4f`，填充 alpha 51）→ 单击 toggle 那一块 → 拖动 > `POINTER_DRAG_THRESHOLD`(4) 框选、相交区域一次性 toggle（整批一个撤销位）→ 参数条「填充区域」下拉按 7 类（正文 / 框内文字 / 图片 / 头像 / 图标 / 消息框 / 整段文字）一次填满、填的是 toggle=false、`available()` 不成立时置灰 + `Opacity 0.45`。
  - **打码方式与强度沿用矩形滤镜那一套**（`ToolStyle.FilterMode` / `FilterStrength`）；类型下拉只有 6 项、不含智能擦除（逐区域擦会掏空洞），样式停在智能擦除时回落到马赛克。新增 `FilterKind.Mosaic`（块边长 = `(2 + 20 × 强度²) × 缩放`，块内取平均）。
  - **标注与顺序**：`Mark.Filter` 加 `AutoRegionId`（`MoveBy` / `ScaleTo` 都带上），所以拖动 / 缩放后还认得「这块打过没有」；`CaptureDocument.EditBatch` 让整批只占一个撤销位（引用序列相同不记）；`AutoFilterLogic.NormalizeOrder` 把顺序固定成「智能擦除 → 自动填充按区域 id 升序 → 其余原序」。
  - 验收：构建 0 错 0 警；`--selftest` 213 用例 / 6315 断言全绿（新增「自动打码：命中 / 框选 / 填充事务 / 绘制顺序」「自动打码：区域检测与上游逐区域对齐」）；`capture-overlay-smoke` 552 条全绿（真窗口识别 10 块 / 点选 toggle / 框选保留 id 序 / 按类型填充替换 / 离开工具清结果 / 填充按钮可用性）。
- 2026-11-18：**P2-a 完成（每工具家族一套样式）**。上游 `screenshotcanvastoolstyles.cpp` 是「一个工具家族一份样式」，LTools 原来只有一份全局 `ToolStyle`。
  - 新增 `Features/CapturePro/ToolStyleSet.cs`：`StyleFamilies`（`shape` / `speechBubble` / `arrow` / `line` / `polyline` / `pen` / `highlighter` / … 20 个族）+ `StyleFamilies.Of(tool)`（矩形与椭圆共用 `shape`，自动打码与矩形滤镜共用 `filter`）；`ToolStyleSet.For(tool)` 按族取同一份对象（不是副本），`SetColor` / `SetSyncColors` 复刻上游 `syncToolColors`：开着改一处 = 全改，关着只改自己那一族。
  - 落盘 `configs/ui.json` 的 `CaptureStyles`（`Config/CaptureStyles.cs` 的 `CaptureToolStyleDto`）：只写碰过的族，颜色 `#RRGGBB`、枚举按名字；认不出的值丢弃、越界钳回，写盘防抖 300 ms + `Flush()`（关窗 / 退出补写）。
  - 参数条跟着族刷新（`CaptureToolbar.RefreshValues`），换工具再切回来时刚才调的值还在。
  - 验收：构建 0 错 0 警；`--selftest` 214 用例 / 6383 断言全绿；`capture-overlay-smoke` 586 条全绿（切工具各自保留 / 矩形与椭圆同一份 / 共享颜色全改 / 参数条跟族刷新）。
- 2026-11-18：**P2-b 完成（形状族参数）**。工具 26 → 25：`Rect` 与 `Ellipse` 合并成上游那样一个 `CaptureTool.Shape`（`Key.R`，`Key.E` 释放），每种形状在参数行的「形状」胶囊里选。
  - **几何**：新文件 `Features/CapturePro/ShapeStyle.cs`（约 1000 行，纯数据 + 绘制，不碰窗口）。`ShapeKind` 四种轮廓（矩形 / 椭圆 / 圆角菱形 / 思考气泡云朵）、`StrokeKind` 实 / 虚 / 点、`StrokeRendering` 干净 / 手绘 / 涂鸦（同 `seed` 重绘完全一致，抖动种子存在标注里，拖动 / 缩放不换）、`FillKind` 无（上游 `Border`）/ 实心 / 斜线 / 交叉，`LineType` 直线 / 曲线，`BrushMode` 常规 / 压感。
  - **对齐上游细节**：圆角矩形上限 = 短边 / 2、圆角菱形上限 = 短边 / 4（`roundedDiamondPath` + `normalize_shape_corner_radii`）；虚 / 点线直接给上游 `{4,4}` / `{0.01,3}`（都是线宽倍数，没有再除线宽）；`PathFigure.IsFilled` 必须为 `true`，否则填充与命中同时失效（踩过）。
  - **气泡**：尾巴根部由身体轮廓二分求出（`TailBases`），身体边线不再横穿尾巴；思考气泡是 14 段云朵 + 1–3 个由大到小的圆点（`ThoughtTrail`，照 `thought_bubble.rs`）。
  - **默认值照 `screenshotdefaultstyles.h`**：描边 `#F5222D`、填充 `#FFCCC7`、圆角 6、线宽 2、`arrowType = Curve`；「思考气泡」只在气泡工具的形状胶囊里出现（第 4 个胶囊按工具显隐）。
  - **参数行**：`ToolParam.Shape` 改名 `BadgeShape`（序号的徽标形状），新增 `ShapeKind` / `StrokeStyle` / `Rendering` / `FillStyle` / `FillColor` / `CornerRadius` / `LineType` / `BrushMode` 八格；`ColorRow` 改成可复用（描边色一排 + 填充色一排，`GroupName` 分开），`Chips` 支持按值显隐。
  - **未做**：上游 `ArrowType::Curve` 对两点直线与 Straight 视觉等价，`LineType` / `BrushMode` 只入参数与模型、不假造效果。
  - 验收：构建 0 错 0 警；`--selftest` 215 用例 / 6513 断言全绿（`capturePro` 47 用例 / 1687 断言，新增「样式：形状族（形状 / 描边 / 填充 / 圆角 / 尾巴 / 圆点）」一组）；`capture-overlay-smoke` 620 条全绿（真参数行胶囊 3 档形状 / 思考只在气泡出现 / 填充色单独一排 / 圆角默认 6 / 点胶囊改样式 / 填充与圆角与菱形与虚点线与手绘与思考气泡圆点逐像素对账）。
- 2026-11-18：**P2-c 完成（文字族参数）**。新文件 `Features/CapturePro/TextStyle.cs` 的 `TextSpec` + `TextInk`（约 700 行，纯计算）。
  - **十格照上游 `buildTextFamily`**：字色 → 字号（6–256，默认 30，另有 S/M/L/XL = 24/30/42/54）→ 字体族（可搜索弹层，与 `QFontDatabase` 同口径取本机字体）→ 粗体 / 斜体 → 水平对齐（左 / 中 / 右）→ 文字描边宽（0–72，默认 0）+ 描边色 → 背景填充色 + 填充方式（无 / 实心）+ 圆角（0–83，默认 6）。上游文字族**没有**垂直对齐与不透明度，故不实现、也不在 UI 里放假开关。
  - **排版照 `text_fill_outset`**：行高 1.2 × 字号、内边距 0.32× / 0.1× 字号、圆角钳 `min(w,h)/2`；描边单独一遍（先粗后细），文字本身与背景分别取对比色。
  - 验收：构建 0 错 0 警；`--selftest` 216 用例 / 6602 断言全绿（`capturePro` 48 用例 / 1776 断言，新增「文字族」一组 89 条）；`capture-overlay-smoke` 670 条全绿（真参数行 10 格 / 点 L·M 改字号 / 字体弹层搜索选中收起 / B·I 独立开关 / 对齐三胶囊 / 描边默认 0 + 换色 / 填充两档 / 圆角 6 / 形状无字号；逐像素：实心胶囊 · 无填充 · 圆角切角 · 描边外圈 · 粗体墨多 · 斜体与换字体像素不同 · 左中右对齐窄行位置）。
- 2026-11-18：**P2-d1 完成（端点 / 测距 / 测角 / 括号 / 橡皮）**。三个新纯逻辑文件：`ArrowInk.cs`（端点几何）、`MeasureStyle.cs`（`DistanceSpec` / `AngleSpec` / `MeasureLabel` 全部文案与数值）、`BracketInk.cs`（花括号四段三次贝塞尔）。
  - **端点 14 种**（照 `snow-draw-engine-document/src/arrow/render_core.rs`）：`Arrowhead` 扩到 16 项，界面上只列上游 distance 家族的 14 种（`Square` / `InvertedTriangle` 留在枚举里不上界面）；尺寸基值 Arrow 25、Diamond/DiamondOutline/Square 12、Crowfoot* 20、其余 15，统一 ×1.25；张角 Bar 90°、Arrow 20°、其余 25°；圆直径 `base + min(stroke,2) − 2`；比例钳 0.5–3.0，且**端点不得长过线段**（×0.5，Diamond / Square ×0.25）。空心端点（`CircleOutline` / `TriangleOutline` / `DiamondOutline` / `IndentedTriangle`）上游填「画布背景色」，LTools 的背景是截图 → **只描边不填充**（已知偏差，写进 `SCREEN_CAPTURE_PRO.md`）。
  - **测距**：标签 = `"{值} {单位}"`（一个空格），值 = `长度(DIP) × 物理缩放 × 系数`；**居中压在中点**、描边色纯文字、无背景（弃用原来的黑底白字 pill）；字号 = `20 × √(线宽/2)`；9 种单位、0–3 位小数；工具栏多一格「实测值」——选中一条测距时显示 `长度 × 系数`，改它写回 `系数 = clamp(值 ÷ 长度, 0.01, 1000)`（上游 `setDistanceValue`），没选中时置灰。
  - **测角**：类型「有向 360° / 内角 180°」——`Interior` 把 `>180°` 的 sweep 折成 `2π − sweep`，圆弧与平分线一起翻到另一侧；单位度 / 弧度（`90°` 无空格、`1.57 rad` 带一个前导空格）；0–3 位小数。
  - **括号真身是花括号**：`bracket.rs` 用 a（起）/ c（尖）/ b（止）三点定义局部坐标系 + 四段三次贝塞尔（肩部切线连续、尖是圆角）；拖拽按 `process_bracket_creation_pointer_event` 换算成三点（首尾在起点的 x 上纵向撑满、尖在终点 x 与中线），**横向拖拽会退化、上游同样什么都不生成**。原来「主线 + 两端刻度」的本地实现已整体替换。
  - **橡皮三模式**：参数行一格「模式」（橡皮 / 矩形橡皮 / 画笔橡皮），点另一个 = `ToolChanged` 换工具（上游 `modeSelector` 同一语义）；画笔橡皮多一格「线宽」，那一族新建时默认 30 DIP（`brushEraserStyle.strokeWidth`）。
  - **样式实时生效**：`ApplyStyleToSelection` 只在「当前工具就是测距 / 测角」时把参数写回选中的那一条（别的族留 P2-e2，避免用形状族的线宽去改一条测距）。
  - 验收：构建 0 错 0 警；`--selftest` 217 用例 / 6693 断言全绿（`capturePro` 49 用例 / 1867 断言，新增 `CaptureProSuite.MeasureStyles.cs` 的「度量：测距端点 / 单位 / 系数 / 测角内角 / 花括号」一组）；`capture-overlay-smoke` 688 条全绿（真参数行测距 7 格 / 测角 5 格 / 橡皮 1 格 + 点「画笔橡皮」后 2 格且默认 30 + 实测值置灰 / 可用 / 显示值；逐像素：花括号的尖与肩画出来而脊线是空的、测距标签没有黑底 pill 且是描边色纯文字、端点比例 2 的箭头从终点长出肩线）。
  - 新发现的两个 1:1 缺口拆成 **P2-e1**（箭头族参数）与 **P2-e2**（选中标注后切到该工具的样式族并实时生效，目前只做测距 / 测角）。上游「样式预设」实为可自定义的颜色 / 线宽 / 字号预设表（`style_presets::strokeColors()` / `strokePresetWidths()` / `sizePresetValues()`），按「设置页可自定义预设」看待，并入 P7。

- 2026-11-18：**P2-d2 完成（放大镜 / 序号 / 水印三族参数）**。三个新纯逻辑文件：`MagnifierStyle.cs`（`MagnifierSpec` + `MagnifierInk` 的镜头 / 源矩形 / 引导线 / 凸包 / 轮廓）、`SerialStyle.cs`（`SerialSpec` + `SerialLabel` 五种写法与五种徽标）、`WatermarkStyle.cs`（`WatermarkSpec` + `WatermarkTemplateText`）。
  - **放大镜**（`buildMagnifierFamily` + `magnifier.rs`）：形状（矩形 / 椭圆 / 菱形）/ 颜色 / 描边宽 / 倍率（1–10，滚轮 0.5）/ 连接方式（无 / 引导线 / 渐变）/ 源轮廓开关 / 引导线端点（14 种）+ 箭头比例（0.5–3）/ 圆角 = 9 格。**源与镜头各自独立定位**（`source_center` / `magnified_center`），镜头 = 源尺寸 × 倍率；引导线起点 = 镜头轮廓 + 半描边 + 8 DIP 空隙（`SHAPE_CONNECTOR_GAP`）、止于源中心；渐变桥 = 镜头与源轮廓的凸包减镜头，源侧透明 → 镜头侧 `0.3 × 不透明度`；源轮廓只在镜头外绘制（与上游一致）。
  - **拖动语义照 `hit_resolution.rs` / `magnifier_drag_style_tests.rs` 修正**：上一版「拖源区域只挪源中心」不符合上游。现在 ① 拖**源区域** = 整块平移（`MoveBy` 同时搬两个中心，上游 selection transform 会映射 `source_center` 与 `magnified_center`）；② 拖**镜头本体**（镜头内、源区域外）或镜头下缘 **+24 DIP** 的**移动手柄**（14×14、命中半宽 ±9，选中时画六个小点）= 只动 `magnified_center`。手柄放在镜头**外面**，所以源与镜头重合时镜头本体照样抓得到。
  - **序号**（`serial_number_format.rs`）：5 种徽标（空心 / 实心 × 圆 / 方，加「纯圆圈不写数字」，照 `supports_number()`）+ 5 种写法（阿拉伯 / 罗马 1–3999 / 小写字母 aa.. / 大写字母 AA.. / 中文，四位分组 + 万 / 亿 / 兆 / 京、跨组补零「一万零一」、空组补零「一亿零一」）+ 字号 6–256（默认 24）+ 字体 / 描边色 + 线宽 / 填充色 + 填充方式。**徽标直径 = 1.68 × 字号**（与写法无关，VIII 不会把圈撑大）、方形圆角 = 0.2 × 字号、实心徽标字色取描边色的对比色、空心徽标字色就是描边色。参数行「序号」显示**下一个号码**，落一个自动 +1（`AddSerialAt` 后刷参数行）；「纯圆圈」不写数字但照样占号。
  - **水印**（`document.rs` 的模板展开 + `SnowCanvasWatermarkConfig`）：颜色 / 文字 / 字号（12·16·24·30 预设 + 6–256 数字框）/ 字体 / 模板 / 角度（−90–90）/ 间距（10–200）/ 不透明度 = 8 格。**格距 = max(间距 + 字号 × 4, 32)**、排版行高 1.2 × 字号、整图斜向平铺（4096 块上限并成对 `PushClip` / `Pop`）。模板 `{text}` 换成当前文字、`{YYYY}{MM}{DD}{HH}{mm}{ss}` 换成此刻时间；**嵌套 `{outer-{YYYY}}` / 未闭合 `{YYYY` / 认不出的 `{foo}` 整段原样保留**（上游同样只展开认得的那几个）。模板表可增删、落 `configs/ui.json` 的 `CaptureStyles.WatermarkTemplates`。
  - **样式记忆**：三族各自独立（`StyleFamilies` 的 `serial` / `magnifier` / `watermark`），落盘往返含徽标 / 写法 / 下一个号码 / 连接方式 / 源轮廓 / 箭头比例 / 水印模板；`ApplyFamilyDefaults` 只在新建族时套默认值。上游 `snowCanvasSerialNumberType` 的 `Circle`（无数字）也一并落成 `SerialBadge.Circle`。
  - 验收：构建 0 错 0 警；`--selftest` 218 用例 / 6795 断言全绿（`capturePro` 50 用例 / 1969 断言，新增 `CaptureProSuite.SerialWatermarkMagnifier.cs` 一组：徽标尺寸矩阵 / 5×5 / 五种写法边界（含 `int.MaxValue` 读全）/ 水印格距与行高 / 模板展开 8 条 / 引导线起点 210 / 凸包 / 轮廓 / 手柄几何与三段拖动语义）；`capture-overlay-smoke` 714 条全绿（真参数行序号 9 格含五种徽标与五种写法胶囊、「序号」框显示 1 → `AddSerialAt` 后变 2 → 撤销；放大镜 9 格含连接方式三胶囊 / 源轮廓开关 / 倍率 2×；水印 8 格含模板按钮 / 12–30 字号 / 30° / 56 / 16%；真窗口里拖源 = 两个中心一起走、拖手柄与拖镜头本体 = 只动镜头、按源区域不归「只挪镜头」；选中态源轮廓 575 个蓝色像素、手柄 24 个）。

- 2026-11-18：**P2-e1 完成（箭头族参数与几何）**。新纯逻辑文件 `ArrowStyle.cs`（`ArrowType` / `ArrowShaft` / `ArrowSpec`（13 字段）/ `ArrowGeometry`），`Mark.Arrow` 从「两点 + 三角头」改成「点列 + `ArrowSpec`」。
  - **三种类型**（`arrow/data.rs`）：直线 = 两点；曲线 = Catmull-Rom（tension 1，两端 previous / next 取端点自身重复，转成三次贝塞尔）；折线 = 四点（长边中点先拐），拐角圆角 `corner = min(16, 邻边/2)`，四点折线输出 5 段 = 3 段直线 + 2 段「二次转三次」的拐角弧。
  - **端点 14 种**：形状 / 尺寸 / 张角全部沿用 P2-d1 的 `ArrowInk`；方向按类型取——曲线用 t=0.85 / 0.15 的切线，直线与折线取首末段弦；起点端点用「从第二点指向第一点」的方向，所以两端同时有箭头时互不干扰。
  - **箭头比例** 0.5–3（`normalize_arrow_ratio`），钳位在 `ArrowSpec.Clamped()`，端点长度仍受「不得长过线段」限制。
  - **杆型**（`arrow/shaft.rs` 的 `tapered_arrow_geometry`）：等宽走普通描边；渐宽 = ribbon 轮廓，半径从次箭头「肩宽 ÷ 4」线性长到主箭头「肩宽 ÷ 4」，实线 / 空心三角把箭头并进同一块轮廓，虚线 / 点线把箭头拆成独立块（`ArrowGeometry.Tapered` 返回多块 `InkPart`），不支持的端点（Bar / 圆类 / Crowfoot…）按上游 `supports_tapered_shaft` 退回等宽。
  - **外描边**（`arrow_outline_width` 0–72 + `default_arrow_outline_color` `#FFCCC7`）：普通杆用同一批路径（`ShapeInk.StrokePaths`，实线 + 非 Architect 是两遍）配「线宽 + 外描边宽」的笔先垫一遍，端点用 `ArrowInk.Draw(..., grow)` 把形状整体外扩；渐宽杆则先用外描边色把轮廓填大（填色 + 宽 `OutlineWidth` 的笔）再盖墨迹。
  - **参数行**（`buildArrowFamily`）：色 / 线宽 / 线型 / 笔触 / 类型（3 胶囊）/ 外描边宽 / 外描边色 / 箭头比例 / 起点 / 杆型（2 胶囊）/ 终点 = 11 格；出厂默认照上游 `defaults.arrow`：`#FFF5222D`、线宽 2、实线、Architect、曲线、起点无 / 终点三角、比例 1、等宽、外描边 0。
  - 验收：构建 0 错 0 警；`--selftest` 219 用例 / 6866 断言全绿（`capturePro` 51 用例 / 2040 断言，新增 `CaptureProSuite.ArrowFamily.cs` 一组：三种类型点列 / 折线五段与拐角坐标 / 曲线与折线端点方向 / 14 端点顺序 / 渐宽杆轮廓与虚线分段 / 肩宽 ÷ 4 / 命中与平移缩放 / 落盘往返）；`capture-overlay-smoke` 749 条全绿（真参数行 11 格 + 三类型与杆型胶囊 + 比例 1.0× + 外描边 0 + 起终点默认文案；逐像素：直线杆、终点三角、曲线在弦外鼓出且经过控制点、折线横竖三段、14 种端点都落在终点附近、两端同时有箭头、外描边青圈在杆外侧而墨迹盖在上面、渐宽杆末端比起点粗）。

- 2026-11-18：**P2-e2 完成（选中标注后切样式族 + 只合并变过的字段）**。上游 `snow-draw-engine-editor/src/style.rs` 的 `style_toolbar_source`（选中 → 样式来源切到 `Selected*`）+ `set_shape_style_patch`（只 merge `properties` 里的位）。
  - 新增 `Features/CapturePro/MarkStyler.cs`：`TryFamily`（18 种标注 → 族 / 代表工具，水印是整图效果、恢复块不可选，都不归族）、`Capture`（把这一条的值抄进显示样式）、`Apply`（按显示样式重造标注，**几何与抖动种子原样保留**）。`Mark.Magnifier.WithSpec` 补齐换整套画法的入口。
  - 新增 `Features/CapturePro/ToolStyleSync.cs`：`Clone` / `CopyChangedTo(target, baseline)` / `DiffersFrom`，46 个样式字段逐个手写，`SerialNumber` 计数器刻意不参与（选中一条旧序号时参数行仍显示下一个号）。
  - `StyleFamilies.ToolOf(family)` 反向映射；`ToolStyle` 改成 `partial`（`ToolStyleSync.cs` 续写）。
  - 工具栏：`_styleOverride` / `_toolOverride` / `ParamTool`，参数行改的是「显示样式」，`SyncSelection(tool, style)` 切族、`ClearSelectionStyle()` 交还当前工具；主色改动走 `SetPrimaryColor`（选中态只改副本，合并时再由 `ToolStyleSet.SetColor` 保留「共享颜色」传播）；思考气泡的显隐改按 `ParamTool` 判断。
  - 覆盖层：`SyncSelectionStyle()` 在 `SelectMark` 里刷新显示样式与基线；`ApplyStyleToSelection()` 改为「只合并变过的字段 → 整份写回这条标注 → 更新基线」；「实测值」提交也走显示样式；换工具 = 放弃选中（参数行交给新工具）。
  - 验收：构建 0 错 0 警；`--selftest` 220 用例 / 7042 断言全绿（`capturePro` 52 用例 / 2216 断言，新增 `CaptureProSuite.SelectionStyles.cs` 46 条：族反向映射、18 种标注的类型映射与逐字段往返、改一项不动几何 / 种子、`CopyChangedTo` 只搬变过字段）；`capture-overlay-smoke` 775 条全绿（选中旧箭头：参数行显示 7、创建样式仍 4、工具按钮停在「选择」；改色写回标注而创建样式只合并颜色；滚轮改线宽两处都变；取消选中参数行收起；画笔下选形状切形状族而工具不动；测距实测值按那一条自己的系数 2 显示）。
  - 上游还有「多选标注 + 混合值显示」（框选 / Shift 点选，`Mixed` 分支），拆成 **P2-e3**。

- 2026-11-18：**P2-e3 完成（多选 + 参数行「多个值」）**。上游 `snow-draw-engine-editor/src/selection.rs` 的 `toggle_selection` / `marquee_candidate_elements`（内部走 `selection_ids_intersecting_bounds`）+ `style.rs` 的 `style_toolbar_source`（选区挑族优先顺序）+ `screenshottoolpalettestylecontrols.cpp` 的 `Mixed`（`setMixed`）。
  - 新增 `Features/CapturePro/MarkMarquee.cs`（纯函数 `RectOf` / `Hits`）：与框**相交**即命中（不是包住），Shift 拖着框 = 在原有选区上加选，零面积（没拖开 / 抖动 < 0.5 DIP）不算框；**零高度 / 零宽度的标注（横竖箭头、直线）也能被框到**（给它 1 DIP 的最小厚度，否则 `Rect.IntersectsWith` 永远 false）；恢复块不进框选、水印外接框为空自然跳过。
  - `MarkStyler` 补 `TrySelectionFamily`（按上游顺序：气泡 → 画笔滤镜 → 矩形滤镜 → 马赛克 → 文字 → 序号 → 聚光 → 形状 → 矩形荧光 → 荧光笔 → 直线 → 折线 → 画笔 → 测距 → 括号 → 测角 → 箭头 → 放大镜；**主选中项在该族就用它显示，否则取该族第一个**）与 `InFamily`（批量写回只认同族）。
  - 选中模型从 `_selectedIndex` 换成 `List<int> _selected`（单选也只是它的一个元素，`_selectedIndex` 仍是主选中项）：`SelectMark` / `SelectMarks` / `ToggleMarkSelection` / `PublishSelection` / `DeleteSelectedMark`；历史往返带的是整组下标。
  - 交互（照上游 `allow_shift_toggle` + `pointer_idle.rs`）：**Shift+点标注 = 加入 / 移出**（橡皮三件套 / 水印 / 自动打码不给）、**Shift+空白拖动 = 框选**（拖不出东西 = 清空）；多选时每条一圈虚线框、**八个把手只在单选出现**、`MarkHandleAt` 同理；拖选区里任意一条 = 整组一起挪（`Previews` 代画原件，一次 `EditBatch` 一个撤销位）；Delete = 批量删除（一个撤销位）。
  - 工具栏：`SyncSelection(tool, style, peers)` 带上同族每条的显示样式；`RefreshValues()` 末尾做一次「混合值探针」——把 `_styleOverride` 临时指到 peer、重跑 refresher、逐控件比签名（TextBlock 文本 / Button 文本 / ToggleButton 勾选 / TextBox 文本），不一样的标成「多个值」（清空选中态 / 读数换字 / 输入框留空 + 占位），混合态与 tooltip 在下次刷新时还原；`MixedParts` 供冒烟断言。
  - 修好两处顺手发现的真 bug：① 自动打码「填充区域」按钮造出来后**从没加进参数行**（`FillRegions()` 少了 `host.Children.Add(button)`），界面上一直看不到它；② `RefreshMarks` 现在会重算选中显示值——否则撤销 / 批量改样式之后参数行还停在旧值（「多个值」消不掉）。
  - **已知限制**：多选**不做整体缩放 / 旋转**（八把手只在单选，上游的多选变换框不实现）；LTools 的「选择」工具在空白处无修饰键拖动仍是「拖整个选区窗口」，所以框选标注挂在 **Shift+拖**（语义 = 上游「加选框选」，但少了「无修饰键框选」这一路）。
  - 验收：构建 0 错 0 警；`--selftest` 221 用例 / 7102 断言全绿（`capturePro` 53 用例 2276 断言，新增 `CaptureProSuite.SelectionMulti.cs`：选区挑族与显示值、`InFamily`、框选相交 / 零高度箭头 / 加选 / 抖动 / 恢复块、`AllowsShiftToggle` 逐工具表）；`capture-overlay-smoke` 809 条全绿（真窗口：三条线宽 3/5/7 箭头 → 线宽显示「多个值」而颜色仍显示选中色块、单选回落 5、滚轮改一次三条一起变 8 且创建样式同步、撤销后三条各回原值并重新显示「多个值」、整组拖动三条一起走 + 一次撤销全回来、框选三条提交选中、空框加选不清空、取消不动选区、Esc 清空、批量删三条一次撤销全回来）。

- 2026-11-18：**P3-a / P3-b / P3-c 完成（选区外观 + 信息条 + 参数框与快速集）**，提交 `cc7dc3d`。
  - `Features/CapturePro/SelectionStyle.cs`：圆角 0–256、阴影与边框宽 0–64，阴影按上游截图曲线（峰值 36% + `(1-d/w)^4`），预览与导出共用一条渲染路径（输出尺寸 = 内容 + 两侧效果宽）。
  - 信息条（照 `ScreenshotSelectionToolbarWidget`）：X · Y · 锁 · 宽 × 高 · 比例下拉 · 圆角 · 效果宽；数值是**屏幕物理像素**、滚轮 ±1 px，外观长度存 DIP、显示按 `Scale` 换算；摆位四向（上方优先，放不下依次换右 / 左 / 下）。
  - 参数框 `Views/CapturePro/SelectionParamsWindow`（XYWH + 比例锁 + 圆角 + 阴影 | 边框 + 效果宽 / 色板 + 快速集增删）落 `settings.json`；比例下拉改成画在覆盖层自己的 Canvas 里——Popup 是独立 HWND，在全屏 NOACTIVATE 覆盖层上按下进了 Popup、抬起被覆盖层收走，条目点了没反应（真机复现过）。
  - Esc 口径对齐上游 `cancel_screenshot`：直接退出这次截图，逐级回退只归右键。
  - 验收：构建 0 错 0 警；`--selftest` 227 用例 7247 断言；`capture-overlay-smoke` 864 条；真机 `selection-bar-e2e` 25 条（真鼠标框选 / 真滚轮 ±1 / 真点击锁与比例预设 / 真键盘 Esc）。

- 2026-11-18：**P3-d 完成（智能选区）**。两个新纯逻辑文件 + 一个接线 partial。
  - `Features/CapturePro/SmartSelection.cs`（照 `screenshotintelligentselectionmodel.cpp`）：三档模式（`Disabled` / `WindowSubElement` / `Window`）、命中路径**从最深的元素排到整窗**（正是 `ScreenSnapTargets` 按面积升序给出的顺序）、层级偏好与 `Tab` 切换（整窗模式永远锁最后一项）、`ApplyRefinementPath` 只增不减（UIA 子树比窗口树更细）、按下期间冻结、`ClearTransientState` = 上游 `pauseIntelligentSelection`。
  - `Features/CapturePro/SmartSelectionTransition.cs`：Soft 101ms / Fast 67ms / 四边都在 0.4 DIP 内直接落位；`p*(2-p)` 缓出，起点取**当前显示中的几何**（中途改目标不跳）；按绝对时间推进，覆盖层用一个只在动画期间跑的 8ms `DispatcherTimer`（`DispatcherPriority.Render`）喂时间。
  - `Views/CapturePro/CaptureOverlayWindow.Smart.cs`：**预选就是真选区**——`_regions` / `_sel` 与手工框选完全一致，只多一个 `_smartPreviewActive` 标记；画面几何走过渡插值，主工具条与形状条收起、信息条只读、提示写明「单击确认 · 滚轮换层级 · Tab 切到整窗 · 也可直接拖动框选 · 右键退出」。单击 = 确认（转普通选区 + 暂停智能选区），拖过阈值 = 从按下点开始手框，换工具 = 把这块落成普通选区，预选态右键 = 直接退出这次截图。
  - 两个坑：① `UpdateSmartTarget` 原来用「模型当前值」判「没变就不重建」，`Tab` 之后模型已经变了、界面却没跟着变——改用 `_smartShown`（上一次真正交给过渡的目标）；② 默认档位是「窗口子元素」，覆盖层一打开光标下的窗口就是预选，离线冒烟里所有「没有预选」的老分支全红——冒烟侧统一用 `NewOverlay()` 先关档位，产品行为不改。
  - 验收：构建 0 错 0 警；`--selftest` 全量 7387 断言全绿（`capturePro` 61 用例 2561 断言，含上游 16 组智能选区用例移植）；`capture-overlay-smoke` **911 条全绿**（新增 `[smart]` 段 50 条，真鼠标单击 / 真鼠标拖框 / 真右键退出）。

- 2026-11-18：**P4-a 完成（识别结果窗 + 文字层编辑器 + 原文预览）**。上游在识别上分了四个文件：`screenshotrecognitionwindow`（窗与工具条）、`screenshotocrvisuals`（逐行估色）、`screenshotocrtextlayout`（字距 / 行距拟合）、`screenshotocrtexteditingsession`（编辑会话）。
  - `Features/CapturePro/OcrPalette.cs`（照 `screenshotocrvisuals.cpp`）：一张图整块摊平成 BGRA 数组后逐行采样。**背景** = 四条边三带 × 24 位 + 8×8 内部网格（相位错开避开笔画），平坦像素权重 ×4，4 个最高权重分箱当种子做 2 步固定半径均值漂移（OKLab 距离 ≤ 0.025），外部样本只破并列；**字色** = 外圈定噪声（四分位 > 24 直接放弃）、三像素水平滤波只用于「分族」、按「相对背景的方向」聚族取证据最多那族、再用对比度四分位提纯取代表色。估不出时退回「浅底黑字 / 深底白字」——上游用主题字色，这里会铺底色，主题字色可能正好融进去。
  - `Features/CapturePro/OcrTextLayer.cs`：行层数据（框 + 估出的字色 / 底色）+ `Build`（后台线程跑）+ `FitFontSize`（起始 = 槽高 82%，超宽等比缩到 98% 槽宽，下限 6 px）。真字体度量只在 UI 线程（`FormattedText`）。
  - `Features/CapturePro/OcrTextEditModel.cs`：逐行文本 + **跨行字符级选区** + 插入 / 替换选区 / 删除 / 退格 / Delete / 整行替换 / 全选 / 左右上下移动；撤销栈存整份行文本快照，上限 50 步（增量 diff 一旦算错就是「撤销后文字错位」，不值得）。
  - `Views/CapturePro/CaptureOcrCanvas.cs`：画布坐标 = **源图像素** × `Zoom`，所以文字与原始行框永远对得上；逐行铺底色 / 画文字 / 画选区与光标 / 悬停虚线框；`CharIndexAt` 是纯函数（二分）供 selftest 直接断言。
  - `Views/CapturePro/CaptureResultWindow.xaml(.cs)` 重写：无边框置顶、按选区物理像素 ÷ 显示器 Scale 摆位、1 源像素 ≈ 1 屏幕物理像素（装不下再等比缩）；三页签（文字 / 表格 / 二维码）+ 底栏「显示原图 / 填充背景 / 原文预览 / 复制 / 关闭」；键盘 Ctrl+A/C/X/V/Z/Y、Delete/Backspace、方向键、Enter 拆行、Esc 关窗；状态行报「共 N 行」或「已选中 N 个字符 · 共 N 行」。
  - `Views/CapturePro/CaptureOriginalPreviewWindow.cs`（照 `screenshotoriginalimagepreviewwindow.cpp`）：无边框置顶不抢焦点、100% 显示、`PlaceBeside` 先右后左再夹进工作区。
  - `CaptureRecognize.Run` 改成一张 GDI+ 位图跑三样（OCR / 二维码 / 文字层估色），不再解码三次。
  - 两处与上游的差异（写进 `docs/SCREEN_CAPTURE_PRO.md` §5）：① 上游识别窗是 Qt Tool 窗、出口挂在覆盖层工具条上，本项目的覆盖层在识别时已关闭，所以识别窗自带复制 / 关闭；② 上游是**整段**行内缩放（有段落信息），本项目 OCR 只给逐行框，所以逐行拟合字号、不做段落重排。
  - 验收：构建 0 错 0 警；`--selftest` 233 用例 **7452 断言全绿**（`capturePro` 65 用例 2626 断言）；`capture-overlay-smoke` **931 条全绿**（识别窗 21 条，含真鼠标跨行拖选与原文预览窗摆位）。

- 2026-11-18：**P4-b 完成（表格编辑器）**。上游把表格拆成三块：`screenshottabledocument`（槽位表 + 锚点，256 边 / 16384 格）、`screenshottableeditor`（QTableView + `ContiguousSelection` + `setSpan` 的交互与命令态）、`screenshotrecognitionfileexport`（xlsx 导出）。识别本身走本地 OCR 派生（`OcrTable`），编辑器部分完整复刻。
  - `Features/CapturePro/TableDocument.cs`：每格一个 `Slot`，只有锚点持有 `TableCell`（`RowSpan` / `ColumnSpan` / `Header` / `Text`），被盖住的格子指回锚点——`ExpandedRange` 就是「拖到合并格一半自动扩成整块」。`Merge` 按行优先把文字接起来（去空白、换行），`Split` 文字留在左上角，`Clear` 只清文字不动结构。`Paste` 有四个出口（`Ok` / `EmptySource` / `DoesNotFit` / `CutsMergedCell`），与上游 `pasteDocument` 一致：源里的空格**照样**清掉目的格，不是跳过。导入导出：`FromGrid`（OCR 近似表）/ `FromTsv` / `FromHtml`（`rowspan` / `colspan` / `th` / `<br>` / 嵌套表退化成换行），`ToHtml` / `ToTsv` / `ToClipboard`（HTML + 纯文本一起写，长数字加 `'` 前缀防被表格软件当科学计数）。
  - `Features/CapturePro/XlsxWriter.cs`（照 `screenshotrecognitionfileexport.cpp` 的 `tableWorkbook`）：六 part OOXML、`inlineStr`（不建 sharedStrings）、`numFmtId=49` 文本格式（前导零 / 长数字不被吃掉）、表头 `th` → 加粗样式、`mergeCells`、列宽按画布像素折成 1/256 字符宽、SpreadsheetML 转义（控制字符转 `_xHHHH_`，字面的 `_xHHHH_` 先把下划线转义）。
  - `Views/CapturePro/CaptureTableGrid.cs`：自绘 `Panel`（列宽按该列文字量算、行高固定、表头加粗、选区 / hover / 当前格三层视觉），单击 = 选中、单击不拖 = 进编辑（内嵌 `TextBox`，Enter / Tab 提交、Esc 取消），拖动 = 矩形选区（不带 Ctrl 替换、Ctrl 并集，对齐上游 `ContiguousSelection`），AnyKeyPressed 直接改成输入，方向键 / Shift+方向 / Home / End / PageUp / PageDown / Ctrl+A、右键菜单。表格文档在窗口那边，控件只通过 `CellEditRequested` / `SelectionChanged` / `CommandRequested` / `ContextMenuRequested` 回报。
  - `Views/CapturePro/CaptureResultWindow.Table.cs`：撤销 / 重做栈 50 步（存整份文档快照，与上游一致——增量 diff 算错就是「撤销后表格错位」）；`canUndo/canRedo/canMerge/canSplit/canReset/hasSelection` 一处算按钮可用性，不给「看着能点但什么都不做」的按钮；工具条 = 撤销 · 重做 · 合并 · 拆分 · 重置 | 复制 HTML · 复制 TSV · 导出 xlsx · 打开表格（最后一个照上游 `openSpreadsheetFile`：导一份临时 xlsx 用系统默认程序打开）。
  - 两个坑：① 每次编辑都换成新 `TableDocument` 实例，冒烟里缓存的引用会变成旧快照（`Doc()` 每次重取）；② `XlsxWriter` 用 `WriteStartElement(root)` + `WriteAttributeString("xmlns", …)` 会把命名空间写坏（`XmlWriter` 不认），必须用带命名空间的重载 `WriteStartElement(root, ns)`，`xml:space` 同理要给完整命名空间 URI。
  - 验收：构建 0 错 0 警；`--selftest` 237 用例 **7562 断言全绿**（`capturePro` 69 用例 2736 断言，新增槽位模型 / HTML·TSV 往返 / 粘贴规则 / xlsx 结构与转义四组）；`capture-overlay-smoke` **960 条全绿**（表格页：真鼠标单击进编辑改字、真鼠标拖选一行两格、合并 / 撤销 / 重做 / 拆分、复制双格式、重置、按钮置灰矩阵）。
