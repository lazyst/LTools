using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 悬浮工具条（2026-11-20，照 Snow Shot 的 <c>floating_toolbar/*</c>）：
/// 设置卡片回显 / 落盘、真窗口（图标模式只露第一个按钮 / 悬停展开 / 透明度）、
/// 模式切换与布局编辑器的增删。
/// </summary>
internal static partial class Program
{
    private static void FloatingBarSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var storeType = asm.GetType("LTools.Features.CapturePro.FloatingToolbarStore")!;
        var serviceType = asm.GetType("LTools.Features.CapturePro.FloatingToolbarService")!;
        var layoutType = asm.GetType("LTools.Features.CapturePro.FloatingToolbarLayout")!;
        var invalidate = storeType.GetMethod("Invalidate", Stat)!;

        string configRoot = Path.Combine(_outDir, "floatingbar-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        invalidate.Invoke(null, null);
        // 冒烟自己起了 WPF 宿主，App.OnStartup 不跑：这里手动订阅一次（Start 幂等）
        serviceType.GetMethod("Start", Stat)!.Invoke(null, null);

        Window? settings = null;
        try
        {
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateFloatingBar", Inst)!
                .Invoke(settings, null);

            CheckBox Box(string name) => (CheckBox)settings.FindName(name)!;
            ComboBox Combo(string name) => (ComboBox)settings.FindName(name)!;
            TextBox Input(string name) => (TextBox)settings.FindName(name)!;
            TextBlock Text(string name) => (TextBlock)settings.FindName(name)!;

            // ============ ① 默认值（逐字照上游 configurationschema） ============
            Check(Box("FloatingBarEnabledBox").IsChecked == false, "默认不显示悬浮工具条");
            Check(Equals(TagOf(Combo("FloatingBarModeCombo")), "icon"), "默认图标模式");
            Check(Equals(TagOf(Combo("FloatingBarDropCombo")), "pin_to_screen"), "默认拖放 = 贴到屏幕");
            Check(Input("FloatingBarOpacityBox").Text == "50", "默认不透明度 50");
            Check(Box("FloatingBarHideFullscreenBox").IsChecked == true, "默认全屏隐藏");
            Check(Box("FloatingBarHideCaptureBox").IsChecked == true, "默认截图时隐藏");
            Check(Text("FloatingBarLayoutText").Text.Contains("7 个按钮"), "布局摘要有按钮数");
            Check(!File.Exists(settingsFile), "只回显不写盘");

            // ============ ② 打开：真窗口按默认布局建出来 ============
            Box("FloatingBarEnabledBox").IsChecked = true;
            Pump(settings);
            Pump(settings);

            Check(FloatingBarField(settingsFile, "Enabled") == "true", "开关落盘");

            var bar = (Window?)serviceType.GetProperty("Window", Stat)!.GetValue(null);
            Check(bar != null, "开着的时候工具条窗口存在");
            if (bar == null) return;

            var ids = (IReadOnlyList<string>)bar.GetType()
                .GetProperty("TriggerIds", Inst)!.GetValue(bar)!;
            Check(ids.Count == 4, $"默认 4 叠（实际 {ids.Count}）");
            Check(ids[0] == "ltools.captureScreen", "第一叠是截图：" + ids[0]);

            var chrome = (FrameworkElement)bar.FindName("Chrome")!;
            Check(Math.Abs(chrome.Opacity - 0.5) < 0.01, $"不透明度 50%（实际 {chrome.Opacity:0.##}）");

            var row = (StackPanel)bar.FindName("Row")!;
            var visible = row.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible)
                .ToList();
            Check(visible.Count == 1, $"图标模式只露第一个按钮（实际 {visible.Count}）");

            bar.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = UIElement.MouseEnterEvent });
            Pump(bar);

            visible = row.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible)
                .ToList();
            Check(visible.Count == 4, $"鼠标停上去展开全部 4 个（实际 {visible.Count}）");
            Check(Math.Abs(chrome.Opacity - 1.0) < 0.01, "悬停时不透明度恢复 100%");
            Save(RenderHi(bar), Path.Combine(_outDir, "floatingbar-expanded.png"));

            bar.RaiseEvent(new System.Windows.Input.MouseEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, 0)
            { RoutedEvent = UIElement.MouseLeaveEvent });
            Pump(bar);

            // ============ ③ 一直展开：不再收起 ============
            SelectTag(Combo("FloatingBarModeCombo"), "toolbar");
            Pump(settings);
            Pump(settings);

            Check(FloatingBarField(settingsFile, "Mode") == "toolbar", "模式落盘");

            var bar2 = (Window?)serviceType.GetProperty("Window", Stat)!.GetValue(null);
            Check(bar2 != null, "切模式后工具条仍在");
            if (bar2 != null)
            {
                var row2 = (StackPanel)bar2.FindName("Row")!;
                Check(row2.Children.OfType<Button>().Count(b => b.Visibility == Visibility.Visible) == 4,
                    "一直展开模式下 4 个按钮都露出来");
            }

            // ============ ④ 布局编辑器：默认 4 叠 / 7 个收起，能新建一叠 ============
            var layout = layoutType.GetMethod("Default", Stat)!.Invoke(null, null)!;
            var editorType = asm.GetType("LTools.Views.CapturePro.FloatingToolbarLayoutWindow")!;
            var editor = (Window)Activator.CreateInstance(editorType, Inst, null,
                new[] { layout }, null)!;
            try
            {
                editor.Show();
                Pump(editor);

                var stackList = (ListBox)editor.FindName("StackList")!;
                var hiddenList = (ListBox)editor.FindName("HiddenList")!;
                Check(stackList.Items.Count == 4, $"编辑器列出 4 叠（实际 {stackList.Items.Count}）");
                Check(hiddenList.Items.Count == 7, $"收起 7 个（实际 {hiddenList.Items.Count}）");

                hiddenList.SelectedIndex = 0;
                editorType.GetMethod("NewStack_Click", Inst)!
                    .Invoke(editor, new object[] { editor, new RoutedEventArgs() });
                Check(stackList.Items.Count == 5, "「新建一叠」后 5 叠");
                Check(hiddenList.Items.Count == 6, "收起少一个");
            }
            finally
            {
                editor.Close();
            }

            // ============ ⑤ 关掉：窗口收起 ============
            Box("FloatingBarEnabledBox").IsChecked = false;
            Pump(settings);
            Pump(settings);
            Check(serviceType.GetProperty("Window", Stat)!.GetValue(null) == null, "关掉后窗口收起");
        }
        finally
        {
            settings?.Close();
            invalidate.Invoke(null, null);
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }

    private static string FloatingBarField(string settingsFile, string name)
    {
        if (!File.Exists(settingsFile)) return "";
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsFile));
        if (!doc.RootElement.TryGetProperty("FloatingToolbar", out var section)) return "";
        if (!section.TryGetProperty(name, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }
}
