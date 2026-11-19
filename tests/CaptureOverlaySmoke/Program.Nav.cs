using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 设置页「截图 Pro」一级导航 + 二级分类（2026-11-20）：左栏结构、八个子页、
/// 点父项落到默认子页、记住上次子页、展开状态与当前页落盘、
/// 以及「这些设置真的搬出了通用页」（控件最近的 ScrollViewer 就是它自己的子页）。
/// </summary>
internal static partial class Program
{
    private static void NavSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "nav-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");

        configPathSet.Invoke(null, new object[] { configPath });

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var applyNav = typeof(LTools.Views.ConfigHelperWindow).GetMethod("ApplyNavPage", Inst)!;
            void Nav(string tag) { applyNav.Invoke(settings, new object[] { tag }); Pump(settings); }

            T Named<T>(string name) where T : class =>
                (T)(settings.FindName(name) ?? throw new InvalidOperationException("找不到 " + name));
            ScrollViewer Page(string name) => Named<ScrollViewer>(name);

            // ============ ① 左栏结构 ============
            var navList = Named<StackPanel>("NavList");
            var parents = navList.Children.OfType<RadioButton>().ToList();
            var navCapture = Named<RadioButton>("NavCapture");
            Check(Equals(navCapture.Tag, "capture"), "左栏有「截图 Pro」父项");
            Check(parents.Contains(navCapture), "「截图 Pro」是一级项");
            Check(!parents.Any(rb => Equals(rb.Tag, "output")), "「截图输出」不再是一级项（已并进截图 Pro）");
            Check(Named<TextBlock>("NavCaptureChevron").Text.Length > 0, "父项带展开箭头");

            var sub = Named<StackPanel>("CaptureSubNav");
            var items = sub.Children.OfType<RadioButton>().ToList();
            Check(items.Count == 8, $"二级分类 8 项（实际 {items.Count}）");
            Check(string.Join(",", items.Select(i => i.Tag)) ==
                  "capture-general,capture-behavior,capture-drawing,capture-overlay,"
                  + "capture-ocr,capture-pin,capture-record,capture-output",
                "二级项顺序与 tag：" + string.Join(",", items.Select(i => i.Tag)));
            Check(string.Join(",", items.Select(i => i.Content?.ToString())) ==
                  "常规,截图行为,绘制行为,界面与参考线,文字识别,贴图窗口,录屏,截图输出",
                "二级项中文名：" + string.Join(",", items.Select(i => i.Content)));
            Check(items.All(i => i.GroupName == "subnav-capture"), "二级项同一 GroupName（互斥）");

            // ============ ② 八个子页都在 ============
            var pages = new (string Page, string Viewer, string Anchor)[]
            {
                ("general", "Page_CaptureGeneral", "ScreenshotDelayCombo"),
                ("behavior", "Page_CaptureBehavior", "CaptureCursorBox"),
                ("drawing", "Page_CaptureDrawing", "CaptureAutoSelectBox"),
                ("overlay", "Page_CaptureOverlay", "CaptureToolbarSizeCombo"),
                ("ocr", "Page_CaptureOcr", "OcrModelCombo"),
                ("pin", "Page_CapturePin", "PinShowBorderBox"),
                ("record", "Page_CaptureRecord", "RecordClarityCombo"),
                ("output", "Page_Output", "ExportDirBox"),
            };

            foreach (var (_, viewer, _) in pages)
                Check(settings.FindName(viewer) is ScrollViewer, $"{viewer} 存在");

            // 设置真的搬出来了：控件最近的 ScrollViewer 就是它自己的子页（不在「通用」里）
            string? OwnerPage(object control)
            {
                DependencyObject? node = (DependencyObject)control;
                while (node != null)
                {
                    if (node is ScrollViewer viewer) return viewer.Name;
                    DependencyObject? next = System.Windows.LogicalTreeHelper.GetParent(node);
                    next ??= System.Windows.Media.VisualTreeHelper.GetParent(node);
                    node = next;
                }
                return null;
            }

            foreach (var (_, viewer, anchor) in pages)
            {
                object? control = settings.FindName(anchor);
                Check(control != null, $"{anchor} 存在");
                if (control == null) continue;
                Check(OwnerPage(control) == viewer,
                    $"{anchor} 在 {viewer} 里（实际 {OwnerPage(control)}）");
            }
            Check(OwnerPage(Named<CheckBox>("CapsLockModifierBox")) == "Page_General",
                "「通用」只剩工具开关 / 开机自启 / 运行提示 / 托盘");

            // ============ ③ 点父项 → 默认子页「常规」 ============
            Nav("capture");
            Check(Page("Page_CaptureGeneral").Visibility == Visibility.Visible, "点父项先落到「常规」");
            Check(Page("Page_General").Visibility == Visibility.Collapsed, "「通用」页让位");
            Check(items[0].IsChecked == true, "「常规」子项高亮");
            Check(navCapture.IsChecked == true, "父项高亮");
            Check(Named<Border>("CaptureSubNavHost").Visibility == Visibility.Visible,
                "进分组后二级下拉展开");
            Save(RenderHi(settings), Path.Combine(_outDir, "settings-capture-general.png"));

            // ============ ④ 逐个切子页：只有它可见 ============
            foreach (var (page, viewer, _) in pages)
            {
                Nav("capture-" + page);

                Check(Page(viewer).Visibility == Visibility.Visible, $"{page}：{viewer} 可见");
                foreach (var (otherPage, otherViewer, _) in pages)
                {
                    if (otherPage == page) continue;
                    Check(Page(otherViewer).Visibility == Visibility.Collapsed,
                        $"{page}：{otherViewer} 收起");
                }

                var want = items.First(i => Equals(i.Tag, "capture-" + page));
                Check(want.IsChecked == true, $"{page}：对应子项高亮");
                Check(items.Count(i => i.IsChecked == true) == 1, $"{page}：只高亮一项");
                if (page is "ocr" or "overlay")
                    Save(RenderHi(settings), Path.Combine(_outDir, $"settings-capture-{page}.png"));
            }

            // ============ ⑤ 记住上次子页 ============
            Nav("capture-record");
            Nav("actions");
            Check(items.All(i => i.IsChecked != true), "离开分组后子项不再高亮（不会两处同时亮）");
            Nav("capture");
            Check(Page("Page_CaptureRecord").Visibility == Visibility.Visible,
                "再点父项回到上次看的「录屏」");

            // ============ ⑥ 落盘：当前页 + 展开状态 ============
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("FlushNavPersist", Inst)!
                .Invoke(settings, null);
            string uiPath = Path.Combine(configRoot, "configs", "ui.json");
            // 落盘走后台任务（不占 UI 线程）：等它写完，最多 3 秒
            for (int i = 0; i < 30 && !File.Exists(uiPath); i++) PumpFor(100);
            Check(File.Exists(uiPath), "切页写出 ui.json");
            if (File.Exists(uiPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(uiPath));
                var root = doc.RootElement;
                Check(root.TryGetProperty("LastNavPage", out var last) && last.GetString() == "capture-record",
                    "LastNavPage 记的是具体子页（" + (root.TryGetProperty("LastNavPage", out var l2) ? l2.GetString() : "缺") + "）");
                var groups = root.TryGetProperty("NavExpandedGroups", out var g) && g.ValueKind == JsonValueKind.Array
                    ? g.EnumerateArray().Select(x => x.GetString()).ToList()
                    : new List<string?>();
                Check(groups.Contains("capture"), "展开状态记进 NavExpandedGroups");
            }

            // ============ ⑦ 存的是父项 / 旧 tag 时怎么落 ============
            var normalize = typeof(LTools.Views.ConfigHelperWindow)
                .GetMethod("NormalizeSavedNav", Stat)!;
            Check((string)normalize.Invoke(null, new object?[] { "capture" })! == "capture-general",
                "存的是父项 → 落到默认子页「常规」");
            Check((string)normalize.Invoke(null, new object?[] { "capture-ocr" })! == "capture-ocr",
                "存的子页原样保留");
            Check((string)normalize.Invoke(null, new object?[] { "output" })! == "actions",
                "「截图输出」不再是合法一级页（旧值回到动作管理，不做旧 tag 兼容）");
        }
        finally
        {
            settings?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}
