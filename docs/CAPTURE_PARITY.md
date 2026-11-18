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
| 测距 Distance | `distanceToolRequested` | `Mark.Distance`（两端刻度 + 中点 px 标签） | P1 | ✅ |
| 测角 Angle（滚轮调角度） | `screenshottoolpalette.cpp:4550` | `Mark.Angle`（两次拖拽成角，滚轮 ±5° / Shift ±1°） | P1 | ✅ |
| 括号 Bracket | `bracketToolRequested` | `Mark.Bracket`（主线 + 两端刻度） | P1 | ✅ |
| 箭头 Arrow | `arrowRequested` | `Mark.Arrow` | — | ✅ |
| 直线 Line | `lineRequested` | `Mark.Line`（可虚线；横向拖拽不会因「高度为 0」被丢） | P1 | ✅ |
| 画笔 Brush（FreeDraw） | `brushRequested` | `CaptureTool.Pen` | — | ✅ |
| 矩形高亮 RectangleHighlight | `highlightRequested` | `CaptureTool.HighlightRect` → `Mark.Highlight`（半透明铺块） | P1 | ✅ |
| 画笔高亮 PenHighlight | `brushHighlightRequested` | `CaptureTool.Highlighter` → `Mark.Stroke(highlighter: true)` | — | ✅ |
| 放大镜 Magnifier（倍率 / 连接线 / 源框） | `magnifierRequested` + `SnowCanvasMagnifierStyle` | `Mark.Magnifier`：镜头框 + 倍率（1.2–8）已 1:1；**连接线 / 箭头 / 源框轮廓 / 圆角**留 P2 | P1 / P2 | 🟡 |
| 聚光灯 Spotlight | `spotlightRequested` | `Mark.Spotlight` | — | ✅ |
| 橡皮 Eraser（点选删除） | `eraserRequested` | `CaptureTool.Eraser` | — | ✅ |
| 矩形橡皮 RectangleEraser | `rectangleEraserRequested` | `CaptureTool.RectEraser` → `Mark.Restore`：落一条**恢复原图块**（把这块像素还原成截图原样，盖掉下面的标注 / 滤镜），块本身不可选、不可删，只能撤销 | P1-b | ✅ |
| 画笔橡皮 BrushEraser | `brushEraserRequested` | `CaptureTool.BrushEraser` → `Mark.Restore`（笔迹遮罩，只有压过的那一条还原） | P1-b | ✅ |
| 自动打码 AutoFilter（视觉区域检测） | `screenshotautofiltercontroller.cpp` + `auto_filter_workflow.rs` + `auto_filter.rs` | `CaptureTool.AutoFilter`（V）+ `Features/CapturePro/AutoFilter/*`（检测 / 状态）+ `AutoFilterLogic`（命中 / 框选 / 填充事务 / 顺序归一）：进入工具即识别、扫描光带（1400ms）、结果闪红 300ms、hover 红 / 绿高亮、单击 toggle、拖动框选一批、按类型「填充区域」；新增 `FilterKind.Mosaic` | P1-c | ✅ |
| 矩形滤镜 RectangleFilter | `rectangleFilterRequested` | `Mark.Filter` + `CaptureTool.Filter` | — | ✅ |
| 画笔滤镜 BrushFilter | `brushFilterRequested` | `Mark.BrushFilter`（笔迹遮罩 + 同一套滤镜） | P1 | ✅ |
| 水印 Watermark（文本 / 模板 / 角度 / 间距 / 不透明度） | `SnowCanvasWatermark*` | `Mark.Watermark`：文字 / 颜色 / 字号 / 角度 / 间距 / 不透明度已 1:1；**图片模板水印**留 P2 | P1 / P2 | 🟡 |
| 文字 Text（富文本样式） | `textRequested` | `Mark.Text` 纯文本 | P1/P2 | 🟡 |
| 序号 SerialNumber（徽标样式 / 数字写法） | `SnowCanvasSerialNumber*` | `Mark.Serial`（形状 + 写法已实现） | P2 | 🟡 |
| 取色 Picker | `canvasColorSamplingRequested` | `CaptureTool.Picker` | — | ✅ |
| 每工具样式记忆 + 落盘 | `screenshotcanvastoolstyles.cpp`、`configs` | `ToolStyleSet`：每个工具家族各一套（`StyleFamilies`，形状 / 气泡共用 `shape` 族）+ 共享颜色开关 + 落 `configs/ui.json` 的 `CaptureStyles`（只写碰过的族） | P2-a | ✅ |
| 样式预设 / 默认样式 | `screenshotdefaultstyles.h` | 默认值已对齐（描边 `#F5222D`、填充 `#FFCCC7`、圆角 6、线宽 2、`arrowType = Curve`）；**用户可存的样式预设**留 P2-d | P2-b / P2-d | 🟡 |
| 描边色 / 描边宽 / 填充 / 虚线 | `SnowCanvasShapeStyle` | `ShapeSpec` + `ShapeInk`：形状（矩形 / 椭圆 / 菱形 / 思考气泡）、描边实 / 虚 / 点、笔触风格（干净 / 手绘 / 涂鸦，同一 seed 重绘一致）、填充无 / 实心 / 斜线 / 交叉、填充色、圆角、直线 / 曲线、画笔压感 | P2-b | ✅ |
| 字体族 / 字号 / 粗斜 / 对齐 / 透明度 | `SnowCanvasTextStyle` | 只有字号 | P2 | 🟡 |
| 滤镜类型与强度 | `SnowCanvasFilterType` | `FilterKind`：模糊 / 灰度 / 反相 / 浮雕 / 亮度 / **智能擦除**（`SmartErase.cs` 移植上游三级重建）+ 强度 | P1-b | ✅ |
| 工具栏自定义（显示哪些工具 / 顺序 / 分组） | `screenshotdrawingtoolbaroptions.h` | `ToolLayout` + `ToolLayoutStore` | — | ✅ |

