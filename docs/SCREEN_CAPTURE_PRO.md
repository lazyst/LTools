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
| 6 | 标注的可编辑性 | 选中（改为当前工具）/ 拖动移动 / **八个缩放手柄改大小** / `Delete` 删除 / 文字双击改字 |
| 7 | 橡皮 | 整笔删除（删掉整个标注元素），不做像素级擦除 |
| 8 | 贴图 | P1 已落地：无激活 / 拖动 / 滚轮缩放 / 不透明度 / 置顶 / 锁定 + 贴图组登记处 + 局部显示（固定取景框里平移）+ 成组移动 |
| 9 | 识别 | P2-1 已落地：文字 + 二维码 + 本地近似表格（PP-OCRv6 词框聚类出 TSV/HTML），**不做公式与翻译** |
| 10 | 录屏 | P4 已落地：随包 ffmpeg（与 AutoHotkey 同一套：`runtime/` + `tools/fetch-*.ps1` + `licenses/`），**不录音频** |
| 11 | 采集历史 | P2，新数据根 `captures/`（`catalog.json` + `assets/` + `thumbnails/` + `records/`），进备份域白名单 |

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
  `CaptureDocument.Scale` 的方向固定是**物理像素 ÷ DIP**（125% 屏 = 1.25，`Canvas = 像素 / Scale`），
  窗口出来后由 `SyncDocumentScale` 用「`_shot.PixelWidth / ActualWidth`」校准——
  **分母是 DIP 宽、分子是像素宽**，写反了底图会被铺成 1/s² 倍大、只看得见屏幕左上角。
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
| 标注选中框 | 同一次 `OnRender` 里画：蓝色虚线框（`PaddedFrame`，外扩 3 DIP）+ 八个白把手（`ShowHandles`），不参与导出 |
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
Selected/Drawing --Esc--> Selected(取消工具) --Esc--> Idle(取消选区+标注) --Esc--> 关闭
Selected/Drawing --右键--> Idle(取消选区+标注)（覆盖层留着，可以重新框）
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

**四条几何契约**（选中框、缩放手柄都建立在上面）：

| 成员 | 含义 |
|---|---|
| `Bounds` | 外接矩形，**含描边**（命中 / 裁剪用） |
| `Frame` | 选中框用的矩形，**贴着笔迹**（描边那半圈不算）；`Shape` / `Arrow` / 折线用点列外接框，文字 / 序号 / 马赛克 / 聚光就是 `Bounds` |
| `CanScale` | 能不能拖手柄改大小；**序号徽标 `false`**（徽标直径固定，数字才看得清，只能移动） |
| `ScaleTo(from, to)` | 按「旧框 → 新框」变形：定义点经 `MarkGeometry.MapPoint` 各轴独立搬，线宽 / 字号 / 块大小乘 `MarkGeometry.ScaleOf`（两方向的几何平均，只横向拉长时线宽按 √2 而不是 ×2） |

`Mark` 仍**不可变**：`ScaleTo` 返回新实例，松手才 `ReplaceAt`，撤销栈里不留中间帧。

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
选择 | 画笔 荧光笔 | 矩形 椭圆 箭头 | 文字 序号 | 马赛克 滤镜 | 撤销 重做 | 保存 快速保存 打印 复制 ✓完成 ✕
```

**更多**（`⋯` 溢层）：折线、聚光、橡皮。

**工具参数条**（选中工具后出现在主栏上方，同一行显示，超宽才折行）：

| 工具 | 参数 |
|---|---|
| 画笔 | 颜色（8 色）、线宽（1/2/4/8/12 + 数字框） |
| 荧光笔 | 颜色、线宽、透明度（20/35/50/70） |
| 矩形 / 椭圆 / 箭头 / 折线 | 颜色、线宽、实线 / 虚线 |
| 文字 | 字号（14/20/28/36 + 数字框）、颜色、外描边（关 / 1–12）、背景（无 / 半透明） |
| 序号 | 颜色、徽标形状（圆 / 方 / 圆角方）、数字写法（1 / I / i / A / a / 一） |
| 马赛克 | 块大小（4/8/16） |
| 滤镜 | 类型（模糊 / 灰度 / 反相 / 浮雕 / 亮度）、强度（0–100） |
| 聚光 | 暗度（40/55/70） |
| 橡皮 | 无 |
| 取色（P1） | 无（色卡跟着光标显示色值） |

规则：

- 工具栏**可拖动**（按住空白处拖，位置只记在本次会话），**双击复位**到「贴选区下方居中」。
- 颜色与线宽等样式**按工具各自记住**（切回来还是上次的值），与 Quicker 的「每种工具独立继承」一致。
- 工具栏与参数条都不许挡住选区中心 40%：放不下时依次试「下方 → 上方 → 选区内侧底部」。

**P0 落地（2026-11-16）与本节的差别**（先跑通，视觉细化留给后续阶段）：

- 14 个工具（P1 加取色）**全部常驻主栏**（没有 `⋯` 溢层，放不下就由 `WrapPanel` 折行）；按钮由 `ToolCatalog.All` 生成。
- 参数条在主栏**下方**（工具在上、参数在下：贴屏幕底时参数不会顶到手腕）。
- 样式是**一份共享状态** `ToolStyle`（颜色 / 线宽 / 字号 / 块大小 / 透明度 / 徽标形状 / 数字写法 / 滤镜类型与强度），切工具沿用同一份，不按工具各记一套。
- 参数只开「这个工具真正用得到」的几格：文字 = 颜色 + 字号（外描边固定「与字色相反」的 0.75）；序号 = 颜色 + 形状 + 编号（从 1 起、删除不重排，长写法按字数收字号）；形状 = 颜色 + 线宽（虚线开关留给 P1）；马赛克 = 块大小；滤镜 = 类型 + 强度（**与马赛克分开**：马赛克是打码，滤镜是处理）；聚光 = 暗度；荧光笔 = 颜色 + 线宽 + 透明度。
- 数字格**滚轮微调、点数值复位**（照 Snow Shot：光标停在参数上滚就是调它；「调过头了」比「找不到恢复按钮」常见得多）。
- 默认落点是**屏幕底部居中**（选区可能贴着屏幕底，跟选区走会被挤到屏幕外），双击复位也回那里；用户拖过之后才按用户的位置，只在贴边时收回可见区。

## 6. 交互规格

**鼠标**

- 拖动 = 框选；松手可继续拖手柄 / 拖选区内部移动。
- 未框选时单击 = 选中光标下吸附到的窗口/控件区域。
- 滚轮 = 切换吸附层级（窗口 ↔ 内部控件），已框选时滚轮不改变选区。
- 右键 = 取消这次框选：**有选区就直接清掉**（选区 + 标注一起，覆盖层留着可以重新框），
  不用先退回选择工具——截图工具里右键就是这个意思。没有选区时才走 `Esc` 那一套逐级退出
  （折线 → 拖动中的手势 → 选中的标注 → 工具 → 退出）；框完随手右键不会把截图整个关掉。
- `Esc` = 逐级退出：折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 取消选区 → 关闭。
- 双击 = 完成（选择工具下；正在作图时双击留给折线收尾）。
- 选中画笔/形状工具后，拖 / 单击即在选区内作图；画的元素超出选区时按选区裁剪。
- 选择工具下点中已有标注 = 选中（画一圈虚线 + 八个缩放手柄）+ 可直接拖动；拖动中不往撤销栈里塞中间帧。
- 选中标注的八个手柄**优先于选区的手柄**（用户刚把它选上，这一下的意图就是改它的大小）：
  拖角 / 边按 `MarkGeometry.ResizeFrame` 只动被拖的那条边，标注按比例变形（`Mark.ScaleTo`），
  最小留 4 DIP 画布内容、夹在窗口里；光标形状与选区手柄同一套。序号徽标没有手柄（`CanScale = false`）。
- 取色工具下单击 = 把光标下的颜色复制成 `#RRGGBB`，**不落标注、不关覆盖层**（可以连着取）。
- 选区外的按下永远重新框选（任何工具下都能改框），八个手柄在任何工具下都能用。

