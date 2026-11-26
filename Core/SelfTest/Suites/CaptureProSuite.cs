using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图 Pro 的标注模型：几何（命中 / 外接框 / 箭头）、不可变语义、撤销栈、
/// 选区换算与导出前置条件、文字量宽、反色字。
/// </summary>
/// <remarks>
/// 这里只碰纯数据与 <see cref="BitmapSource"/>，**不开窗口**：覆盖层本体与工具栏走
/// 仓库外冒烟（<c>capture-pro-smoke</c>）。
/// </remarks>
internal static partial class CaptureProSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("capturePro", "几何：矩形 / 外接框 / 折线命中", Geometry));
        cases.Add(new("capturePro", "几何：箭头三角头", ArrowHead));
        cases.Add(new("capturePro", "标注：移动返回新实例", ImmutableMarks));
        cases.Add(new("capturePro", "标注：把手命中与等比缩放", MarkScale));
        cases.Add(new("capturePro", "文档：撤销 / 重做 / 上限", UndoRedo));
        cases.Add(new("capturePro", "文档：序号撤销后不跳号", SerialNumbers));
        cases.Add(new("capturePro", "标注：序号写法与徽标字号", SerialTexts));
        cases.Add(new("capturePro", "标注：矩形滤镜与像素处理", FilterMark));
        cases.Add(new("capturePro", "文档：换底图（翻采集历史）", SwapBase));
        cases.Add(new("capturePro", "原文预览：摆位与尺寸（右 / 左 / 上下 / 内侧）", PreviewPlacement));
        cases.Add(new("capturePro", "原文预览：1:1 布局（左右 / 上下 / 退回缩放）", PreviewLayout));
        cases.Add(new("capturePro", "保存：格式 / 尺寸 / 编码", SaveFormats));
        cases.Add(new("capturePro", "文档：选区换算与导出", Selection));
        cases.Add(new("capturePro", "选区形状：自由 / 折线的几何与导出", SelectionShapes));
        cases.Add(new("capturePro", "文档：比例是「物理 ÷ DIP」", DocScale));
        cases.Add(new("capturePro", "文字：量宽随字号增长", TextMetrics));
        cases.Add(new("capturePro", "样式：底色对比取字色", Contrast));
        cases.Add(new("capturePro", "工具：目录 / 参数格 / 默认样式", Tools));
        cases.Add(new("capturePro", "标注：P1 新工具（线 / 尺寸 / 气泡 / 镜头 / 水印 / 画笔滤镜）", ParityMarks));
        cases.Add(new("capturePro", "滤镜：像素级智能擦除（仿射 / 周期 / 多尺度）", SmartEraseCases));
        cases.Add(new("capturePro", "橡皮：恢复原图块（矩形 / 画笔，不可点中）", RestoreMarks));
        cases.Add(new("capturePro", "自动打码：区域检测与上游逐区域对齐", AutoFilterDetect));
        cases.Add(new("capturePro", "自动打码：命中 / 框选 / 填充事务 / 绘制顺序", AutoFilterLogicChecks));
        cases.Add(new("capturePro", "取色：像素颜色与文本", Picker));
        cases.Add(new("capturePro", "贴图：缩放 / 不透明度 / 锚点", Pin));
        cases.Add(new("capturePro", "贴图：分组名与批量另存", PinGrouping));
        cases.Add(new("capturePro", "贴图：实时状态与裁剪", PinLiveCrop));
        cases.Add(new("capturePro", "工具栏：格子布局 / 收起 / 落盘往返", ToolLayouts));
        cases.Add(new("capturePro", "样式：每个工具家族各记一套（落盘往返）", ToolStyles));
        cases.Add(new("capturePro", "样式：形状族（形状 / 描边 / 填充 / 圆角 / 尾巴 / 圆点）", ShapeStyles));
        cases.Add(new("capturePro", "样式：箭头族（三种类型 / 14 端点 / 渐宽杆 / 外描边）", ArrowFamily));
        cases.Add(new("capturePro", "度量：测距端点 / 单位 / 系数 / 测角内角 / 花括号", MeasureStyles));
        cases.Add(new("capturePro", "样式：序号徽标 / 水印模板 / 放大镜几何", SerialWatermarkMagnifier));
        cases.Add(new("capturePro", "识别：表格聚类 / TSV / HTML", Recognize));
        cases.Add(new("capturePro", "识别：文字层像素估色（背景 / 字色 / 花底放弃）", OcrPaletteCases));
        cases.Add(new("capturePro", "识别：文字层构建与字号拟合", OcrTextLayerCases));
        cases.Add(new("capturePro", "识别：文字层编辑（选区 / 跨行 / 撤销）", OcrEditCases));
        cases.Add(new("capturePro", "识别：文字层命中（行内字符）", OcrCanvasHitCases));
        cases.Add(new("capturePro", "识别：表格文档（槽位 / 合并 / 拆分 / 清空 / 选区扩展）", TableDocumentCases));
        cases.Add(new("capturePro", "识别：表格 HTML / TSV 往返（span / 表头 / 嵌套表）", TableHtmlCases));
        cases.Add(new("capturePro", "识别：表格粘贴规则（放不下 / 切合并格）", TablePasteCases));
        cases.Add(new("capturePro", "识别：表格导出 xlsx（part / 引用 / 转义）", TableXlsxCases));
        cases.Add(new("capturePro", "识别：二维码缩放 / 网址 / 气泡落点", QrGeometryCases));
        cases.Add(new("capturePro", "识别：二维码检测（同图两枚 / 空白图）", QrDetectCases));
        cases.Add(new("capturePro", "转换：版面 → 块（标题 / 段落 / 列表 / 表格）", ConversionBlockCases));
        cases.Add(new("capturePro", "转换：Markdown / HTML 源码与转义", ConversionSourceCases));
        cases.Add(new("capturePro", "转换：文档导出（纯文本 / LaTeX / HTML / docx）", DocumentExportCases));
        cases.Add(new("capturePro", "翻译：端点 / 请求体 / 响应解析 / 语言判定", TranslationCases));
        cases.Add(new("capturePro", "识别：文字排版 / 标点转换（基线 / 智能分段 / 并集框）", TextTransformCases));
        cases.Add(new("capturePro", "翻译：版面处理 / 译文渲图（并集框 / 中位字号 / 像素）", TranslationImageCases));
        cases.Add(new("capturePro", "LaTeX：公式行识别 / 围栏 / 定界符 / 退回原文", LatexSourceCases));
        cases.Add(new("capturePro", "贴图历史：剪枝 / 缩略图 / 来源 / 摘要", PinStoreCases));
        cases.Add(new("capturePro", "贴图色卡：认 / 拒颜色 / 三行文案 / 版面 / 出场", ColorSwatchCases));
        cases.Add(new("capturePro", "贴图文字样式：范围 / 颜色 / 字体 / 快照 / 折行", PinTextStyleCases));
        cases.Add(new("capturePro", "贴图点击穿透：控件几何 / 不透明度口径", PinClickThroughCases));
        cases.Add(new("capturePro", "贴图抖动识别（移植上游 8 个用例）", PinShakeCases));
        cases.Add(new("capturePro", "贴图批量贴出 / 拖放规则 / 拖出临时文件预算", PinFileCases));
        cases.Add(new("capturePro", "贴图前后序：加 / 提 / 删 / 换组 / 观测 / 还原", PinStackingCases));
        cases.Add(new("capturePro", "采集历史贴回：外观 / 窗口矩形 / 桌面求交", HistoryPinCases));
        cases.Add(new("capturePro", "采集历史：剪枝 / 缩略图 / 文案", Catalog));
        cases.Add(new("capturePro", "采集历史：整屏底图 / 选区还原 / 整屏预览", HistoryDisplayCases));
        cases.Add(new("capturePro", "采集历史：标注落盘往返（19 种 / 逐像素）", MarkCodecCases));
        cases.Add(new("capturePro", "采集历史：来源 / 日期 / 关键词 / 分页 / 卡片文案", HistoryPageCases));
        cases.Add(new("capturePro", "快门音：音频路径 / MCI 命令 / 别名 / 缺文件不挡截图", ShutterSoundCases));
        cases.Add(new("capturePro", "贴图收顶部：手柄占位 / 命中 / 缓动 / 换屏重排", HideToTopCases));
        cases.Add(new("capturePro", "全屏画布：相机缩放 / 平移 / 视图矩阵 / 工具条摆位", GlobalCanvasLayoutCases));
        cases.Add(new("capturePro", "全屏画布：激光拖尾（流线 / 收细 / 寿命 / 上限）", GlobalCanvasLaserCases));
        cases.Add(new("capturePro", "跨屏：取景并集 / 交集", ScreenArea));
        cases.Add(new("capturePro", "长截图：滚动位移识别", ScrollDetection));
        cases.Add(new("capturePro", "长截图：拼接与面板落点", ScrollBuild));
        cases.Add(new("capturePro", "长截图：固定页脚与就地预览", ScrollSticky));
        cases.Add(new("capturePro", "长截图：横向拼接", ScrollHorizontal));
        cases.Add(new("capturePro", "长截图：自动滚动的自适应节奏", ScrollCadenceRules));
        cases.Add(new("capturePro", "长截图：真实页面形态（snow-shot 夹具）", ScrollRealPages));
        cases.Add(new("capturePro", "长截图：整页切帧回放（逐像素）", ScrollReplay));
        cases.Add(new("capturePro", "选字：候选切分", ScreenTextSplit));
        cases.Add(new("capturePro", "录屏：参数 / 命令行 / 定位", Record));
        cases.Add(new("capturePro", "录屏：暂停计时与历史文案", RecordBookkeeping));
        cases.Add(new("capturePro", "录屏：跟随鼠标（移植上游用例）", RecordFollow));
        cases.Add(new("capturePro", "录屏：空闲区域框的拖拽几何", RecordRegion));
        cases.Add(new("capturePro", "录屏：中断残片的尺寸 / 时长解析", RecordRecoveryProbe));
        cases.Add(new("capturePro", "录屏：选项 / 编码器 / 混音参数", RecordOptionsRules));
        cases.Add(new("capturePro", "录屏：设置默认值 / 质量换算 / 清晰度 / 进度条", RecordSettingsRules));
        cases.Add(new("capturePro", "录屏：特效几何 / 画中画落点", RecordEffectRules));
        cases.Add(new("capturePro", "录屏：裁剪 / 倍速 / 导出参数", RecordTrimRules));
        cases.Add(new("capturePro", "文字族：字号 / 字体 / 粗斜 / 对齐 / 描边 / 背景胶囊", TextStyles));
        cases.Add(new("capturePro", "样式：选中标注后切族并写回", SelectionStyles));
        cases.Add(new("capturePro", "多选：选区切族 / 框选命中", SelectionMulti));
        cases.Add(new("capturePro", "选区：外观 / 比例锁定 / 阴影曲线 / 导出布局", SelectionStyleRules));
        cases.Add(new("capturePro", "选区：比例锁定的拖动 / 框选几何", SelectionAspectGeometry));
        cases.Add(new("capturePro", "选区：圆角几何与命中", SelectionCornerGeometry));
        cases.Add(new("capturePro", "选区：圆角 / 边框 / 阴影的导出合成（逐像素）", SelectionExportLayout));
        cases.Add(new("capturePro", "选区：信息条摆位", SelectionBarRules));
        cases.Add(new("capturePro", "选区：参数与外观往返 / 两份效果宽保持", SelectionParamsRules));
        cases.Add(new("capturePro", "智能选区：换层过渡（上游 14 组用例）", SmartTransitionCases));
        cases.Add(new("capturePro", "智能选区：命中路径 / 细化 / Tab 切目标", SmartSelectionModelCases));
        cases.Add(new("capturePro", "图片编辑：适应 / 滚轮 / 裁剪条带几何", ImageEditGeometryCases));
        cases.Add(new("capturePro", "图片编辑：剪行 / 剪列逐像素搬运", ImageEditBitmapCases));
        cases.Add(new("capturePro", "图片编辑：裁剪 / 撤销 / 脏判定", ImageEditDocumentCases));
        cases.Add(new("capturePro", "切图：等分 / 精确像素 / 百分比 / 宫格布局", SplitCalculatorCases));
        cases.Add(new("capturePro", "切图：原始格子与重叠矩形", SplitRegionCases));
        cases.Add(new("capturePro", "切图：智能找线（避开纹理 / 取消 / 标称兜底）", SplitSmartCases));
        cases.Add(new("capturePro", "切图：加线 / 删线 / 拖线 / 撤销 / 设置切换", SplitDocumentCases));
        cases.Add(new("capturePro", "切图：表单草案与单位换算", SplitFormDraftCases));
        cases.Add(new("capturePro", "切图：导出目录命名与逐张落盘", SplitExportCases));
        cases.Add(new("capturePro", "拼图：纵向 / 横向 / 宫格布局与手摆", CollageLayoutCases));
        cases.Add(new("capturePro", "拼图：设置 / 背景色 / 几何变化", CollageSettingsCases));
        cases.Add(new("capturePro", "拼图：增删换序与宫格规整", CollageDocumentCases));
        cases.Add(new("capturePro", "拼图：成品逐像素渲染", CollageRenderCases));
        cases.Add(new("capturePro", "拼图 PDF：纸张 / 内容区 / 模板格", CollagePdfPaperCases));
        cases.Add(new("capturePro", "拼图 PDF：页数 / 校验 / 页面布局 / 合成页", CollagePdfGeometryCases));
        cases.Add(new("capturePro", "拼图 PDF：像素准备与页面绘制", CollagePdfRenderCases));
        cases.Add(new("capturePro", "拼图 PDF：导出的真实字节", CollagePdfExportCases));
        cases.Add(new("capturePro", "采集入口：延时规则与八个快捷功能", CaptureEntryCases));
        cases.Add(new("capturePro", "指针动作：取值 / 默认值 / 实时门禁 / 释放手势", PointerActionCases));
        cases.Add(new("capturePro", "贴图文字：尺寸门槛 / 阶段 / 菜单与状态条", PinOcrCases));
        cases.Add(new("capturePro", "全局快捷键：动作表 / 播种 / 读写 / 摘要 / 冲突", ShortcutCases));
        cases.Add(new("capturePro", "截图输出：文件名模板 / 清洗封顶 / 设置读写 / PNG 压缩", ExportCases));
        cases.Add(new("capturePro", "剪贴板元数据：外观 / 落位 / 桌面变化 / 文件签名", ClipboardMetaCases));
        cases.Add(new("capturePro", "渲染进度：阶段文案 / 按钮 / ffmpeg 进度解析", RecordRenderCases));
        cases.Add(new("capturePro", "录屏快捷键：动作表 / 匹配 / 状态门槛 / 区域历史", RecordShortcutCases));
        cases.Add(new("capturePro", "录屏自动收起：贴边判定 / 边条几何 / 转移区 / 门槛", RecordAutoHideCases));
        cases.Add(new("capturePro", "画中画预览：方向换算 / 拖动夹取 / 缩放 / 工具条", RecordWebcamCases));
        cases.Add(new("capturePro", "颜色预设：默认值 / 解析 / 归一 / 工具映射 / 色块", ColorPresetCases));
        cases.Add(new("capturePro", "绘制行为：工具键对照 / 清单归一 / 默认值 / 组首按钮", DrawingCases));
        cases.Add(new("capturePro", "截图行为：默认值 / 归一 / HiDPI / PDF 版面 / 光标混合 / 拖边框", BehaviorCases));
        cases.Add(new("capturePro", "截图界面与参考线：归一 / 单位 / 显隐 / 色值 / 参考线几何", OverlayUiCases));
        cases.Add(new("capturePro", "贴图窗口：归一 / 滚轮档位 / 缩放锚点 / 阴影带宽", PinWindowCases));
        cases.Add(new("capturePro", "贴图文字标注：样式 / 选区 / toggle / 载荷 / 几何", PinAnnotationCases));
        cases.Add(new("capturePro", "完成动作布局：默认 / 归一 / 往返 / 编辑模式可用集", ActionLayoutCases));
        cases.Add(new("capturePro", "文字识别设置：默认值 / 归一 / 六值分发 / 模糊核", OcrSettingsCases));
        cases.Add(new("capturePro", "截图快捷键：四张表 / 解析显示 / 归一 / 覆盖恢复 / 匹配 / 工具映射", ShortcutTableCases));
        cases.Add(new("capturePro", "采集历史策略：默认 / 归一范围 / 永久保留 / 条数 / 磁盘上限", CaptureHistoryCases));
        cases.Add(new("capturePro", "贴图历史策略：默认 / 归一范围 / 永久保留 / 磁盘上限", PinnedHistoryCases));
        cases.Add(new("capturePro", "悬浮工具条：默认布局 / 归一 / 往返 / 设置默认值", FloatingToolbarCases));
        cases.Add(new("capturePro", "选区形状：id 映射 / 化简 / 平滑曲线 / 命中 / 变换保持", RegionTypeCases));
        cases.Add(new("capturePro", "选区背景与版面压缩：夹取 / 方案 / 导出像素", SelectionExportCases));
    }

}
