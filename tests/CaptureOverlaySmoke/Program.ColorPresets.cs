using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 颜色预设（P2-f，照上游 <c>screenshot/{stroke,fill,highlight}_color_presets</c>）：
/// 默认值 / 落盘 / 参数条换族 / 真点色块改样式 / 设置页入口与编辑框 / 恢复默认。
/// </summary>
/// <remarks>
/// 配置根指向冒烟自己的临时目录（不碰仓库里真正的 <c>configs/settings.json</c>），
/// 段尾搬回原来的 <c>ConfigStore.ConfigPath</c> 并丢掉缓存。
/// </remarks>
internal static partial class Program
{
    private static void ColorPresetsSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var presetType = asm.GetType("LTools.Features.CapturePro.ColorPresets")!;
        var familyType = asm.GetType("LTools.Features.CapturePro.ColorPresetFamily")!;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var presetWindowType = asm.GetType("LTools.Views.CapturePro.CaptureColorPresetsWindow")!;

        object Family(string name) => Enum.Parse(familyType, name);
        object Tool(string name) => Enum.Parse(toolType, name);
        string[] Hex(object family) =>
            (string[])presetType.GetMethod("GetHex", Stat)!.Invoke(null, new[] { family })!;
        string[] Set(object family, IReadOnlyList<string> colors) =>
            (string[])presetType.GetMethod("Set", Stat)!.Invoke(null, new object[] { family, colors })!;
        string[] Reset(object family) =>
            (string[])presetType.GetMethod("Reset", Stat)!.Invoke(null, new[] { family })!;
        void Invalidate() => presetType.GetMethod("Invalidate", Stat)!.Invoke(null, null);

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "colorpresets-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        var styleStoreType = asm.GetType("LTools.Features.CapturePro.CaptureStyleStore")!;
        styleStoreType.GetMethod("Forget", Stat)!.Invoke(null, null);