**键盘**

| 键 | 作用 |
|---|---|
| `Esc` | 逐级退出：折线 → 拖动中的手势 → 选中的标注 → 退回选择工具 → 取消选区 → 关闭 |
| 右键 | 取消这次框选（选区 + 标注）；没有选区时按 `Esc` 那一套逐级退出 |
| `Enter` | 完成（复制）；折线进行中 = 收尾；文字输入中 = 提交（`Shift+Enter` 换行） |
| `Ctrl+C` / `Ctrl+S` | 复制 / 另存为 |
| `Ctrl+Shift+S` | 快速保存到「图片\LTools 截图」（不弹框，toast 带「打开文件夹」） |
| `Ctrl+P` | 打印（系统打印框取消 = 留在覆盖层） |
| `Ctrl+T` | 贴图（把成品钉到屏幕上并关闭覆盖层） |
| `Ctrl+R` | 识别（文字 / 表格 / 二维码，结果窗在覆盖层关掉之后开） |
| `Ctrl+Z` / `Ctrl+Y` | 撤销 / 重做 |
| `Ctrl+A` | 选整个屏幕 |
| `Delete` / `Backspace` | 删除选中的标注 |
| 方向键（`Shift` 加速） | 微调选区 1px / 10px |
| `Q P H R E A L T N M S X C F` | 选择 / 画笔 / 荧光笔 / 矩形 / 椭圆 / 箭头 / 折线 / 文字 / 序号 / 马赛克 / 聚光 / 橡皮 / 取色 / 滤镜（打字时全部让给输入框） |

## 7. 完成动作

| 出口 | 行为 |
|---|---|
| 复制（默认，`Enter` / 双击 / ✓） | 导出 png → `NativeClipboard.SetImage`（同时写 `PNG` 与 `CF_DIB`）→ 关闭 → toast「已复制」 |
| 贴图（`Ctrl+T` / 动作栏 / 工具栏图钉） | 导出 png → 关闭覆盖层 → `CapturePinManager.Pin(...)` 钉到屏幕上（正好盖住选区）；**不动剪贴板** |
| 识别（`Ctrl+R` / 动作栏 / 工具栏扫描框） | 导出 png → 关闭覆盖层 → 开识别结果窗（文字 / 表格 / 二维码，见 §10）；**不动剪贴板** |
| 长截图（`Ctrl+L` / 动作栏） | 关闭覆盖层 → 滚屏拼接（见 §12）→ 成品替换选区的图 → 复制 + toast；取消 = 什么都不要 |
| 保存（`Ctrl+S` / 另存为…） | `CaptureSave.TrySave`：另存为对话框（默认目录「图片」，文件名 `截图-yyyyMMdd-HHmmss.png`）→ 关闭；取消 = 留在覆盖层继续改 |
| 快速保存（`Ctrl+Shift+S` / 工具栏下箭头） | `CaptureSave.TryQuickSave`：不弹框，固定落「图片\LTools 截图」，同秒重名加 `-2`；toast 带「打开文件夹」。失败 = 留在覆盖层（提示已由 `CaptureSave` 发出） |
| 打印（`Ctrl+P` / 工具栏打印机） | `CaptureSave.TryPrint`：系统打印框 → 按比例缩进可打印区并居中（不裁不拉伸）；取消 = 留在覆盖层 |
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
| 拖动 | 移动窗口；至少留 40 DIP 在虚拟桌面里（拖出去也找得回来）；成组移动开着时整组一起走 |
| 滚轮 | 缩放 10%–400%，一格 10%，**锚在光标下**（缩放前后光标下的内容不动） |
| `Ctrl` / `Shift` + 滚轮 | 不透明度 20%–100%，一格 5% |
| 双击 | 关闭 |
| 悬停 | 右上角出现 ✕（**一次点击就关**）；局部显示时左下角还有一句「拖动看别处 · Ctrl 拖动移窗口」 |
| 右键 | 复制图片 / 另存为… / 缩放（100·50·200·400%）/ 不透明度（100·80·60·40%）/ 置顶 / 锁定位置 / 局部显示 / 成组移动 / 关闭 / 关闭全部贴图 |

- **不抢焦点**：`ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`（与悬浮窗同一套）。
  点它不改变前台，紧接着的键鼠注入仍打在原来的程序上。
