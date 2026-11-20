using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图界面与参考线设置（P9-c）：照上游 <c>screenshot_ui/*</c> 那 18 项——
/// 默认值 / 归一 / 落盘 / 设置页回显与改动 / 覆盖层真的按设置画
/// （压暗色 / 边框色 / 提示不透明度 / 工具条 0.8 倍 / Alt 轻按切参考线 /
/// 取色器 Shift 换格式、Shift+P 换坐标口径 / 区域类型提示开关 / 信息条单位与显隐）。
/// </summary>
/// <remarks>
/// 与 <c>BehaviorSection</c> 同一套做法：把 <c>ConfigStore.ConfigPath</c> 指到本段自己的临时目录，
/// 断言完恢复原路径并 <c>Invalidate()</c>，不给后面的段留脏配置。
/// </remarks>
internal static partial class Program
{
    private static void OverlayUiSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var uiType = asm.GetType("LTools.Features.CapturePro.OverlayUiSettings")!;
        var transitionType = asm.GetType("LTools.Features.CapturePro.SelectionTransition")!;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;

        object Tool(string name) => Enum.Parse(toolType, name);
        object Transition(string name) => Enum.Parse(transitionType, name);

        object Get(string name) => uiType.GetProperty(name, Stat)!.GetValue(null)!;
        bool Flag(string name) => (bool)Get(name);
        string Text(string name) => (string)Get(name);
        int Number(string name) => (int)Get(name);
        Color Col(string name) => (Color)Get(name);
        string TransitionName() => Get("SelectionTransition").ToString()!;

        void Set(string method, params object?[] args) =>
            uiType.GetMethod(method, Stat)!.Invoke(null, args);

        void Invalidate() => uiType.GetMethod("Invalidate", Stat)!.Invoke(null, null);

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "overlayui-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        Window? settings = null;
        var overlays = new List<Window>();
        try
        {
            // ============ ① 默认值（逐字对齐上游 screenshot_ui/*） ============
            Check(Text("ToolbarSize") == "normal", "工具条尺寸默认「正常」：" + Text("ToolbarSize"));
            Check(TransitionName() == "Soft", "换层级过渡默认柔和：" + TransitionName());
            Check(Text("SelectionDisplayUnit") == "physical_pixels", "信息条单位默认物理像素");
            Check(Text("UnitLabel") == "px", "单位文字默认 px");
            Check(!Flag("SelectionToolbarHidden"), "信息条默认不隐藏");
            Check(Flag("ShowEditSelectionToolbar"), "「选择」工具下默认也显示信息条（上游默认 true）");
            Check(Text("ColorPickerDisplayMode") == "hide_outside_selection", "取色器默认选区外不显示");
            Check(Text("ColorPickerFormat") == "hex", "取色格式默认 HEX");
            Check(Text("ColorPickerCoordinateMode") == "global", "取色坐标默认全局");
            Check(Number("ShortcutHintOpacity") == 100, "提示不透明度默认 100");
            Check(Flag("AreaTypeHintEnabled"), "区域类型提示默认开");
            Check(!Flag("ShowGuidesByDefault"), "参考线默认不显示");
            Check(Flag("AdaptiveGuideColors"), "参考线自适应默认开");
            Check(Col("SelectionBorderColor") == Color.FromArgb(0xFF, 0x40, 0x96, 0xFF),
                "选区边框色默认 #4096FF");
            Check(Col("SelectionMaskColor") == Color.FromArgb(0x80, 0, 0, 0), "压暗色默认 #00000080");
            Check(Col("CursorGuideColor") == Color.FromArgb(0xFF, 0, 0, 0), "光标参考线默认黑");
            Check(Col("MonitorCenterGuideColor") == Color.FromArgb(0xFF, 0xFF, 0, 0), "显示器中心默认红");
            Check(Col("PickerCenterGuideColor").A == 0, "取样点参考线默认全透明");
            Check(!File.Exists(settingsFile), "只读默认值不会平白落一份 settings.json");

            // ============ ② 写入即落盘 + 归一化 ============
            Set("SetToolbarSize", "small");
            Check(File.Exists(settingsFile), "改一项才出现 settings.json");
            Check(File.ReadAllText(settingsFile).Contains("\"ToolbarSize\": \"small\""),
                "settings.json 的 Screenshot 段写着 ToolbarSize = small");
            Check(Math.Abs((double)Get("ToolbarScale") - 0.8) < 1e-9, "小档 = 0.8 倍");

            var transform = (ScaleTransform)uiType.GetMethod("ToolbarTransform", Stat)!.Invoke(null, null)!;
            Check(Math.Abs(transform.ScaleX - 0.8) < 1e-9 && Math.Abs(transform.ScaleY - 0.8) < 1e-9,
                "工具条缩放变换 = 0.8 × 0.8");

            Set("SetSelectionTransition", Transition("Fast"));
            Invalidate();
            Check(TransitionName() == "Fast", "过渡动画切到快速");
            Check(File.ReadAllText(settingsFile).Contains("\"SelectionTransitionAnimation\": 72"),
                "快速过渡落盘成 72ms（上游存毫秒）");

            Set("SetSelectionDisplayUnit", "logical_pixels");
            Invalidate();
            Check(Text("UnitLabel") == "dp", "逻辑像素档的单位文字是 dp");
            Check((int)uiType.GetMethod("DisplayValue", Stat, null,
                    new[] { typeof(int), typeof(double) }, null)!
                    .Invoke(null, new object[] { 125, 1.25 })! == 100, "125px @125% 显示成 100dp");

            Set("SetColorPickerFormat", "hsl");
            Set("SetColorPickerCoordinateMode", "relative");
            Set("SetColorPickerDisplayMode", "always_show");
            Set("SetShortcutHintOpacity", 55);
            Set("SetSelectionBorderColor", Color.FromRgb(0xFF, 0x00, 0x00));
            Set("SetSelectionMaskColor", Color.FromArgb(0x40, 0x00, 0x00, 0x80));
            Set("SetCursorGuideColor", Color.FromRgb(0x11, 0x22, 0x33));
            Set("SetSelectionCenterGuideColor", Color.FromRgb(0x44, 0x55, 0x66));
            Set("SetMonitorCenterGuideColor", Color.FromRgb(0x77, 0x88, 0x99));
            Set("SetPickerCenterGuideColor", Color.FromArgb(0xCC, 0xAA, 0xBB, 0xCC));
            Invalidate();

            Check(Text("ColorPickerFormat") == "hsl", "取色格式写成 hsl");
            Check(Flag("RelativePickerCoordinates"), "取色坐标切到相对");
            Check(Text("ColorPickerDisplayMode") == "always_show", "取色器切到总是显示");
            Check(Number("ShortcutHintOpacity") == 55, "提示不透明度 55");
            Check(Col("SelectionBorderColor") == Color.FromRgb(0xFF, 0x00, 0x00), "边框色写成红");
            Check(Col("SelectionMaskColor") == Color.FromArgb(0x40, 0x00, 0x00, 0x80), "压暗色带透明度存得住");
            Check(Col("PickerCenterGuideColor") == Color.FromArgb(0xCC, 0xAA, 0xBB, 0xCC), "取样点参考线色存得住");
            Check(File.ReadAllText(settingsFile).Contains("\"SelectionBorderColor\": \"#FF0000FF\""),
                "颜色按 #RRGGBBAA 落盘");

            Set("SetToolbarSize", "big");
            Set("SetSelectionDisplayUnit", "dp");
            Set("SetColorPickerFormat", "cmyk");
            Set("SetColorPickerCoordinateMode", "screen");
            Set("SetColorPickerDisplayMode", "sometimes");
            Set("SetShortcutHintOpacity", 400);
            Set("SetSelectionBorderColor", Color.FromRgb(1, 2, 3));
            Set("SetSelectionTransition", Transition("None"));
            Invalidate();
            Check(Text("ToolbarSize") == "normal", "认不出的工具条尺寸回落正常");
            Check(Text("SelectionDisplayUnit") == "physical_pixels", "认不出的单位回落物理像素");
            Check(Text("ColorPickerFormat") == "hex", "认不出的取色格式回落 HEX");
            Check(Text("ColorPickerCoordinateMode") == "global", "认不出的坐标口径回落全局");
            Check(Text("ColorPickerDisplayMode") == "hide_outside_selection", "认不出的显示时机回落选区外不显示");
            Check(Number("ShortcutHintOpacity") == 100, "不透明度超出夹到 100");
            Check(TransitionName() == "None", "过渡动画可以关掉");
            Check(Col("SelectionBorderColor") == Color.FromRgb(1, 2, 3), "6 位简写也能读（#010203FF）");

            // 回到「上游默认」再进覆盖层断言，答案才确定
            Set("SetToolbarSize", "normal");
            Set("SetSelectionTransition", Transition("Soft"));
            Set("SetSelectionDisplayUnit", "physical_pixels");
            Set("SetColorPickerFormat", "hex");
            Set("SetColorPickerCoordinateMode", "global");
            Set("SetColorPickerDisplayMode", "hide_outside_selection");
            Set("SetShortcutHintOpacity", 55);
            Set("SetSelectionBorderColor", Color.FromRgb(0xFF, 0x00, 0x00));
            Set("SetSelectionMaskColor", Color.FromArgb(0x40, 0x00, 0x00, 0x80));
            Set("SetCursorGuideColor", Color.FromRgb(0x11, 0x22, 0x33));
            Set("SetSelectionCenterGuideColor", Color.FromRgb(0x44, 0x55, 0x66));
            Set("SetMonitorCenterGuideColor", Color.FromRgb(0x77, 0x88, 0x99));
            Set("SetPickerCenterGuideColor", Color.FromArgb(0xCC, 0xAA, 0xBB, 0xCC));
            Set("SetAreaTypeHintEnabled", true);
            Set("SetShowGuidesByDefault", true);
            Set("SetAdaptiveGuideColors", true);
            Set("SetSelectionToolbarHidden", false);
            Set("SetShowEditSelectionToolbar", true);
            Invalidate();

            // ============ ③ 设置页：控件回显 + 改动即落盘 ============
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);

            settings.GetType().GetField("_initializing", Inst)!.SetValue(settings, false);
            settings.GetType().GetMethod("PopulateOverlayUi", Inst)!.Invoke(settings, null);

            ComboBox? Combo(string name) => settings!.FindName(name) as ComboBox;
            CheckBox? Box(string name) => settings!.FindName(name) as CheckBox;
            Slider? Slide(string name) => settings!.FindName(name) as Slider;
            TextBlock? Label(string name) => settings!.FindName(name) as TextBlock;

            Check(Combo("CaptureToolbarSizeCombo")?.SelectedIndex == 0, "工具条尺寸回显「标准」");
            Check(Combo("CaptureTransitionCombo")?.SelectedIndex == 0, "过渡回显「柔和」");
            Check(Combo("CaptureUnitCombo")?.SelectedIndex == 0, "单位回显「物理像素」");
            Check(Combo("CapturePickerModeCombo")?.SelectedIndex == 0, "取色显示回显「选区外不显示」");
            Check(Combo("CapturePickerFormatCombo")?.SelectedIndex == 0, "取色格式回显「HEX」");
            Check(Combo("CapturePickerFormatCombo")?.Items.Count == 5,
                "取色格式下拉有 5 种写法（HEX / 无井号 / RGB / HSL / HSB）");
            Check(Combo("CapturePickerCoordCombo")?.SelectedIndex == 0, "取色坐标回显「全局」");
            Check(Box("CaptureHideSelectionBarBox")?.IsChecked == false, "「隐藏信息条」回显为不勾");
            Check(Box("CaptureShowEditBarBox")?.IsChecked == true, "「选择工具下也显示」回显为勾上");
            Check(Box("CaptureAreaHintBox")?.IsChecked == true, "「区域类型提示」回显为勾上");
            Check(Box("CaptureGuidesDefaultBox")?.IsChecked == true, "「默认显示参考线」回显为勾上");
            Check(Box("CaptureGuidesAdaptiveBox")?.IsChecked == true, "「参考线自适应」回显为勾上");
            Check(Math.Abs((Slide("CaptureHintOpacitySlider")?.Value ?? -1) - 55) < 0.001, "不透明度滑块回显 55");
            Check(Label("CaptureHintOpacityText")?.Text == "55%", "不透明度数字回显 55%");
            Check(Label("CaptureBorderColorText")?.Text == "#FF0000FF", "边框色回显 #FF0000FF");
            Check(Label("CaptureMaskColorText")?.Text == "#00008040", "压暗色回显带透明度的 #00008040");
            Check((settings.FindName("CaptureBorderColorSwatch") as Border)?.Background is SolidColorBrush borderBrush &&
                  borderBrush.Color == Color.FromRgb(0xFF, 0x00, 0x00), "边框色色块是红的");

            Combo("CaptureToolbarSizeCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Text("ToolbarSize") == "small", "下拉切「小」立刻写进设置");
            Check(Math.Abs((double)Get("ToolbarScale") - 0.8) < 1e-9, "切「小」后缩放立刻是 0.8");

            Combo("CaptureTransitionCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(TransitionName() == "Fast", "下拉切「快速」立刻写进设置");

            Combo("CaptureUnitCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Text("UnitLabel") == "dp", "下拉切「逻辑像素」立刻写进设置");

            Combo("CapturePickerModeCombo")!.SelectedIndex = 2;
            Pump(settings);
            Check(Text("ColorPickerDisplayMode") == "always_hide", "下拉切「总是不显示」立刻写进设置");

            Combo("CapturePickerFormatCombo")!.SelectedIndex = 2;
            Pump(settings);
            Check(Text("ColorPickerFormat") == "rgb", "下拉切 rgb 立刻写进设置");

            Combo("CapturePickerCoordCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Flag("RelativePickerCoordinates"), "下拉切「相对选区」立刻写进设置");

            Box("CaptureHideSelectionBarBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("SelectionToolbarHidden"), "勾上「隐藏信息条」立刻写进设置");

            Box("CaptureShowEditBarBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("ShowEditSelectionToolbar"), "取消「选择工具下也显示」立刻写进设置");
            Box("CaptureShowEditBarBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("ShowEditSelectionToolbar"), "重新勾上「选择工具下也显示」立刻写进设置");

            Box("CaptureAreaHintBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("AreaTypeHintEnabled"), "取消「区域类型提示」立刻写进设置");

            Box("CaptureGuidesDefaultBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("ShowGuidesByDefault"), "取消「默认显示参考线」立刻写进设置");

            Box("CaptureGuidesAdaptiveBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("AdaptiveGuideColors"), "取消「参考线自适应」立刻写进设置");

            Slide("CaptureHintOpacitySlider")!.Value = 30;
            Pump(settings);
            Check(Number("ShortcutHintOpacity") == 30, "拖滑块到 30 立刻写进设置");
            Check(Label("CaptureHintOpacityText")?.Text == "30%", "拖动时数字跟着走（30%）");

            // 颜色按钮走的是 CaptureColorDialog（模态），这里只验「回填后的外观」这一半：
            // 换一份颜色 → 重新回填 → 色块与文本都跟上。
            Set("SetSelectionBorderColor", Color.FromRgb(0x00, 0x80, 0x00));
            Invalidate();
            settings.GetType().GetMethod("PopulateOverlayUi", Inst)!.Invoke(settings, null);
            Check(Label("CaptureBorderColorText")?.Text == "#008000FF", "重新回填：边框色文本跟着变");
            Check((settings.FindName("CaptureBorderColorSwatch") as Border)?.Background is SolidColorBrush green &&
                  green.Color == Color.FromRgb(0x00, 0x80, 0x00), "重新回填：色块也换成新色");

            // 全透明色在按钮上写「透明（不画）」而不是 #00000000
            Set("SetPickerCenterGuideColor", Colors.Transparent);
            Invalidate();
            settings.GetType().GetMethod("PopulateOverlayUi", Inst)!.Invoke(settings, null);
            Check(Label("CapturePickerGuideColorText")?.Text == "透明（不画）", "全透明色显示「透明（不画）」");

            settings.Close();
            settings = null;

            // ============ ④ 覆盖层真的按设置画 ============
            Set("SetToolbarSize", "small");
            Set("SetSelectionDisplayUnit", "logical_pixels");
            Set("SetShortcutHintOpacity", 55);
            Set("SetSelectionBorderColor", Color.FromRgb(0x00, 0x80, 0x00));
            Set("SetSelectionMaskColor", Color.FromArgb(0x40, 0x00, 0x00, 0x80));
            Set("SetShowGuidesByDefault", true);
            Set("SetAreaTypeHintEnabled", false);
            Set("SetSelectionToolbarHidden", false);
            Set("SetShowEditSelectionToolbar", true);
            Invalidate();

            var shot = BuildShot(1200, 800);
            var monitor = new System.Drawing.Rectangle(0, 0, 1200, 800);
            var window = NewOverlay(shot, monitor);
            overlays.Add(window);
            window.Show();
            window.Activate();
            Pump(window);

            var surface = (FrameworkElement)typeof(CaptureOverlayWindow).GetField("Surface", Inst)!.GetValue(window)!;
            var surfaceType = surface.GetType();

            Color SurfaceColor(string name) =>
                (Color)surfaceType.GetProperty(name, Inst)!.GetValue(surface)!;

            Check(SurfaceColor("SelectionMaskColor") == Color.FromArgb(0x40, 0x00, 0x00, 0x80),
                "覆盖层的压暗用的是设置里的颜色");
            Check(SurfaceColor("SelectionBorderColor") == Color.FromRgb(0x00, 0x80, 0x00),
                "覆盖层的选区边框用的是设置里的颜色");
            Check(Math.Abs(((UIElement)window.FindName("HintBox")).Opacity - 0.55) < 0.01,
                "顶部提示条按 55% 不透明度显示");

            var toolbar = (FrameworkElement)typeof(CaptureOverlayWindow).GetField("Toolbar", Inst)!.GetValue(window)!;
            Check(toolbar.LayoutTransform is ScaleTransform ts && Math.Abs(ts.ScaleX - 0.8) < 1e-9,
                "截图工具条挂着 0.8 倍缩放");

            // 参考线：默认开着（设置里 show_guides_by_default = true）
            Check((bool)surfaceType.GetProperty("ShowGuides", Inst)!.GetValue(surface)!, "参考线按设置默认开着");

            // Alt 轻按 = 关掉参考线；再轻按一次 = 开回来
            RaiseModifierTap(window, Key.LeftAlt);
            Pump(window);
            Check(!(bool)surfaceType.GetProperty("ShowGuides", Inst)!.GetValue(surface)!, "Alt 轻按一次关掉参考线");
            RaiseModifierTap(window, Key.LeftAlt);
            Pump(window);
            Check((bool)surfaceType.GetProperty("ShowGuides", Inst)!.GetValue(surface)!, "Alt 再轻按一次开回来");

            // 区域类型提示关掉：琥珀色框还在，标签不显示
            var drawDetect = typeof(CaptureOverlayWindow).GetMethod("DrawDetect", Inst)!;
            drawDetect.Invoke(window, new object[] { new Rect(100, 100, 200, 120), "按钮 · 确定" });
            Pump(window);
            Check(((UIElement)window.FindName("DetectRect")).Visibility == Visibility.Visible,
                "关掉提示后悬停高亮框还在");
            Check(((UIElement)window.FindName("DetectLabel")).Visibility == Visibility.Collapsed,
                "关掉提示后那块「控件 · 名称 · 尺寸」标签不显示");

            Set("SetAreaTypeHintEnabled", true);
            Invalidate();
            drawDetect.Invoke(window, new object[] { new Rect(100, 100, 200, 120), "按钮 · 确定" });
            Pump(window);
            Check(((UIElement)window.FindName("DetectLabel")).Visibility == Visibility.Visible,
                "打开提示后标签重新出现");

            // 信息条：单位跟着设置走（物理像素 → dp）
            SetSelection(window, new Rect(200, 150, 400, 300));
            typeof(CaptureOverlayWindow).GetMethod("SyncArea", Inst)!.Invoke(window, null);
            Pump(window);

            string Unit(string name) => ((TextBlock)window.FindName(name)).Text;
            string BarText(string name) => ((TextBlock)window.FindName(name)).Text;

            Check(Unit("SelPosUnit") == "dp" && Unit("SelSizeUnit") == "dp", "信息条两个单位标签都是 dp");
            double docScale = (double)Member(window, "Document")!.GetType().GetProperty("Scale")!.GetValue(Member(window, "Document"))!;
            var paramsObj = typeof(CaptureOverlayWindow).GetMethod("CurrentSelectionParams", Inst)!.Invoke(window, null)!;
            int physicalX = (int)paramsObj.GetType().GetProperty("X")!.GetValue(paramsObj)!;
            int expectedDp = (int)Math.Round(physicalX / docScale);
            Check(BarText("SelXText") == expectedDp.ToString(),
                $"信息条 X 按 dp 显示（{physicalX}px → {BarText("SelXText")}dp，期望 {expectedDp}）");
            Check(docScale <= 1.001 || physicalX != expectedDp, "125% 下 dp 与物理像素确实不同（换算真的发生了）");

            // 「选择」工具下显示 / 隐藏：两个开关各自生效
            typeof(CaptureOverlayWindow).GetMethod("SetTool", Inst)!.Invoke(window, new[] { Tool("Select") });
            Pump(window);
            Check(((UIElement)window.FindName("SelBar")).Visibility == Visibility.Visible,
                "打开「选择工具下也显示」后信息条在「选择」工具下出现");

            Set("SetSelectionToolbarHidden", true);
            Invalidate();
            typeof(CaptureOverlayWindow).GetMethod("ShowSelectionBar", Inst)!
                .Invoke(window, new object[] { new Rect(200, 150, 400, 300) });
            Pump(window);
            Check(((UIElement)window.FindName("SelBar")).Visibility == Visibility.Collapsed,
                "总开关打开后信息条不再出现");

            Set("SetSelectionToolbarHidden", false);
            Invalidate();

            // ============ ⑤ 取色器：Shift 换格式、Shift+P 换坐标口径 ============
            Set("SetColorPickerFormat", "hex");
            Set("SetColorPickerCoordinateMode", "global");
            Set("SetColorPickerDisplayMode", "always_show");
            Invalidate();

            typeof(CaptureOverlayWindow).GetMethod("SetTool", Inst)!.Invoke(window, new[] { Tool("Picker") });
            Pump(window);

            RaiseModifierTap(window, Key.LeftShift);
            Pump(window);
            Check(Text("ColorPickerFormat") == "hex_without_hash", "取色时轻按 Shift 换成无井号");
            RaiseModifierTap(window, Key.LeftShift);
            Pump(window);
            Check(Text("ColorPickerFormat") == "rgb", "再轻按 Shift 换成 rgb");
            RaiseModifierTap(window, Key.LeftShift);
            Pump(window);
            Check(Text("ColorPickerFormat") == "hsl", "再轻按 Shift 换成 hsl");
            RaiseModifierTap(window, Key.LeftShift);
            Pump(window);
            Check(Text("ColorPickerFormat") == "hsb", "再轻按 Shift 换成 hsb");
            RaiseModifierTap(window, Key.LeftShift);
            Pump(window);
            Check(Text("ColorPickerFormat") == "hex", "第五次轻按 Shift 转回 hex");

            RaiseShiftP(window);
            Pump(window);
            Check(Flag("RelativePickerCoordinates"), "Shift+P 把取色坐标切到相对选区");
            RaiseShiftP(window);
            Pump(window);
            Check(!Flag("RelativePickerCoordinates"), "再按一次 Shift+P 切回全局");

            window.Close();
            overlays.Remove(window);
        }
        finally
        {
            settings?.Close();
            foreach (var overlay in overlays)
            {
                try { overlay.Close(); } catch { }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
        }
    }

    /// <summary>轻按一个修饰键（按下 + 抬起），走真实的 <c>PreviewKeyDown</c> / <c>PreviewKeyUp</c> 链。</summary>
    private static void RaiseModifierTap(Window window, Key key)
    {
        RaiseKey(window, key, down: true, shift: false);
        RaiseKey(window, key, down: false, shift: false);
    }

    /// <summary>按一次 Shift+P（先按下 Shift、再 P、再各自抬起），走真实的键盘处理链。</summary>
    private static void RaiseShiftP(Window window)
    {
        RaiseKey(window, Key.LeftShift, down: true, shift: false);
        RaiseKey(window, Key.P, down: true, shift: false);
        RaiseKey(window, Key.P, down: false, shift: false);
        RaiseKey(window, Key.LeftShift, down: false, shift: false);
    }

    /// <summary>投一次按键（按下或抬起）。</summary>
    private static void RaiseKey(Window window, Key key, bool down, bool shift)
    {
        var source = PresentationSource.FromVisual(window);
        if (source == null)
        {
            Check(false, "窗口没有 PresentationSource，无法投按键");
            return;
        }

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent,
        };

        window.RaiseEvent(args);
    }
}