        Window? window = null;
        Window? dialog = null;
        Window? settings = null;
        Window? picker = null;
        try
        {
            // ============ ① 默认值（照上游 configurationschema）============
            var stroke = Hex(Family("Stroke"));
            var fill = Hex(Family("Fill"));
            var highlight = Hex(Family("Highlight"));

            Check(stroke.SequenceEqual(new[]
                {
                    "#F5222DFF", "#52C41AFF", "#1677FFFF", "#FADB14FF", "#000000FF",
                }), "空配置下描边预设是上游默认 5 色：" + string.Join(" ", stroke));
            Check(fill[0] == "#FFFFFF00", "填充预设第一个是透明（#FFFFFF00）：" + fill[0]);
            Check(highlight[4] == "#B37FEBFF", "荧光预设第五个是浅紫：" + highlight[4]);
            Check(stroke.Length == 5 && fill.Length == 5 && highlight.Length == 5, "三族都是恒 5 个");
            Check(!File.Exists(settingsFile), "只读缓存不会平白落一份 settings.json");

            // ============ ② 写盘：Set 落到 settings.json，Invalidate 后读回来一样 ============
            var custom = new[]
            {
                "#112233FF", "#445566FF", "#778899FF", "#AABBCCFF", "#DDEEFFFF",
            };
            string[] written = Set(Family("Stroke"), custom);
            Check(written.SequenceEqual(custom), "Set 返回真正落地的 5 个颜色");

            string text = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
            Check(text.Contains("\"StrokeColorPresets\""), "settings.json 里有 StrokeColorPresets");
            Check(text.Contains("#112233FF") && text.Contains("#DDEEFFFF"), "写入的是 8 位 #RRGGBBAA");

            Invalidate();
            Check(Hex(Family("Stroke")).SequenceEqual(custom), "清缓存重新读盘拿到刚写的 5 色");
            Check(Hex(Family("Fill"))[0] == "#FFFFFF00", "只改了描边，填充还是默认");

            // ============ ③ 设置页：入口与摘要 ============
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            Pump(settings);

            var initField = typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!;
            initField.SetValue(settings, false);
            typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateCaptureColorPresets", Inst)!
                .Invoke(settings, null);

            var presetsButton = settings.FindName("CaptureColorPresetsButton") as Button;
            var presetsHint = settings.FindName("CaptureColorPresetsHint") as TextBlock;
            Check(presetsButton != null, "设置页「截图 Pro」里有「颜色预设…」按钮");
            Check(presetsHint != null && presetsHint.Text.Contains("#112233FF"),
                "设置页摘要显示当前描边首色：" + presetsHint?.Text);
            Check(presetsHint != null && presetsHint.Text.Contains("各 5 个"), "摘要写明每族 5 个");

            // 共用颜色开关（上游 drawing/sync_tool_colors，默认关）
            var syncBox = settings.FindName("CaptureSyncColorsBox") as CheckBox;
            Check(syncBox != null && syncBox.IsChecked == false, "「共用一套颜色」默认不勾");

            syncBox!.IsChecked = true;
            Pump(settings);
            var current = styleStoreType.GetProperty("Current", Stat)!.GetValue(null)!;
            Check((bool)current.GetType().GetProperty("SyncColors")!.GetValue(current)!,
                "勾上后进程内立刻打开共享颜色");

            styleStoreType.GetMethod("Flush", Stat)!.Invoke(null, null);
            string uiFile = Path.Combine(configRoot, "configs", "ui.json");
            string uiText = File.Exists(uiFile) ? File.ReadAllText(uiFile) : "";
            Check(uiText.Contains("\"SyncColors\": true"), "开关落到 ui.json 的 CaptureStyles");

            syncBox.IsChecked = false;
            Pump(settings);
            Check(!(bool)current.GetType().GetProperty("SyncColors")!.GetValue(current)!,
                "取消勾选又关掉共享颜色");

            settings.Close();
            settings = null;

            // ============ ④ 参数条：换工具换族，色块就是预设 ============
            var shot = BuildShot(1200, 800);
            var overlay = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, 1200, 800));
            window = overlay;
            overlay.Show();
            Pump(overlay);
            SetSelection(overlay, new Rect(180, 140, 640, 420));
            Pump(overlay);

            var toolbar = Field(overlay, "Toolbar")!;
            var hosts = (System.Collections.IDictionary)toolbar.GetType()
                .GetField("_paramHosts", Private)!.GetValue(toolbar)!;

            FrameworkElement Host(string param)
            {
                foreach (System.Collections.DictionaryEntry entry in hosts)
                    if (entry.Key?.ToString() == param) return (FrameworkElement)entry.Value;
                throw new InvalidOperationException("参数条里没有 " + param);
            }

            List<RadioButton> Chips(FrameworkElement host) =>
                ((Panel)host).Children.OfType<RadioButton>().ToList();

            List<string> VisibleTips(FrameworkElement host) => Chips(host)
                .Where(chip => chip.Visibility == Visibility.Visible)
                .Select(chip => chip.ToolTip?.ToString() ?? "")
                .ToList();

            // 画笔：主色用描边预设（刚写进去的那 5 个）
            InvokeWith(window, "SetTool", Tool("Pen"));
            Pump(window);
            var colorHost = Host("Color");
            Check(Chips(colorHost).Count == 15, $"主色那一排三族都建出来（{Chips(colorHost).Count} 个）");
            Check(VisibleTips(colorHost).SequenceEqual(custom),
                "画笔的主色一排就是描边预设：" + string.Join(" ", VisibleTips(colorHost)));

            // 荧光笔 / 矩形荧光：主色换成荧光预设
            InvokeWith(window, "SetTool", Tool("Highlighter"));
            Pump(window);
            Check(VisibleTips(colorHost).SequenceEqual(highlight),
                "荧光笔的主色一排换成荧光预设：" + string.Join(" ", VisibleTips(colorHost)));

            InvokeWith(window, "SetTool", Tool("HighlightRect"));
            Pump(window);
            Check(VisibleTips(colorHost).SequenceEqual(highlight), "矩形荧光也用荧光预设");

            InvokeWith(window, "SetTool", Tool("Spotlight"));
            Pump(window);
            Check(VisibleTips(colorHost).SequenceEqual(custom), "聚光用描边预设（上游 textColors）");

            // 填充色 / 文字描边色 / 箭头外描边各用自己那一族
            Check(VisibleTips(Host("FillColor")).SequenceEqual(fill), "填充色一排用填充预设");
            Check(VisibleTips(Host("ArrowOutlineColor")).SequenceEqual(fill), "箭头外描边用填充预设");
            Check(VisibleTips(Host("TextStrokeColor")).SequenceEqual(custom), "文字描边色用描边预设");

            // ============ ⑤ 真点一个色块：写进这一族的样式 ============
            InvokeWith(window, "SetTool", Tool("Pen"));
            Pump(window);
            var third = Chips(colorHost).First(chip => chip.Visibility == Visibility.Visible && chip.ToolTip?.ToString() == "#778899FF");
            third.IsChecked = true;
            Pump(window);

            var styles = toolbar.GetType().GetField("_styles", Inst)!.GetValue(toolbar)!;
            var penStyle = styles.GetType().GetMethod("For")!.Invoke(styles, new[] { Tool("Pen") })!;
            var penColor = (System.Windows.Media.Color)penStyle.GetType().GetProperty("Color")!.GetValue(penStyle)!;
            Check(presetType.GetMethod("Format", Stat)!.Invoke(null, new object[] { penColor })!.ToString() == "#778899FF",
                "点第三个色块把画笔颜色改成 #778899FF");

            window.Close();
            window = null;

            // ============ ⑥ 编辑框：三排各 5 个 / 恢复默认只动这一族 ============
            dialog = (Window)Activator.CreateInstance(presetWindowType, Inst, null,
                Array.Empty<object>(), null)!;
            dialog.Show();
            Pump(dialog);

            foreach (var (name, label) in new[]
                     {
                         ("StrokePalette", "描边"), ("FillPalette", "填充"), ("HighlightPalette", "荧光"),
                     })
            {
                var panel = dialog.FindName(name) as WrapPanel;
                Check(panel != null && panel.Children.Count == 5, $"{label}一排 5 个色块");
            }

            var drafts = (System.Collections.IDictionary)presetWindowType
                .GetField("_drafts", Inst)!.GetValue(dialog)!;
            Check(((string[])drafts[Family("Stroke")]!).SequenceEqual(custom), "编辑框打开时带出当前的描边预设");

            presetWindowType.GetMethod("Reset_Click", Inst)!.Invoke(dialog,
                new object[] { new Button { Tag = "Stroke" }, new RoutedEventArgs() });
            Pump(dialog);

            Check(((string[])drafts[Family("Stroke")]!).SequenceEqual(new[]
                {
                    "#F5222DFF", "#52C41AFF", "#1677FFFF", "#FADB14FF", "#000000FF",
                }), "「恢复默认」把描边草稿换回上游 5 色");
            Check(((string[])drafts[Family("Fill")]!)[0] == "#FFFFFF00", "恢复默认描边不动填充");

            var strokePanel = dialog.FindName("StrokePalette") as WrapPanel;
            Check(strokePanel!.Children.Count == 5, "恢复默认后色块仍是 5 个");

            dialog.Close();
            dialog = null;

            // ============ ⑦ 取色框：十六进制 / RGBA 互相联动，预览铺棋盘格 ============
            var colorDialogType = asm.GetType("LTools.Views.CapturePro.CaptureColorDialog")!;
            picker = (Window)Activator.CreateInstance(colorDialogType, Inst, null,
                new object[] { System.Windows.Media.Color.FromArgb(0x80, 0x11, 0x22, 0x33) }, null)!;
            picker.Show();
            Pump(picker);

            var hexBox = picker.FindName("HexBox") as TextBox;
            var rBox = picker.FindName("RBox") as TextBox;
            var gBox = picker.FindName("GBox") as TextBox;
            var bBox = picker.FindName("BBox") as TextBox;
            var aBox = picker.FindName("ABox") as TextBox;
            var previewBorder = picker.FindName("Preview") as Border;

            Check(hexBox?.Text == "#11223380", "取色框先填好当前颜色：" + hexBox?.Text);
            Check(rBox?.Text == "17" && gBox?.Text == "34" && bBox?.Text == "51" && aBox?.Text == "128",
                $"四个分量按十进制显示（{rBox?.Text}/{gBox?.Text}/{bBox?.Text}/{aBox?.Text}）");
            Check(previewBorder?.Background is System.Windows.Media.DrawingBrush,
                "带透明度时预览铺棋盘格（不是一块看不见的空色）");

            rBox!.Text = "255";
            Pump(picker);
            Check(hexBox?.Text == "#FF223380", "改 R 分量后十六进制跟着变：" + hexBox?.Text);
            Check(previewBorder?.Background is System.Windows.Media.SolidColorBrush == false,
                "alpha 还是 128，预览仍是棋盘格");

            hexBox!.Text = "#00FF00";
            Pump(picker);
            Check(gBox?.Text == "255" && rBox.Text == "0" && aBox.Text == "255",
                $"写 6 位十六进制后分量跟着变、alpha 补成 255（{rBox.Text}/{gBox.Text}/{aBox.Text}）");
            Check(previewBorder?.Background is System.Windows.Media.SolidColorBrush,
                "alpha 回到 255 时预览就是纯色");

            picker.Close();
            picker = null;

            // ============ ⑧ Reset 真落盘 ============
            Reset(Family("Stroke"));
            string after = File.Exists(settingsFile) ? File.ReadAllText(settingsFile) : "";
            Check(after.Contains("#F5222DFF"), "Reset 把默认值写回 settings.json");

            Invalidate();
            Check(Hex(Family("Stroke"))[0] == "#F5222DFF", "清缓存重新读盘拿到默认描边");
        }
        finally
        {
            foreach (var open in new[] { picker, dialog, settings, window })
            {
                if (open == null) continue;
                try { open.Close(); } catch { /* 已经关了 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            styleStoreType.GetMethod("Forget", Stat)!.Invoke(null, null);
            Invalidate();
        }
    }
}