- 代价是收不到键盘事件（`Esc` 关不掉），所以出口全部做成鼠标可达：双击 / ✕ / 右键菜单。
- 锁定 = 不能拖动、不能缩放、不能改不透明度，边框变蓝；解锁仍走右键菜单。
- 出场 100%：窗口 = 选区物理像素 ÷ 该屏缩放，正好压住刚截的那块。
- **贴图组**（`Features/CapturePro/CapturePinManager.cs`）：所有还开着的贴图的登记处——
  `Count` / `All` / `CloseAll()` / `RaiseAll()`；关窗由窗口自己 `Forget` 摘掉。
  贴图**不进配置、重启不恢复**（与悬浮项同口径：它是临时视图，不是用户资产）。
- **局部显示**：截图比屏幕还大（长截图、整页网页）时用。窗口缩成固定大小的取景框
  （不超过内容、也不超过所在显示器工作区，进出都以窗口中心为锚），图片在框里按 `PanTransform`
  平移、超出的部分裁掉；此时**拖动 = 平移内容**，按住 `Ctrl` 拖动才是移动窗口。
  滚轮只改内容缩放（锚点仍是光标下那个点），偏移由 `PinMath.ClampPan` 夹住：
  内容比框大就不许露出空白，比框小就居中。
- **成组移动**：右键菜单里的开关（默认关、只在内存、只有一张时置灰）。开着时拖任意一张，
  全组按**同一个位移**一起走，位移先按整组算一次（`PinMath.ConstrainGroupMove`），
  谁都不会被推出桌面，相对位置也不变。

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

## 11. 采集历史（P2-2 / P4）

截图 / 识别 / 录屏的落点：数据根 `captures/`（`catalog.json` + `assets/<id>.png` + `thumbnails/<id>.png`
+ 录屏的 `records/<id>.mp4|gif`），进备份域白名单（`BackupService.BuildPlan`），换机后历史还在。

- 一条 = 一次**已经完成**的采集：`Kind` = `shot`（复制 / 贴图 / 另存为）/ `recognize`（识别）/ `record`（录屏）；
  `Source` 记完成动作（复制 / 贴图 / 识别 / 另存为 / 录屏），`CreatedAt` 固定 `yyyy-MM-ddTHH:mm:ss`，
  `Width` / `Height` 是成品图（录屏是录制区域）的物理像素；录屏另有 `File`（相对 `captures/` 的
  `records/x.mp4`）与 `DurationMs`。
- 取消覆盖层不记；另存为在对话框里取消也不记（那一次没完成）；录屏在框选 / 倒计时 / 控制条上取消也不记。
- 识别条目先记一条 `State = pending`（列表显示「识别中…」），识别结果窗算完后 `SetText` 回填文字并改成
  `done`；用户提前关掉结果窗也照样回填。程序中途退出留下的 `pending` 十分钟后按「没有识别到文字」显示。
- **保留策略**：最多 200 条、最多 30 天，超出连同两张图（录屏再连带视频文件）一起删；启动时后台 `Maintain()` 一次，
  每次新增也顺带剪枝。纯函数 `PlanPrune` / `ThumbnailSize` / `FormatBytes` 由 `--selftest` 直接断言。
- 缩略图长边 240（小图不放大）；预览按宽 1600 解码，复制 / 另存为时再读原图，列表不把整张大图常驻内存。
  录屏的缩略图 / 预览是**第一帧封面**（录制时抓到的），视频本身不进内存。
- 窗口（`Views/CapturePro/CaptureHistoryWindow`）：四页签（全部 / 截图 / 识别 / 录屏）+ 搜索（150ms 防抖，
  认识别文字与类型名）+ 左列表 + 右预览 + 识别文字只读框；操作：复制图片 / 另存为… / 复制识别文字 /
  播放 / 删除 / 清空全部 / 打开目录 / 关闭。录屏条目把「播放」「另存为…」换成视频（另存为复制视频文件），
  预览信息多写时长 / 格式 / 文件大小。
  双击整行 = 复制图片（截图 / 识别）或播放（录屏），`ShouldCopyOnDoubleClick` 纯判定，滚动条 / 行内按钮 /
  表头空白都不算。
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

## 14. 录屏（P4）

入口 = 内置功能「录屏」（`common.record`，屏幕分类）：放面板 / 绑快捷键 / 悬浮都行，没有动作单元。
参数：范围（框选区域 / 主屏 / 全部屏幕 / 前台窗口）、格式（MP4 / GIF）、帧率（5–60）、倒计时（0–10 秒）。

- 一次会话（`Features/CapturePro/RecordSession.cs`，跑在后台线程；同一时间只允许一次）：
  定区域 → 倒计时 → 控制条 + 抓帧编码 → 进采集历史。框选会开覆盖层，与截图同一套三道闸
  （`TransientMinimize.MinimizeAll` / `MouseHook.SetWindowPickerActive` / `HotkeyRegistry.Suspended`），
  `finally` 成对还原。倒计时期间也让鼠标钩子让路——右键是它唯一的取消口。
- 抓帧 + 编码（`RecordEngine.cs`）：按帧率抓屏幕那一块，原始 BGRA 直接从 `pipe:0` 喂 ffmpeg
  （`libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart`，`pad` 保证宽高是偶数），
  **不落中间帧**。某帧抓慢了补写上一帧（最多 4 帧），落后太多就对齐当前时刻，成品时长与现实一致
  而不是「掉帧后跑快」。收尾关管道（EOF）再等 ffmpeg 写完容器；退出码非 0 时把 stderr 尾部当报错。
  帧源是字段（`FrameSource`）：冒烟注入合成画面，不录用户桌面。
- 控制条（`Views/CapturePro/CaptureRecordWindow`）：红点脉冲 + 计时 + 规格（格式 / 帧率 / 尺寸），
  三个鼠标出口（暂停 / 继续、完成、取消）。**不抢焦点**（`ShowActivated=False` +
  `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，录制时用户还要在别的程序里操作），并用
  `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 自己不入镜。代价是收不到 `Esc`，所以出口全是鼠标按钮。
- 暂停只停计时与抓帧（`RecordController` 用活动时钟扣掉暂停），恢复后接着录，成品里没有一段空白；
  暂停期间控制条上的计时冻住。
- 倒计时（`RecordCountdownWindow`）：盖住要录的那块区域，同样不抢焦点 / 不入镜；数到 0 自己关，
  右键或「取消」按钮放弃这一次（`OnClosing` 兜底当取消，别让会话等一个永远不结束的倒计时）。
