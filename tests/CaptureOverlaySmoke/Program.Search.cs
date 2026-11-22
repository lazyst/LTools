using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 设置搜索（2026-11-22）：索引从页面逻辑树自动抓、结果浮层、回车跳到那一页的那一项并高亮，
/// 以及「停止运行热键」同时出现在键鼠宏设置与运行日志页的入口。
/// </summary>
/// <remarks>
/// 与 <c>NavSection</c> 同一套做法：把 <c>ConfigStore.ConfigPath</c> 指到本段自己的临时目录，
/// 断言完恢复原路径，不给后面的段留脏配置。
/// </remarks>
internal static partial class Program
{
    private static void SearchSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "search-config");
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

            var type = settings.GetType();
            TextBox searchBox = (TextBox)(settings.FindName("SettingsSearchBox")
                ?? throw new InvalidOperationException("找不到搜索框"));
            ListBox results = (ListBox)(settings.FindName("SearchResults")
                ?? throw new InvalidOperationException("找不到结果列表"));
            Popup popup = (Popup)(settings.FindName("SearchPopup")
                ?? throw new InvalidOperationException("找不到结果浮层"));
            TextBlock empty = (TextBlock)(settings.FindName("SearchEmpty")
                ?? throw new InvalidOperationException("找不到空结果提示"));
            Canvas highlight = (Canvas)(settings.FindName("HighlightLayer")
                ?? throw new InvalidOperationException("找不到高亮层"));

            var ensureIndex = type.GetMethod("EnsureSearchIndex", Inst)!;
            var refresh = type.GetMethod("RefreshSearchResults", Inst)!;
            var activate = type.GetMethod("ActivateSearchEntry", Inst)!;

            static string LabelOf(object entry) =>
                (string)entry.GetType().GetProperty("Label")!.GetValue(entry)!;

            void Query(string text)
            {
                searchBox.Text = text;
                refresh.Invoke(settings, null);
                Pump(settings);
            }

            // ============ ① 索引：不维护手工清单，从页面逻辑树自动抓 ============
            ensureIndex.Invoke(settings, null);
            var entries = (IList)(type.GetField("_searchEntries", Inst)!.GetValue(settings)
                ?? throw new InvalidOperationException("索引没建出来"));
            Check(entries.Count > 150, $"索引条目数 > 150（实际 {entries.Count}）");

            var labels = entries.Cast<object>().Select(LabelOf).ToList();
            Check(labels.Contains("停止运行热键"), "宏设置页的停止键进了索引");
            Check(labels.Any(l => l.Contains("CapsLock")), "通用页的 CapsLock 开关进了索引");
            Check(labels.Contains("左键双击"), "通用页托盘的「左键双击」进了索引");

            // ============ ② 输入 → 浮层结果 ============
            Query("停止");
            Check(popup.IsOpen, "有结果时浮层打开");
            Check(results.Items.Count >= 2, $"「停止」至少命中两条（实际 {results.Items.Count}）");
            var stopLabels = results.Items.Cast<object>().Select(LabelOf).ToList();
            Check(stopLabels[0] == "停止运行热键", "标签开头命中的排第一：" + string.Join(" / ", stopLabels.Take(4)));
            Check(stopLabels.Contains("录制 / 停止热键"), "录制热键也在结果里");

            Query("托盘");
            var trayLabels = results.Items.Cast<object>().Select(LabelOf).ToList();
            Check(trayLabels.Contains("左键双击"), "「托盘」通过卡片标题命中「左键双击」");
            Check(empty.Visibility == Visibility.Collapsed, "有结果时不显示空提示");
            Save(RenderHi(settings), Path.Combine(_outDir, "settings-search.png"));

            Query("zzzz");
            Check(results.Items.Count == 0, "无匹配时结果为空");
            Check(empty.Visibility == Visibility.Visible, "无匹配时显示空提示");
            Check(popup.IsOpen, "空结果也开着浮层（写明没有匹配，而不是静默关掉）");

            Query("   ");
            Check(!popup.IsOpen, "清空查询就收起浮层");

            // ============ ③ 鼠标点击结果行也能跳（不只用回车） ============
            Query("停止");
            results.UpdateLayout();
            var row = results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            Check(row != null, "结果行生成了容器（点击之前）");
            if (row != null)
            {
                // 真实鼠标点击第一行：Popup 是独立窗口，点它会不会先被关掉、点完能不能跳，
                // 正是「搜索结果点不动」要守的那条路。
                settings.Activate();
                Pump(settings);
                Point center = row.PointToScreen(new Point(20, 11));
                SetCursorPos((int)center.X, (int)center.Y);
                PumpFor(80);
                // 真实点 Popup 时主窗口会先 Deactivated：模拟这一拍，
                // 鼠标在结果行上就不该关浮层，否则点一半窗就没了（用户报过「点了不跳」）
                type.GetMethod("Window_Deactivated", Inst)!
                    .Invoke(settings, new object?[] { settings, EventArgs.Empty });
                Pump(settings);
                Check(popup.IsOpen, "主窗口失活但鼠标停在结果行上：浮层不关");
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                PumpFor(30);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Pump(settings);
                Pump(settings);
                Check((string)type.GetField("_currentNav", Inst)!.GetValue(settings)! == "macros-settings",
                    "真鼠标点结果行跳到那一页");
                Check(!popup.IsOpen, "点完之后浮层收起");
            }

            // ============ ④ 回车跳转：切页 + 展开 + 高亮 ============
            Query("停止运行热键");
            Check(results.Items.Count > 0, "能搜到停止运行热键");
            object first = results.Items[0]!;
            activate.Invoke(settings, new[] { first });
            Pump(settings);
            Pump(settings);

            Check((string)type.GetField("_currentNav", Inst)!.GetValue(settings)! == "macros-settings",
                "跳到键鼠宏 · 设置：" + type.GetField("_currentNav", Inst)!.GetValue(settings));
            Check(!popup.IsOpen, "跳转后浮层收起");
            Check(string.IsNullOrEmpty(searchBox.Text), "跳转后搜索框清空（好接着搜下一个）");

            object? macroView = type.GetField("_pageMacros", Inst)!.GetValue(settings);
            Check(macroView != null, "索引时惰性页已经建出来（跳转不需要再等）");
            if (macroView != null)
            {
                var pageSettings = (FrameworkElement)macroView.GetType()
                    .GetField("Page_Settings", Inst)!.GetValue(macroView)!;
                Check(pageSettings.Visibility == Visibility.Visible, "键鼠宏页切到「设置」子页");
            }
            Check(highlight.Children.Count > 0, $"目标控件被高亮（高亮元素 {highlight.Children.Count}）");

            // ============ ④ 运行日志页的「停止运行热键」入口 ============
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("ApplyNavPage", Inst)!
                .Invoke(settings, new object[] { "logs-settings" });
            Pump(settings);
            var stopText = settings.FindName("StopHotkeyText") as TextBlock;
            Check(stopText != null && stopText.Text.Length > 0,
                "运行日志页显示当前停止键（" + stopText?.Text + "）");
            var gotoMacros = type.GetMethod("OpenStopHotkeySettings_Click", Inst);
            Check(gotoMacros != null, "运行日志页有「去键鼠宏设置…」入口");
            gotoMacros!.Invoke(settings, new object?[] { null, new RoutedEventArgs() });
            Pump(settings);
            Check((string)type.GetField("_currentNav", Inst)!.GetValue(settings)! == "macros-settings",
                "入口跳到键鼠宏 · 设置");
        }
        finally
        {
            settings?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }
}
