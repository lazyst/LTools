using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 贴图窗口设置（P9-d1）：照上游 <c>pin_to_screen/*</c> 的外观与滚轮那十二项——
/// 默认值 / 归一 / 落盘 / 设置页回显与改动 / 真贴图窗口的边框开关、阴影窗口跟随、
/// 锁定与活动状态色、右上角按钮开关、右键菜单两项、滚轮档位与锚点、关掉再贴回按记录恢复。
/// </summary>
/// <remarks>
/// 与 <c>OverlayUiSection</c> 同一套做法：把 <c>ConfigStore.ConfigPath</c> 指到本段自己的临时目录，
/// 断言完恢复原路径并 <c>Invalidate()</c>，不给后面的段留脏配置。
/// 贴图历史（<c>pins/pins.json</c>）落在数据根，收尾时把本段建的那张删掉。
/// </remarks>
internal static partial class Program
{
    private static void PinSettingsSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var pinSettingsType = asm.GetType("LTools.Features.CapturePro.PinSettings")!;
        var shadowType = asm.GetType("LTools.Views.CapturePro.PinShadowWindow")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;

        object? Get(string name) => pinSettingsType.GetProperty(name, Stat)!.GetValue(null);
        bool Flag(string name) => (bool)Get(name)!;
        string Text(string name) => (string)Get(name)!;
        int Number(string name) => (int)Get(name)!;
        Color Col(string name) => (Color)Get(name)!;

        void Set(string method, params object?[] args) =>
            pinSettingsType.GetMethod(method, Stat)!.Invoke(null, args);

        void Invalidate() => pinSettingsType.GetMethod("Invalidate", Stat)!.Invoke(null, null);

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "pinsettings-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        // 前一节可能还留着贴图：先全关（不然下面的「贴回最近关闭」会挑错）
        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        Window? settings = null;
        Window? pin = null;
        string pinId = "";

        try
        {
            // ============ ① 默认值（逐字对齐上游 configurationschema.cpp） ============
            Check(Flag("ShowBorderByDefault"), "新贴图默认显示边框（上游 true）");
            Check(!Flag("ShowShadowByDefault"), "新贴图默认不显示阴影（上游 false）");
            Check(Flag("ShowWindowButtons"), "默认显示右上角窗口按钮（上游 true）");
            Check(!Flag("ConfirmBeforeClosingWindow"), "关闭贴图默认不确认（上游 false）");
            Check(Text("ZoomMode") == "mouse_position", "滚轮缩放锚点默认光标位置");
            Check(Number("ZoomStepPercent") == 10, "滚轮缩放步长默认 10%");
            Check(Number("OpacityStepPercent") == 5, "滚轮不透明度步长默认 5%");
            Check(Col("BorderColor") == Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB), "边框色默认 #DBDBDB");
            Check(Col("BorderActiveColor") == Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF), "活动边框色默认 #69B1FF");
            Check(Col("LockedBorderColor") == Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14), "锁定边框色默认 #FAAD14");
            Check(Col("ShadowColor") == Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF), "阴影色默认 #BFBFBF");
            Check(Col("ShadowActiveColor") == Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF), "活动阴影色默认 #69B1FF");
            Check(Col("LockedShadowColor") == Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14), "锁定阴影色默认 #FAAD14");
            Check(!File.Exists(settingsFile), "只读默认值不会平白落一份 settings.json");

            // 阴影带宽与峰值照上游 screenshotshadowprofile.h
            double marginPhysical = (double)shadowType.GetField("MarginPhysical", Stat)!.GetValue(null)!;
            double peakAlpha = (double)shadowType.GetField("PeakAlpha", Stat)!.GetValue(null)!;
            Check(Math.Abs(marginPhysical - 16) < 1e-9, "阴影带宽 16 物理像素");
            Check(Math.Abs(peakAlpha - 0.36) < 1e-9, "阴影峰值不透明度 0.36");

            // ============ ② 写入即落盘 + 归一化 ============
            Set("SetShowShadowByDefault", true);
            Check(File.Exists(settingsFile), "改一项才出现 settings.json");
            string json = File.ReadAllText(settingsFile);
            Check(json.Contains("\"ShowShadowByDefault\": true"), "settings.json 的 PinToScreen 段写着 ShowShadowByDefault = true");
            Check(json.Contains("\"PinToScreen\""), "落的是 PinToScreen 段（不是 Screenshot）");

            Set("SetZoomMode", "center");
            Set("SetZoomStepPercent", 25);
            Set("SetOpacityStepPercent", 1);
            Set("SetBorderColor", Color.FromRgb(0x01, 0x02, 0x03));
            Set("SetLockedShadowColor", Color.FromArgb(0x80, 0x11, 0x22, 0x33));
            Invalidate();
            Check(Text("ZoomMode") == "center", "滚轮锚点写成 center");
            Check(Number("ZoomStepPercent") == 25, "缩放步长写成 25");
            Check(Number("OpacityStepPercent") == 1, "不透明度步长写成 1");
            Check(Col("BorderColor") == Color.FromRgb(0x01, 0x02, 0x03), "6 位色值补成不透明");
            Check(Col("LockedShadowColor") == Color.FromArgb(0x80, 0x11, 0x22, 0x33), "带透明度的色值存得住");
            Check(File.ReadAllText(settingsFile).Contains("\"BorderColor\": \"#010203FF\""),
                "颜色按 #RRGGBBAA 落盘");

            // 乱写回落：锚点回光标位置、步长夹到 1–50、0 回默认
            Set("SetZoomMode", "corner");
            Set("SetZoomStepPercent", 999);
            Set("SetOpacityStepPercent", 0);
            Set("SetBorderColor", Colors.Transparent);
            Invalidate();
            Check(Text("ZoomMode") == "mouse_position", "认不出的锚点回落光标位置");
            Check(Number("ZoomStepPercent") == 50, "超出步长夹到 50");
            Check(Number("OpacityStepPercent") == 5, "步长 0 回落默认 5");
            Check(Col("BorderColor").A == 0, "全透明色也存得住（不是回落）");

            // 回到上游默认再进设置页 / 贴图窗口，答案才确定
            Set("SetShowBorderByDefault", true);
            Set("SetShowShadowByDefault", false);
            Set("SetShowWindowButtons", true);
            Set("SetConfirmBeforeClosingWindow", false);
            Set("SetZoomMode", "mouse_position");
            Set("SetZoomStepPercent", 10);
            Set("SetOpacityStepPercent", 5);
            Set("SetBorderColor", Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB));
            Set("SetBorderActiveColor", Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF));
            Set("SetLockedBorderColor", Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14));
            Set("SetShadowColor", Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF));
            Set("SetShadowActiveColor", Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF));
            Set("SetLockedShadowColor", Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14));
            Invalidate();

            // ============ ③ 设置页：控件回显 + 改动即落盘 ============
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);

            settings.GetType().GetField("_initializing", Inst)!.SetValue(settings, false);
            settings.GetType().GetMethod("PopulatePinWindow", Inst)!.Invoke(settings, null);

            ComboBox? Combo(string name) => settings!.FindName(name) as ComboBox;
            CheckBox? Box(string name) => settings!.FindName(name) as CheckBox;
            TextBlock? Label(string name) => settings!.FindName(name) as TextBlock;

            Check(Box("PinShowBorderBox")?.IsChecked == true, "「默认显示边框」回显为勾上");
            Check(Box("PinShowShadowBox")?.IsChecked == false, "「默认显示阴影」回显为不勾");
            Check(Box("PinWindowButtonsBox")?.IsChecked == true, "「显示窗口按钮」回显为勾上");
            Check(Box("PinConfirmCloseBox")?.IsChecked == false, "「关闭前确认」回显为不勾");
            Check(Combo("PinWheelModeCombo")?.Items.Count == 6, "滚轮锚点下拉是六项");
            Check(Combo("PinWheelModeCombo")?.SelectedIndex == 0, "滚轮锚点回显「光标位置」");
            Check(Combo("PinZoomStepCombo")?.SelectedItem is ComboBoxItem { Tag: int zoomStep } && zoomStep == 10,
                "缩放步长回显 10%");
            Check(Combo("PinOpacityStepCombo")?.SelectedItem is ComboBoxItem { Tag: int opStep } && opStep == 5,
                "不透明度步长回显 5%");
            Check(Label("PinBorderColorText")?.Text == "#DBDBDBFF", "边框色回显 #DBDBDBFF");
            Check(Label("PinLockedShadowColorText")?.Text == "#FAAD14FF", "锁定阴影色回显 #FAAD14FF");

            Combo("PinWheelModeCombo")!.SelectedIndex = 5;
            Pump(settings);
            Check(Text("ZoomMode") == "center", "下拉切「窗口中心」立刻写进设置");

            Combo("PinZoomStepCombo")!.SelectedIndex = 3;      // 预设 5 / 10 / 15 / 20 / 25 / 50
            Pump(settings);
            Check(Number("ZoomStepPercent") == 20, "下拉切 20% 立刻写进设置");

            Box("PinShowShadowBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("ShowShadowByDefault"), "勾上「默认显示阴影」立刻写进设置");
            Box("PinShowShadowBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("ShowShadowByDefault"), "取消「默认显示阴影」立刻写回");

            Box("PinWindowButtonsBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("ShowWindowButtons"), "取消「显示窗口按钮」立刻写进设置");
            Box("PinWindowButtonsBox")!.IsChecked = true;
            Pump(settings);

            // 颜色按钮走的是模态取色框，这里验「重新回填后外观跟上」这一半
            Set("SetBorderColor", Color.FromRgb(0x00, 0x80, 0x00));
            Invalidate();
            settings.GetType().GetMethod("PopulatePinWindow", Inst)!.Invoke(settings, null);
            Check(Label("PinBorderColorText")?.Text == "#008000FF", "重新回填：边框色文本跟着变");
            Check((settings.FindName("PinBorderColorSwatch") as Border)?.Background is SolidColorBrush green &&
                  green.Color == Color.FromRgb(0x00, 0x80, 0x00), "重新回填：色块也换成新色");

            settings.Close();
            settings = null;

            // ============ ④ 真贴图窗口：边框 / 阴影 / 按钮 / 状态色 ============
            Set("SetBorderColor", Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB));
            Set("SetZoomMode", "mouse_position");
            Set("SetZoomStepPercent", 10);
            Set("SetShowBorderByDefault", true);
            Set("SetShowShadowByDefault", false);
            Set("SetShowWindowButtons", true);
            Invalidate();

            var shot = BuildShot(320, 240);
            pin = manager.GetMethod("Pin")!.Invoke(null,
                new object[] { shot, new Int32Rect(300, 200, 320, 240), "screenshot", null }) as Window;
            Check(pin != null && pin.IsVisible, "贴图设置：先贴一张图");
            if (pin == null) return;

            var type = pin.GetType();
            pinId = (string)type.GetProperty("PinId", Inst)!.GetValue(pin)!;
            var applyChrome = type.GetMethod("ApplyChrome", Inst)!;
            applyChrome.Invoke(pin, null);
            Pump(pin);

            var frame = (Border)pin.FindName("Frame");
            var closeBtn = (Button)pin.FindName("CloseBtn");

            Check(frame.Visibility == Visibility.Visible, "默认画边框");
            Check(frame.BorderBrush is SolidColorBrush b1 && b1.Color == Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB),
                "普通状态边框用 border_color（#DBDBDB）");
            Check(Math.Abs(frame.BorderThickness.Left - 1) < 0.01, "普通状态边框 1px");

            // 记录里的两个开关跟着新建时的默认值走
            object? Record() => ((System.Collections.IEnumerable)store.GetMethod("Load", Stat)!.Invoke(null, null)!)
                .Cast<object>()
                .FirstOrDefault(r => (string)r.GetType().GetProperty("Id")!.GetValue(r)! == pinId);

            Check(Record() is { } r0 && (bool)r0.GetType().GetProperty("ShowBorder")!.GetValue(r0)!,
                "贴图历史记下「显示边框」");
            Check(Record() is { } r1 && !(bool)r1.GetType().GetProperty("ShowShadow")!.GetValue(r1)!,
                "贴图历史记下「不显示阴影」");

            // 关掉边框：收起 + 写回记录
            type.GetMethod("SetShowBorder", Inst)!.Invoke(pin, new object[] { false, true });
            Pump(pin);
            Check(frame.Visibility == Visibility.Collapsed, "关掉「显示边框」后描边收起");
            Check(Record() is { } r2 && !(bool)r2.GetType().GetProperty("ShowBorder")!.GetValue(r2)!,
                "关掉边框写回贴图历史");

            type.GetMethod("SetShowBorder", Inst)!.Invoke(pin, new object[] { true, true });
            Pump(pin);
            Check(frame.Visibility == Visibility.Visible, "再打开边框后描边回来");

            // 打开阴影：独立分层窗口，比贴图大一圈、垫在下面
            type.GetMethod("SetShowShadow", Inst)!.Invoke(pin, new object[] { true, true });
            Pump(pin);

            var shadow = Field(pin, "_shadow") as Window;
            Check(shadow != null && shadow.IsVisible, "打开「显示阴影」后阴影窗口出现");
            if (shadow == null) return;

            double pinScale = (double)Field(pin, "_scale")!;
            double margin = (double)shadowType.GetMethod("MarginDip", Stat)!
                .Invoke(null, new object[] { pinScale })!;

            Check(Math.Abs(shadow.Left - (pin.Left - margin)) < 0.6,
                $"阴影左边比贴图多出 {margin:0.#} DIP（{shadow.Left:0.#} vs {pin.Left:0.#}）");
            Check(Math.Abs(shadow.Top - (pin.Top - margin)) < 0.6,
                $"阴影顶边比贴图多出 {margin:0.#} DIP（{shadow.Top:0.#} vs {pin.Top:0.#}）");
            Check(Math.Abs(shadow.Width - (pin.Width + margin * 2)) < 0.6,
                $"阴影宽度 = 贴图 + 两侧边距（{shadow.Width:0.#} vs {pin.Width:0.#}）");
            Check(Math.Abs(shadow.Height - (pin.Height + margin * 2)) < 0.6,
                $"阴影高度 = 贴图 + 上下边距（{shadow.Height:0.#} vs {pin.Height:0.#}）");

            var blur = (Border)Field(shadow, "_blur")!;
            Check(blur.Background is SolidColorBrush sb && sb.Color.R == 0xBF && sb.Color.G == 0xBF && sb.Color.B == 0xBF,
                "普通状态阴影用 shadow_color（#BFBFBF）");
            Check(blur.Background is SolidColorBrush sb2 && Math.Abs(sb2.Color.A - Math.Round(255 * 0.36)) <= 1,
                $"阴影峰值不透明度 = 0.36 × 颜色 alpha（{((SolidColorBrush)blur.Background).Color.A}）");
            Check(blur.Effect is System.Windows.Media.Effects.BlurEffect, "阴影用高斯模糊逼近上游衰减曲线");

            // 拖动贴图：阴影跟着走
            pin.Left += 40;
            pin.Top += 24;
            Pump(pin);
            Check(Math.Abs(shadow.Left - (pin.Left - margin)) < 0.6 &&
                  Math.Abs(shadow.Top - (pin.Top - margin)) < 0.6,
                $"贴图挪动后阴影跟着走（{shadow.Left:0.#},{shadow.Top:0.#}）");

            // 锁定：边框与阴影一起换成 locked_* 色
            type.GetMethod("SetLocked", Inst)!.Invoke(pin, new object[] { true });
            Pump(pin);
            Check(frame.BorderBrush is SolidColorBrush lb && lb.Color == Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14),
                "锁定时边框用 locked_border_color（#FAAD14）");
            Check(blur.Background is SolidColorBrush ls && ls.Color.R == 0xFA && ls.Color.G == 0xAD && ls.Color.B == 0x14,
                "锁定时阴影用 locked_shadow_color（#FAAD14）");
            type.GetMethod("SetLocked", Inst)!.Invoke(pin, new object[] { false });
            Pump(pin);

            // 右上角按钮：设置关掉之后悬停也不画（出口仍在：双击 / 右键菜单）
            type.GetMethod("Window_MouseEnter", Inst)!
                .Invoke(pin, new object[] { pin, new MouseEventArgs(Mouse.PrimaryDevice, 0) });
            Pump(pin);
            Check(closeBtn.Visibility == Visibility.Visible, "悬停时右上角 ✕ 出现");

            Set("SetShowWindowButtons", false);
            Invalidate();
            type.GetMethod("Window_MouseEnter", Inst)!
                .Invoke(pin, new object[] { pin, new MouseEventArgs(Mouse.PrimaryDevice, 0) });
            Pump(pin);
            Check(closeBtn.Visibility == Visibility.Collapsed, "关掉「显示窗口按钮」后悬停也不画 ✕");

            Set("SetShowWindowButtons", true);
            Invalidate();
            type.GetMethod("Window_MouseEnter", Inst)!
                .Invoke(pin, new object[] { pin, new MouseEventArgs(Mouse.PrimaryDevice, 0) });
            Pump(pin);
            Check(closeBtn.Visibility == Visibility.Visible, "打开「显示窗口按钮」后 ✕ 回来");

            // ============ ⑤ 右键菜单两项 ============
            var menu = (ContextMenu)type.GetMethod("BuildMenu", Inst)!.Invoke(pin, null)!;
            MenuItem? Item(string header) =>
                menu.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == header);

            var borderItem = Item("显示边框");
            var shadowItem = Item("显示阴影");
            Check(borderItem is { IsCheckable: true }, "右键菜单有「显示边框」（可勾选）");
            Check(shadowItem is { IsCheckable: true }, "右键菜单有「显示阴影」（可勾选）");
            Check(borderItem?.IsChecked == true, "「显示边框」当前是勾上的");
            Check(shadowItem?.IsChecked == true, "「显示阴影」当前是勾上的");

            borderItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(pin);
            Check(frame.Visibility == Visibility.Collapsed, "点菜单「显示边框」就收起描边");
            Check(Record() is { } r3 && !(bool)r3.GetType().GetProperty("ShowBorder")!.GetValue(r3)!,
                "点菜单切边框也写回贴图历史");

            shadowItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(pin);
            Check(!shadow.IsVisible, "点菜单「显示阴影」就收起阴影");
            menu.IsOpen = false;

            // ============ ⑥ 滚轮：档位 + 锚点 ============
            Set("SetShowBorderByDefault", true);
            Set("SetZoomMode", "mouse_position");
            Set("SetZoomStepPercent", 10);
            Invalidate();

            type.GetMethod("SetShowShadow", Inst)!.Invoke(pin, new object[] { false, true });
            type.GetMethod("SetShowBorder", Inst)!.Invoke(pin, new object[] { true, true });
            type.GetMethod("SetZoom", Inst)!.Invoke(pin, new object[] { 1.0 });
            Pump(pin);

            void Wheel(int delta)
            {
                // 光标压在贴图中间：锚点才落在图上
                SetCursorPos((int)Math.Round((pin!.Left + pin.Width / 2) * pinScale),
                    (int)Math.Round((pin.Top + pin.Height / 2) * pinScale));
                Pump(pin);
                var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
                {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = pin,
                };
                pin.RaiseEvent(args);
                Pump(pin);
            }

            double Zoom() => (double)Field(pin!, "_zoom")!;

            Wheel(120);
            Check(Math.Abs(Zoom() - 1.1) < 1e-6, $"滚轮一格放大到 110%（实际 {Zoom():0.###}）");
            Wheel(-120);
            Check(Math.Abs(Zoom() - 1.0) < 1e-6, $"再滚回来回到 100%（实际 {Zoom():0.###}）");

            Set("SetZoomStepPercent", 25);
            Invalidate();
            Wheel(120);
            Check(Math.Abs(Zoom() - 1.25) < 1e-6, $"步长改成 25% 后一格到 125%（实际 {Zoom():0.###}）");
            Wheel(-120);
            Check(Math.Abs(Zoom() - 1.0) < 1e-6, $"再滚回来回到 100%（实际 {Zoom():0.###}）");

            // 拖边框得到的非整档 137%：向上一档到 150%
            Set("SetZoomStepPercent", 10);
            Invalidate();
            type.GetMethod("SetZoom", Inst)!.Invoke(pin, new object[] { 1.37 });
            Pump(pin);
            Wheel(120);
            Check(Math.Abs(Zoom() - 1.4) < 1e-6, $"137% 滚一格到 140%（实际 {Zoom():0.###}）");

            Save(Render(pin), Path.Combine(_outDir, "pin-settings.png"));

            // ============ ⑦ 关掉再贴回：按记录恢复边框 / 阴影 ============
            Check(!Flag("ConfirmBeforeClosingWindow"), "关闭贴图默认不确认（否则这里会弹模态框）");

            type.GetMethod("SetShowBorder", Inst)!.Invoke(pin, new object[] { false, true });
            type.GetMethod("SetShowShadow", Inst)!.Invoke(pin, new object[] { true, true });
            Pump(pin);

            type.GetMethod("RequestUserClose", Inst)!.Invoke(pin, null);
            Pump(pin);
            Check(!pin.IsVisible, "关闭贴图：窗口收起来了");
            pin = null;

            string asset = (string)store.GetMethod("AssetPath", Stat)!.Invoke(null, new object[] { pinId })!;
            for (int i = 0; i < 40 && !File.Exists(asset); i++) Thread.Sleep(50);

            var restored = manager.GetMethod("RestoreLastClosed", Stat)!.Invoke(null, null) as Window;
            Check(restored != null, "贴回最近关闭的那张");
            if (restored == null) return;

            Pump(restored);
            var rtype = restored.GetType();
            var rframe = (Border)restored.FindName("Frame");
            var rshadow = Field(restored, "_shadow") as Window;

            Check(!(bool)rtype.GetProperty("ShowBorder", Inst)!.GetValue(restored)!,
                "贴回沿用记录里的「不显示边框」");
            Check((bool)rtype.GetProperty("ShowShadow", Inst)!.GetValue(restored)!,
                "贴回沿用记录里的「显示阴影」");
            Check(rframe.Visibility == Visibility.Collapsed, "贴回后描边确实没画");
            Check(rshadow != null && rshadow.IsVisible, "贴回后阴影窗口也回来了");

            rtype.GetMethod("RequestUserClose", Inst)!.Invoke(restored, null);
            Pump(restored);
        }
        finally
        {
            settings?.Close();

            try
            {
                if (pin != null && pin.IsVisible)
                    pin.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(pin, null);
            }
            catch { /* 已经关了 */ }

            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

            if (pinId.Length > 0)
                store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { pinId });

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
        }
    }
}