- 成品与历史：先落 `cache/record/`（`cache/` 不进备份），成功后由 `CaptureCatalog.CreateRecord`
  **搬进** `captures/records/<id>.mp4|gif` 并记一条 `Kind = record`（带 `File` 与 `DurationMs`）；
  封面 = 第一帧，走 `assets/<id>.png` + 缩略图。历史窗口多一个「录屏」页签：预览封面 + 时长 / 尺寸 /
  格式 / 文件大小，按钮是「播放」「另存为…」（复制视频文件）「删除」，双击整行 = 播放；
  删记录 / 清空 / 剪枝都连带删视频文件。
- GIF：先录成临时 mp4，再两遍转码（`palettegen` + `paletteuse sierra2_4a`），帧率 ≤ 15、宽 ≤ 960，
  无限循环。**转码失败退回 MP4 并给一条 warning**——已经录到的东西不能因为压 GIF 失败就丢掉。
- ffmpeg 从哪来（`RecordPlan.FindFfmpeg`）：先找随包的 `runtime\ffmpeg\ffmpeg.exe`，其次 `PATH`。
  二进制**不进仓库**：`tools/fetch-ffmpeg.ps1` 下载 gyan.dev essentials（约 100 MB），
  `release.yml` 在 publish 前跑一次；csproj 用通配 `runtime\ffmpeg\**\*`（目录不存在不报错），
  许可证与源码出处见 `licenses/FFmpeg-NOTICE.txt`。两处都找不到时报错并指向那个脚本。
- 已知限制：不录音频；录制期间全局热键照常（不挂起——按快捷键的动作会被录进去）；
  `WDA_EXCLUDEFROMCAPTURE` 要 Win10 2004+，老系统上控制条 / 倒计时会入镜（录制内容本身不受影响）。

## 15. 阶段路线

| 阶段 | 模块 | 内容 |
|---|---|---|
| **P0** ✅ | ①② | 覆盖层 + 12 个标注工具 + 工具栏/参数条 + 复制/保存/关闭 + 内置功能「截图」 |
| **P1** ✅ | ③⑨ | 贴图窗口（无激活 / 拖动 / 滚轮缩放 / 不透明度 / 置顶 / 锁定 / 局部显示）+ 贴图组登记处（含成组移动）+ 取色（第 13 个工具） |
| **P2-1** ✅ | ④ | 识别结果窗（文字 / 二维码 / 本地近似表格）+ 覆盖层「识别」出口 |
| **P2-2** ✅ | ⑦ | 采集历史中心（`captures/` + 三页签 + 保留策略） |
| **P3-1** ✅ | — | 跨屏选区：覆盖层盖住所有显示器的并集，逐块抓帧拼一张冻结画面 |
| **P3-2** ✅ | ⑤ | 长截图（滚动触发 + 重叠检测 + 拼接 + 预览窗） |
| **P3-3** ✅ | ⑧ | 屏幕选字（拖选识别 + 候选条） |
| **P4** ✅ | ⑥ | 录屏（选区 / 主屏 / 全屏 / 前台窗口 + 倒计时 + 暂停继续 + MP4/GIF）+ 录屏历史 |
| **P0 补** ✅ | — | 标注的八个缩放手柄（选中后拖角 / 边改大小，按比例变形；序号只移动） |

## 16. 验收

- `--selftest` 的 `capturePro` 组：标注几何（箭头端点、折线命中、序号递增、马赛克块计算、
  聚光排除几何）、把手与缩放（八个把手位置 / 命中顺序 / 改框只动一条边 / 夹在窗口里 / 小于最小尺寸原样退回 /
  选中框外扩 3 DIP / `ScaleOf` 的几何平均与退化框 / `MapPoint` / 各标注 `ScaleTo` 的定义点与线宽字号块大小 /
  序号徽标 `CanScale = false`）、撤销栈（含重做与上限）、导出尺寸 = 选区物理像素、
  比例方向（`Scale` = 物理 ÷ DIP、`Canvas` = 像素 ÷ Scale、写反时被铺成 1.56 倍）、
  工具 ↔ 参数条映射（序号 = 形状 + 编号、滤镜 = 类型 + 强度、马赛克不带滤镜类型）、
  序号写法（罗马 / 字母 / 中文与超范围退回）与徽标字号收缩、
  矩形滤镜（框的移动 / 缩放 / 命中 + 反相 / 灰度 / 亮度 50 与 25 / 模糊不动透明度）、工具栏布局项、
  取色（BGRA 读反 / 越界 / 十六进制与 rgb 文本）、贴图（缩放与不透明度步进 / 上限 / 光标锚点 / 出场落点 /
  局部显示的内容偏移与取景框尺寸 / 成组移动的整组位移夹取）、
  识别（词框 → 表格的行 / 列聚类、中文逐字拼接、TSV / HTML、单行单列不算表格、引擎名字、位图转 GDI+）、
  采集历史（保留策略的数量 / 天数剪枝、缩略图长边与不放大、类型 / 尺寸 / 时间 / 摘要文案）、
  跨屏取景（显示器并集含负坐标 / 空表 / 空矩形；逐块抓帧的交集与贴边不算相交）、
  长截图（行位移检测：正常位移 / 小幅位移 / 没动 / 内容对不上 / 高度不一致 / 页脚不动；
  拼接只接新露的行、预览按比例缩、到高度上限自己停；面板落点右侧 → 下方 → 塞回工作区）、
  屏幕选字（按句末标点 / 逗号 / 长度上限切候选、空白与完全重复丢掉、切完字符总数不变、
  候选条落点右侧 → 下方 → 夹回窗口右下角）、
  录屏（参数解析与夹紧、MP4 的 rawvideo/bgra/尺寸/帧率/pipe:0/libx264/pad/faststart/输出末位、
  GIF 两遍与帧率 ≤ 15 / 宽 ≤ 960 / 无限循环、ffmpeg 定位的随包优先与 PATH 引号、暂停不计时、
  录屏条目的时长 / 格式 / 文件大小 / 绝对路径 / 摘要文案、录屏条目照常参与保留策略）。