### 2.2 选区（P3）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| X / Y / W / H 数值框 + px 单位 | `screenshotselectiontoolbarwidgets.cpp` | 只有角上尺寸提示 | P3 | ❌ |
| 比例锁定 + 比例预设（16:9 等） | 同上 `Lock selection aspect ratio` | 无 | P3 | ❌ |
| 圆角半径 | 同上 `Corner radius` | 无 | P3 | ❌ |
| 阴影像素宽（无边框时） / 边框宽 | 同上 `Shadow width` / `Border width` | 无（导出永远直角） | P3 | ❌ |
| 选区预设 / 上次选区 | 同上 `Selection presets` `Previous selection` | 历史窗口有上一张，不是选区几何 | P3 | 🟡 |
| 智能选区（UI 元素识别 + 过渡动画） | `core/screenshotintelligentselectionmodel.cpp`、`screenshotsmartselectiontransition.cpp` | `Views/ScreenSnapTargets` 只做吸附线 | P3 | 🟡 |
| 选区阴影渲染 | `overlay/screenshotselectionshadowrenderer.cpp` | 无 | P3 | ❌ |
| 悬停窗口 / 控件高亮与切换 | `overlay/screenshotoverlaycanvaspresenter.cpp` | `CaptureOverlayWindow.Hover.cs` | — | ✅ |

### 2.3 识别与转换（P4）

| 上游 | 位置 | LTools 现状 | 批次 | 状态 |
| --- | --- | --- | --- | --- |
| 本地 OCR（系统 / PP-OCR） | `ocr/screenshotocrcontroller.cpp` | `Features/Flow/PaddleOcr.cs` + `CaptureRecognize.cs` | — | ✅ |
| OCR 结果编辑器（选中 / 编辑 / 复制 / 替换为文本） | `screenshotocrtexteditingsession.cpp`、`screenshotocrvisuals.cpp` | 无（只把文本塞进速记 / 剪贴板） | P4 | ❌ |
| OCR 原文预览窗 | `screenshotoriginalimagepreviewwindow.cpp` | 无 | P4 | ❌ |
| 文本排版还原（行 / 段 / 字号） | `screenshotocrtextlayout.cpp`、`screenshotocrlayout.cpp` | `OcrLayout` 只有词框 | P4 | 🟡 |
| 二维码识别 + 呈现 | `screenshotqrcontroller.cpp` | `QrCodeAlgorithms.Read` 只填字段 | P4 | 🟡 |
| 表格识别 | `tableRequested`（在线服务） | 无 | P4 | ❌ |
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
| P2-c | 文字族参数：字体族 / 粗体 / 斜体 / 水平与垂直对齐 / 不透明度 / 圆角 / 填充样式 / 描边宽度 | ❌ | 待填 |
| P2-d | 序号 / 水印 / 放大镜 / 测距 / 测角 / 括号 / 橡皮的完整参数 | ❌ | 待填 |
| P3 | 选区：XYWH / 比例 / 圆角 / 阴影 / 智能选区 / 预设 | ❌ | 待填 |
| P4 | 识别与转换：OCR 编辑器 / 原文预览 / 表格 / 二维码 / LaTeX / Markdown / HTML / 文档 / 翻译 | ❌ | 待填 |
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
