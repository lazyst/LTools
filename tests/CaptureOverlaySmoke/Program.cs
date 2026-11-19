using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图覆盖层骨架的离线冒烟：构造真窗口（假冻结画面）→ 框选 → 看界面元素 → 导出像素。
/// **不进仓库、不进 CI**：真交互（拖鼠标 / 吸附 / 热键让路）仍靠真机点一遍。
/// </summary>
internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _failed;
    private static string _outDir = "";

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static string _only = "";

    [STAThread]
    private static int Main(string[] args)
    {
        // 中文断言文案按 UTF-8 输出（cmd 里 chcp 65001 之后读出来才是中文，不然全是 ?）
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        _only = args.FirstOrDefault() ?? "";

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // 主题：识别结果窗用的是 ModernWindow + 主题色（覆盖层 / 贴图窗都是内联样式，不受影响）。
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        int code = 0;
        app.Startup += (_, _) =>
        {
            try { code = Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            app.Shutdown(code);
        };
        app.Run();
        return code;
    }

    private static int Run()
    {
        _outDir = Path.Combine(Path.GetTempPath(), "opencode", "capture-overlay-smoke", "out");
        Directory.CreateDirectory(_outDir);

        // 贴图「出现时自动识别一次」（P9-d2）默认开着：真跑 OCR 会让冒烟变慢、还依赖系统语言包。
        // 冒烟统一关掉它，需要识别结果的段自己灌夹具（OcrFixture / ApplyHiddenOcrResult）。
        typeof(CapturePinWindow).Assembly
            .GetType("LTools.Views.CapturePro.CapturePinWindow")!
            .GetField("AutoOcrEnabled", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, false);

        // 只跑一段（调试用）：dotnet run --no-build -- split
        if (_only == "split")
        {
            SplitSection();
            Console.WriteLine(_failed == 0 ? "split-only：通过" : $"split-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "collage")
        {
            CollageSection();
            Console.WriteLine(_failed == 0 ? "collage-only：通过" : $"collage-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "collagepdf")
        {
            CollagePdfSection();
            Console.WriteLine(_failed == 0 ? "collagepdf-only：通过" : $"collagepdf-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "captureEntry")
        {
            CaptureEntrySection();
            Console.WriteLine(_failed == 0 ? "captureEntry-only：通过" : $"captureEntry-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "releaseAction")
        {
            ReleaseActionSection();
            Console.WriteLine(_failed == 0 ? "releaseAction-only：通过" : $"releaseAction-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "shortcuts")
        {
            ShortcutsSection();
            Console.WriteLine(_failed == 0 ? "shortcuts-only：通过" : $"shortcuts-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "export")
        {
            ExportSection();
            Console.WriteLine(_failed == 0 ? "export-only：通过" : $"export-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "clipboardmeta")
        {
            ClipboardMetaSection();
            Console.WriteLine(_failed == 0 ? "clipboardmeta-only：通过" : $"clipboardmeta-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "history")
        {
            HistoryPageSection();
            Console.WriteLine(_failed == 0 ? "history-only：通过" : $"history-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "sound")
        {
            SoundSection();
            Console.WriteLine(_failed == 0 ? "sound-only：通过" : $"sound-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "hidetotop")
        {
            HideToTopSection(BuildShot(320, 240));
            Console.WriteLine(_failed == 0 ? "hidetotop-only：通过" : $"hidetotop-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "pinocr")
        {
            PinOcrSection(BuildShot(320, 240));
            Console.WriteLine(_failed == 0 ? "pinocr-only：通过" : $"pinocr-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "record")
        {
            Record();
            Console.WriteLine(_failed == 0 ? "record-only：通过" : $"record-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "keys")
        {
            RecordKeysSection();
            Console.WriteLine(_failed == 0 ? "keys-only：通过" : $"keys-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "autohide")
        {
            RecordAutoHideSection();
            Console.WriteLine(_failed == 0 ? "autohide-only：通过" : $"autohide-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "render")
        {
            RecordRenderSection();
            Console.WriteLine(_failed == 0 ? "render-only：通过" : $"render-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "webcam")
        {
            RecordWebcamSection();
            Console.WriteLine(_failed == 0 ? "webcam-only：通过" : $"webcam-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "globalcanvas")
        {
            GlobalCanvasSection();
            Console.WriteLine(_failed == 0 ? "globalcanvas-only：通过" : $"globalcanvas-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "colorpresets")
        {
            ColorPresetsSection();
            Console.WriteLine(_failed == 0 ? "colorpresets-only：通过" : $"colorpresets-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "drawing")
        {
            DrawingSection();
            Console.WriteLine(_failed == 0 ? "drawing-only：通过" : $"drawing-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "behavior")
        {
            BehaviorSection();
            Console.WriteLine(_failed == 0 ? "behavior-only：通过" : $"behavior-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "overlayui")
        {
            OverlayUiSection();
            Console.WriteLine(_failed == 0 ? "overlayui-only：通过" : $"overlayui-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "pinsettings")
        {
            PinSettingsSection();
            Console.WriteLine(_failed == 0 ? "pinsettings-only：通过" : $"pinsettings-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "pinrecognition")
        {
            PinRecognitionSection(BuildShot(320, 240));
            Console.WriteLine(_failed == 0 ? "pinrecognition-only：通过" : $"pinrecognition-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "pinannotate")
        {
            PinAnnotationSection(BuildShot(320, 240));
            Console.WriteLine(_failed == 0 ? "pinannotate-only：通过" : $"pinannotate-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "actionlayout")
        {
            ActionLayoutSection(BuildShot(320, 240));
            Console.WriteLine(_failed == 0 ? "actionlayout-only：通过" : $"actionlayout-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        if (_only == "ocrsettings")
        {
            OcrSettingsSection();
            Console.WriteLine(_failed == 0 ? "ocrsettings-only：通过" : $"ocrsettings-only：{_failed} 条失败");
            return _failed == 0 ? 0 : 1;
        }

        // ============ 截图行为（P9-a：指针 / Esc 确认 / 拖框 / HiDPI / PDF 纸张） ============
        BehaviorSection();

        // ============ 截图界面与参考线（P9-c：工具条尺寸 / 单位 / 参考线 / 取色格式） ============
        OverlayUiSection();

        // ============ 贴图窗口设置（P9-d1：边框 / 阴影 / 滚轮 / 按钮 / 关闭确认） ============
        PinSettingsSection();

        // ============ 贴图识别划选与重复内容（P9-d2：隐形文字层 / 重复动作 / 抖动 / 自动缩小） ============
        PinRecognitionSection(BuildShot(320, 240));

        // ============ 贴图快速文字标注（P9-d3：划选工具条 / 样式 / 导出 / 贴回指纹） ============
        PinAnnotationSection(BuildShot(320, 240));

        // ============ 完成动作布局（P9-d4：顺序 / 分组 / 显隐 / 落盘 / 编辑窗过滤） ============
        ActionLayoutSection(BuildShot(320, 240));

        // ============ 文字识别设置（P9-e：默认值 / 落盘 / 设置卡片 / 识别后自动执行） ============
        OcrSettingsSection();

        const int width = 1600, height = 900;
        var shot = BuildShot(width, height);
        var monitor = new System.Drawing.Rectangle(0, 0, width, height);

        var window = NewOverlay(shot, monitor);
        window.Show();
        Pump(window);

        // 冒烟传的 scale 是 1，但真机 DPI 可能是 125%：PinToMonitor 会把窗口按物理像素钉住，
        // 之后的 DIP 尺寸就按真实缩放算。用窗口宽度反推本机缩放（与真实调用路径一致）。
        double scale = width / Math.Max(1, window.Width);
        Check(Math.Abs(window.Width * scale - width) < 0.5 && Math.Abs(window.Height * scale - height) < 0.5,
            $"窗口按物理像素盖住显示器：{window.Width}×{window.Height} DIP · 缩放 {scale:0.##}");

        // 冻结画面必须**正好铺满**覆盖层。doc.Scale 是「物理 ÷ DIP」（这里 1.25）；
        // 写成「DIP ÷ 物理」时底图会被铺成 1.56 倍大、只看得见屏幕左上角，
        // 而剪贴板里的图看着还是对的（导出先除 Scale 再乘回 Scale）——2026-11-17 的真机 bug。
        var firstDoc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        double docScale = (double)firstDoc.GetType().GetProperty("Scale")!.GetValue(firstDoc)!;
        Check(Math.Abs(docScale - scale) < 0.01,
            $"文档比例 = 物理 ÷ DIP：{docScale:0.###}（期望 {scale:0.###}）");

        var canvasRect = (Rect)firstDoc.GetType().GetProperty("Canvas")!.GetValue(firstDoc)!;
        Check(Math.Abs(canvasRect.Width - window.ActualWidth) < 0.6 &&
              Math.Abs(canvasRect.Height - window.ActualHeight) < 0.6,
            $"冻结画面正好铺满覆盖层：{canvasRect.Width:0.#}×{canvasRect.Height:0.#} DIP vs 窗口 " +
            $"{window.ActualWidth:0.#}×{window.ActualHeight:0.#} DIP");

        var toImage = typeof(CaptureOverlayWindow).GetMethod("ToImage", Private)!;
        var mapped = ((int X, int Y))toImage.Invoke(window, new object[] { new Point(100, 100) })!;
        Check(Math.Abs(mapped.X - 100 * scale) <= 1 && Math.Abs(mapped.Y - 100 * scale) <= 1,
            $"DIP → 冻结画面像素按同一个比例：DIP(100,100) → 像素({mapped.X},{mapped.Y})");


        var surface = Field(window, "Surface") as FrameworkElement;
        Check(surface != null, "画面元素存在");

        var hint = Field(window, "HintText") as TextBlock;
        Check(hint != null && hint.Text.Contains("拖动框选"), "初始提示教怎么开始：" + hint?.Text);

        // 模拟用户拖出一块选区，然后把界面刷新成「已框选」状态
        var selField = typeof(CaptureOverlayWindow).GetField("_sel", Private)!;
        SetSelection(window, new Rect(200, 150, 600, 400));
        Pump(window);

        Check(window.GetType().GetField("ActionBar", Private) == null,
            "没有单独的动作栏：完成动作并进工具栏同一条（照 snow-shot）");

        // 完成动作：和工具挤在**同一条**里（按钮上只有图标，文字在 ToolTip 里）
        var tips = ActionTips(window);
        Check(tips.Count >= 11, $"工具栏里有完成动作按钮齐（{tips.Count} 个：撤销 / 重做 + 9 个出口）");
        Check(tips.Any(t => t.Contains("贴图")), "工具栏有「贴图」");
        Check(tips.Any(t => t.Contains("识别")), "工具栏有「识别」");
        Check(tips.Any(t => t.Contains("长截图")), "工具栏有「长截图」");
        Check(tips.Any(t => t.Contains("实时")), "工具栏有「实时」（Ctrl+W 的实时贴图）");
        Check(tips.Any(t => t.Contains("撤销")) && tips.Any(t => t.Contains("重做")), "工具栏有撤销 / 重做");
        Check(tips.Any(t => t.Contains("复制到剪贴板")), "工具栏有 ✓ 复制（主色那个）");
        Check(tips.Any(t => t.Contains("取消")), "工具栏有 ✕ 取消");
        Check(ToolRowSingleLine(window), "工具、撤销 / 重做、完成动作都排在一行里（不再分两条）");

        // 选区按「包含两端像素」计尺寸：与框选取图（ScreenPickerWindow）同一口径
        int expectW = (int)Math.Round(800 * scale) - (int)Math.Round(200 * scale) + 1;
        int expectH = (int)Math.Round(550 * scale) - (int)Math.Round(150 * scale) + 1;
        var barFrame = Field(window, "SelBar") as FrameworkElement;
        var barW = Field(window, "SelWText") as TextBlock;
        var barH = Field(window, "SelHText") as TextBlock;
        Check(barFrame is { Visibility: Visibility.Visible }, "框选后出现选区信息条");
        Check(barW?.Text == expectW.ToString() && barH?.Text == expectH.ToString(),
            $"信息条按物理像素报宽 / 高（期望 {expectW} × {expectH}）：{barW?.Text} × {barH?.Text}");

        // 把手：8 个都应可见
        var grips = Field(window, "_grips") as System.Collections.IList;
        int visible = 0;
        if (grips != null)
            foreach (var grip in grips)
                if (grip is UIElement { Visibility: Visibility.Visible }) visible++;
        Check(visible == 8, $"八个缩放手柄都出现（实际 {visible}）");

        // 工具条跟着框走（不再钉在屏幕底部）
        ToolbarPlacement(window);

        // 导出：必须等于选区物理尺寸，而且是从文档重绘
        Invoke(window, "SyncArea");
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var image = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
        Check(image.PixelWidth == expectW && image.PixelHeight == expectH,
            $"导出尺寸 = 选区物理尺寸：{image.PixelWidth}×{image.PixelHeight}");
        Save(image, Path.Combine(_outDir, "export.png"));

        // ============ 选区信息条 + 选区参数对话框（P3-b / P3-c） ============
        SelectionBarSection(window);

        // ============ 标注工具 ============
        Annotate(window, doc, image);

        // ============ 样式记忆（每个工具家族一套） ============
        StyleMemory(window);

        // ============ 选中标注的样式族（显示这一条的值 + 只把变过的字段合并进创建样式） ============
        SelectionStyleFamily(window);

        // ============ 多选（Shift 加选 / 框选 / 多个值 / 整组拖动 / 批量删除） ============
        SelectionMulti(window);

        // ============ 文字族逐像素（背景胶囊 / 圆角 / 斜线 / 描边 / 对齐 / 粗斜 / 字体） ============
        TextFamily();

        // 界面截图（人眼看布局：工具栏 / 选区框 / 把手 / 尺寸条 / 提示 / 动作栏 / 刚画的标注）
        Save(Render(window), Path.Combine(_outDir, "overlay.png"));

        // ============ 工具栏堆叠 / 收起 ============
        ToolLayouts(window);

        // ============ 贴图 ============
        Pin(image, scale);

        // ============ 贴图分组 + 多选批量 ============
        PinGroupsAndBatch(image);

        // ============ 实时贴图 ============
        PinLive(image, scale);

        // ============ 贴图内标注（覆盖层当编辑器） ============
        AnnotateMode(image, scale);

        // ============ 识别结果窗 ============
        ResultWindow();

        // ============ 采集历史 ============
        HistoryPageSection();

        // ============ 贴图管理 ============
        PinManagerSection(image);

        PinColorSection(image, scale);

        PinTextStyleSection(scale);

        // ============ 贴图点击穿透 + 抖动隐藏同组其他贴图 ============
        PinClickThroughSection(image);

        // ============ 贴图拖放替换 / 拖出成文件（P5-f） ============
        PinDropSection(image, scale);

        // ============ 贴图收顶部（P5-i：手柄 / 悬停展开 / 右键菜单） ============
        HideToTopSection(BuildShot(320, 240));

        // ============ 贴图文字识别结果（P4-j：铺回原图 / 划选复制 / 尺寸门槛） ============
        PinOcrSection(BuildShot(320, 240));

        // ============ 贴图堆叠与层级协调（P5-g） ============
        PinStackSection(image);

        // ============ 采集历史按原选区 + 原外观贴回（P5-h） ============
        HistoryPinSection();

        // ============ 图片编辑窗（P6-a1）：装图 / 缩放 / 裁剪 / 导出 ============
        ImageEditSection();

        // ============ 切图窗（P6-b）：拖线 / 删线 / 宫格 / 重叠 / 全部保存 ============
        SplitSection();

        // ============ 图像拼接窗（P6-c）：拖动 / 把手 / 交换 / 取色 / 复制 / 保存 ============
        CollageSection();

        // ============ 拼图转 PDF（P6-d）：两套画布 / 表单可见性 / 真导出与取消 ============
        CollagePdfSection();

        // ============ 采集入口（P7-a）：pending 自动出口 / 直接截图 ============
        CaptureEntrySection();

        // ============ 指针动作 / 鼠标释放动作（P7-b）：真鼠标 ============
        ReleaseActionSection();

        // ============ 跨屏取景（ScreenShot 纯函数 + 真抓帧） ============
        ScreenArea();

        // ============ 长截图（合成帧驱动，不滚真桌面） ============
        Scroll();

        // ============ 屏幕选字 ============
        TextSelect();

        // ============ 录屏 ============
        Record();

        // ============ 录屏渲染进度（P8-c）：结构 / 状态机 / 真 ffmpeg 三条端到端 ============
        RecordRenderSection();

        // ============ 录屏快捷键（P8-d）：快照 / 指针门槛 / 按钮 / Esc 抬起 / R 翻区域 ============
        RecordKeysSection();

        // ============ 录屏工具条贴边自动收起（P8-e）：真光标收起 / 悬停唤出 / 豁免 ============
        RecordAutoHideSection();

        // ============ 录屏摄像头画中画（P8-f）：几何 / 真鼠标拖 / 缩放 / 方向 / 开录收掉 ============
        RecordWebcamSection();

        // ============ 采集历史翻页（覆盖层里 , / .） ============
        HistoryPaging(window);

        // ============ 保存对话框（格式 / 尺寸 / 文件名） ============
        SaveDialog();

        // ============ 自由 / 折线选区 ============
        SelectionShapes(window, selField, doc);

        // ============ 右键取消选区（不退出截图） ============
        StepBackClearsSelection(window, selField, doc);

        // ============ 自动打码（识别 → 高亮 → 点选 / 框选 / 填充区域） ============
        AutoFilterSection();

        // ============ 自动识别二维码（标记 / 气泡 / 复制结束截图） ============
        QrSection();

        // 没选区时工具栏应该收起来（它属于「框选之后」的东西）
        SetSelection(window, null);
        Pump(window);
        var toolbar = Field(window, "Toolbar") as FrameworkElement;
        Check(toolbar != null && toolbar.Visibility != Visibility.Visible, "清掉选区后工具栏收起");

        window.Close();

        // 空闲区域框要真鼠标拖：放在覆盖层关掉之后（覆盖层是全屏的，会把注入的点击吃掉）
        RecordRegionBox();

        // ============ 智能选区（悬停预选 / 滚轮换层 / Tab / 确认 / 手框 / 右键退出） ============
        SmartSelectionSection();

        // ============ 全局快捷键（P7-c）：播种 / 真按键 / 设置对话框 / 三个新出口 ============
        ShortcutsSection();

        // ============ 截图输出（P7-d）：模板 / 格式 / 压缩 / 自动保存 / 复制带文件 / 结果条 ============
        ExportSection();

        // ============ 剪贴板外观与落位（P7-e）：真复制 → 读回元数据 → 真贴回原位 ============
        ClipboardMetaSection();

        // ============ 相机快门音（P7-g）：默认值 / 真播放 / 设置页勾选落盘 ============
        SoundSection();

        // ============ 全屏画布（P8-a）：真窗口 / 真鼠标画 / 缩放平移 / 激光 / 穿透 / Esc ============
        GlobalCanvasSection();

        // ============ 颜色预设（P2-f）：默认值 / 落盘 / 参数条换族 / 真点色块 / 设置页 ============
        ColorPresetsSection();

        // ============ 绘制行为（P9-b）：设置页 / 清单窗口 / 组首按钮 / 自动选中 / 快速选择 ============
        DrawingSection();

        Console.WriteLine(_failed == 0 ? "capture-overlay-smoke：全部通过" : $"capture-overlay-smoke：{_failed} 条失败");
        return _failed == 0 ? 0 : 1;
    }

    private static BitmapSource BuildAutoFilterShot(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)), null,
                new Rect(0, 0, width, height));

            var ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
            var typeface = new Typeface("Consolas");

            // 标题 + 三行正文（检测会把它们当正文 / 整段文字）
            dc.DrawText(new FormattedText("Auto filter sample", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 26, ink, 1.0), new Point(70, 60));

            for (int i = 0; i < 3; i++)
                dc.DrawText(new FormattedText("The quick brown fox jumps over the lazy dog 0123456789",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 14, ink, 1.0),
                    new Point(70, 120 + i * 28));

            // 一张照片（噪声纹理，检测会当图片）
            var rng = new Random(20261118);
            for (int y = 0; y < 150; y++)
                for (int x = 0; x < 220; x += 2)
                {
                    var color = Color.FromRgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(340 + x, 60 + y, 2, 1));
                }

            // 一个消息框（浅底 + 框内文字）
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                new Pen(new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)), 2), new Rect(70, 230, 260, 90));
            dc.DrawText(new FormattedText("Message box body line", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, 14, ink, 1.0), new Point(90, 260));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 自动打码：真窗口 + 合成界面 → 进入工具就识别 → 高亮 / 点选打码 / 再点取消 /
    /// 框选一批 / 「填充区域」按类型一次填满 / 换工具清干净。
    /// </summary>
    private static void AutoFilterSection()
    {
        const int width = 1200, height = 700;
        var shot = BuildAutoFilterShot(width, height);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, width, height));
        window.Show();
        Pump(window);

        SetSelection(window, new Rect(40, 30, 620, 310));
        Invoke(window, "SyncArea");
        Pump(window);

        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var kindType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.FilterKind")!;
        var setTool = typeof(CaptureOverlayWindow).GetMethod("SetTool", Private)!;
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var docType = doc.GetType();
        var marks = (System.Collections.IList)docType.GetProperty("Marks")!.GetValue(doc)!;

        var surface = Field(window, "Surface")!;
        var toolbar = Field(window, "Toolbar")!;
        var fillButton = toolbar.GetType().GetField("_fillRegionsButton", Private)!.GetValue(toolbar) as Button;

        var icon = ((FrameworkElement)toolbar).FindResource("IconAutoFilter") as Geometry;
        Check(icon != null && !icon.IsEmpty(), "自动打码图标来自 snow-shot 的 magic-wand（手柄 + 星形 + 内孔）");
        Check(fillButton != null && !fillButton.IsEnabled,
            "没识别结果前「填充区域」是灰的（上游 available() = false）");

        // 切到自动打码：进入工具就开始识别（选区已经有内容）
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "AutoFilter") });
        Pump(window);

        var stateField = typeof(CaptureOverlayWindow).GetField("_autoFilter", Private)!;
        object state = stateField.GetValue(window)!;
        var stateType = state.GetType();
        var detecting = stateType.GetProperty("Detecting")!;

        var wait = System.Diagnostics.Stopwatch.StartNew();
        while ((bool)detecting.GetValue(state)! && wait.ElapsedMilliseconds < 30000)
        {
            System.Threading.Thread.Sleep(25);
            Pump(window);
        }

        var regions = (System.Collections.IList)stateType.GetProperty("Regions")!.GetValue(state)!;
        Check(regions.Count > 0, $"选区里识别出了区域（{regions.Count} 块，耗时 {wait.ElapsedMilliseconds} ms）");
        Check(!(bool)detecting.GetValue(state)!, "识别已经结束（不再画扫描光带）");
        Check(surface.GetType().GetProperty("AutoFilter")!.GetValue(surface) != null,
            "覆盖层拿到了识别结果（画布负责高亮 / 扫描光带 / 闪红）");
        Check(fillButton!.IsEnabled, "识别出区域后「填充区域」可以点了");
        Check(FillRegionsHostVisible(toolbar), "参数条里出现了「填充区域」下拉（只在自动打码工具下）");

        Save(Render(window), Path.Combine(_outDir, "autofilter-detect.png"));

        // 第一块区域 → 点一下 = 用当前类型（默认马赛克）打码
        object first = regions[0]!;
        var firstType = first.GetType();
        var firstBounds = (Rect)firstType.GetProperty("Bounds")!.GetValue(first)!;
        int firstId = (int)firstType.GetProperty("Id")!.GetValue(first)!;
        string firstCategory = (string)firstType.GetProperty("Category")!.GetValue(first)!;
        var center = new Point(firstBounds.X + firstBounds.Width / 2, firstBounds.Y + firstBounds.Height / 2);
        Check(firstBounds.Width > 2 && firstBounds.Height > 2, $"区域有实际大小：{firstBounds.Width:0}×{firstBounds.Height:0}");

        int undoBefore = UndoDepth(doc);
        InvokeWith(window, "BeginAutoFilterGesture", center);
        InvokeWith(window, "FinishAutoFilterGesture", center);
        ResetDrag(window);
        Pump(window);

        Check(marks.Count == 1 && FilterRegionId(marks[0]!) == firstId,
            $"点一下给那一块打码（落了一条 AutoRegionId = {firstId} 的滤镜）");
        Check(marks.Count == 1 && FilterKindOf(marks[0]!) == "Mosaic", "默认类型是马赛克（上游 FilterData 默认）");
        Check(UndoDepth(doc) == undoBefore + 1, "一次填充只占一个撤销位");

        // 打码真的落到文档里：这条滤镜正好盖住识别到的区域（导出走同一条 MarkRenderer，像素处理由
        // --selftest 的「马赛克块内取平均」守着，这里只验落位与导出图）
        var exported = (BitmapSource)docType.GetMethod("Render")!.Invoke(doc, null)!;
        var markRect = (Rect)marks[0]!.GetType().GetProperty("Rect")!.GetValue(marks[0]!)!;
        Save(exported, Path.Combine(_outDir, "autofilter-mosaic.png"));
        Check(Math.Abs(markRect.X - firstBounds.X) < 1 && Math.Abs(markRect.Y - firstBounds.Y) < 1 &&
              Math.Abs(markRect.Width - firstBounds.Width) < 1 && Math.Abs(markRect.Height - firstBounds.Height) < 1,
            $"打码正好落在识别到的区域上（{markRect.X:0},{markRect.Y:0} {markRect.Width:0}×{markRect.Height:0}）");

        // 再点一次同一块 = 取消（toggle：同类型再填一次是删掉）
        InvokeWith(window, "BeginAutoFilterGesture", center);
        InvokeWith(window, "FinishAutoFilterGesture", center);
        ResetDrag(window);
        Pump(window);
        Check(marks.Count == 0, "再点一次同一块就把这条打码删掉（toggle）");

        // 框选：从选区左上拖到右下，相交的区域一次事务全部打码
        undoBefore = UndoDepth(doc);
        InvokeWith(window, "BeginAutoFilterGesture", new Point(45, 35));
        InvokeWith(window, "UpdateAutoFilterGesture", new Point(75, 65));
        var marquee = stateType.GetProperty("Marquee")!.GetValue(state) as Rect?;
        Check(marquee is { Width: > 0 }, "拖动超过 4 DIP 就是框选（画蓝色套索）");
        InvokeWith(window, "UpdateAutoFilterGesture", new Point(660, 340));
        InvokeWith(window, "FinishAutoFilterGesture", new Point(660, 340));
        ResetDrag(window);
        Pump(window);
        Check(marquee != null && marks.Count == regions.Count,
            $"框选把相交的区域一次打码（{marks.Count} / {regions.Count} 块）");
        Check(UndoDepth(doc) == undoBefore + 1, "框选一整批也只占一个撤销位");

        // 自动填充按区域 id 升序排在一起（normalize_auto_filter_order）
        var ids = marks.Cast<object>().Select(FilterRegionId).ToList();
        Check(ids.All(id => id > 0) && ids.SequenceEqual(ids.OrderBy(id => id)),
            "自动填充按区域 id 升序排列");

        // 「填充区域」按类型一次填满：同类型同强度 = 没有变化；换成模糊后整类替换
        int before = marks.Count;
        undoBefore = UndoDepth(doc);
        InvokeWith(window, "OnAutoFilterFillRequested", firstCategory);
        Pump(window);
        Check(marks.Count == before && UndoDepth(doc) == undoBefore,
            "同类型再「填充区域」不刷撤销栈（没有变化）");

        var style = Member(window, "_style")!;
        style.GetType().GetProperty("FilterMode")!.SetValue(style, Enum.Parse(kindType, "Blur"));
        InvokeWith(window, "OnAutoFilterFillRequested", firstCategory);
        Pump(window);

        int blurred = marks.Cast<object>()
            .Count(m => FilterRegionId(m) == firstId && FilterKindOf(m) == "Blur");
        Check(blurred == 1, "换类型后「填充区域」原地替换那一块（不新增）");
        Check(marks.Count == before, "替换不是追加：条数不变");

        // 换工具：识别结果与「填充区域」一起清掉
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Select") });
        Pump(window);
        Check((System.Collections.IList)stateType.GetProperty("Regions")!.GetValue(state)! is { Count: 0 },
            "离开自动打码就把识别结果丢掉");
        Check(!fillButton.IsEnabled, "离开工具后「填充区域」又变灰");

        Save(Render(window), Path.Combine(_outDir, "autofilter-filled.png"));
        window.Close();
    }

    private static int UndoDepth(object doc) =>
        ((System.Collections.ICollection)doc.GetType().GetField("_undo", Private)!.GetValue(doc)!).Count;

    private static int FilterRegionId(object mark) =>
        (int?)mark.GetType().GetProperty("AutoRegionId")!.GetValue(mark) ?? 0;

    private static string FilterKindOf(object mark) =>
        mark.GetType().GetProperty("Kind")!.GetValue(mark)!.ToString()!;

    /// <summary>手势是直接调方法的：把 <c>_drag</c> 复位（真路径是鼠标松手复位）。</summary>
    private static void ResetDrag(CaptureOverlayWindow window)
    {
        var mode = typeof(CaptureOverlayWindow).GetNestedType("DragMode", Private)!;
        SetField(typeof(CaptureOverlayWindow), window, "_drag", Enum.ToObject(mode, 0));
    }

    /// <summary>参数条里某个参数格现在是不是可见。</summary>
    private static bool FillRegionsHostVisible(object toolbar)
    {
        var hosts = toolbar.GetType().GetField("_paramHosts", Private)!.GetValue(toolbar)
            as System.Collections.IDictionary;
        if (hosts == null) return false;

        foreach (System.Collections.DictionaryEntry entry in hosts)
        {
            if (entry.Key?.ToString() != "FillRegions") continue;
            return entry.Value is FrameworkElement { Visibility: Visibility.Visible };
        }

        return false;
    }


    /// <summary>
    /// 右键的「有选区」这一档：只取消这次框选（连标注），覆盖层留着让用户重新框。
    /// **Esc 不走这条**（上游 Esc = 直接退出这次截图）。
    /// </summary>
    private static void StepBackClearsSelection(CaptureOverlayWindow window, FieldInfo selField, object doc)
    {
        var marks = doc.GetType().GetProperty("Marks")!.GetValue(doc) as System.Collections.IList;
        int before = marks?.Count ?? 0;
        bool wasVisible = window.IsVisible;      // 这一段只验「没关掉覆盖层」，跟进来时比

        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var setTool = typeof(CaptureOverlayWindow).GetMethod("SetTool", Private)!;
        var stepBack = typeof(CaptureOverlayWindow).GetMethod("StepBack", Private)!;

        // A. 还没选区、手里是作图工具：右键先把工具退回「选择」
        //    （ClearSelection 会顺手把工具切回「选择」，所以先清选区、再进作图工具）
        Invoke(window, "CancelGesture");
        Invoke(window, "ClearMarkSelection");
        SetSelection(window, new Rect(200, 150, 600, 400));
        Invoke(window, "ClearSelection");
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Shape") });
        Pump(window);

        stepBack.Invoke(window, null);
        Check(window.IsVisible == wasVisible && selField.GetValue(window) == null &&
              Field(window, "_tool")!.ToString() == "Select",
            "没选区时右键退回选择工具，覆盖层还留着");
        Check(before == 0 || marks is not { Count: 0 }, "这一下没清标注");

        // B. 有选区：右键直接取消这次框选，连标注一起
        setTool.Invoke(window, new[] { Enum.Parse(toolType, "Shape") });
        SetSelection(window, new Rect(200, 150, 600, 400));
        Pump(window);

        stepBack.Invoke(window, null);
        Pump(window);

        Check(window.IsVisible == wasVisible, "有选区时右键只取消选区，不关覆盖层");
        Check(selField.GetValue(window) == null, "选区被清掉");
        Check(before == 0 || marks is { Count: 0 }, $"选区取消时标注一起清掉（原来 {before} 条）");

        var hint = Field(window, "HintText") as TextBlock;
        Check(hint != null && hint.Text.Contains("拖动框选"), "取消选区后提示回到「拖动框选」：" + hint?.Text);
    }

    /// <summary>
    /// 自由 / 折线选区：形状条（矩形 / 自由 / 折线）、套索松手闭合、折线逐点收尾、
    /// 切形状清选区，以及「导出按多边形裁」（形状外透明）。
    /// 纯几何在 <c>--selftest</c> 里逐条断言，这里验真窗口的界面与落盘产物。
    /// </summary>
    private static void SelectionShapes(CaptureOverlayWindow window, FieldInfo selField, object doc)
    {
        var shapeBar = Field(window, "ShapeBar") as Border;
        var shapeRect = Field(window, "ShapeRect") as RadioButton;
        var shapeLasso = Field(window, "ShapeLasso") as RadioButton;
        var shapePolyline = Field(window, "ShapePolyline") as RadioButton;
        var shapes = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.SelectionMode")!;
        var regionsField = typeof(CaptureOverlayWindow).GetField("_regions", Private)!;
        var surface = Field(window, "Surface")!;

        System.Collections.IList Regions() => (System.Collections.IList)regionsField.GetValue(window)!;

        // 1. 没选区时形状条出现，三个按钮都在，默认矩形
        Invoke(window, "ClearSelection");
        Pump(window);
        Check(Regions().Count == 0 && selField.GetValue(window) == null, "开头是「没有选区」");

        Check(shapeBar != null && shapeBar.Visibility == Visibility.Visible, "没选区时形状条出现");
        var labels = new List<string>();
        if (shapeBar?.Child is StackPanel strip)
            foreach (var child in strip.Children)
                if (child is RadioButton rb) labels.Add(rb.Content?.ToString() ?? "");
        Check(labels.SequenceEqual(new[] { "矩形", "自由", "折线" }),
            "形状条是矩形 / 自由 / 折线：" + string.Join("/", labels));
        Check(shapeRect?.IsChecked == true, "默认是矩形");
        if (shapeBar != null) Save(CropElement(window, shapeBar), Path.Combine(_outDir, "shapebar.png"));

        // 2. 套索：圈一圈松手 = 闭合的多边形选区
        InvokeWith(window, "SetSelectionShape", Enum.Parse(shapes, "Lasso"));
        Pump(window);
        Check(Field(window, "_shape")!.ToString() == "Lasso", "切到自由选区");
        Check(shapeLasso?.IsChecked == true, "「自由」按钮点亮");
        Check(shapeRect?.IsChecked == false, "「矩形」不再点亮");

        var lasso = (List<Point>)Field(window, "_lasso")!;
        lasso.Clear();
        lasso.AddRange(new[]
        {
            new Point(200, 150), new Point(700, 150), new Point(760, 300),
            new Point(700, 500), new Point(300, 520), new Point(200, 400),
        });
        Invoke(window, "CommitLasso");
        Invoke(window, "UpdateChrome");
        Pump(window);

        Check(Regions().Count == 1, $"套索松手得到 1 块选区（实际 {Regions().Count}）");
        var polygon = Regions()[0]!.GetType().GetProperty("Points")!.GetValue(Regions()[0]!) as Point[];
        Check(polygon is { Length: 6 }, $"多边形有 6 个点（实际 {polygon?.Length}）");
        var bounds = selField.GetValue(window) as Rect?;
        Check(bounds is Rect b && Math.Abs(b.Width - 560) < 1 && Math.Abs(b.Height - 370) < 1,
            "选区外接框跟点列对得上：" + bounds);
        Check(shapeBar?.Visibility == Visibility.Visible,
            "框过之后形状条仍然在（它就是「下一块的形状」）");

        var geometry = surface.GetType().GetProperty("SelectionGeometry")!.GetValue(surface) as Geometry;
        Check(geometry != null, "画面拿到合起来的几何（按形状挖洞 / 画框）");
        Save(Render(window), Path.Combine(_outDir, "lasso-overlay.png"));

        // 3. 导出按多边形裁：四角透明、中间是底图，而且内容必须与选区对得上
        //    （裁剪切曾经把几何又挪了一次选区偏移，右边一大片被裁成透明）
        Invoke(window, "SyncArea");
        var docRegions = doc.GetType().GetProperty("Regions")!.GetValue(doc) as Array;
        Check(docRegions is { Length: 1 }, $"同步到文档的是 1 块区域（{docRegions?.Length}）");
        var area = (Int32Rect)doc.GetType().GetProperty("Area")!.GetValue(doc)!;
        Check(area.Width > 600 && area.Height > 400, $"文档选区 = 点列外接框：{area.Width} × {area.Height}");

        var image = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
        Save(image, Path.Combine(_outDir, "polygon.png"));
        Check(AlphaAt(image, 0, image.PixelHeight - 1) < 40,
            $"左下角在形状外 = 透明（alpha {AlphaAt(image, 0, image.PixelHeight - 1)}）");
        Check(AlphaAt(image, image.PixelWidth - 1, image.PixelHeight - 1) < 40, "右下角在形状外 = 透明");
        Check(AlphaAt(image, image.PixelWidth / 2, image.PixelHeight / 2) > 200, "形状中间保留底图");
        Check(AlphaAt(image, 450, 230) > 200,
            $"形状内部靠右也保留底图（裁剪切不许偏移，alpha {AlphaAt(image, 450, 230)}）");

        var baseShot = (BitmapSource)doc.GetType().GetProperty("Base")!.GetValue(doc)!;
        var expected = PixelAt(baseShot, area.X + 100, area.Y + 60);
        var actual = PixelAt(image, 100, 60);
        Check(expected == actual,
            $"导出内容与选区对得上：(100,60) 期望 #{expected.R:X2}{expected.G:X2}{expected.B:X2}" +
            $" 实际 #{actual.R:X2}{actual.G:X2}{actual.B:X2}");

        // 4. 折线：逐点单击（含双击收尾）+ Enter 收尾；加区不动已有选区
        InvokeWith(window, "SetSelectionShape", Enum.Parse(shapes, "Polyline"));
        Pump(window);
        Check(Regions().Count == 1, "换形状不动已有选区（它只决定下一块画什么）");
        Check(shapePolyline?.IsChecked == true, "「折线」按钮点亮");

        InvokeWith(window, "AddSelectionPoint", new Point(300, 200), 1);
        InvokeWith(window, "AddSelectionPoint", new Point(800, 260), 1);
        InvokeWith(window, "AddSelectionPoint", new Point(600, 560), 1);
        Check((bool)Field(window, "_selPolyActive")!, "点了三个点：折线正在画");
        var pendingShape = surface.GetType().GetProperty("PendingPolygon")!.GetValue(surface) as Point[];
        Check(pendingShape is { Length: >= 3 }, "画面上有那条跟手虚线");
        Check((Geometry?)surface.GetType().GetProperty("SelectionGeometry")!.GetValue(surface) != null,
            "折线还在逐点单击时，已有选区仍留在画面上");
        Save(Render(window), Path.Combine(_outDir, "polyline-pending.png"));
        Invoke(window, "CompleteSelectionPolyline");
        Pump(window);

        Check(Regions().Count == 2, $"折线并进选区：现在 2 块（实际 {Regions().Count}）");
        var line = Regions()[1]!.GetType().GetProperty("Points")!.GetValue(Regions()[1]!) as Point[];
        Check(line is { Length: 3 }, $"折线收尾得到 3 个点（实际 {line?.Length}）");
        Check(!(bool)Field(window, "_selPolyActive")!, "收尾后不再处于「正在点」");

        // 5. 回到矩形：形状条仍常驻（框过之后也不收起）
        InvokeWith(window, "SetSelectionShape", Enum.Parse(shapes, "Rect"));
        Pump(window);
        Check(Regions().Count == 2, "换回矩形也不动已有选区");
        Check(shapeRect?.IsChecked == true, "「矩形」重新点亮");
        Check(shapeBar?.Visibility == Visibility.Visible, "有选区时形状条也在");

        Invoke(window, "ClearSelection");
        Pump(window);
        Check(Regions().Count == 0 && selField.GetValue(window) == null, "清掉选区后没有区域");
        Check(shapeBar?.Visibility == Visibility.Visible, "清掉选区后形状条还在");
        Check((Geometry?)surface.GetType().GetProperty("SelectionGeometry")!.GetValue(surface) == null,
            "画面不再有选区几何");

        // 6. 多区域：加区 / 减区
        MultiRegion(window, doc);
    }

    /// <summary>
    /// 多区域选区：加区并、减区挖、挖空的那一块不并；尺寸条 / 提示报「N 块」；
    /// 导出按合起来的几何裁（两块加区都在、洞透明、内容与底图对得上）。
    /// </summary>
    private static void MultiRegion(CaptureOverlayWindow window, object doc)
    {
        var type = typeof(CaptureOverlayWindow);
        var regionsField = type.GetField("_regions", Private)!;
        var selField = type.GetField("_sel", Private)!;
        var modeField = type.GetField("_mode", Private)!;
        var setType = type.Assembly.GetType("LTools.Features.CapturePro.SelectionSet")!;
        var contains = setType.GetMethod("Contains")!;
        var modes = type.Assembly.GetType("LTools.Features.CapturePro.RegionMode")!;

        System.Collections.IList Regions() => (System.Collections.IList)regionsField.GetValue(window)!;
        bool Inside(Point p) => (bool)contains.Invoke(null, new object[] { regionsField.GetValue(window)!, p, 0d })!;

        // 第一块加区（整块换掉旧的）→ 第二块加区 → 第二块上挖一个洞
        InvokeWith(window, "SetSingleRect", new Rect(100, 100, 300, 200));
        modeField.SetValue(window, Enum.Parse(modes, "Add"));
        Check((bool)InvokeValue(window, "CommitBox", new Rect(500, 120, 240, 160))!, "第二块加区并进来");
        modeField.SetValue(window, Enum.Parse(modes, "Subtract"));
        Check((bool)InvokeValue(window, "CommitBox", new Rect(560, 160, 120, 80))!, "减区并进来");
        Invoke(window, "UpdateChrome");
        Pump(window);

        Check(Regions().Count == 3, $"两块加区 + 一块减区（实际 {Regions().Count}）");
        var bounds = (Rect?)selField.GetValue(window);
        Check(bounds is Rect box && Math.Abs(box.X - 100) < 0.5 && Math.Abs(box.Width - 640) < 0.5,
            "外接框只并加区：" + bounds);

        Check(Inside(new Point(150, 150)), "第一块加区里 = 在选区里");
        Check(Inside(new Point(600, 130)), "第二块加区里 = 在选区里");
        Check(!Inside(new Point(600, 200)), "减区挖掉的洞里 = 不在选区里");
        Check(!Inside(new Point(460, 150)), "两块加区之间的空隙 = 不在选区里");

        var hint = Field(window, "HintText") as TextBlock;
        Check(hint != null && hint.Text.Contains("Shift 拖 = 加区") && hint.Text.Contains("Alt 拖 = 减区"),
            "提示写明加区 / 减区手势");
        Save(Render(window), Path.Combine(_outDir, "multi-region-overlay.png"));

        // 减区把选区整个挖空：这一块不并，原样退回
        Check(!(bool)InvokeValue(window, "CommitBox", new Rect(80, 80, 700, 260))!, "挖空的减区不并进去");
        Check(Regions().Count == 3, "挖空的那一块没进选区");
        modeField.SetValue(window, Enum.Parse(modes, "Add"));

        // 导出：两块加区都在、洞透明，内容与底图对得上
        Invoke(window, "SyncArea");
        var area = (Int32Rect)doc.GetType().GetProperty("Area")!.GetValue(doc)!;
        var docRegions = doc.GetType().GetProperty("Regions")!.GetValue(doc) as Array;
        Check(docRegions is { Length: 3 }, $"文档里也是 3 块（{docRegions?.Length}）");
        Check(area.Width > 600 && area.Height > 180, $"外接框 = 两块加区的并集：{area.Width} × {area.Height}");

        var multiW = Field(window, "SelWText") as TextBlock;
        var multiH = Field(window, "SelHText") as TextBlock;
        Check(multiW?.Text == area.Width.ToString() && multiH?.Text == area.Height.ToString(),
            $"多块选区的信息条报外接框（期望 {area.Width} × {area.Height}）：{multiW?.Text} × {multiH?.Text}");
        var radiusField = Field(window, "SelFieldR") as FrameworkElement;
        Check(radiusField != null && radiusField.Opacity < 0.6,
            "多块选区时圆角字段变暗（上游 m_cornerRadiusApplicable = false）");

        var image = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
        Save(image, Path.Combine(_outDir, "multi-region.png"));

        double scale = (double)doc.GetType().GetProperty("Scale")!.GetValue(doc)!;
        int OutX(double dip) => (int)Math.Round(dip * scale) - area.X;
        int OutY(double dip) => (int)Math.Round(dip * scale) - area.Y;

        Check(AlphaAt(image, OutX(150), OutY(150)) > 200, "导出：第一块加区里保留底图");
        Check(AlphaAt(image, OutX(600), OutY(130)) > 200, "导出：第二块加区里保留底图");
        Check(AlphaAt(image, OutX(600), OutY(200)) < 40,
            $"导出：减区挖出来的洞里透明（alpha {AlphaAt(image, OutX(600), OutY(200))}）");
        Check(AlphaAt(image, OutX(460), OutY(150)) < 40, "导出：两块之间的空隙透明");

        var baseShot = (BitmapSource)doc.GetType().GetProperty("Base")!.GetValue(doc)!;
        var expected = PixelAt(baseShot, area.X + OutX(600), area.Y + OutY(130));
        var actual = PixelAt(image, OutX(600), OutY(130));
        Check(expected == actual,
            $"导出：第二块加区的内容与底图对得上（期望 #{expected.R:X2}{expected.G:X2}{expected.B:X2}" +
            $" 实际 #{actual.R:X2}{actual.G:X2}{actual.B:X2}）");
    }

    /// <summary>导出图某个像素的 alpha（Bgra32）。</summary>
    private static int AlphaAt(BitmapSource image, int x, int y)
    {
        var conv = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        conv.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return one[3];
    }

    /// <summary>某个像素的颜色（Bgra32）。</summary>
    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var conv = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        conv.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return Color.FromArgb(one[3], one[2], one[1], one[0]);
    }

    /// <summary>
    /// 直接摆一块矩形选区（绕开鼠标手势）：清掉所有区域再按真实落点逻辑加一块。
    /// **别直接写 <c>_sel</c>**：选区现在是多块区域算出来的外接框，只写它会立刻被重算冲掉。
    /// </summary>
    /// <summary>
    /// 造一个覆盖层并把智能选区关掉（P3-d 起它默认是「窗口子元素」：窗口一打开，光标下的窗口
    /// 就已经是预选，工具栏 / 形状条都收着）。除了 <see cref="SmartSelectionSection"/> 之外，
    /// 各支验的都是「没有预选」的老行为，所以统一从这里造。
    /// </summary>
    private static CaptureOverlayWindow NewOverlay(BitmapSource shot, System.Drawing.Rectangle monitor)
    {
        var overlay = new CaptureOverlayWindow(shot, monitor, 1, 1);
        var smart = typeof(CaptureOverlayWindow).GetField("_smart", Private)!.GetValue(overlay)!;
        var mode = typeof(CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.CapturePro.SmartSelectionMode")!;
        smart.GetType().GetMethod("BeginSession")!.Invoke(smart, new[] { Enum.Parse(mode, "Disabled") });
        return overlay;
    }

    private static void SetSelection(CaptureOverlayWindow window, Rect? rect)
    {
        var regions = (System.Collections.IList)typeof(CaptureOverlayWindow)
            .GetField("_regions", Private)!.GetValue(window)!;
        regions.Clear();

        if (rect is Rect r) InvokeValue(window, "CommitBox", r);
        else Invoke(window, "RefreshSelection");

        Invoke(window, "UpdateChrome");
    }

    /// <summary>
    /// 工具栏堆叠组：一格放几个工具（同格再点循环）、收起 / 放回、右键菜单、恢复默认。
    /// 纯布局逻辑在 <c>--selftest</c> 里已逐条断言，这里只验**真窗口**的格子数 / 图标 / 角标 / 菜单条目。
    /// </summary>
    private static void ToolLayouts(CaptureOverlayWindow window)
    {
        var toolbar = Field(window, "Toolbar");
        var row = toolbar.GetType().GetField("ToolRow", Private)?.GetValue(toolbar) as WrapPanel;
        Check(row != null, "工具栏有工具行（WrapPanel）");
        if (row == null) return;

        var layoutType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.ToolLayout")!;
        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        object Tool(string name) => Enum.Parse(toolType, name);
        object Default() => layoutType.GetMethod("Default")!.Invoke(null, null)!;
        object Change(object layout, string name, object arg) => layoutType.GetMethod(name)!.Invoke(layout, new[] { arg })!;
        void Apply(object layout) => InvokeWith(toolbar, "ApplyLayout", layout);

        int Cells() => CellList().Count;
        List<RadioButton> CellList() => row.Children.OfType<RadioButton>()
            .Where(b => b.GroupName == "captureTool").ToList();
        string Current() => toolbar.GetType().GetProperty("CurrentTool", Private)!.GetValue(toolbar)!.ToString()!;
        static bool BadgeShown(RadioButton cell) =>
            cell.Content is Grid { Children.Count: > 1 } grid &&
            grid.Children[grid.Children.Count - 1] is System.Windows.Shapes.Ellipse { Visibility: Visibility.Visible };

        int all = Cells();
        Check(all == 13, $"默认布局按 Snow Shot 分成 13 格（实际 {all}）");
        Check(!BadgeShown(CellList()[0]), "单工具格子（选择）不显示「这里还有别的工具」的角标");
        Check(!BadgeShown(CellList()[1]), "「形状」合并后单独一格（矩形 / 椭圆 / 菱形在参数行里选），没有角标");
        Check(BadgeShown(CellList()[2]), "「箭头 / 直线」格有角标（一格不止一个工具）");

        // 按名字找格子（布局一改，下标就变，断言不该跟着改）
        RadioButton CellWith(string name) => CellList().First(c =>
            (c.Tag!.GetType().GetProperty("Tools")!.GetValue(c.Tag) as System.Collections.IEnumerable)!
            .Cast<object>().Any(t => t!.ToString() == name));

        // 并到左边一格：把「气泡 / 文字」并进它左边的「测距 / 测角 / 括号」
        var merged = Change(Default(), "MergeLeft", Tool("Text"));
        Apply(merged);
        Pump(window);
        Check(Cells() == all - 1, $"并到左边一格后少一格（{Cells()}）");
        var mergedCell = CellWith("Text");
        Check(BadgeShown(mergedCell), "多工具格子右下角有角标");
        Check((mergedCell.ToolTip?.ToString() ?? "").Contains("文字"),
            "那一格的提示写明还有个「文字」：" + mergedCell.ToolTip);

        // 同格再点一次循环切换：画笔荧光 → 矩形荧光 → 画笔荧光
        InvokeWith(window, "SetTool", Tool("Highlighter"));
        var glowCell = CellWith("Highlighter");
        glowCell.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(Current() == "HighlightRect", "同格点一下就换到组里的「矩形荧光」：" + Current());
        glowCell.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(Current() == "Highlighter", "再点一下转回「画笔荧光」：" + Current());

        // 格子右键菜单：收起 / 拆成单独一格 / 并到左边一格（第一格那条置灰）
        var cellMenu = (ContextMenu)InvokeValue(toolbar, "OpenCellMenu", glowCell.Tag!, glowCell, false);
        var items = Headers(cellMenu.Items);
        Check(items.Any(t => t.Contains("收起「画笔荧光」")), "格子菜单有「收起」：" + string.Join(" / ", items));
        Check(items.Any(t => t.Contains("拆成单独一格")), "多工具格子菜单有「拆成单独一格」");

        var firstCell = CellList()[0];
        var firstMenu = (ContextMenu)InvokeValue(toolbar, "OpenCellMenu", firstCell.Tag!, firstCell, false);
        Check(firstMenu.Items.OfType<MenuItem>().Any(m => m.Header?.ToString() == "并到左边一格" && !m.IsEnabled),
            "第一格没有左邻，那条「并到左边一格」是灰的");

        // 收起：单工具格（水印）整格消失，工具栏空白右键列得出它
        var hidden = Change(merged, "Hide", Tool("Watermark"));
        Apply(hidden);
        Pump(window);
        Check(Cells() == all - 2, $"收起一个单格工具再少一格（{Cells()}）");

        var background = (ContextMenu)InvokeValue(toolbar, "OpenBackgroundMenu", toolbar, false);
        var hiddenItems = Headers(background.Items);
        Check(hiddenItems.Any(t => t.Contains("水印")),
            "工具栏空白右键列得出收起来的工具：" + string.Join(" / ", hiddenItems));

        // 多工具格里收起一个：那一格还在，只是少一个工具
        var trimmed = Change(hidden, "Hide", Tool("RectEraser"));
        Apply(trimmed);
        Pump(window);
        Check(Cells() == all - 2, $"多工具格收起一个工具，格子数不变（{Cells()}）");

        // 人眼看一眼堆叠后的工具栏（第一格两个工具 + 角标）
        Apply(merged);
        Pump(window);
        Save(CropElement(window, (FrameworkElement)toolbar), Path.Combine(_outDir, "toolbar-stacked.png"));

        // 恢复默认：回到 13 格（选择 / 形状是单格，箭头与荧光等仍是多工具格）
        Apply(Default());
        Pump(window);
        Check(Cells() == all, $"恢复默认后回到 {all} 格（{Cells()}）");
        Check(!BadgeShown(CellList()[0]) && !BadgeShown(CellList()[1]) && BadgeShown(CellList()[2]),
            "恢复默认后仍是「选择 / 形状单格 + 箭头多工具格」");
    }

    /// <summary>
    /// 工具条跟着选区走：在框选区域下方居中，贴到窗口底边就翻到上方
    /// （照 snow-shot；以前是固定钉在屏幕底部中间）。
    /// </summary>
    private static void ToolbarPlacement(CaptureOverlayWindow window)
    {
        var toolbar = (FrameworkElement)Field(window, "Toolbar")!;
        var sel = new Rect(200, 150, 600, 400);

        SetSelection(window, sel);
        Pump(window);

        double W() => toolbar.ActualWidth > 0 ? toolbar.ActualWidth : toolbar.DesiredSize.Width;
        double H() => toolbar.ActualHeight > 0 ? toolbar.ActualHeight : toolbar.DesiredSize.Height;

        double left = Canvas.GetLeft(toolbar);
        double top = Canvas.GetTop(toolbar);

        Check(top >= sel.Bottom - 0.5, $"工具条在选区下方（top={top:0.#}，选区底边 {sel.Bottom}）");
        Check(top + H() <= window.ActualHeight - 7.5, $"工具条整个留在窗口里（bottom={top + H():0.#}）");
        // 工具条比选区还宽时（25 个工具 + 动作按钮）居中会被窗口边夹住，这是照上游的兜底
        double expectLeft = Math.Clamp(sel.Left + (sel.Width - W()) / 2, 8,
            Math.Max(8, window.ActualWidth - W() - 8));
        Check(Math.Abs(left - expectLeft) < 2,
            $"工具条在选区下方居中（left={left:0.#} 期望 {expectLeft:0.#} 宽 {W():0.#}）");

        // 选区贴到窗口底边（只剩 20 DIP）→ 下面根本摆不下工具条，翻到选区上方
        var low = new Rect(200, Math.Max(80, window.ActualHeight - 220), 600, 200);
        SetSelection(window, low);
        Pump(window);

        double top2 = Canvas.GetTop(toolbar);
        Check(top2 + H() <= low.Top + 0.5,
            $"贴着窗口底边时翻到选区上方（top={top2:0.#}，选区顶边 {low.Top:0.#}）");

        SetSelection(window, sel);
        Pump(window);
    }

    /// <summary>
    /// 标注：工具栏出现 → 切到矩形 → 画一个 → 导出里真的有它 → 撤销 / 重做 → 文字工具。
    /// 手势用反射直接调 <c>BeginToolGesture/UpdateToolDrag/FinishToolDrag</c>（不伪造鼠标事件），
    /// 鼠标按下 / 抬起的分派仍靠真机点一遍。
    /// </summary>
    private static void Annotate(CaptureOverlayWindow window, object doc, BitmapSource beforeMarks)
    {
        var toolbar = Field(window, "Toolbar") as FrameworkElement;
        Check(toolbar != null && toolbar.Visibility == Visibility.Visible, "选区出现后显示浮动工具栏");

        var toolRow = toolbar?.GetType().GetField("ToolRow", Private)?.GetValue(toolbar) as WrapPanel;
        int cells = 0;
        var covered = new HashSet<string>();
        if (toolRow != null)
            foreach (var child in toolRow.Children)
                if (child is RadioButton { GroupName: "captureTool" } cell)
                {
                    cells++;
                    if (cell.Tag?.GetType().GetProperty("Tools")?.GetValue(cell.Tag) is System.Collections.IEnumerable tools)
                        foreach (var tool in tools) covered.Add(tool!.ToString()!);
                }
        Check(cells == 13 && covered.Count == 25,
            $"工具栏按目录分成 13 格、覆盖全部 25 个工具（{cells} 格 / {covered.Count} 个工具）");

        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        ParamRow(window, toolbar);
        object Tool(string name) => Enum.Parse(toolType, name);

        // 换一个底图里没有的颜色（洋红）：后面按颜色找像素才说明「真的画出去了」
        var magenta = Color.FromRgb(0xFF, 0x00, 0xFF);

        // 切到形状（走覆盖层真正的入口，顺带验工具栏的选中态同步）
        InvokeWith(window, "SetTool", Tool("Shape"));
        var style = Member(window, "_style")!;
        style.GetType().GetProperty("Color")!.SetValue(style, magenta);
        style.GetType().GetProperty("ShapeKind")!.SetValue(style,
            Enum.Parse(style.GetType().GetProperty("ShapeKind")!.PropertyType, "Rectangle"));
        var current = toolbar!.GetType().GetProperty("CurrentTool", Private)!.GetValue(toolbar)!;
        Check(current.ToString() == "Shape", "工具栏跟着切到形状：" + current);

        // 画一个矩形：起点 (300,250)、终点 (500,350)，都在选区里
        var at = new Point(300, 250);
        var to = new Point(500, 350);
        InvokeWith(window, "BeginToolGesture", at, 1);
        InvokeWith(window, "UpdateToolDrag", to);
        InvokeWith(window, "FinishToolDrag", to);

        var marks = doc.GetType().GetProperty("Marks")!.GetValue(doc) as System.Collections.IList;
        Check(marks != null && marks.Count == 1, $"画完落进文档 {marks?.Count} 条");
        Check(marks is { Count: 1 } && marks[0]!.GetType().Name == "Shape", "画的是一条矩形（Shape）");

        var surface = Field(window, "Surface") as FrameworkElement;
        Check(surface != null && surface.GetType().GetProperty("Preview")!.GetValue(surface) == null,
            "松手后预览清空（只剩文档里的那条）");

        Invoke(window, "SyncArea");
        var drawn = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
        int hit = CountColor(drawn, magenta, 24);
        Check(hit > 100, $"导出里能找到标注的颜色（{hit} 个像素）");
        Check(CountColor(beforeMarks, magenta, 24) == 0, "画之前导出里没有这个颜色（对照）");
        Save(drawn, Path.Combine(_outDir, "export-annotated.png"));

        // 撤销 / 重做
        doc.GetType().GetMethod("Undo")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        Check(marks!.Count == 0, "撤销后标注没了");
        doc.GetType().GetMethod("Redo")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        Check(marks!.Count == 1, "重做后标注回来");

        // 文字工具：开框 → 写字 → 提交
        InvokeWith(window, "SetTool", Tool("Text"));
        InvokeWith(window, "OpenTextEntry", new Point(320, 280));
        var entry = Field(window, "TextEntry") as TextBox;
        Check(entry != null && entry.Visibility == Visibility.Visible, "文字工具弹出就地输入框");
        entry!.Text = "截图 Pro";
        Invoke(window, "CommitText");
        Check(marks!.Count == 2 && marks[1]!.GetType().Name == "Text", "提交后多了一条文字标注");
        Check(entry.Visibility != Visibility.Visible, "提交后输入框收起");

        // 空文本不该留下标注
        InvokeWith(window, "OpenTextEntry", new Point(360, 320));
        entry.Text = "   ";
        Invoke(window, "CommitText");
        Check(marks!.Count == 2, "空白文字不落盘");

        // 取色：色卡跟着光标，读的是冻结底图上的颜色
        InvokeWith(window, "SetTool", Tool("Picker"));
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));   // 底图上的红色块（250..450 × 200..320）
        var chip = Field(window, "PickerChip") as Border;
        var chipText = Field(window, "PickerText") as TextBlock;
        Check(chip != null && chip.Visibility == Visibility.Visible, "取色色卡出现");
        Check(chipText != null && chipText.Text.StartsWith("#E15B4E"),
            "取到的是底图色块的颜色：" + chipText?.Text);

        InvokeWith(window, "PickColor", new Point(260, 210));
        Check(chip != null && chip.Visibility == Visibility.Visible, "取色后覆盖层不关，色卡还在（可以连着取）");
        Check(marks!.Count == 2, "取色不落标注");

        Save(Render(window), Path.Combine(_outDir, "overlay-picker.png"));

        InvokeWith(window, "SetTool", Tool("Select"));
        Check(chip == null || chip.Visibility != Visibility.Visible, "切回选择后色卡收起");

        MarkHandles(window, doc, Tool);
        MagnifierHandles(window, doc, Tool);
        ParityTools(window, doc, Tool);
    }

    /// <summary>
    /// P1 的 11 个新工具：先在真窗口里走一遍真手势（两条臂的测角、矩形 / 画笔橡皮、水印单击），
    /// 再用合成底图跑 <c>CaptureDocument.Render()</c> 做**逐像素对账**
    /// （放大镜真的放大、画笔滤镜真的只改笔迹、测距标签真的有白字）。
    /// </summary>
    private static void ParityTools(CaptureOverlayWindow window, object doc, Func<string, object> tool)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var docType = asm.GetType("LTools.Features.CapturePro.CaptureDocument")!;
        var filterType = asm.GetType("LTools.Features.CapturePro.FilterKind")!;

        var marks = doc.GetType().GetProperty("Marks")!.GetValue(doc) as System.Collections.IList;
        Check(marks != null, "文档有标注列表");
        if (marks == null) return;

        int Count() => marks.Count;
        void Clear()
        {
            docType.GetMethod("ClearMarks")!.Invoke(doc, null);
            Invoke(window, "RefreshMarks");
        }

        void Drag(string name, Point from, Point to)
        {
            // 矩形 / 椭圆合并成一个「形状」工具（上游只有一个 Shape + 形状选择），
            // 这里用旧名字指定想要的形状。
            bool shape = name is "Rect" or "Ellipse";
            InvokeWith(window, "SetTool", tool(shape ? "Shape" : name));
            if (shape)
            {
                var current = Member(window, "_style")!;
                var prop = current.GetType().GetProperty("ShapeKind")!;
                prop.SetValue(current, Enum.Parse(prop.PropertyType, name == "Rect" ? "Rectangle" : "Ellipse"));
            }
            InvokeWith(window, "BeginToolGesture", from, 1);
            InvokeWith(window, "UpdateToolDrag", to);
            InvokeWith(window, "FinishToolDrag", to);
        }

        // ---- 真手势：直线 / 测距 / 括号 / 气泡 / 矩形荧光 / 放大镜 / 画笔滤镜 ----
        Clear();
        Drag("Line", new Point(300, 400), new Point(520, 400));
        Drag("Distance", new Point(300, 430), new Point(520, 430));
        Drag("Bracket", new Point(300, 450), new Point(340, 560));
        Drag("SpeechBubble", new Point(300, 480), new Point(420, 560));
        Drag("HighlightRect", new Point(440, 480), new Point(560, 560));
        Drag("Magnifier", new Point(580, 400), new Point(680, 480));
        Drag("BrushFilter", new Point(300, 380), new Point(560, 380));

        string NameAt(int index) => marks[index]!.GetType().Name;
        Check(Count() == 7, $"七个新工具各画出一条（{Count()}）");
        Check(Count() > 0 && NameAt(0) == "Line", "直线落盘");
        Check(Count() > 1 && NameAt(1) == "Distance", "测距落盘");
        Check(Count() > 2 && NameAt(2) == "Bracket", "括号落盘");
        Check(Count() > 3 && NameAt(3) == "SpeechBubble", "气泡落盘");
        Check(Count() > 4 && NameAt(4) == "Highlight", "矩形荧光落盘");
        Check(Count() > 5 && NameAt(5) == "Magnifier", "放大镜落盘");
        Check(Count() > 6 && NameAt(6) == "BrushFilter", "画笔滤镜落盘");

        // ---- 测角：两次拖拽（先一条臂，再一条臂），落成一个 90° 的角 ----
        Clear();
        InvokeWith(window, "SetTool", tool("Angle"));
        InvokeWith(window, "BeginToolGesture", new Point(300, 600), 1);
        InvokeWith(window, "UpdateToolDrag", new Point(420, 600));
        InvokeWith(window, "FinishToolDrag", new Point(420, 600));
        Check(Count() == 0, "第一次松手只记住第一条臂（还没落盘）");
        InvokeWith(window, "BeginToolGesture", new Point(300, 600), 1);
        InvokeWith(window, "UpdateToolDrag", new Point(300, 480));
        InvokeWith(window, "FinishToolDrag", new Point(300, 480));
        Check(Count() == 1 && NameAt(0) == "Angle", "第二次松手落成一个角");
        if (Count() == 1)
        {
            var degrees = (double)marks[0]!.GetType().GetProperty("Degrees")!.GetValue(marks[0])!;
            Check(Math.Abs(degrees - 90) < 0.5, $"两条垂直的臂 → 90°（{degrees:0.#}）");
        }

        // ---- 水印：单击一次落到图上（整张图平铺，不是拖框） ----
        Clear();
        InvokeWith(window, "SetTool", tool("Watermark"));
        InvokeWith(window, "BeginToolGesture", new Point(400, 400), 1);
        Check(Count() == 1 && NameAt(0) == "Watermark", "水印单击就落盘");
        var surface = Field(window, "Surface") as FrameworkElement;
        Check(surface != null && surface.GetType().GetProperty("Preview")!.GetValue(surface) is null,
            "水印落盘后收起预览（成品已在图上，再叠一层会翻倍）");

        // ---- 矩形橡皮：落一条「恢复原图」块（对齐上游 RestoreBackground），标注本身不删 ----
        Clear();
        Drag("Rect", new Point(300, 300), new Point(400, 380));
        Drag("Ellipse", new Point(420, 300), new Point(520, 380));
        Drag("Line", new Point(300, 700), new Point(400, 700));
        Check(Count() == 3, $"橡皮之前有三条标注（{Count()}）");
        Drag("RectEraser", new Point(290, 290), new Point(530, 390));
        Check(Count() == 4, $"矩形橡皮落一条恢复块（{Count()} 条）");
        Check(Count() == 4 && NameAt(3) == "Restore", "落下的是恢复块（不是删掉两条）");
        Check(Count() == 4 && NameAt(0) == "Shape" && NameAt(1) == "Shape" && NameAt(2) == "Line",
            "原来的三条标注都还在（恢复块只盖像素）");
        var hitTest = docType.GetMethod("HitTest")!;
        Check((int)hitTest.Invoke(doc, new object[] { new Point(450, 340), 4.0 })! != 3,
            "恢复块点不中（选择 / 橡皮都碰不到它）");

        // ---- 画笔橡皮：同样落恢复块 ----
        Clear();
        Drag("Line", new Point(300, 700), new Point(500, 700));
        Drag("Rect", new Point(300, 720), new Point(500, 780));
        Drag("BrushEraser", new Point(320, 700), new Point(480, 700));
        Check(Count() == 3 && NameAt(2) == "Restore",
            $"画笔橡皮落一条恢复块（{Count()} 条：{(Count() > 0 ? NameAt(Count() - 1) : "无")}）");

        // ============ 合成底图：逐像素对账 ============
        const int W = 200, H = 100;
        BitmapSource MakeBase(Func<int, int, (byte r, byte g, byte b)> color)
        {
            var buffer = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var (r, g, b) = color(x, y);
                    int at = (y * W + x) * 4;
                    buffer[at + 0] = b;
                    buffer[at + 1] = g;
                    buffer[at + 2] = r;
                    buffer[at + 3] = 0xFF;
                }

            var image = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, buffer, W * 4);
            image.Freeze();
            return image;
        }

        var halves = MakeBase((x, _) => x < W / 2 ? ((byte)0xFF, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)0xFF));
        var gray = MakeBase((x, y) =>
            x is >= 80 and < 90 && y is >= 40 and < 50 ? ((byte)0x20, (byte)0xC0, (byte)0x40) : ((byte)0x30, (byte)0x30, (byte)0x30));

        object Mark(string name, params object[] args)
        {
            var type = asm.GetType("LTools.Features.CapturePro.Mark+" + name)!;
            var ctor = type.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .First(c => c.GetParameters().Length >= args.Length);

            var full = new object[ctor.GetParameters().Length];
            for (int i = 0; i < full.Length; i++) full[i] = i < args.Length ? args[i] : Type.Missing;
            return ctor.Invoke(full);
        }

        /// <summary>
        /// 造一份样式规格（<c>MagnifierSpec</c> / <c>WatermarkSpec</c> / <c>SerialSpec</c>）：
        /// 从 <c>Default</c> 拷一份 <c>Clamped()</c> 的结果再逐项赋值——<c>Clamped()</c> 本身就是
        /// <c>this with { … }</c>，返回新实例，不会把静态默认值改脏。
        /// </summary>
        object MakeSpec(string typeName, params (string Name, object Value)[] fields)
        {
            var type = asm.GetType("LTools.Features.CapturePro." + typeName)!;
            object spec = type.GetMethod("Clamped")!.Invoke(type.GetProperty("Default")!.GetValue(null), null)!;
            foreach (var (name, value) in fields) type.GetProperty(name)!.SetValue(spec, value);
            return spec;
        }

        byte[] Render(BitmapSource baseImage, params object[] items)
        {
            var document = Activator.CreateInstance(docType, baseImage, new Int32Rect(0, 0, W, H), 1.0)!;
            docType.GetProperty("Area")!.SetValue(document, new Int32Rect(0, 0, W, H));
            var add = docType.GetMethod("Add")!;
            foreach (var item in items) add.Invoke(document, new[] { item });

            var frame = (BitmapSource)docType.GetMethod("Render")!.Invoke(document, null)!;
            var buffer = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(buffer, frame.PixelWidth * 4, 0);
            return buffer;
        }

        (byte b, byte g, byte r, byte a) Px(byte[] buffer, int x, int y)
        {
            int at = (y * W + x) * 4;
            return (buffer[at], buffer[at + 1], buffer[at + 2], buffer[at + 3]);
        }

        bool Near(byte[] buffer, int x, int y, int radius,
            Func<(byte b, byte g, byte r, byte a), bool> test)
        {
            for (int dy = -radius; dy <= radius; dy++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 0 || py < 0 || px >= W || py >= H) continue;
                    if (test(Px(buffer, px, py))) return true;
                }
            return false;
        }

        /// <summary>造一份箭头样式（走 ToolStyle.ArrowSpecOf，与界面同一个入口）。</summary>
        object ArrowSpecOf(string type = "Curve", string start = "None", string end = "Arrow",
            string shaft = "Plain", double width = 2d, double outline = 0d,
            string strokeStyle = "Solid", double ratio = 1d, uint seed = 3)
        {
            var styleType = asm.GetType("LTools.Features.CapturePro.ToolStyle")!;
            var style = Activator.CreateInstance(styleType)!;
            object E(string name, string value) =>
                Enum.Parse(asm.GetType("LTools.Features.CapturePro." + name)!, value);
            void Set(string name, object value) => styleType.GetProperty(name)!.SetValue(style, value);

            Set("Color", Colors.Magenta);
            Set("Width", width);
            Set("StrokeStyle", E("StrokeKind", strokeStyle));
            Set("Rendering", E("StrokeRendering", "Architect"));
            Set("ArrowKind", E("ArrowType", type));
            Set("StartArrowhead", E("Arrowhead", start));
            Set("EndArrowhead", E("Arrowhead", end));
            Set("ArrowRatio", ratio);
            Set("ArrowShaftType", E("ArrowShaft", shaft));
            Set("ArrowOutlineWidth", outline);
            Set("ArrowOutlineColor", Colors.Cyan);
            return styleType.GetMethod("ArrowSpecOf")!.Invoke(style, new object[] { seed })!;
        }

        int CountIf(byte[] buffer, Func<(byte b, byte g, byte r, byte a), bool> test)
        {
            int n = 0;
            for (int i = 0; i < buffer.Length; i += 4)
                if (test((buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]))) n++;
            return n;
        }

        // 直线 / 括号：洋红笔画真的落在像素上
        var linePixels = Render(halves, Mark("Line", new Point(20, 50), new Point(180, 50), Colors.Magenta, 4d, false));
        Check(Px(linePixels, 100, 50) is { r: > 200, b: > 200, g: < 80 }, "直线画成洋红（导出像素）");
        Check(Px(linePixels, 80, 80) is { r: > 200, b: < 120 }, "没画到的地方还是底图");

        // 箭头族：直线 / 曲线 / 折线的杆与终点箭头都真的落到像素上
        bool Magenta((byte b, byte g, byte r, byte a) p) => p.r > 150 && p.b > 150 && p.g < 120;

        var arrowStraight = Render(halves, Mark("Arrow",
            new[] { new Point(20, 50), new Point(180, 50) }, ArrowSpecOf("Straight")));
        Check(Px(arrowStraight, 100, 50) is { r: > 190, b: > 190, g: < 90 }, "直线箭头杆画在像素上");
        Check(Near(arrowStraight, 160, 50, 18, Magenta), "终点三角箭头画在像素上");

        var arrowCurve = Render(halves, Mark("Arrow",
            new[] { new Point(20, 80), new Point(100, 20), new Point(180, 80) }, ArrowSpecOf()));
        Check(Near(arrowCurve, 55, 54, 5, Magenta), "曲线箭头在弦外鼓出来（Catmull-Rom）");
        Check(Near(arrowCurve, 100, 20, 6, Magenta), "曲线箭头经过中间控制点");
        Check(!Near(arrowCurve, 100, 80, 3, Magenta), "曲线箭头不在直线弦上");

        var arrowElbow = Render(halves, Mark("Arrow",
            new[] { new Point(20, 30), new Point(100, 30), new Point(100, 90), new Point(180, 90) },
            ArrowSpecOf("Elbow")));
        Check(Near(arrowElbow, 50, 30, 3, Magenta), "折线第一段横杆");
        Check(Near(arrowElbow, 100, 55, 4, Magenta), "折线竖段（拐角圆角之间）");
        Check(Near(arrowElbow, 140, 90, 3, Magenta), "折线末段横杆");
        Check(Near(arrowElbow, 175, 90, 18, Magenta), "折线终点箭头（方向 = 末段弦）");

        // 14 种端点都画得出来（顺序照上游 buildArrowFamily）
        string[] headNames =
        {
            "None", "Arrow", "Bar", "Dot", "Circle", "CircleOutline", "IndentedTriangle",
            "Triangle", "TriangleOutline", "Diamond", "DiamondOutline", "CrowfootOne",
            "CrowfootMany", "CrowfootOneOrMany",
        };
        foreach (var headName in headNames)
        {
            var ends = Render(halves, Mark("Arrow",
                new[] { new Point(40, 60), new Point(200, 60) }, ArrowSpecOf("Straight", end: headName)));
            Check(Near(ends, 170, 60, 26, Magenta), $"端点「{headName}」画在终点附近");
        }

        // 起点也能有箭头（两端同时）
        var bothEnds = Render(halves, Mark("Arrow",
            new[] { new Point(40, 60), new Point(200, 60) },
            ArrowSpecOf("Straight", start: "Bar", end: "Triangle")));
        Check(Near(bothEnds, 40, 60, 20, Magenta), "起点端点画出来");
        Check(Near(bothEnds, 200, 60, 20, Magenta), "终点端点画出来");

        // 外描边：垫在墨迹下面，杆两侧各露出一截
        var outlined = Render(halves, Mark("Arrow",
            new[] { new Point(40, 80), new Point(160, 80) },
            ArrowSpecOf("Straight", outline: 6)));
        Check(Px(outlined, 100, 76) is { r: < 100, g: > 190, b: > 190 }, "外描边在杆外侧");
        Check(Px(outlined, 100, 80) is { r: > 190, b: > 190, g: < 90 }, "墨迹盖在外描边上面");

        // 渐宽杆：越靠箭头越粗
        var taperedShot = Render(halves, Mark("Arrow",
            new[] { new Point(30, 60), new Point(170, 60) },
            ArrowSpecOf("Straight", shaft: "Tapered")));
        int thin = 0, thick = 0;
        for (int y = 30; y < 90; y++)
        {
            if (Magenta(Px(taperedShot, 50, y))) thin++;
            if (Magenta(Px(taperedShot, 150, y))) thick++;
        }
        Check(thick > thin + 2, $"渐宽杆越靠箭头越粗（起点列 {thin} / 末端列 {thick}）");

        var bracketPixels = Render(halves, Mark("Bracket", new Point(40, 20), new Point(120, 100), Colors.Magenta, 4d));
        Check(Px(bracketPixels, 120, 60) is { r: > 200, b: > 200, g: < 80 }, "花括号的尖画出来了（终点 x、中线）");
        Check(Px(bracketPixels, 80, 40) is { r: > 200, b: > 200, g: < 80 }, "花括号的肩画出来了（脊线鼓出一半深度）");
        Check(Px(bracketPixels, 40, 60) is { r: > 200, g: < 60, b: < 60 }, "花括号脊线中间是空的（不是一条直线加刻度）");

        // 气泡：边框是洋红，身体里不填色
        var bubblePixels = Render(halves, Mark("SpeechBubble", new Rect(40, 20, 120, 50), Colors.Magenta, 4d));
        Check(Px(bubblePixels, 40, 45) is { r: > 200, b: > 200, g: < 90 }, "气泡左边框画出来了");
        Check(Px(bubblePixels, 80, 45) is { r: > 200, g: < 60, b: < 60 }, "气泡里面不填色（透出底图红）");

        // ---- P2-b：形状族（填充 / 圆角 / 菱形 / 虚线 / 思考气泡）逐像素 ----
        var kindType = asm.GetType("LTools.Features.CapturePro.ShapeKind")!;
        var strokeType = asm.GetType("LTools.Features.CapturePro.StrokeKind")!;
        var renderType = asm.GetType("LTools.Features.CapturePro.StrokeRendering")!;
        var fillType = asm.GetType("LTools.Features.CapturePro.FillKind")!;
        var specType = asm.GetType("LTools.Features.CapturePro.ShapeSpec")!;
        var shapeType = asm.GetType("LTools.Features.CapturePro.Mark+Shape")!;
        var bubbleType = asm.GetType("LTools.Features.CapturePro.Mark+SpeechBubble")!;

        object Spec(string kind, Color stroke, double width, string strokeStyle, string rendering,
            Color fill, string fillStyle, double radius, uint seed) =>
            Activator.CreateInstance(specType,
                Enum.Parse(kindType, kind), stroke, width, Enum.Parse(strokeType, strokeStyle),
                Enum.Parse(renderType, rendering), fill, Enum.Parse(fillType, fillStyle),
                radius, seed)!;

        object Shape(Point a, Point b, object spec) =>
            Activator.CreateInstance(shapeType, new[] { a, b, spec })!;

        object Bubble(Rect body, object spec) =>
            Activator.CreateInstance(bubbleType, new object[] { body, spec })!;

        bool IsMagenta((byte b, byte g, byte r, byte a) p) => p.r > 180 && p.b > 180 && p.g < 110;

        int MagentaOnRow(byte[] buffer, int y, int x0, int x1)
        {
            int n = 0;
            for (int x = x0; x <= x1; x++) if (IsMagenta(Px(buffer, x, y))) n++;
            return n;
        }

        var boxA = new Point(20, 20);
        var boxB = new Point(120, 80);

        // 填充：无 = 上游 Border（里面透出底图）；实心 = 整块被填充色盖住
        var borderOnly = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 0, 3)));
        Check(Px(borderOnly, 70, 50) is { r: > 200, g: < 60, b: < 60 }, "填充「无」：框里透出底图");

        var solidFill = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "Solid", 0, 3)));
        Check(Px(solidFill, 70, 50) is { r: > 230, g: > 230, b: > 230 }, "填充「实心」：框里是填充色（白）");
        Check(Px(solidFill, 150, 50) is { b: > 200, r: < 90 }, "填充不外溢（框外仍是底图）");

        // 斜线 / 交叉：只在那块里画线，框里框外都不整块染色
        var lineFill = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "Line", 0, 3)));
        int lineInterior = CountIf(lineFill, p => p is { r: > 200, g: < 60, b: < 60 });
        int solidInterior = CountIf(solidFill, p => p is { r: > 200, g: < 60, b: < 60 });
        Check(lineInterior > solidInterior + 200,
            $"填充「斜线」不整块盖住（红底像素 {lineInterior} vs 实心 {solidInterior}）");

        // 圆角：直角会把左上角描到，圆角 24 会把它让开
        var sharpPixels = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 0, 3)));
        var roundPixels = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 24, 3)));
        Check(IsMagenta(Px(sharpPixels, 21, 21)), "圆角 0：左上角被描到");
        Check(!IsMagenta(Px(roundPixels, 21, 21)), "圆角 24：左上角让开（圆弧从更远的地方起）");
        Check(IsMagenta(Px(roundPixels, 70, 20)), "圆角 24：顶边中段照样描到");

        // 菱形：右顶点落在边的中点，四角空着
        var diamondPixels = Render(halves, Shape(boxA, boxB,
            Spec("Diamond", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 0, 3)));
        Check(IsMagenta(Px(diamondPixels, 120, 50)), "菱形右顶点在边的中点 (120,50)");
        Check(IsMagenta(Px(diamondPixels, 20, 50)), "菱形左顶点在 (20,50)");
        Check(!IsMagenta(Px(diamondPixels, 22, 22)), "菱形左上角空着（不是矩形）");

        // 椭圆：外接框的边与角
        var ellipsePixels = Render(halves, Shape(boxA, boxB,
            Spec("Ellipse", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 0, 3)));
        Check(IsMagenta(Px(ellipsePixels, 70, 20)), "椭圆顶边中点在框上");
        Check(!IsMagenta(Px(ellipsePixels, 21, 21)), "椭圆左上角空着");

        // 虚线 / 点线：沿顶边的洋红像素明显少于实线
        int solidTop = MagentaOnRow(borderOnly, 20, 22, 118);
        var dashed = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Dashed", "Architect", Colors.White, "None", 0, 3)));
        int dashedTop = MagentaOnRow(dashed, 20, 22, 118);
        Check(solidTop > 80, $"实线顶边基本连着（{solidTop} 个洋红像素）");
        Check(dashedTop < solidTop * 0.8, $"虚线顶边有断口（{dashedTop} vs 实线 {solidTop}）");
        var dotted = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Dotted", "Architect", Colors.White, "None", 0, 3)));
        int dottedTop = MagentaOnRow(dotted, 20, 22, 118);
        Check(dottedTop < dashedTop, $"点线比虚线更疏（{dottedTop} vs {dashedTop}）");

        // 笔触风格：手绘那条线不再是一条笔直的实线（同一行上的像素变少 / 抖动出界）
        var artist = Render(halves, Shape(boxA, boxB,
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Artist", Colors.White, "None", 0, 3)));
        int artistTop = 0, artistNear = 0;
        for (int y = 16; y <= 26; y++)
        {
            int on = MagentaOnRow(artist, y, 22, 118);
            if (y == 20) artistTop = on;
            artistNear += on;
        }
        Check(artistTop < solidTop || artistNear > solidTop * 1.3,
            $"手绘笔触让顶边抖动（同一行 {artistTop}，±6 行共 {artistNear}）");

        // 思考气泡：云朵轮廓 + 身体下方的圆点尾巴
        var thoughtPixels = Render(halves, Bubble(new Rect(20, 10, 120, 50),
            Spec("ThoughtBubble", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 12, 5)));
        int dots = 0;
        for (int y = 62; y < H; y++)
            for (int x = 0; x < W; x++)
                if (IsMagenta(Px(thoughtPixels, x, y))) dots++;
        Check(dots > 10, $"思考气泡下方有圆点尾巴（{dots} 个洋红像素）");
        Check(IsMagenta(Px(thoughtPixels, 80, 10)) || IsMagenta(Px(thoughtPixels, 70, 14)),
            "思考气泡的云朵顶边画出来了");

        // 矩形气泡：三角尾巴仍在（P1 的老口径没被改坏）
        var tailPixels = Render(halves, Bubble(new Rect(40, 20, 120, 50),
            Spec("Rectangle", Colors.Magenta, 4, "Solid", "Architect", Colors.White, "None", 6, 7)));
        int tailCount = 0;
        for (int y = 70; y < H; y++)
            for (int x = 40; x < 100; x++)
                if (IsMagenta(Px(tailPixels, x, y))) tailCount++;
        Check(tailCount > 10, $"矩形气泡的尾巴画在身体下方（{tailCount} 个洋红像素）");

        // 矩形荧光：半透明黄铺在红底上 → 红不变、绿抬到约 89
        var glowPixels = Render(halves, Mark("Highlight", new Point(40, 20), new Point(160, 80), Colors.Yellow, 0.35));
        var inside = Px(glowPixels, 60, 50);
        Check(inside.r > 200 && inside.g is > 70 and < 110 && inside.b < 20,
            $"矩形荧光是半透明黄（r={inside.r} g={inside.g} b={inside.b}）");
        Check(Px(glowPixels, 20, 95) is { r: > 200, g: < 40 }, "荧光框外不动");

        // 测距：中点压着描边色的纯文字（上游注释文字：无背景、居中），再没有黑底 pill
        var distancePixels = Render(halves, Mark("Distance", new Point(20, 50), new Point(180, 50), Colors.Magenta, 4d));
        int pill = 0, ink = 0;
        for (int y = 26; y < 76; y++)
            for (int x = 40; x < 160; x++)
            {
                var p = Px(distancePixels, x, y);
                if (p.r < 60 && p.g < 60 && p.b < 60) pill++;
                if (Math.Abs(y - 50) > 5 && p.r > 120 && p.b > 120 && p.g < 100) ink++;
            }
        Check(pill < 8, $"测距标签不再有黑底 pill（{pill} 个深色像素）");
        Check(ink > 25, $"测距标签是描边色的纯文字（{ink} 个像素）");
        Check(Px(distancePixels, 100, 50) is { r: > 200, b: > 200, g: < 80 }, "测距主线也是洋红");

        // 端点：箭头样式在两端长出去（长度钳到线段一半，肩点张角 20°）
        var dSpecType = asm.GetType("LTools.Features.CapturePro.DistanceSpec")!;
        var dUnitType = asm.GetType("LTools.Features.CapturePro.DistanceUnit")!;
        var dHeadType = asm.GetType("LTools.Features.CapturePro.Arrowhead")!;
        object MeasureSpec(string unit, double factor, int decimals, double scale, string head) =>
            Activator.CreateInstance(dSpecType, Enum.Parse(dUnitType, unit), factor, decimals, scale,
                Enum.Parse(dHeadType, head))!;
        var arrowEnds = Render(halves, Mark("Distance", new Point(60, 50), new Point(140, 50),
            Colors.Magenta, 4d, MeasureSpec("Px", 1, 0, 2, "Arrow")));
        Check(Px(arrowEnds, 120, 43) is { r: > 190, b: > 190, g: < 100 }, "端点比例 2 的箭头从终点长出肩线");

        // 放大镜：底图上只有 10×10 的绿块，2 倍镜头里应该看到约 20×20
        var lensSpec = MakeSpec("MagnifierSpec", ("Factor", 2d), ("StrokeWidth", 3d), ("Stroke", Colors.Magenta));
        var lensPixels = Render(gray, Mark("Magnifier", new Rect(50, 25, 100, 50), lensSpec));
        bool IsGreen((byte b, byte g, byte r, byte a) p) => p.g > 140 && p.r < 100 && p.b < 120;
        int before = CountIf(Render(gray), IsGreen);
        int after = CountIf(lensPixels, IsGreen);
        Check(before is > 60 and < 140, $"底图里的绿块约 10×10（{before} 个像素）");
        Check(after > before + 150, $"放大镜把绿块放大到约 2 倍面积（{before} → {after}）");
        Check(IsGreen(Px(lensPixels, 66, 36)), "镜头里绿块的左上角被搬到了新位置");

        // 画笔滤镜：反相只改笔迹压过的像素
        var brushPixels = Render(halves, Mark("BrushFilter",
            new[] { new Point(25, 25), new Point(25, 75) }, 20d, Enum.Parse(filterType, "Invert"), 40d));
        var brushed = Px(brushPixels, 25, 50);
        Check(brushed is { r: < 80, g: > 170, b: > 170 }, $"笔迹上红底被反相成青色（r={brushed.r} g={brushed.g} b={brushed.b}）");
        Check(Px(brushPixels, 150, 50) is { r: < 40, g: < 40, b: > 170 }, "笔迹外的底图没变");
        Check(Px(brushPixels, 25, 95) is { r: > 200, g: < 40 }, "笔迹下方没扫到的红底没变");

        // 智能擦除（P1-b）：洞横跨红 / 蓝两半，重建后每个像素仍属于它那一侧
        var erasePixels = Render(halves, Mark("Filter", new Point(40, 20), new Point(160, 80),
            Enum.Parse(filterType, "SmartErase"), 40d));
        var eraseLeft = Px(erasePixels, 70, 50);
        var eraseRight = Px(erasePixels, 130, 50);
        Check(eraseLeft.r > eraseLeft.b, $"智能擦除：洞的左半边接着红底（r={eraseLeft.r} b={eraseLeft.b}）");
        Check(eraseRight.b > eraseRight.r, $"智能擦除：洞的右半边接着蓝底（r={eraseRight.r} b={eraseRight.b}）");
        Check(eraseLeft.a == 255, "智能擦除保留 Alpha（不把洞抠透明）");
        Check(Px(erasePixels, 10, 50) is { r: > 200, b: < 90 }, "洞外的底图一个像素都不动");

        // 画笔滤镜 + 智能擦除：笔迹下的内容被周围重建回去，而不是反相成别的颜色
        var eraseBrush = Render(halves, Mark("BrushFilter",
            new[] { new Point(30, 20), new Point(30, 80) }, 16d,
            Enum.Parse(filterType, "SmartErase"), 40d));
        var erased = Px(eraseBrush, 30, 50);
        Check(erased.r > erased.b && erased.r > 120,
            $"智能擦除笔迹：红底上的笔迹被红底盖回去（r={erased.r} g={erased.g} b={erased.b}）");
        Check(Px(eraseBrush, 120, 50) is { b: > 200, r: < 90 }, "智能擦除笔迹：没扫到的右半边仍是蓝底");

        // 恢复原图（矩形 / 画笔橡皮）：把底图贴回来，盖掉下面画的东西
        var magentaFill = Mark("Highlight", new Point(0, 0), new Point(W, H), Colors.Magenta, 1d);
        var restoredRect = Render(halves, magentaFill,
            Mark("Restore", new Point(60, 20), new Point(140, 80), null, 0d));
        Check(Px(restoredRect, 80, 50) is { r: > 200, b: < 90 }, "矩形橡皮：恢复块里露出左半边的红底");
        Check(Px(restoredRect, 120, 50) is { b: > 200, r: < 90 }, "矩形橡皮：恢复块里露出右半边的蓝底");
        Check(Px(restoredRect, 20, 50) is { r: > 200, b: > 200 }, "矩形橡皮：块外仍是盖上去的洋红");

        var restoredBrush = Render(halves, magentaFill,
            Mark("Restore", new Point(60, 20), new Point(60, 80),
                new[] { new Point(60, 20), new Point(60, 80) }, 16d));
        Check(Px(restoredBrush, 60, 50) is { r: > 200, b: < 90 }, "画笔橡皮：笔迹压过的地方露出红底");
        Check(Px(restoredBrush, 20, 50) is { r: > 200, b: > 200 }, "画笔橡皮：笔迹旁边仍是洋红");

        // 水印：白字铺满整张图（左右两半都有）
        var wmSpec = MakeSpec("WatermarkSpec", ("Text", "WM"), ("Color", Colors.White), ("FontSize", 12d),
            ("Angle", 0d), ("Gap", 30d), ("Opacity", 1d));
        var wmPixels = Render(halves, Mark("Watermark", wmSpec));
        bool IsWhite((byte b, byte g, byte r, byte a) p) => p.r > 200 && p.g > 200 && p.b > 200;
        int whiteLeft = 0, whiteRight = 0;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                if (IsWhite(Px(wmPixels, x, y)))
                {
                    if (x < W / 2) whiteLeft++; else whiteRight++;
                }
        Check(whiteLeft > 20 && whiteRight > 20, $"水印平铺整张图（左 {whiteLeft} / 右 {whiteRight} 个白像素）");

        Clear();
    }

    /// <summary>
    /// 放大镜的拖动语义（照上游 <c>hit_resolution.rs</c> + <c>magnifier_drag_style_tests.rs</c>）：
    /// 拖**源区域** = 镜头与源一起平移；拖**镜头本体**（源之外）或镜头下方 24 DIP 的**移动手柄**
    /// = 只动镜头。再画一次选中态，数一遍源轮廓虚线与手柄小点的像素。
    /// </summary>
    private static void MagnifierHandles(CaptureOverlayWindow window, object doc, Func<string, object> tool)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var docType = asm.GetType("LTools.Features.CapturePro.CaptureDocument")!;
        var magnifierType = asm.GetType("LTools.Features.CapturePro.Mark+Magnifier")!;
        var specType = asm.GetType("LTools.Features.CapturePro.MagnifierSpec")!;
        var inkType = asm.GetType("LTools.Features.CapturePro.MagnifierInk")!;
        var canvasType = asm.GetType("LTools.Features.CapturePro.CaptureCanvas")!;

        var marks = (System.Collections.IList)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
        docType.GetMethod("ClearMarks")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");

        // 落一个 200 × 100 的镜头
        InvokeWith(window, "SetTool", tool("Magnifier"));
        InvokeWith(window, "BeginToolGesture", new Point(300, 300), 1);
        InvokeWith(window, "UpdateToolDrag", new Point(500, 400));
        InvokeWith(window, "FinishToolDrag", new Point(500, 400));
        Check(marks.Count == 1 && marks[0]!.GetType().Name == "Magnifier",
            $"放大镜落盘（{marks.Count} 条，{marks[0]?.GetType().Name}）");
        if (marks.Count != 1) return;

        Rect Lens() => (Rect)magnifierType.GetProperty("Lens")!.GetValue(marks[0]!)!;
        Point LensCenter() => (Point)magnifierType.GetProperty("LensCenter")!.GetValue(marks[0]!)!;
        Point SourceCenter() => (Point)magnifierType.GetProperty("SourceCenter")!.GetValue(marks[0]!)!;
        Rect SourceRect() => (Rect)magnifierType.GetProperty("SourceRect")!.GetValue(marks[0]!)!;
        static bool Near(Point a, Point b) => Math.Abs(a.X - b.X) < 0.01 && Math.Abs(a.Y - b.Y) < 0.01;

        InvokeWith(window, "SetTool", tool("Select"));
        InvokeWith(window, "SelectMark", 0);

        // ---- ① 拖源区域 = 整块挪动（两个中心一起走） ----
        var lens0 = LensCenter();
        var source0 = SourceCenter();
        var press = SourceCenter();
        InvokeWith(window, "BeginMarkDrag", press, 0);
        InvokeWith(window, "UpdateMarkDrag", press + new Vector(40, 20));
        InvokeWith(window, "CommitMarkDrag", press + new Vector(40, 20));
        Check(Near(LensCenter(), lens0 + new Vector(40, 20)), "拖源区域：镜头跟着走（整块挪动）");
        Check(Near(SourceCenter(), source0 + new Vector(40, 20)), "拖源区域：源中心一起走（上游 transform 同时映射两个中心）");

        // ---- ② 拖移动手柄 = 只挪镜头 ----
        var grip = (Point)inkType.GetMethod("MoveGrip")!.Invoke(null, new object[] { Lens() })!;
        Check(Math.Abs(grip.Y - (Lens().Bottom + 24)) < 0.01,
            $"移动手柄在镜头下缘 +24 DIP（手柄 y={grip.Y:0.#}，镜头底 {Lens().Bottom:0.#}）");
        Check((bool)InvokeValue(window, "BeginMagnifierLensDrag", grip)!,
            "按在移动手柄上开始拖镜头（手柄在镜头外面，镜头与源重合时也抓得到）");
        InvokeWith(window, "UpdateMagnifierLensDrag", grip + new Vector(-30, 15));
        InvokeWith(window, "CommitMagnifierLensDrag", grip + new Vector(-30, 15));
        Check(Near(LensCenter(), lens0 + new Vector(40 - 30, 20 + 15)), "拖手柄：镜头跟着走");
        Check(Near(SourceCenter(), source0 + new Vector(40, 20)), "拖手柄：源区域不动");

        // ---- ③ 按镜头本体（源之外）= 只挪镜头 ----
        var lensOnly = new Point(Lens().Left + 5, Lens().Top + 5);
        Check(!SourceRect().Contains(lensOnly), "取的这点在源区域之外（对照）");
        Check((bool)InvokeValue(window, "BeginMagnifierLensDrag", lensOnly)!,
            "按在镜头本体（源之外）开始拖镜头");
        InvokeWith(window, "UpdateMagnifierLensDrag", lensOnly + new Vector(25, -10));
        InvokeWith(window, "CommitMagnifierLensDrag", lensOnly + new Vector(25, -10));
        Check(Near(LensCenter(), lens0 + new Vector(35, 25)), "拖镜头本体：镜头跟着走");
        Check(Near(SourceCenter(), source0 + new Vector(40, 20)), "拖镜头本体：源区域不动");

        // ---- ④ 按源区域不是「只挪镜头」（那是整块挪动，交给通用拖标注） ----
        Check(!(bool)InvokeValue(window, "BeginMagnifierLensDrag", SourceCenter())!,
            "按在源区域上不归「只挪镜头」管（整块挪动）");

        // ---- ⑤ 选中态：源轮廓虚线 + 镜头下方的移动手柄 ----
        var black = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 600 * 4], 800 * 4);
        black.Freeze();
        var probeDoc = Activator.CreateInstance(docType, black, new Int32Rect(0, 0, 800, 600), 1.0)!;
        object spec = specType.GetMethod("Clamped")!
            .Invoke(specType.GetProperty("Default")!.GetValue(null), null)!;
        var probe = Activator.CreateInstance(magnifierType, new Rect(200, 200, 200, 100), spec)!;
        docType.GetMethod("Add")!.Invoke(probeDoc, new[] { probe });

        var canvas = (FrameworkElement)Activator.CreateInstance(canvasType)!;
        canvasType.GetProperty("Document")!.SetValue(canvas, probeDoc);
        canvasType.GetProperty("Selection")!.SetValue(canvas, new Rect(0, 0, 800, 600));
        canvasType.GetProperty("SelectedIndex")!.SetValue(canvas, 0);
        canvasType.GetProperty("ShowHandles")!.SetValue(canvas, false);
        canvas.Measure(new Size(800, 600));
        canvas.Arrange(new Rect(0, 0, 800, 600));
        canvas.UpdateLayout();

        var bitmap = new RenderTargetBitmap(800, 600, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        bitmap.Freeze();
        Save(bitmap, Path.Combine(_outDir, "overlay-magnifier-chrome.png"));

        /// 细虚线抗锯齿之后颜色会被黑底拉暗，按「明显偏蓝」数，别按精确色比
        static int Bluish(BitmapSource image)
        {
            var conv = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var buffer = new byte[w * h * 4];
            conv.CopyPixels(buffer, w * 4, 0);
            int n = 0;
            for (int i = 0; i + 3 < buffer.Length; i += 4)
                if (buffer[i] > 90 && buffer[i] > buffer[i + 2] + 30) n++;
            return n;
        }

        var probeSource = (Rect)magnifierType.GetProperty("SourceRect")!.GetValue(probe)!;
        var probeSourceBorder = new Rect(probeSource.X - 1, probeSource.Y - 1, probeSource.Width + 2, probeSource.Height + 2);
        var borderCrop = new CroppedBitmap(bitmap, new Int32Rect((int)probeSourceBorder.X,
            (int)probeSourceBorder.Y, (int)Math.Ceiling(probeSourceBorder.Width),
            (int)Math.Ceiling(probeSourceBorder.Height)));
        int borderPixels = Bluish(borderCrop);
        Check(borderPixels > 8, $"选中放大镜时源区域有虚线轮廓（{borderPixels} 个蓝色像素）");

        var probeGrip = (Point)inkType.GetMethod("MoveGrip")!.Invoke(null,
            new object[] { (Rect)magnifierType.GetProperty("Lens")!.GetValue(probe)! })!;
        var gripCrop = new CroppedBitmap(bitmap, new Int32Rect((int)probeGrip.X - 8, (int)probeGrip.Y - 8, 16, 16));
        int gripPixels = Bluish(gripCrop);
        Check(gripPixels > 3, $"镜头下方画出了移动手柄（{gripPixels} 个蓝色像素）");

        docType.GetMethod("ClearMarks")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        InvokeWith(window, "SetTool", tool("Select"));
    }

    /// <summary>
    /// 标注的缩放手柄：合成一份黑底文档 + 一条矩形，直接画 <c>CaptureCanvas</c>，数白色把手像素
    /// （黑底上白方块跑不掉）；再验「序号徽标不给手柄」「没选中 / 关掉就一个都不画」，
    /// 以及真窗口里拖一次把手之后这条标注的外接框变大。
    /// </summary>
    private static void MarkHandles(CaptureOverlayWindow window, object doc, Func<string, object> tool)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var docType = asm.GetType("LTools.Features.CapturePro.CaptureDocument")!;
        var canvasType = asm.GetType("LTools.Features.CapturePro.CaptureCanvas")!;
        var shapeType = asm.GetType("LTools.Features.CapturePro.Mark+Shape")!;
        var serialType = asm.GetType("LTools.Features.CapturePro.Mark+Serial")!;

        var black = BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, new byte[200 * 100 * 4], 200 * 4);
        black.Freeze();

        object NewDoc() => Activator.CreateInstance(docType, black, new Int32Rect(0, 0, 200, 100), 1.0)!;

        FrameworkElement NewCanvas(object document, bool select, bool handles)
        {
            var canvas = (FrameworkElement)Activator.CreateInstance(canvasType)!;
            var t = canvasType;
            t.GetProperty("Document")!.SetValue(canvas, document);
            t.GetProperty("Selection")!.SetValue(canvas, new Rect(0, 0, 200, 100));
            t.GetProperty("SelectedIndex")!.SetValue(canvas, select ? 0 : -1);
            t.GetProperty("ShowHandles")!.SetValue(canvas, handles);
            canvas.Measure(new Size(200, 100));
            canvas.Arrange(new Rect(0, 0, 200, 100));
            canvas.UpdateLayout();
            return canvas;
        }

        BitmapSource Draw(FrameworkElement canvas)
        {
            var bitmap = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            bitmap.Freeze();
            return bitmap;
        }

        // 一条矩形：Frame = (20,20,100,60)，选中框外扩 3 → 把手在 (17,17)…(123,83)
        var rectDoc = NewDoc();
        var shape = Activator.CreateInstance(shapeType,
            new Point(20, 20), new Point(120, 80), Colors.Red, 2.0, false, false)!;
        docType.GetMethod("Add")!.Invoke(rectDoc, new[] { shape });
        var rectFrame = new Rect(20, 20, 100, 60);

        Check(CountWhiteNear(Draw(NewCanvas(rectDoc, false, false)), rectFrame) == 0,
            "没选中时一个把手都不画");
        Check(CountWhiteNear(Draw(NewCanvas(rectDoc, true, false)), rectFrame) == 0,
            "选中但没到「选择」工具时不画把手（只有虚线框）");

        var withHandles = Draw(NewCanvas(rectDoc, true, true));
        int whites = CountWhiteNear(withHandles, rectFrame);
        Check(whites > 120, $"八个白把手画出来了（{whites} 个白像素）");

        int onCorner = 0;
        foreach (var p in GripPoints(rectFrame))
            if (IsWhite(withHandles, (int)Math.Round(p.X), (int)Math.Round(p.Y))) onCorner++;
        Check(onCorner == 8, $"八个把手都在选中框的角 / 中点上（{onCorner}/8）");
        Save(withHandles, Path.Combine(_outDir, "overlay-mark-handles.png"));

        // 序号徽标是固定尺寸的：不给手柄
        var serialDoc = NewDoc();
        var serialSpecType = asm.GetType("LTools.Features.CapturePro.SerialSpec")!;
        var serialSpec = serialSpecType.GetMethod("Clamped")!
            .Invoke(serialSpecType.GetProperty("Default")!.GetValue(null), null)!;
        var serial = Activator.CreateInstance(serialType, new Point(100, 50), 1, serialSpec)!;
        docType.GetMethod("Add")!.Invoke(serialDoc, new[] { serial });
        var serialFrame = (Rect)serial.GetType().GetProperty("Frame")!.GetValue(serial)!;
        Check(CountWhiteNear(Draw(NewCanvas(serialDoc, true, true)), serialFrame) == 0,
            "序号徽标不给缩放手柄（固定尺寸，只能移动）");

        // 真窗口：选中那条矩形 → 出现把手 → 拖右下角把手 → 外接框变大 → 换工具把手收起
        InvokeWith(window, "SelectMark", 0);
        var surface = Field(window, "Surface")!;
        Check((bool)surface.GetType().GetProperty("ShowHandles")!.GetValue(surface)!,
            "选中标注后画面进入「画把手」状态");

        var frameHandle = asm.GetType("LTools.Features.CapturePro.FrameHandle")!;
        var bottomRight = Enum.Parse(frameHandle, "BottomRight");
        InvokeWith(window, "BeginMarkResize", new Point(503, 353), bottomRight);
        InvokeWith(window, "UpdateMarkResize", new Point(543, 383));
        InvokeWith(window, "CommitMarkResize", new Point(543, 383));

        var marks = (System.Collections.IList)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
        var frame = (Rect)marks[0]!.GetType().GetProperty("Frame")!.GetValue(marks[0])!;
        Check(Math.Abs(frame.Width - 240) < 1.5 && Math.Abs(frame.Height - 130) < 1.5,
            $"拖右下角把手后外接框跟着放大：{frame.Width:0} × {frame.Height:0}（期望 240 × 130）");

        Save(Render(window), Path.Combine(_outDir, "overlay-mark-handles-window.png"));

        InvokeWith(window, "SetTool", tool("Shape"));
        Check(!(bool)surface.GetType().GetProperty("ShowHandles")!.GetValue(surface)!,
            "换到画标注的工具后把手收起");
        InvokeWith(window, "SetTool", tool("Select"));
    }

    /// <summary>读一个像素是不是白（把手是白底方块）。</summary>
    private static bool IsWhite(BitmapSource image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.PixelWidth || y >= image.PixelHeight) return false;
        var one = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        one.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3] > 200 && pixel[0] > 231 && pixel[1] > 231 && pixel[2] > 231;
    }

    /// <summary>选中框（外扩 3 DIP）上八个把手的位置。</summary>
    private static IEnumerable<Point> GripPoints(Rect frame)
    {
        var box = frame;
        box.Inflate(3, 3);
        double cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
        yield return new Point(box.Left, box.Top);
        yield return new Point(box.Right, box.Top);
        yield return new Point(box.Left, box.Bottom);
        yield return new Point(box.Right, box.Bottom);
        yield return new Point(cx, box.Top);
        yield return new Point(cx, box.Bottom);
        yield return new Point(box.Left, cy);
        yield return new Point(box.Right, cy);
    }

    /// <summary>
    /// 八个把手位置各取 11×11 的窗口数白像素：黑底上白方块跑不掉，
    /// 又不会数到选区那条白描边（它只在画面四边）。
    /// </summary>
    private static int CountWhiteNear(BitmapSource image, Rect frame)
    {
        var conv = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buffer = new byte[w * h * 4];
        conv.CopyPixels(buffer, w * 4, 0);

        int count = 0;
        foreach (var p in GripPoints(frame))
        {
            int px = (int)Math.Round(p.X), py = (int)Math.Round(p.Y);
            for (int y = py - 5; y <= py + 5; y++)
                for (int x = px - 5; x <= px + 5; x++)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    int i = (y * w + x) * 4;
                    if (buffer[i + 3] > 200 && buffer[i] > 231 && buffer[i + 1] > 231 && buffer[i + 2] > 231)
                        count++;
                }
        }
        return count;
    }

    /// <summary>跨屏取景：显示器枚举 / 并集 / 逐块抓帧拼出来的尺寸。</summary>
    private static void ScreenArea()
    {
        var type = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.ScreenShot")!;

        var monitors = (System.Collections.IEnumerable)type.GetMethod("Monitors")!.Invoke(null, null)!;
        int count = 0;
        Int32Rect union = Int32Rect.Empty;
        bool first = true;
        foreach (var item in monitors)
        {
            count++;
            var r = (Int32Rect)item;
            if (first) { union = r; first = false; continue; }
            int left = Math.Min(union.X, r.X), top = Math.Min(union.Y, r.Y);
            int right = Math.Max(union.X + union.Width, r.X + r.Width);
            int bottom = Math.Max(union.Y + union.Height, r.Y + r.Height);
            union = new Int32Rect(left, top, right - left, bottom - top);
        }

        Check(count >= 1, $"至少枚举到一块显示器（实际 {count}）");

        var bounds = (Int32Rect)type.GetMethod("Bounds")!.Invoke(null, null)!;
        Check(bounds.Width == union.Width && bounds.Height == union.Height,
            $"取景范围 = 显示器并集：{bounds.Width}×{bounds.Height}");

        // 真抓一帧：拼出来的位图尺寸必须正好等于取景范围（逐块抓 + 贴回去不会缩）
        var shot = (BitmapSource)CallStatic(type, "Capture", bounds)!;
        Check(shot.PixelWidth == bounds.Width && shot.PixelHeight == bounds.Height,
            $"冻结画面尺寸 = 取景范围：{shot.PixelWidth}×{shot.PixelHeight}");

        // 只截一小块（左上角 40×30）：尺寸精确，且不是全透明
        var small = new Int32Rect(bounds.X, bounds.Y, 40, 30);
        var crop = (BitmapSource)CallStatic(type, "Capture", small)!;
        Check(crop.PixelWidth == 40 && crop.PixelHeight == 30, $"局部抓帧尺寸：{crop.PixelWidth}×{crop.PixelHeight}");

        var pixels = new byte[40 * 30 * 4];
        crop.CopyPixels(pixels, 40 * 4, 0);
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        Check(anyAlpha, "局部抓帧拿到了画面（不是全透明）");

        double scale = (double)type.GetMethod("ScaleOf")!.Invoke(null, new object[] { bounds })!;
        Check(scale > 0, $"缩放比读得到：{scale:0.##}");
    }

    /// <summary>
    /// 长截图（就地手动拼接）：合成帧驱动 ScrollStitch（每帧接多少行 / 固定页脚 / 末尾一屏 / 小预览），
    /// 再在真覆盖层里摆一遍长截图模式的界面（选区里 1:1 拼接画面 + 旁边的小面板 + 收起框选那套）。
    /// **不真滚桌面、不抓真窗口**：滚轮转发与 PrintWindow 抓帧都是 Win32，真机点一遍。
    /// </summary>
    private static void Scroll()
    {
        const int width = 200, viewHeight = 300, step = 100, last = 600;
        var page = NewPage(width, last + viewHeight);

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var type = asm.GetType("LTools.Features.CapturePro.ScrollStitch")!;
        var stitch = Activator.CreateInstance(type, true)!;

        var start = type.GetMethod("Start")!;
        var append = type.GetMethod("Append")!;
        var height = type.GetProperty("Extent")!;
        var sticky = type.GetProperty("StickyBottom")!;
        var tail = type.GetMethod("Tail")!;
        var previewOf = type.GetMethod("Preview")!;
        var render = type.GetMethod("Render")!;
        var finish = type.GetMethod("Finish")!;

        // ① 用户自己往下滚：每滚一格接 100 行
        start.Invoke(stitch, new object[] { View(page, 0, viewHeight) });
        var shifts = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            int offset = (i + 1) * step;
            shifts.Add((int?)append.Invoke(stitch, new object[] { View(page, offset, viewHeight) }) ?? -1);
        }

        Check(shifts.All(s => s == -step), "每一帧都认出 100 行位移（向下为负）：" + string.Join(",", shifts));
        Check((int)height.GetValue(stitch)! == viewHeight + 6 * step,
            $"拼接高度 = 起始一屏 + 6 × 100 行（{height.GetValue(stitch)}）");

        var longImage = (BitmapSource)render.Invoke(stitch, null)!;
        Check(longImage.PixelWidth == width && longImage.PixelHeight == viewHeight + 6 * step,
            $"长图尺寸 = {width} × {viewHeight + 6 * step}（实际 {longImage.PixelWidth} × {longImage.PixelHeight}）");

        int stride = width * 4;
        var got = new byte[stride];
        var want = new byte[stride];
        int bad = 0;
        for (int y = 0; y < longImage.PixelHeight; y += 37)
        {
            longImage.CopyPixels(new Int32Rect(0, y, width, 1), got, stride, 0);
            page.CopyPixels(new Int32Rect(0, y, width, 1), want, stride, 0);
            if (!SameRow(got, want)) bad++;
        }
        Check(bad == 0, $"拼出来的每一行都与整页对得上（对不上 {bad} 行）");
        Save(longImage, Path.Combine(_outDir, "scroll-long.png"));

        // ② 就地显示：选区里只要**末尾一屏**（每帧只搬这几行，不重画整张长图）
        var tailImage = (BitmapSource)tail.Invoke(stitch, new object[] { viewHeight })!;
        Check(tailImage.PixelWidth == width && tailImage.PixelHeight == viewHeight,
            $"末尾一屏 = {width} × {viewHeight}（实际 {tailImage.PixelWidth} × {tailImage.PixelHeight}）");

        var tailLast = new byte[stride];
        var imageLast = new byte[stride];
        tailImage.CopyPixels(new Int32Rect(0, viewHeight - 1, width, 1), tailLast, stride, 0);
        longImage.CopyPixels(new Int32Rect(0, longImage.PixelHeight - 1, width, 1), imageLast, stride, 0);
        Check(SameRow(tailLast, imageLast), "末尾一屏的最后一行 = 长图的最后一行");

        // ③ 小预览按比例装进框
        var previewImage = (BitmapSource)previewOf.Invoke(stitch, new object[] { 60, 200 })!;
        Check(previewImage.PixelWidth <= 60 && previewImage.PixelHeight <= 200,
            $"小预览装得进 60 × 200（{previewImage.PixelWidth} × {previewImage.PixelHeight}）");

        // ④ 到底 / 认不出：0 = 画面没动（调用方说「到底了」），null = 认不出重叠（这一帧不接）
        Check((int?)append.Invoke(stitch, new object[] { View(page, last, viewHeight) }) == 0,
            "滚到底后再滚一格 → 位移 0");
        Check(append.Invoke(stitch, new object[] { Noise(width, viewHeight, 7) }) == null,
            "风马牛不相及的一帧 → null");
        Check((int)height.GetValue(stitch)! == viewHeight + 6 * step, "认不出的帧不吃掉已拼好的内容");

        // ⑤ 固定页脚（置顶 / 置底按钮条）：中间不重复，收尾补一次到整张长图最下面
        const int footer = 30;
        var stickyStitch = Activator.CreateInstance(type, true)!;
        start.Invoke(stickyStitch, new object[] { FooterFrame(page, 0, footer) });
        Check((int)sticky.GetValue(stickyStitch)! == 0, "首帧还不知道有没有固定条");

        Check((int?)append.Invoke(stickyStitch, new object[] { FooterFrame(page, 60, footer) }) == -60,
            "有固定页脚也认得出位移");
        Check((int)sticky.GetValue(stickyStitch)! == footer, $"认出 {footer} 行的固定页脚");
        Check((int)height.GetValue(stickyStitch)! == viewHeight - footer + 60 + footer,
            "首帧尾部那条已经剪掉，新内容照接，成品高里含一条固定条");

        Check((int?)append.Invoke(stickyStitch, new object[] { FooterFrame(page, 120, footer) }) == -60,
            "第三帧位移 60");
        Check((int)height.GetValue(stickyStitch)! == viewHeight - footer + 120 + footer,
            "每一帧只接固定条上面那一截");

        // 固定条在 Render 时才补到成品最下面（不必再调 Finish）
        var footerRow = RowAt(FooterFrame(page, 120, footer), viewHeight - 10);
        var product = (BitmapSource)render.Invoke(stickyStitch, null)!;
        Check(product.PixelHeight == viewHeight - footer + 120 + footer,
            $"成品高度 = 内容 + 一条固定条（{product.PixelHeight}）");
        Check(CountRows(product, footerRow) == 1, "整张长图里固定条只出现一次");
        Check(SameRow(RowAt(product, product.PixelHeight - 10), footerRow), "固定条补在成品最下面");

        finish.Invoke(stickyStitch, null);
        Check((int)height.GetValue(stickyStitch)! == viewHeight - footer + 120 + footer,
            "Finish 不会再多补一条");
        Check(((BitmapSource)render.Invoke(stickyStitch, null)!).PixelHeight == product.PixelHeight,
            "再次渲染长度不变");

        // ⑥ 小面板落点（纯函数）：右侧 → 下方 → 塞右下角
        var layout = asm.GetType("LTools.Features.CapturePro.ScrollLayout")!;
        var placeIn = layout.GetMethod("PlaceIn")!;
        var right = (Rect)placeIn.Invoke(null, new object[]
        {
            new Rect(100, 100, 800, 600), new Size(220, 300), new Size(1920, 1040),
        })!;
        Check(Math.Abs(right.Left - 912) < 0.5 && Math.Abs(right.Top - 100) < 0.5,
            $"小面板优先落在选区右侧（Left={right.Left:0}）");

        var below = (Rect)placeIn.Invoke(null, new object[]
        {
            new Rect(0, 0, 400, 300), new Size(220, 300), new Size(420, 1040),
        })!;
        Check(Math.Abs(below.Left) < 0.5 && Math.Abs(below.Top - 312) < 0.5,
            $"右边放不下 → 落到选区下方（Top={below.Top:0}）");

        var corner = (Rect)placeIn.Invoke(null, new object[]
        {
            new Rect(0, 0, 1920, 1040), new Size(220, 300), new Size(1920, 1040),
        })!;
        Check(corner.Right <= 1909 && corner.Bottom <= 1029, "选区占满 → 塞回窗口右下角");

        // ⑦ 就地界面：真覆盖层里摆一遍长截图模式
        var shot = BuildShot(1600, 900);
        var monitor = new System.Drawing.Rectangle(0, 0, 1600, 900);
        var window = NewOverlay(shot, monitor);
        window.Show();
        Pump(window);

        var sel = new Rect(200, 150, 600, 400);
        SetSelection(window, sel);
        Pump(window);

        var fields = typeof(CaptureOverlayWindow);
        fields.GetField("_scrollMode", Private)!.SetValue(window, true);
        fields.GetField("_scrollRect", Private)!.SetValue(window, sel);
        fields.GetField("_scrollWidth", Private)!.SetValue(window, width);
        fields.GetField("_scrollHeight", Private)!.SetValue(window, longImage.PixelHeight);
        fields.GetField("_scrollShown", Private)!.SetValue(window, longImage.PixelHeight);
        fields.GetField("_scrollView", Private)!.SetValue(window, viewHeight);

        Invoke(window, "ApplyScrollChrome");
        ((Image)Field(window, "ScrollPreview")).Source = previewImage;
        InvokeWith(window, "UpdateScrollStatus", new object[] { null! });
        Pump(window);

        var toolbar = Field(window, "Toolbar") as FrameworkElement;
        var frameBorder = Field(window, "ScrollFrame") as FrameworkElement;
        var panel = Field(window, "ScrollPanel") as FrameworkElement;
        var status = (TextBlock)Field(window, "ScrollStatus");

        Check(toolbar is { Visibility: Visibility.Collapsed }, "长截图模式隐藏浮动工具栏");
        Check(frameBorder is { Visibility: Visibility.Visible }, "选区外面一圈长截图边框");
        Check(panel is { Visibility: Visibility.Visible }, "旁边工具栏放出长图缩小预览框");
        Check(status.Text.Contains("滚轮"), "状态行教用户向下滚轮" + status.Text.Replace("\n", " / "));
        Check(status.Text.Contains("自动滚动"), "状态行提到自动滚动" + status.Text.Replace("\n", " / "));


        // 面板上两颗开关：自动滚动 / 方向，各亮各的（同一个 StackPanel 里不给 GroupName 会互斥顶掉）
        var autoBtn = Field(window, "ScrollAutoBtn") as System.Windows.Controls.Primitives.ToggleButton;
        var axisBtn = Field(window, "ScrollAxisBtn") as System.Windows.Controls.Primitives.ToggleButton;
        Check(autoBtn is { Visibility: Visibility.Visible }, "面板上有「自动滚动」开关");
        Check(axisBtn is { Visibility: Visibility.Visible }, "面板上有「拼接方向」开关");
        Check(autoBtn!.IsChecked == false && axisBtn!.IsChecked == false, "默认：两个都关着");
        axisBtn.IsChecked = true;
        autoBtn.IsChecked = true;
        Check(autoBtn.IsChecked == true && axisBtn.IsChecked == true,
            "两颗开关能同时亮（自动滚动 + 横向），不会互相顶掉");
        autoBtn.IsChecked = false;
        axisBtn.IsChecked = false;

        double panelLeft = Canvas.GetLeft(panel);
        double panelTop = Canvas.GetTop(panel);
        Check(panelLeft >= sel.Right + 8 && panelTop >= sel.Top - 1,
            $"小面板落在选区右侧（{panelLeft:0},{panelTop:0}）");
        Save(Render(window), Path.Combine(_outDir, "scroll-inplace.png"));

        InvokeWith(window, "ExitScrollMode", new object[] { null! });
        Pump(window);
        Check(!(bool)fields.GetField("_scrollMode", Private)!.GetValue(window)!, "放弃后回到普通框选");
        Check(toolbar is { Visibility: Visibility.Visible }, "回到框选后工具栏又出来");
        window.Close();
    }

    /// <summary>
    /// 滚到 <paramref name="offset"/> 的一帧，底部 <paramref name="footer"/> 行换成「固定条」
    /// （每一行都不同，才能按「某一行出现了几次」数固定条）。
    /// </summary>
    private static BitmapSource FooterFrame(BitmapSource page, int offset, int footer)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * 300];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, 300), pixels, stride, 0);

        for (int y = 300 - footer; y < 300; y++)
        {
            for (int x = 0; x < page.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)(0x30 + x * 7);
                pixels[i + 1] = (byte)(0x40 + (y - (300 - footer)) * 5);
                pixels[i + 2] = 0xD0;
                pixels[i + 3] = 0xFF;
            }
        }

        var frame = BitmapSource.Create(page.PixelWidth, 300, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>位图里第 <paramref name="y"/> 行（Bgra32 字节）。</summary>
    private static byte[] RowAt(BitmapSource image, int y)
    {
        int stride = image.PixelWidth * 4;
        var row = new byte[stride];
        image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
        return row;
    }

    /// <summary>整张图里有多少行与 <paramref name="row"/> 逐字节相同（数固定条重复了几遍）。</summary>
    private static int CountRows(BitmapSource image, byte[] row)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        int count = 0;
        for (int y = 0; y < image.PixelHeight; y++)
        {
            bool same = true;
            for (int i = 0; i < stride; i++)
            {
                if (pixels[y * stride + i] != row[i]) { same = false; break; }
            }
            if (same) count++;
        }
        return count;
    }

    private static void SetField(Type type, object target, string name, object value) =>
        type.GetField(name, Private)!.SetValue(target, value);

    /// <summary>
    /// 屏幕选字：选中一块后灌一份识别结果，看候选条 / 整段文字 / 落点 / 复制出口。
    /// OCR 本身不在这里跑（要真文字与引擎），识别结果由测试直接注入。
    /// </summary>
    private static void TextSelect()
    {
        const int width = 1200, height = 800;   // 够宽：候选条能落在选区右侧
        var type = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Views.CapturePro.CaptureTextSelectWindow")!;
        var shot = BuildShot(width, height);
        var area = new System.Drawing.Rectangle(0, 0, width, height);
        var candidates = new[] { "第一句话。", "第二句话！", "第三行" };

        // ① 候选条：选中一块 → 灌结果 → 列表 / 整段文字 / 落点 / 视觉图
        var window = NewTextWindow(type, shot, area);
        PrepareTextWindow(window, width, height, candidates);

        var bar = (Border)Field(window, "ResultBar");
        Check(bar.Visibility == Visibility.Visible, "识别完成后候选条出现");
        Check(((ItemsControl)Field(window, "CandidateList")).Items.Count == 3,
            $"候选条 3 条（{((ItemsControl)Field(window, "CandidateList")).Items.Count}）");
        Check(((TextBox)Field(window, "FullTextBox")).Text.Contains("第二句"), "整段文字填进只读框");
        Check(Canvas.GetLeft(bar) >= 300, $"候选条落在选区右侧（Left={Canvas.GetLeft(bar):0}）");
        Check(Math.Abs(Canvas.GetTop(bar) - 40) < 1.5, "与选区顶边对齐");

        Save(Render(window), Path.Combine(_outDir, "text-select.png"));

        // ② 点一条候选 = 复制那一条 + 关窗
        var first = FindChild<Button>((ItemsControl)Field(window, "CandidateList"));
        Check(first != null, "候选按钮已生成");
        first!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // 剪贴板是全局独占资源，偶尔会被别的进程短暂占住（OLE 复制会静默失败），给它一会儿
        Check(WaitUntil(() => Clipboard.GetText() == "第一句话。", 2000), "点候选复制的是那一条");
        Check((bool)type.GetProperty("Copied", Private)!.GetValue(window)!, "复制后标记为已完成");

        // ③ 复制全部
        Clipboard.Clear();
        var all = NewTextWindow(type, shot, area);
        PrepareTextWindow(all, width, height, candidates);
        ((Button)Field(all, "CopyAllBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // 与上一条同一个理由：剪贴板是全局独占资源，偶尔被别的进程短暂占住
        Check(WaitUntil(() =>
        {
            try { return Clipboard.ContainsText() && Clipboard.GetText().Contains("第三行") && Clipboard.GetText().Contains("第二句"); }
            catch { return false; }
        }, 2000), "复制全部拿到整段文字");

        // ④ 取消：没有复制
        var cancel = NewTextWindow(type, shot, area);
        InvokeWith(cancel, "CloseWith", false);
        Check(!(bool)type.GetProperty("Copied", Private)!.GetValue(cancel)!, "取消不算完成");
        cancel.Close();
    }

    private static Window NewTextWindow(Type type, BitmapSource shot, System.Drawing.Rectangle area) =>
        (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { shot, area, 1.0, 1.0 }, null)!;

    /// <summary>排版一个选字窗口（不 Show），选中一块并灌一份识别结果。</summary>
    private static void PrepareTextWindow(Window window, int width, int height, string[] candidates)
    {
        var type = window.GetType();
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        Invoke(window, "PlaceHint");

        SetField(type, window, "_selection", new Rect(40, 40, 260, 70));
        Invoke(window, "UpdateChrome");
        InvokeWith(window, "ApplyResult", string.Join("\n", candidates),
            (IReadOnlyList<string>)candidates, "系统 OCR", "");
        content.UpdateLayout();
    }

    private static T FindChild<T>(DependencyObject parent) where T : class
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;

            var found = FindChild<T>(child);
            if (found != null) return found;
        }
        return null!;
    }

    /// <summary>整棵视觉树里的所有 <typeparamref name="T"/>（顺序按树的先序）。</summary>
    private static List<T> FindAll<T>(DependencyObject parent) where T : class
    {
        var found = new List<T>();
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) found.Add(typed);
            found.AddRange(FindAll<T>(child));
        }

        return found;
    }

    /// <summary>点开着的菜单里那一项（走真实菜单项的 Click，不用伪造鼠标）。</summary>
    private static void ClickMenuItem(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(i => (string?)i.Header == header)
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

    /// <summary>给控件发一次滚轮（<paramref name="delta"/> 120 = 上滚，-120 = 下滚）。</summary>
    private static void Wheel(UIElement element, int delta) =>
        element.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        { RoutedEvent = UIElement.PreviewMouseWheelEvent });

    /// <summary>找到挂在这个按钮上的悬停弹层（<c>_popovers</c> 里的某一项）。</summary>
    private static Popup? PopupOf(object window, ToggleButton anchor)
    {
        foreach (object entry in (System.Collections.IEnumerable)Field(window, "_popovers"))
            if (ReferenceEquals(Field(entry, "_anchor"), anchor))
                return (Popup?)Field(entry, "_popup");

        return null;
    }

    /// <summary>
    /// 贴图内标注：覆盖层只当编辑器——选区一开始框满整张、没有完成动作栏、
    /// 只认「完成」一个出口（别的出口被忽略、不关窗）。
    /// </summary>
    private static void AnnotateMode(BitmapSource image, double scale)
    {
        var area = new System.Drawing.Rectangle(200, 150, image.PixelWidth, image.PixelHeight);
        var type = typeof(CaptureOverlayWindow);
        var edit = new CaptureOverlayWindow(image, area, scale, scale, editOnly: true);
        edit.Show();
        Pump(edit);

        Check((bool)type.GetProperty("EditOnly", Private)!.GetValue(edit)!, "标注模式记下了（EditOnly）");

        var sel = (Rect?)type.GetField("_sel", Private)!.GetValue(edit);
        Check(sel is Rect r && Math.Abs(r.Width - edit.Width) < 0.6 && Math.Abs(r.Height - edit.Height) < 0.6,
            $"标注模式的选区一开始框满整张（{sel?.Width:0.#}×{sel?.Height:0.#} vs 窗口 {edit.Width:0.#}×{edit.Height:0.#}）");

        var parts = (System.Collections.IList)Field(Field(edit, "Toolbar"), "_actionParts");
        int hidden = 0;
        foreach (var part in parts)
            if (part is UIElement { Visibility: Visibility.Collapsed }) hidden++;
        Check(parts.Count >= 10 && hidden == parts.Count,
            $"标注模式收起完成动作那一截（{hidden}/{parts.Count}，完成走 Enter / 双击）");
        Check(ToolRow(edit)!.Children.OfType<RadioButton>().Any(b => b.Visibility == Visibility.Visible),
            "标注模式里工具那一截还在（能继续画 / 改工具）");

        var hint = Field(edit, "HintText") as TextBlock;
        Check(hint != null && hint.Text.Contains("标注这张贴图"), "提示写明这是标注贴图：" + hint?.Text);

        var hintBox = Field(edit, "HintBox") as FrameworkElement;
        double hintLeft = hintBox == null ? -1 : System.Windows.Controls.Canvas.GetLeft(hintBox);
        Check(hintBox != null && hintLeft >= 0 && hintLeft + hintBox.ActualWidth <= edit.ActualWidth + 1,
            $"提示不出窗口右边：left {hintLeft:0.#} + 宽 {hintBox?.ActualWidth:0.#} ≤ 窗口 {edit.ActualWidth:0.#}");

        Save(Render(edit), Path.Combine(_outDir, "annotate-mode.png"));

        var finish = type.GetNestedType("FinishKind", Private)!;
        var accept = type.GetMethod("Accept", Private)!;

        // 别的出口在标注模式里没意义：不关窗、不产出（否则会弹出保存对话框卡住流程）
        accept.Invoke(edit, new object[] { Enum.Parse(finish, "Save") });
        Check(edit.IsVisible && type.GetProperty("ResultImage")!.GetValue(edit) == null,
            "标注模式里「另存为」被忽略（留在覆盖层）");

        accept.Invoke(edit, new object[] { Enum.Parse(finish, "Copy") });
        Pump(edit);

        var result = (BitmapSource?)type.GetProperty("ResultImage")!.GetValue(edit);
        Check(result != null && result.PixelWidth == image.PixelWidth && result.PixelHeight == image.PixelHeight,
            $"标注模式交回成品 {result?.PixelWidth}×{result?.PixelHeight}（期望 {image.PixelWidth}×{image.PixelHeight}）");
        Check(!edit.IsVisible, "交回成品后覆盖层关掉");
    }

    /// <summary>贴图窗口：落位 / 缩放 / 锁定 / 右键菜单 / 全部关闭。</summary>
    private static void Pin(BitmapSource image, double scale)
    {
        var manager = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var area = new Int32Rect(200, 150, image.PixelWidth, image.PixelHeight);

        var pin = manager.GetMethod("Pin")!.Invoke(null, new object[] { image, area, "screenshot", null }) as Window;
        Check(pin != null && pin.IsVisible, "贴图窗口出现在屏幕上");
        if (pin == null) return;

        var type = pin.GetType();
        double expectW = image.PixelWidth / scale;
        Check(Math.Abs(pin.Width - expectW) < 1.5, $"贴图按 100% 落位：{pin.Width:0.#} DIP（期望 {expectW:0.#}）");
        Check((double)type.GetProperty("Zoom")!.GetValue(pin)! == 1, "初始缩放 100%");
        Check((bool)type.GetProperty("Topmost")!.GetValue(pin)!, "贴图默认置顶");

        Save(Render(pin), Path.Combine(_outDir, "pin.png"));

        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 2.0 });
        Check(Math.Abs(pin.Width - expectW * 2) < 1.5, $"放大到 200%：{pin.Width:0.#}");
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 99.0 });
        Check((double)type.GetProperty("Zoom")!.GetValue(pin)! == 4.0, "缩放上限 400%");

        type.GetMethod("SetLocked")!.Invoke(pin, new object[] { true });
        Check((bool)type.GetProperty("Locked")!.GetValue(pin)!, "锁定状态记下了");

        // 局部显示：窗口变固定取景框，换缩放时窗口尺寸不动、内容在框里平移
        type.GetMethod("SetLocked")!.Invoke(pin, new object[] { false });
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 1.0 });
        type.GetMethod("SetViewport")!.Invoke(pin, new object[] { true });
        Check((bool)type.GetProperty("Viewport")!.GetValue(pin)!, "局部显示打开");
        double viewW = pin.Width, viewH = pin.Height;
        double expectH = image.PixelHeight / scale;
        Check(viewW <= expectW + 1.5 && viewH <= expectH + 1.5,
            $"取景框不超过内容（{viewW:0.#}×{viewH:0.#}）");

        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 2.0 });
        Check(Math.Abs(pin.Width - viewW) < 0.5 && Math.Abs(pin.Height - viewH) < 0.5,
            $"局部显示里换缩放：窗口尺寸不动（{pin.Width:0.#}×{pin.Height:0.#}）");
        var pan = (Vector)type.GetProperty("Pan")!.GetValue(pin)!;
        Check(pan.X < -1 && pan.Y < -1, $"放大后内容往左上偏移，看的是中间那块（{pan.X:0.#}, {pan.Y:0.#}）");
        Save(Render(pin), Path.Combine(_outDir, "pin-viewport.png"));

        // 悬停提示（真实鼠标进不来，直接调处理器；它不读事件参数）
        type.GetMethod("Window_MouseEnter", Private)!.Invoke(pin, new object[] { null, null });
        Pump(pin);
        var hint = Field(pin, "ViewHint") as FrameworkElement;
        Check(hint is { Visibility: Visibility.Visible }, "局部显示里悬停出现操作提示");
        Save(Render(pin), Path.Combine(_outDir, "pin-viewport-hint.png"));

        type.GetMethod("SetViewport")!.Invoke(pin, new object[] { false });
        Check(Math.Abs(pin.Width - expectW * 2) < 1.5, "退出局部显示：窗口回到整张图（200%）");
        Check(((Vector)type.GetProperty("Pan")!.GetValue(pin)!).X == 0, "退出后偏移归零");
        Check(hint is { Visibility: Visibility.Collapsed }, "退出局部显示后提示收起来了");

        // ============ 缩略图模式：83 DIP 小方块，整张图等比塞进去 ============
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 1.0 });
        type.GetMethod("SetThumbnail")!.Invoke(pin, new object[] { true });
        Check((bool)type.GetProperty("Thumbnail")!.GetValue(pin)!, "缩略图模式打开");
        Check(Math.Abs(pin.Width - 83) < 0.6 && Math.Abs(pin.Height - 83) < 0.6,
            $"缩略图是 83×83 的小方块（{pin.Width:0.#}×{pin.Height:0.#}）");

        var shotEl = Field(pin, "Shot") as System.Windows.Controls.Image;
        Check(shotEl != null && shotEl.Stretch == Stretch.Uniform,
            "缩略图里 Stretch=Uniform（整张等比放进去，不是只露左上角）");
        Check(shotEl != null && Math.Abs(shotEl.Width - 83) < 0.6,
            $"图按方块大小铺开（{shotEl?.Width:0.#}）");
        Save(Render(pin), Path.Combine(_outDir, "pin-thumbnail.png"));

        // 缩略图里不给悬停按钮（会盖住内容）；出口仍在双击与右键菜单
        type.GetMethod("Window_MouseEnter", Private)!.Invoke(pin, new object[] { null, null });
        Pump(pin);
        var closeBtn = Field(pin, "CloseBtn") as FrameworkElement;
        Check(closeBtn is { Visibility: Visibility.Collapsed }, "缩略图里悬停不显示 ✕");

        // 两种特殊形态互斥：开局部显示会先把缩略图还原
        type.GetMethod("SetViewport")!.Invoke(pin, new object[] { true });
        Check(!(bool)type.GetProperty("Thumbnail")!.GetValue(pin)!, "开局部显示时缩略图退出");
        Check((bool)type.GetProperty("Viewport")!.GetValue(pin)!, "局部显示接管（缩略图让位）");
        type.GetMethod("SetViewport")!.Invoke(pin, new object[] { false });

        // 缩略图里改缩放 = 先还原（方块里缩放没有意义）
        type.GetMethod("SetThumbnail")!.Invoke(pin, new object[] { true });
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 1.0 });
        Check(!(bool)type.GetProperty("Thumbnail")!.GetValue(pin)!, "改缩放先退出缩略图");
        Check(shotEl != null && shotEl.Stretch == Stretch.Fill, "回到整张图后 Stretch 复位 Fill");
        Check(Math.Abs(pin.Width - expectW) < 1.5,
            $"退出缩略图按当前缩放回到整张图（{pin.Width:0.#} DIP）");
        // 回到整张图后**重新悬停**才给 ✕：中间开过局部显示（窗口一度铺满工作区、盖住真光标），
        // 缩回去时 WPF 会真的发一次 MouseLeave 把悬停态清掉——那是正确行为，不是 bug。
        type.GetMethod("Window_MouseEnter", Private)!.Invoke(pin, new object[] { null, null });
        Pump(pin);
        Check(closeBtn is { Visibility: Visibility.Visible }, "正常尺寸又给回悬停 ✕");

        // 成组移动：拖任意一张，全组走同一个位移
        var pin2 = manager.GetMethod("Pin")!.Invoke(null, new object[] { image, new Int32Rect(600, 400, image.PixelWidth, image.PixelHeight), "screenshot", null }) as Window;
        Check(pin2 != null && pin2.IsVisible, "第二张贴图出现");
        if (pin2 != null)
        {
            manager.GetProperty("GroupMove")!.SetValue(null, true);
            pin.Left = 100; pin.Top = 100;
            pin2.Left = 700; pin2.Top = 500;

            manager.GetMethod("BeginMove")!.Invoke(null, new object[] { pin });
            manager.GetMethod("MoveBy")!.Invoke(null, new object[] { pin, 24d, 16d });
            Check(Math.Abs(pin.Left - 124) < 0.01 && Math.Abs(pin2.Left - 724) < 0.01,
                $"成组移动：两张一起走 X（{pin.Left:0.#} / {pin2.Left:0.#}）");
            Check(Math.Abs(pin.Top - 116) < 0.01 && Math.Abs(pin2.Top - 516) < 0.01,
                $"成组移动：两张一起走 Y（{pin.Top:0.#} / {pin2.Top:0.#}）");

            manager.GetMethod("EndMove")!.Invoke(null, null);
            manager.GetProperty("GroupMove")!.SetValue(null, false);
        }

        // 拖动 = 「现在 − 按下时」的**屏幕**坐标：窗口跟着光标走，用窗口内坐标会互相抵消
        // （每帧只落下最后一个增量再回弹 = 用户说的「拖影严重」）。起点快照 + 绝对位移才是对的。
        pin.Left = 300; pin.Top = 200;
        type.GetField("_moveStart", Private)!.SetValue(pin, new Point(1000, 1000));
        manager.GetMethod("BeginMove")!.Invoke(null, new object[] { pin });
        type.GetMethod("DragTo", Private)!.Invoke(pin, new object[] { new Point(1100, 1050) });
        // WPF 会把窗口位置吸到物理像素（250 DIP × 1.25 = 312.5 → 250.4），容差给 1 DIP
        Check(Math.Abs(pin.Left - 400) < 1 && Math.Abs(pin.Top - 250) < 1,
            $"拖到光标位置：右 100 / 下 50 → Left/Top = {pin.Left:0.#} / {pin.Top:0.#}");
        type.GetMethod("DragTo", Private)!.Invoke(pin, new object[] { new Point(1000, 1000) });
        Check(Math.Abs(pin.Left - 300) < 1 && Math.Abs(pin.Top - 200) < 1,
            $"拖回起点就回到原位（位移是绝对值，不是累加）：{pin.Left:0.#} / {pin.Top:0.#}");
        manager.GetMethod("EndMove")!.Invoke(null, null);

        // 右键菜单：每次现建，含缩放 / 不透明度两个子菜单与「关闭全部贴图」
        var menu = (ContextMenu)type.GetMethod("BuildMenu", Private)!.Invoke(pin, null)!;
        int items = menu.Items.Count;
        bool hasZoom = false, hasOpacity = false, hasCloseAll = false, hasViewport = false, hasGroup = false;
        bool hasAnnotate = false, hasThumb = false;
        foreach (var item in menu.Items)
            if (item is MenuItem mi)
            {
                if (mi.Header?.ToString() == "缩放") hasZoom = mi.Items.Count >= 4;
                if (mi.Header?.ToString() == "不透明度") hasOpacity = mi.Items.Count >= 4;
                if (mi.Header?.ToString() == "关闭全部贴图") hasCloseAll = true;
                if (mi.Header?.ToString() == "局部显示") hasViewport = true;
                if (mi.Header?.ToString() == "成组移动") hasGroup = true;
                if (mi.Header?.ToString() == "标注…") hasAnnotate = true;
                if (mi.Header?.ToString() == "缩略图模式") hasThumb = true;
            }
        Check(items >= 12, $"右键菜单条目齐（{items} 项）");
        Check(hasZoom && hasOpacity, "右键菜单有缩放 / 不透明度子菜单");
        Check(hasViewport && hasGroup, "右键菜单有「局部显示」与「成组移动」");
        Check(hasAnnotate && hasThumb, "右键菜单有「标注…」与「缩略图模式」");
        Check(hasCloseAll, "右键菜单有「关闭全部贴图」");

        // 右键菜单的「贴图管理…」入口：点它真的打开管理页（托盘菜单那一项走同一个 Toggle）
        var managerItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header?.ToString() == "贴图管理…");
        Check(managerItem != null, "右键菜单有「贴图管理…」");
        if (managerItem != null)
        {
            managerItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(pin);
            var managerWindow = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType().Name == "PinManagerWindow");
            Check(managerWindow != null, "点「贴图管理…」真的打开管理页");
            managerWindow?.Close();
            Pump(pin);
        }

        int count = (int)manager.GetProperty("Count")!.GetValue(null)!;
        Check(count == 2, $"贴图组登记了 {count} 张");

        manager.GetMethod("CloseAll")!.Invoke(null, null);
        Check(!pin.IsVisible, "关闭全部贴图后窗口不见了");
        if (pin2 != null) Check(!pin2.IsVisible, "第二张也不见了");
        Check((int)manager.GetProperty("Count")!.GetValue(null)! == 0, "贴图组清空");

        // 右上角 ✕：窗口级 Preview 处理器（按下 = 开始拖动 + CaptureMouse）不能把它吞掉，
        // 否则按钮永远收不到 MouseUp、Click 不触发，用户得连点好几次凑出双击才关得掉。
        var pin3 = manager.GetMethod("Pin")!.Invoke(null, new object[] { image, area, "screenshot", null }) as Window;
        Check(pin3 != null, "第三张贴图（验 ✕）");
        if (pin3 != null)
        {
            var close = (Button)Field(pin3, "CloseBtn");
            close.Visibility = Visibility.Visible;
            close.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            });
            Check(!pin3.IsMouseCaptured, "点 ✕ 不会开始拖动（窗口不抓鼠标）");
            Check(pin3.IsVisible, "按下 ✕ 时还没关（等 Click）");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(pin3);
            Check(!pin3.IsVisible, "✕ 点一次就关掉");
            Check((int)manager.GetProperty("Count")!.GetValue(null)! == 0, "✕ 关掉的贴图也从组里摘掉了");
        }

        // ============ 贴图历史（PinStore）：贴出来就落一条，关掉补关闭时间 ============
        var pinStore = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.PinStore")!;
        var pinIdProp = typeof(CapturePinWindow).GetProperty("PinId",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        var pin4 = manager.GetMethod("Pin")!.Invoke(null, new object[] { image, area, "screenshot", null }) as Window;
        Check(pin4 != null, "第四张贴图（验贴图历史）");
        if (pin4 != null)
        {
            string id = (string)pinIdProp.GetValue(pin4)!;
            Check(id.Length > 0, "贴图带上了历史记录 Id");

            // 图片是后台写的；等它落盘（最多 2 秒）
            string asset = (string)pinStore.GetMethod("AssetPath")!.Invoke(null, new object[] { id })!;
            for (int i = 0; i < 40 && !File.Exists(asset); i++) Thread.Sleep(50);
            Check(File.Exists(asset), "贴图原图落进 pins/assets：" + asset);

            var records = (System.Collections.IList)pinStore.GetMethod("Load")!.Invoke(null, null)!;
            object record = null;
            foreach (var item in records)
                if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) record = item;
            Check(record != null, "贴图历史里能找到这条记录");
            if (record != null)
            {
                var rt = record.GetType();
                Check((string)rt.GetProperty("Source")!.GetValue(record)! == "screenshot",
                    "来源记成截图");
                Check((string)rt.GetProperty("Group")!.GetValue(record)! == "默认", "分组记成默认");
                Check((int)rt.GetProperty("Width")!.GetValue(record)! == image.PixelWidth, "记下了图像宽度");
                Check((int)rt.GetProperty("AreaX")!.GetValue(record)! == area.X, "记下了源区域 X");
                Check((bool)rt.GetProperty("IsOpen")!.GetValue(record)!, "还开着 = 没有关闭时间");

                pin4.Close();
                Pump(pin4);

                var after = (System.Collections.IList)pinStore.GetMethod("Load")!.Invoke(null, null)!;
                object closed = null;
                foreach (var item in after)
                    if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) closed = item;
                Check(closed != null, "关掉后记录还在（历史不是会话态）");
                if (closed != null)
                {
                    Check(!(bool)closed.GetType().GetProperty("IsOpen")!.GetValue(closed)!,
                        "关掉后补上了关闭时间");
                    Check(Math.Abs((double)closed.GetType().GetProperty("Left")!.GetValue(closed)! - pin4.Left) < 1,
                        "关闭时记下最后落点 X");
                }
            }

            // 冒烟自己写进去的记录收掉，别污染真实数据
            pinStore.GetMethod("Remove")!.Invoke(null, new object[] { id });
            var cleaned = (System.Collections.IList)pinStore.GetMethod("Load")!.Invoke(null, null)!;
            bool still = false;
            foreach (var item in cleaned)
                if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) still = true;
            Check(!still, "删除后历史里没有这条了");
        }
    }

    /// <summary>
    /// 贴图分组 + 多选批量：建组 / 移动 / 切组隐藏与恢复、Ctrl+单击多选、批量拖动 / 锁定 / 缩略图 /
    /// 关闭、空组自动消失、右键菜单条目与批量另存落盘。
    /// </summary>
    private static void PinGroupsAndBatch(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var manager = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var saveType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureSave")!;

        object Call(string name, params object[] args) => manager.GetMethod(name, Stat)!.Invoke(null, args);
        object Get(string name) => manager.GetProperty(name, Stat)!.GetValue(null);
        static int N(object list) => list is System.Collections.IEnumerable e ? e.Cast<object>().Count() : -1;
        static string GroupOf(Window pin) => (string)pin.GetType().GetProperty("Group")!.GetValue(pin)!;
        static bool SelectedOf(Window pin) => (bool)pin.GetType().GetProperty("Selected")!.GetValue(pin)!;
        static bool LockedOf(Window pin) => (bool)pin.GetType().GetProperty("Locked")!.GetValue(pin)!;
        static bool ThumbOf(Window pin) => (bool)pin.GetType().GetProperty("Thumbnail")!.GetValue(pin)!;

        var p1 = (Window)Call("Pin", image, new Int32Rect(100, 100, image.PixelWidth, image.PixelHeight), "screenshot", null)!;
        var p2 = (Window)Call("Pin", image, new Int32Rect(500, 300, image.PixelWidth, image.PixelHeight), "screenshot", null)!;
        var p3 = (Window)Call("Pin", image, new Int32Rect(900, 500, image.PixelWidth, image.PixelHeight), "screenshot", null)!;
        Check(p1.IsVisible && p2.IsVisible && p3.IsVisible, "三张贴图都在屏幕上");
        Check(N(Get("Groups")) == 1, "还没建组时名单里只有默认组");
        Check(Get("ActiveGroup") == null, "默认显示全部分组");
        Check(GroupOf(p1) == "默认", "新贴图落在默认组");

        // ============ 分组名：清洗 / 判重 / 空名字 ============
        Check((string)Call("CreateGroup", "  外部  ") == "外部", "建组时清洗名字");
        Check((string)Call("CreateGroup", "外部") == "外部", "同名（忽略大小写）不建第二个");
        Check(Call("CreateGroup", "   ") == null, "空名字建不出组");
        Check(N(Get("Groups")) == 2, "名单里现在有两个组");

        Call("MoveToGroup", p2, "素材");
        Check(GroupOf(p2) == "素材", "挪到一个不存在的组：先建再挪");
        Check(N(Get("Groups")) == 3, "按需建组后名单有三项");
        var groupBadge1 = Field(p1, "GroupBadge") as FrameworkElement;
        var groupBadge2 = Field(p2, "GroupBadge") as FrameworkElement;
        Check(groupBadge2 is { Visibility: Visibility.Visible }, "不在默认组的贴图挂分组徽标");
        Check(groupBadge1 is { Visibility: Visibility.Collapsed }, "默认组不挂徽标（不给每张图挂一行字）");
        Save(Render(p2), Path.Combine(_outDir, "pin-group-badge.png"));

        // ============ 切组 = 隐藏，不是关闭 ============
        double keepLeft = p2.Left, keepTop = p2.Top;
        Call("SetActiveGroup", "素材");
        Check(!p1.IsVisible && !p3.IsVisible, "只看某个组时别的组藏起来");
        Check(p2.IsVisible, "当前组的贴图留着");
        Check(N(Get("Visible")) == 1 && N(Get("All")) == 3, "看得见 1 张 / 登记处仍是 3 张");
        Save(Render(p2), Path.Combine(_outDir, "pin-group-filter.png"));

        Call("SetActiveGroup", (object)null);
        Check(p1.IsVisible && p3.IsVisible, "显示全部分组：藏起来的又回来了");
        Check(Math.Abs(p2.Left - keepLeft) < 0.01 && Math.Abs(p2.Top - keepTop) < 0.01,
            "切回来位置不变（隐藏不是关闭）");

        // ============ Ctrl+单击 = 多选 ============
        Check(!SelectedOf(p1), "一开始没有选中");
        Call("ToggleSelect", p1);
        Check(SelectedOf(p1), "Ctrl+单击选中");
        Check(Field(p1, "SelectionBadge") is FrameworkElement { Visibility: Visibility.Visible },
            "选中后挂「已选」徽标");
        var frame = (Border)Field(p1, "Frame");
        Check(Math.Abs(frame.BorderThickness.Left - 2) < 0.01, "选中的边框加粗到 2px");
        Save(Render(p1), Path.Combine(_outDir, "pin-selected.png"));

        Call("ToggleSelect", p1);
        Check(!SelectedOf(p1), "再点一次取消选中");
        Check(Math.Abs(frame.BorderThickness.Left - 1) < 0.01, "取消后边框回到 1px");

        Call("SelectAll");
        Check(N(Get("Selected")) == 3, "全选 = 看得见的全部");

        // ============ 批量拖动：多选优先于成组移动 ============
        p1.Left = 100; p1.Top = 100;
        p2.Left = 400; p2.Top = 300;
        p3.Left = 700; p3.Top = 500;
        Call("ClearSelection");
        Call("ToggleSelect", p1);
        Call("ToggleSelect", p2);
        Call("BeginMove", p1);
        Call("MoveBy", p1, 30d, 20d);
        Check(Math.Abs(p1.Left - 130) < 1.5 && Math.Abs(p2.Left - 430) < 1.5,
            $"多选拖动：选中的两张一起走（{p1.Left:0.#} / {p2.Left:0.#}）");
        Check(Math.Abs(p3.Left - 700) < 1.5, $"没选中的不动（{p3.Left:0.#}）");
        Call("EndMove");

        Call("BeginMove", p3);
        Check(N(Get("Selected")) == 0, "拖一张没选中的贴图 = 收起多选");
        Call("EndMove");

        // ============ 批量锁定 / 缩略图 ============
        Call("SelectAll");
        Call("SetSelectedLocked", true);
        Check(LockedOf(p1) && LockedOf(p2) && LockedOf(p3), "批量锁定");
        Call("SetSelectedLocked", false);
        Check(!LockedOf(p1) && !LockedOf(p2), "批量解锁");

        Call("SetSelectedThumbnail", true);
        Check(ThumbOf(p1) && ThumbOf(p2), "批量缩略图");
        Check(Field(p1, "SelectionBadge") is FrameworkElement { Visibility: Visibility.Collapsed },
            "缩略图方块里不挂徽标");
        Save(Render(p1), Path.Combine(_outDir, "pin-batch-thumbnail.png"));
        Call("SetSelectedThumbnail", false);
        Check(!ThumbOf(p1), "批量还原缩略图");
        Check(Field(p1, "SelectionBadge") is FrameworkElement { Visibility: Visibility.Visible },
            "还原后徽标回来");

        // ============ 右键菜单：分组 / 批量 ============
        var windowType = p1.GetType();
        var menu = (ContextMenu)windowType.GetMethod("BuildMenu", Private)!.Invoke(p1, null)!;
        MenuItem groupMenu = null, batchMenu = null;
        foreach (var item in menu.Items)
            if (item is MenuItem mi)
            {
                if ((mi.Header?.ToString() ?? "") == "分组") groupMenu = mi;
                if ((mi.Header?.ToString() ?? "").StartsWith("批量（已选 3 张）", StringComparison.Ordinal))
                    batchMenu = mi;
            }

        static bool Has(ItemsControl parent, string header) =>
            parent != null && parent.Items.OfType<MenuItem>().Any(mi =>
                (mi.Header == null ? "" : mi.Header.ToString() ?? "").StartsWith(header, StringComparison.Ordinal));

        Check(groupMenu != null, "右键菜单有「分组」子菜单");
        Check(groupMenu != null && Has(groupMenu, "移到「默认」") && Has(groupMenu, "移到「素材」"),
            "分组子菜单列出每个分组");
        Check(groupMenu != null && Has(groupMenu, "新建分组并移入…") &&
              Has(groupMenu, "显示全部分组") && Has(groupMenu, "只看「素材」"),
            "分组子菜单有新建 / 显示全部 / 只看");

        Check(batchMenu != null, "「批量」标题带已选张数");
        Check(Has(batchMenu, "全选") && Has(batchMenu, "取消选择"), "批量子菜单有全选 / 取消选择");
        Check(Has(batchMenu, "关闭所选（3 张）") && Has(batchMenu, "另存所选…"),
            "批量子菜单有关闭所选 / 另存所选");
        Check(Has(batchMenu, "锁定所选") && Has(batchMenu, "缩略图所选"), "批量子菜单有锁定 / 缩略图");
        Check(Has(batchMenu, "按住 Ctrl"), "批量子菜单底部有「Ctrl+单击」提示");

        // ============ 空组自动消失 ============
        Call("ClearSelection");
        Call("SetActiveGroup", "素材");
        Call("ToggleSelect", p2);
        Call("MoveToGroup", p3, "外部");           // 让「外部」还有贴图，验证它不会被顺手清掉
        int before = (int)Get("Count")!;
        Call("CloseSelected");
        Check((int)Get("Count")! == before - 1, "关闭所选真的关掉一张");
        Check(N(Get("Groups")) == 2, "空掉的分组自己消失，还有人的留着");
        Check(Get("ActiveGroup") == null, "正看着的组被关空 → 回到显示全部");
        Check(p1.IsVisible && p3.IsVisible, "剩下两张重新露出来");

        // ============ 批量另存 ============
        string dir = Path.Combine(_outDir, "pin-batch");
        var saved = ((System.Collections.IEnumerable)saveType
            .GetMethod("SaveManyTo", Stat)!.Invoke(null, new object[] { dir, new[] { image, image } })!)
            .Cast<string>().ToArray();
        Check(saved.Length == 2 && saved.All(File.Exists), "批量另存：两张都落盘");
        Check(saved[0].EndsWith("-1.png", StringComparison.Ordinal), "按序号命名");

        Call("CloseAll");
        Check((int)Get("Count")! == 0 && N(Get("Groups")) == 1, "关闭全部贴图后组清空（只剩默认组）");
    }

    /// <summary>
    /// 实时贴图：跟住源窗口刷新 / 右下角「● 实时」徽标 / 菜单里的「转为图像（停止实时）」/
    /// 停止后就是普通贴图。抓帧注入合成帧，不碰真桌面（真 <c>PrintWindow</c> 只能真机验）。
    /// </summary>
    private static void PinLive(BitmapSource image, double scale)
    {
        var type = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Views.CapturePro.CapturePinWindow")!;
        var area = new Int32Rect(300, 200, 240, 160);
        var pin = (Window)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public, null, new object[] { image, area, scale }, null)!;
        pin.Show();
        Pump(pin);

        // 抓帧注入：DWM 缩略图路线**不该**调用它（零抓帧 = 零延迟）；退回抓帧的路线才会用
        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            pixels[i] = 0x20;
            pixels[i + 1] = 0x40;
            pixels[i + 2] = 0xE0;
            pixels[i + 3] = 0xFF;
        }
        var liveFrame = BitmapSource.Create(area.Width, area.Height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        liveFrame.Freeze();

        int calls = 0;
        type.GetField("LiveFrame", Private)!.SetValue(pin,
            new Func<IntPtr, Int32Rect, BitmapSource>((_, _) => { calls++; return liveFrame; }));

        // 源窗口 = 另一个真实窗口（拿自己当源是退化情形，DWM 缩略图也不该这么用）
        var source = new Window
        {
            Width = 260,
            Height = 180,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Background = Brushes.DarkSlateBlue,
        };
        source.Show();
        Pump(source);

        var hwnd = new System.Windows.Interop.WindowInteropHelper(source).Handle;
        type.GetMethod("StartLive", Private)!.Invoke(pin, new object[] { hwnd });
        Check((bool)type.GetProperty("Live")!.GetValue(pin)!, "开始实时：Live = true");
        Nudge(pin);

        var state = type.GetMethod("RefreshLive", Private)!.Invoke(pin, null);
        Nudge(pin);

        Check((state?.ToString() ?? "") == "Live", "源窗口在、没最小化 → 这一拍是 Live");
        var shotEl = Field(pin, "Shot") as Image;
        var layer = type.GetField("_thumbLayer", Private)!.GetValue(pin);
        if (layer != null)
        {
            // 主路线：DWM 缩略图画在下面那层（宿主窗口），一帧都不抓
            Check((bool)layer.GetType().GetProperty("Attached")!.GetValue(layer)!,
                "实时内容层挂上了（LiveThumbnail.Attached）");
            Check(calls == 0, "DWM 缩略图路线一帧都不抓（零抓帧 = 零延迟）");
            Check(shotEl is { Visibility: Visibility.Collapsed },
                "缩略图画在下面那层：WPF 那张图收起来（鼠标穿透到宿主再转发）");

            type.GetMethod("RefreshLive", Private)!.Invoke(pin, null);
            Pump(pin);
            Check(calls == 0, "再跑一拍仍然不抓帧（画面由 DWM 实时合成）");
        }
        else
        {
            // 退路：系统不吃缩略图（远程桌面 / 老驱动）→ 逐帧抓图，内容走 Shot.Source
            Check(calls > 0, "退回抓帧时真的调到了抓帧源");
            for (int i = 0; i < 25 && !ReferenceEquals(shotEl?.Source, liveFrame); i++)
            {
                type.GetMethod("RefreshLive", Private)!.Invoke(pin, null);
                System.Threading.Thread.Sleep(20);
                Nudge(pin);
            }

            Check(shotEl?.Source != null && ReferenceEquals(shotEl.Source, liveFrame),
                "贴图内容换成了实时那一帧");
        }

        var badge = Field(pin, "LiveBadge") as FrameworkElement;
        var badgeText = Field(pin, "LiveBadgeText") as TextBlock;
        Check(badge is { Visibility: Visibility.Visible }, "右下角挂上实时徽标");
        Check(badgeText != null && badgeText.Text.Contains("实时"), "徽标写着「实时」：" + badgeText?.Text);
        Check(Math.Abs(pin.Width - area.Width / scale) < 1.5,
            $"实时贴图尺寸仍是选区那块（{pin.Width:0.#} DIP）");
        Save(Render(pin), Path.Combine(_outDir, "pin-live.png"));

        // 菜单：最上面是「转为图像（停止实时）」，会破坏实时的东西置灰
        var menu = (ContextMenu)type.GetMethod("BuildMenu", Private)!.Invoke(pin, null)!;
        var headers = menu.Items.OfType<MenuItem>()
            .Select(mi => mi.Header?.ToString() ?? "").ToList();
        Check(headers.Count > 0 && headers[0] == "转为图像（停止实时）",
            "实时贴图的菜单第一项是「转为图像（停止实时）」：" + string.Join(" / ", headers));

        var annotate = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => (mi.Header?.ToString() ?? "").StartsWith("标注", StringComparison.Ordinal));
        var group = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => (mi.Header?.ToString() ?? "") == "分组");
        var zoom = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => (mi.Header?.ToString() ?? "") == "缩放");
        Check(annotate is { IsEnabled: false }, "实时期间「标注…」置灰（内容每 200ms 换一次）");
        Check(group is { IsEnabled: false }, "实时期间「分组 ▸」置灰");
        Check(zoom is { IsEnabled: false }, "实时期间「缩放」置灰（尺寸必须跟选区对齐）");

        // 实时里改缩放 / 缩略图都不生效；停止之后随便改
        double w0 = pin.Width;
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 2.0 });
        Check(Math.Abs(pin.Width - w0) < 0.5, "实时里缩放不改窗口尺寸");
        type.GetMethod("SetThumbnail")!.Invoke(pin, new object[] { true });
        Check(!(bool)type.GetProperty("Thumbnail")!.GetValue(pin)!, "实时里不切缩略图");

        type.GetMethod("StopLive")!.Invoke(pin, null);
        Check(!(bool)type.GetProperty("Live")!.GetValue(pin)!, "停止实时：Live = false");
        Check(shotEl?.Source != null, "停止实时后最后一帧还留着（就是普通贴图了）");
        Check(badge is { Visibility: Visibility.Collapsed }, "停止后实时徽标收起");
        type.GetMethod("SetZoom")!.Invoke(pin, new object[] { 2.0 });
        Check(Math.Abs(pin.Width - area.Width / scale * 2) < 1.5, "停止后缩放到 200% 正常");

        pin.Close();
        source.Close();
    }

    /// <summary>识别结果窗：三页内容 / 页签切换 / 复制按钮文案（不跑真 OCR，灌一份结果）。</summary>
    private static void ResultWindow()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var wordType = asm.GetType("LTools.Features.OcrWordBox")!;
        var lineType = asm.GetType("LTools.Features.OcrLineBox")!;
        var tableType = asm.GetType("LTools.Features.CapturePro.OcrTable")!;
        var resultType = asm.GetType("LTools.Features.CapturePro.RecognizeResult")!;
        var windowType = asm.GetType("LTools.Views.CapturePro.CaptureResultWindow")!;

        static void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);

        object Word(string text, int x, int y)
        {
            var word = Activator.CreateInstance(wordType)!;
            Set(word, "Text", text);
            Set(word, "X", x);
            Set(word, "Y", y);
            Set(word, "W", 60);
            Set(word, "H", 30);
            return word;
        }

        object Line(int y, string a, string b)
        {
            var words = Array.CreateInstance(wordType, 2);
            words.SetValue(Word(a, 10, y), 0);
            words.SetValue(Word(b, 130, y), 1);

            var line = Activator.CreateInstance(lineType)!;
            Set(line, "Text", a + " " + b);
            Set(line, "X", 10);
            Set(line, "Y", y);
            Set(line, "W", 300);
            Set(line, "H", 30);
            Set(line, "Confident", true);
            Set(line, "Words", words);
            return line;
        }

        var lines = Array.CreateInstance(lineType, 2);
        lines.SetValue(Line(0, "名称", "数量"), 0);
        lines.SetValue(Line(40, "苹果", "12"), 1);

        var table = tableType.GetMethod("Build")!.Invoke(null, new object[] { lines })!;

        var result = Activator.CreateInstance(resultType)!;
        Set(result, "Text", "名称 数量\r\n苹果 12");
        Set(result, "Lines", lines);
        Set(result, "Table", table);
        Set(result, "QrText", "https://example.com/ltools");
        Set(result, "QrType", "QR_CODE");
        Set(result, "Engine", "PP-OCRv6 快速");
        Set(result, "ElapsedMs", 1234L);
        Set(result, "Error", "");

        // 320 × 80 的纯色图：只给「显示原图 / 原文预览」一个真图，像素内容不参与断言
        var pixels = new byte[320 * 80 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;
            pixels[i + 1] = 0x80;
            pixels[i + 2] = 0xD0;
            pixels[i + 3] = 0xFF;
        }

        var image = BitmapSource.Create(320, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        image.Freeze();

        var window = (Window)Activator.CreateInstance(windowType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            null, new object[] { result, image }, null)!;
        window.Show();
        Pump(window);

        object Prop(string name) =>
            windowType.GetProperty(name, Private)!.GetValue(window)!;

        object Get(object target, string name) =>
            target.GetType().GetProperty(name, Private | BindingFlags.Public)!.GetValue(target)!;

        object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private | BindingFlags.Public)!.Invoke(target, args)!;

        var canvas = Field(window, "OcrCanvas");
        var model = Prop("EditModel");

        Check(((TextBlock)Field(window, "StatusText")).Text.Contains("共 2 行"),
            "状态行报出文字层行数：" + ((TextBlock)Field(window, "StatusText")).Text);
        Check((int)Get(canvas, "ImageWidth") == 320 && (int)Get(canvas, "ImageHeight") == 80,
            $"文字层画布按原图像素铺（{Get(canvas, "ImageWidth")}×{Get(canvas, "ImageHeight")}）");
        Check((int)Get(model, "LineCount") == 2, "文字层两行");
        Check(((string)Get(model, "AllText")).Contains("苹果"), "文字层带识别到的文字");
        Check((string)Prop("CurrentTab") == "text", "默认停在文字页");
        Check(((TextBlock)Field(window, "EmptyText")).Visibility == Visibility.Collapsed,
            "有文字时不显示空态");

        var tableCanvas = (FrameworkElement)Field(window, "TableCanvas");
        Check(tableCanvas.Visibility == Visibility.Visible, "表格画布可见");
        Check(((TextBlock)Field(window, "TableEmpty")).Visibility == Visibility.Collapsed,
            "有表格时不显示空态");

        var tableDocument = Get(tableCanvas, "Document");
        Check((int)Get(tableDocument, "RowCount") == 2 && (int)Get(tableDocument, "ColumnCount") == 2,
            $"表格文档 2 行 × 2 列（{Get(tableDocument, "RowCount")} / {Get(tableDocument, "ColumnCount")}）");
        Check((string)Call(tableDocument, "CellText", 1, 1) == "12", "表格文档带着识别到的文字");

        // 每次改完文档都会换成新实例：读之前重新取
        object Doc() => Get(tableCanvas, "Document");

        var undoBtn = (Button)Field(window, "TableUndoBtn");
        var redoBtn = (Button)Field(window, "TableRedoBtn");
        var mergeBtn = (Button)Field(window, "TableMergeBtn");
        var splitBtn = (Button)Field(window, "TableSplitBtn");
        var resetBtn = (Button)Field(window, "TableResetBtn");

        Check(!undoBtn.IsEnabled && !redoBtn.IsEnabled, "刚识别出来没有可撤销 / 可重做");
        Check(!mergeBtn.IsEnabled && !splitBtn.IsEnabled, "单格选区不能合并 / 拆分");
        Check(!resetBtn.IsEnabled, "没改过时「重置」不可用");
        Check(((Button)Field(window, "TableExportBtn")).IsEnabled, "有表格就能导出 xlsx");
        Check(((Button)Field(window, "TableOpenBtn")).IsEnabled, "有表格就能用默认程序打开");

        Check(((TextBox)Field(window, "QrView")).Text == "https://example.com/ltools", "二维码页有内容");
        Check(((TextBlock)Field(window, "QrInfo")).Text.Contains("QR_CODE"), "二维码页写明格式");

        // 页签：切过去看页面显隐与「复制当前页」的文案
        ((RadioButton)Field(window, "TabTable")).IsChecked = true;
        Pump(window);
        Check(((Grid)Field(window, "TablePage")).Visibility == Visibility.Visible, "切到表格页");
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制表格", "表格页的复制按钮是「复制表格」");
        Check(!((CheckBox)Field(window, "OriginalToggle")).IsEnabled, "表格页没有原图开关");

        // —— 表格页的真鼠标交互：单击进编辑 / 拖动选一片 / 合并 / 撤销 / 重置 / 复制 ——
        window.Activate();
        Pump(window);

        Point CellCenter(int row, int column)
        {
            var rect = (Rect)Call(tableCanvas, "CellRect", row, column, 1, 1);
            return tableCanvas.PointToScreen(new Point(rect.Left + rect.Width / 2,
                rect.Top + rect.Height / 2));
        }

        void TableMove(Point point, int settle = 180)
        {
            SetCursorPos((int)point.X, (int)point.Y);
            Thread.Sleep(settle);
            Pump(window);
        }

        void TableClick(Point point)
        {
            TableMove(point, 220);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(240);
            Pump(window);
        }

        // 单击（不拖）= 进编辑（上游 QTableView 的 mouseReleaseEvent）
        TableClick(CellCenter(1, 1));
        Check((bool)Get(tableCanvas, "IsEditing"), "单击单元格进入编辑");
        var cellEditor = (TextBox)Field(tableCanvas, "_editor");
        Check(cellEditor.Visibility == Visibility.Visible && cellEditor.Text == "12",
            $"编辑框带着这格的原文（{cellEditor.Text}）");

        cellEditor.Text = "99";
        Call(tableCanvas, "CommitEdit");
        Pump(window);
        Check((string)Call(Doc(), "CellText", 1, 1) == "99", "改字写回文档");
        Check(undoBtn.IsEnabled, "改完能撤销");
        Check(resetBtn.IsEnabled, "改完能重置");

        // 拖一片：第一行两格
        Point cell00 = CellCenter(0, 0), cell01 = CellCenter(0, 1);
        TableMove(cell00, 220);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        TableMove(cell01, 200);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(240);
        Pump(window);

        var selection = Get(tableCanvas, "Selection");
        Check((int)Get(selection, "RowCount") == 1 && (int)Get(selection, "ColumnCount") == 2,
            $"拖动选中一行两格（{Get(selection, "RowCount")} × {Get(selection, "ColumnCount")}）");
        Check(mergeBtn.IsEnabled, "选中两格后「合并」可用");
        Check(!splitBtn.IsEnabled, "还没合并时「拆分」不可用");
        Check(((TextBlock)Field(window, "TableInfo")).Text.Contains("选中 2 格"),
            "工具条写明选中格数：" + ((TextBlock)Field(window, "TableInfo")).Text);

        mergeBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(!(bool)Call(Doc(), "IsAnchor", 0, 1), "合并后第一行第二格归到锚点");
        Check((string)Call(Doc(), "CellText", 0, 0) == "名称\n数量", "合并把两格文字接起来");
        Check(splitBtn.IsEnabled, "合并后「拆分」可用");

        undoBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((bool)Call(Doc(), "IsAnchor", 0, 1), "撤销合并");
        Check(redoBtn.IsEnabled, "撤销后可重做");

        redoBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(!(bool)Call(Doc(), "IsAnchor", 0, 1), "重做合并");

        splitBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((bool)Call(Doc(), "IsAnchor", 0, 1), "拆分回来");

        // 复制：全选后点「复制表格」，读回来的是整份文档
        Call(tableCanvas, "SelectAll");
        Pump(window);
        System.Windows.Clipboard.Clear();
        ((Button)Field(window, "CopyBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        string copied = System.Windows.Clipboard.GetText();
        Check(copied.Contains("苹果") && copied.Contains('\t'),
            "复制到剪贴板的是 TSV：" + copied.Replace("\n", "/"));
        Check(System.Windows.Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Html) == true,
            "剪贴板里同时有 HTML（表格软件读它）");

        resetBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((string)Call(Doc(), "CellText", 1, 1) == "12", "重置回识别出来的内容");
        Check(!resetBtn.IsEnabled, "重置之后「重置」又不可用");
        Check(undoBtn.IsEnabled, "重置本身也是可撤销的一步");

        Save(Render(window), Path.Combine(_outDir, "result-table.png"));

        ((RadioButton)Field(window, "TabQr")).IsChecked = true;
        Pump(window);
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制二维码内容", "二维码页的复制按钮文案");

        // ============ Markdown / HTML 页 ============
        var conversionPage = (FrameworkElement)Field(window, "ConversionPage");
        var copyButton = (Button)Field(window, "CopyBtn");
        var statusText = (TextBlock)Field(window, "StatusText");

        var conversion = Prop("Conversion");
        string markdownSource = (string)Get(conversion, "Markdown");
        Check(markdownSource.Contains("| 名称 | 数量 |"),
            "Markdown 源码里有识别出的表格：" + markdownSource);

        ((RadioButton)Field(window, "TabMarkdown")).IsChecked = true;
        Pump(window);
        Check((string)Prop("CurrentTab") == "markdown", "切到 Markdown 页");
        Check(conversionPage.Visibility == Visibility.Visible, "Markdown 预览可见");
        Check((string)copyButton.Content == "复制 Markdown", "Markdown 页的复制按钮文案");
        Check(statusText.Text.Contains("Markdown"), "状态行写明 Markdown：" + statusText.Text);
        Check(copyButton.IsEnabled, "有内容时「复制 Markdown」可用");

        // 预览能划选：全选之后状态行报选中的字符数
        Call(conversionPage, "SelectAllText");
        Pump(window);
        string selectedPreview = (string)Get(conversionPage, "SelectedText");
        Check(selectedPreview.Contains("名称") && selectedPreview.Contains("苹果"),
            "预览里全选能拿到渲染出来的文字：" + selectedPreview.Replace("\r\n", "/"));

        System.Windows.Clipboard.Clear();
        copyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        string copiedPreview = System.Windows.Clipboard.GetText();
        Check(copiedPreview.Contains("名称") && !copiedPreview.Contains("| ---"),
            "选了就复制选中那段（不是整份源码）：" + copiedPreview.Replace("\r\n", "/"));

        Save(Render(window), Path.Combine(_outDir, "result-markdown.png"));

        // HTML 页：重新渲染会清掉选区，复制的是整份源码，并同时写 text/html
        ((RadioButton)Field(window, "TabHtml")).IsChecked = true;
        Pump(window);
        Check((string)Prop("CurrentTab") == "html", "切到 HTML 页");
        Check((string)copyButton.Content == "复制 HTML", "HTML 页的复制按钮文案");
        Check(statusText.Text.Contains("HTML"), "状态行写明 HTML：" + statusText.Text);

        System.Windows.Clipboard.Clear();
        copyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        string copiedHtml = System.Windows.Clipboard.GetText();
        Check(copiedHtml.Contains("<table>") && copiedHtml.Contains("<th>名称</th>"),
            "没选就复制整份 HTML 源码：" + copiedHtml);
        Check(System.Windows.Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Html) == true,
            "HTML 页同时写 text/html 剪贴板格式");

        Save(Render(window), Path.Combine(_outDir, "result-html.png"));

        // ============ 文档页 ============
        var documentPage = (FrameworkElement)Field(window, "DocumentPage");
        var documentView = (FrameworkElement)Field(window, "DocumentView");
        var docInfo = (TextBlock)Field(window, "DocInfo");
        var docExportBtn = (Button)Field(window, "DocExportBtn");
        var docOpenBtn = (Button)Field(window, "DocOpenBtn");

        var exportMethod = windowType
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(m => m.Name == "ExportDocument" && m.GetParameters().Length == 1);
        bool Export(string path) => (bool)exportMethod.Invoke(window, new object[] { path })!;
        string DocumentError() => (string)windowType
            .GetProperty("DocumentError", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(window)!;

        ((RadioButton)Field(window, "TabDocument")).IsChecked = true;
        Pump(window);
        Check((string)Prop("CurrentTab") == "document", "切到文档页");
        Check(documentPage.Visibility == Visibility.Visible, "文档页可见");
        Check(documentView.Visibility == Visibility.Visible, "文档预览可见");
        Check((string)copyButton.Content == "复制文档", "文档页的复制按钮文案");
        Check(statusText.Text.Contains("文档"), "状态行写明文档：" + statusText.Text);
        Check(docInfo.Text.Contains("1 块"), "文档底栏报块数：" + docInfo.Text);
        Check(docExportBtn.IsEnabled && docOpenBtn.IsEnabled, "有内容时导出 / 打开都可用");

        string documentText = (string)Get(documentView, "SelectedText");
        Call(documentView, "SelectAllText");
        Pump(window);
        documentText = (string)Get(documentView, "SelectedText");
        Check(documentText.Contains("名称") && documentText.Contains("苹果"),
            "文档预览全选拿到渲染出来的文字：" + documentText.Replace("\r\n", "/"));

        System.Windows.Clipboard.Clear();
        copyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(System.Windows.Clipboard.GetText().Contains("苹果"), "文档复制拿到选中那段");
        Check(System.Windows.Clipboard.GetDataObject()?.GetDataPresent(DataFormats.Html) == true,
            "文档复制同时写 text/html（富文本）");

        Save(Render(window), Path.Combine(_outDir, "result-document.png"));

        // —— 导出四种本地格式（PDF 看机器上有没有 Edge / Chrome）——
        string folder = Path.Combine(_outDir, "document-export");
        Directory.CreateDirectory(folder);

        string mdPath = Path.Combine(folder, "doc.md");
        Check(Export(mdPath) && File.Exists(mdPath), "导出 Markdown");
        Check(File.ReadAllText(mdPath).Contains("| --- |"), "Markdown 文件里有表格分隔行");

        string htmlPath = Path.Combine(folder, "doc.html");
        Check(Export(htmlPath) && File.Exists(htmlPath), "导出 HTML");
        string exportedHtml = File.ReadAllText(htmlPath);
        Check(exportedHtml.Contains("<table>") && exportedHtml.Contains("<meta charset=\"utf-8\">"),
            "HTML 文件是独立页（有字符集 + 表格）");

        string texPath = Path.Combine(folder, "doc.tex");
        Check(Export(texPath) && File.Exists(texPath), "导出 LaTeX");
        Check(File.ReadAllText(texPath).Contains("\\begin{tabular}{ll}"), "LaTeX 文件里有表格环境");

        string docxPath = Path.Combine(folder, "doc.docx");
        Check(Export(docxPath) && File.Exists(docxPath), "导出 Word");
        bool docxOk = false;
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(docxPath);
            docxOk = zip.GetEntry("word/document.xml") != null;
        }
        catch (Exception ex)
        {
            Console.WriteLine("docx 读不开：" + ex.Message);
        }

        Check(docxOk, "docx 里有 word/document.xml");

        string? pdfEngine = (string?)asm.GetType("LTools.Features.CapturePro.DocumentExport")!
            .GetMethod("PdfEngine", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, null);
        string pdfPath = Path.Combine(folder, "doc.pdf");
        bool pdfOk = Export(pdfPath);
        if (pdfEngine != null)
        {
            Check(pdfOk && File.Exists(pdfPath),
                "导出 PDF（引擎 " + Path.GetFileName(pdfEngine) + "）：" + DocumentError());
            byte[] header = pdfOk ? File.ReadAllBytes(pdfPath).Take(4).ToArray() : Array.Empty<byte>();
            Check(header.Length == 4 && System.Text.Encoding.ASCII.GetString(header) == "%PDF",
                "PDF 文件头是 %PDF");
        }
        else
        {
            Check(!pdfOk && DocumentError().Contains("Edge"),
                "没装浏览器时 PDF 明确报错而不是假装成功：" + DocumentError());
        }

        // —— 打开文档：写临时 docx（冒烟不启动 Word，只验文件是好的）——
        string? temporary = (string?)Call(window, "PrepareDocumentFile");
        Check(temporary is { Length: > 0 } && File.Exists(temporary), "打开文档写出了临时 docx");
        if (temporary != null)
        {
            bool temporaryOk = false;
            try
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(temporary);
                temporaryOk = zip.GetEntry("word/document.xml") != null;
            }
            catch (Exception)
            {
                // 断言会给红
            }

            Check(temporaryOk, "临时 docx 结构有效");
        }

        ((RadioButton)Field(window, "TabText")).IsChecked = true;
        Pump(window);
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制文字", "回文字页时复制按钮回到文字");

        // 「显示原图」= 同一块画布换成原图，文字层收起
        var original = (CheckBox)Field(window, "OriginalToggle");
        var fill = (CheckBox)Field(window, "FillToggle");
        var ocrScroll = (ScrollViewer)Field(window, "OcrScroll");
        var originalScroll = (ScrollViewer)Field(window, "OriginalScroll");

        Check(ocrScroll.Visibility == Visibility.Visible, "文字页默认显示文字层");

        original.IsChecked = true;
        Pump(window);
        Check(originalScroll.Visibility == Visibility.Visible, "显示原图 → 原图可见");
        Check(ocrScroll.Visibility == Visibility.Collapsed, "显示原图 → 文字层收起");
        Check(!fill.IsEnabled, "显示原图时「填充背景」不可用");

        original.IsChecked = false;
        Pump(window);
        Check(ocrScroll.Visibility == Visibility.Visible && fill.IsEnabled, "关掉原图回到文字层");

        // 「填充背景」= 文字层铺不铺估出来的底色
        fill.IsChecked = false;
        Pump(window);
        Check(!(bool)Get(canvas, "FillBackground"), "关掉填充背景 → 画布不铺底色");
        fill.IsChecked = true;
        Pump(window);
        Check((bool)Get(canvas, "FillBackground"), "打开填充背景 → 画布铺底色");

        // 「原文预览」= 旁边开一个 100% 原图窗；默认设置（show_original_image_preview = 开）下
        // 识别完成时它已经自动开出来了（P9-e），所以这里先确认自动开、再验按钮能关 / 能开
        Window? Preview() => Application.Current.Windows.Cast<Window>()
            .FirstOrDefault(w => w.GetType().Name == "CaptureOriginalPreviewWindow" && w.IsVisible);

        var preview = Preview();
        Check(preview != null, "默认设置下识别完自动开出原图预览窗");
        if (preview != null) preview.Close();
        Pump(window);
        Check(Preview() == null, "关掉预览窗后确实不在了");

        ((Button)Field(window, "PreviewBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        preview = Preview();
        Check(preview != null, "原文预览按钮打开预览窗");
        if (preview != null)
        {
            Check(preview.Left + preview.Width <= window.Left + 1 ||
                  preview.Left >= window.Left + window.Width - 1,
                $"预览窗贴在识别窗旁边（识别 {window.Left:0}/{window.Width:0} · 预览 {preview.Left:0}/{preview.Width:0}）");
            preview.Close();
        }

        Save(Render(window), Path.Combine(_outDir, "result.png"));

        // —— 真鼠标：在文字层里从第一行拖到第二行 = 跨行选区（文字层不是只读文本框）——
        window.Activate();
        Pump(window);

        double zoom = (double)Get(canvas, "Zoom");
        Point Screen(double sx, double sy) =>
            ((FrameworkElement)canvas).PointToScreen(new Point(sx * zoom, sy * zoom));

        void Move(int x, int y, int settle = 120)
        {
            SetCursorPos(x, y);
            Thread.Sleep(settle);
            Pump(window);
        }

        Point start = Screen(20, 12);    // 第一行「名称 数量」
        Point end = Screen(20, 52);      // 第二行「苹果 12」

        Move((int)start.X, (int)start.Y, 220);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        Pump(window);
        Move((int)end.X, (int)end.Y, 180);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(180);
        Pump(window);

        Check((bool)Get(model, "HasSelection"), "文字层里拖一下就选中文字（真鼠标）");
        string selected = (string)Call(model, "SelectionText");
        Check(selected.Contains("苹果") || selected.Contains("数量"),
            "选中的是跨两行的那段文字：" + selected.Replace("\n", "/"));
        Check(((TextBlock)Field(window, "StatusText")).Text.Contains("已选中"),
            "状态行改报选中的字数：" + ((TextBlock)Field(window, "StatusText")).Text);

        // 双击一行 = 选中整行（上游「点一行进整行编辑」的入口）
        Call(canvas, "SelectLine", 1);
        Pump(window);
        Check(((string)Call(model, "SelectionText")).Contains("苹果 12"), "选整行取到整行文字");
        Check(((TextBlock)Field(window, "StatusText")).Text.Contains("共 2 行"),
            "状态行仍带着总行数");

        // ============ 在线翻译（本地假端点，端到端）============
        // 真跑一次「点翻译 → 请求发出去 → 译文摆回画布 → 复制译文 → 切回原文 → 401 不假装成功」。
        // 配置写进临时目录，别动本机真正在用的 configs/settings.json。
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "translate-config");
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        configPathSet.Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });

        var translationConfigType = asm.GetType("LTools.Features.CapturePro.TranslationConfig")!;
        var translationStoreType = asm.GetType("LTools.Features.CapturePro.TranslationStore")!;
        var normalizeMethod = translationConfigType
            .GetMethod("Normalize", BindingFlags.Static | BindingFlags.Public)!;
        var saveMethod = translationStoreType
            .GetMethod("Save", BindingFlags.Static | BindingFlags.Public)!;
        var storeConfigProp = translationStoreType
            .GetProperty("Config", BindingFlags.Static | BindingFlags.Public)!;

        void Configure(string endpoint)
        {
            object cfg = normalizeMethod.Invoke(null,
                new object[] { endpoint, "ltools-test", "", "zh-CN", "auto", 10000 })!;
            saveMethod.Invoke(null, new[] { cfg });
        }

        // P4-h 的三个显示项（版面处理 / 画进原图 / 复制成图片）走各自的静态 setter
        var setLayoutMethod = translationStoreType
            .GetMethod("SetLayout", BindingFlags.Static | BindingFlags.Public)!;
        var setOriginalImageMethod = translationStoreType
            .GetMethod("SetOriginalImage", BindingFlags.Static | BindingFlags.Public)!;
        var setCopyAsImageMethod = translationStoreType
            .GetMethod("SetCopyAsImage", BindingFlags.Static | BindingFlags.Public)!;
        var layoutProp = translationStoreType
            .GetProperty("Layout", BindingFlags.Static | BindingFlags.Public)!;
        var originalImageProp = translationStoreType
            .GetProperty("OriginalImage", BindingFlags.Static | BindingFlags.Public)!;
        var copyAsImageProp = translationStoreType
            .GetProperty("CopyAsImage", BindingFlags.Static | BindingFlags.Public)!;

        // 老口径（逐行翻、不画进原图、复制文本）先钉住，后面再逐项打开
        setLayoutMethod.Invoke(null, new object[] { "original" });
        setOriginalImageMethod.Invoke(null, new object[] { false });
        setCopyAsImageMethod.Invoke(null, new object[] { false });
        Check((string)layoutProp.GetValue(null)! == "original", "版面处理落盘 = 按原行");
        Check((bool)originalImageProp.GetValue(null)! == false, "译文画进原图默认关");
        Check((bool)copyAsImageProp.GetValue(null)! == false, "复制成图片默认关");

        string ConfigField(string name)
        {
            object cfg = storeConfigProp.GetValue(null)!;
            return (string)cfg.GetType().GetProperty(name)!.GetValue(cfg)!;
        }

        var translateBar = (FrameworkElement)Field(window, "TranslateBar");
        var translateBtn = (Button)Field(window, "TranslateBtn");
        var targetBox = (ComboBox)Field(window, "TargetBox");

        Check(translateBar.Visibility == Visibility.Visible, "文字页露出翻译条");
        Check(targetBox.SelectedItem != null,
            "目标语言有默认值：" + ((ComboBoxItem)targetBox.SelectedItem!).Content);
        Check(translateBtn.IsEnabled, "有文字时「翻译」可用");

        ((RadioButton)Field(window, "TabTable")).IsChecked = true;
        Pump(window);
        Check(translateBar.Visibility == Visibility.Collapsed, "非文字页把翻译条收起（不放死按钮）");
        ((RadioButton)Field(window, "TabText")).IsChecked = true;
        Pump(window);
        Check(translateBar.Visibility == Visibility.Visible, "回文字页翻译条又出来");

        using var openAi = new FakeOpenAiServer("[\"Name Count\",\"Apple 12\"]", 200, openAiShape: true);
        Configure(openAi.Url);
        Check(ConfigField("Endpoint").StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
            "翻译配置写进了 settings.json：" + ConfigField("Endpoint"));

        void WaitTranslation(Window w)
        {
            for (int i = 0; i < 400; i++)
            {
                Pump(w);
                if (!(bool)Prop("Translating")) return;
                Thread.Sleep(25);
            }
        }

        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(translateBtn.Content as string == "取消", "翻译期间按钮变「取消」（真能取消）");
        WaitTranslation(window);
        Pump(window);

        object translatedValue = windowType.GetProperty("TranslatedLines", Private)!.GetValue(window)!;
        var translatedLines = translatedValue as IEnumerable<string>;
        Check(translatedLines != null && translatedLines.Count() == 2,
            "翻译回来 2 行：" + (translatedLines == null ? "null" : string.Join(" / ", translatedLines)));
        Check(translatedLines != null && translatedLines.First() == "Name Count", "第一行是译文");
        Check(translatedLines != null && translatedLines.Last() == "Apple 12", "第二行是译文");
        Check((bool)Get(canvas, "ShowingTranslation"), "画布切到译文（overlay translation）");
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制译文", "复制按钮文案变「复制译文」");
        Check(((TextBlock)Field(window, "StatusText")).Text.Contains("译文"),
            "状态行报译文行数与目标语言：" + ((TextBlock)Field(window, "StatusText")).Text);

        Check(openAi.Requests >= 1, "真的发了一次请求");
        Check(openAi.LastRequest.Contains("苹果 12") && openAi.LastRequest.Contains("名称 数量"),
            "请求体带的是当前文字层的行（含用户改过的内容）");
        Check(openAi.LastRequest.Contains("ltools-test"), "请求体带上配置里的模型名");

        System.Windows.Clipboard.Clear();
        ((Button)Field(window, "CopyBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        string copiedTranslation = System.Windows.Clipboard.GetText();
        Check(copiedTranslation.Contains("Name Count") && copiedTranslation.Contains("Apple 12"),
            "复制译文拿到两行译文：" + copiedTranslation.Replace("\r\n", "/"));

        Save(Render(window), Path.Combine(_outDir, "result-translate.png"));

        // 译文模式只读（编辑键都不认），点「原文」切回
        Check(!(bool)windowType.GetProperty("TextEditable", Private)!.GetValue(window)!,
            "译文模式文字层只读");

        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(windowType.GetProperty("TranslatedLines", Private)!.GetValue(window) == null,
            "点「原文」切回原文");
        Check(!(bool)Get(canvas, "ShowingTranslation"), "画布不再显示译文");
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制文字", "复制按钮文案回到文字");
        Check(((TextBlock)Field(window, "StatusText")).Text.Contains("共 2 行"), "状态行回到原文统计");

        // —— 失败路径：401 必须报「密钥」而不是假装翻好了 ——
        using var denied = new FakeOpenAiServer("{\"error\":\"bad key\"}", 401);
        Configure(denied.Url);
        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WaitTranslation(window);
        Pump(window);

        string translateError = (string)windowType
            .GetProperty("TranslationError", Private)!.GetValue(window)!;
        Check(translateError.Contains("密钥"), "401 的失败原因指明是密钥：" + translateError);
        Check(windowType.GetProperty("TranslatedLines", Private)!.GetValue(window) == null,
            "翻译失败时不显示任何译文");
        Check(!(bool)Get(canvas, "ShowingTranslation"), "翻译失败时画布仍是原文");

        // 收掉失败时那条常驻 toast，别挡后面的段落
        foreach (Window toast in Application.Current.Windows.Cast<Window>()
                     .Where(w => w.GetType().Name == "ToastWindow").ToList())
        {
            toast.Close();
        }

        // ============ 版面处理 / 译文画进原图 / 复制译文图（P4-h）============
        // 两行紧挨着（y=0/40，行高 30）→ 智能合并成 1 段；译文直接铺在原图上；
        // 复制得到的是图片，不是文本。
        setLayoutMethod.Invoke(null, new object[] { "smart_merge" });
        setOriginalImageMethod.Invoke(null, new object[] { true });
        setCopyAsImageMethod.Invoke(null, new object[] { true });
        Check((string)layoutProp.GetValue(null)! == "smart_merge", "版面处理改回智能合并");
        Check((bool)originalImageProp.GetValue(null)!, "译文画进原图已打开");
        Check((bool)copyAsImageProp.GetValue(null)!, "复制成图片已打开");

        using var merged = new FakeOpenAiServer("[\"Name Count Apple 12\"]", 200, openAiShape: true);
        Configure(merged.Url);
        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WaitTranslation(window);
        Pump(window);

        var mergedTexts = windowType.GetProperty("TranslatedLines", Private)!.GetValue(window)
            as IEnumerable<string>;
        Check(mergedTexts != null && mergedTexts.Count() == 1,
            "智能合并后只翻 1 段：" + (mergedTexts == null ? "null" : string.Join(" / ", mergedTexts)));
        Check(merged.LastRequest.Contains("名称 数量苹果 12"),
            "请求体里是合并后的整段（不是两行）");
        Check(((System.Collections.IEnumerable)Get(canvas, "TranslationLayer")).Cast<object>()
            .Count() == 1, "译文落点也变 1 段");
        Check((bool)Get(canvas, "TranslationOnImage"), "译文画进原图（画布底图换原图）");
        Check((string)((Button)Field(window, "CopyBtn")).Content == "复制译文图",
            "复制按钮文案变「复制译文图」");

        var renderImage = windowType
            .GetMethod("RenderTranslationImage", Private)!.Invoke(window, null) as BitmapSource;
        Check(renderImage != null, "译文能渲染成图片");
        Check(renderImage != null && renderImage.PixelWidth == 320 && renderImage.PixelHeight == 80,
            $"译文图尺寸 = 源图（{renderImage?.PixelWidth}×{renderImage?.PixelHeight}）");
        Check(PixelAt(renderImage!, 5, 78) == Color.FromRgb(0xD0, 0x80, 0x40),
            "段框外的像素仍是原图");
        Check(PixelAt(renderImage!, 298, 68) == Colors.White,
            "段框内铺的是行底色（白），实际 " + PixelAt(renderImage!, 298, 68));

        System.Windows.Clipboard.Clear();
        ((Button)Field(window, "CopyBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(System.Windows.Clipboard.ContainsImage(), "复制译文时剪贴板里是图片");

        Save(Render(window), Path.Combine(_outDir, "result-translate-on-image.png"));

        // 关掉「复制成图片」→ 同一份译文复制成文本
        setCopyAsImageMethod.Invoke(null, new object[] { false });
        System.Windows.Clipboard.Clear();
        ((Button)Field(window, "CopyBtn")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(!System.Windows.Clipboard.ContainsImage(), "关掉之后不再写图片");
        Check(System.Windows.Clipboard.ContainsText() &&
              System.Windows.Clipboard.GetText().Contains("Name Count"),
            "关掉之后复制的是文本译文");

        // 版面处理改回「按原行」→ 又是 2 行，且译文仍画在原图上
        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // 先切回原文
        Pump(window);
        setLayoutMethod.Invoke(null, new object[] { "original" });
        using var rowByRow = new FakeOpenAiServer("[\"Name Count\",\"Apple 12\"]", 200,
            openAiShape: true);
        Configure(rowByRow.Url);
        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        WaitTranslation(window);
        Pump(window);
        var rowLines = windowType.GetProperty("TranslatedLines", Private)!.GetValue(window)
            as IEnumerable<string>;
        Check(rowLines != null && rowLines.Count() == 2,
            "改回按原行后翻 2 行：" + (rowLines == null ? "null" : string.Join(" / ", rowLines)));
        Check(((System.Collections.IEnumerable)Get(canvas, "TranslationLayer")).Cast<object>()
            .Count() == 2, "落点回到 2 行");
        Check((bool)Get(canvas, "TranslationOnImage"), "按原行也照样画进原图");

        translateBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));   // 收尾：切回原文
        Pump(window);
        Check(windowType.GetProperty("TranslatedLines", Private)!.GetValue(window) == null,
            "收尾切回原文，后面的排版用例接着用文字层");

        // ============ 排版 / 标点（基线式变换，照上游 screenshotocrtexttransform）============
        var transformBar = (FrameworkElement)Field(window, "TransformBar");
        var formattingBox = (ComboBox)Field(window, "FormattingBox");
        var punctuationBox = (ComboBox)Field(window, "PunctuationBox");
        var textResetBtn = (Button)Field(window, "TextResetBtn");

        Check(transformBar.Visibility == Visibility.Visible, "文字页露出排版 / 标点");
        Check(formattingBox.Items.Count == 4, "排版下拉 4 项（不排版 / 保留换行 / 去掉换行 / 智能排版）");
        Check(punctuationBox.Items.Count == 3, "标点下拉 3 项（不转换 / 半角 / 全角）");
        Check((string)((ComboBoxItem)formattingBox.SelectedItem).Tag == "none", "默认排版是「不排版」");
        Check((string)((ComboBoxItem)punctuationBox.SelectedItem).Tag == "none", "默认标点是「不转换」");
        Check(textResetBtn.IsEnabled, "有文字时「重置」可用");

        ((RadioButton)Field(window, "TabTable")).IsChecked = true;
        Pump(window);
        Check(transformBar.Visibility == Visibility.Collapsed, "非文字页把排版条收起");
        ((RadioButton)Field(window, "TabText")).IsChecked = true;
        Pump(window);
        Check(transformBar.Visibility == Visibility.Visible, "回文字页排版条又出来");

        // 先把文字改成带标点的两行（相当于用户在画布上改过字）
        Call(model, "ReplaceAll", new List<string> { "a,b", "c,d" });
        Pump(window);
        Check((int)Prop("TextLineCount") == 2, "改字后仍是两行");

        // 标点：全角（只换标点，行数与行框不动）
        punctuationBox.SelectedIndex = 2;
        Pump(window);
        var markedLines = (IReadOnlyList<string>)Get(model, "Lines");
        Check(markedLines.Count == 2 && markedLines[0] == "a，b" && markedLines[1] == "c，d",
            "选「全角标点」把逗号换成全角：" + string.Join(" / ", markedLines));
        Check((int)Prop("TextLineCount") == 2, "标点转换不改行数");

        // 排版：去掉换行（从基线重算，标点照旧生效）
        formattingBox.SelectedIndex = 2;
        Pump(window);
        var mergedLines = (IReadOnlyList<string>)Get(model, "Lines");
        Check((int)Prop("TextLineCount") == 1, "去掉换行后画布只剩一行");
        Check(mergedLines.Count == 1 && mergedLines[0] == "a，bc，d",
            "去换行 + 全角一起生效：" + string.Join(" / ", mergedLines));

        // 两个都回「无」= 恢复基线（不叠加，也不留空行）
        formattingBox.SelectedIndex = 0;
        Pump(window);
        punctuationBox.SelectedIndex = 0;
        Pump(window);
        var restored = (IReadOnlyList<string>)Get(model, "Lines");
        Check((int)Prop("TextLineCount") == 2, "两个都回「无」恢复成两行");
        Check(restored[0] == "a,b" && restored[1] == "c,d",
            "恢复基线文本：" + string.Join(" / ", restored));

        // 「重置」= 回到最初的 OCR 文本
        textResetBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        var originalLines = (IReadOnlyList<string>)Get(model, "Lines");
        Check(originalLines.Count == 2 && originalLines[0] == "名称 数量" && originalLines[1] == "苹果 12",
            "「重置」回到最初的识别文本：" + string.Join(" / ", originalLines));
        Check((string)((ComboBoxItem)formattingBox.SelectedItem).Tag == "none",
            "「重置」把排版下拉也回「无」");

        // 智能排版：两行行距紧（10 ≤ 中位行高 × 0.75 = 22）→ 接成一段
        formattingBox.SelectedIndex = 3;
        Pump(window);
        var smartLines = (IReadOnlyList<string>)Get(model, "Lines");
        Check(smartLines.Count == 1 && smartLines[0] == "名称 数量苹果 12",
            "智能排版把紧挨着的两行接成一段：" + string.Join(" / ", smartLines));
        Check((int)Prop("TextLineCount") == 1, "智能排版后画布是一行");

        Save(Render(window), Path.Combine(_outDir, "result-transform.png"));

        // 下拉改动会记成默认值（照上游 default_formatting / default_punctuation）
        var recognitionStoreType = asm.GetType("LTools.Features.CapturePro.TextRecognitionStore")!;
        var recognitionDefaults = recognitionStoreType
            .GetMethod("Defaults", BindingFlags.Static | BindingFlags.Public)!;
        object defaultsTuple = recognitionDefaults.Invoke(null, null)!;
        string savedFormatting =
            (string)defaultsTuple.GetType().GetField("Item1")!.GetValue(defaultsTuple)!;
        Check(savedFormatting == "smart", "换排版即写入默认值：" + savedFormatting);

        // 手动编辑会作废变换（上游 appendHistory → clearTransforms）
        Call(model, "ReplaceAll", new List<string> { "改过的字" });
        Pump(window);
        Check((string)((ComboBoxItem)formattingBox.SelectedItem).Tag == "none",
            "手动编辑后排版下拉回「无」（变换作废）");
        var editedLines = (IReadOnlyList<string>)Get(model, "Lines");
        Check(editedLines.Count == 1 && editedLines[0] == "改过的字", "手动编辑的文字保留");

        // 收尾：把默认值改回「无」，别影响后面的段落
        recognitionStoreType.GetMethod("SetFormatting", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, new object[] { "none" });
        recognitionStoreType.GetMethod("SetPunctuation", BindingFlags.Static | BindingFlags.Public)!
            .Invoke(null, new object[] { "none" });

        // ============ LaTeX 页（本地只给源码，不做渲染）============
        var latexPage = (FrameworkElement)Field(window, "LatexPage");
        var latexView = (TextBox)Field(window, "LatexView");
        var latexInfo = (TextBlock)Field(window, "LatexInfo");

        ((RadioButton)Field(window, "TabLatex")).IsChecked = true;
        Pump(window);
        Check((string)Prop("CurrentTab") == "latex", "切到 LaTeX 页");
        Check(latexPage.Visibility == Visibility.Visible, "LaTeX 页可见");
        Check(latexView.IsReadOnly, "公式源码是只读框（能划选、不能误改）");
        Check((string)copyButton.Content == "复制公式源码", "LaTeX 页的复制按钮文案");
        Check(statusText.Text.Contains("LaTeX"), "状态行写明 LaTeX：" + statusText.Text);
        Check(statusText.Text.Contains("不渲染"), "状态行说明本地不渲染公式：" + statusText.Text);
        Check(latexInfo.Text.Contains("整段识别文字"), "没有公式特征时退回整段识别文字：" + latexInfo.Text);
        Check(latexView.Text == "改过的字", "退回原文时就是识别文字：" + latexView.Text);

        // 有公式特征：只挑公式行，并脱掉整行的 $ 定界符
        Call(model, "ReplaceAll", new List<string> { "$E = mc^2$" });
        Pump(window);
        Call(window, "RefreshLatex");
        Pump(window);
        Check(latexView.Text == "E = mc^2", "只挑公式行并脱掉 $ 定界符：" + latexView.Text);
        Check(latexInfo.Text.Contains("上游"), "公式模式的页脚点出平台差异：" + latexInfo.Text);

        System.Windows.Clipboard.Clear();
        copyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(System.Windows.Clipboard.GetText() == "E = mc^2", "没选时复制整份公式源码");

        latexView.Focus();
        latexView.SelectAll();
        Pump(window);
        Check(statusText.Text.Contains("已选中"), "全选后状态行报选中的字数：" + statusText.Text);

        Save(Render(window), Path.Combine(_outDir, "result-latex.png"));

        configPathSet.Invoke(null, new object[] { oldConfigPath });

        window.Close();
    }

    /// <summary>
    /// 本地假 OpenAI 兼容端点：真 TCP + 手写 HTTP/1.1，避开 HttpListener 的 URL ACL
    /// （冒烟可能不是提权进程）。收到什么就回什么，请求体留给断言用。
    /// </summary>
    private sealed class FakeOpenAiServer : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly Thread _thread;
        private readonly string _response;
        private readonly int _status;
        private volatile string _lastRequest = "";

        public FakeOpenAiServer(string content, int status = 200, bool openAiShape = false)
        {
            _status = status;
            _response = openAiShape
                ? "{\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" +
                  System.Text.Json.JsonSerializer.Serialize(content) + "}}]}"
                : content;

            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        public string Url => "http://127.0.0.1:" +
            ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port + "/v1";

        public string LastRequest => _lastRequest;
        public int Requests { get; private set; }

        private void Serve()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    using (client)
                    using (System.Net.Sockets.NetworkStream stream = client.GetStream())
                    {
                        var head = new List<byte>();
                        while (head.Count < 64 * 1024)
                        {
                            int b = stream.ReadByte();
                            if (b < 0) break;
                            head.Add((byte)b);
                            int n = head.Count;
                            if (n >= 4 && head[n - 4] == 13 && head[n - 3] == 10 &&
                                head[n - 2] == 13 && head[n - 1] == 10)
                            {
                                break;
                            }
                        }

                        string header = System.Text.Encoding.ASCII.GetString(head.ToArray());
                        int length = 0;
                        foreach (string line in header.Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                                int.TryParse(line.Substring(15).Trim(), out int parsed))
                            {
                                length = parsed;
                            }
                        }

                        var body = new byte[Math.Max(0, length)];
                        int read = 0;
                        while (read < body.Length)
                        {
                            int chunk = stream.Read(body, read, body.Length - read);
                            if (chunk <= 0) break;
                            read += chunk;
                        }

                        _lastRequest = System.Text.Encoding.UTF8.GetString(body, 0, read);
                        Requests++;

                        byte[] payload = System.Text.Encoding.UTF8.GetBytes(_response);
                        string statusText = _status == 200 ? "200 OK" : _status + " Error";
                        byte[] headBytes = System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 " + statusText + "\r\n" +
                            "Content-Type: application/json; charset=utf-8\r\n" +
                            "Content-Length: " + payload.Length + "\r\n" +
                            "Connection: close\r\n\r\n");
                        stream.Write(headBytes, 0, headBytes.Length);
                        stream.Write(payload, 0, payload.Length);
                        stream.Flush();
                    }
                }
                catch (Exception)
                {
                    // 客户端提前断开（取消 / 超时）：忽略
                }
            }
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // 已停
            }

            _thread.Join(500);
        }
    }

    // ============ 贴图管理 ============

    /// <summary>
    /// 贴图管理页（照 snow-shot 的 <c>PinnedWindowManagementPageWidget</c>）：标题 + 计数、
    /// 来源多选、日期范围、选择栏（全选本页 / 取消选择 / 批量贴到屏幕 / 删除选中）、
    /// 卡片网格（缩略图 / 时间 / 来源 / 状态 / 分组 + 尺寸）、分页（10 / 20 / 50，落 ui.json）、
    /// 两档空态。贴回走 <c>CapturePinManager.Restore</c>（原记录复用，不新建）。
    /// </summary>
    private static void PinColorSection(BitmapSource image, double scale)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var clipboard = asm.GetType("LTools.Native.NativeClipboard")!;
        var pinClipboard = asm.GetType("LTools.Features.CapturePro.PinClipboard")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;

        var setText = clipboard.GetMethod("SetText")!;
        var setImage = clipboard.GetMethod("SetImage")!;
        var tryText = clipboard.GetMethod("TryGetText")!;
        var capture = clipboard.GetMethod("Capture")!;
        var restoreClipboard = clipboard.GetMethod("Restore")!;
        var pin = pinClipboard.GetMethod("Pin")!;
        var closeAll = manager.GetMethod("CloseAll")!;
        var clearStore = store.GetMethod("Clear")!;
        var restorePin = manager.GetMethod("Restore")!;
        var pinsField = manager.GetField("Pins", BindingFlags.NonPublic | BindingFlags.Static)!;

        var snapshot = capture.Invoke(null, null);

        string ReadClipboard()
        {
            var args = new object[] { null };
            tryText.Invoke(null, args);
            return args[0] as string ?? "";
        }

        Window OpenWindow()
        {
            var windows = Application.Current.Windows.OfType<Window>()
                .Where(w => w.GetType().Name == "CapturePinWindow" && w.IsVisible).ToList();
            return windows.Count == 1 ? windows[0] : windows.LastOrDefault();
        }

        object Prop(object target, string name) =>
            target.GetType().GetProperty(name, Private)?.GetValue(target)
            ?? target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

        void Clean()
        {
            closeAll.Invoke(null, null);
            clearStore.Invoke(null, null);
        }

        void RealClick(Window window, Point cardPoint)
        {
            var screen = window.PointToScreen(cardPoint);
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            Nudge(window);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Nudge(window);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Nudge(window);
        }

        try
        {
            // ---------- 颜色文本 → 色卡 ----------
            Clean();
            setText.Invoke(null, new object[] { "#7DD353" });
            pin.Invoke(null, null);
            var card = OpenWindow();
            Check(card != null, "贴剪贴板：颜色文本贴出一张卡片");
            if (card == null) return;
            Pump(card);

            Check(Prop(card, "CardKind")?.ToString() == "Color", "颜色文本贴成颜色取样板");
            Check(Math.Abs(card.Width - 342) < 1.5 && Math.Abs(card.Height - 210) < 1.5,
                $"色卡按上游度量 342×210（{card.Width:0.#}×{card.Height:0.#}）");

            var copies = Prop(card, "Copies") as System.Collections.IList;
            Check(copies is { Count: 3 }, $"色卡三枚复制按钮（实际 {(copies == null ? 0 : copies.Count)}）");

            Save(Render(card), Path.Combine(_outDir, "color-card.png"));

            if (copies is { Count: 3 })
            {
                var copyAt = card.GetType().GetMethod("CopyAt", Private)!;
                var rows = new[] { "#7DD353", "rgb(125, 211, 83)", "hsl(100.3, 59.3%, 57.6%)" };
                for (int i = 0; i < 3; i++)
                {
                    // 每行的复制图标：取「第二笔独有」的左上带（OddEven 重叠处不算命中）
                    var front = (Rect)copies[i]!.GetType().GetProperty("Front")!.GetValue(copies[i]!)!;
                    var at = new Point(front.X + 1, front.Y + 1);
                    setText.Invoke(null, new object[] { "（未复制）" });
                    var ok = (bool)copyAt.Invoke(card, new object[] { at })!;
                    Check(ok && ReadClipboard() == rows[i],
                        $"第 {i + 1} 行复制按钮给规范 CSS：{rows[i]}（实际 {ReadClipboard()}）");
                }

                var front3 = (Rect)copies[2]!.GetType().GetProperty("Front")!.GetValue(copies[2]!)!;
                RealClick(card, new Point(front3.X + 1, front3.Y + 1));
                Check(ReadClipboard() == "hsl(100.3, 59.3%, 57.6%)",
                    $"真鼠标点复制图标得到 HSL（实际 {ReadClipboard()}）");
            }

            // ---------- 历史里记成色卡、贴回仍有热区 ----------
            var list = (System.Collections.IList)store.GetMethod("Load")!.Invoke(null, null)!;
            Check(list.Count == 1, $"色卡进贴图历史（{list.Count} 条）");
            if (list.Count == 1)
            {
                var record = list[0]!;
                Check(Prop(record, "Kind")?.ToString() == "color", "历史记录种类 = color");
                Check((string)Prop(record, "Text") == "#7DD353", "历史里留着原文");
                closeAll.Invoke(null, null);
                var again = restorePin.Invoke(null, new object[] { record }) as Window;
                Check(again != null && again.IsVisible, "色卡从历史贴回屏幕");
                if (again != null)
                {
                    Pump(again);
                    var back = Prop(again, "Copies") as System.Collections.IList;
                    Check(back is { Count: 3 }, "贴回的色卡仍有三枚复制按钮");
                    Check(Math.Abs(again.Width - 342) < 1.5, $"贴回按原尺寸重画（{again.Width:0.#}）");
                    closeAll.Invoke(null, null);
                }
            }

            // ---------- 普通文字 → 文字卡 ----------
            Clean();
            setText.Invoke(null, new object[] { "第一行\n第二行 #7DD353" });
            pin.Invoke(null, null);
            var textCard = OpenWindow();
            Check(textCard != null, "贴剪贴板：文字贴出一张卡片");
            if (textCard != null)
            {
                Pump(textCard);
                Check(Prop(textCard, "CardKind")?.ToString() == "Text", "含颜色的整段文字仍按文字卡");
                Check((Prop(textCard, "Copies") as System.Collections.IList)?.Count == 0, "文字卡没有复制热区");
                Check(textCard.Width <= 1025 && textCard.Height > 0, $"文字卡不超过最大宽度（{textCard.Width:0.#}）");
                Save(Render(textCard), Path.Combine(_outDir, "pin-text-card.png"));
                Clean();
            }

            // ---------- 图片优先 ----------
            setImage.Invoke(null, new object[] { image });
            pin.Invoke(null, null);
            var imagePin = OpenWindow();
            Check(imagePin != null, "贴剪贴板：有图片时优先贴图");
            if (imagePin != null)
            {
                Pump(imagePin);
                Check(Prop(imagePin, "CardKind")?.ToString() == "Image", "图片按图片贴（不是文字卡）");
                Check(Math.Abs(imagePin.Width - image.PixelWidth / scale) < 2,
                    $"图片按 100% 贴出（{imagePin.Width:0.#} DIP）");
                Clean();
            }

            // ---------- 剪贴板空的：给提示、不抛 ----------
            clipboard.GetMethod("Clear")!.Invoke(null, null);
            pin.Invoke(null, null);
            Check(OpenWindow() == null, "剪贴板空时什么都不贴");
            Check(pinsField.GetValue(null) is System.Collections.IList { Count: 0 }, "剪贴板空时不进贴图登记处");
        }
        finally
        {
            Clean();
            if (snapshot != null) restoreClipboard.Invoke(null, new object[] { snapshot });
        }
    }

    /// <summary>
    /// 贴图文字样式（照上游 <c>pinTextStyleItems</c> / <c>pinnedtextstylecapture</c>）：
    /// 真对话框里的控件与预览、值写进 <c>settings.json</c>、新贴按新默认值、
    /// 已贴的贴图按记录里的快照重画、恢复默认、填错不保存。
    /// </summary>
    private static void PinTextStyleSection(double scale)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var clipboard = asm.GetType("LTools.Native.NativeClipboard")!;
        var pinClipboard = asm.GetType("LTools.Features.CapturePro.PinClipboard")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var textStore = asm.GetType("LTools.Features.CapturePro.PinTextStore")!;
        var windowType = asm.GetType("LTools.Views.CapturePro.PinTextStyleWindow")!;
        var limitsType = asm.GetType("LTools.Features.CapturePro.PinTextLimits")!;

        var setText = clipboard.GetMethod("SetText")!;
        var capture = clipboard.GetMethod("Capture")!;
        var restoreClipboard = clipboard.GetMethod("Restore")!;
        var pin = pinClipboard.GetMethod("Pin")!;
        var closeAll = manager.GetMethod("CloseAll")!;
        var clearStore = store.GetMethod("Clear")!;
        var loadStore = store.GetMethod("Load")!;
        var restorePin = manager.GetMethod("Restore")!;
        var limitsOf = textStore.GetMethod("Limits")!;
        var saveLimits = textStore.GetMethod("Save")!;
        var resetLimits = textStore.GetMethod("Reset")!;

        object Limits(int width, int size, string font, Color? text, Color? background) =>
            Activator.CreateInstance(limitsType, width, (double)size, font, text, background)!;

        object Prop(object target, string name) =>
            target.GetType().GetProperty(name, Private)?.GetValue(target)
            ?? target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

        object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private)!.Invoke(target, args)!;

        object LimitsNow() => limitsOf.Invoke(null, null)!;

        object Saved() => LimitsNow();

        Window OpenWindow()
        {
            var windows = Application.Current.Windows.OfType<Window>()
                .Where(w => w.GetType().Name == "CapturePinWindow" && w.IsVisible).ToList();
            return windows.Count == 0 ? null : windows[windows.Count - 1];
        }

        void Clean()
        {
            closeAll.Invoke(null, null);
            clearStore.Invoke(null, null);
        }

        var snapshot = capture.Invoke(null, null);

        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "pin-text-config");
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));

        try
        {
            configPathSet.Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });
            Clean();
            resetLimits.Invoke(null, null);

            var ctor = windowType.GetConstructor(Private, null, new[] { limitsType }, null)!;
            var dialog = (Window)ctor.Invoke(new[] { LimitsNow() })!;
            dialog.Show();
            Pump(dialog);

            var widthBox = (TextBox)Field(dialog, "WidthBox");
            var sizeBox = (TextBox)Field(dialog, "SizeBox");
            var fontBox = (ComboBox)Field(dialog, "FontBox");
            var textTheme = (CheckBox)Field(dialog, "TextThemeBox");
            var backgroundTheme = (CheckBox)Field(dialog, "BackgroundThemeBox");
            var textHex = (TextBox)Field(dialog, "TextHexBox");
            var preview = (Image)Field(dialog, "PreviewImage");

            // ---------- 打开时就是上游默认值 ----------
            Check(widthBox.Text == "1024", $"最大宽初值 1024（实际 {widthBox.Text}）");
            Check(sizeBox.Text == "16", $"字号初值 16（实际 {sizeBox.Text}）");
            Check(fontBox.SelectedIndex == 0, "字体初值是「系统默认」");
            Check(fontBox.Items.Count > 1, $"字体下拉列出系统字体（{fontBox.Items.Count} 项）");
            Check(textTheme.IsChecked == true, "字色默认跟随主题");
            Check(backgroundTheme.IsChecked == true, "底色默认跟随主题");
            Check(preview.Source is BitmapSource { PixelWidth: > 0 }, "预览真的渲染出位图");
            Save(Render(dialog), Path.Combine(_outDir, "pin-text-style.png"));

            // ---------- 改成 24px 红字并保存 ----------
            sizeBox.Text = "24";
            textHex.Text = "#EF4444";
            Pump(dialog);
            Check(textTheme.IsChecked == false, "填自定义 HEX 后不再跟随主题");
            Check((bool)Call(dialog, "TrySave"), "字号 24 + 红字保存成功");

            var saved = Saved();
            Check((int)Prop(saved, "MaximumWidth") == 1024, "保存后最大宽还是 1024");
            Check((double)Prop(saved, "FontSize") == 24.0, "settings.json 记下字号 24");
            Check(Prop(saved, "TextColor")?.ToString()?.EndsWith("EF4444") == true,
                $"settings.json 记下字色（实际 {Prop(saved, "TextColor")}）");

            // ---------- 新贴的文字按新默认值 ----------
            setText.Invoke(null, new object[] { "文字样式字号测试" });
            pin.Invoke(null, null);
            var big = OpenWindow();
            Check(big != null, "改完默认值后文字能贴上屏幕");
            if (big == null) return;
            Pump(big);
            double bigWidth = big.Width;
            Save(Render(big), Path.Combine(_outDir, "pin-text-24.png"));

            var records = (System.Collections.IList)loadStore.Invoke(null, null)!;
            Check(records.Count == 1, $"文字贴图进历史（{records.Count} 条）");
            object record = records[0]!;
            Check(Prop(record, "Kind")?.ToString() == "text", "文字贴图 kind = text");
            object style = Prop(record, "TextStyle");
            Check(style != null, "文字贴图记录带样式快照");
            Check(style != null && (double)Prop(style, "FontSize") == 24.0,
                "快照记下当时的字号 24");
            Check(style != null && Prop(style, "TextColor")?.ToString() == "#EF4444",
                "快照记下当时的字色");

            // ---------- 之后再改默认值，贴回来的老贴图仍按它自己的快照 ----------
            saveLimits.Invoke(null, new[] { Limits(1024, 32, "", null, null) });
            closeAll.Invoke(null, null);
            var again = restorePin.Invoke(null, new object[] { record }) as Window;
            Check(again != null, "老文字贴图能从历史贴回");
            if (again != null)
            {
                Pump(again);
                Check(Math.Abs(again.Width - bigWidth) < 1.5,
                    $"贴回按快照重画、宽度不变（{again.Width:0.#} vs {bigWidth:0.#}）");
                closeAll.Invoke(null, null);

                setText.Invoke(null, new object[] { "文字样式字号测试" });
                pin.Invoke(null, null);
                var newer = OpenWindow();
                Check(newer != null && newer.Width > bigWidth + 4,
                    $"新默认字号 32 的卡片更宽（{(newer == null ? 0 : newer.Width):0.#} vs {bigWidth:0.#}）");
                closeAll.Invoke(null, null);
            }

            // ---------- 恢复默认 ----------
            Call(dialog, "ResetToDefaults");
            Pump(dialog);
            Check(widthBox.Text == "1024" && sizeBox.Text == "16", "恢复默认：最大宽 / 字号回 1024 / 16");
            Check(fontBox.SelectedIndex == 0, "恢复默认：字体回「系统默认」");
            Check(textTheme.IsChecked == true && backgroundTheme.IsChecked == true,
                "恢复默认：两种颜色都跟随主题");
            Check((bool)Call(dialog, "TrySave"), "恢复默认能保存");
            var back = Saved();
            Check((double)Prop(back, "FontSize") == 16.0, "settings.json 回到字号 16");
            Check(Prop(back, "TextColor") == null, "settings.json 字色回到 system（null）");

            // ---------- 填错不保存、不关窗 ----------
            sizeBox.Text = "abc";
            Check(!(bool)Call(dialog, "TrySave"), "字号填错时保存被拦下");
            Check((double)Prop(Saved(), "FontSize") == 16.0, "填错时磁盘上的值不变");
            sizeBox.Text = "16";

            dialog.Close();
        }
        finally
        {
            Clean();
            resetLimits.Invoke(null, null);
            configPathSet.Invoke(null, new object[] { oldConfigPath });
            if (snapshot != null) restoreClipboard.Invoke(null, new object[] { snapshot });
        }
    }

    /// <summary>
    /// 贴图点击穿透 + 抖动隐藏同组其他贴图（P5-e）：真窗口、真 ex-style、真几何、真落盘与贴回。
    /// </summary>
    private static void PinClickThroughSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var ct = asm.GetType("LTools.Features.CapturePro.PinClickThrough")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var settings = asm.GetType("LTools.Features.CapturePro.PinToScreenStore")!;
        var sizing = asm.GetType("LTools.Views.WindowSizing")!;

        var pin = manager.GetMethod("Pin")!.Invoke(null,
            new object[] { image, new Int32Rect(300, 200, image.PixelWidth, image.PixelHeight), "screenshot", null }) as Window;
        Check(pin != null && pin.IsVisible, "点穿：先贴一张图");
        if (pin == null) return;

        var type = pin.GetType();
        string id = (string)pin.GetType()
            .GetProperty("PinId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pin)!;

        object Call(string name, params object[] args) =>
            type.GetMethod(name, Private)!.Invoke(pin, args);

        try
        {
            // ============ 默认：不穿透、不透明度 100% ============
            Check(!(bool)type.GetProperty("ClickThrough")!.GetValue(pin)!, "点穿：默认不开");
            Check((int)type.GetProperty("OpacityPercent")!.GetValue(pin)! == 100, "点穿：用户不透明度默认 100");

            // 右键菜单里有这一项（实时贴图才置灰）
            var menu = (ContextMenu)Call("BuildMenu")!;
            bool hasItem = false;
            foreach (var item in menu.Items)
                if (item is MenuItem mi && (mi.Header as string) == "点击穿透") hasItem = true;
            Check(hasItem, "点穿：右键菜单里有「点击穿透」");

            // ============ 开穿透 ============
            type.GetMethod("SetClickThrough")!.Invoke(pin, new object[] { true });
            Pump(pin);

            Check((bool)type.GetProperty("ClickThrough")!.GetValue(pin)!, "点穿：打开了");
            Check(Math.Abs(pin.Opacity - 0.5) < 0.01, $"点穿：默认不透明度 50%（实际 {pin.Opacity:0.##}）");

            var hwnd = (IntPtr)Field(pin, "_hwnd");
            long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            Check((ex & 0x20) != 0, "点穿：窗口带上 WS_EX_TRANSPARENT（鼠标真的穿过去）");

            var bar = Field(pin, "_ctBar") as Window;
            Check(bar != null && bar.IsVisible, "点穿：控件栏浮出来");
            Check(bar != null && Math.Abs(bar.Width - 232) < 0.5 && Math.Abs(bar.Height - 32) < 0.5,
                $"点穿：控件栏 232×32（{bar?.Width:0.#}×{bar?.Height:0.#}）");

            if (bar != null)
            {
                // 三件套：胶囊 0 / 移动 160 / 退出 200，都是 32 高
                var root = bar.Content as System.Windows.Controls.Canvas;
                Check(root != null && root.Children.Count == 3, "点穿：控制栏里只有三件套");
                if (root != null)
                {
                    var capsule = root.Children[0] as FrameworkElement;
                    var move = root.Children[1] as FrameworkElement;
                    var exit = root.Children[2] as FrameworkElement;
                    Check(capsule != null && Math.Abs(System.Windows.Controls.Canvas.GetLeft(capsule) - 0) < 0.01 &&
                          Math.Abs(capsule.Width - 152) < 0.01, "点穿：不透明度胶囊 152 宽在最左");
                    Check(move != null && Math.Abs(System.Windows.Controls.Canvas.GetLeft(move) - 160) < 0.01 &&
                          Math.Abs(move.Width - 32) < 0.01, "点穿：移动按钮在 160（胶囊 + 8）");
                    Check(exit != null && Math.Abs(System.Windows.Controls.Canvas.GetLeft(exit) - 200) < 0.01 &&
                          Math.Abs(exit.Width - 32) < 0.01, "点穿：退出按钮在 200（移动按钮 + 8）");
                }

                // 几何与纯函数一致（工作区 / 内容矩形都是真的）
                var work = (Rect)sizing.GetMethod("WorkAreaOf", Stat)!.Invoke(null, new object[] { pin })!;
                var content = new Rect(pin.Left, pin.Top, pin.Width, pin.Height);
                object layout = ct.GetMethod("Layout")!.Invoke(null, new object[] { content, work })!;
                var origin = (Point)layout.GetType().GetProperty("Origin")!.GetValue(layout)!;

                Check(Math.Abs(bar.Left - origin.X) < 0.8 && Math.Abs(bar.Top - origin.Y) < 0.8,
                    $"点穿：控件栏摆在 Layout 算出的位置（{bar.Left:0.#},{bar.Top:0.#} vs {origin.X:0.#},{origin.Y:0.#}）");
                Check(bar.Left >= work.Left - 0.8 && bar.Left + bar.Width <= work.Right + 0.8 &&
                      bar.Top >= work.Top - 0.8 && bar.Top + bar.Height <= work.Bottom + 0.8,
                    "点穿：控件栏整个在工作区里");
                Check((bool)bar.Topmost, "点穿：控件栏跟着贴图置顶");

                Save(Render(bar), Path.Combine(_outDir, "pin-click-through-bar.png"));

                // 图标要真的画出来（透明底 + 白色图标，只验渲染结果最直接）
                var shot = Render(bar);
                int capsuleInk = 0, moveInk = 0, exitInk = 0;
                for (int y = 0; y < 32; y++)
                    for (int x = 0; x < 232; x++)
                    {
                        var c = PixelAt(shot, x, y);
                        if (c.A < 200 || c.R < 200 || c.G < 200 || c.B < 200) continue;
                        if (x >= 200) exitInk++;
                        else if (x >= 160) moveInk++;
                        else if (x < 50) capsuleInk++;
                    }

                Check(capsuleInk > 20 && moveInk > 20 && exitInk > 20,
                    $"点穿：三件套的白色图标都画出来了（{capsuleInk} / {moveInk} / {exitInk} 像素）");
            }

            // ============ 滑条改穿透不透明度 ============
            type.GetMethod("SetClickThroughOpacity")!.Invoke(pin, new object[] { 30 });
            Check((int)type.GetProperty("ClickThroughOpacity")!.GetValue(pin)! == 30, "点穿：穿透不透明度记下 30");
            Check(Math.Abs(pin.Opacity - 0.3) < 0.01, $"点穿：滑条改完窗口跟着变（{pin.Opacity:0.##}）");
            if (bar != null)
                Check((int)bar.GetType().GetProperty("Percent")!.GetValue(bar)! == 30, "点穿：胶囊滑条同步到 30");

            // ============ 移动按钮拖动贴图 ============
            pin.Left = 300;
            pin.Top = 200;
            Call("OnClickThroughMoveStart", new Point(500, 500));
            Call("OnClickThroughMove", new Point(540, 520));
            Check(Math.Abs(pin.Left - 340) < 1 && Math.Abs(pin.Top - 220) < 1,
                $"点穿：移动按钮把贴图带走（{pin.Left:0.#},{pin.Top:0.#}）");
            Call("OnClickThroughMoveEnd");

            if (bar != null)
            {
                var work = (Rect)sizing.GetMethod("WorkAreaOf", Stat)!.Invoke(null, new object[] { pin })!;
                object layout = ct.GetMethod("Layout")!.Invoke(null,
                    new object[] { new Rect(pin.Left, pin.Top, pin.Width, pin.Height), work })!;
                var origin = (Point)layout.GetType().GetProperty("Origin")!.GetValue(layout)!;
                Check(Math.Abs(bar.Left - origin.X) < 0.8 && Math.Abs(bar.Top - origin.Y) < 0.8,
                    "点穿：贴图一动控件栏跟着走");
            }
            Pump(pin);
            Save(Render(pin), Path.Combine(_outDir, "pin-click-through.png"));

            // ============ 锁定态：移动按钮不给走 ============
            type.GetMethod("SetLocked")!.Invoke(pin, new object[] { true });
            pin.Left = 300;
            pin.Top = 200;
            Call("OnClickThroughMoveStart", new Point(500, 500));
            Call("OnClickThroughMove", new Point(560, 560));
            Check(Math.Abs(pin.Left - 300) < 0.5 && Math.Abs(pin.Top - 200) < 0.5, "点穿：锁定时移动按钮不动窗口");
            Call("OnClickThroughMoveEnd");
            type.GetMethod("SetLocked")!.Invoke(pin, new object[] { false });

            // ============ 穿透与缩略图互斥 ============
            type.GetMethod("SetThumbnail")!.Invoke(pin, new object[] { true });
            Check(!(bool)type.GetProperty("ClickThrough")!.GetValue(pin)!, "点穿：开缩略图会先退出穿透");
            Check((bool)type.GetProperty("Thumbnail")!.GetValue(pin)!, "点穿：缩略图接管");
            type.GetMethod("SetThumbnail")!.Invoke(pin, new object[] { false });

            // ============ 再进一次，然后退出 ============
            type.GetMethod("SetClickThrough")!.Invoke(pin, new object[] { true });
            Pump(pin);
            Check((bool)type.GetProperty("ClickThrough")!.GetValue(pin)!, "点穿：再打开");
            type.GetMethod("SetClickThrough")!.Invoke(pin, new object[] { false });
            Pump(pin);
            Check(!(bool)type.GetProperty("ClickThrough")!.GetValue(pin)!, "点穿：退出");
            Check(Math.Abs(pin.Opacity - 1.0) < 0.01, $"点穿：退出回到用户不透明度（{pin.Opacity:0.##}）");
            ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            Check((ex & 0x20) == 0, "点穿：退出后去掉 WS_EX_TRANSPARENT");
            Check(Field(pin, "_ctBar") == null, "点穿：退出后控件栏关掉");

            // ============ 状态落进贴图历史 ============
            var records = (System.Collections.IList)store.GetMethod("Load")!.Invoke(null, null)!;
            object record = null;
            foreach (var item in records)
                if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) record = item;
            Check(record != null, "点穿：历史里能找到这张");
            if (record != null)
            {
                var rt = record.GetType();
                Check(!(bool)rt.GetProperty("ClickThrough")!.GetValue(record)!, "点穿：退出后历史记的是不穿透");
                Check((int)rt.GetProperty("ClickThroughOpacity")!.GetValue(record)! == 30,
                    "点穿：穿透不透明度也落进了历史");
            }

            // ============ 抖动：真按鼠标轨迹喂给窗口 ============
            var other = manager.GetMethod("Pin")!.Invoke(null,
                new object[] { image, new Int32Rect(900, 220, image.PixelWidth, image.PixelHeight), "screenshot", null }) as Window;
            Check(other != null && other.IsVisible, "抖动：第二张同组贴图");
            if (other != null)
            {
                Check((bool)settings.GetProperty("ShakeWindowToHideOthers")!.GetValue(null)!, "抖动：开关默认开");

                // 六腿、每腿 64 DIP、几毫秒内走完 → 触发
                void Shake()
                {
                    Call("BeginShake", new Point(100, 100));
                    for (int leg = 1; leg <= 6; leg++)
                        Call("SampleShake", leg % 2 == 1 ? new Point(164, 100) : new Point(100, 100));
                }

                other.Hide();
                Shake();
                Check(other.IsVisible, "抖动：其他贴图都藏着 → 甩一下就全放出来");

                Shake();
                Check(!other.IsVisible, "抖动：再甩一次 → 全藏起来");

                // 开关关掉就不武装
                settings.GetMethod("SetShakeWindowToHideOthers")!.Invoke(null, new object[] { false });
                Call("BeginShake", new Point(100, 100));
                Check(!(bool)Field(pin, "_shakeActive"), "抖动：开关关掉后拖动不武装识别器");
                Call("EndShake", true);
                settings.GetMethod("SetShakeWindowToHideOthers")!.Invoke(null, new object[] { true });

                Call("BeginShake", new Point(100, 100));
                Check((bool)Field(pin, "_shakeActive"), "抖动：开关打开后拖动会武装识别器");
                Call("EndShake", true);
                Check(!(bool)Field(pin, "_shakeActive"), "抖动：松手收掉识别器");

                // 小幅来回（拖拽纠偏）不触发
                other.Hide();
                Call("BeginShake", new Point(100, 100));
                for (int leg = 1; leg <= 12; leg++)
                    Call("SampleShake", leg % 2 == 1 ? new Point(128, 100) : new Point(100, 100));
                Check(!other.IsVisible, "抖动：小幅来回不切换");
                Call("EndShake", true);

                other.Close();
                Pump(pin);
            }

            // ============ 记录说穿透 → 贴回还是穿透 ============
            store.GetMethod("UpdateClickThrough")!.Invoke(null, new object[] { id, true, 40 });
            pin.Close();
            Pump(pin);

            var after = (System.Collections.IList)store.GetMethod("Load")!.Invoke(null, null)!;
            object closed = null;
            foreach (var item in after)
                if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) closed = item;

            Check(closed != null, "点穿：关掉后记录还在");
            if (closed != null)
            {
                var restored = manager.GetMethod("Restore")!.Invoke(null, new object[] { closed }) as Window;
                Check(restored != null, "点穿：按记录贴回屏幕");
                if (restored != null)
                {
                    Check((bool)restored.GetType().GetProperty("ClickThrough")!.GetValue(restored)!,
                        "点穿：贴回还是穿透态");
                    Check(Math.Abs(restored.Opacity - 0.4) < 0.01,
                        $"点穿：贴回用记录里的穿透不透明度（{restored.Opacity:0.##}）");
                    restored.Close();
                    Pump(restored);
                }
            }
        }
        finally
        {
            if (pin.IsVisible) pin.Close();
            store.GetMethod("Remove")!.Invoke(null, new object[] { id });
        }
    }

    private static void PinDropSection(BitmapSource image, double scale)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var asm = typeof(CaptureOverlayWindow).Assembly;

        // Ui.Post 要有个 dispatcher 才投递（真程序在 App.OnStartup 里初始化）。
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { Dispatcher.CurrentDispatcher });

        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var filesType = asm.GetType("LTools.Features.CapturePro.PinFiles")!;
        var dragType = asm.GetType("LTools.Views.CapturePro.PinDragExport")!;
        var previewType = asm.GetType("LTools.Views.CapturePro.PinDragPreview")!;
        var retentionType = asm.GetType("LTools.Features.CapturePro.PinDragRetention")!;

        // P9-d1 起边框色不再是写死的白，而是设置里的 pin_to_screen/border_color
        var pinSettingsType = asm.GetType("LTools.Features.CapturePro.PinSettings")!;
        var normalBorder = (Color)pinSettingsType.GetProperty("BorderColor", Stat)!.GetValue(null)!;

        var pin = manager.GetMethod("Pin")!.Invoke(null, new object[]
        {
            image, new Int32Rect(420, 260, image.PixelWidth, image.PixelHeight), "screenshot", null,
        }) as Window;
        Check(pin != null && pin.IsVisible, "拖放：先贴一张图");
        if (pin == null) return;

        var type = pin.GetType();
        string id = (string)type.GetProperty("PinId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pin)!;

        object? Call(string name, params object[] args) => type.GetMethod(name, Private)!.Invoke(pin, args);

        string CardKind() => type
            .GetProperty("CardKind", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pin)!.ToString()!;

        string CardText() => (string)type
            .GetProperty("CardText", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(pin)!;

        BitmapSource PinImage() => (BitmapSource)type
            .GetProperty("Image", BindingFlags.Instance | BindingFlags.Public)!
            .GetValue(pin)!;

        object? Record()
        {
            var all = (System.Collections.IEnumerable)store.GetMethod("Load", Stat)!.Invoke(null, null)!;
            foreach (var item in all)
                if ((string)item.GetType().GetProperty("Id")!.GetValue(item)! == id) return item;
            return null;
        }

        static BitmapSource Solid(int w, int h, byte b, byte g, byte r)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                px[i * 4] = b;
                px[i * 4 + 1] = g;
                px[i * 4 + 2] = r;
                px[i * 4 + 3] = 255;
            }

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            bmp.Freeze();
            return bmp;
        }

        string dropped = Path.Combine(Path.GetTempPath(), "ltools-smoke-drop.txt");
        File.WriteAllText(dropped, "拖进贴图的文字");

        try
        {
            // ============ 拖放与菜单 ============
            Check((bool)type.GetProperty("AllowDrop")!.GetValue(pin)!, "拖放：贴图接受拖放");
            Check(!(bool)type.GetProperty("FileDragActive", Private)!.GetValue(pin)!, "拖放：默认没有高亮");

            var frame = (Border)Field(pin, "Frame")!;
            Call("SetFileDragActive", true);
            Check(Math.Abs(frame.BorderThickness.Left - 2) < 0.01, "拖放：拖入时边框 2px");
            Check(((SolidColorBrush)frame.BorderBrush).Color == Color.FromRgb(0x69, 0xB1, 0xFF),
                "拖放：拖入时边框是上游的 #69B1FF");
            Save(Render(pin), Path.Combine(_outDir, "pin-drop-active.png"));

            Call("SetFileDragActive", false);
            Check(Math.Abs(frame.BorderThickness.Left - 1) < 0.01, "拖放：离开后回到 1px");
            Check(((SolidColorBrush)frame.BorderBrush).Color == normalBorder,
                $"拖放：离开后回到设置里的普通边框色（{normalBorder}）");

            // 判定：支持的文件收、不支持的拒
            var probe = new DataObject();
            probe.SetData(DataFormats.FileDrop, new[] { dropped });
            var args = new object[] { probe, null };
            Check(Call("VerdictOf", args!)!.ToString() == "Accept", "拖放：支持的文本文件判为可收");

            var bad = new DataObject();
            bad.SetData(DataFormats.FileDrop, new[] { Path.Combine(Path.GetTempPath(), "ltools-smoke.exe") });
            Check(Call("VerdictOf", new object[] { bad, null }!)!.ToString() == "Empty",
                "拖放：不支持的扩展名判为不收");

            // 右键菜单：「加载新内容」两个入口 + 批量「复制所选为文件」
            var menu = (ContextMenu)Call("BuildMenu")!;

            static MenuItem? Find(ItemsControl root, string header)
            {
                foreach (var item in root.Items)
                    if (item is MenuItem mi && (mi.Header as string) == header) return mi;
                return null;
            }

            var load = Find(menu, "加载新内容");
            Check(load != null, "菜单里有「加载新内容」");
            Check(load != null && Find(load, "图片 / 文本文件…") != null, "加载菜单里有「图片 / 文本文件…」");
            Check(load != null && Find(load, "剪贴板") != null, "加载菜单里有「剪贴板」");

            var batch = Find(menu, "批量") ?? Find(menu, "批量（已选 1 张）");
            Check(batch != null, "菜单里有「批量」");
            Check(batch != null && Find(batch, "复制所选为文件") != null, "批量菜单里有「复制所选为文件」");

            // ============ 替换成更大 / 同尺寸的图 ============
            double beforeW = pin.Width, beforeH = pin.Height;
            var larger = Solid(image.PixelWidth + 120, image.PixelHeight + 80, 0x30, 0x60, 0xF0);

            Check((bool)Call("ReplaceWithImage", larger)!, "替换：更大的图换成功");
            Check(PinImage().PixelWidth == larger.PixelWidth, "替换：内容换成了新图");
            Check(pin.Width > beforeW + 10 && pin.Height > beforeH + 6,
                $"替换：窗口按中心长大（{beforeW:0.#}×{beforeH:0.#} → {pin.Width:0.#}×{pin.Height:0.#}）");
            Check(CardKind() == "Image", "替换：种类还是图片");

            var record = Record();
            Check(record != null, "替换：历史记录还在");
            if (record != null)
            {
                Check((string)record.GetType().GetProperty("Kind")!.GetValue(record)! == "image",
                    "替换：历史记成图片");
                Check((int)record.GetType().GetProperty("Width")!.GetValue(record)! == larger.PixelWidth,
                    "替换：历史里的图像宽度跟着换");
            }

            // ============ 替换成文本卡片（走真文件 + 后台解码） ============
            Call("LoadFile", dropped);
            for (int i = 0; i < 60 && CardKind() != "Text"; i++)
            {
                System.Threading.Thread.Sleep(50);
                Pump(pin);
            }

            Check(CardKind() == "Text", "替换：文本文件换成文字卡");
            Check(CardText() == "拖进贴图的文字",
                "替换：文字卡带着原文");
            record = Record();
            Check(record != null && (string)record.GetType().GetProperty("Kind")!.GetValue(record)! == "text",
                "替换：历史记成文字卡");
            Check(record != null && (string)record.GetType().GetProperty("Text")!.GetValue(record)! == "拖进贴图的文字",
                "替换：历史里存原文");

            // ============ 锁定：不同尺寸拒、同尺寸放行 ============
            type.GetMethod("SetLocked")!.Invoke(pin, new object[] { true });
            var lockedImage = PinImage();

            Check(!(bool)Call("ReplaceWithImage", Solid(64, 64, 1, 2, 3))!, "替换：锁着 + 尺寸不同被拒");
            Check(ReferenceEquals(PinImage(), lockedImage), "替换：被拒时内容一个像素都没动");

            var sameSize = Solid(PinImage().PixelWidth, PinImage().PixelHeight, 9, 9, 9);
            Check((bool)Call("ReplaceWithImage", sameSize)!, "替换：锁着但同尺寸能换");
            Check(PinImage().PixelWidth == sameSize.PixelWidth, "替换：同尺寸换上去了");

            type.GetMethod("SetLocked")!.Invoke(pin, new object[] { false });

            // ============ 拖出：准备的数据与临时文件 ============
            var shared = retentionType.GetProperty("Shared", Stat)!.GetValue(null)!;
            int Retained() => (int)shared.GetType().GetProperty("Count")!.GetValue(shared)!;

            int before = Retained();
            var payload = dragType.GetMethod("Prepare", Stat)!.Invoke(null, new object[] { image, "贴图-冒烟" });
            Check(payload != null, "拖出：准备成功");
            Check(Retained() == before + 1, "拖出：预约了一份临时文件额度");

            if (payload != null)
            {
                var payloadType = payload.GetType();
                var data = (DataObject)payloadType.GetProperty("Data")!.GetValue(payload)!;
                string file = (string)payloadType.GetProperty("File")!.GetValue(payload)!;

                Check(data.GetDataPresent(DataFormats.FileDrop), "拖出：带文件 URL");
                var paths = (string[])data.GetData(DataFormats.FileDrop)!;
                Check(paths.Length == 1 && string.Equals(paths[0], file, StringComparison.OrdinalIgnoreCase),
                    "拖出：文件列表就是那个临时 png");
                Check(file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(file),
                    "拖出：临时 png 真的落盘");
                Check(new FileInfo(file).Length > 0, "拖出：临时 png 不是空文件");
                Check(data.GetDataPresent(DataFormats.Bitmap), "拖出：同时带位图（聊天框只认这个）");

                string dir = Path.GetDirectoryName(file)!;
                payloadType.GetMethod("Complete")!.Invoke(payload, new object[] { false });
                Check(!File.Exists(file) && !Directory.Exists(dir), "拖出：取消后临时目录立刻删掉");
                Check(Retained() == before, "拖出：取消不占额度");
            }

            // ============ 拖出预览（160px、不挡命中） ============
            var preview = Activator.CreateInstance(previewType)!;
            previewType.GetMethod("Show")!.Invoke(preview, new object[] { image });
            Pump(pin);

            var previewWindow = (Window)previewType.GetProperty("Window", Private)!.GetValue(preview)!;
            Check(previewWindow != null && previewWindow.IsVisible, "拖出：预览窗口显示出来");
            if (previewWindow != null)
            {
                Check(Math.Abs(previewWindow.Width - 160) < 0.5 && Math.Abs(previewWindow.Height - 160) < 0.5,
                    $"拖出：预览是 160×160（{previewWindow.Width:0.#}×{previewWindow.Height:0.#}）");
                Check(previewWindow.Topmost, "拖出：预览置顶");
                Check(!previewWindow.ShowActivated, "拖出：预览不抢焦点");

                var hwnd = new System.Windows.Interop.WindowInteropHelper(previewWindow).Handle;
                long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
                Check((ex & 0x20) != 0, "拖出：预览带 WS_EX_TRANSPARENT（不挡拖放命中）");

                Save(Render(previewWindow), Path.Combine(_outDir, "pin-drag-preview.png"));
            }

            previewType.GetMethod("Close")!.Invoke(preview, null);
            Pump(pin);
            Check((Window?)previewType.GetProperty("Window", Private)!.GetValue(preview) == null,
                "拖出：预览关得掉");
        }
        finally
        {
            if (pin.IsVisible) pin.Close();
            store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id });
            try { File.Delete(dropped); } catch { /* 删不掉就算了 */ }
        }
    }

    private static void PinStackSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var stack = asm.GetType("LTools.Features.CapturePro.PinStacking")!;
        string group = (string)asm.GetType("LTools.Features.CapturePro.PinGroups")!
            .GetField("Default", Stat)!.GetValue(null)!;

        // 前一节可能还留着贴图：先全关，前后序才是干净的
        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        static string IdOf(Window w) => (string)w.GetType()
            .GetProperty("PinId", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .GetValue(w)!;

        static IntPtr HwndOf(Window w) => (IntPtr)w.GetType()
            .GetField("_hwnd", Private)!.GetValue(w)!;

        var windows = new Dictionary<string, Window>();
        var names = new Dictionary<string, string>();
        var allIds = new List<string>();

        Window NewPin(int x, int y, string shortName)
        {
            var pin = manager.GetMethod("Pin")!.Invoke(null, new object[]
            {
                image, new Int32Rect(x, y, image.PixelWidth, image.PixelHeight), "screenshot", null,
            }) as Window;
            if (pin == null) return null;

            string id = IdOf(pin);
            windows[id] = pin;
            names[id] = shortName;
            allIds.Add(id);
            return pin;
        }

        // ============ 记录里的前后序（只看这几张） ============
        List<string> Stored(string onlyGroup = null)
        {
            var result = new List<string>();
            var list = (System.Collections.IEnumerable)store.GetMethod("StackingOrder", Stat)!
                .Invoke(null, new object[] { onlyGroup ?? group })!;
            foreach (var id in list)
                if (names.TryGetValue((string)id!, out var name)) result.Add(name);
            return result;
        }

        string StoredText(string onlyGroup = null) => string.Join(",", Stored(onlyGroup));

        // ============ 原生 z 序（EnumWindows 就是前 → 后） ============
        List<IntPtr> ZOrder()
        {
            var order = new List<IntPtr>();
            EnumWindows((hwnd, _) =>
            {
                if (IsWindowVisible(hwnd)) order.Add(hwnd);
                return true;
            }, IntPtr.Zero);
            return order;
        }

        string Native()
        {
            var lookup = new Dictionary<IntPtr, string>();
            foreach (var pair in windows)
            {
                var hwnd = HwndOf(pair.Value);
                if (hwnd != IntPtr.Zero) lookup[hwnd] = names[pair.Key];
            }

            var result = new List<string>();
            foreach (var hwnd in ZOrder())
                if (lookup.TryGetValue(hwnd, out var name)) result.Add(name);
            return string.Join(",", result);
        }

        // ============ 真点一下（走窗口自己的 Preview 按下 / 抬起） ============
        void Click(Window w)
        {
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            };
            w.GetType().GetMethod("Window_MouseDown", Private)!.Invoke(w, new object[] { w, down });

            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            };
            w.GetType().GetMethod("Window_MouseUp", Private)!.Invoke(w, new object[] { w, up });
        }

        object RecordOf(string id)
        {
            foreach (var item in (System.Collections.IEnumerable)store.GetMethod("Load", Stat)!
                         .Invoke(null, null)!)
                if ((string)item!.GetType().GetProperty("Id")!.GetValue(item)! == id) return item;
            return null;
        }

        int RestoreBatch(params string[] batchIds)
        {
            var records = Array.CreateInstance(
                asm.GetType("LTools.Features.CapturePro.PinRecord")!, batchIds.Length);
            for (int i = 0; i < batchIds.Length; i++) records.SetValue(RecordOf(batchIds[i]), i);

            return (int)manager.GetMethod("RestoreBatch", Stat)!
                .Invoke(null, new object[] { records })!;
        }

        var p1 = NewPin(300, 200, "p1");
        var p2 = NewPin(360, 260, "p2");
        var p3 = NewPin(420, 320, "p3");
        Check(p1 != null && p2 != null && p3 != null, "堆积：先贴三张图");
        if (p1 == null || p2 == null || p3 == null) return;

        string id1 = IdOf(p1), id2 = IdOf(p2), id3 = IdOf(p3);

        try
        {
            // ============ 新贴的三张：新的在最上面 ============
            Check(StoredText() == "p3,p2,p1", $"堆积：记录里新的排最前（实际 {StoredText()}）");
            Check(Native() == "p3,p2,p1", $"堆积：原生 z 序跟着记录走（实际 {Native()}）");

            // ============ 点最下面那张：提到最前，位置不变 ============
            double left = p1.Left, top = p1.Top;
            Click(p1);
            Pump(p1);
            Check(StoredText() == "p1,p3,p2", $"堆积：点过的那张排到最前（实际 {StoredText()}）");
            Check(Native() == "p1,p3,p2", $"堆积：原生 z 序同样提到最前（实际 {Native()}）");
            Check(Math.Abs(p1.Left - left) < 0.01 && Math.Abs(p1.Top - top) < 0.01,
                "堆积：点击只改层级、不改位置");

            Click(p2);
            Pump(p2);
            Check(StoredText() == "p2,p1,p3", $"堆积：再点另一张也是提到最前（实际 {StoredText()}）");
            Click(p2);
            Pump(p2);
            Check(StoredText() == "p2,p1,p3", "堆积：再点同一张不再重复改动");

            // ============ 关掉一张：记录里保留槽位 ============
            manager.GetMethod("CloseRecord", Stat)!.Invoke(null, new object[] { id1 });
            Pump(p2);
            Check(StoredText() == "p2,p1,p3", $"堆积：关掉的那张槽位还在（实际 {StoredText()}）");
            Check(Native() == "p2,p3", $"堆积：关掉的窗口从原生 z 序里消失（实际 {Native()}）");

            // ============ 单张贴回：跳到最前 ============
            var back = manager.GetMethod("Restore", Stat)!
                .Invoke(null, new object[] { RecordOf(id1) }) as Window;
            Check(back != null, "堆积：单张贴回成功");
            if (back != null) windows[IdOf(back)] = back;
            Pump(p2);
            Check(StoredText() == "p1,p2,p3", $"堆积：单张贴回跳到最前（实际 {StoredText()}）");
            Check(Native().StartsWith("p1", StringComparison.Ordinal),
                $"堆积：贴回的那张真的在最上面（实际 {Native()}）");

            // ============ 批量贴回：保序，不整批顶到最前 ============
            manager.GetMethod("CloseRecord", Stat)!.Invoke(null, new object[] { id2 });
            manager.GetMethod("CloseRecord", Stat)!.Invoke(null, new object[] { id3 });
            Pump(p2);
            Check(StoredText() == "p1,p2,p3", $"堆积：两张都关掉后槽位都还在（实际 {StoredText()}）");

            int opened = RestoreBatch(id3, id2);
            Check(opened == 2, $"堆积：批量贴回两张（实际 {opened}）");
            foreach (var id in new[] { id2, id3 })
                if (manager.GetMethod("FindByRecord", Stat)!.Invoke(null, new object[] { id }) is Window w)
                    windows[id] = w;

            Pump(p2);
            Check(StoredText() == "p1,p2,p3", $"堆积：批量贴回保持原来的先后（实际 {StoredText()}）");
            Check(Native() == "p1,p2,p3", $"堆积：批量贴回后原生 z 序也保序（实际 {Native()}）");

            // ============ 切分组：每组各存一份 ============
            var win3 = windows[id3];
            manager.GetMethod("MoveToGroup", Stat)!.Invoke(null, new object[] { win3, "工作" });
            Pump(win3);
            Check(StoredText("工作") == "p3", $"堆积：换组后目标组里有它（实际 {StoredText("工作")}）");
            Check(StoredText() == "p1,p2", $"堆积：原组里不再有它（实际 {StoredText()}）");

            manager.GetMethod("SetActiveGroup", Stat)!.Invoke(null, new object[] { "工作" });
            Pump(win3);
            Check(win3.IsVisible && !windows[id1].IsVisible, "堆积：只看工作组时默认组藏起来");
            Check(Native() == "p3", $"堆积：只看得见工作组带来的那张（实际 {Native()}）");

            manager.GetMethod("SetActiveGroup", Stat)!.Invoke(null, new object[] { null });
            Pump(p2);
            Check(windows[id1].IsVisible && windows[id2].IsVisible, "堆积：回到全部时默认组又露出来");

            manager.GetMethod("MoveToGroup", Stat)!.Invoke(null, new object[] { win3, group });
            Pump(win3);
            Check(StoredText() == "p3,p1,p2", $"堆积：搬回默认组排到最前（实际 {StoredText()}）");
            Check(Native() == "p3,p1,p2", $"堆积：搬回来之后原生 z 序跟着变（实际 {Native()}）");

            // ============ 再贴一张：照样插到最前 ============
            var p4 = NewPin(480, 380, "p4");
            Check(p4 != null, "堆积：再贴一张");
            string id4 = p4 == null ? "" : IdOf(p4);
            if (p4 != null)
            {
                Pump(p4);
                Check(StoredText() == "p4,p3,p1,p2", $"堆积：新贴的插到最前（实际 {StoredText()}）");
                Check(Native().StartsWith("p4", StringComparison.Ordinal),
                    $"堆积：新贴的真的盖在最上面（实际 {Native()}）");
            }

            // ============ 删除一条记录：顺序里不留残渣 ============
            manager.GetMethod("CloseRecord", Stat)!.Invoke(null, new object[] { id1 });
            store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id1 });
            Pump(p2);
            Check(!Stored().Contains("p1"), $"堆积：删掉的记录不在顺序里（实际 {StoredText()}）");
            Check(Native() == "p4,p3,p2", $"堆积：删掉的那张也不在原生 z 序里（实际 {Native()}）");

            // ============ 落盘：pins.json 里真有 Stacking ============
            string path = (string)store.GetProperty("IndexPath", Stat)!.GetValue(null)!;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var persisted = new List<string>();
            if (doc.RootElement.TryGetProperty("Stacking", out var stacking) &&
                stacking.TryGetProperty(group, out var groupOrder))
            {
                foreach (var entry in groupOrder.EnumerateArray())
                    if (entry.GetString() is string id && names.TryGetValue(id, out var name))
                        persisted.Add(name);
            }

            Check(string.Join(",", persisted) == "p4,p3,p2",
                $"堆积：前后序写进了 pins.json（实际 {string.Join(",", persisted)}）");
        }
        finally
        {
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Check((int)stack.GetProperty("TrackedCount", Stat)!.GetValue(null)! == 0,
                "堆积：关闭全部之后清账");

            foreach (var id in allIds)
                store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id });
        }
    }

    // ============ 采集历史贴回（P5-h） ============

    /// <summary>
    /// 采集历史「按原选区 + 原外观贴回」（照上游 <c>presentation/historypinplacement</c>）：
    /// 记下的选区 + 外观 → 窗口按内边距让开、内容正好盖住原选区；图自带阴影 / 圆角时不铺底、不叠描边；
    /// 长截图 / 老记录（没有落位信息）退回「居中贴到光标所在屏」。
    /// </summary>
    private static void HistoryPinSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;
        var placementType = asm.GetType("LTools.Features.CapturePro.CapturePlacement")!;
        var appearanceType = asm.GetType("LTools.Features.CapturePro.PinAppearance")!;
        var styleType = asm.GetType("LTools.Features.CapturePro.SelectionStyle")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var shotType = asm.GetType("LTools.Features.CapturePro.ScreenShot")!;
        var pinClipboard = asm.GetType("LTools.Features.CapturePro.PinClipboard")!;
        var historyType = asm.GetType("LTools.Views.CapturePro.CaptureHistoryWindow")!;

        static BitmapSource Solid(int w, int h, Color color)
        {
            var pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 0xFF;
            }
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            image.Freeze();
            return image;
        }

        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
        catalogType.GetMethod("Clear")!.Invoke(null, null);

        // 选区样式：圆角 8 + 阴影 10（长度是 DIP，落进图里会按屏幕缩放换成图像素）
        var style = Activator.CreateInstance(styleType)!;
        styleType.GetProperty("CornerRadius")!.SetValue(style, 8.0);
        styleType.GetProperty("ShadowWidth")!.SetValue(style, 10.0);

        var selection = new Int32Rect(220, 180, 300, 160);
        double scale = (double)shotType.GetMethod("ScaleOf", Stat)!
            .Invoke(null, new object[] { selection })!;
        var appearance = appearanceType.GetMethod("FromStyle", Stat)!
            .Invoke(null, new object[] { style, scale, selection.Width, selection.Height })!;
        int radius = (int)appearanceType.GetProperty("RadiusPx")!.GetValue(appearance)!;
        int effect = (int)appearanceType.GetProperty("EffectPx")!.GetValue(appearance)!;
        Check(radius > 0 && effect > 0,
            $"贴回：外观按缩放算出来（圆角 {radius}px / 内边距 {effect}px @ {scale:0.##}x）");

        var of = placementType.GetMethod("Of", Stat)!;
        var placement = of.Invoke(null, new object[] { selection, style, scale, false })!;
        var scrollingPlacement = of.Invoke(null, new object[] { selection, style, scale, true })!;

        // 成品图 = 内容 + 两侧效果内边距（RenderStyled 的输出尺寸）
        int imageW = selection.Width + effect * 2;
        int imageH = selection.Height + effect * 2;

        object Make(int w, int h, string kind, object geometry)
        {
            var entry = catalogType.GetMethod("Create")!
                .Invoke(null, new object[] { w, h, kind, "贴图", geometry })!;
            catalogType.GetMethod("SaveAssets")!
                .Invoke(null, new object[] { entry, Solid(w, h, Colors.SteelBlue) });
            return entry;
        }

        var normal = Make(imageW, imageH, "shot", placement);
        var longShot = Make(200, 100, "shot", scrollingPlacement);
        var legacy = Make(200, 100, "shot", null);
        var record = Make(320, 200, "record", null);

        var window = (Window)Activator.CreateInstance(historyType)!;
        var pinned = new List<string>();

        try
        {
            window.Show();
            Pump(window);

            InvokeWith(window, "SelectPageSize", 20);
            InvokeWith(window, "Reload");
            Pump(window);

            var cards = (ItemsControl)Field(window, "CardList");
            Check(cards.Items.Count == 4, $"贴回：历史读出四条（实际 {cards.Items.Count}）");

            // 新的采集历史页用卡片「更多 → 贴到屏幕」贴回（旧页那个常驻 PinBtn 已删）
            ContextMenu MenuFor(object entry) =>
                (ContextMenu)InvokeValue(window, "BuildCardMenu", entry);

            MenuItem PinItemOf(object entry) => MenuFor(entry).Items.OfType<MenuItem>()
                .First(item => (string)item.Header! == "贴到屏幕");

            HashSet<string> PinIds()
            {
                var ids = new HashSet<string>();
                foreach (var item in (System.Collections.IEnumerable)store.GetMethod("Load", Stat)!
                             .Invoke(null, null)!)
                    ids.Add((string)item!.GetType().GetProperty("Id")!.GetValue(item)!);
                return ids;
            }

            object NewestPinRecord(HashSet<string> before)
            {
                foreach (var item in (System.Collections.IEnumerable)store.GetMethod("Load", Stat)!
                             .Invoke(null, null)!)
                {
                    string id = (string)item!.GetType().GetProperty("Id")!.GetValue(item)!;
                    if (!before.Contains(id)) return item;
                }

                return null;
            }

            Window? PinEntry(object entry)
            {
                var before = PinIds();
                ClickMenuItem(MenuFor(entry), "贴到屏幕");
                Pump(window);

                var rec = NewestPinRecord(before);
                Check(rec != null, "贴回：点一下真的写出了一条贴图记录");
                if (rec == null) return null;

                string id = (string)rec.GetType().GetProperty("Id")!.GetValue(rec)!;
                pinned.Add(id);
                return (Window)manager.GetMethod("FindByRecord", Stat)!
                    .Invoke(null, new object[] { id })!;
            }

            // ============ 普通截图：按原选区贴回 ============
            Check((bool)normal.GetType().GetProperty("CanPin")!.GetValue(normal)!,
                "贴回：普通截图记下了原选区");
            Check(PinItemOf(normal).IsEnabled, "贴回：截图条目能贴到屏幕");

            var box = new Int32Rect(selection.X - effect, selection.Y - effect, imageW, imageH);
            var pin = PinEntry(normal);
            Check(pin != null, "贴回：普通截图贴出了窗口");
            if (pin != null)
            {
                var area = (Int32Rect)pin.GetType().GetProperty("Area")!.GetValue(pin)!;
                Check(area == box,
                    $"贴回：窗口按内边距让开、内容盖住原选区（实际 {area.X},{area.Y} {area.Width}×{area.Height}）");
                Check(Math.Abs(pin.Left - box.X / scale) < 1.1 && Math.Abs(pin.Top - box.Y / scale) < 1.1,
                    $"贴回：窗口落在原选区左上角（实际 {pin.Left:0.#},{pin.Top:0.#}）");
                Check(Math.Abs(pin.Width - box.Width / scale) < 1.1 &&
                      Math.Abs(pin.Height - box.Height / scale) < 1.1,
                    $"贴回：窗口尺寸按屏幕缩放换算（实际 {pin.Width:0.#}×{pin.Height:0.#}）");

                Check(((FrameworkElement)Field(pin, "Backdrop")).Visibility == Visibility.Collapsed,
                    "贴回：图自带圆角 / 阴影，窗口不再铺不透明底");
                Check(((FrameworkElement)Field(pin, "Frame")).Visibility == Visibility.Collapsed,
                    "贴回：图里已经画了阴影，窗口不再叠 1px 描边");

                var look = pin.GetType().GetProperty("Appearance",
                    BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pin)!;
                Check((int)appearanceType.GetProperty("RadiusPx")!.GetValue(look)! == radius,
                    "贴回：窗口拿到的圆角与记录一致");
                Check((int)appearanceType.GetProperty("EffectPx")!.GetValue(look)! == effect,
                    "贴回：窗口拿到的效果内边距与记录一致");

                Save(Render(pin), Path.Combine(_outDir, "history-pin.png"));

                // pins.json 里也记下了外观（重开贴图时按它恢复）
                var records = (System.Collections.IEnumerable)store.GetMethod("Load", Stat)!
                    .Invoke(null, null)!;
                object kept = null;
                foreach (var item in records)
                {
                    string id = (string)item!.GetType().GetProperty("Id")!.GetValue(item)!;
                    if (pinned.Count > 0 && id == pinned[0]) { kept = item; break; }
                }

                Check(kept != null, "贴回：记录还在盘点里");
                if (kept != null)
                {
                    string source = (string)kept.GetType().GetProperty("Source")!.GetValue(kept)!;
                    Check(source == "history", $"贴回：来源记成 history（实际 {source}）");
                    Check((int)kept.GetType().GetProperty("RadiusPx")!.GetValue(kept)! == radius,
                        "贴回：圆角落进 pins.json");
                    Check((int)kept.GetType().GetProperty("EffectPx")!.GetValue(kept)! == effect,
                        "贴回：效果内边距落进 pins.json");
                    Check((string)kept.GetType().GetProperty("Decoration")!.GetValue(kept)! == "shadow",
                        "贴回：装饰类型落进 pins.json");
                }
            }

            // ============ 长截图：只能居中贴 ============
            Check(!(bool)longShot.GetType().GetProperty("CanPin")!.GetValue(longShot)!,
                "贴回：长截图不按原选区贴回");
            Check(PinItemOf(longShot).IsEnabled, "贴回：长截图仍能贴（居中）");

            var longPin = PinEntry(longShot);
            Check(longPin != null, "贴回：长截图贴出了窗口");
            if (longPin != null)
            {
                var area = (Int32Rect)longPin.GetType().GetProperty("Area")!.GetValue(longPin)!;
                Check(area != box, "贴回：长截图不按原选区摆");

                double cursorScale = (double)pinClipboard.GetMethod("ScreenScale", Stat)!
                    .Invoke(null, null)!;
                var work = (Rect)pinClipboard.GetMethod("WorkAreaDip", Stat)!
                    .Invoke(null, new object[] { cursorScale })!;
                double centerX = longPin.Left + longPin.Width / 2;
                double centerY = longPin.Top + longPin.Height / 2;
                Check(Math.Abs(centerX - (work.X + work.Width / 2)) <= 1.5 &&
                      Math.Abs(centerY - (work.Y + work.Height / 2)) <= 1.5,
                    $"贴回：长截图居中贴到光标所在屏（实际中心 {centerX:0.#},{centerY:0.#}）");

                Check(((FrameworkElement)Field(longPin, "Backdrop")).Visibility == Visibility.Visible,
                    "贴回：没有外观时照常铺不透明底");
                Check(((FrameworkElement)Field(longPin, "Frame")).Visibility == Visibility.Visible,
                    "贴回：没有外观时照常画窗口描边");
            }

            // ============ 老记录（P5-h 之前）：没有落位信息，同样居中 ============
            Check(!(bool)legacy.GetType().GetProperty("CanPin")!.GetValue(legacy)!,
                "贴回：老记录没有落位信息");
            Check(PinItemOf(legacy).IsEnabled, "贴回：老记录仍能贴（居中）");
            var legacyPin = PinEntry(legacy);
            Check(legacyPin != null, "贴回：老记录贴出了窗口");

            // ============ 录屏：没有截图可以贴 ============
            Check(!PinItemOf(record).IsEnabled, "贴回：录屏条目的「贴到屏幕」置灰");
        }
        finally
        {
            foreach (var id in pinned)
                manager.GetMethod("CloseRecord", Stat)!.Invoke(null, new object[] { id });
            foreach (var id in pinned)
                store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id });
            window.Close();

            // 后面的录屏段假设采集历史里只有它自己那条：这里造的四条清掉
            catalogType.GetMethod("Clear")!.Invoke(null, null);
        }
    }

    /// <summary>
    /// 图片编辑窗（P6-a1）：空态 / 装图 / 适应与 100% / 滚轮 / 裁剪条带与提交 /
    /// 撤销重做 / 导出（复制 · 快速保存）/ 一张图一个窗口。
    /// </summary>
    private static void ImageEditSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;

        // 会话入口用 Ui.Post 判断线程（真程序在 App.OnStartup 里初始化）
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { Dispatcher.CurrentDispatcher });

        var docType = asm.GetType("LTools.Features.CapturePro.ImageEdit.ImageEditDocument")!;
        var axisType = asm.GetType("LTools.Features.CapturePro.ImageEdit.CropAxis")!;
        var saveType = asm.GetType("LTools.Features.CapturePro.CaptureSave")!;
        var sessionType = asm.GetType("LTools.Features.CapturePro.ImageEdit.ImageEditSession")!;

        string dir = Path.Combine(_outDir, "imageedit");
        Directory.CreateDirectory(dir);

        // 源图 320×200：(R, G) = (x, y)，剪完能逐像素认出是原图哪一块
        var pixels = new byte[320 * 200 * 4];
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                int i = (y * 320 + x) * 4;
                pixels[i] = (byte)(x % 256);
                pixels[i + 1] = (byte)(y % 256);
                pixels[i + 2] = (byte)((x + y) % 256);
                pixels[i + 3] = 0xFF;
            }
        }

        var source = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Pbgra32, null, pixels, 320 * 4);
        source.Freeze();

        string file = Path.Combine(dir, "edit-source.png");
        Save(source, file);

        var window = new ImageEditWindow();
        window.Show();
        Pump(window);

        var emptyState = (FrameworkElement)Field(window, "EmptyState");
        var fitBtn = (Button)Field(window, "FitBtn");
        var actualBtn = (Button)Field(window, "ActualBtn");
        var infoText = (TextBlock)Field(window, "InfoText");
        var zoomText = (TextBlock)Field(window, "ZoomText");
        var notice = (TextBlock)Field(window, "NoticeText");
        var zoomScale = (ScaleTransform)Field(window, "ZoomScale");
        var surface = (UIElement)Field(window, "Surface");
        var cropLayer = Field(window, "CropLayer");
        var toolbar = Field(window, "Toolbar");

        // 浮动工具条（照上游 placeToolbar）：平移是第 0 格，其余从 ToolCatalog 生成
        var panBtn = (RadioButton)Field(toolbar, "_panBtn")!;
        var cropRowsBtn = (ToggleButton)Field(toolbar, "_cropRowsBtn")!;
        var cropColsBtn = (ToggleButton)Field(toolbar, "_cropColumnsBtn")!;
        var undoBtn = (Button)Field(toolbar, "_undoBtn")!;
        var redoBtn = (Button)Field(toolbar, "_redoBtn")!;
        var toolRow = (Panel)Field(toolbar, "ToolRow")!;
        var copyBtn = toolRow.Children.OfType<Button>()
            .First(b => (b.ToolTip?.ToString() ?? "").Contains("复制到剪贴板"));
        var pinBtn = toolRow.Children.OfType<Button>()
            .First(b => (b.ToolTip?.ToString() ?? "").Contains("贴图到屏幕"));

        Check(window.Title.StartsWith("图片编辑"), "编辑窗标题：" + window.Title);
        Check(emptyState.Visibility == Visibility.Visible, "没图时显示空态");
        Check(!fitBtn.IsEnabled && !actualBtn.IsEnabled, "没图时缩放按钮置灰");
        Check(((FrameworkElement)toolbar).Visibility == Visibility.Collapsed,
            "没图时工具条收起（照上游：有图才摆调色板）");
        Check(zoomText.Text.Length == 0 && infoText.Text.Length == 0, "没图时状态栏是空的");
        Check(panBtn.IsChecked == true, "打开就是「平移」模式（上游默认 Mode::Pan）");
        Check(ReferenceEquals(toolRow.Children[0], panBtn), "平移格排在最前面");
        Check(toolRow.Children.OfType<ToggleButton>().Count() >= 2, "裁剪格是两条可勾选的开关");

        // ---- 打开图片（后台解码 → 空态收起 → 自动适应窗口）----
        window.PresentFile(file);
        bool opened = WaitFor(window, () => emptyState.Visibility == Visibility.Collapsed, 8000);
        Check(opened, "打开图片后空态收起");

        var doc = Field(window, "_doc");
        Check((bool)docType.GetProperty("HasImage")!.GetValue(doc)!, "文档装上了图");
        Check(fitBtn.IsEnabled && actualBtn.IsEnabled, "有图后缩放按钮可用");
        Check(((FrameworkElement)toolbar).Visibility == Visibility.Visible, "有图后工具条露出来");
        Check(undoBtn.IsEnabled == false && redoBtn.IsEnabled == false, "刚打开没有可撤销的");
        Check(infoText.Text.Contains("320 × 200"), "状态栏报图像尺寸：" + infoText.Text);
        Check(zoomText.Text.EndsWith("%"), "缩放读数带百分号：" + zoomText.Text);

        double dpi = (double)Field(window, "_dpi");
        double fitZoom = zoomScale.ScaleX;
        Check(fitZoom > 1, $"小图按适应窗口放大（{zoomText.Text}）");

        // ---- 100% = 1 图像像素对 1 物理像素 ----
        actualBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(Math.Abs(zoomScale.ScaleX * dpi - 1) < 0.02,
            $"100% 时 1 图像像素 = 1 物理像素（缩放 {zoomScale.ScaleX:0.###} × DPI {dpi:0.##}）");

        // ---- 滚轮缩放（以光标为锚，一格 1.15 倍）----
        double before = zoomScale.ScaleX;
        Wheel(surface, 120);
        Pump(window);
        Check(zoomScale.ScaleX > before + 1e-6, $"滚轮向上放大（{before:0.###} → {zoomScale.ScaleX:0.###}）");
        Wheel(surface, -120);
        Pump(window);
        Check(Math.Abs(zoomScale.ScaleX - before) < 1e-6, "滚轮向下缩回原比例");

        // ---- 裁剪行：悬停导引线 / 红带 / 「N px」----
        cropRowsBtn.IsChecked = true;
        cropRowsBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(Field(window, "_cropAxis") != null, "勾上「裁剪行」进入裁剪模式");
        Check(cropRowsBtn.IsChecked == true && cropColsBtn.IsChecked == false, "裁剪行按钮回显勾上");

        SetField(window.GetType(), window, "_cropping", true);
        SetField(window.GetType(), window, "_cropFirst", 50);
        SetField(window.GetType(), window, "_cropLast", 90);
        InvokeWith(window, "UpdateCropLayer");
        Pump(window);

        var strip = (Rect)cropLayer.GetType().GetProperty("Strip")!.GetValue(cropLayer)!;
        int stripPixels = (int)cropLayer.GetType().GetProperty("Pixels")!.GetValue(cropLayer)!;
        Check(stripPixels == 40, $"条带报 40 px（实际 {stripPixels}）");
        Check(Math.Abs(strip.Height - 40 * zoomScale.ScaleX) < 1.5,
            $"红带高度 = 40 图像像素 × 缩放（{strip.Height:0.#} DIP）");
        Check(strip.Width > 0 && Math.Abs(strip.Width - 320 * zoomScale.ScaleX) < 1.5,
            "红带横跨整张图");
        Save(CropElement(window, (FrameworkElement)cropLayer),
            Path.Combine(dir, "crop-strip.png"));

        // ---- 松手提交：高度真的少了 40，进撤销栈 ----
        InvokeWith(window, "CommitCrop");
        Pump(window);
        Check((int)docType.GetProperty("Height")!.GetValue(doc)! == 160,
            $"剪掉 40 行后高度 160（实际 {docType.GetProperty("Height")!.GetValue(doc)}）");
        Check(infoText.Text.Contains("320 × 160"), "状态栏跟着改：" + infoText.Text);
        Check(undoBtn.IsEnabled, "裁剪后能撤销");

        undoBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check((int)docType.GetProperty("Height")!.GetValue(doc)! == 200, "撤销恢复高度");
        Check(redoBtn.IsEnabled, "撤销后能重做");

        redoBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check((int)docType.GetProperty("Height")!.GetValue(doc)! == 160, "重做又剪掉了");

        // ---- 裁剪列：宽度少 20 ----
        cropRowsBtn.IsChecked = false;
        cropColsBtn.IsChecked = true;
        cropColsBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(cropColsBtn.IsChecked == true && cropRowsBtn.IsChecked == false, "裁剪列按钮回显勾上");
        SetField(window.GetType(), window, "_cropping", true);
        SetField(window.GetType(), window, "_cropFirst", 10);
        SetField(window.GetType(), window, "_cropLast", 30);
        InvokeWith(window, "UpdateCropLayer");
        InvokeWith(window, "CommitCrop");
        Pump(window);
        Check((int)docType.GetProperty("Width")!.GetValue(doc)! == 300, "剪掉 20 列后宽度 300");
        Check(infoText.Text.Contains("300 × 160"), "状态栏跟着改：" + infoText.Text);

        // ---- 导出 = 剪完的那块原图（逐像素）----
        var rendered = (BitmapSource)InvokeValue(window, "RenderImage")!;
        Check(rendered.PixelWidth == 300 && rendered.PixelHeight == 160,
            $"导出尺寸 = 剪完的尺寸：{rendered.PixelWidth}×{rendered.PixelHeight}");
        var dot = PixelAt(rendered, 0, 0);
        Check(PixelAt(source, 0, 0) == dot,
            $"导出左上角 = 原图 (0,0)（逐像素）：期望 {PixelAt(source, 0, 0)} 实际 {dot}");
        Check(PixelAt(source, 30, 90) == PixelAt(rendered, 10, 50),
            $"剪掉的那条被跳过：导出 (10,50) = 原图 (30,90)"
            + $"（期望 {PixelAt(source, 30, 90)} 实际 {PixelAt(rendered, 10, 50)}）");
        Check(PixelAt(source, 319, 199) == PixelAt(rendered, 299, 159),
            "导出右下角 = 原图 (319,199)（逐像素）");
        Save(rendered, Path.Combine(dir, "export.png"));

        // ---- 复制到剪贴板：成品进剪贴板，且算「已导出」----
        System.Windows.Clipboard.Clear();
        copyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(System.Windows.Clipboard.ContainsImage(), "复制把成品放进剪贴板");
        Check(notice.Text.Contains("已复制"), "状态栏说已复制：" + notice.Text);
        Check(!(bool)docType.GetProperty("Dirty")!.GetValue(doc)!, "复制算已导出 → 不再算改过");

        // ---- 快速保存：真的落一张 png，内容就是剪完的那块 ----
        string quickDir = (string)saveType.GetProperty("QuickSaveDir", Stat)!.GetValue(null)!;
        var beforeFiles = Directory.Exists(quickDir)
            ? new HashSet<string>(Directory.GetFiles(quickDir), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        InvokeWith(window, "QuickSave");
        Pump(window);

        var added = Directory.Exists(quickDir)
            ? Directory.GetFiles(quickDir).Where(path => !beforeFiles.Contains(path)).ToArray()
            : Array.Empty<string>();
        Check(added.Length == 1, $"快速保存写出一张 png（实际 {added.Length}）");
        if (added.Length > 0)
        {
            var reloaded = BitmapFrame.Create(new Uri(added[0]), BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);
            Check(reloaded.PixelWidth == 300 && reloaded.PixelHeight == 160,
                $"落盘的就是剪完的尺寸：{reloaded.PixelWidth}×{reloaded.PixelHeight}");
            Check(PixelAt(reloaded, 0, 0) == PixelAt(source, 0, 0),
                $"落盘的左上角 = 原图 (0,0)：期望 {PixelAt(source, 0, 0)} 实际 {PixelAt(reloaded, 0, 0)}");
            File.Delete(added[0]);
        }

        // ---- P6-a2：标注手势（画一条 → 选中拖动 → 框选 → 删除 → 撤销）----
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var docObj = docType.GetProperty("Doc")!.GetValue(doc)!;
        var marksProp = docObj.GetType().GetProperty("Marks")!;
        int MarkCount() => ((System.Collections.ICollection)marksProp.GetValue(docObj)!).Count;
        object MarkAt(int i) => ((System.Collections.IList)marksProp.GetValue(docObj)!)[i]!;

        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Shape"));
        Check((bool)InvokeValue(window, "BeginDrawGesture", new Point(20, 20), 1)!,
            "选「形状」后按下被画布手势接住");
        InvokeWith(window, "UpdateDraw", new Point(140, 100));
        InvokeWith(window, "FinishDraw", new Point(140, 100));
        Pump(window);
        Check(MarkCount() == 1, $"拖出一个形状（实际 {MarkCount()} 条）");

        var shapeFrame = (Rect)MarkAt(0).GetType().GetProperty("Frame")!.GetValue(MarkAt(0))!;
        Check(Math.Abs(shapeFrame.Width - 120) < 2 && Math.Abs(shapeFrame.Height - 80) < 2,
            $"形状尺寸就是拖出的 120×80（实际 {shapeFrame.Width:0.#}×{shapeFrame.Height:0.#}）");
        Check(undoBtn.IsEnabled, "画完能撤销");

        // 选择：点中同一条（点在描边上，空心形状的里面不算命中——与截图覆盖层同一套）并整体拖动 (30,20)
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Select"));
        InvokeWith(window, "BeginDrawGesture", new Point(20, 60), 1);
        Check(Field(window, "_drag").ToString() == "Mark", "在选择工具里按中标注 = 拖动它");
        InvokeWith(window, "UpdateDraw", new Point(50, 80));
        InvokeWith(window, "FinishDraw", new Point(50, 80));
        Pump(window);

        var movedFrame = (Rect)MarkAt(0).GetType().GetProperty("Frame")!.GetValue(MarkAt(0))!;
        Check(Math.Abs(movedFrame.X - shapeFrame.X - 30) < 2 && Math.Abs(movedFrame.Y - shapeFrame.Y - 20) < 2,
            $"拖动把标注挪了 (30,20)（实际 {movedFrame.X - shapeFrame.X:0.#},{movedFrame.Y - shapeFrame.Y:0.#}）");

        // 空白框选 → 命中；Delete 删掉
        InvokeWith(window, "BeginDrawGesture", new Point(1, 1), 1);
        InvokeWith(window, "UpdateDraw", new Point(220, 160));
        InvokeWith(window, "FinishDraw", new Point(220, 160));
        Pump(window);

        var selected = (System.Collections.IEnumerable)Field(window, "_selected")!;
        Check(selected.Cast<object>().Any(), "框选把形状选中");

        InvokeWith(window, "DeleteSelection");
        Pump(window);
        Check(MarkCount() == 0, $"删除选中后一条不剩（实际 {MarkCount()} 条）");

        undoBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(MarkCount() == 1, "撤销把删掉的形状找回来");

        // 平移：点工具条的平移格 = 退出绘图、进入平移
        panBtn.IsChecked = true;
        panBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((bool)Field(window, "_panMode"), "点「平移」进入平移模式");
        Check(panBtn.IsChecked == true, "平移格保持勾上");

        // 文字：就地输入框 → 提交成一条文字标注
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Text"));
        Check(!(bool)Field(window, "_panMode"), "选绘图工具后离开平移模式");

        InvokeWith(window, "BeginDrawGesture", new Point(40, 40), 1);
        Pump(window);

        var textEntry = (TextBox)Field(window, "TextEntry");
        Check(textEntry.Visibility == Visibility.Visible, "文字工具弹出行内输入框");

        textEntry.Text = "冒烟文字";
        InvokeWith(window, "CommitText");
        Pump(window);

        Check(textEntry.Visibility == Visibility.Collapsed, "提交后输入框收起");
        Check(MarkCount() == 2, $"文字落成一条标注（实际 {MarkCount()} 条）");
        Check((string?)MarkAt(1).GetType().GetProperty("Content")!.GetValue(MarkAt(1)) == "冒烟文字",
            "文字内容原样落盘");

        // 裁剪模式里换绘图工具 = 退出裁剪（否则画布按下会被当成拉条带）
        cropRowsBtn.IsChecked = true;
        cropRowsBtn.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check(Field(window, "_cropAxis") != null, "重新进裁剪模式");

        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Shape"));
        Pump(window);
        Check(Field(window, "_cropAxis") == null, "换成绘图工具就退出裁剪");
        Check(cropRowsBtn.IsChecked == false, "退出裁剪后按钮也取消勾选");

        // ---- P6-a3：识别按钮（整张底图 → 结果窗）----
        var recognizeBtn = toolRow.Children.OfType<Button>()
            .FirstOrDefault(b => (b.ToolTip?.ToString() ?? "").Contains("识别文字"));
        Check(recognizeBtn != null, "编辑窗工具条有「识别」按钮");

        InvokeWith(window, "OpenRecognition");
        Pump(window);

        var resultWin = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.GetType().Name == "CaptureResultWindow");
        Check(resultWin != null, "识别打开结果窗");
        if (resultWin != null)
        {
            var shown = (BitmapSource?)resultWin.GetType()
                .GetField("_image", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resultWin);
            Check(shown != null && shown.PixelWidth == 300 && shown.PixelHeight == 160,
                $"结果窗拿的是整张底图（不带标注）：{shown?.PixelWidth}×{shown?.PixelHeight}");
            resultWin.Close();
            Pump(window);
        }

        // ---- P6-a3：自动打码（识别范围 = 整张图）----
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "AutoFilter"));
        Pump(window);

        var canvas = Field(window, "Canvas");
        var autoFilterProp = canvas.GetType().GetProperty("AutoFilter")!;
        Check(autoFilterProp.GetValue(canvas) != null, "进自动打码后画布拿到识别状态");

        var state = Field(window, "_autoFilter");
        var stateType = state.GetType();
        var detectingProp = stateType.GetProperty("Detecting")!;
        WaitFor(window, () => !(bool)detectingProp.GetValue(state)!, 15000);
        Check(!(bool)detectingProp.GetValue(state)!, "整张图识别在时限内跑完（不再扫光带）");

        // 合成图不一定检得出区域，灌两块来验「点一下 / 框选 / 填充区域」这三条路
        var regionType = asm.GetType("LTools.Features.CapturePro.AutoFilter.AutoRegion")!;
        var regions = (System.Collections.IList)Activator.CreateInstance(
            typeof(List<>).MakeGenericType(regionType))!;
        regions.Add(Activator.CreateInstance(regionType, 1, new Rect(10, 10, 120, 40), "text"));
        regions.Add(Activator.CreateInstance(regionType, 2, new Rect(10, 80, 120, 40), "text"));

        var bounds = new Rect(0, 0, (int)docType.GetProperty("Width")!.GetValue(doc)!,
            (int)docType.GetProperty("Height")!.GetValue(doc)!);
        stateType.GetMethod("Set")!.Invoke(state, new object[] { bounds, regions });
        InvokeWith(window, "SyncAutoFilter", false);
        Pump(window);
        Check((bool)stateType.GetMethod("Matches")!.Invoke(state, new object[] { bounds })!,
            "区域与整张图对得上");

        var fillRegionsBtn = (Button)Field(toolbar, "_fillRegionsButton")!;
        Check(fillRegionsBtn.IsEnabled, "有区域后「填充区域」可用");

        object? FillMark(int id) => ((System.Collections.IList)marksProp.GetValue(docObj)!)
            .Cast<object>()
            .FirstOrDefault(m => m.GetType().GetProperty("AutoRegionId") is { } p && (int?)p.GetValue(m) == id);

        int marksBefore = MarkCount();
        InvokeWith(window, "BeginAutoFilterGesture", new Point(30, 20));
        InvokeWith(window, "FinishAutoFilterGesture", new Point(30, 20));
        Pump(window);
        Check(MarkCount() == marksBefore + 1, $"点一下命中区域就落一条打码（实际 {MarkCount() - marksBefore} 条）");
        Check(FillMark(1) != null, "打码记住了命中的区域 id");
        Check(FillMark(1)!.GetType().Name == "Filter", "自动打码落的是滤镜标注");

        InvokeWith(window, "BeginAutoFilterGesture", new Point(30, 20));
        InvokeWith(window, "FinishAutoFilterGesture", new Point(30, 20));
        Pump(window);
        Check(MarkCount() == marksBefore && FillMark(1) == null, "同类型再点一次把它撤掉（toggle）");

        // 框选一批：拖过两块 → 一次填两条
        InvokeWith(window, "BeginAutoFilterGesture", new Point(5, 5));
        InvokeWith(window, "UpdateAutoFilterGesture", new Point(150, 150));
        InvokeWith(window, "FinishAutoFilterGesture", new Point(150, 150));
        Pump(window);
        Check(MarkCount() == marksBefore + 2 && FillMark(1) != null && FillMark(2) != null,
            $"框选一次填满两块（实际 {MarkCount() - marksBefore} 条）");

        InvokeWith(window, "BeginAutoFilterGesture", new Point(5, 5));
        InvokeWith(window, "UpdateAutoFilterGesture", new Point(150, 150));
        InvokeWith(window, "FinishAutoFilterGesture", new Point(150, 150));
        Pump(window);
        Check(MarkCount() == marksBefore, "框选再拖一次把它们撤掉");

        InvokeWith(window, "OnAutoFilterFillRequested", "text");
        Pump(window);
        Check(MarkCount() == marksBefore + 2, "「填充区域」一次补满这一类（只补不删）");

        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Select"));
        Pump(window);
        Check(autoFilterProp.GetValue(canvas) == null, "换工具后画布清掉自动打码覆盖层");
        Check(((System.Collections.ICollection)stateType.GetProperty("Regions")!.GetValue(state)!).Count == 0,
            "换工具后识别结果也丢掉");

        // 后面要关窗：把这几笔画过的记为「已导出」，免得弹「放弃更改」
        docType.GetMethod("MarkSaved")!.Invoke(doc, null);

        // 人眼对照一张：浮动工具条（右下）+ 缩放条（底部居中）+ 标注
        Pump(window);
        Save(RenderHi(window), Path.Combine(dir, "edit-window.png"));

        // ---- 一张图一个窗口：新开的图不会顶掉当前窗 ----
        var openList = (System.Collections.IList)sessionType
            .GetField("Open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var existing = openList.Cast<object>().ToList();

        string second = Path.Combine(dir, "edit-two.png");
        Save(source, second);

        sessionType.GetMethod("OpenFiles", Stat)!
            .Invoke(null, new object[] { new[] { file, second } });

        var created = openList.Cast<object>().Where(item => !existing.Contains(item)).ToList();
        Check(created.Count == 2, $"两张图各开一个窗口（实际新开 {created.Count}）");
        foreach (var item in created)
        {
            var createdDoc = Field(item, "_doc");
            WaitFor((Window)item, () => (bool)docType.GetProperty("HasImage")!.GetValue(createdDoc)!, 8000);
            Check((bool)docType.GetProperty("HasImage")!.GetValue(createdDoc)!,
                "新开的窗口里装上了图");
            ((Window)item).Close();
        }

        Check((bool)docType.GetProperty("HasImage")!.GetValue(doc)!, "原来的窗口内容没被顶掉");

        // 关窗：导出过（不算脏）→ 不弹「放弃更改」
        window.Close();
        Pump(window);
        Check(openList.Count == existing.Count - 1, "关掉的窗口从登记表里摘掉");

        // ---- 空窗入口：托盘「图片编辑…」不急着实例化图片 ----
        var empty = new ImageEditWindow();
        empty.Show();
        Pump(empty);
        Check(((FrameworkElement)Field(empty, "EmptyState")).Visibility == Visibility.Visible,
            "新开的空窗还是空态");
        empty.Close();
        Pump(empty);
    }

    /// <summary>
    /// P6-b 切图窗：空态 / 装图 / 智能线 / 真鼠标拖线 / Delete 删线 / 撤销 / 宫格 / 重叠 /
    /// 输入错误 / 全部保存落盘 / 关窗确认 / 空窗复用。
    /// </summary>
    /// <remarks>
    /// 拖线用真实鼠标注入：只有真窗口里的命中（6 DIP 容差）、捕获、松开提交这几步都跑过，
    /// 才算「导引线能拖」。导出走窗口自己的 <c>StartExport</c>，导出成功后它会像上游那样打开
    /// 保存目录，这里把那个 Explorer 窗口按标题关掉，免得留一屏窗口。
    /// </remarks>
    private static void SplitSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { Dispatcher.CurrentDispatcher });

        string dir = Path.Combine(_outDir, "split");
        Directory.CreateDirectory(dir);

        // 源图 320×200：R = x、G = y（导出后逐像素能认出裁的是哪一块）
        var pixels = new byte[320 * 200 * 4];
        for (int y = 0; y < 200; y++)
        {
            for (int x = 0; x < 320; x++)
            {
                int i = (y * 320 + x) * 4;
                pixels[i] = (byte)x;
                pixels[i + 1] = (byte)y;
                pixels[i + 2] = (byte)x;
                pixels[i + 3] = 0xFF;
            }
        }

        var source = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        source.Freeze();
        string file = Path.Combine(dir, "split-source.png");
        Save(source, file);

        var window = new ImageSplitWindow();
        window.Show();
        Pump(window);

        var preview = (FrameworkElement)Field(window, "Preview");
        var emptyState = (FrameworkElement)Field(window, "EmptyState");
        var toolbar = (FrameworkElement)Field(window, "CanvasToolbar");
        var dims = (TextBlock)Field(window, "DimensionsText");
        var total = (TextBlock)Field(window, "TotalText");
        var status = (TextBlock)Field(window, "StatusText");
        var saveAll = (Button)Field(window, "SaveAllBtn");
        var undoBtn = (Button)Field(window, "UndoBtn");
        var redoBtn = (Button)Field(window, "RedoBtn");
        var fitBtn = (Button)Field(window, "FitBtn");
        var reloadBtn = (Button)Field(window, "ReloadBtn");
        var dirGrid = (RadioButton)Field(window, "DirGrid");
        var typeEqual = (RadioButton)Field(window, "TypeEqual");
        var unitPercentage = (RadioButton)Field(window, "UnitPercentage");
        var countBox = (TextBox)Field(window, "CountBox");
        var rowsBox = (TextBox)Field(window, "RowsBox");
        var overlapBox = (TextBox)Field(window, "OverlapBox");
        var rowsRow = (FrameworkElement)Field(window, "RowsRow");
        var countRow = (FrameworkElement)Field(window, "CountRow");
        var sizeRow = (FrameworkElement)Field(window, "SizeRow");
        var rowsError = (TextBlock)Field(window, "RowsError");

        Check(window.Title == "切图", "切图窗标题：" + window.Title);
        Check(emptyState.Visibility == Visibility.Visible, "没有图时显示空态");
        Check(toolbar.Visibility == Visibility.Collapsed, "没有图时不摆画布工具条");
        Check(!saveAll.IsEnabled && !undoBtn.IsEnabled && !redoBtn.IsEnabled, "没有图时保存 / 撤销 / 重做都置灰");
        Check(dims.Text.Length == 0 && total.Text.Length == 0, "没有图时页脚不报尺寸 / 张数");
        Check(((RadioButton)Field(window, "DirVertical")).IsChecked == true, "默认纵向切");
        Check(((RadioButton)Field(window, "TypeSmart")).IsChecked == true, "默认智能切分");
        Check(unitPercentage.IsChecked == true, "默认按百分比");
        Check(countBox.Text == "2" && overlapBox.Text == "0", "默认 2 张、不重叠");
        Check(rowsRow.Visibility == Visibility.Collapsed, "非宫格时收起行列");

        // ---- 打开图片 → 后台解码 + 找智能线 ----
        window.PresentFile(file);
        Check(WaitFor(window, () => emptyState.Visibility == Visibility.Collapsed, 15000),
            "打开图片后空态收起");
        Pump(window);

        var doc = Field(window, "_doc");
        var docType = doc.GetType();
        object State() => docType.GetProperty("State")!.GetValue(doc)!;
        object Layout() => docType.GetProperty("Layout")!.GetValue(doc)!;
        int[] Lines(bool vertical) => (int[])State().GetType()
            .GetProperty(vertical ? "Vertical" : "Horizontal")!.GetValue(State())!;

        Check(dims.Text.Contains("320 × 200"), "页脚报图像尺寸：" + dims.Text);
        Check(total.Text.Contains("共 2 张"), "默认切 2 张：" + total.Text);
        Check(toolbar.Visibility == Visibility.Visible, "有图后画布工具条露出来");
        Check(fitBtn.IsEnabled && reloadBtn.IsEnabled, "有图后「适应窗口 / 重选图片」可用");
        Check(saveAll.IsEnabled, "布局有效时「全部保存」可用");
        Check(Lines(true).SequenceEqual(new[] { 160 }),
            $"渐变图的智能线落在标称位置 160（实际 [{string.Join(", ", Lines(true))}]）");
        Check(!undoBtn.IsEnabled && !redoBtn.IsEnabled, "刚装好图没有可撤销的");

        // ---- 等分 4 张：前几段先宽后窄 ----
        typeEqual.IsChecked = true;
        Pump(window);
        countBox.Text = "4";
        InvokeWith(window, "CommitDraft");
        Pump(window);
        Check(Lines(true).SequenceEqual(new[] { 80, 160, 240 }),
            $"等分 4 张 → 80 / 160 / 240（实际 [{string.Join(", ", Lines(true))}]）");
        Check(total.Text.Contains("共 4 张"), "页脚跟着报 4 张：" + total.Text);
        Check(undoBtn.IsEnabled, "改过设置就能撤销");

        // ---- 真鼠标把中间那条线从 160 拖到 140 ----
        SetTopmost(window, true);
        ForceForeground(window);
        Pump(window);

        Point LinePoint(int coordinate) =>
            preview.PointToScreen((Point)InvokeValue(preview, "ToViewport", new Point(coordinate, 100)));

        Point from = LinePoint(160), to = LinePoint(140);
        SetCursorPos((int)from.X, (int)from.Y);
        Thread.Sleep(180);
        Pump(window);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        for (int step = 1; step <= 4; step++)
        {
            SetCursorPos((int)(from.X + (to.X - from.X) * step / 4.0),
                (int)(from.Y + (to.Y - from.Y) * step / 4.0));
            Thread.Sleep(60);
        }
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(260);
        Pump(window);
        SetTopmost(window, false);

        Check(Lines(true).Contains(140) && !Lines(true).Contains(160),
            $"真鼠标把线从 160 拖到 140（实际 [{string.Join(", ", Lines(true))}]）");
        Check(undoBtn.IsEnabled, "拖完能撤销");

        // ---- Delete 删掉选中的那条线 ----
        var keySource = PresentationSource.FromVisual(preview)!;
        var deleteArgs = new KeyEventArgs(Keyboard.PrimaryDevice, keySource, 0, Key.Delete)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        };
        bool handled = (bool)preview.GetType().GetMethod("HandleKey", Private)!
            .Invoke(preview, new object[] { deleteArgs })!;
        Pump(window);
        Check(handled && Lines(true).Length == 2,
            $"选中线按 Delete 删掉（实际 [{string.Join(", ", Lines(true))}]）");
        Check(total.Text.Contains("共 3 张"), "少一条线就少一张：" + total.Text);

        undoBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(Lines(true).Contains(140), "撤销把删掉的线找回来");
        Check(redoBtn.IsEnabled, "撤销后能重做");

        redoBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(Lines(true).Length == 2, "重做又删掉");

        undoBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        // ---- 切宫格：行列常驻，张数 / 每张尺寸收起 ----
        dirGrid.IsChecked = true;
        Pump(window);
        Check(rowsRow.Visibility == Visibility.Visible &&
              countRow.Visibility == Visibility.Collapsed &&
              sizeRow.Visibility == Visibility.Collapsed,
            "宫格只留行列，收起张数 / 每张尺寸");
        Check(total.Text.Contains("共 4 张"), "宫格 2×2 → 4 张：" + total.Text);
        Check(Lines(true).SequenceEqual(new[] { 160 }) && Lines(false).SequenceEqual(new[] { 100 }),
            "宫格两条轴各一条线");

        rowsBox.Text = "3";
        InvokeWith(window, "CommitDraft");
        Pump(window);
        Check(total.Text.Contains("共 6 张"), "改成 3 行 → 6 张：" + total.Text);
        Check(Lines(false).SequenceEqual(new[] { 67, 134 }),
            $"3 行的横线 67 / 134（实际 [{string.Join(", ", Lines(false))}]）");

        // ---- 重叠写进布局 ----
        overlapBox.Text = "10";
        InvokeWith(window, "CommitDraft");
        Pump(window);
        Check((int)Layout().GetType().GetProperty("Overlap")!.GetValue(Layout())! == 10,
            "重叠宽度写进布局");

        // ---- 输入错误：就地挂错误、保存置灰，改回来就恢复 ----
        rowsBox.Text = "0";
        InvokeWith(window, "CommitDraft");
        Pump(window);
        Check(rowsError.Visibility == Visibility.Visible, "行数 0 → 输入框下面挂错误：" + rowsError.Text);
        Check(!saveAll.IsEnabled, "有错时「全部保存」置灰");
        rowsBox.Text = "3";
        InvokeWith(window, "CommitDraft");
        Pump(window);
        Check(saveAll.IsEnabled && rowsError.Visibility == Visibility.Collapsed, "改回 3 行又能保存");

        // ---- 全部保存：真落盘（每格 = 格子 + 两边各 10 像素重叠）----
        string saveDir = Path.Combine(dir, "save");
        if (Directory.Exists(saveDir)) Directory.Delete(saveDir, true);
        Directory.CreateDirectory(saveDir);
        int expected = (int)(long)Layout().GetType().GetProperty("OutputCount")!.GetValue(Layout())!;
        InvokeWith(window, "StartExport", saveDir);
        bool saved = WaitFor(window, () => status.Text.Contains("已保存"), 30000);
        Pump(window);
        Check(saved, "保存全部跑完并报出结果：" + status.Text);
        CloseExplorerFolders("切图_");

        var folders = Directory.GetDirectories(saveDir);
        Check(folders.Length == 1 && Path.GetFileName(folders[0]).StartsWith("切图_"),
            "导出到「切图_<名称>」目录");
        var pngs = folders.Length == 1 ? Directory.GetFiles(folders[0], "*.png") : Array.Empty<string>();
        Check(pngs.Length == expected, $"落盘 {pngs.Length} 张（布局说 {expected} 张）");

        string? firstPath = pngs.FirstOrDefault(p => Path.GetFileName(p) == "1.png");
        if (firstPath != null)
        {
            var first = BitmapFrame.Create(new Uri(firstPath));
            Check(first.PixelWidth == 170 && first.PixelHeight == 77,
                $"第 1 张 = 列宽 160 + 重叠 10、行高 67 + 重叠 10（实际 {first.PixelWidth}×{first.PixelHeight}）");
            var corner = PixelAt(first, 0, 0);
            Check(corner.R == 0 && corner.G == 0, "第 1 张左上角取自原图 (0,0)");
        }
        else
        {
            Check(false, "导出目录里没有 1.png");
        }

        Pump(window);
        Save(RenderHi(window), Path.Combine(dir, "split-window.png"));

        // ---- 有图关窗：先问「放弃这些切图？」----
        bool confirmed = false;
        var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
            (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                if (dialog == null) return;
                confirmed = true;
                ((Button)Field(dialog, "OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, window.Dispatcher);
        closer.Start();
        window.Close();
        closer.Stop();
        Pump(window);
        Check(confirmed, "有图关窗会先问「放弃这些切图？」");
        Check(!window.IsVisible, "确认后窗口关掉");

        // ---- 会话复用：连点两次「切图…」只有一个空窗 ----
        var sessionType = asm.GetType("LTools.Features.CapturePro.Split.SplitSession")!;
        var openList = (System.Collections.IList)sessionType
            .GetField("Open", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Check(openList.Count == 0, "关掉的窗口从登记表里摘掉");

        sessionType.GetMethod("OpenEmpty", Stat)!.Invoke(null, null);
        sessionType.GetMethod("OpenEmpty", Stat)!.Invoke(null, null);
        Pump(window);
        Check(openList.Count == 1, $"托盘「切图…」连点两次只留一个空窗（实际 {openList.Count}）");

        foreach (var item in openList.Cast<object>().ToList()) ((Window)item).Close();
    }

    private static void PinManagerSection(BitmapSource image)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var storeType = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var managerType = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var windowType = asm.GetType("LTools.Views.CapturePro.PinManagerWindow")!;

        // 先清空，避免上一次冒烟留下的记录影响条数断言
        storeType.GetMethod("Clear")!.Invoke(null, null);

        var saveAssets = storeType.GetMethod("SaveAssets")!;

        object Make(string source, string group, int x)
        {
            object record = CallStatic(storeType, "Create",
                image.PixelWidth, image.PixelHeight, x, 150, image.PixelWidth, image.PixelHeight,
                1.0, source, group, false, "image", "", 0, null, false, 50, null)!;
            saveAssets.Invoke(null, new object[] { record, image });
            return record;
        }

        Make("screenshot", "默认", 200);
        Make("clipboard", "默认", 340);
        Make("screenshot", "默认", 480);

        var window = (Window)Activator.CreateInstance(windowType)!;
        window.Show();
        Pump(window);

        // 每页条数固定成 20 再断言（真机上 ui.json 里可能存过别的）
        window.GetType().GetField("_pageSize", Private)!.SetValue(window, 20);
        InvokeWith(window, "SelectPageSize", 20);
        InvokeWith(window, "Reload");
        Pump(window);

        var cards = (ItemsControl)Field(window, "CardList");
        var empty = (StackPanel)Field(window, "EmptyPanel");
        var pageInfo = (TextBlock)Field(window, "PageInfo");
        var selectionBar = (FrameworkElement)Field(window, "SelectionBar");
        var selectionText = (TextBlock)Field(window, "SelectionText");
        var batchBtn = (Button)Field(window, "BatchPinBtn");
        var deleteSelectedBtn = (Button)Field(window, "DeleteSelectedBtn");

        Check(cards.Items.Count == 3, $"管理页读出三条贴图（实际 {cards.Items.Count}）");
        Check(((TextBlock)Field(window, "CountText")).Text.Contains("3"), "标题旁显示总数");
        Check(empty.Visibility == Visibility.Collapsed, "有记录时不显示空态");
        Check(pageInfo.Text == "1-3 / 共 3 条", $"分页信息按当前页报（{pageInfo.Text}）");
        Check(selectionBar.Visibility == Visibility.Collapsed, "没选东西时不显示选择栏");

        // 卡片：缩略图 / 状态 / 副标题 / 来源徽标
        var firstRow = cards.Items[0];
        Check(firstRow.GetType().GetProperty("Thumb")!.GetValue(firstRow) != null, "卡片解码出缩略图");
        Check((string)firstRow.GetType().GetProperty("StatusText")!.GetValue(firstRow)! == "已关闭",
            "屏幕上的贴图已经不在 → 状态显示「已关闭」");
        Check(((string)firstRow.GetType().GetProperty("InfoText")!.GetValue(firstRow)!).Contains("分组："),
            "副标题写明分组与尺寸");
        Check((string)firstRow.GetType().GetProperty("ShowLabel")!.GetValue(firstRow)! == "贴回屏幕",
            "没开着的记录按钮是「贴回屏幕」");

        var sourcesSeen = new List<string>();
        foreach (var row in cards.Items)
            sourcesSeen.Add((string)row.GetType().GetProperty("SourceText")!.GetValue(row)!);
        Check(sourcesSeen.Contains("截图") && sourcesSeen.Contains("剪贴板"),
            "来源徽标按记录显示（截图 / 剪贴板）");

        Save(Render(window), Path.Combine(_outDir, "pin-manager.png"));

        // ---- 来源多选：只勾「剪贴板」→ 只剩一条 ----
        var filterSet = (HashSet<string>)Field(window, "_sources");
        filterSet.Add("clipboard");
        InvokeWith(window, "UpdateSourceButton");
        InvokeWith(window, "ApplyFilter", true);
        Pump(window);
        Check(cards.Items.Count == 1, $"按来源筛选后只剩一条（实际 {cards.Items.Count}）");
        Check(((Button)Field(window, "SourceFilterBtn")).Content.ToString()!.Contains("剪贴板"),
            "来源按钮写明当前筛选");
        Check((string)cards.Items[0].GetType().GetProperty("SourceText")!.GetValue(cards.Items[0])! == "剪贴板",
            "筛出来的就是剪贴板那条");

        // ---- 日期范围：今天之后 = 没有匹配（第二档空态）；清除日期回到上面那一条 ----
        ((DatePicker)Field(window, "StartDate")).SelectedDate = DateTime.Today.AddDays(1);
        Pump(window);
        Check(cards.Items.Count == 0 && empty.Visibility == Visibility.Visible,
            "日期筛空时显示空态");
        Check(((TextBlock)Field(window, "EmptyTitle")).Text == "没有匹配的贴图",
            "空态文案区分「还没有贴图」与「没有匹配的贴图」");
        InvokeWith(window, "ClearDate_Click", null, null);
        Pump(window);
        Check(cards.Items.Count == 1, "清除日期后回到来源筛选的结果");

        filterSet.Clear();
        InvokeWith(window, "UpdateSourceButton");
        InvokeWith(window, "ApplyFilter", true);
        Pump(window);
        Check(cards.Items.Count == 3, "清掉来源筛选恢复三条");

        // ---- 选择栏 + 批量贴回 ----
        InvokeWith(window, "SelectPage_Click", null, null);
        Pump(window);
        Check(selectionBar.Visibility == Visibility.Visible, "有选中项时选择栏出现");
        Check(selectionText.Text == "已选 3 项", $"选择栏报已选条数（{selectionText.Text}）");
        Check(batchBtn.IsEnabled, "同一分组的选中项可以批量贴回");
        Check(deleteSelectedBtn.IsEnabled, "选中后可以批量删除");

        InvokeWith(window, "BatchPin_Click", null, null);
        Pump(window);
        int open = (int)managerType.GetProperty("Count")!.GetValue(null)!;
        Check(open == 3, $"批量贴回后屏幕上有三张贴图（实际 {open}）");

        var pinned = (System.Collections.IList)storeType.GetMethod("Load")!.Invoke(null, null)!;
        Check(pinned.Count == 3, $"贴回不新建记录（仍是 {pinned.Count} 条）");
        int reopened = 0;
        foreach (var item in pinned)
            if ((bool)item.GetType().GetProperty("IsOpen")!.GetValue(item)!) reopened++;
        Check(reopened == 3, "贴回的记录回到「未关闭」");
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        Check((string)cards.Items[0].GetType().GetProperty("StatusText")!.GetValue(cards.Items[0])! == "未关闭",
            "贴回后卡片状态变成「未关闭」");

        // ---- 混组时批量贴回置灰（照上游 selectedGroupId()）----
        var firstPin = ((System.Collections.IEnumerable)managerType.GetProperty("All")!
            .GetValue(null)!).GetEnumerator();
        firstPin.MoveNext();
        managerType.GetMethod("MoveToGroup", BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic)!.Invoke(null, new[] { firstPin.Current, (object)"组B" });
        InvokeWith(window, "Reload");
        Pump(window);
        InvokeWith(window, "SelectPage_Click", null, null);
        Pump(window);
        Check(!batchBtn.IsEnabled, "混组的选中项不能批量贴回（按钮置灰）");
        Check(batchBtn.ToolTip.ToString()!.Contains("同一分组"), "置灰时说明原因");

        // ---- 删除选中：确认框点「删除」→ 记录 / 图片 / 屏幕上的贴图一起没 ----
        bool confirmed = false;
        var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
            (_, _) =>
            {
                var dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                if (dialog == null) return;
                confirmed = true;
                ((Button)Field(dialog, "OkBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, window.Dispatcher);
        closer.Start();
        InvokeWith(window, "DeleteSelected_Click", null, null);
        closer.Stop();
        Pump(window);

        Check(confirmed, "删除选中先弹确认框");
        Check(cards.Items.Count == 0, $"删完列表清空（实际 {cards.Items.Count}）");
        Check(((System.Collections.IList)storeType.GetMethod("Load")!.Invoke(null, null)!).Count == 0,
            "pins.json 里也没有了");
        Check(empty.Visibility == Visibility.Visible, "空态回来");
        Check(((TextBlock)Field(window, "EmptyTitle")).Text == "还没有贴图", "空态文案是「还没有贴图」");
        Check((int)managerType.GetProperty("Count")!.GetValue(null)! == 0, "屏幕上的贴图跟着关掉");
        Save(Render(window), Path.Combine(_outDir, "pin-manager-empty.png"));

        // ---- 分页：25 条，每页 20 → 切 10 → 3 页；每页条数落 ui.json ----
        for (int i = 0; i < 25; i++) Make("screenshot", "默认", 120 + i);
        InvokeWith(window, "Reload");
        Pump(window);
        Check(cards.Items.Count == 20, $"默认每页 20 条（实际 {cards.Items.Count}）");
        Check(pageInfo.Text == "1-20 / 共 25 条", $"第一页区间（{pageInfo.Text}）");

        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "pin-manager-config");
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        configPathSet.Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });

        var pageSizeBox = (ComboBox)Field(window, "PageSizeBox");
        try
        {
            pageSizeBox.SelectedIndex = 0;   // 10 条
            Pump(window);
        }
        finally
        {
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }

        Check(cards.Items.Count == 10, $"切到每页 10 条（实际 {cards.Items.Count}）");
        Check(pageInfo.Text == "1-10 / 共 25 条", $"分页信息跟着变（{pageInfo.Text}）");

        var uiStoreType = asm.GetType("LTools.Config.UiStore")!;
        object uiFile = uiStoreType.GetMethod("Read", BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic, null, new[] { typeof(string) }, null)!
            .Invoke(null, new object[] { Path.Combine(configRoot, "LTools.json") });
        Check((int)uiFile.GetType().GetProperty("PinPageSize")!.GetValue(uiFile)! == 10,
            "每页条数写进 ui.json 的 PinPageSize");

        InvokeWith(window, "NextPage_Click", null, null);
        Pump(window);
        Check(pageInfo.Text == "11-20 / 共 25 条", $"下一页（{pageInfo.Text}）");
        InvokeWith(window, "NextPage_Click", null, null);
        Pump(window);
        Check(pageInfo.Text == "21-25 / 共 25 条" && cards.Items.Count == 5,
            $"最后一页只剩 5 条（{pageInfo.Text}）");
        Check(!((Button)Field(window, "NextPageBtn")).IsEnabled, "最后一页「下一页」置灰");
        InvokeWith(window, "PrevPage_Click", null, null);
        Pump(window);
        Check(pageInfo.Text == "11-20 / 共 25 条", "上一页回去");

        // ---- 刷新 / 关闭 ----
        InvokeWith(window, "Refresh_Click", null, null);
        Pump(window);
        Check(cards.Items.Count == 10, "刷新后仍停在当前页");

        window.Close();
        Pump(window);

        storeType.GetMethod("Clear")!.Invoke(null, null);
    }

    // ============ 录屏 ============

    /// <summary>
    /// 只在测试里用的「点击计数窗」：盖住整屏、不抢焦点，用来验证区域框中间真的是点得过去的。
    /// </summary>
    private sealed class ClickProbe : Window
    {
        public int Clicks;

        public ClickProbe()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brushes.DimGray;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Content = new Grid();
            MouseLeftButtonDown += (_, e) => { Clicks++; e.Handled = true; };
            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
                SetWindowLongPtr(hwnd, -20, (IntPtr)(ex | 0x08000000 | 0x00000080));  // NOACTIVATE | TOOLWINDOW
            };
        }
    }

    /// <summary>
    /// 空闲区域框（照 Snow Shot 的 <c>screenrecordingareawindow</c>）：摆着等开录时就得看得见
    /// 要录哪一块、边缘能拖、右下角能挪，开录那一刻收掉。
    /// </summary>
    private static void RecordRegionBox()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
        var controllerType = asm.GetType("LTools.Features.CapturePro.RecordController")!;
        var gateType = asm.GetType("LTools.Features.CapturePro.RecordStartGate")!;
        var barType = asm.GetType("LTools.Views.CapturePro.CaptureRecordWindow")!;
        var borderType = asm.GetType("LTools.Views.CapturePro.RecordBorderWindow")!;

        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var area = new Int32Rect(100, 100, 400, 300);

        // 垫一块不抢焦点的实心窗：区域框中间必须点得过去（命中只看像素 alpha）
        var probe = new ClickProbe
        {
            Left = 0,
            Top = 0,
            Width = SystemParameters.PrimaryScreenWidth,
            Height = SystemParameters.PrimaryScreenHeight,
        };
        probe.Show();

        object plan = planType.GetMethod("FromValues")!.Invoke(null, new object[] { "all", "mp4", 30, 0, false, null })!;
        object controller = Activator.CreateInstance(controllerType)!;
        object gate = Activator.CreateInstance(gateType, AnyCtor | BindingFlags.NonPublic, null, null, null)!;
        var bar = (Window)Activator.CreateInstance(barType, AnyCtor, null,
            new object[] { area, plan, controller, gate }, null)!;
        bar.Show();
        Pump(bar);

        var region = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.GetType().Name == "RecordRegionWindow");
        Check(region != null, "面板摆着等开录时出现录屏区域框");
        if (region == null)
        {
            bar.Close();
            probe.Close();
            return;
        }

        var regionType = region.GetType();
        var meta = (TextBlock)Field(bar, "MetaText");

        Check(region.AllowsTransparency && region.Topmost && !region.ShowActivated,
            "区域框是分层置顶、不抢焦点的窗口");
        Check(region.WindowStyle == WindowStyle.None && region.ResizeMode == ResizeMode.NoResize,
            "区域框没有系统边框、也不能被系统拖动");
        Check((Int32Rect)regionType.GetProperty("Area", Private)!.GetValue(region)! == area,
            "区域框一上来就是面板那块选区");

        var idleColor = (Color)borderType.GetField("IdleColor")!.GetValue(null)!;
        var frames = FindAll<System.Windows.Shapes.Rectangle>(region)
            .Where(r => r.Fill is SolidColorBrush b && b.Color == idleColor).ToList();
        Check(frames.Count == 4, $"蓝框由四条边画出来（实际 {frames.Count}）");
        Check(frames.All(f => !f.IsHitTestVisible), "蓝框自己不挡鼠标（挡的是那圈透明的抓取带）");

        var handle = FindAll<Border>(region)
            .FirstOrDefault(b => b.ToolTip?.ToString() == "移动录制区域");
        Check(handle != null, "右下角有移动把手");
        if (handle != null)
        {
            Check(Math.Abs(handle.Width - 32) < 0.01 && Math.Abs(handle.Height - 32) < 0.01,
                $"把手是 32 逻辑像素（{handle.Width:0.##} × {handle.Height:0.##}）");
            Check(handle.Background is SolidColorBrush { Color.A: 115 }, "把手是半透明黑圆钮（alpha 115）");
            Check(handle.Cursor == Cursors.SizeAll, "把手光标是四向移动");
        }

        // ---- 中间点得过去：点选区正中落到底下那块窗口上，不被区域框吞掉 ----
        SetCursorPos(area.X + area.Width / 2, area.Y + area.Height / 2);
        Thread.Sleep(300);
        Pump(bar);
        Mouse(LeftDown);
        Thread.Sleep(100);
        Mouse(LeftUp);
        Thread.Sleep(250);
        Pump(bar);
        Check(probe.Clicks == 1, $"区域框中间点得过去（下面收到 {probe.Clicks} 次点击）");

        // 下面那几个点击都在区域框自己的边上：先把区域框重新顶到最前（点过底下那块之后 z 序会变）。
        Raise(region);

        // ---- 真鼠标：拖左边（往右收），再拖右边（往右放） ----
        int oldX = area.X;
        if (SetCursorPos(oldX + 3, area.Y + area.Height / 2))
        {
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftDown);
            Thread.Sleep(200);
            Pump(bar);
            for (int i = 1; i <= 5; i++)
            {
                SetCursorPos(oldX + 3 + i * 10, area.Y + area.Height / 2);
                Thread.Sleep(150);
                Pump(bar);
            }
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftUp);
            Thread.Sleep(250);
            Pump(bar);

            var moved = (Int32Rect)regionType.GetProperty("Area", Private)!.GetValue(region)!;
            Check(moved.X > area.X && moved.X + moved.Width == area.X + area.Width,
                $"真鼠标拖左边把区域收窄，右边不动（{moved.X},{moved.Y} {moved.Width}×{moved.Height}）");
            Check(meta.Text.Contains($"{moved.Width} × {moved.Height}"), $"面板摘要跟着变（{meta.Text}）");

            int right = moved.X + moved.Width;
            SetCursorPos(right - 2, moved.Y + moved.Height / 2);
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftDown);
            Thread.Sleep(200);
            Pump(bar);
            for (int i = 1; i <= 5; i++)
            {
                SetCursorPos(right - 2 + i * 10, moved.Y + moved.Height / 2);
                Thread.Sleep(150);
                Pump(bar);
            }
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftUp);
            Thread.Sleep(250);
            Pump(bar);

            var grown = (Int32Rect)regionType.GetProperty("Area", Private)!.GetValue(region)!;
            Check(grown.Width > moved.Width && grown.X == moved.X,
                $"真鼠标拖右边把区域放宽，左边不动（{grown.Width}）");

            // ---- 拖右下角把手：整块平移，尺寸不变 ----
            const double side = 32;
            const double inset = 8;
            int gripX = grown.X + grown.Width - (int)(side + inset) + 16;
            int gripY = grown.Y + grown.Height - (int)(side + inset) + 16;
            SetCursorPos(gripX, gripY);
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftDown);
            Thread.Sleep(200);
            Pump(bar);
            for (int i = 1; i <= 5; i++)
            {
                SetCursorPos(gripX - i * 8, gripY - i * 4);
                Thread.Sleep(150);
                Pump(bar);
            }
            Thread.Sleep(300);
            Pump(bar);
            Pump(bar);
            Mouse(LeftUp);
            Thread.Sleep(250);
            Pump(bar);

            var shifted = (Int32Rect)regionType.GetProperty("Area", Private)!.GetValue(region)!;
            Check(shifted.Width == grown.Width && shifted.Height == grown.Height && shifted.X < grown.X,
                $"拖把手整块挪，尺寸不变（{shifted.X},{shifted.Y} {shifted.Width}×{shifted.Height}）");

            meta = (TextBlock)Field(bar, "MetaText");
            Check(meta.Text.Contains($"{shifted.Width} × {shifted.Height}"), $"摘要写的是新尺寸（{meta.Text}）");
            Check(probe.Clicks == 1, "拖边缘的点击不会漏给下面的窗口");

            Save(RenderHi(region), Path.Combine(_outDir, "record-region.png"));
        }

        // ---- 点「录制」：区域定稿、框收掉、会话随后读到的就是这块 ----
        var final = (Int32Rect)regionType.GetProperty("Area", Private)!.GetValue(region)!;
        ((Button)Field(bar, "RecordBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);

        Check((bool)gateType.GetProperty("Started")!.GetValue(gate)!, "点录制放行会话");
        Check(!Application.Current.Windows.OfType<Window>()
                .Any(w => w.GetType().Name == "RecordRegionWindow"),
            "开录那一刻区域框收掉（后面交给录制期红框）");
        Check((Int32Rect)barType.GetProperty("Area", Private)!.GetValue(bar)! == final,
            "会话读到的就是用户拖出来的最后一块");

        barType.GetMethod("CloseQuietly", AnyCtor)!.Invoke(bar, null);
        Pump(bar);
        probe.Close();
        Pump(bar);

        static void Mouse(uint flags) => mouse_event(flags, 0, 0, 0, UIntPtr.Zero);

        // 把区域框顶到最前（跟它自己的 RaiseAbove 一个做法：不改大小 / 位置 / 焦点）。
        static void Raise(Window window) => SetWindowPos(
            new System.Windows.Interop.WindowInteropHelper(window).Handle, new IntPtr(-1),
            0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE | NOMOVE | NOACTIVATE
    }

    /// <summary>
    /// 录屏：控制条（计时 / 暂停 / 完成）+ 真抓帧编成 mp4 + 采集历史里的录屏条目。
    /// 抓帧源换成合成画面（不录用户桌面）；没装 ffmpeg 时只验控制条与历史，编码断言跳过。
    /// </summary>
    private static void Record()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
        var controllerType = asm.GetType("LTools.Features.CapturePro.RecordController")!;
        var engineType = asm.GetType("LTools.Features.CapturePro.RecordEngine")!;
        var barType = asm.GetType("LTools.Views.CapturePro.CaptureRecordWindow")!;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;

        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var area = new Int32Rect(0, 0, 320, 240);
        object plan = planType.GetMethod("FromValues")!.Invoke(null, new object[] { "all", "mp4", 30, 0, false, null })!;

        // ① 控制条：两行布局（照 Snow Shot 的导出设置）、规格 / 计时 / 状态色 / 工具与设置的互斥
        var gateType = asm.GetType("LTools.Features.CapturePro.RecordStartGate")!;
        object barController = Activator.CreateInstance(controllerType)!;

        // gate != null = 「面板先摆着、等用户点那个红圈」，所以刚打开是 Idle（Snow Shot 的录屏入口就是这样）
        object gate = Activator.CreateInstance(gateType, AnyCtor | BindingFlags.NonPublic, null, null, null)!;
        var bar = (Window)Activator.CreateInstance(barType, AnyCtor, null,
            new object[] { area, plan, barController, gate }, null)!;
        bar.Show();
        Pump(bar);

        var clock = (TextBlock)Field(bar, "ClockLabel");
        Check(clock.Text == "00:00:00", $"刚打开时计时是零（HH:MM:SS）：{clock.Text}");

        var meta = (TextBlock)Field(bar, "MetaText");
        Check(meta.Text.Contains("320 × 240") && meta.Text.Contains("30 fps"),
            $"控制条写明规格：{meta.Text}");

        // 第二行（导出设置）默认展开，右边缘对齐第一行（上游 Qt::AlignRight）
        var styleBar = (FrameworkElement)Field(bar, "StyleBar");
        var actionBar = (FrameworkElement)Field(bar, "ActionBar");
        Check(styleBar.Visibility == Visibility.Visible, "导出设置那一行默认展开");
        Check(styleBar.ActualWidth > 0 && styleBar.ActualWidth < actionBar.ActualWidth,
            $"第二行比第一行窄（{styleBar.ActualWidth:0} / {actionBar.ActualWidth:0}）");

        double styleRight = styleBar.TransformToAncestor(bar)
            .Transform(new Point(styleBar.ActualWidth, 0)).X;
        double actionRight = actionBar.TransformToAncestor(bar)
            .Transform(new Point(actionBar.ActualWidth, 0)).X;
        Check(Math.Abs(styleRight - actionRight) <= 1,
            $"第二行右边缘贴第一行（{styleRight:0.##} / {actionRight:0.##}）");

        // 第二行的度量照上游：格式 76 / 延迟 64 / 按钮 28
        Check(Math.Abs(((FrameworkElement)Field(bar, "_formatPill")).Width - 76) < 0.5,
            "格式下拉宽 76");
        Check(Math.Abs(((FrameworkElement)Field(bar, "_delayPill")).Width - 64) < 0.5,
            "延迟胶囊宽 64");

        // 绘制工具：10 个、展开导出设置时一个都不选（上游 clearActiveToolState）
        var tools = ((System.Collections.IEnumerable)Field(bar, "_toolButtons")).Cast<RadioButton>().ToList();
        Check(tools.Count == 10, $"绘制工具 10 个（实际 {tools.Count}）");
        Check(tools.All(b => b.IsChecked != true), "展开导出设置时不选中任何工具");

        // 两组色卡各自独立选中：默认激光 / 点击都是「透明」，两边各有一个勾上的预设
        var styleRowNow = (StackPanel)Field(bar, "StyleRow");
        int checkedSwatches = styleRowNow.Children.OfType<RadioButton>().Count(r => r.IsChecked == true);
        Check(checkedSwatches == 2, $"两组「透明」预设各自选中（实际勾上 {checkedSwatches} 个）");

        // 选工具 → 收起导出设置；点开关 → 重新展开并清掉工具选中
        tools[1].IsChecked = true;
        Pump(bar);
        Check(styleBar.Visibility != Visibility.Visible, "选了绘制工具就收起导出设置");

        var exportToggle = (ToggleButton)Field(bar, "_exportToggle");
        exportToggle.IsChecked = true;
        Pump(bar);
        Check(styleBar.Visibility == Visibility.Visible, "点导出设置开关又展开");
        Check(tools.All(b => b.IsChecked != true), "展开导出设置时清掉工具选中");

        // ---- 空闲态（上游 Idle）：开始键蓝、暂停禁用且灰、没有录屏时收尾三键禁用 ----
        var recordBtn = (Button)Field(bar, "RecordBtn");
        var pauseBtn = (Button)Field(bar, "PauseBtn");
        Check(pauseBtn.Content is System.Windows.Shapes.Path, "暂停按钮只画图标（与上游一致）");

        Check(HasIcon(bar, recordBtn, "IconRecStart"), "空闲时开始键画实心圆");
        Check(ReferenceEquals(recordBtn.Foreground, bar.FindResource("CapturePrimary")),
            "空闲时开始键是 primary 蓝");
        Check(!pauseBtn.IsEnabled, "空闲时暂停不可点");
        Check(ReferenceEquals(pauseBtn.Foreground, bar.FindResource("CaptureTextDisabled")),
            "空闲时暂停是禁用灰");

        foreach (string name in new[] { "_trimBtn", "_saveBtn", "_copyBtn" })
        {
            var button = (Button)Field(bar, name);
            bool hasLast = (bool)Field(bar, "_hasLastRecord");
            Check(button.IsEnabled == hasLast,
                hasLast ? $"{name} 在历史里已有录屏时可用" : $"空闲且没有录屏时 {name} 不可点");
        }
        var copyBtn = (Button)Field(bar, "_copyBtn");
        if (!(bool)Field(bar, "_hasLastRecord"))
            Check(ReferenceEquals(copyBtn.Foreground, bar.FindResource("CaptureTextDisabled")),
                "空闲时复制键是禁用灰");
        Check(styleBar.IsEnabled, "空闲时导出设置整行可编辑");

        // ============ 第二行的真实交互（每个控件都要真写回 plan） ============

        object Options() => planType.GetProperty("Options")!.GetValue(plan)!;
        object Option(string name) => Options().GetType().GetProperty(name)!.GetValue(Options())!;

        // ---- 格式下拉：4 项、当前项打勾、切 GIF 联动（胶囊 / 摘要 / 声音禁用） ----
        var formatPill = (Button)Field(bar, "_formatPill");
        formatPill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);
        var formatMenu = (ContextMenu?)Field(bar, "_formatMenu");
        Check(formatMenu is { IsOpen: true }, "点格式胶囊真的弹出菜单");
        var formatItems = formatMenu?.Items.Cast<MenuItem>().ToList() ?? new List<MenuItem>();
        Check(formatItems.Count == 4, $"格式是 4 项（实际 {formatItems.Count}）");
        Check(string.Join(" / ", formatItems.Select(i => i.Header?.ToString())) == "MP4 / GIF / APNG / WebP",
            "格式项是 MP4 / GIF / APNG / WebP");
        Check(formatItems.Single(i => (string)i.Header! == "MP4").IsChecked, "当前格式（MP4）打了勾");

        ClickMenuItem(formatMenu!, "GIF");
        Pump(bar);
        Check(planType.GetProperty("Format")!.GetValue(plan)!.ToString() == "Gif", "选 GIF 写回 plan.Format");
        Check(((TextBlock)Field(bar, "_formatText")).Text == "GIF", "胶囊文字跟着变 GIF");
        Check(meta.Text.Contains("GIF"), $"摘要跟着变：{meta.Text}");
        Check(!((ToggleButton)Field(bar, "_micBtn")).IsEnabled
            && !((ToggleButton)Field(bar, "_speakerBtn")).IsEnabled,
            "动图格式下麦克风 / 扬声器禁用（不带音轨）");

        formatPill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);
        ClickMenuItem((ContextMenu)Field(bar, "_formatMenu")!, "MP4");
        Pump(bar);
        Check(planType.GetProperty("Format")!.GetValue(plan)!.ToString() == "Mp4", "再选 MP4 切回来");

        // ---- 延迟胶囊：滚轮 ±1 秒（0–10），点一下归零 ----
        var delayPill = (Button)Field(bar, "_delayPill");
        var delayText = (TextBlock)Field(bar, "_delayText");
        Check(delayText.Text == "0s", $"延迟默认 0 秒（{delayText.Text}）");
        Wheel(delayPill, 120);
        Pump(bar);
        Check(delayText.Text == "1s" && (int)planType.GetProperty("Countdown")!.GetValue(plan)! == 1,
            $"滚轮向上 +1 秒（{delayText.Text}）");
        Wheel(delayPill, -120);
        Pump(bar);
        Check(delayText.Text == "0s", $"滚轮向下回到 0，不会变负数（{delayText.Text}）");
        for (int i = 0; i < 14; i++) Wheel(delayPill, 120);
        Pump(bar);
        Check(delayText.Text == "10s", $"延迟最多 10 秒（{delayText.Text}）");
        delayPill.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);
        Check(delayText.Text == "0s", "点一下延迟归零");

        // ---- 麦克风 / 扬声器：写回开关、换图标、开是 success 绿 ----
        var micBtn = (ToggleButton)Field(bar, "_micBtn");
        var speakerBtn = (ToggleButton)Field(bar, "_speakerBtn");
        Check(micBtn.IsChecked != true && HasIcon(bar, micBtn, "IconRecMicOff"), "麦克风默认关（画的是关图标）");
        micBtn.IsChecked = true;
        Pump(bar);
        Check((bool)Option("Microphone"), "点麦克风写回 Microphone = true");
        Check(HasIcon(bar, micBtn, "IconRecMic"), "打开后换成开图标");
        Check(ReferenceEquals(micBtn.Foreground, bar.FindResource("CaptureSuccess")), "打开后是 success 绿");
        micBtn.IsChecked = false;
        Pump(bar);
        Check(!(bool)Option("Microphone"), "再点一下关掉麦克风");

        // 系统声音默认开（Snow Shot `enable_system_audio` 默认 true）
        Check((bool)Option("SystemAudio"), "系统声音默认开");
        Check(HasIcon(bar, speakerBtn, "IconRecAudio"), "扬声器默认画的是开图标");
        speakerBtn.IsChecked = false;
        Pump(bar);
        Check(!(bool)Option("SystemAudio"), "点扬声器关掉系统声音");
        Check(HasIcon(bar, speakerBtn, "IconRecAudioOff"), "关掉后换成关图标");

        // ---- 声音增益弹层（照上游 RecordingAudioGainPopover：180 宽 / −24…+24 / 悬停 150ms / 电平条）----
        Check((int)Option("MicrophoneGainDb") == 0 && (int)Option("SystemAudioGainDb") == 0,
            "两路增益默认 0 dB");
        var speakerGainPop = (Popup?)Field(Field(bar, "_speakerGainPop"), "_popup");
        Check(speakerGainPop is { IsOpen: false }, "增益弹层默认不显示");

        speakerBtn.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.MouseEnterEvent });
        Pump(bar);
        Check(speakerGainPop is { IsOpen: false }, "悬停还没到 150ms，增益弹层先不开");
        Wait(bar, 280);
        Check(speakerGainPop is { IsOpen: true }, "悬停 150ms 后弹出增益弹层");

        var gainPanel = speakerGainPop!.Child is Border gainCard ? gainCard.Child as StackPanel : null;
        Check(gainPanel != null && Math.Abs(gainPanel.Width - 180) < 0.5,
            $"增益弹层宽 180（实际 {gainPanel?.Width:0}）");
        var gainSlider = gainPanel!.Children.OfType<Slider>().SingleOrDefault();
        Check(gainSlider != null && gainSlider.Minimum == -24 && gainSlider.Maximum == 24
            && Math.Abs(gainSlider.TickFrequency - 1) < 0.001,
            "增益滑块是 −24…+24、步进 1");
        var gainMeter = gainPanel.Children.OfType<Grid>().SingleOrDefault();
        Check(gainMeter != null && Math.Abs(gainMeter.Height - 4) < 0.01, "弹层里有 4px 高的电平条");
        Check(gainPanel.Children.OfType<DockPanel>().SingleOrDefault() != null, "弹层顶部写明这一路是哪路声音");

        gainSlider!.Value = -6;
        Pump(bar);
        Check((int)Option("SystemAudioGainDb") == -6, "拖滑块写回 SystemAudioGainDb");
        Check((gainSlider.ToolTip?.ToString() ?? "") == "-6 dB", "滑块 tooltip 跟着写 -6 dB");
        Check(((TextBlock)Field(bar, "_speakerGainText")).Text == "-6 dB", "弹层右上角显示 -6 dB");

        // 复位：增益回 0、系统声音切回开（录制中要验证「已开的一路仍能开弹层」）
        gainSlider.Value = 0;
        Pump(bar);
        Check((int)Option("SystemAudioGainDb") == 0, "增益可以调回 0 dB");
        speakerGainPop.IsOpen = false;
        speakerBtn.IsChecked = true;
        Pump(bar);
        Check((bool)Option("SystemAudio"), "系统声音重新打开");

        // ---- 色卡：点预设写回颜色，两组互不影响 ----
        var swatchButtons = styleRowNow.Children.OfType<RadioButton>().ToList();
        var laserSwatches = swatchButtons.Where(s => s.GroupName == "recLaser").ToList();
        var clickSwatches = swatchButtons.Where(s => s.GroupName == "recClick").ToList();
        Check(laserSwatches.Count == 5 && clickSwatches.Count == 5, "两组各 5 个预设色卡");
        Check(laserSwatches[0].IsChecked == true && clickSwatches[0].IsChecked == true,
            "两组默认都选「透明」");

        laserSwatches[1].IsChecked = true;
        Pump(bar);
        string laser = (string)Option("LaserColor")!;
        Check(laser == "#FFF5222D", $"点红色预设写回 LaserColor（{laser}）");
        Check(clickSwatches[0].IsChecked == true, "选激光笔颜色不影响鼠标点击那组");
        var triggerBrush = (SolidColorBrush)((Button)Field(bar, "_laserTrigger")).Background;
        Check(triggerBrush.Color == ((SolidColorBrush)laserSwatches[1].Background).Color,
            "当前色触发器跟着显示刚选的颜色");

        clickSwatches[2].IsChecked = true;
        Pump(bar);
        string click = (string)Option("ClickColor")!;
        Check(click == "#8052C41A", $"点绿色预设写回 ClickColor，alpha 仍是 128（{click}）");

        // ---- 悬停弹层：光标按钮弹出三个勾选项，勾上写回 plan ----
        var cursorBtn = (ToggleButton)Field(bar, "_cursorBtn");
        cursorBtn.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.MouseEnterEvent });
        Pump(bar);
        var cursorPopup = PopupOf(bar, cursorBtn);
        Check(cursorPopup is { IsOpen: true }, "光标按钮悬停弹出选项");
        var popupBoxes = cursorPopup?.Child is Border popCard && popCard.Child is Panel popStack
            ? popStack.Children.OfType<CheckBox>().ToList()
            : new List<CheckBox>();
        Check(popupBoxes.Count == 3
            && string.Join("/", popupBoxes.Select(b => b.Content?.ToString())) == "鼠标高亮/记录鼠标点击/记录鼠标滚动",
            "弹层里三个勾选：鼠标高亮 / 记录鼠标点击 / 记录鼠标滚动");
        popupBoxes.FirstOrDefault()!.IsChecked = true;
        Pump(bar);
        Check((bool)Option("MouseHighlight"), "勾「鼠标高亮」写回 MouseHighlight");
        cursorPopup!.IsOpen = false;

        // ---- 取色器：点当前颜色开弹层，打字（不透明度 + 十六进制）写回 ----
        var laserTrigger = (Button)Field(bar, "_laserTrigger");
        laserTrigger.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);
        var picker = (Popup?)Field(bar, "_pickerPopup");
        Check(picker is { IsOpen: true }, "点当前颜色开取色弹层");
        if (picker?.Child is Border pickCard)
        {
            var inputs = FindAll<TextBox>(pickCard);
            Check(inputs.Count == 2, $"取色弹层里有颜色 / 不透明度两个输入框（实际 {inputs.Count}）");
            if (inputs.Count == 2)
            {
                inputs[1].Text = "255";
                inputs[0].Text = "00FF00";
                Pump(bar);
                string typed = (string)Option("LaserColor")!;
                Check(typed == "#FF00FF00", $"打字把颜色写回 LaserColor（{typed}）");
                Check(laserSwatches.All(s => s.IsChecked != true), "自定义颜色不会落在预设上");
            }
        }
        picker!.IsOpen = false;

        // ---- 「特效设置」真的打开选项窗口（关闭后回到面板） ----
        bool optionsOpened = false;
        var closer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) =>
            {
                var window = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.GetType().Name == "RecordOptionsWindow");
                if (window == null) return;
                optionsOpened = true;
                window.Close();
            }, bar.Dispatcher);
        closer.Start();
        ((Button)Field(bar, "_effectBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        closer.Stop();
        Pump(bar);
        Check(optionsOpened, "点「特效设置」打开录屏选项窗口");

        // 复原默认（后面的截图和断言都按上游默认态：两组透明、没有鼠标高亮）
        laserSwatches[0].IsChecked = true;
        clickSwatches[0].IsChecked = true;
        ((CheckBox)Field(bar, "_highlightBox")).IsChecked = false;
        Pump(bar);


        // 空闲态截图（两行 + 色卡 + 工具，用来跟 Snow Shot 截图逐像素对账）
        Save(RenderHi(bar), Path.Combine(_outDir, "record-bar.png"));

        // ---- 录制中（上游 updateRecordingControls）：开始→停止、暂停可用、第二行整行禁用 ----
        controllerType.GetMethod("Start")!.Invoke(barController, null);
        barType.GetMethod("BeginRecording", AnyCtor)!.Invoke(bar, null);
        Pump(bar);

        Check(HasIcon(bar, recordBtn, "IconRecStop"), "录制中开始键换成停止方块");
        Check(ReferenceEquals(recordBtn.Foreground, bar.FindResource("CaptureDanger")),
            "录制中停止键是 danger 红");
        Check(pauseBtn.IsEnabled, "录制中暂停可点");
        Check(HasIcon(bar, pauseBtn, "IconRecPause"), "录制中暂停键画暂停图标");
        Check(ReferenceEquals(pauseBtn.Foreground, bar.FindResource("CaptureWarning")),
            "录制中暂停是警示黄");
        Check(!styleBar.IsEnabled, "录制中导出设置整行禁用");
        Check(styleBar.Visibility == Visibility.Visible, "录制中仍然显示导出设置");
        Check(tools.All(b => b.IsChecked != true), "录制中不选中任何绘制工具");

        // ---- 录制中的声音按钮：已开的一路仍能点开增益弹层（不切开关），没开的一路禁用 ----
        Check(speakerBtn.IsEnabled, "录制中已开的系统声音仍可点（打开增益弹层）");
        Check(!micBtn.IsEnabled, "录制中没开的麦克风禁用（不能中途打开）");
        speakerBtn.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        Pump(bar);
        Check(speakerGainPop.IsOpen, "录制中点一下扬声器打开增益弹层");
        Check((bool)Option("SystemAudio") && speakerBtn.IsChecked == true,
            "录制中点扬声器不会把声音关掉");
        speakerGainPop.IsOpen = false;

        Save(RenderHi(bar), Path.Combine(_outDir, "record-bar-live.png"));

        // ---- 暂停（上游 Paused）：换「继续」、回到 primary 蓝，计时冻住 ----
        pauseBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(bar);
        Check(HasIcon(bar, pauseBtn, "IconRecResume"), "暂停后图标换成「继续」");
        Check(ReferenceEquals(pauseBtn.Foreground, bar.FindResource("CapturePrimary")),
            "暂停后「继续」是 primary 蓝");
        Check((pauseBtn.ToolTip?.ToString() ?? "").Contains("继续"), "点暂停后提示变「继续录制」");
        Check(((TextBlock)Field(bar, "StatusText")).Text.Contains("暂停"), "状态行说明已暂停");

        long frozen = (long)controllerType.GetProperty("ElapsedMs")!.GetValue(barController)!;
        Thread.Sleep(150);
        long later = (long)controllerType.GetProperty("ElapsedMs")!.GetValue(barController)!;
        Check(later - frozen < 40, $"暂停期间计时不动（{frozen} → {later} ms）");

        Save(RenderHi(bar), Path.Combine(_outDir, "record-bar-paused.png"));

        ((Button)Field(bar, "CancelBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check((bool)controllerType.GetProperty("Cancelled")!.GetValue(barController)!, "点取消标记为取消");
        barType.GetMethod("CloseQuietly", AnyCtor)!.Invoke(bar, null);
        Pump(bar);

        // ② 真抓帧编码（ffmpeg 不在就跳过这一段）
        string? ffmpeg = (string?)planType.GetMethod("FindFfmpeg")!.Invoke(null, null);
        if (ffmpeg == null)
        {
            Console.WriteLine("  · 没找到 ffmpeg，跳过编码断言（发布包里会随包带上）");
            CheckRecordHistory(catalogType);
            return;
        }

        Console.WriteLine("  · 用 ffmpeg：" + ffmpeg);
        string outFile = Path.Combine(_outDir, "record-smoke.mp4");
        if (File.Exists(outFile)) File.Delete(outFile);

        object controller = Activator.CreateInstance(controllerType)!;
        controllerType.GetMethod("Start")!.Invoke(controller, null);
        object engine = Activator.CreateInstance(engineType, AnyCtor, null,
            new object[] { ffmpeg, area, plan, outFile, controller, "libx264", null }, null)!;

        // 抓帧源换合成画面：冒烟不该真录用户桌面
        engineType.GetField("FrameSource", Private)!.SetValue(engine,
            new Func<Int32Rect, System.Drawing.Bitmap>(MakeFrame));

        var stopper = new Thread(() =>
        {
            Thread.Sleep(800);
            controllerType.GetMethod("Stop")!.Invoke(controller, null);
        }) { IsBackground = true };
        stopper.Start();

        bool ok = (bool)engineType.GetMethod("Run")!.Invoke(engine, null)!;
        stopper.Join();

        int frames = (int)engineType.GetProperty("Frames")!.GetValue(engine)!;
        object? error = engineType.GetProperty("Error")!.GetValue(engine);
        Check(ok, $"编码跑完（错误：{error?.ToString() ?? "无"}）");
        Check(frames >= 10, $"录到足够帧（{frames}）");
        Check(File.Exists(outFile) && new FileInfo(outFile).Length > 0,
            "mp4 落盘：" + (File.Exists(outFile) ? new FileInfo(outFile).Length + " 字节" : "没有文件"));
        Check(engineType.GetProperty("Poster")!.GetValue(engine) != null, "抓到封面帧");

        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ffmpeg,
            $"-v error -i \"{outFile}\" -f null -")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        })!;
        string stderr = probe.StandardError.ReadToEnd();
        probe.WaitForExit(20000);
        Check(probe.ExitCode == 0 && stderr.Trim().Length == 0,
            "成品能被 ffmpeg 完整解码：" + (stderr.Trim().Length == 0 ? "无告警" : stderr.Trim()));

        // ---- 声音增益端到端：FinalizeArgs 套 +6 dB，成品真的响 6 dB（ffmpeg volume 滤镜真跑一遍）----
        var finalize = planType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(m => m.Name == "FinalizeArgs" && m.GetParameters().Length == 4);

        string tone = Path.Combine(_outDir, "gain-tone.wav");
        string tone2 = Path.Combine(_outDir, "gain-tone2.wav");
        string flatFile = Path.Combine(_outDir, "gain-flat.mp4");
        string loudFile = Path.Combine(_outDir, "gain-plus6.mp4");
        string mixFlat = Path.Combine(_outDir, "gain-mix-flat.mp4");
        string mixLoud = Path.Combine(_outDir, "gain-mix-plus6.mp4");

        Check(RunFfmpeg(ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "sine=frequency=440:sample_rate=48000:duration=1", "-af", "volume=0.25", tone,
        }) == 0, "生成测试音（440Hz / 0.25 幅度）");
        Check(RunFfmpeg(ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "sine=frequency=1000:sample_rate=44100:duration=1", "-af", "volume=0.25", tone2,
        }) == 0, "生成第二路测试音（1000Hz / 44.1kHz）");

        List<string> ArgsOf(object result) => ((System.Collections.IEnumerable)result).Cast<string>().ToList();

        Check(RunFfmpeg(ffmpeg, ArgsOf(finalize.Invoke(null,
                new object[] { outFile, flatFile, new[] { tone }, new[] { 0 } })!)) == 0,
            "单轨 0 dB 收尾跑通");
        Check(RunFfmpeg(ffmpeg, ArgsOf(finalize.Invoke(null,
                new object[] { outFile, loudFile, new[] { tone }, new[] { 6 } })!)) == 0,
            "单轨 +6 dB 收尾跑通");
        double flatDb = MeanVolume(ffmpeg, flatFile);
        double loudDb = MeanVolume(ffmpeg, loudFile);
        Check(Math.Abs((loudDb - flatDb) - 6) <= 0.3,
            $"+6 dB 成品真的响 6 dB（{flatDb:0.0} → {loudDb:0.0}）");

        Check(RunFfmpeg(ffmpeg, ArgsOf(finalize.Invoke(null,
                new object[] { outFile, mixFlat, new[] { tone, tone2 }, new[] { 0, 0 } })!)) == 0,
            "双轨 0 dB 混音跑通");
        Check(RunFfmpeg(ffmpeg, ArgsOf(finalize.Invoke(null,
                new object[] { outFile, mixLoud, new[] { tone, tone2 }, new[] { 6, 0 } })!)) == 0,
            "双轨按轨 +6 dB 混音跑通（每路各挂一次 volume）");
        double mixFlatDb = MeanVolume(ffmpeg, mixFlat);
        double mixLoudDb = MeanVolume(ffmpeg, mixLoud);
        Check(mixLoudDb - mixFlatDb > 2.5 && mixLoudDb - mixFlatDb < 5,
            $"双轨只把第二路推 6 dB，混音整体抬高 3–4 dB（{mixFlatDb:0.0} → {mixLoudDb:0.0}）");

        // ③ 采集历史：录屏条目 + 页签 + 播放按钮 + 删除连带删视频
        var poster = BuildShot(320, 240);
        object? entry = catalogType.GetMethod("CreateRecord")!.Invoke(null,
            new object[] { 320, 240, "录屏", "mp4", 800, outFile, poster });

        Check(entry != null, "录屏条目落盘");
        if (entry == null)
        {
            CheckRecordHistory(catalogType);
            return;
        }

        var entryType = entry.GetType();
        string id = (string)entryType.GetProperty("Id")!.GetValue(entry)!;
        string moved = (string)catalogType.GetMethod("FilePath")!.Invoke(null, new[] { entry })!;
        Check(File.Exists(moved), "录制文件搬进 captures/records：" + moved);
        Check(!File.Exists(outFile), "临时文件已经搬走");
        Check((string)catalogType.GetMethod("SummaryOf")!.Invoke(null, new[] { entry })! == "MP4 · 录屏",
            "摘要写「格式 · 完成动作」");

        var windowType = asm.GetType("LTools.Views.CapturePro.CaptureHistoryWindow")!;
        var window = (Window)Activator.CreateInstance(windowType)!;
        window.Show();
        Pump(window);

        var cards = (ItemsControl)Field(window, "CardList");
        Check(cards.Items.Count == 1, $"历史里有一条录屏（实际 {cards.Items.Count}）");

        var cardRow = cards.Items[0];
        Check((bool)cardRow.GetType().GetProperty("CanPlay")!.GetValue(cardRow)!,
            "录屏条目给「播放」按钮");
        Check(!(bool)cardRow.GetType().GetProperty("CanCopy")!.GetValue(cardRow)!,
            "录屏不显示「复制图片」");

        string summary = (string)cardRow.GetType().GetProperty("SummaryText")!.GetValue(cardRow)!;
        Check(summary.Contains("MP4") && summary.Contains("00:"),
            $"卡片摘要写清格式与时长：{summary}");

        Save(Render(window), Path.Combine(_outDir, "history-record.png"));

        // ④ 录屏选项 / 裁剪倍速两个新窗口的结构与联动
        RecordPanels(asm, entry);

        catalogType.GetMethod("Remove")!.Invoke(null, new object[] { id });
        Pump(window);
        Check(!File.Exists(moved), "删记录连带删掉视频文件");
        Check(cards.Items.Count == 0, "删完列表为空");

        window.Close();
        catalogType.GetMethod("Clear")!.Invoke(null, null);
    }

    /// <summary>
    /// 两个记录类新入口的结构与联动：
    /// ① 「录屏选项」：摄像头 / 麦克风 / 光标高亮的开关要能立刻解锁它下面的子控件（不用先去动摄像头）；
    /// ② 「裁剪 / 倍速」：源时间轴时间码、9 档倍速、抓手拖动只改起止不改倍速、成品时长按时长换算。
    /// </summary>
    private static void RecordPanels(Assembly asm, object entry)
    {
        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // ---- ① 录屏选项 ----
        var optionsType = asm.GetType("LTools.Features.CapturePro.RecordOptions")!;
        object options = Activator.CreateInstance(optionsType)!;
        var optType = asm.GetType("LTools.Views.CapturePro.RecordOptionsWindow")!;
        var opt = (Window)Activator.CreateInstance(optType, AnyCtor, null, new[] { options }, null)!;
        opt.Show();
        Pump(opt);

        var cameraBox = (ComboBox)Field(opt, "CameraBox");
        var micDevice = (ComboBox)Field(opt, "MicDeviceBox");
        var cameraSize = (Slider)Field(opt, "CameraSizeSlider");
        var radius = (Slider)Field(opt, "RadiusSlider");

        Check(cameraBox.Items.Count == 5, $"摄像头有「不录」+ 4 个序号（实际 {cameraBox.Items.Count}）");
        Check(cameraBox.SelectedIndex == 0, "默认不录摄像头");
        Check(!cameraSize.IsEnabled && !radius.IsEnabled, "默认：画中画尺寸 / 高亮半径都不可用");

        cameraBox.SelectedIndex = 1;
        Pump(opt);
        Check(cameraSize.IsEnabled, "选了摄像头就解锁边长滑块");

        ((CheckBox)Field(opt, "MicBox")).IsChecked = true;
        Pump(opt);
        Check(micDevice.IsEnabled, "勾上麦克风立刻解锁设备下拉（不用先去动摄像头）");

        ((CheckBox)Field(opt, "MicBox")).IsChecked = false;
        Pump(opt);
        Check(!micDevice.IsEnabled, "取消麦克风又把它锁回去");

        ((CheckBox)Field(opt, "HighlightBox")).IsChecked = true;
        Pump(opt);
        Check(radius.IsEnabled, "勾上光标高亮立刻解锁半径滑块");

        var codec = (ComboBox)Field(opt, "CodecBox");
        Check(codec.Items.Count == 3, "编码三选一：自动 / H.264 / HEVC");

        Check(((TextBlock)Field(opt, "RadiusText")).Text.Trim().Length > 0, "半径有数值文案");
        Check(((TextBlock)Field(opt, "CameraSizeText")).Text.Contains("px"), "边长文案带单位");

        Save(Render(opt), Path.Combine(_outDir, "record-options.png"));
        opt.Close();

        // ---- ② 裁剪 / 倍速 ----
        var trimType = asm.GetType("LTools.Views.CapturePro.RecordTrimWindow")!;
        var trim = (Window)Activator.CreateInstance(trimType, AnyCtor, null, new[] { entry }, null)!;
        trim.Show();
        Pump(trim);

        int duration = (int)entry.GetType().GetProperty("DurationMs")!.GetValue(entry)!;
        Check(((TextBlock)Field(trim, "StartText")).Text == "00:00.000", "起点时间码从零开始");
        Check(((TextBlock)Field(trim, "EndText")).Text == RecordTrimText(duration),
            $"终点时间码 = 片长（{((TextBlock)Field(trim, "EndText")).Text}）");
        Check((int)Field(trim, "_endMs") == duration, "整片默认全选");

        var speedBox = (ComboBox)Field(trim, "SpeedBox");
        Check(speedBox.Items.Count == 9, $"倍速 9 档（实际 {speedBox.Items.Count}）");
        Check(Math.Abs((double)Field(trim, "_speed") - 1.0) < 0.001, "默认 1.0x");

        speedBox.SelectedIndex = 7;   // 2.0x
        Pump(trim);
        Check(Math.Abs((double)Field(trim, "_speed") - 2.0) < 0.001, "选到 2.0x");
        Check(((TextBlock)Field(trim, "OutputText")).Text.Contains("2.0x"), "状态行写明倍速");
        Check(((TextBlock)Field(trim, "BadgeText")).Text.Contains("2.0x"), "画面角标也写明倍速");
        Check(((TextBlock)Field(trim, "RangeText")).Text.Contains("成品 00:00."),
            $"选区文案给到毫秒（{((TextBlock)Field(trim, "RangeText")).Text}）");
        Check((int)Field(trim, "_startMs") == 0 && (int)Field(trim, "_endMs") == duration,
            "改倍速不动源时间轴选区（照上游：时间码保持原时间）");

        string before = ((TextBlock)Field(trim, "RangeText")).Text;
        InvokeWith(trim, "StartGrip_DragDelta", null!,
            new System.Windows.Controls.Primitives.DragDeltaEventArgs(30, 0));
        Pump(trim);
        int startMs = (int)Field(trim, "_startMs");
        Check(startMs > 0, $"拖起点抓手改起点（{startMs} ms）");
        Check(((TextBlock)Field(trim, "StartText")).Text != "00:00.000", "起点时间码跟着变");
        Check(((TextBlock)Field(trim, "RangeText")).Text != before, "选区文案跟着变");

        // 往左拖到底：夹在 0，不会出现负时间
        InvokeWith(trim, "StartGrip_DragDelta", null!,
            new System.Windows.Controls.Primitives.DragDeltaEventArgs(-1000, 0));
        Pump(trim);
        Check((int)Field(trim, "_startMs") == 0, "往左拖到底夹在 0");

        // 终点抓手不许压到起点上
        InvokeWith(trim, "EndGrip_DragDelta", null!,
            new System.Windows.Controls.Primitives.DragDeltaEventArgs(-100000, 0));
        Pump(trim);
        Check((int)Field(trim, "_endMs") - (int)Field(trim, "_startMs") >= 16,
            "终点抓手压不到起点（至少留一帧）");

        Save(Render(trim), Path.Combine(_outDir, "record-trim.png"));
        trim.Close();
    }

    private static string RecordTrimText(int ms) =>
        (string)typeof(CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.CapturePro.RecordTrim")!
            .GetMethod("FormatSeconds")!.Invoke(null, new object[] { ms })!;

    /// <summary>合成一帧（不碰真桌面）：固定底色够 ffmpeg 编出可解码的 mp4。</summary>
    private static System.Drawing.Bitmap MakeFrame(Int32Rect area)
    {
        var bmp = new System.Drawing.Bitmap(
            Math.Max(2, area.Width), Math.Max(2, area.Height),
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var graphics = System.Drawing.Graphics.FromImage(bmp);
        graphics.Clear(System.Drawing.Color.FromArgb(24, 32, 64));
        return bmp;
    }

    /// <summary>没有 ffmpeg 时只验「空态 + 页签」这几个不出编码的断言。</summary>
    private static void CheckRecordHistory(Type catalogType)
    {
        catalogType.GetMethod("Clear")!.Invoke(null, null);
    }

    /// <summary>
    /// 参数行：序号有「形状 / 编号」、滤镜有「类型 / 强度」两族新参数，
    /// 滚轮在数值上微调、点数值复位（照 Snow Shot 的手感）。
    /// </summary>
    /// <summary>
    /// 样式记忆（P2-a）：换工具再切回来时刚才调的值还在、同一族的两个工具共用一份、
    /// 不同族互不影响、参数条跟着族刷新、共享颜色开关按上游语义工作。
    /// </summary>
    /// <summary>
    /// P2-c 逐像素：文字的背景胶囊（实心 / 斜线 / 圆角）、文字描边、水平对齐、粗 / 斜、字体族。
    /// 走真实 <c>Mark.Text</c> + <c>CaptureDocument.Render</c>（不另画一份）。
    /// </summary>
    private static void TextFamily()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var docType = asm.GetType("LTools.Features.CapturePro.CaptureDocument")!;
        var specType = asm.GetType("LTools.Features.CapturePro.TextSpec")!;
        var alignType = asm.GetType("LTools.Features.CapturePro.TextAlign")!;
        var fillType = asm.GetType("LTools.Features.CapturePro.FillKind")!;
        var textType = asm.GetType("LTools.Features.CapturePro.Mark+Text")!;

        const int W = 320, H = 240;

        var white = new byte[W * H * 4];
        for (int i = 0; i < white.Length; i += 4)
        {
            white[i] = 0xFF;
            white[i + 1] = 0xFF;
            white[i + 2] = 0xFF;
            white[i + 3] = 0xFF;
        }
        var baseImage = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, white, W * 4);
        baseImage.Freeze();

        object Spec(double size, bool bold, bool italic, string align, Color color,
            double strokeWidth, Color stroke, Color fill, string fillStyle, double radius,
            string family = "") =>
            Activator.CreateInstance(specType, family, size, bold, italic,
                Enum.Parse(alignType, align), color, strokeWidth, stroke, fill,
                Enum.Parse(fillType, fillStyle), radius)!;

        object Text(Point at, string content, object spec) =>
            Activator.CreateInstance(textType, at, content, spec)!;

        byte[] Render(params object[] marks)
        {
            var document = Activator.CreateInstance(docType, baseImage, new Int32Rect(0, 0, W, H), 1.0)!;
            docType.GetProperty("Area")!.SetValue(document, new Int32Rect(0, 0, W, H));
            var add = docType.GetMethod("Add")!;
            foreach (var mark in marks) add.Invoke(document, new[] { mark });
            var frame = (BitmapSource)docType.GetMethod("Render")!.Invoke(document, null)!;
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            return pixels;
        }

        (byte b, byte g, byte r, byte a) Px(byte[] px, int x, int y)
        {
            int at = (y * W + x) * 4;
            return (px[at], px[at + 1], px[at + 2], px[at + 3]);
        }

        int Count(byte[] px, Func<(byte b, byte g, byte r, byte a), bool> test)
        {
            int n = 0;
            for (int i = 0; i < px.Length; i += 4)
                if (test((px[i], px[i + 1], px[i + 2], px[i + 3]))) n++;
            return n;
        }

        static bool Lime((byte b, byte g, byte r, byte a) p) => p.g > 200 && p.r < 90 && p.b < 90;
        static bool Magenta((byte b, byte g, byte r, byte a) p) => p.r > 190 && p.b > 190 && p.g < 110;
        static bool Paper((byte b, byte g, byte r, byte a) p) => p.r > 240 && p.g > 240 && p.b > 240;

        var origin = new Point(20, 30);
        const string glyphs = "Ag";

        // ---- 背景胶囊：实心铺满 / 「无」不画 / 圆角真的切掉角 ----
        var solid = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Black, Colors.Lime, "Solid", 0)));
        int solidLime = Count(solid, Lime);
        Check(solidLime > 300, $"实心背景铺出胶囊（{solidLime} 像素）");
        Check(Lime(Px(solid, 10, 28)),
            "圆角 0 时背景胶囊的左上角是实心的（origin - 内边距 内缩 1px）");
        Check(Paper(Px(solid, 300, 40)), "胶囊外面还是底图");

        var noFill = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Black, Colors.Lime, "None", 0)));
        Check(Count(noFill, Lime) == 0, "填充「无」时一条胶囊都不画");

        var rounded = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Black, Colors.Lime, "Solid", 20)));
        Check(!Lime(Px(rounded, 10, 28)), "圆角 20 时那个角被切掉了");
        Check(Lime(Px(rounded, 12, 50)), "圆角只切四角，中间照样实心");
        Check(Count(rounded, Lime) > 300, $"圆角后胶囊仍然铺满（{Count(rounded, Lime)} 像素）");

        // ---- 斜线背景：只有斜线覆盖，比实心少、比没有多 ----
        var hatch = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Black, Colors.Lime, "Line", 0)));
        int hatchLime = Count(hatch, Lime);
        Check(hatchLime > 0 && hatchLime < solidLime,
            $"斜线背景画的是斜线不是实心（{hatchLime} < {solidLime}）");

        // ---- 文字描边：单独一遍（字色仍是字色，描边色只出现在外圈） ----
        var plain = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 0)));
        var halo = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 6, Colors.Lime, Colors.Transparent, "None", 0)));
        Check(Count(plain, Lime) == 0, "描边宽 0 时没有描边色像素");
        int haloLime = Count(halo, Lime);
        Check(haloLime > 150, $"描边画出外圈（{haloLime} 像素）");
        Check(Count(halo, Magenta) > 60, "描边不盖住字色（前景仍是字色）");

        // ---- 粗体 / 斜体 / 字体族：画出来的像素确实不一样 ----
        int regularInk = Count(plain, Magenta);
        var bold = Render(Text(origin, glyphs,
            Spec(30, true, false, "Left", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 0)));
        int boldInk = Count(bold, Magenta);
        Check(boldInk > regularInk, $"加粗的墨更多（{boldInk} vs {regularInk}）");

        var italic = Render(Text(origin, glyphs,
            Spec(30, false, true, "Left", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 0)));
        int Diff(byte[] a, byte[] b)
        {
            int n = 0;
            for (int i = 0; i < a.Length; i += 4)
                if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) n++;
            return n;
        }
        Check(Diff(plain, italic) > 30, $"斜体画得不一样（{Diff(plain, italic)} 像素不同）");

        var consolas = Render(Text(origin, glyphs,
            Spec(30, false, false, "Left", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 0,
                "Consolas")));
        Check(Diff(plain, consolas) > 30, $"换字体画得不一样（{Diff(plain, consolas)} 像素不同）");

        // ---- 水平对齐：两行时窄的那一行贴左 / 居中 / 贴右 ----
        int InkBottom(byte[] px)
        {
            for (int y = H - 1; y >= 0; y--)
                for (int x = 0; x < W; x++)
                    if (Magenta(Px(px, x, y))) return y;
            return -1;
        }

        int BottomBandMinX(byte[] px)
        {
            int bottom = InkBottom(px);
            if (bottom < 0) return -1;

            // 从最底一行往上走，直到遇到没有墨的行 = 第二行的顶
            int top = bottom;
            while (top > 0)
            {
                bool ink = false;
                for (int x = 0; x < W && !ink; x++) ink = Magenta(Px(px, x, top - 1));
                if (!ink) break;
                top--;
            }

            int minX = int.MaxValue;
            for (int y = top; y <= bottom; y++)
                for (int x = 0; x < W; x++)
                    if (Magenta(Px(px, x, y))) minX = Math.Min(minX, x);
            return minX == int.MaxValue ? -1 : minX;
        }

        var wideLeft = Render(Text(new Point(20, 30), "MMMM\nii",
            Spec(24, false, false, "Left", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 6)));
        var wideCenter = Render(Text(new Point(20, 30), "MMMM\nii",
            Spec(24, false, false, "Center", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 6)));
        var wideRight = Render(Text(new Point(20, 30), "MMMM\nii",
            Spec(24, false, false, "Right", Colors.Magenta, 0, Colors.Lime, Colors.Transparent, "None", 6)));

        int leftX = BottomBandMinX(wideLeft);
        int centerX = BottomBandMinX(wideCenter);
        int rightX = BottomBandMinX(wideRight);
        Check(leftX > 0 && centerX > 0 && rightX > 0, $"三份都画出来了（{leftX} / {centerX} / {rightX}）");
        Check(leftX < centerX && centerX < rightX,
            $"对齐：左 < 中 < 右（{leftX} / {centerX} / {rightX}）");
        Check(rightX - leftX > 20, $"右对齐把窄行推到右边（相差 {rightX - leftX} 像素）");
    }

    /// <summary>
    /// P3-b / P3-c：顶部选区信息条（数字 / 滚轮 ±1 / 锁 / 比例下拉 / 圆角与效果宽）
    /// 与选区参数对话框（几何联动 / 效果类型 / 颜色 / 快速集 / 确定回传）。
    /// </summary>
    private static void SelectionBarSection(CaptureOverlayWindow window)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var docType = doc.GetType();
        double scale = (double)docType.GetProperty("Scale")!.GetValue(doc)!;
        Int32Rect Area() => (Int32Rect)docType.GetProperty("Area")!.GetValue(doc)!;

        var styleType = asm.GetType("LTools.Features.CapturePro.SelectionStyle")!;
        var aspectType = asm.GetType("LTools.Features.CapturePro.SelectionAspect")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.SelectionStore")!;

        FrameworkElement Elm(string name) => (FrameworkElement)Field(window, name);
        TextBlock Label(string name) => (TextBlock)Field(window, name);

        object Style() => Field(window, "_selStyle");
        T Prop<T>(object target, string name) =>
            (T)target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(target)!;

        // 现场：外观 / 比例锁都退回默认，别让本机 ui.json 里留下的值干扰断言
        InvokeWith(window, "ApplySelStyle", Activator.CreateInstance(styleType)!, false);
        InvokeWith(window, "SetAspectLocked", false);

        SetSelection(window, new Rect(200, 150, 600, 400));
        Invoke(window, "SyncArea");
        Pump(window);

        var bar = Elm("SelBar");
        Check(bar.Visibility == Visibility.Visible, "框选后信息条可见");

        // ---- ① 数字 = 屏幕物理像素，摆位在选区上方 ----
        var area = Area();
        Check(Label("SelXText").Text == area.X.ToString() &&
              Label("SelYText").Text == area.Y.ToString(), $"信息条 X / Y：{Label("SelXText").Text}, {Label("SelYText").Text}");
        Check(Label("SelWText").Text == area.Width.ToString() &&
              Label("SelHText").Text == area.Height.ToString(), $"信息条宽 × 高：{Label("SelWText").Text} x {Label("SelHText").Text}");
        Check(Label("SelRText").Text == "0" && Label("SelEText").Text == "0", "默认没有圆角 / 效果宽");

        bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double barTop = Canvas.GetTop(bar);
        Check(Math.Abs(barTop - (150 - bar.ActualHeight - 4)) < 1.5,
            $"信息条摆在选区上方、间距 4（实际 top {barTop:0.#}，条高 {bar.ActualHeight:0.#}）");

        // ---- ② 滚轮 ±1 物理像素：X / Y 平移 ----
        int x0 = area.X, y0 = area.Y;
        Wheel(Elm("SelFieldX"), 120);
        Pump(window);
        Check(Area().X == x0 + 1, $"X 滚轮 +1：{x0} → {Area().X}");
        Wheel(Elm("SelFieldX"), -120);
        Wheel(Elm("SelFieldY"), -120);
        Pump(window);
        Check(Area().X == x0, "X 滚轮 -1 回到原位");
        Check(Area().Y == y0 - 1, $"Y 滚轮 -1：{y0} → {Area().Y}");

        // ---- ③ 滚轮改宽 / 高（右 / 下边，左上角不动） ----
        int w0 = Area().Width, h0 = Area().Height;
        Wheel(Elm("SelFieldW"), 120);
        Pump(window);
        Check(Area().Width == w0 + 1 && Area().X == x0, $"宽 +1 且左边不动：{Area().Width} · X {Area().X}");
        Wheel(Elm("SelFieldH"), 120);
        Pump(window);
        Check(Area().Height == h0 + 1 && Area().Y == y0 - 1, $"高 +1 且上边不动：{Area().Height} · Y {Area().Y}");

        // ---- ④ 圆角 / 效果宽滚轮 ±1（物理像素） ----
        Wheel(Elm("SelFieldR"), 120);
        Pump(window);
        double radiusDip = Prop<double>(Style(), "CornerRadius");
        Check(Label("SelRText").Text == "1", $"圆角滚轮 +1 显示 1（实际 {Label("SelRText").Text}）");
        Check(Math.Abs(radiusDip * scale - 1) < 0.01, $"圆角存的是 DIP、按物理像素显示（{radiusDip:0.###} DIP）");
        Wheel(Elm("SelFieldR"), -120);
        Pump(window);
        Check(Label("SelRText").Text == "0", "圆角滚轮 -1 回到 0");

        Wheel(Elm("SelFieldE"), 120);
        Pump(window);
        Check(Label("SelEText").Text == "1", $"效果宽滚轮 +1（实际 {Label("SelEText").Text}）");
        double effectDip = Prop<double>(Style(), "ShadowWidth");
        Check(Math.Abs(effectDip * scale - 1) < 0.01, $"效果宽也按物理像素显示（{effectDip:0.###} DIP）");
        Check(Prop<double>(docType.GetProperty("Style")!.GetValue(doc)!, "EffectWidth") > 0,
            "文档样式跟着变（预览与导出共用）");
        Wheel(Elm("SelFieldE"), -120);
        Pump(window);

        // ---- ⑤ 锁：图标变主色，改宽按比例推高、左上角锚定 ----
        var lockIcon = (System.Windows.Shapes.Path)Field(window, "SelLockIcon");
        lockBtn_Click(Elm("SelLockBtn"), window);
        Pump(window);
        Check((bool)Field(window, "_aspectLocked"), "点锁图标 = 锁上比例");
        Check(((SolidColorBrush)lockIcon.Fill).Color == Color.FromRgb(0x16, 0x77, 0xFF),
            "锁上后图标是主色 #1677FF");

        var beforeLock = Area();
        double ratio = (double)beforeLock.Height / Math.Max(1, beforeLock.Width);
        InvokeWith(window, "AdjustBarSelection", 0, 0, 60, 0);
        Pump(window);
        var locked = Area();
        Check(Math.Abs(locked.Height - locked.Width * ratio) <= 2,
            $"锁比例改宽：高按比例推（{locked.Width} × {locked.Height}，比例 {ratio:0.###}）");
        Check(locked.X == beforeLock.X && locked.Y == beforeLock.Y, "锁比例改宽时左上角不动");

        lockBtn_Click(Elm("SelLockBtn"), window);
        Pump(window);
        Check(!(bool)Field(window, "_aspectLocked"), "再点一次解锁");

        // ---- ⑥ 比例预设：1:1 把选区套成正方形 ----
        InvokeWith(window, "SetAspectPreset", Enum.Parse(aspectType, "Square"));
        Pump(window);
        var square = Area();
        Check((bool)Field(window, "_aspectLocked"), "选比例预设后是锁着的");
        Check(Math.Abs(square.Width - square.Height) <= 2, $"1:1 预设把选区套成正方形：{square.Width} × {square.Height}");
        Check(Label("SelAspectText").Text == AspectLabel(aspectType, "Square"),
            $"比例下拉显示当前预设：{Label("SelAspectText").Text}");
        InvokeWith(window, "SetAspectLocked", false);
        Pump(window);

        // ---- ⑦ 比例下拉：选区预设 + 8 个比例，项高 28 ----
        Invoke(window, "BuildAspectMenu");
        Pump(window);
        var menuPanel = (StackPanel)Field(window, "SelAspectItems");
        var texts = new List<string>();
        var heights = new List<double>();
        foreach (var child in menuPanel.Children)
        {
            if (child is TextBlock group) texts.Add("·" + group.Text);
            else if (child is Border item)
            {
                heights.Add(item.Height);
                if (item.Child is TextBlock label) texts.Add(label.Text);
            }
        }

        Check(texts.Any(t => t == "·选区预设") && texts.Any(t => t == "·比例预设"), "下拉有「选区预设 / 比例预设」两组");
        Check(texts.Contains("上次选区"), "下拉有「上次选区」（快速集）");
        foreach (string ratioLabel in new[] { "1:1", "4:3", "3:4", "16:9", "9:16" })
            Check(texts.Contains(ratioLabel), $"下拉有比例 {ratioLabel}");
        Check(heights.Count >= 9 && heights.All(h => Math.Abs(h - 28) < 0.01),
            $"下拉项高统一 28（{heights.Count} 项）");

        var squareLabel = AspectLabel(aspectType, "Square");
        var squareItem = menuPanel.Children.OfType<Border>()
            .First(b => b.Child is TextBlock t && t.Text == squareLabel);
        squareItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        Pump(window);
        Check((bool)Field(window, "_aspectLocked") && Field(window, "_aspect")!.ToString() == "Square",
            "点下拉里的 1:1 = 套成正方形并锁上");
        InvokeWith(window, "SetAspectLocked", false);
        Pump(window);

        // ---- ⑧ 参数对话框：字段 / 联动 / 效果类型 / 颜色 / 快速集 ----
        InvokeWith(window, "ApplySelStyle",
            Activator.CreateInstance(styleType)!, false);
        SetSelection(window, new Rect(200, 150, 600, 400));
        Invoke(window, "SyncArea");
        Pump(window);

        var dlgType = asm.GetType("LTools.Views.CapturePro.SelectionParamsWindow")!;
        var ctor = dlgType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        object current = InvokeValue(window, "CurrentSelectionParams")!;
        object presets = storeType.GetProperty("Presets",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var bounds = new Int32Rect(0, 0,
            (int)Math.Round(window.ActualWidth * scale), (int)Math.Round(window.ActualHeight * scale));

        var dlg = (Window)ctor.Invoke(new[] { current, null, presets, bounds, scale, true })!;
        dlg.Owner = window;
        dlg.Show();
        Pump(dlg);

        var xBox = (TextBox)Field(dlg, "XBox");
        var yBox = (TextBox)Field(dlg, "YBox");
        var wBox = (TextBox)Field(dlg, "WidthBox");
        var hBox = (TextBox)Field(dlg, "HeightBox");
        var rBox = (TextBox)Field(dlg, "RadiusBox");
        var eBox = (TextBox)Field(dlg, "EffectWidthBox");
        var lockButton = (Button)Field(dlg, "LockBtn");
        var shadowType = (RadioButton)Field(dlg, "ShadowTypeBtn");
        var borderType = (RadioButton)Field(dlg, "BorderTypeBtn");
        var quickSet = (ComboBox)Field(dlg, "QuickSetBox");
        var removePreset = (Button)Field(dlg, "RemovePresetBtn");
        var colorHex = (TextBox)Field(dlg, "ColorHexBox");
        var palette = (WrapPanel)Field(dlg, "ColorPalette");
        var widthLabel = (TextBlock)Field(dlg, "EffectWidthLabel");
        var colorLabel = (TextBlock)Field(dlg, "EffectColorLabel");

        var openArea = Area();
        Check(Math.Abs(xBox.ActualWidth - 218) < 1.5, $"参数框两列各 218 宽（X 列实际 {xBox.ActualWidth:0.#}）");
        Check(xBox.Text == openArea.X.ToString() && yBox.Text == openArea.Y.ToString() &&
              wBox.Text == openArea.Width.ToString() && hBox.Text == openArea.Height.ToString(),
            $"参数框预填当前选区：{xBox.Text},{yBox.Text} {wBox.Text} x {hBox.Text}");
        Check(shadowType.IsChecked == true && borderType.IsChecked == false, "默认是「阴影」");
        Check(widthLabel.Text == "阴影宽度" && colorLabel.Text == "阴影颜色", "效果标签默认写「阴影」");
        Check(palette.Children.Count >= 12, $"色板至少 12 个颜色（{palette.Children.Count}）");
        Check(quickSet.Items.Count >= 3, $"快速集至少 3 项（{quickSet.Items.Count}）");
        Check(!removePreset.IsEnabled, "选中「快速集…」时删除按钮不可用");

        // 改宽 + 锁：改宽自动推高
        lockButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(dlg);
        double lockRatio = (double)int.Parse(hBox.Text) / Math.Max(1, int.Parse(wBox.Text));
        wBox.Text = "300";
        Pump(dlg);
        Check(Math.Abs(int.Parse(hBox.Text) - 300 * lockRatio) <= 1,
            $"锁上后改宽自动推高：300 → {hBox.Text}（比例 {lockRatio:0.###}）");

        // 改高推宽
        hBox.Text = "120";
        Pump(dlg);
        Check(Math.Abs(int.Parse(wBox.Text) - 120 / lockRatio) <= 1,
            $"改高自动推宽：120 → {wBox.Text}");

        // 切「边框」：标签换，宽度 / 颜色仍是各自那一份
        borderType.IsChecked = true;
        Pump(dlg);
        Check(widthLabel.Text == "边框宽度" && colorLabel.Text == "边框颜色", "切到边框后标签跟着换");
        Check(colorHex.Text.Trim().ToUpperInvariant() == "#FFFFFF", $"边框默认白色（实际 {colorHex.Text}）");
        shadowType.IsChecked = true;
        Pump(dlg);
        Check(widthLabel.Text == "阴影宽度" && colorHex.Text.Trim().ToUpperInvariant() == "#333333",
            $"切回阴影：标签与颜色都回到阴影那一份（{colorHex.Text}）");
        borderType.IsChecked = true;
        Pump(dlg);

        // 点色板第 5 个（红）→ HEX 跟着变
        var swatch = palette.Children.OfType<Border>().ElementAt(4);
        swatch.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Pump(dlg);
        Check(colorHex.Text.Trim().ToUpperInvariant() == "#EF4444", $"点色板后 HEX 同步：{colorHex.Text}");

        eBox.Text = "5";
        rBox.Text = "12";

        // 「确定」走的就是 ReadParams：字段 → 参数（对话框用 ShowDialog，冒烟里不开模态，
        // 直接读同一个出口；DialogResult 只能在 ShowDialog 里设，见 WPF 的约束）
        object? result = InvokeValue(dlg, "ReadParams");
        Check(result != null, "读字段得到一份参数");
        if (result != null)
        {
            Check(Prop<int>(result, "Width") == int.Parse(wBox.Text) &&
                  Prop<int>(result, "Height") == int.Parse(hBox.Text),
                $"读出来的宽 / 高与字段一致：{Prop<int>(result, "Width")} × {Prop<int>(result, "Height")} vs {wBox.Text} × {hBox.Text}");
            Check(Prop<bool>(result, "LockAspect"), "读出来的锁状态一致");
            Check(Prop<int>(result, "RadiusPixels") == 12, "读出来的圆角 = 12");
            Check(Prop<object>(result, "BorderType")!.ToString() == "Border", "读出来的效果类型 = 边框");
            Check(Prop<int>(result, "BorderWidthPixels") == 5, "读出来的边框宽 = 5");
            Check(Prop<Color>(result, "EffectColor") == Color.FromRgb(0xEF, 0x44, 0x44),
                "读出来的边框色 = 点中的那个红色");
        }

        dlg.Close();

        // 收尾：外观 / 比例锁 / 几何都还原，后面的用例继续用同一份文档
        InvokeWith(window, "ApplySelStyle", Activator.CreateInstance(styleType)!, false);
        InvokeWith(window, "SetAspectLocked", false);
        SetSelection(window, new Rect(200, 150, 600, 400));
        Invoke(window, "SyncArea");
        Pump(window);
    }

    /// <summary>比例预设的中文名（<c>SelectionAspects.Label</c> 的 static 反射）。</summary>
    private static string AspectLabel(Type aspectEnumType, string name) =>
        (string)aspectEnumType.Assembly.GetType("LTools.Features.CapturePro.SelectionAspects")!
            .GetMethod("Label", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { Enum.Parse(aspectEnumType, name) })!;

    /// <summary>
    /// 点一下信息条上的字段 / 锁：直接调窗口的分发器。
    /// 鼠标那几个事件（<c>PreviewMouseLeftButtonDown</c> / <c>MouseLeftButtonDown</c>）都是 **Direct**
    /// 路由：真鼠标由 WPF 沿路由逐元素提升，反射 <c>RaiseEvent</c> 到子元素则到不了祖先，
    /// 所以冒烟直接走分发器本身（真机点一遍覆盖真实事件）。
    /// </summary>
    private static void lockBtn_Click(FrameworkElement target, Window window)
    {
        InvokeWith(window, "HandleSelectionBarClick", target);
        Pump(window);
    }

    private static void StyleMemory(CaptureOverlayWindow window)
    {
        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        object Tool(string name) => Enum.Parse(toolType, name);

        var toolbar = Field(window, "Toolbar")!;
        var paramRow = toolbar.GetType().GetField("ParamRow", Private)!.GetValue(toolbar) as Panel;
        var styles = Member(window, "_styles")!;

        void Select(string name)
        {
            InvokeWith(window, "SetTool", Tool(name));
            Pump(window);
        }

        object Style() => Member(window, "_style")!;
        object StyleOf(string name) { Select(name); return Style(); }
        double WidthOf(object style) => (double)style.GetType().GetProperty("Width")!.GetValue(style)!;
        void SetWidth(object style, double value) =>
            style.GetType().GetProperty("Width")!.SetValue(style, value);
        Color ColorOf(object style) => (Color)style.GetType().GetProperty("Color")!.GetValue(style)!;

        Select("Pen");
        var pen = Style();
        Check(WidthText(paramRow) != null, "画笔参数条有「线宽」那一格");
        SetWidth(pen, 13);
        toolbar.GetType().GetMethod("RefreshValues")!.Invoke(toolbar, null);
        Check(WidthText(paramRow) == "13", $"画笔线宽改成 13 后参数条显示 13（实际 {WidthText(paramRow)}）");

        var shape = StyleOf("Shape");
        Check(!ReferenceEquals(pen, shape), "换到形状拿到另一份样式（不是同一个对象）");
        Check(Math.Abs(WidthOf(shape) - 4) < 1e-6, $"形状用自己的默认线宽 4（实际 {WidthOf(shape)}）");
        Check(WidthText(paramRow) == "4", $"参数条跟着换成形状的 4（实际 {WidthText(paramRow)}）");

        // ---- P2-b：形状族的参数行（形状 / 描边 / 笔触 / 填充色 / 填充 / 圆角）----
        StackPanel? Host(string label) =>
            paramRow?.Children.OfType<StackPanel>().FirstOrDefault(host =>
                host.Visibility == Visibility.Visible &&
                host.Children.OfType<TextBlock>().Any(text => text.Text == label));

        List<string> ChipTexts(string label, bool visibleOnly = true) =>
            Host(label)?.Children.OfType<RadioButton>()
                .Where(chip => !visibleOnly || chip.Visibility == Visibility.Visible)
                .Select(chip => chip.Content?.ToString() ?? "")
                .ToList() ?? new List<string>();

        Check(Host("形状") != null, "形状工具的参数行有「形状」那一排");
        Check(string.Join(",", ChipTexts("形状")) == "矩形,椭圆,菱形",
            "形状：矩形 / 椭圆 / 菱形（思考气泡不在形状工具里）：" + string.Join(",", ChipTexts("形状")));
        Check(ChipTexts("形状", false).Contains("思考"), "思考气泡那个胶囊做出来了，只是这一格藏起来");
        Check(string.Join(",", ChipTexts("描边")) == "实线,虚线,点线", "描边三档：" + string.Join(",", ChipTexts("描边")));
        Check(string.Join(",", ChipTexts("笔触")) == "干净,手绘,涂鸦", "笔触三档：" + string.Join(",", ChipTexts("笔触")));
        Check(string.Join(",", ChipTexts("填充")) == "无,实心,斜线,交叉", "填充四档（无 = 上游 Border）：" + string.Join(",", ChipTexts("填充")));
        Check(Host("填充色") != null, "填充色单独一排（与描边色分开）");
        Check(StepText(paramRow, "圆角") == "6", $"圆角默认 6（实际 {StepText(paramRow, "圆角")}）");

        // 真点一下「椭圆」胶囊：样式跟着改，参数条选中态跟手
        var ellipseChip = Host("形状")!.Children.OfType<RadioButton>()
            .First(chip => (chip.Content?.ToString() ?? "") == "椭圆");
        Check(ellipseChip.IsChecked != true, "默认不是椭圆");
        ellipseChip.IsChecked = true;
        Pump(window);
        Check(Style().GetType().GetProperty("ShapeKind")!.GetValue(Style())!.ToString() == "Ellipse",
            "点「椭圆」把形状改成了椭圆");
        Check(ellipseChip.IsChecked == true, "点过之后胶囊是选中态");

        // 气泡族：多出「思考」这一档
        Select("SpeechBubble");
        Check(ChipTexts("形状").Contains("思考"), "气泡工具的形状里有「思考气泡」：" + string.Join(",", ChipTexts("形状")));
        Check(ChipTexts("填充").Count == 4, "气泡也有整套填充");
        Select("Shape");
        Host("形状")!.Children.OfType<RadioButton>().First(chip => (chip.Content?.ToString() ?? "") == "矩形")
            .IsChecked = true;
        Pump(window);
        Check(Style().GetType().GetProperty("ShapeKind")!.GetValue(Style())!.ToString() == "Rectangle",
            "点回「矩形」把形状恢复成矩形（不给后面的断言留奇怪状态）");

        Check(Math.Abs(WidthOf(StyleOf("Pen")) - 13) < 1e-6, "切回画笔，刚才的 13 还在");
        Check(Math.Abs(WidthOf(StyleOf("Shape")) - 4) < 1e-6, "再切回形状，形状的 4 没被画笔带走");

        // 共享颜色：关着时只改一族，开着时改一处 = 全改（上游「同步工具颜色」）
        var setSync = styles.GetType().GetMethod("SetSyncColors")!;
        var setColor = styles.GetType().GetMethod("SetColor")!;
        var palette = (Color[])typeof(CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.CapturePro.ToolStyle")!
            .GetField("Palette")!.GetValue(null)!;

        setSync.Invoke(styles, new object[] { false });
        setColor.Invoke(styles, new object[] { "pen", Colors.Blue });
        Check(ColorOf(StyleOf("Pen")) == Colors.Blue, "共享颜色关着时画笔变蓝");
        Check(ColorOf(StyleOf("Shape")) != Colors.Blue, "共享颜色关着时形状不动");

        setSync.Invoke(styles, new object[] { true });
        setColor.Invoke(styles, new object[] { "shape", Colors.Lime });
        Check(ColorOf(StyleOf("Pen")) == Colors.Lime, "打开共享颜色后改形状 = 画笔也变");
        Check(ColorOf(StyleOf("Arrow")) == Colors.Lime, "连还没碰过的箭头也跟着变");
        Check(ColorOf(StyleOf("Shape")) == Colors.Lime, "形状族color一致");
        Check(ColorOf(StyleOf("SpeechBubble")) == Colors.Lime, "气泡与形状同族，颜色也一致");

        setSync.Invoke(styles, new object[] { false });
        var textBefore = ColorOf(StyleOf("Text"));
        setColor.Invoke(styles, new object[] { "pen", Colors.Blue });
        Check(ColorOf(StyleOf("Text")) == textBefore, "关掉共享颜色后改画笔不影响文字那一族");
        Check(ColorOf(StyleOf("Pen")) == Colors.Blue, "关掉共享颜色后画笔自己确实变了");

        // ---- P2-c：文字族参数行（上游 buildTextFamily）----
        Select("Text");
        Check(Host("字号") != null, "文字有「字号」一格");
        Check(StepText(paramRow, "字号") == "30", $"文字默认字号 30（实际 {StepText(paramRow, "字号")}）");
        Check(string.Join(",", ChipTexts("字号")) == "S,M,L,XL",
            $"字号预设 S / M / L / XL（实际 {string.Join(",", ChipTexts("字号"))}）");
        Check(StepText(paramRow, "线宽") == null, "文字没有「线宽」那一格");

        var textStyle = Style();
        var textSizeProp = textStyle.GetType().GetProperty("TextSize")!;
        RadioButton SizeChip(string label) => Host("字号")!.Children.OfType<RadioButton>()
            .First(chip => (chip.Content?.ToString() ?? "") == label);

        SizeChip("L").IsChecked = true;
        Pump(window);
        Check(Math.Abs((double)textSizeProp.GetValue(textStyle)! - 42) < 1e-6,
            $"点「L」字号变 42（实际 {textSizeProp.GetValue(textStyle)}）");
        Check(StepText(paramRow, "字号") == "42", $"字号那一格跟着显示 42（实际 {StepText(paramRow, "字号")}）");

        SizeChip("M").IsChecked = true;
        Pump(window);
        Check(Math.Abs((double)textSizeProp.GetValue(textStyle)! - 30) < 1e-6, "点「M」回到 30");

        // 字体：胶囊打开可搜索弹层，搜 / 选一条写回样式
        Check(Host("字体") != null, "文字有「字体」一格");
        var fontHost = Host("字体")!;
        var fontButton = fontHost.Children.OfType<Button>().Single();
        var fontCaption = ((Panel)fontButton.Content).Children.OfType<TextBlock>().First();
        Check(fontCaption.Text.Contains("默认"), $"没选字体时胶囊写「默认…」（实际 {fontCaption.Text}）");

        var familyProp = textStyle.GetType().GetProperty("FontFamily")!;
        fontButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        var fontPopup = Field(toolbar, "_fontPopup") as Popup;
        Check(fontPopup != null && fontPopup.IsOpen, "点字体胶囊打开弹层");
        if (fontPopup != null)
        {
            var fontSearch = FindChild<TextBox>(fontPopup.Child);
            var fontList = FindChild<ListBox>(fontPopup.Child);
            Check(fontSearch != null && fontList != null, "弹层里有搜索框与列表");
            if (fontSearch != null && fontList != null)
            {
                Check(fontList.Items.Count > 20, $"字体列表是整台机器的字体（{fontList.Items.Count} 项）");
                fontSearch.Text = "Consolas";
                Pump(window);
                Check(fontList.Items.Count is > 0 and < 20,
                    $"搜索把列表筛窄（{fontList.Items.Count} 项）");
                Check(fontList.Items.Contains("Consolas"), "搜得到 Consolas");

                fontList.SelectedItem = "Consolas";
                Pump(window);
                Check((string)familyProp.GetValue(textStyle)! == "Consolas",
                    $"选中字体写回样式（实际 {familyProp.GetValue(textStyle)}）");
                Check(fontCaption.Text == "Consolas", $"胶囊跟着显示 Consolas（实际 {fontCaption.Text}）");
                Check(!fontPopup.IsOpen, "选完自动收起弹层");
            }
        }

        // 粗体 / 斜体：两个可以同时按下的胶囊
        Check(Host("粗细") != null, "文字有粗 / 斜一格");
        var emphasis = Host("粗细")!.Children.OfType<ToggleButton>().ToList();
        Check(emphasis.Count == 2, $"粗 / 斜是两个开关（{emphasis.Count}）");
        var boldProp = textStyle.GetType().GetProperty("TextBold")!;
        var italicProp = textStyle.GetType().GetProperty("TextItalic")!;
        emphasis[0].IsChecked = true;
        emphasis[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((bool)boldProp.GetValue(textStyle)!, "点 B 打开粗体");
        emphasis[1].IsChecked = true;
        emphasis[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);
        Check((bool)italicProp.GetValue(textStyle)!, "点 I 打开斜体（与粗体可以同时开）");
        Check((bool)boldProp.GetValue(textStyle)!, "开斜体不会把粗体关掉");
        emphasis[0].IsChecked = false;
        emphasis[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        emphasis[1].IsChecked = false;
        emphasis[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Pump(window);

        // 水平对齐：三个胶囊（上游只有水平，没有垂直）
        Check(string.Join(",", ChipTexts("对齐")) == "左,中,右",
            $"对齐是左 / 中 / 右（实际 {string.Join(",", ChipTexts("对齐"))}）");
        var alignProp = textStyle.GetType().GetProperty("TextAlign")!;
        var centerChip = Host("对齐")!.Children.OfType<RadioButton>()
            .First(chip => (chip.Content?.ToString() ?? "") == "中");
        centerChip.IsChecked = true;
        Pump(window);
        Check(alignProp.GetValue(textStyle)!.ToString() == "Center", "点「中」= 居中");
        Host("对齐")!.Children.OfType<RadioButton>()
            .First(chip => (chip.Content?.ToString() ?? "") == "左").IsChecked = true;
        Pump(window);

        // 描边宽度 + 描边色
        Check(StepText(paramRow, "描边") == "0", $"文字默认不描边（实际 {StepText(paramRow, "描边")}）");
        Check(Host("描边色") != null, "文字有「描边色」一格");
        var strokeChips = Host("描边色")!.Children.OfType<RadioButton>().ToList();
        var shownStroke = strokeChips.Where(chip => chip.Visibility == Visibility.Visible).ToList();
        Check(strokeChips.Count == 15 && shownStroke.Count == 5,
            $"描边色是描边那一族 5 个预设（三族共 {strokeChips.Count}，可见 {shownStroke.Count}）");
        var strokeColorProp = textStyle.GetType().GetProperty("StrokeColor")!;
        var pickedStroke = (Color)shownStroke[2].Tag!;
        shownStroke[2].IsChecked = true;
        Pump(window);
        Check((Color)strokeColorProp.GetValue(textStyle)! == pickedStroke, "点描边色写回样式");

        // 背景：填充色 / 填充方式 / 圆角
        Check(Host("填充色") != null, "文字有「填充色」（= 背景色）一格");
        Check(string.Join(",", ChipTexts("填充")) == "无,实心,斜线,交叉",
            $"填充方式是 无 / 实心 / 斜线 / 交叉（实际 {string.Join(",", ChipTexts("填充"))}）");
        Check(StepText(paramRow, "圆角") == "6", $"背景圆角默认 6（实际 {StepText(paramRow, "圆角")}）");

        Select("Shape");
        Check(StepText(paramRow, "字号") == null, "形状没有字号（文字族的格子收起来了）");
        Check(StepText(paramRow, "线宽") != null, "形状的线宽还在");

        // 收尾：把共享颜色关掉、画笔恢复默认（不给后续断言留下奇怪状态）
        setColor.Invoke(styles, new object[] { "pen", palette[0] });
        Select("Select");
        Pump(window);
    }

    /// <summary>
    /// P2-e2：选中一条标注 → 参数行切到**它自己的样式族**并显示这一条的值；改参数只把变过的字段
    /// 合并进该族的创建样式（照上游 <c>style_toolbar_source</c> / <c>set_shape_style_patch</c>）。
    /// 不选 / 换工具 = 参数行交还给当前工具。
    /// </summary>
    private static void SelectionStyleFamily(CaptureOverlayWindow window)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        object Tool(string name) => Enum.Parse(toolType, name);

        var toolbar = Field(window, "Toolbar")!;
        var paramRow = toolbar.GetType().GetField("ParamRow", Private)!.GetValue(toolbar) as Panel;
        var styles = Member(window, "_styles")!;
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var docType = doc.GetType();
        var marks = (System.Collections.IList)docType.GetProperty("Marks")!.GetValue(doc)!;

        var styleType = asm.GetType("LTools.Features.CapturePro.ToolStyle")!;
        var arrowType = asm.GetType("LTools.Features.CapturePro.Mark+Arrow")!;
        var shapeType = asm.GetType("LTools.Features.CapturePro.Mark+Shape")!;
        var distanceType = asm.GetType("LTools.Features.CapturePro.Mark+Distance")!;

        object Family(string family) => styles.GetType().GetMethod("ForFamily")!
            .Invoke(styles, new object[] { family })!;
        object Prop(object target, string name) =>
            target.GetType().GetProperty(name)!.GetValue(target);
        void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);

        StackPanel Host(string label) =>
            paramRow?.Children.OfType<StackPanel>().FirstOrDefault(host =>
                host.Visibility == Visibility.Visible &&
                host.Children.OfType<TextBlock>().Any(text => text.Text == label));
        bool Shows(string label) => Host(label) != null;

        void Select(string name)
        {
            InvokeWith(window, "SetTool", Tool(name));
            Pump(window);
        }

        void SelectMark(int index)
        {
            InvokeWith(window, "SelectMark", index);
            Pump(window);
        }

        string CurrentTool() => toolbar.GetType()
            .GetProperty("CurrentTool", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(toolbar)?.ToString();

        // ---- 现场：多出来的标注与改过的族样式在收尾时还原（后面的用例还要用同一份文档） ----
        var before = ((System.Collections.IEnumerable)docType.GetProperty("Marks")!.GetValue(doc)!)
            .Cast<object>().ToList();

        var arrowStyle = Family("arrow");
        var shapeStyle = Family("shape");
        var distanceStyle = Family("distance");
        var savedArrowWidth = (double)Prop(arrowStyle, "Width")!;
        var savedArrowColor = (Color)Prop(arrowStyle, "Color")!;
        var savedShapeWidth = (double)Prop(shapeStyle, "Width")!;
        var savedShapeColor = (Color)Prop(shapeStyle, "Color")!;
        var savedDistanceFactor = (double)Prop(distanceStyle, "DistanceFactor")!;

        // ---- ① 选一条「旧箭头」（宽 7 / 洋红），创建样式却是默认的宽 4 ----
        Select("Select");
        Set(arrowStyle, "Width", 7d);
        Set(arrowStyle, "Color", Colors.Magenta);
        var oldArrowSpec = styleType.GetMethod("ArrowSpecOf")!
            .Invoke(arrowStyle, new object[] { 11u })!;
        Set(arrowStyle, "Width", 4d);
        Set(arrowStyle, "Color", savedArrowColor);

        var arrowPoints = new[] { new Point(60, 120), new Point(200, 120) };
        var arrow = Activator.CreateInstance(arrowType, new object[] { arrowPoints, oldArrowSpec })!;
        docType.GetMethod("Add")!.Invoke(doc, new[] { arrow });
        SelectMark(marks.Count - 1);

        Check(CurrentTool() == "Select", $"选中标注时工具按钮不动（实际 {CurrentTool()}）");
        Check(Shows("线宽") && StepText(paramRow, "线宽") == "7",
            $"参数行显示这一条自己的线宽 7（实际 {StepText(paramRow, "线宽")}）");
        Check(Shows("起点") && Shows("终点"), "参数行切到箭头族（起点 / 终点端点）");
        Check(!Shows("形状"), "参数行不再显示画笔 / 形状那些格子");

        var display = Member(window, "_selectionStyle");
        Check(display != null, "选中时有一份显示样式（参数行改的是它，不是创建样式）");
        Check(Math.Abs((double)Prop(display, "Width")! - 7) < 1e-6, "显示样式的线宽 = 这一条的 7");
        Check(Math.Abs((double)Prop(arrowStyle, "Width")! - 4) < 1e-6,
            $"创建样式仍是最初的 4（看一条旧箭头不会改掉它）：{Prop(arrowStyle, "Width")}");

        // ---- ② 改颜色：那条标注跟着变，创建样式只合并「颜色」这一项 ----
        var colorChips = Host("颜色")!.Children.OfType<RadioButton>().ToList();
        var picked = (Color)colorChips[2].Tag!;
        colorChips[2].IsChecked = true;
        Pump(window);

        var recolored = marks[marks.Count - 1]!;
        var recoloredSpec = Prop(recolored, "Spec")!;
        Check((Color)Prop(recoloredSpec, "Stroke")! == picked, "选中的箭头换了颜色");
        Check(((IReadOnlyList<Point>)Prop(recolored, "Points")!).SequenceEqual(arrowPoints),
            "改颜色不动几何（点列原样）");
        Check((Color)Prop(arrowStyle, "Color")! == picked, "创建样式跟着换颜色");
        Check(Math.Abs((double)Prop(arrowStyle, "Width")! - 4) < 1e-6,
            $"创建样式的线宽还是 4（只合并变过的字段）：{Prop(arrowStyle, "Width")}");

        // ---- ③ 改线宽：这一次「线宽」确实变了，写回那条也写回创建样式 ----
        var widthHost = Host("线宽")!;
        widthHost.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        });
        Pump(window);
        Check(Math.Abs((double)Prop(Prop(marks[marks.Count - 1], "Spec")!, "Width")! - 8) < 1e-6,
            "滚轮 +1：这条箭头变成 8");
        Check(Math.Abs((double)Prop(arrowStyle, "Width")! - 8) < 1e-6,
            $"线宽变过，创建样式也改成 8（实际 {Prop(arrowStyle, "Width")}）");

        // ---- ④ 取消选中：参数行交还给当前工具（选择工具没有参数） ----
        SelectMark(-1);
        var paramBar = toolbar.GetType().GetField("ParamBar", Private)!.GetValue(toolbar) as FrameworkElement;
        Check(typeof(CaptureOverlayWindow).GetField("_selectionStyle", Private)!.GetValue(window) == null,
            "取消选中后没有显示样式");
        Check(paramBar is not { Visibility: Visibility.Visible }, "选择工具没有参数行（收起来了）");
        Check(!Shows("起点"), "参数行不再停在箭头族");

        // ---- ⑤ 用画笔时选中一条形状：参数行切到形状族，工具仍是画笔 ----
        Select("Pen");
        Check(Shows("线宽"), "画笔的参数行回来了");

        Set(shapeStyle, "Width", 9d);
        Set(shapeStyle, "Color", Colors.Lime);
        var shapeSpec = styleType.GetMethod("ShapeSpecOf")!.Invoke(shapeStyle, new object[] { 5u })!;
        Set(shapeStyle, "Width", 4d);
        Set(shapeStyle, "Color", savedShapeColor);

        var shapeMark = Activator.CreateInstance(shapeType,
            new object[] { new Point(260, 160), new Point(380, 240), shapeSpec })!;
        docType.GetMethod("Add")!.Invoke(doc, new[] { shapeMark });
        SelectMark(marks.Count - 1);

        Check(CurrentTool() == "Pen", $"选中形状时工具还停在画笔（实际 {CurrentTool()}）");
        Check(Shows("形状"), "参数行切到形状族（形状胶囊）");
        Check(!Shows("起点"), "形状族没有箭头的端点格子");
        Check(StepText(paramRow, "线宽") == "9", $"显示这条形状自己的线宽 9（实际 {StepText(paramRow, "线宽")}）");
        Check(Math.Abs((double)Prop(shapeStyle, "Width")! - 4) < 1e-6,
            $"形状族的创建线宽仍是最初的 4（实际 {Prop(shapeStyle, "Width")}）");

        SelectMark(-1);
        Check(Shows("线宽") && !Shows("形状"), "取消选中后参数行回到画笔族");

        // ---- ⑥ 测距「实测值」也走显示样式：系数来自那一条，不是创建默认 ----
        Set(distanceStyle, "DistanceFactor", 1d);
        Set(distanceStyle, "Color", Colors.Cyan);
        var factor2Style = Activator.CreateInstance(styleType)!;
        Set(factor2Style, "Color", Colors.Red);
        Set(factor2Style, "DistanceFactor", 2d);
        var distanceSpec = styleType.GetMethod("DistanceSpecOf")!
            .Invoke(factor2Style, null)!;
        Set(distanceStyle, "Color", savedShapeColor);

        var distanceMark = Activator.CreateInstance(distanceType,
            new object[] { new Point(300, 300), new Point(400, 300), Colors.Red, 2d, distanceSpec })!;
        docType.GetMethod("Add")!.Invoke(doc, new[] { distanceMark });
        SelectMark(marks.Count - 1);

        var measureHost = Host("实测值");
        Check(measureHost != null, "选中测距时参数行切到测距族（有「实测值」）");
        var measureBox = measureHost?.Children.OfType<TextBox>().FirstOrDefault();
        var markLength = (double)Prop(distanceMark, "Length")! * (double)Prop(doc, "Scale")!;
        var shown = double.TryParse(measureBox?.Text ?? "", out double shownValue) ? shownValue : -1;
        Check(measureBox is { IsEnabled: true }, "选中测距后「实测值」可用");
        Check(Math.Abs(shown - markLength * 2) < 0.01,
            $"实测值按这一条自己的系数 2 显示（{shown:0.##} vs 期望 {markLength * 2:0.##}，创建样式是 1）");

        // ---- 收尾：还原标注与族样式 ----
        SelectMark(-1);
        var markType = asm.GetType("LTools.Features.CapturePro.Mark")!;
        var snapshot = Array.CreateInstance(markType, before.Count);
        for (int i = 0; i < before.Count; i++) snapshot.SetValue(before[i], i);
        docType.GetMethod("ResetMarks")!.Invoke(doc, new object[] { snapshot });
        Invoke(window, "RefreshMarks");

        Set(arrowStyle, "Width", savedArrowWidth);
        Set(shapeStyle, "Width", savedShapeWidth);
        Set(shapeStyle, "Color", savedShapeColor);
        Set(distanceStyle, "DistanceFactor", savedDistanceFactor);
        styles.GetType().GetMethod("SetColor")!.Invoke(styles, new object[] { "arrow", savedArrowColor });

        Select("Select");
        Pump(window);
    }

    /// <summary>
    /// 多选（P2-e3）：Shift 加选后的参数行「多个值」、改参数写回同族每条、整组拖动、
    /// 框选标注（相交即命中 / 加选 / 取消）、批量删除（一个撤销位）。
    /// </summary>
    private static void SelectionMulti(CaptureOverlayWindow window)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        object Tool(string name) => Enum.Parse(toolType, name);

        var toolbar = Field(window, "Toolbar")!;
        var paramRow = toolbar.GetType().GetField("ParamRow", Private)!.GetValue(toolbar) as Panel;
        var styles = Member(window, "_styles")!;
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var docType = doc.GetType();
        var marks = (System.Collections.IList)docType.GetProperty("Marks")!.GetValue(doc)!;
        var surface = Field(window, "Surface")!;
        var surfaceType = surface.GetType();

        var styleType = asm.GetType("LTools.Features.CapturePro.ToolStyle")!;
        var arrowType = asm.GetType("LTools.Features.CapturePro.Mark+Arrow")!;
        var markType = asm.GetType("LTools.Features.CapturePro.Mark")!;
        // 主色那一排现在是「颜色预设」（P2-f）：要有一条色块被点亮，箭头颜色得是预设色之一。
        var presetType = asm.GetType("LTools.Features.CapturePro.ColorPresets")!;
        var presetFamily = Enum.Parse(asm.GetType("LTools.Features.CapturePro.ColorPresetFamily")!, "Stroke");
        var strokePresets = ((System.Collections.Generic.IEnumerable<Color>)presetType
            .GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, new[] { presetFamily })!).ToArray();

        object Family(string family) => styles.GetType().GetMethod("ForFamily")!
            .Invoke(styles, new object[] { family })!;
        object Prop(object target, string name) =>
            target.GetType().GetProperty(name)!.GetValue(target)!;
        void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);
        T SurfaceProp<T>(string name) => (T)surfaceType
            .GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(surface)!;

        StackPanel Host(string label) =>
            paramRow?.Children.OfType<StackPanel>().FirstOrDefault(host =>
                host.Visibility == Visibility.Visible &&
                host.Children.OfType<TextBlock>().Any(text => text.Text == label));

        var selectedField = typeof(CaptureOverlayWindow).GetField("_selected", Private)!;
        int SelectedCount() => ((System.Collections.IList)selectedField.GetValue(window)!).Count;

        void Select(string name)
        {
            InvokeWith(window, "SetTool", Tool(name));
            Pump(window);
        }

        void SelectMarks(int[] indices, int primary)
        {
            InvokeWith(window, "SelectMarks", indices, primary);
            Pump(window);
        }

        IReadOnlyList<Point> PointsOf(object mark) => (IReadOnlyList<Point>)Prop(mark, "Points");
        double[] Widths() => marks.Cast<object>()
            .Select(mark => (double)Prop(Prop(mark, "Spec"), "Width")).ToArray();
        bool ShowsHandles() => (bool)typeof(CaptureOverlayWindow)
            .GetProperty("ShowsMarkHandles", Private)!.GetValue(window)!;

        // ---- 现场：这一段的标注收尾时整体还原 ----
        var before = marks.Cast<object>().ToList();
        var arrowStyle = Family("arrow");
        var savedWidth = (double)Prop(arrowStyle, "Width")!;
        var savedColor = (Color)Prop(arrowStyle, "Color")!;

        // ---- ① 造三条同类箭头（线宽 3 / 5 / 7，颜色一样），整组选中 ----
        object ArrowWidth(double width, int y, uint seed)
        {
            var clone = styleType.GetMethod("Clone")!.Invoke(arrowStyle, null)!;
            Set(clone, "Width", width);
            Set(clone, "Color", strokePresets[3]);
            var spec = styleType.GetMethod("ArrowSpecOf")!.Invoke(clone, new object[] { seed })!;
            return Activator.CreateInstance(arrowType,
                new object[] { new[] { new Point(260, y), new Point(420, y) }, spec })!;
        }

        docType.GetMethod("Add")!.Invoke(doc, new[] { ArrowWidth(3, 200, 11u) });
        docType.GetMethod("Add")!.Invoke(doc, new[] { ArrowWidth(5, 260, 12u) });
        docType.GetMethod("Add")!.Invoke(doc, new[] { ArrowWidth(7, 320, 13u) });
        Invoke(window, "RefreshMarks");

        int first = marks.Count - 3;
        Select("Select");     // 先切到选择工具（换工具会放弃选中，所以别放在选中之后）
        InvokeWith(window, "SelectMark", -1);
        SelectMarks(new[] { first, first + 1, first + 2 }, first + 2);

        Check(SelectedCount() == 3, $"整组选中三条（实际 {SelectedCount()}）");
        Check(SurfaceProp<IReadOnlyList<int>>("SelectedIndices").Count == 3, "画布拿到三条选中下标");
        Check(!ShowsHandles(), "多选不给缩放手柄（整体缩放还没做）");
        Check(!SurfaceProp<bool>("ShowHandles"), "画布同样不画把手");

        // ---- ② 参数行显示「多个值」：三条线宽不一样 ----
        Check(StepText(paramRow, "线宽") == "多个值",
            $"多选时线宽显示「多个值」（实际 {StepText(paramRow, "线宽")}）");
        var mixed = (System.Collections.IEnumerable)toolbar.GetType()
            .GetProperty("MixedParts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(toolbar)!;
        Check(mixed.Cast<object>().Any(), "工具栏记下了处于混合态的控件");

        var colorHost = Host("颜色")!;
        Check(colorHost.Children.OfType<RadioButton>().Any(chip => chip.IsChecked == true),
            "颜色三条一样 → 照常显示选中的色块（不是混合态）");

        // ---- ③ 只选一条：回到这一条自己的值 ----
        InvokeWith(window, "SelectMark", first + 1);
        Pump(window);
        Check(StepText(paramRow, "线宽") == "5", $"单选回落到那一条的线宽 5（实际 {StepText(paramRow, "线宽")}）");
        Check(((System.Collections.IEnumerable)toolbar.GetType()
            .GetProperty("MixedParts", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(toolbar)!).Cast<object>().Count() == 0, "单选没有混合态");

        // ---- ④ 多选改线宽：写回同族每一条（整批只占一个撤销位） ----
        SelectMarks(new[] { first, first + 1, first + 2 }, first + 2);
        var widthHost = Host("线宽")!;
        widthHost.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        });
        Pump(window);

        var widths = Widths().Skip(first).Take(3).ToArray();
        Check(widths.All(w => Math.Abs(w - 8) < 1e-6),
            $"改一次线宽，三条一起变 8（实际 {string.Join(" / ", widths)}）");
        Check(Math.Abs((double)Prop(arrowStyle, "Width")! - 8) < 1e-6,
            $"创建样式也跟着变 8（实际 {Prop(arrowStyle, "Width")}）");
        Check(StepText(paramRow, "线宽") == "8", $"改完之后不再显示「多个值」（实际 {StepText(paramRow, "线宽")}）");

        docType.GetMethod("Undo")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        Pump(window);
        widths = Widths().Skip(first).Take(3).ToArray();
        Check(Math.Abs(widths[0] - 3) < 1e-6 && Math.Abs(widths[1] - 5) < 1e-6 && Math.Abs(widths[2] - 7) < 1e-6,
            $"一次撤销回到三条各自的线宽（实际 {string.Join(" / ", widths)}）");

        Check(StepText(paramRow, "线宽") == "多个值",
            $"撤销后又回到「多个值」（实际 {StepText(paramRow, "线宽")}）");

        // ---- ⑤ 整组拖动：三条一起挪，一次撤销全回来 ----
        SelectMarks(new[] { first, first + 1, first + 2 }, first);
        var dragStart = new Point(260, 200);
        InvokeWith(window, "BeginMarkDrag", dragStart, first);
        var previews = surfaceType.GetProperty("Previews")!.GetValue(surface) as System.Collections.IList;
        Check(previews is { Count: 3 }, $"整组拖动有三份预览（实际 {previews?.Count ?? 0}）");
        Check(SurfaceProp<int>("SkipIndex") == -1, "整组拖动不跳过任何原件（改用预览代画）");

        Check(SurfaceProp<Rect>("MarkMarquee").IsEmpty, "没框选时画布上没有框");
        InvokeWith(window, "UpdateMarkDrag", dragStart + new Vector(40, 18));
        InvokeWith(window, "CommitMarkDrag", dragStart + new Vector(40, 18));
        Pump(window);
        for (int i = 0; i < 3; i++)
        {
            var moved = (object)marks[first + i]!;
            Check(PointsOf(moved)[0] == new Point(300, 218 + i * 60),
                $"第 {i + 1} 条跟着整组挪到 {PointsOf(moved)[0]}");
        }

        docType.GetMethod("Undo")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        Pump(window);
        Check(PointsOf(marks[first]!)[0] == new Point(260, 200), "一次撤销把整组位置退回去");

        // ---- ⑥ 框选标注（Shift + 拖 = MarqueeSelect）：相交即命中、加选、可取消 ----
        InvokeWith(window, "SelectMark", -1);
        // 直接调私有提交 / 取消时不会走鼠标松开，_drag 还停在 Marquee：Pump 期间到达的真实
        // 鼠标移动会重新进 UpdateMarkMarquee，把刚清掉的框又画回来（跑两次才撞到的假失败）。
        void ResetDrag()
        {
            var dragField = window.GetType().GetField("_drag", Private)!;
            dragField.SetValue(window, Enum.ToObject(dragField.FieldType, 0));
        }

        InvokeWith(window, "BeginMarkMarquee", new Point(240, 180));
        InvokeWith(window, "UpdateMarkMarquee", new Point(430, 340));
        Pump(window);
        Check(!SurfaceProp<Rect>("MarkMarquee").IsEmpty, "拖动中画布上有框选框");
        Check(SurfaceProp<IReadOnlyList<int>>("MarqueeHits").Count == 3, "框里三条都成了候选");

        InvokeWith(window, "CommitMarkMarquee", new Point(430, 340));
        ResetDrag();
        Pump(window);
        Check(SelectedCount() == 3, $"松手后框里的三条都选中（实际 {SelectedCount()}）");
        Check(SurfaceProp<Rect>("MarkMarquee").IsEmpty, "提交后框选框消失");

        // 加选：原有选区 + 不碰任何东西的空框 = 原样保留
        InvokeWith(window, "BeginMarkMarquee", new Point(700, 700));
        InvokeWith(window, "UpdateMarkMarquee", new Point(710, 710));
        InvokeWith(window, "CommitMarkMarquee", new Point(710, 710));
        ResetDrag();
        Pump(window);
        Check(SelectedCount() == 3, "加选：空框不把已有选区清掉");

        // 取消（Esc / 右键）：框选框清掉，选区不动
        InvokeWith(window, "BeginMarkMarquee", new Point(240, 180));
        InvokeWith(window, "UpdateMarkMarquee", new Point(430, 340));
        InvokeWith(window, "CancelDrag");
        Pump(window);
        Check(SurfaceProp<Rect>("MarkMarquee").IsEmpty, "取消后框选框消失");
        Check(SurfaceProp<IReadOnlyList<int>>("MarqueeHits").Count == 0, "取消后候选清单清空");
        Check(SelectedCount() == 3, "取消框选不动已有选区");

        // ---- ⑦ 右键：清掉标注选择（不退出覆盖层） ----
        Invoke(window, "StepBack");
        Pump(window);
        Check(SelectedCount() == 0, $"右键清掉多选（实际 {SelectedCount()}）");
        Check(StepText(paramRow, "线宽") is null or "4", "参数行交还给工具族");

        // ---- ⑧ 批量删除：一次删三条，一次撤销全回来 ----
        SelectMarks(new[] { first, first + 1, first + 2 }, first + 1);
        int beforeDelete = marks.Count;
        Invoke(window, "DeleteSelectedMark");
        Pump(window);
        Check(marks.Count == beforeDelete - 3, $"一次删掉三条（{beforeDelete} → {marks.Count}）");
        Check(SelectedCount() == 0, "删完清掉选区");

        docType.GetMethod("Undo")!.Invoke(doc, null);
        Invoke(window, "RefreshMarks");
        Pump(window);
        Check(marks.Count == beforeDelete, $"一次撤销把三条都找回来（实际 {marks.Count}）");

        // ---- 收尾：还原文档与箭头族样式 ----
        InvokeWith(window, "SelectMark", -1);
        var snapshot = Array.CreateInstance(markType, before.Count);
        for (int i = 0; i < before.Count; i++) snapshot.SetValue(before[i], i);
        docType.GetMethod("ResetMarks")!.Invoke(doc, new object[] { snapshot });
        Invoke(window, "RefreshMarks");

        Set(arrowStyle, "Width", savedWidth);
        styles.GetType().GetMethod("SetColor")!.Invoke(styles, new object[] { "arrow", savedColor });
        Select("Select");
        Pump(window);
    }

    /// <summary>参数条上「线宽」那一格的数值文本（没有就 null）。</summary>
    private static string? WidthText(Panel? row) => StepText(row, "线宽");
    /// <summary>参数条上某一格的数值 / 文本（按左边那个灰标签找；没有就 null）。</summary>
    private static string? StepText(Panel? row, string label)
    {
        if (row == null) return null;
        foreach (var host in row.Children.OfType<StackPanel>())
        {
            if (host.Visibility != Visibility.Visible) continue;
            if (!host.Children.OfType<TextBlock>().Any(text => text.Text == label)) continue;
            return host.Children.OfType<TextBlock>().Last().Text;
        }
        return null;
    }

    private static void ParamRow(CaptureOverlayWindow window, FrameworkElement? toolbar)
    {
        Check(toolbar != null, "参数行检查：工具栏在");
        if (toolbar == null) return;

        var paramRow = toolbar.GetType().GetField("ParamRow", Private)?.GetValue(toolbar) as Panel;
        Check(paramRow != null, "参数行（ParamRow）存在");
        if (paramRow == null) return;

        int Visible() => paramRow.Children.OfType<FrameworkElement>().Count(c => c.Visibility == Visibility.Visible);
        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        void Select(string name)
        {
            InvokeWith(window, "SetTool", Enum.Parse(toolType, name));
            Pump(window);
        }

        List<string> Chips() => paramRow.Children.OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<RadioButton>())
            .Select(button => button.Content?.ToString() ?? "")
            .ToList();

        // 序号（上游 buildSequenceNumberFamily）：色 / 徽标 / 写法 / 下一个号码 / 字号 / 字体 /
        // 线宽 / 填充色 / 填充方式 = 9 格
        Select("Serial");
        Check(Visible() == 9, $"序号参数行是 9 格（颜色 / 徽标 / 写法 / 序号 / 字号 / 字体 / 线宽 / 填充色 / 填充），实际 {Visible()}");
        var serialChips = Chips();
        Check(serialChips.Contains("空心圆") && serialChips.Contains("实心圆") &&
              serialChips.Contains("空心方") && serialChips.Contains("实心方") && serialChips.Contains("圆圈"),
            "序号有五种徽标胶囊：" + string.Join(" / ", serialChips));
        Check(serialChips.Contains("1") && serialChips.Contains("I") && serialChips.Contains("a") &&
              serialChips.Contains("A") && serialChips.Contains("一"),
            "序号有五种写法胶囊：1 / I / a / A / 一");

        // 「序号」那一格显示的是**下一个要用的号码**，落一个自动 +1（上游参数行语义）
        var numberHost = paramRow.Children.OfType<StackPanel>().First(panel =>
            panel.Visibility == Visibility.Visible &&
            panel.Children.OfType<TextBlock>().Any(text => text.Text == "序号"));
        var numberBox = numberHost.Children.OfType<TextBox>().First();
        Check(numberBox.Text == "1", $"序号参数行显示下一个号码 1（实际 {numberBox.Text}）");
        InvokeWith(window, "AddSerialAt", new Point(420, 320));
        Pump(window);
        Check(numberBox.Text == "2", $"落一个序号后参数行自动 +1（实际 {numberBox.Text}）");
        var liveDoc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        liveDoc.GetType().GetMethod("Undo")!.Invoke(liveDoc, null);
        Invoke(window, "RefreshMarks");

        // 放大镜（上游 buildMagnifierFamily）：形状 / 色 / 线宽 / 倍率 / 连接 / 源轮廓 /
        // 箭头比例 / 端点 / 圆角 = 9 格
        Select("Magnifier");
        Check(Visible() == 9,
            $"放大镜参数行是 9 格（形状 / 颜色 / 线宽 / 倍率 / 连接 / 源轮廓 / 箭头比例 / 端点 / 圆角），实际 {Visible()}");
        var magnifierChips = Chips();
        Check(magnifierChips.Contains("无") && magnifierChips.Contains("引导线") && magnifierChips.Contains("渐变"),
            "放大镜有连接方式胶囊：" + string.Join(" / ", magnifierChips));
        Check(paramRow.Children.OfType<StackPanel>().Any(panel =>
                panel.Visibility == Visibility.Visible &&
                panel.Children.OfType<TextBlock>().Any(text => text.Text == "源轮廓")),
            "放大镜有「源轮廓」开关");
        Check(StepText(paramRow, "倍率") == "2×", $"放大镜默认倍率 2×（实际 {StepText(paramRow, "倍率")}）");

        // 水印（上游 buildWatermarkFamily）：色 / 文字 / 字号 / 字体 / 模板 / 角度 / 间距 / 不透明度 = 8 格
        Select("Watermark");
        Check(Visible() == 8,
            $"水印参数行是 8 格（颜色 / 文字 / 字号 / 字体 / 模板 / 角度 / 间距 / 不透明度），实际 {Visible()}");
        Check(paramRow.Children.OfType<StackPanel>().Any(panel =>
                panel.Visibility == Visibility.Visible &&
                panel.Children.OfType<Button>().Any(button => (button.Content?.ToString() ?? "") == "模板")),
            "水印有「模板」按钮（点名字套用 / 右键增删）");
        Check(paramRow.Children.OfType<StackPanel>()
                .SelectMany(panel => panel.Children.OfType<RadioButton>())
                .Any(button => (button.Content?.ToString() ?? "") == "16"),
            "水印字号有 12 / 16 / 24 / 30 预设");
        Check(StepText(paramRow, "角度") == "30°", $"水印默认角度 30°（实际 {StepText(paramRow, "角度")}）");
        Check(StepText(paramRow, "间距") == "56", $"水印默认间距 56（实际 {StepText(paramRow, "间距")}）");
        Check(StepText(paramRow, "不透明度") == "16%", $"水印默认不透明度 16%（实际 {StepText(paramRow, "不透明度")}）");

        // 箭头（上游 buildArrowFamily）：色 / 线宽 / 线型 / 笔触 / 类型 / 外描边宽 / 外描边色 /
        // 箭头比例 / 起点 / 杆型 / 终点 = 11 格
        Select("Arrow");
        Check(Visible() == 11,
            $"箭头参数行是 11 格（颜色 / 线宽 / 线型 / 笔触 / 类型 / 外描边宽 / 外描边色 / 箭头比例 / 起点 / 杆型 / 终点），实际 {Visible()}");
        var arrowChips = Chips();
        Check(arrowChips.Contains("直线") && arrowChips.Contains("曲线") && arrowChips.Contains("折线"),
            "箭头有三种类型胶囊：" + string.Join(" / ", arrowChips));
        Check(arrowChips.Contains("等宽") && arrowChips.Contains("渐宽"),
            "箭头有杆型胶囊：等宽 / 渐宽");
        Check(StepText(paramRow, "箭头比例") == "1.0×",
            $"箭头比例默认 1.0×（实际 {StepText(paramRow, "箭头比例")}）");
        Check(StepText(paramRow, "外描边") == "0", $"外描边默认 0（实际 {StepText(paramRow, "外描边")}）");
        var arrowButtons = paramRow.Children.OfType<StackPanel>()
            .Where(panel => panel.Visibility == Visibility.Visible)
            .SelectMany(panel => panel.Children.OfType<Button>()
                .Select(button => (Label: panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text,
                    Text: button.Content?.ToString() ?? "")))
            .ToList();
        Check(arrowButtons.Any(b => b.Label == "起点" && b.Text == "无"), "箭头起点端点默认「无」");
        Check(arrowButtons.Any(b => b.Label == "终点" && b.Text == "箭头"), "箭头终点端点默认「箭头」");

        // 滤镜：类型 + 强度 两格（马赛克的块大小不该出现在这里）
        Select("Filter");
        Check(Visible() == 2, $"滤镜参数行是 2 格（类型 / 强度），实际 {Visible()}");
        var filterChips = Chips();
        Check(filterChips.Contains("模糊") && filterChips.Contains("灰度") && filterChips.Contains("反相")
              && filterChips.Contains("浮雕") && filterChips.Contains("亮度")
              && filterChips.Contains("智能"),
            "滤镜类型胶囊齐全（含智能擦除）：" + string.Join(" / ", filterChips));

        // 滚轮微调 + 点数值复位（线宽那格）
        Select("Pen");
        var style = Member(window, "_style")!;
        var width = style.GetType().GetProperty("Width")!;
        width.SetValue(style, 10d);
        toolbar.GetType().GetMethod("RefreshValues")!.Invoke(toolbar, null);

        var widthHost = paramRow.Children.OfType<StackPanel>().First(panel =>
            panel.Visibility == Visibility.Visible &&
            panel.Children.OfType<TextBlock>().Any(text => text.Text == "线宽"));
        widthHost.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
        });
        Check(Math.Abs((double)width.GetValue(style)! - 11) < 1e-6,
            $"滚轮 +1：线宽 {(double)width.GetValue(style)!}");

        var valueText = widthHost.Children.OfType<TextBlock>().First(text => text.Text == "11");
        valueText.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent,
        });
        Check(Math.Abs((double)width.GetValue(style)! - 4) < 1e-6,
            $"点数值复位：线宽 {(double)width.GetValue(style)!}");

        // 测距：7 格（色 / 线宽 / 实测值 / 单位 / 小数位 / 端点比例 / 端点样式）
        Select("Distance");
        Check(Visible() == 7, $"测距参数行是 7 格（颜色 / 线宽 / 实测值 / 单位 / 小数位 / 端点比例 / 端点样式），实际 {Visible()}");
        var distanceChips = Chips();
        Check(distanceChips.Contains("cm") && distanceChips.Contains("μm") && distanceChips.Contains("px"),
            "测距有单位胶囊：" + string.Join(" / ", distanceChips));
        Check(distanceChips.Contains("整数") && distanceChips.Contains("3 位"), "测距有小数位胶囊");
        Check(paramRow.Children.OfType<StackPanel>()
                .Any(panel => panel.Visibility == Visibility.Visible &&
                              panel.Children.OfType<Button>().Any(button => (button.Content?.ToString() ?? "") == "横杠")),
            "测距有端点样式按钮（默认横杠）");

        var measureHost = paramRow.Children.OfType<StackPanel>().First(panel =>
            panel.Visibility == Visibility.Visible &&
            panel.Children.OfType<TextBlock>().Any(text => text.Text == "实测值"));
        var measureBox = measureHost.Children.OfType<TextBox>().First();
        Check(!measureBox.IsEnabled, "没选中测距时「实测值」置灰");
        InvokeWith(toolbar, "SetMeasuredDistance", 150d);
        Check(measureBox.IsEnabled, "选中一条测距后「实测值」可用");
        Check(measureBox.Text == "150", $"实测值显示当前长度 × 系数（实际 {measureBox.Text}）");
        InvokeWith(toolbar, "SetMeasuredDistance", 0d);

        // 测角：5 格（色 / 线宽 / 类型 / 单位 / 小数位）
        Select("Angle");
        Check(Visible() == 5, $"测角参数行是 5 格（颜色 / 线宽 / 类型 / 单位 / 小数位），实际 {Visible()}");
        var angleChips = Chips();
        Check(angleChips.Contains("有向") && angleChips.Contains("内角"), "测角有类型胶囊：有向 / 内角");
        Check(angleChips.Contains("度") && angleChips.Contains("弧度"), "测角有单位胶囊：度 / 弧度");

        // 橡皮：模式一格；换成画笔橡皮后多出宽度一格，且默认笔头是 30
        Select("Eraser");
        Check(Visible() == 1, $"橡皮参数行只有模式一格，实际 {Visible()}");
        var eraserChips = Chips();
        Check(eraserChips.Contains("橡皮") && eraserChips.Contains("矩形橡皮") && eraserChips.Contains("画笔橡皮"),
            "橡皮模式胶囊齐全：" + string.Join(" / ", eraserChips));

        var brushChip = paramRow.Children.OfType<StackPanel>()
            .SelectMany(panel => panel.Children.OfType<RadioButton>())
            .First(button => (button.Content?.ToString() ?? "") == "画笔橡皮");
        brushChip.IsChecked = true;
        Pump(window);
        Check(toolbar.GetType().GetProperty("CurrentTool", Private)?.GetValue(toolbar)?.ToString() == "BrushEraser",
            "点「画笔橡皮」胶囊 = 换工具");
        Check(Visible() == 2, $"画笔橡皮参数行是 2 格（模式 / 宽度），实际 {Visible()}");
        Check(StepText(paramRow, "线宽") == "30", $"画笔橡皮默认笔头 30（实际 {StepText(paramRow, "线宽")}）");

        Select("Serial");
        Pump(window);
        Save(Render(window), Path.Combine(_outDir, "param-row.png"));
    }
    /// <summary>
    /// 采集历史翻页：<c>,</c> / <c>.</c> 就地换底图（不关覆盖层），选区框满整张、标注清空；
    /// Esc / 右键回到刚才那次截图（底图 / 标注 / 选区都还回来）。
    /// </summary>
    private static void HistoryPaging(CaptureOverlayWindow window)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;
        var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
        var docType = doc.GetType();

        catalogType.GetMethod("Clear")!.Invoke(null, null);

        static BitmapSource Solid(int w, int h, Color color)
        {
            var pixels = new byte[w * h * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 0xFF;
            }
            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
            image.Freeze();
            return image;
        }

        object Make(int w, int h, Color color)
        {
            var entry = catalogType.GetMethod("Create")!.Invoke(null, new object[] { w, h, "shot", "复制", null })!;
            catalogType.GetMethod("SaveAssets")!.Invoke(null, new object[] { entry, Solid(w, h, color) });
            return entry;
        }

        Make(320, 200, Colors.DarkOrange);            // 更早
        Make(640, 360, Colors.SteelBlue);             // 更新（索引 0）

        SetSelection(window, new Rect(200, 150, 600, 400));
        Invoke(window, "SyncArea");

        var baseBefore = docType.GetProperty("Base")!.GetValue(doc);
        var marksProp = docType.GetProperty("Marks")!;
        var browsing = typeof(CaptureOverlayWindow).GetProperty("BrowsingHistory", Private)!;

        int MarksIn(object target) => (marksProp.GetValue(target) as System.Collections.IList)?.Count ?? -1;
        int MarksNow() => MarksIn(doc);
        int BaseWidth() => ((BitmapSource)docType.GetProperty("Base")!.GetValue(doc)!).PixelWidth;

        int marksBefore = MarksNow();

        // . = 更早：第一次落在最新那条（640×360）
        InvokeWith(window, "StepHistory", 1);
        Pump(window);
        Check((bool)browsing.GetValue(window)!, "翻页后进入历史浏览态");
        Check(MarksNow() == 0, "换底图清空标注");
        Check(!(bool)docType.GetProperty("CanUndo")!.GetValue(doc)!, "换底图清空撤销栈");
        Check(BaseWidth() == 640, "先落在最新那张（640 宽）");
        Check((int)docType.GetMethod("NextSerial")!.Invoke(doc, null)! == 1, "历史图上序号从 1 重新开始");

        var canvas = (Rect)docType.GetProperty("Canvas")!.GetValue(doc)!;
        var historySelField = typeof(CaptureOverlayWindow).GetField("_sel", Private)!;
        var selection = (Rect?)historySelField.GetValue(window);
        Check(selection is { } s && Math.Abs(s.Width - canvas.Width) < 0.6 && Math.Abs(s.Height - canvas.Height) < 0.6,
            $"选区框满整张历史图：{selection} vs {canvas}");
        Check(Math.Abs(canvas.X - (window.ActualWidth - canvas.Width) / 2) < 1.0,
            $"历史图摆在窗口中间：{canvas.X:0.#}（窗口 {window.ActualWidth:0.#}）");

        var hint = (TextBlock)Field(window, "HintText");
        Check(hint.Text.Contains("历史 1/2"), "顶部提示写出第几张：" + hint.Text);

        // 再按 . 到更早那张（320×200）
        InvokeWith(window, "StepHistory", 1);
        Pump(window);
        Check(BaseWidth() == 320, "再往前翻到 320 宽那张");

        // 到最早一张了：按键不再变，但仍是历史态
        InvokeWith(window, "StepHistory", 1);
        Pump(window);
        Check(BaseWidth() == 320, "到头了就停在最早那张");

        // , 回到更新那张
        InvokeWith(window, "StepHistory", -1);
        Pump(window);
        Check(BaseWidth() == 640, ", 回到更新那张");

        // 右键：退回刚才那次截图，底图 / 标注 / 选区都回来
        Invoke(window, "StepBack");
        Pump(window);
        Check(!(bool)browsing.GetValue(window)!, "退回实时截图");
        Check(ReferenceEquals(baseBefore, docType.GetProperty("Base")!.GetValue(doc)), "底图换回原来那张");
        Check(MarksNow() == marksBefore, $"标注原样放回（{MarksNow()} vs {marksBefore}）");
        Check(historySelField.GetValue(window) is Rect r && Math.Abs(r.Width - 600) < 0.6 && Math.Abs(r.Height - 400) < 0.6,
            "选区也回到刚才那块");

        catalogType.GetMethod("Clear")!.Invoke(null, null);
    }

    /// <summary>
    /// 保存对话框：格式 / 尺寸胶囊、扩展名与成品尺寸跟着走、质量只对 JPEG 有效、
    /// 文件名带扩展名不会存成 <c>x.jpg.jpg</c>。
    /// </summary>
    private static void SaveDialog()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var windowType = asm.GetType("LTools.Views.CapturePro.CaptureSaveWindow")!;

        var image = BuildShot(400, 200);
        var window = NewSaveWindow(windowType, image, null);
        window.Show();
        Pump(window);

        Check(((TextBox)Field(window, "NameBox")).Text.StartsWith("截图_"),
            "没给建议名时默认文件名是时间戳：" + ((TextBox)Field(window, "NameBox")).Text);

        var formatRow = (StackPanel)Field(window, "FormatRow");
        var scaleRow = (StackPanel)Field(window, "ScaleRow");
        static List<string> Chips(StackPanel row) =>
            row.Children.OfType<RadioButton>().Select(b => b.Content?.ToString() ?? "").ToList();

        Check(Chips(formatRow).SequenceEqual(new[] { "PNG", "JPEG", "BMP", "TIFF", "PDF" }),
            "五种格式胶囊：" + string.Join(" / ", Chips(formatRow)));
        Check(Chips(scaleRow).SequenceEqual(new[] { "100%", "75%", "50%", "25%" }),
            "四档尺寸胶囊：" + string.Join(" / ", Chips(scaleRow)));

        var meta = (TextBlock)Field(window, "MetaText");
        var extension = (TextBlock)Field(window, "ExtensionText");
        var quality = (TextBox)Field(window, "QualityBox");

        Check(meta.Text.Contains("400 × 200 px"), "成品尺寸先写原尺寸：" + meta.Text);
        Check(extension.Text == ".png", $"默认 PNG 扩展名（{extension.Text}）");
        Check(!quality.IsEnabled, "非 JPEG 时质量框置灰");
        Check(ReferenceEquals(((Image)Field(window, "Preview")).Source, image), "预览就是待保存的图");

        ((RadioButton)formatRow.Children[1]).IsChecked = true;       // JPEG
        Pump(window);
        Check(extension.Text == ".jpg", "切 JPEG 后扩展名跟着变：" + extension.Text);
        Check(quality.IsEnabled, "JPEG 能调质量");

        ((RadioButton)formatRow.Children[4]).IsChecked = true;       // PDF
        Pump(window);
        Check(extension.Text == ".pdf", "切 PDF 后扩展名变成 .pdf：" + extension.Text);
        Check(quality.IsEnabled, "PDF 也能调质量（决定内嵌 JPEG 的质量）");

        // 后面的写盘断言按 JPEG 走，切回来
        ((RadioButton)formatRow.Children[1]).IsChecked = true;
        Pump(window);
        Check(extension.Text == ".jpg", "切回 JPEG 扩展名又变回 .jpg：" + extension.Text);

        ((RadioButton)scaleRow.Children[2]).IsChecked = true;        // 50%
        Pump(window);
        Check(meta.Text.Contains("→ 200 × 100 px"), "50% 的成品尺寸：" + meta.Text);

        Save(Render(window), Path.Combine(_outDir, "save-dialog.png"));

        // 文件名空：给提示、不写文件、窗口不关
        string folder = Path.Combine(_outDir, "save-dialog");
        Directory.CreateDirectory(folder);
        ((TextBox)Field(window, "FolderBox")).Text = folder;
        ((TextBox)Field(window, "NameBox")).Text = "";
        ((Button)Field(window, "SaveBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        var error = (TextBlock)Field(window, "ErrorText");
        Check(error.Visibility == Visibility.Visible && error.Text.Contains("文件名"),
            "文件名空时说明原因：" + error.Text);
        Check(window.IsVisible, "校验不过时不关窗");

        // 正常写：文件名自己带了 .jpg 也不会重复
        string target = Path.Combine(folder, "smoke.jpg");
        if (File.Exists(target)) File.Delete(target);
        ((TextBox)Field(window, "NameBox")).Text = "smoke.jpg";
        ((Button)Field(window, "SaveBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);

        Check(File.Exists(target), "按扩展名写下去：" + target);
        if (File.Exists(target))
        {
            var bytes = File.ReadAllBytes(target);
            Check(bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8, "写下的是 JPEG");
            Check((bool)windowType.GetProperty("Saved")!.GetValue(window)!, "Saved = true");
            Check((string)windowType.GetProperty("SavedPath")!.GetValue(window)! == target, "SavedPath 是那个文件");
            File.Delete(target);
        }

        try { Directory.Delete(folder, true); } catch (IOException) { }

        // 建议文件名（图片编辑窗「另存为」用原图名字）：有就预填
        var suggested = NewSaveWindow(windowType, image, "demo-shot");
        suggested.Show();
        Pump(suggested);
        string suggestedName = ((TextBox)Field(suggested, "NameBox")).Text;
        Check(suggestedName == "demo-shot", "给了建议名就预填：" + suggestedName);
        suggested.Close();
    }

    private static void InvokeWith(object target, string method, params object[] args)
    {
        var type = target.GetType();
        var info = args.Length == 0
            ? type.GetMethod(method, Private, null, Type.EmptyTypes, null)
            : type.GetMethod(method, Private);
        info!.Invoke(target, args);
    }

    /// <summary>调一个返回值的方法（右键菜单这类「建出来给我看」的私有方法）。</summary>
    private static object InvokeValue(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Private)!.Invoke(target, args);

    /// <summary>菜单里所有标题（含子菜单），用来断言「收起来的工具」列得出某一项。</summary>
    private static List<string> Headers(ItemCollection items)
    {
        var texts = new List<string>();
        foreach (var item in items)
            if (item is MenuItem menu)
            {
                texts.Add(menu.Header?.ToString() ?? "");
                texts.AddRange(Headers(menu.Items));
            }
        return texts;
    }

    /// <summary>数一数图里有多少像素接近某个颜色（断言「这条标注真的画进了导出」）。</summary>
    private static int CountColor(BitmapSource image, Color want, int tolerance)
    {
        var conv = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        var buffer = new byte[w * h * 4];
        conv.CopyPixels(buffer, w * 4, 0);

        int count = 0;
        for (int i = 0; i + 3 < buffer.Length; i += 4)
        {
            if (buffer[i + 3] < 200) continue;                            // 透明的不算
            if (Math.Abs(buffer[i] - want.B) > tolerance) continue;       // BGRA
            if (Math.Abs(buffer[i + 1] - want.G) > tolerance) continue;
            if (Math.Abs(buffer[i + 2] - want.R) > tolerance) continue;
            count++;
        }
        return count;
    }

    /// <summary>
    /// P3-d 智能选区：真窗口上的「悬停 → 预选 → 滚轮换层 → Tab 切目标 → 单击确认 → 拖框 → 右键退出」。
    /// </summary>
    /// <remarks>
    /// 命中路径由这里注入（真实的 EnumWindows / UIA 在无人值守机器上不可控），
    /// 但「选哪一层 / 过渡插值 / 收界面 / 信息条只读 / 确认与手框」全部走产品代码。
    /// </remarks>
    private static void SmartSelectionSection()
    {
        const int width = 1200, height = 700;
        var shot = BuildShot(width, height);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, width, height));
        window.Show();
        Pump(window);

        double scale = width / Math.Max(1, window.Width);

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var snapType = asm.GetType("LTools.Views.SnapBox")!;
        var modeType = asm.GetType("LTools.Features.CapturePro.SmartSelectionMode")!;
        var targetType = asm.GetType("LTools.Features.CapturePro.SmartSelectionTarget")!;
        var animType = asm.GetType("LTools.Features.CapturePro.SelectionTransition")!;

        object Prop(object o, string name) => o.GetType()
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;
        object Call(object o, string name, params object[] args) => o.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(o, args);

        var smart = Field(window, "_smart")!;
        var fx = Field(window, "_smartFx")!;
        var surface = Field(window, "Surface")!;
        var toolbar = (FrameworkElement)Field(window, "Toolbar")!;
        var shapeBar = (FrameworkElement)Field(window, "ShapeBar")!;
        var selBar = (FrameworkElement)Field(window, "SelBar")!;
        var hint = (TextBlock)Field(window, "HintText")!;
        var wText = (TextBlock)Field(window, "SelWText")!;
        var hText = (TextBlock)Field(window, "SelHText")!;

        // 档位与动画固定成「子元素 + 柔和」，不读本机配置（CI 上的 settings 不可控）
        Call(smart, "BeginSession", Enum.Parse(modeType, "WindowSubElement"));
        Call(fx, "SetAnimation", Enum.Parse(animType, "Soft"), 0L);
        Check(Prop(smart, "Mode")!.ToString() == "WindowSubElement", "档位固定成「窗口子元素」");

        // 三块候选：整窗 / 侧栏 / 按钮（物理像素，按面积升序排完就是「最深 → 最外层」）
        var boxWindow = new System.Drawing.Rectangle(60, 40, 1080, 620);
        var boxPanel = new System.Drawing.Rectangle(120, 90, 640, 420);
        var boxButton = new System.Drawing.Rectangle(180, 140, 240, 120);
        object Box(System.Drawing.Rectangle r, string label) =>
            Activator.CreateInstance(snapType, new object[] { r, label, IntPtr.Zero })!;

        var targets = (System.Collections.IList)typeof(CaptureOverlayWindow)
            .GetField("_windowTargets", Private)!.GetValue(window)!;
        targets.Clear();
        targets.Add(Box(boxWindow, "编辑器窗口"));
        targets.Add(Box(boxPanel, "侧栏"));
        targets.Add(Box(boxButton, "按钮"));

        // 光标钉在按钮里：悬停重算 / Tab 重新命中的时候结果还是这三块
        var area = (System.Drawing.Rectangle)Field(window, "_area");
        int cx = area.X + 300, cy = area.Y + 200;
        SetCursorPos(cx, cy);

        var hwnd = (IntPtr)Field(window, "_hwnd");
        var topAt = asm.GetType("LTools.Views.ScreenSnapTargets")!.GetMethod(
            "TopWindowAtPoint", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        var top = (IntPtr)topAt.Invoke(null, new object[] { hwnd, new System.Drawing.Point(cx, cy) })!;

        // 把那个顶层窗口的 UIA 子树预先钉成空：真实 UIA 是异步的，跑起来会打乱候选
        var trees = (System.Collections.IDictionary)Field(window, "_uiaTrees")!;
        trees.Clear();
        trees[top] = Activator.CreateInstance(typeof(List<>).MakeGenericType(snapType))!;
        SetField(typeof(CaptureOverlayWindow), window, "_uiaPending", IntPtr.Zero);
        ((System.Collections.IList)Field(window, "_uiaTargets")!).Clear();

        var dip = new Point(300 / scale, 200 / scale);
        var screenAt = (System.Drawing.Point)InvokeValue(window, "ToScreen", dip);
        Check(Math.Abs(screenAt.X - cx) <= 1 && Math.Abs(screenAt.Y - cy) <= 1,
            $"注入点的 DIP ↔ 屏幕往返一致：DIP({dip.X:0},{dip.Y:0}) → 屏幕({screenAt.X},{screenAt.Y})");

        Rect Dip(System.Drawing.Rectangle r) => (Rect)InvokeValue(window, "ToDipRect", r);
        Rect Selection() => (Rect)Prop(surface, "Selection");
        void Hover() => InvokeWith(window, "UpdateHover", dip, false);

        // ============ 悬停 = 预选（最深的那一层） ============
        Hover();
        Pump(window);

        Check((bool)Prop(window, "SmartPreviewActive"), "光标停在候选上立刻出现预选（智能选区）");
        Check(Prop(smart, "HitCount")!.Equals(3), $"命中路径是三层（实际 {Prop(smart, "HitCount")}）");
        Check(Prop(smart, "Index")!.Equals(0), "默认落到最深的那一层");
        Check(Prop(smart, "CurrentSelection")!.Equals(Dip(boxButton)), "预选几何 = 按钮那块");
        Check((string)Prop(window, "SmartTargetLabel") == "按钮", "预选标签取到「按钮」");
        Check(toolbar.Visibility != Visibility.Visible, "预选时不摆主工具条");
        Check(shapeBar.Visibility != Visibility.Visible, "预选时不摆形状条");
        Check(hint.Text.Contains("智能选区") && hint.Text.Contains("子元素") && hint.Text.Contains("单击确认"),
            "提示写明「智能选区 · 子元素 · 单击确认」：" + hint.Text);

        // 信息条在预选态是只读的（上游 selectionEditable = false）
        Check(selBar.Visibility == Visibility.Visible, "预选时出现选区信息条");
        // 信息条报的是「含两端像素」的物理尺寸：DIP → 像素再 +1（和框选取图同一口径）
        int expectW = boxButton.Width + 1;
        int expectH = boxButton.Height + 1;
        bool BarSize(int w, int h) =>
            int.TryParse(wText.Text, out int gw) && int.TryParse(hText.Text, out int gh) &&
            Math.Abs(gw - w) <= 1 && Math.Abs(gh - h) <= 1;
        Check(BarSize(expectW, expectH),
            $"信息条按物理像素报预选尺寸（≈{expectW}×{expectH}）：{wText.Text}×{hText.Text}");
        var before = Selection();
        InvokeWith(window, "HandleSelectionBarClick", (object)wText);
        Check(Selection() == before && (bool)Prop(window, "SmartPreviewActive"),
            "预选态点信息条不改参数、也不取消预选");

        Save(Render(window), Path.Combine(_outDir, "smart-preview.png"));

        // ============ 滚轮 = 换层级（往上 = 外层） ============
        // 事件是同步派发的：先断言「刚开始换」，再喂帧跑完（8ms 计时器只在这段动画里跑）
        window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        });

        Check(Prop(smart, "Index")!.Equals(1) && Prop(smart, "CurrentSelection")!.Equals(Dip(boxPanel)),
            "滚轮往上换到父级（侧栏）");
        Check((bool)Prop(fx, "IsRunning"), "换层级走过渡动画，不直接跳");
        Check(Selection() == Dip(boxButton), "动画第一拍还停在原来那块");

        // 手动喂一帧把动画跑完（真实 8ms 计时器按绝对时间走，这里同一套几何）
        long last = (long)Field(fx, "_lastAdvancedAt");
        Call(fx, "Advance", last + 500);
        Invoke(window, "RepaintSmartFrame");
        Check(Prop(fx, "Displayed")!.Equals(Dip(boxPanel)), "过渡结束精确落在新目标上");
        Check(Selection() == Dip(boxPanel), "画布上的选区跟着过渡走到侧栏");
        Check(BarSize(boxPanel.Width + 1, boxPanel.Height + 1),
            "信息条跟着换到侧栏的宽度：" + wText.Text);
        Check(!(bool)Call(fx, "Advance", last), "时间戳不许倒放（不重复发帧）");

        // 再往下滚一格回到按钮
        window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
        });
        Pump(window);
        Check(Prop(smart, "Index")!.Equals(0) && Prop(smart, "CurrentSelection")!.Equals(Dip(boxButton)),
            "滚轮往下回到更细的子元素");
        last = (long)Field(fx, "_lastAdvancedAt");
        Call(fx, "Advance", last + 500);
        Invoke(window, "RepaintSmartFrame");

        // ============ Tab = 整窗 ↔ 子元素 ============
        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
            Environment.TickCount, Key.Tab)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        Pump(window);

        Check(Prop(smart, "Target")!.ToString() == "Window", "Tab 切到整窗");
        Check(Prop(smart, "Index")!.Equals(2) && Prop(smart, "CurrentSelection")!.Equals(Dip(boxWindow)),
            "整窗模式锁在最外层");
        last = (long)Field(fx, "_lastAdvancedAt");
        Call(fx, "Advance", last + 500);
        Invoke(window, "RepaintSmartFrame");
        Check(Selection() == Dip(boxWindow), "画布跟着 Tab 换成整窗（预选几何与模型一致）");
        Check(hint.Text.Contains("整窗") && hint.Text.Contains("Tab 切到子元素"),
            "提示改成整窗 / 下一次 Tab 回子元素：" + hint.Text);

        Save(Render(window), Path.Combine(_outDir, "smart-window.png"));

        window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
            Environment.TickCount, Key.Tab)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        });
        Pump(window);
        Check(Prop(smart, "Target")!.ToString() == "WindowSubElement" &&
              Prop(smart, "Index")!.Equals(0) && Prop(smart, "CurrentSelection")!.Equals(Dip(boxButton)),
            "再按一次 Tab 回到子元素的最深一层");
        last = (long)Field(fx, "_lastAdvancedAt");
        Call(fx, "Advance", last + 500);
        Invoke(window, "RepaintSmartFrame");

        // ============ 单击 = 确认这一块（真鼠标：覆盖层自己走 MouseDown / MouseUp） ============
        void ClearSnap()
        {
            ((System.Collections.IList)Field(window, "_snapXs")!).Clear();
            ((System.Collections.IList)Field(window, "_snapYs")!).Clear();
        }

        void Point(int x, int y, int settleMs = 140)
        {
            SetCursorPos(x, y);
            Thread.Sleep(settleMs);
            Pump(window);
        }

        void Input(uint flags) => mouse_event(flags, 0, 0, 0, UIntPtr.Zero);

        Point(cx, cy, 300);
        Input(LeftDown);
        Thread.Sleep(150);
        Pump(window);
        Check(Field(window, "_drag")!.ToString() == "SmartPress", "预选里按下先记账（不立刻改装）");
        Check((bool)Prop(smart, "PressActive"), "按下状态交给模型（细化 / 确认都冻结在这一刻）");

        Input(LeftUp);
        Thread.Sleep(200);
        Pump(window);
        Check(!(bool)Prop(window, "SmartPreviewActive"), "松手还在预选里 = 确认，预选结束");
        Check(Prop(smart, "HitCount")!.Equals(0), "确认后暂停智能选区（命中路径清空）");
        Check(Selection() == Dip(boxButton), "确认下来的就是刚才预选的那一块");
        Check(toolbar.Visibility == Visibility.Visible, "确认之后主工具条回来");
        Check(!hint.Text.Contains("智能选区") && hint.Text.Contains("识别"),
            "提示从智能选区切回普通框选那一句：" + hint.Text);

        // 有选区时不再预选（要右键取消选区才重新开始认）
        Hover();
        Pump(window);
        Check(!(bool)Prop(window, "SmartPreviewActive") && Selection() == Dip(boxButton),
            "已有确认过的选区时不再自动预选");

        // ============ 换工具 = 预选落成普通选区（智能选区暂停） ============
        Invoke(window, "ClearSelection");
        Hover();
        Pump(window);
        Check((bool)Prop(window, "SmartPreviewActive"), "清掉选区后重新出现预选");

        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Shape"));
        Pump(window);
        Check(!(bool)Prop(window, "SmartPreviewActive"), "换工具后预选结束");
        Check(Prop(smart, "HitCount")!.Equals(0), "换工具后智能选区暂停");
        Check(Selection() == Dip(boxButton), "这块预选就地变成普通选区（几何不丢）");
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Select"));

        // ============ 预选里拖动 = 从按下点手框 ============
        Invoke(window, "ClearSelection");
        Hover();
        Pump(window);
        Check((bool)Prop(window, "SmartPreviewActive"), "再次进入预选");

        ClearSnap();
        Point(cx, cy, 300);
        Input(LeftDown);
        Thread.Sleep(150);
        Pump(window);
        Check(Field(window, "_drag")!.ToString() == "SmartPress", "预选里按下先记账（不立刻改装）");
        Check((bool)Prop(smart, "PressActive"), "按下状态交给模型（细化 / 确认都冻结在这一刻）");

        // 真鼠标移动：第一下就超过阈值 = 从按下点开始手框
        Point(cx + 60, cy + 40, 160);
        Check(Field(window, "_drag")!.ToString() == "New", "按住拖过阈值 = 改成手拖框选");
        Check(!(bool)Prop(window, "SmartPreviewActive"), "手拖时预选关闭");

        Point(cx + 120, cy + 80, 160);
        Input(LeftUp);
        Thread.Sleep(200);
        Pump(window);

        var manual = Selection();
        Check(Math.Abs(manual.X - 240) < 1 && Math.Abs(manual.Y - 160) < 1 &&
              Math.Abs(manual.Width - 96) < 1 && Math.Abs(manual.Height - 64) < 1,
            $"手拖出来的是从按下点起的那块框：{manual.X:0},{manual.Y:0} {manual.Width:0}×{manual.Height:0}");
        Check(toolbar.Visibility == Visibility.Visible, "手拖框选之后主工具条出来");
        Save(Render(window), Path.Combine(_outDir, "smart-manual.png"));

        // ============ 预选态右键 = 直接退出这次截图 ============
        Invoke(window, "ClearSelection");
        Hover();
        Pump(window);
        Check((bool)Prop(window, "SmartPreviewActive"), "右键之前回到预选");

        // 真右键（上游 handleRightClick：预选态 = 直接退出这次截图，不是取消选区）
        Point(cx, cy, 200);
        Input(0x0008);
        Thread.Sleep(150);
        Input(0x0010);
        Thread.Sleep(250);
        Pump(window);
        Check(!window.IsVisible && !(bool)Prop(window, "Confirmed"), "预选态右键直接退出（不是取消选区）");

        try { window.Close(); } catch (InvalidOperationException) { }
    }

    // ============ 夹具 ============

    /// <summary>合成一页「整页内容」：每行颜色固定，按 offset 裁出来就是滚动中的某一帧。</summary>
    private static BitmapSource NewPage(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)((y * 7) & 0xFF);
                pixels[i + 1] = (byte)((x * 11 + y * 3) & 0xFF);
                pixels[i + 2] = (byte)((y * 13) & 0xFF);
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>从整页里裁一段（= 滚到那个位置时屏幕上看到的那一帧）。</summary>
    private static BitmapSource View(BitmapSource page, int offset, int height)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * height];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, height), pixels, stride, 0);

        var frame = BitmapSource.Create(page.PixelWidth, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    private static bool SameRow(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>随机纹理帧：和任何别的画面都对不上（冒充「认不出重叠」的那一帧）。</summary>
    private static BitmapSource Noise(int width, int height, int seed)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        new Random(seed).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>假冻结画面：深色底 + 100px 网格 + 坐标标签 + 几个色块，用肉眼确认没有缩放/偏移。</summary>
    private static BitmapSource BuildShot(int width, int height)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));

            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1);
            for (int x = 0; x < width; x += 100) dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
            for (int y = 0; y < height; y += 100) dc.DrawLine(pen, new Point(0, y), new Point(width, y));

            var label = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            for (int y = 0; y < height; y += 100)
                for (int x = 0; x < width; x += 100)
                {
                    var text = new FormattedText($"{x},{y}", CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Consolas"), 12, label, 1.0);
                    dc.DrawText(text, new Point(x + 4, y + 4));
                }

            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE1, 0x5B, 0x4E)), null, new Rect(250, 200, 200, 120));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0xB8, 0x72)), null, new Rect(520, 260, 180, 160));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), null, new Rect(300, 560, 320, 160));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>按 125% 缩放渲染（1:1 对照 Snow Shot 的截图：它是 1.25 物理像素）。</summary>
    private static BitmapSource RenderHi(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(content.ActualWidth * 1.25)),
            Math.Max(1, (int)Math.Round(content.ActualHeight * 1.25)),
            120, 120, PixelFormats.Pbgra32);
        bitmap.Render(content);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// 自动识别二维码（P4-c）：真窗口 + 真二维码图 → 后台识别 → 标记 / 悬停气泡 / 换标记 /
    /// 单击聚焦 / Esc 只关气泡 / 切工具收起 / 点「复制文本」结束这次截图。
    /// </summary>
    private static void QrSection()
    {
        const int width = 1200, height = 800;
        const string first = "https://example.com/qr";
        const string second = "LTools QR 二号";

        var shot = BuildQrShot(width, height, first, second);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, width, height));
        window.Show();
        Pump(window);

        double scale = width / Math.Max(1, window.Width);

        SetSelection(window, new Rect(20, 20, 900, 700));
        Invoke(window, "SyncArea");
        Pump(window);

        var toolbar = Field(window, "Toolbar")!;
        var qrBtn = (ToggleButton)toolbar.GetType().GetField("_qrBtn", Private)!.GetValue(toolbar)!;
        Check(qrBtn.Visibility == Visibility.Visible, "「选择」工具下「显示二维码」按钮出现");
        Check(!qrBtn.IsEnabled, "还没扫完时按钮是灰的");

        var hits = (System.Collections.IList)Field(window, "_qrHits")!;
        Check(WaitFor(window, () => hits.Count > 0, 8000), "后台识别出二维码");
        Check(hits.Count == 2, $"同一张图里的两枚码都认出来（实际 {hits.Count}）");

        var host = (Canvas)Field(window, "QrMarkerHost")!;
        Check(host.Children.Count == 2, $"两枚标记按钮（实际 {host.Children.Count}）");
        var markers = host.Children.OfType<Button>().ToList();
        Check(markers.All(m => m.Visibility == Visibility.Visible), "标记都显示在选区里");
        Check(qrBtn.IsEnabled && qrBtn.IsChecked == true, "识别完按钮可用且处于「显示中」");

        Button MarkerFor(string text) => markers.First(m =>
            (m.ToolTip?.ToString() ?? "").Contains(text.Length > 80 ? text.Substring(0, 80) : text));
        Point Center(Button marker) =>
            new(Canvas.GetLeft(marker) + marker.Width / 2, Canvas.GetTop(marker) + marker.Height / 2);

        Button firstMarker = MarkerFor(first);
        Button secondMarker = MarkerFor(second);

        // 标记落在码的中心（DIP）：第一枚画在像素 80..300，中心 190 px
        Point firstCenter = Center(firstMarker);
        Check(Math.Abs(firstCenter.X - 190 / scale) < 24 && Math.Abs(firstCenter.Y - 190 / scale) < 24,
            $"标记摆在码中心：({firstCenter.X:0.#},{firstCenter.Y:0.#}) 期望 ≈({190 / scale:0.#},{190 / scale:0.#})");

        var bubbleHost = (FrameworkElement)Field(window, "QrBubbleHost")!;
        var bubbleText = (TextBox)Field(window, "QrBubbleText")!;
        var openBtn = (Button)Field(window, "QrOpenBtn")!;
        var copyBtn = (Button)Field(window, "QrCopyBtn")!;
        bool BubbleOpen() => bubbleHost.Visibility == Visibility.Visible;

        void Hover(Point dip, int settleMs = 220)
        {
            Point screen = window.PointToScreen(dip);
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            WaitFor(window, () => false, settleMs);     // 一边等一边抽消息，MouseEnter / 计时器才会跑
        }

        void Click(Point dip)
        {
            Point screen = window.PointToScreen(dip);
            SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
            WaitFor(window, () => false, 200);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            WaitFor(window, () => false, 120);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            WaitFor(window, () => false, 200);
        }

        void SendKey(Key key)
        {
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                Environment.TickCount, key)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Pump(window);
        }

        // 悬停 150ms → 气泡；整串是网址 → 有「打开链接」
        Hover(firstCenter);
        Check(WaitFor(window, BubbleOpen, 3000), "悬停标记后弹出气泡");
        Check(bubbleText.Text == first, "气泡里是识别到的文本：" + bubbleText.Text);
        Check(openBtn.Visibility == Visibility.Visible, "整串是 http(s) 网址 →「打开链接」出现");

        // 换到第二枚：文本跟着换，非网址就没有「打开链接」
        Hover(Center(secondMarker));
        Check(WaitFor(window, () => bubbleText.Text == second, 3000), "换一枚标记 → 气泡跟着换文本");
        Check(openBtn.Visibility == Visibility.Collapsed, "不是网址 → 不显示「打开链接」");
        Check(copyBtn.Style == (Style)window.FindResource("QrBtnPrimary"),
            "没有链接时「复制文本」是主按钮（照上游 setDetection）");

        Save(Render(window), Path.Combine(_outDir, "qr-bubble.png"));

        // 单击标记：气泡打开且焦点进文本框
        Click(Center(secondMarker));
        Check(BubbleOpen() && bubbleText.IsKeyboardFocusWithin, "单击标记 → 气泡打开且焦点进文本框");

        // Esc 只关气泡，不退出截图；焦点回到标记；Enter 再打开
        SendKey(Key.Escape);
        Check(!BubbleOpen(), "Esc 只关气泡");
        Check(window.IsVisible, "Esc 之后覆盖层还在（不是退出这次截图）");
        Check(Keyboard.FocusedElement is Button focused && markers.Contains(focused),
            "关掉气泡后焦点回到标记");
        SendKey(Key.Enter);
        Check(BubbleOpen(), "标记上按 Enter 重新打开气泡");
        SendKey(Key.Escape);

        // 指针移开 250ms 后自己收起
        Hover(firstCenter);
        Check(WaitFor(window, BubbleOpen, 3000), "回第一枚又弹出气泡");
        Hover(new Point(window.ActualWidth - 30, window.ActualHeight - 30));
        Check(WaitFor(window, () => !BubbleOpen(), 3000), "指针离开 250ms 后气泡自己收起");

        // 换工具：标记与按钮一起收（只有「选择」工具下才有二维码）
        var toolType = typeof(CaptureOverlayWindow).Assembly.GetType("LTools.Features.CapturePro.CaptureTool")!;
        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Pen"));
        Pump(window);
        Check(markers.All(m => m.Visibility != Visibility.Visible), "画笔画标注时标记收起");
        Check(qrBtn.Visibility == Visibility.Collapsed, "非「选择」工具下按钮也收起");

        InvokeWith(window, "SetTool", Enum.Parse(toolType, "Select"));
        Pump(window);
        Check(markers.All(m => m.Visibility == Visibility.Visible), "切回「选择」标记回来");

        // 点「复制文本」：写剪贴板 + 结束这次截图（照上游 cancelCapture）
        try { Clipboard.SetText("qr-smoke-sentinel"); } catch { }
        Hover(firstCenter);
        Check(WaitFor(window, () => BubbleOpen() && bubbleText.Text == first, 3000), "复制前气泡在第一枚上");
        Click(Center(firstMarker));
        Point copyCenter = copyBtn.TransformToAncestor((Visual)window.Content)
            .Transform(new Point(copyBtn.ActualWidth / 2, copyBtn.ActualHeight / 2));
        Click(copyCenter);

        Check(!window.IsVisible, "复制之后结束这次截图（覆盖层关闭）");

        // 剪贴板是全局资源，刚写完可能被别的进程抢一下：给 3 秒重试，别让偶发的 OpenClipboard 失败
        // 把这条断言变成假失败。
        string copied = "";
        Check(WaitFor(window, () =>
        {
            try { copied = Clipboard.GetText(); }
            catch { copied = ""; }

            return copied == first;
        }, 3000), $"剪贴板里是二维码文本：{copied}");
    }

    /// <summary>白底 + 两枚真实二维码（位置固定，供标记落点断言）。</summary>
    private static BitmapSource BuildQrShot(int width, int height, string first, string second)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawImage(QrImage(first, 220), new Rect(80, 80, 220, 220));
            dc.DrawImage(QrImage(second, 200), new Rect(700, 420, 200, 200));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource QrImage(string text, int size)
    {
        var type = typeof(CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.Units.Screen.QrCodeAlgorithms")!;
        using var bmp = (System.Drawing.Bitmap)type.GetMethod("Generate")!
            .Invoke(null, new object[] { text, size, 4, "M" })!;

        IntPtr handle = bmp.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(handle);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>一边抽消息一边等条件成立（后台识别、DispatcherTimer 都靠它推进）。</summary>
    private static bool WaitFor(Window window, Func<bool> condition, int timeoutMs = 3000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            if (condition()) return true;

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(25), DispatcherPriority.Send,
                (_, _) => frame.Continue = false, window.Dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        return condition();
    }

    private static BitmapSource Render(Window window)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(content.ActualWidth)),
            Math.Max(1, (int)Math.Round(content.ActualHeight)),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }

    /// <summary>
    /// 把窗口截图裁到某个元素那一块。**不直接 <c>RenderTargetBitmap.Render(element)</c>**：
    /// 工具栏在 Canvas 上、带 <c>Canvas.Left/Top</c> 偏移，单独渲染出来是空白（实测）。
    /// </summary>
    private static BitmapSource CropElement(Window window, FrameworkElement element)
    {
        var full = Render(window);
        var root = (Visual)window.Content;
        var box = element.TransformToAncestor(root)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

        int x = Math.Max(0, (int)Math.Floor(box.X) - 2);
        int y = Math.Max(0, (int)Math.Floor(box.Y) - 2);
        int w = Math.Min(full.PixelWidth - x, (int)Math.Ceiling(box.Width) + 4);
        int h = Math.Min(full.PixelHeight - y, (int)Math.Ceiling(box.Height) + 4);
        return new CroppedBitmap(full, new Int32Rect(x, y, Math.Max(1, w), Math.Max(1, h)));
    }

    /// <summary>
    /// 临时把窗口提到最上层（冒烟全程还开着截图覆盖层，它也是 Topmost；真鼠标要落到切图窗上
    /// 就得在同一层里再顶一次），拖动验完再放回去。
    /// </summary>
    private static void SetTopmost(Window window, bool on)
    {
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, new IntPtr(on ? -1 : -2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    }

    /// <summary>
    /// 把窗口顶到前台，好让注入的真鼠标点到它（冒烟是从 OpenChamber 的终端起的，前台锁会拦
    /// <see cref="Window.Activate"/>；用 AttachThreadInput 兜底——与产品里 WindowAction 同一套做法）。
    /// </summary>
    private static bool ForceForeground(Window window)
    {
        IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return false;

        uint current = GetCurrentThreadId();
        uint owner = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = false;

        try
        {
            AllowSetForegroundWindow(0xFFFFFFFF);
            if (owner != 0 && owner != current) attached = AttachThreadInput(current, owner, true);

            window.Activate();
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
            return GetForegroundWindow() == handle;
        }
        finally
        {
            if (attached) AttachThreadInput(current, owner, false);
        }
    }

    /// <summary>当前前台窗口标题（诊断「真鼠标是不是点在自己窗口上」用）。</summary>
    private static string ForegroundTitle()
    {
        IntPtr hwnd = GetForegroundWindow();
        var text = new System.Text.StringBuilder(512);
        GetWindowTextW(hwnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>
    /// 关掉「切图_*」的 Explorer 窗口：切图导出成功后程序会像上游那样自动打开保存目录，
    /// 冒烟里不把它留在屏幕上（只按标题匹配，其它资源管理器窗口不动）。
    /// </summary>
    private static void CloseExplorerFolders(string titlePart)
    {
        try
        {
            var pids = new HashSet<uint>();
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                pids.Add((uint)process.Id);
                process.Dispose();
            }

            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (!pids.Contains(pid)) return true;

                var text = new System.Text.StringBuilder(512);
                GetWindowTextW(hwnd, text, text.Capacity);
                if (text.ToString().Contains(titlePart, StringComparison.OrdinalIgnoreCase))
                    PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);

            Thread.Sleep(200);
        }
        catch
        {
            // 关不掉也不影响断言
        }
    }

    /// <summary>跑一次 ffmpeg（参数逐个进 ArgumentList：路径里有空格也不用自己加引号）。</summary>
    private static int RunFfmpeg(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in args) psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);
        return process.ExitCode;
    }

    /// <summary>用 volumedetect 量整段平均音量（dB）；解析不出来返回 NaN。</summary>
    private static double MeanVolume(string ffmpeg, string file)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in new[] { "-hide_banner", "-i", file, "-af", "volumedetect", "-f", "null", "-" })
            psi.ArgumentList.Add(arg);

        using var process = System.Diagnostics.Process.Start(psi)!;
        string text = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(30000);

        var match = System.Text.RegularExpressions.Regex.Match(text, @"mean_volume:\s*(-?[\d.]+) dB");
        return match.Success
            ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : double.NaN;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("写出 " + path);
    }

    private static void Pump(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 只把已排队的布局 / 渲染跑掉，**不等 ApplicationIdle**：实时贴图的宿主窗口会持续把鼠标
    /// 消息转发给贴图窗口，Idle 优先级可能一直排不到（冒烟在这里卡死过）。带 250ms 兜底。
    /// </summary>
    private static void Nudge(Window window)
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        var guard = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Send,
            (_, _) => frame.Continue = false, window.Dispatcher);
        guard.Start();
        Dispatcher.PushFrame(frame);
        guard.Stop();
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)!.GetValue(target);

    /// <summary>
    /// 反射调一个静态方法，缺的参数按参数表补 <see cref="Type.Missing"/>。
    /// <para>
    /// **反射不会替可选参数补默认值**：`Capture(area, withCursor = false)` 用 `new object[] { area }`
    /// 去 Invoke 会抛 <c>TargetParameterCountException</c>。产品方法加了「可选参数」而冒烟还按旧数量调用时，
    /// 就会在跑了几百条断言之后才炸——所以调用点统一走这里，别直写 <c>GetMethod(...).Invoke</c>。
    /// </para>
    /// </summary>
    private static object? CallStatic(Type type, string name, params object?[] args)
    {
        var candidates = type
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == name && m.GetParameters().Length >= args.Length)
            .OrderBy(m => m.GetParameters().Length)
            .ToList();
        if (candidates.Count == 0) throw new MissingMethodException(type.Name + "." + name);

        // 重载里挑「参数够用、且多出来的那几个都是可选的」那一份
        var method = candidates.FirstOrDefault(m => m.GetParameters()
                         .Skip(args.Length).All(p => p.IsOptional))
                     ?? candidates[0];
        var parameters = method.GetParameters();
        if (args.Length == parameters.Length) return method.Invoke(null, args);

        var filled = new object?[parameters.Length];
        args.CopyTo(filled, 0);
        for (int i = args.Length; i < filled.Length; i++) filled[i] = Type.Missing;
        return method.Invoke(null, filled);
    }

    /// <summary>私有字段或私有属性（样式这类按工具取的量是表达式属性，没有字段）。</summary>
    private static object Member(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target)
        ?? target.GetType().GetProperty(name, Private)!.GetValue(target);

    /// <summary>按钮里那个 <see cref="System.Windows.Shapes.Path"/> 画的是哪张图标（按资源实例比）。</summary>
    private static bool HasIcon(object window, ContentControl control, string key) =>
        control.Content is System.Windows.Shapes.Path path &&
        ReferenceEquals(path.Data, ((FrameworkElement)window).FindResource(key));

    /// <summary>工具栏里那一条（工具格子 + 撤销 / 重做 + 完成动作）。</summary>
    private static WrapPanel? ToolRow(object window)
    {
        var toolbar = Field(window, "Toolbar");
        return toolbar.GetType().GetField("ToolRow", Private)?.GetValue(toolbar) as WrapPanel;
    }

    /// <summary>工具条上所有按钮的 ToolTip（完成动作只画图标，文字只在这里）。</summary>
    private static List<string> ActionTips(object window) =>
        ToolRow(window)?.Children.OfType<Button>().Select(b => b.ToolTip?.ToString() ?? "").ToList()
        ?? new List<string>();

    /// <summary>工具栏是不是「一排」：所有可见按钮相对 ToolRow 的 Y 都相同（分隔线不参与，它们竖直居中）。</summary>
    private static bool ToolRowSingleLine(object window)
    {
        var row = ToolRow(window);
        if (row == null) return false;

        var ys = new List<double>();
        foreach (var child in row.Children.OfType<System.Windows.Controls.Primitives.ButtonBase>())
        {
            if (child.Visibility != Visibility.Visible) continue;
            ys.Add(child.TranslatePoint(new Point(0, 0), row).Y);
        }

        return ys.Count > 8 && ys.Max() - ys.Min() < 1.5;
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, Private)!.Invoke(target, null);

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);

        if (!ok)
        {
            _failed++;

            // 控制台编码在这台机器上会把中文压成 ?，所以失败清单另存一份 base64（UTF-8），
            // 用 PowerShell 解出来看看到底是哪条断言红了。
            try
            {
                File.AppendAllText(Path.Combine(_outDir, "fails.txt"), what + Environment.NewLine);
            }
            catch { /* 写不进就算了 */ }
        }
    }
}