- 仓库外 `capture-overlay-smoke`：真窗口里框选 → 选矩形工具画一个 → 撤销 → 导出，断言导出的像素
  （按颜色找得到矩形的描边、画之前找不到）与尺寸，并验工具栏 14 个工具按钮 / 动作栏 6 个按钮（含贴图 / 识别 / 长截图）/
  工具栏动作按钮（含快速保存 / 打印）/
  文字工具开框提交 / 清选区后收起；
  参数行（序号 = 3 格且形状 / 编号胶囊齐全、滤镜 = 2 格且五种类型齐全、数值格滚轮 +1 与点一下复位）；
  缩放比例（`doc.Scale` = 窗口像素 ÷ 窗口 DIP、冻结画面的 `Canvas` 正好铺满 `ActualWidth/ActualHeight`、
  DIP(100,100) → 像素(125,125)）；
  标注缩放手柄（合成黑底文档直接画 `CaptureCanvas`：没选中 / 没到选择工具时一个白把手都不画、选中后八个把手
  落在选中框的角与中点上、序号徽标不给手柄；真窗口里选中标注 → `ShowHandles` 打开 → 拖右下角把手后外接框
  从 200×100 变成 240×130 → 换到画标注的工具后把手收起）；
  取色色卡取到底图色块的颜色且不落标注、不关覆盖层；贴图按 100% 盖住选区、200% 与 400% 上限、
  锁定、右键菜单 12 项（含缩放 / 不透明度两个子菜单、「局部显示」「成组移动」与「关闭全部贴图」）、
  局部显示里窗口尺寸不跟缩放走且内容偏移到中间那块、悬停提示、退出后窗口回到整张图且偏移归零、
  两张贴图一起走同一个位移（成组移动）、`CloseAll` 清空贴图组；
  识别结果窗灌一份结果后：状态行 / 文字页 / 表格 2 列 2 行 / 二维码格式、切页签的显隐与复制按钮文案；
  采集历史：真写两条（截图 + 识别）后开窗，断言 catalog / 原图 / 缩略图落盘、列表 2 条、
  三页签过滤、搜索命中识别文字、预览大图与识别文字、复制按钮可用、双击判定、删除与清空后的空态；
  跨屏取景：枚举到的显示器并集 = 取景范围、真抓一帧尺寸对得上、局部抓帧是 40×30 且不是全透明、读得到缩放比；
  长截图：动作栏 6 个按钮（含「长截图」）、用合成帧驱动整套「滚一屏 → 抓帧 → 认重叠 → 拼接」到 200×900 且逐行
  对得上、取消时没有结果、面板视觉截图、面板落在选区右侧（`Left = 选区右 + 12`）；
  屏幕选字：灌一份识别结果后候选条 3 条 / 整段文字进只读框 / 条落在选区右侧并对齐顶边 / 视觉截图，
  点一条候选复制的是那一条、复制全部拿到整段、取消不算完成；
  录屏：控制条（计时归零 / 写明 320 × 240 与 30 fps / 点暂停变「继续」且计时不动 / 取消标记）+ 视觉截图；
  用合成帧真抓一小段编成 mp4（ffmpeg 解码零告警、帧数与封面帧齐），再走 `CreateRecord` 搬进
  `captures/records`：临时文件搬走、摘要「MP4 · 录屏」、历史「录屏」页签与「播放」按钮出现、
  「复制图片」「复制识别文字」收起、预览信息写时长 / 格式 / 文件大小、删记录连带删视频。
- 真机：三个入口（`screen.capture` 交互 / 快捷键 / 面板）都能唤出；`Esc` 逐级退出不卡；
  覆盖层期间按单键绑定不会触发别的动作；框选里输入的字母能正常用（不会被钩子吞）。
- 真机（P1）：点贴图不改变前台（紧接着的注入仍打在原程序上）；滚轮缩放时**光标下的内容不动**；
  `Ctrl`+滚轮改不透明度；锁定后拖不动也滚不动；双击 / ✕ / 右键菜单 / 「关闭全部贴图」四条出口都可用；
  取色复制到的 `#RRGGBB` 与色卡一致。
- 真机（缩放）：系统缩放不是 100% 时（125% / 150% / 200%）打开截图，冻结画面和桌面**1:1 对齐**——
  框选时看到的边与桌面上真实的边重合，不会只看见屏幕左上角放大的那一块；框完复制出来的图与框的一样。
- 真机（标注缩放）：选中一条标注后八个白把手出现在选中框上；拖角 / 边改大小跟手、拉成扁的就是扁的
  （线宽 / 字号跟着等比放大，不会画成歪纸条）；拖到窗口边被夹住、拖过头不会翻面；
  序号徽标没有把手（只能拖）；改完 `Ctrl+Z` 一步退回原样（拖动过程不刷撤销栈）。
- 真机（交互微调，2026-11-17）：框完选区随手右键**只取消这次框选**（连同标注一起），覆盖层还在、
  光标回到十字、能接着框；再按一次右键 / `Esc` 才关掉。光标停在右 / 下边的把手上（变成 `SizeWE` /
  `SizeNS`）按住拖动是改框，不会变成重新框选。贴图的 ✕ **点一下**就关；拖贴图时窗口紧跟光标，
  不拖影、不回弹（`Ctrl` 拖动移窗口时同理）。
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
- 真机（P4）：把「录屏」绑一个键 / 放面板，框一块区域录 10 秒：倒计时看得见但**不抢前台**，
  录制期间照常操作目标程序；控制条不出现在成品画面里；暂停 3 秒再继续，成品里那 3 秒不在了、
  计时在暂停期间不动；点「完成」后 mp4 能在系统播放器里播、时长约 10 秒、画面不卡不跳；
  「另存为」复制出来的文件能播；采集历史「录屏」页签里点「播放」能直接打开；
  换成 GIF 再录一次（≤ 15 fps、宽 ≤ 960、循环播放）；删掉那条记录后 `captures/records/` 里也没了；
  `runtime\ffmpeg\` 挪走且 PATH 里没有 ffmpeg 时给一句明确的报错。
- 真机（P0 手感层，2026-11-17）：`F` 切到滤镜，拖一块矩形后按五种类型各画一次（模糊发虚、灰度去色、
  反相变成底片、浮雕有凸起、亮度 25 变暗 / 75 变亮），强度在 0–100 之间滚轮调得动；序号切成「I / 一 / A」
  后徽标里的字放得下、切回 `1` 也正常；参数行的数值格滚轮 +/− 一格、点一下就回到默认；
  `Ctrl+Shift+S` 直接落「图片\LTools 截图」并弹出带「打开文件夹」的 toast（同秒连按两次不会覆盖）；
  `Ctrl+P` 打印框取消后覆盖层还在。
- 构建 0 错 0 警。

## 17. 坑（实现时逐条对照）

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
29. **录制控制条既不能抢焦点也不能入镜**：抢焦点会把前台换给自己（之后注入的键鼠、菜单展开状态全变），
    所以 `ShowActivated=False` + `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`；又因为它就压在录制区域上，
    必须 `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` 才不会录进成品（Win10 2004+，失败忽略）。
    同理**别在倒计时窗口里 `Activate()` / `Focus()`**——录「展开的菜单」时一抢焦点菜单就收了。
30. **时间轴靠「活动时钟」，不是墙钟**：`RecordController` 用 `Stopwatch` 记总时长、扣掉暂停段，
    抓帧循环按 `ActiveTicks` 排下一帧。直接用墙钟会让暂停的几十秒在成品里变成一段空白，
    播放器上看着像「卡住了」。
31. **抓帧慢了要补帧、但不能无限补**：一帧抓慢了补写上一帧（最多 4 帧）让时长对齐现实；
    连续落后说明机器跟不上，直接 `frame = ceil(active / ticksPerFrame)` 对齐，别补到雪崩
    （越补越慢，最后一直在追进度）。
32. **`CaptureEntry.File` 相对的是 `captures/`，不是数据根**：`FilePath` 拼 `Root`（`captures`），
    写成 `AppPaths.DataRoot` 会得到 `records/x.mp4` 这种少一层目录的路径——文件明明搬进去了却「找不到」
    （冒烟第一轮就是这么红的）。
33. **GIF 转码失败不能当整次录制失败**：mp4 已经录好了，压 GIF 失败就退回 mp4 + 一条 warning；
    反过来把临时 mp4 删掉才是真的丢东西。同理 `CreateRecord` 搬文件失败要报错并删临时文件，
    绝不能留下「有条目、没文件」的坏记录。
34. **录制期间全局热键不挂起，倒计时才挂鼠标钩子**：录制时用户可能真想按快捷键（动作也会被录进去，
    这是预期）；但倒计时的右键取消必须先 `MouseHook.SetWindowPickerActive(true)` 让「裸右键长按 = 面板」
    让路，否则那一下右键被吞掉、又注入一次，等于取消不掉。
35. **贴图的边框画在图上面，别当外层 `Border` 的 `BorderThickness`**：那边框会把内容区各缩 1px，
    局部显示里内容只比取景框大一点时右下角会多露一条空白；改成 `Grid`（`ClipToBounds`）+ 一层
    `IsHitTestVisible="False"` 的 1px `Border` 叠在 `Image` 上，图像尺寸才能和窗口严丝合缝。
36. **成组移动必须「一个位移」，不能各夹各的**：每张按自己的边界 `Clamp` 时，拖到屏幕边上会
    有的先停、有的还在走，相对位置被压扁，组就散了。`ConstrainGroupMove` 先按整组算一个允许的
    位移（取所有窗口里最紧的那条边界），再统一写回；拖动起点要快照，别在现值上累加（有误差会越拖越飘）。
37. **标注的选中框与手柄必须共用一个矩形**：选中框画 `Mark.Frame`、拉手柄也按 `Mark.Frame` 算，
    否则画出 3 DIP 的余量（`FramePad`）之后，「看到哪儿」和「按得到哪儿」会差一条边，角上尤其明显。
    另外 `Frame` 要**贴着笔迹**（`Shape` 不要用含描边的 `Bounds`），不然细线标注的把手浮在离图形很远的
    空当里。八个把手的位置 / 命中 / 改框只写一份（`MarkGeometry.Handles` / `HitFrame` / `ResizeFrame`），
    选区与标注共用——以前选区那套写在窗口里，加标注手柄时差点抄第二份。
38. **缩放是「变形」不是「重画」**：线宽 / 字号 / 块大小取 `ScaleOf` 的**两方向几何平均**，
    只横向拉长时按 √2（而不是 ×2）放大——按单轴取倍数会让斜着的文字线宽忽粗忽细；定义点必须
    `MapPoint` 各轴独立搬，否则拉成扁的会变成「在新位置重画一遍原样」，看着就是拖不动。
39. **`CaptureDocument.Scale` 只能是「物理 ÷ DIP」**，校准它时分子是**像素宽**、分母是 **DIP 宽**
    （`_shot.PixelWidth / ActualWidth`）。2026-11-17 的真机 bug 就是写反了：125% 屏上
    `doc.Scale = 0.8`，`Canvas = 像素 / 0.8` 把底图铺成 1.56 倍大，覆盖层里只看得见屏幕左上角；
    而导出「先除 Scale 再乘回 Scale」自己抵消了，所以**剪贴板里的图还是对的**——只看导出查不出这个 bug，
    冒烟必须单独断言「`doc.Scale` ≈ `窗口像素 ÷ 窗口 DIP`」且「`Canvas` 正好等于 `ActualWidth/ActualHeight`」。
40. **把手命中半径是 8 DIP，抓边常常发生在框外侧**：八个把手画在框边上，用户从外面那圈抓边时
    `Rect.Contains(p)` 是 false（`HitFrame` 仍会返回把手）。所以「把手优先」那一档**不能**再要求
    `inside`——否则光标已经变成 `SizeWE`，一按下却进了「选区外 = 重新框选」，松手时框太小被清掉，
    看着就是「按住拖动退出了截图」。判定只看 `HitFrame` 的结果（`None` / `Move` 以外就是把手）。
41. **拖动窗口的位移必须用屏幕坐标**：`e.GetPosition(this)` 是**窗口内**坐标，窗口跟着光标走时
    两者互相抵消，每帧只落下最后一个增量 → 拖起来一顿一顿地回弹（用户叫「拖影严重」）。
    位移取「现在 − 按下时」的**屏幕**位置（`GetCursorPos` × `TransformFromDevice`，与 `Left/Top` 同坐标系），
    再交给 `CapturePinManager.MoveBy`（按起点快照 + 位移写回）。取景框里的平移不用改：那时窗口不动。
    另外 `Left/Top` 落盘时 WPF 会把位置吸附到**物理像素**（250 DIP × 1.25 = 312.5 → 250.4），
    断言窗口位置时容差给到 1 DIP，别拿 0.01 卡死。
42. **窗口级 `Preview` 鼠标处理器会把按钮点成拖动**：`Window_MouseDown` 在隧道阶段先到，
    只要它 `CaptureMouse()` + `e.Handled = true`，子按钮就永远收不到 `MouseUp`、`Click` 不触发
    （贴图的 ✕ 因此点了没反应，得连点凑出一次双击才关掉）。凡是窗口级 `Preview` 处理拖动的窗口，
    都要先用 `IsOnButton(OriginalSource)` 把按钮放过去——别指望「按钮有自己的 Click」自动生效。
43. **嵌套类型的字段名不能与兄弟嵌套类型同名**：`Mark` 下已有嵌套类 `Shape`（矩形），序号再加
    `public SerialShape Shape` 就是 `CS0108`（隐藏继承成员），而且 `switch (n.Shape)` 里的 `case`
    会被当成**类型名**解析、编译直接报 `CS0572`。改名 `BadgeShape`（别用 `new` 压住警告）。
44. **滤镜和马赛克一样要按标注对象缓存**：每帧 `CroppedBitmap` + 逐像素处理在大图上必掉帧
    （`ConditionalWeakTable<Mark.Filter, …>`）。缓存键是**标注对象本身**，拖动返回新实例时自然失效。
45. **枚举型参数用文字胶囊，别用图标**：徽标形状（圆角方）与数字写法（罗马 / 中文）没有可辨识的图标，
    一排 `RadioButton`（`ChoiceChip` 样式 + 按参数各给 `GroupName`，刷新时 `_syncing` 防回环）最省地方。
    滚轮微调 / 点击复位写在 `Step` 的 host 上（参数行会重建，别挂在窗口上）。

## 18. 实施记录

- **P0 手感层（2026-11-17，按 `docs/SNOW_SHOT_REFERENCE.md` §17 的 P0 逐项复刻）**：
  ① **矩形滤镜**（新工具 `CaptureTool.Filter`，`F`）：`Mark.Filter`（A / B / 类型 / 强度）走 `MarkRenderer.DrawFilter`
  ——从底图裁这块 → `Process` 逐像素处理 → 贴回；五种类型（模糊用两趟方框模糊、灰度按亮度加权、反相、
  浮雕（左上邻居差分 + 128 偏置压灰度）、亮度（50 = 原样））。像素处理是**纯函数**，`--selftest` 直接断言方向；
  处理结果按标注对象缓存（`ConditionalWeakTable`），拖动不重算。
  **刻意不与马赛克合并**：马赛克是打码，滤镜是「处理方式可选」，合并会变成第二个「三合一动作单元」。
  ② **序号形状与编号**（`Features/CapturePro/MarkStyles.cs` 的 `SerialShape` / `SerialNumbering` / `SerialLabel`）：
  徽标圆 / 方 / 圆角方，数字写法阿拉伯 / 大小写罗马 / 大小写字母 / 中文；文案换算只有一份（`Of` / `Chip` / `Name`），
  超范围（罗马 > 3999、中文 > 9999）退回阿拉伯数字而不是留空；长写法按字数收字号（`SerialFontSize`）。
  字段名 `BadgeShape`——叫 `Shape` 会与嵌套类 `Mark.Shape` 撞（`CS0108` + `CS0572`，见 §17 坑 43）。
  ③ **参数行补齐 + 滚轮微调 / 点击复位**：枚举参数用文字胶囊（`ChoiceChip` 样式，`Chips<T>` 生成），
  数值格 `Step(...)` 在 host 上挂 `MouseWheel`（±一个步长、夹在上下限）与数值 `MouseLeftButtonUp`（复位到默认），
  tooltip 写明「滚轮微调 · 点一下复位」。
  ④ **出口层快速保存 / 打印**：`CaptureSave.TryQuickSave`（不弹框，固定落「图片\LTools 截图」，
  `UniquePath` 同秒重名加序号，toast 带「打开文件夹」）、`CaptureSave.TryPrint`（系统打印框，
  按比例缩进可打印区并居中）；覆盖层加 `FinishKind.QuickSave` / `Print` 与 `Ctrl+Shift+S` / `Ctrl+P`，
  工具栏加两个按钮（`IconQuickSave` / `IconPrint`），采集历史的完成动作写「快速保存」「打印」。
  快速保存**不读配置**（装完就能用，配置项留给后续）。
  自检补「标注：序号写法与徽标字号」「标注：矩形滤镜与像素处理」与工具参数格断言；
  冒烟补 14 个工具按钮 + 参数行（序号 3 格 / 滤镜 2 格 / 胶囊文案 / 滚轮 +1 / 点击复位）。
  待办（P0 剩余，需先解决 `CaptureDocument.Base` 不可变才能翻历史）：`, ` / `.` 翻历史、预览式保存对话框（格式 / 质量）。

- **修 · 真机四项（2026-11-17）**：
  ① **右键取消选区**：`StepBack(preferSelection: true)` 在「折线 / 手势 / 选中的标注」之后
  **优先**这一档——右键有选区就直接 `ClearSelection`（`_sel = null` + `_doc.Area = default` +
  `CaptureDocument.ClearMarks`（进撤销栈）+ 工具回选择 + 光标与悬停高亮复位），覆盖层留着重新框，再按一次才关；
  `Esc` 仍先把工具退回「选择」（提示里写着「Esc 逐级退出」），再取消选区。
  ② **贴图 ✕ 一次点击就关**：窗口级 `PreviewMouseLeftButtonDown` 原来把所有按下都当「开始拖动」，
  `CaptureMouse()` + `Handled` 让按钮永远收不到 `MouseUp` → `Click` 不触发，用户得连点凑出双击才关掉。
  现在先 `IsOnButton(OriginalSource)` 放行按钮。
  ③ **贴图拖动不再拖影**：位移原来用 `e.GetPosition(this)`（窗口内坐标），窗口跟着走时两者抵消，
  每帧只落下最后一个增量、然后回弹；改成「现在 − 按下时」的**屏幕** DIP（`CursorScreenDip`）
  交给 `DragTo` → `CapturePinManager.MoveBy`（起点快照 + 位移）。冒烟用 `DragTo` 直接断言
  「往右 100 / 下 50 → Left/Top 各加这么多、再拖回起点就回到原位」。
  ④ **按右 / 下边把手不再重新框选**：把手的命中半径是 8 DIP，用户常从框外侧那圈抓边，
  此时 `Rect.Contains` 为 false，原来「把手优先」还要求 `inside`，于是光标是 `SizeWE` 却进了
  「重选框」（见 §17 坑 40）；判定改为只看 `HitFrame`。
  自检补「把手命中含右 / 下边界」与 `ClearMarks` 的撤销语义，冒烟补 ✕ 一次点击与拖动位移两条。

- **修 · 非 100% 缩放下覆盖层画面被放大（2026-11-17 真机）**：`SyncDocumentScale` 把比例算反了
  （`ActualWidth / 像素宽` = 0.8），`CaptureDocument.Canvas = 像素 / Scale` 于是把冻结画面铺成 1.56 倍大
  （125% 屏），只能看见屏幕左上角；导出「先除 Scale 再乘回 Scale」自己抵消，所以剪贴板里的图仍然对，
  这个 bug 一直躲过了只看导出的断言。改成 `像素宽 / ActualWidth`（物理 ÷ DIP），
  并补两道回归：自检「文档：比例是「物理 ÷ DIP」」（Canvas / SelectionDip / 写反时的 1.56 倍），
  冒烟「`doc.Scale` ≈ 窗口像素 ÷ 窗口 DIP」「`Canvas` 正好铺满 `ActualWidth/ActualHeight`」
  「DIP(100,100) → 像素(125,125)」。顺带修正了长截图面板落点算的 `doc.Scale`（同一份值，之前也是反的）。
- **P0 补 · 标注的缩放手柄**（`Features/CapturePro/FrameHandle.cs`、`MarkGeometry` 的 `Handles` /
  `GripPoint` / `HitFrame` / `ResizeFrame` / `FramePad` / `PaddedFrame` / `ScaleOf` / `MapPoint` /
  `MapAll`、`Mark.Frame` / `CanScale` / `ScaleTo`；`CaptureCanvas.ShowHandles`；
  `CaptureOverlayWindow` 的 `MarkHandleAt` / `BeginMarkResize` / `UpdateMarkResize` / `CommitMarkResize` /
  `CancelMarkResize`）：选中标注后八个白把手画在选中框上（外扩 3 DIP 的 `PaddedFrame`，绘制与命中同一个矩形），
  拖角 / 边只动被拖的那条边（`ResizeFrame`：夹在窗口里、小于最小尺寸原样退回、不翻面），
  标注自己按 `ScaleTo` 做**等比 + 变形**（定义点 `MapPoint` 各轴独立、线宽字号块大小乘几何平均 `ScaleOf`）。
  拖动期间原件藏起来（`SkipIndex`）、预览代画，松手才 `ReplaceAt`，撤销栈里不留中间帧。
  **把手枚举提到 Features 层**（`FrameHandle`）、位置 / 命中 / 改框三件纯函数归 `MarkGeometry`，
  选区与标注共用一套（窗口里原来那份 `HitTest` / `GripPoint` / `ResizeFrom` 的重复实现已删）。
  序号徽标 `CanScale = false`（固定尺寸，只能移动）；把手方块边长也归一处（`MarkGeometry.HandleSize`）。
  自检补「标注：把手命中与等比缩放」（33 条断言）；冒烟补「合成黑底文档画三个状态（不选中 / 选中不画把手 /
  画把手）+ 八个把手位置 + 序号不给手柄」，真窗口里再拖一次右下角把手验外接框从 200×100 变 240×130。
- **P1 补 · 贴图的局部显示与成组移动**（`PinMath` 的 `ClampPan` / `CenterPan` / `ZoomAnchoredPan` /
  `ViewportSize` / `ConstrainGroupMove`；`CapturePinWindow.SetViewport`；
  `CapturePinManager` 的 `GroupMove` / `BeginMove` / `MoveBy` / `EndMove`）：
  **局部显示**给「截图比屏幕还大」用——窗口缩成固定取景框（不超过内容、也不超过工作区，
  进出都以窗口中心为锚），图片按 `PanTransform` 平移、超出的部分由 `Surface` 裁掉。
  此时拖动 = 平移内容（`ClampPan`：内容比框大就限制在「始终盖住框」，比框小就居中），
  **按住 `Ctrl` 拖动才是移动窗口**，悬停时左下角给一句提示；滚轮只改内容缩放，锚点仍是光标下那个点
  （`ZoomAnchoredPan`——`AnchoredOrigin` 挪窗口、它挪内容，同一件事的两个坐标空间）。
  **成组移动**是右键菜单开关（默认关、只在内存、只有一张时置灰）：拖动开始记下全组落点（`BeginMove`），
  每步按「起点 + 同一个位移」写回（`MoveBy`），位移先按**整组**夹一次（`ConstrainGroupMove`：
  谁都不许被推出桌面）。单张贴图的拖动也走这条（参与者只有自己），夹法只有一份。
  自检补「局部显示的内容偏移 / 取景框尺寸 / 缩放锚点」「成组移动的整组位移」；冒烟补取景框尺寸不跟缩放、
  内容偏移到中间那块、悬停提示、退出回整张图，以及两张贴图一起走同一个位移。
- **P4 录屏**（`Features/CapturePro/RecordPlan.cs`、`RecordEngine.cs`、`RecordController.cs`、
  `RecordSession.cs`；`Views/CapturePro/CaptureRecordWindow.xaml(.cs)`、`RecordCountdownWindow.xaml(.cs)`；
  入口是内置功能 `common.record`）：四段拆开——参数与命令行（纯函数）、抓帧编码、暂停计时、会话编排。
  抓帧原始 BGRA 走 `pipe:0` 直编 MP4（不落中间帧，慢了补帧），GIF 走两遍调色板转码（失败退回 MP4）；
  控制条与倒计时都不抢焦点、都 `WDA_EXCLUDEFROMCAPTURE` 不入镜；暂停用活动时钟扣掉，成品里没有空白段。
  成品先落 `cache/record/`，成功后搬进 `captures/records/` 并记 `Kind = record`（封面 = 第一帧）；
  历史窗口加「录屏」页签、「播放」按钮、时长 / 格式 / 文件大小，删记录连带删视频。
  ffmpeg 随包但不进仓库（`tools/fetch-ffmpeg.ps1` + `release.yml` 的 fetch 步骤 + `licenses/FFmpeg-NOTICE.txt`），
  运行时先找随包再找 PATH。
  自检补「录屏：参数 / 命令行 / 定位」「录屏：暂停计时与历史文案」；冒烟补控制条 + 合成帧真编码 + 录屏历史。
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
  局部显示与成组移动是同一次补上的（见下面 P1 补 那条）。
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
