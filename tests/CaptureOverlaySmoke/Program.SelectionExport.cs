using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 选区背景不透明度与版面压缩（照上游 <c>background_opacity_percent</c> /
/// <c>layout_compaction_enabled</c> / <c>layout_compaction_spacing</c>）：
/// 设置卡片回显与落盘、真覆盖层的两块选区压缩导出、圆角 + 背景导出的像素。
/// </summary>
internal static partial class Program
{
    private static void SelectionExportSection()
    {
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        var selectionStore = asm.GetType("LTools.Features.CapturePro.SelectionStore")!;
        var flush = selectionStore.GetMethod("Flush", Stat)!;
        var forget = selectionStore.GetMethod("Forget", Stat)!;

        string root = Path.Combine(_outDir, "selectionexport-config");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "configs"));
        string uiFile = Path.Combine(root, "configs", "ui.json");

        configPathSet.Invoke(null, new object[] { Path.Combine(root, "LTools.json") });
        forget.Invoke(null, null);

        try
        {
            // ============ ① 设置页：默认值 / 回显 / 落盘 ============
            var settings = new LTools.Views.ConfigHelperWindow(Path.Combine(root, "LTools.json"));
            try
            {
                settings.Show();
                Pump(settings);
                typeof(LTools.Views.ConfigHelperWindow).GetField("_initializing", Inst)!
                    .SetValue(settings, false);
                typeof(LTools.Views.ConfigHelperWindow).GetMethod("PopulateSelectionExport", Inst)!
                    .Invoke(settings, null);

                var slider = (Slider)settings.FindName("CaptureBackgroundOpacitySlider")!;
                var text = (TextBlock)settings.FindName("CaptureBackgroundOpacityText")!;
                var compact = (CheckBox)settings.FindName("CaptureCompactionBox")!;
                var spacing = (TextBox)settings.FindName("CaptureCompactionSpacingBox")!;

                Check(Math.Abs(slider.Value) < 0.01, "背景不透明度默认 0");
                Check(text.Text == "0%", "摘要写 0%（实际 " + text.Text + "）");
                Check(compact.IsChecked == false, "版面压缩默认关");
                Check(spacing.Text == "0", "压缩留白默认 0（实际 " + spacing.Text + "）");

                slider.Value = 40;
                Pump(settings);
                flush.Invoke(null, null);
                Check(SelectionField(uiFile, "BackgroundOpacityPercent") == "40",
                    "滑到 40 就落盘（实际 " + SelectionField(uiFile, "BackgroundOpacityPercent") + "）");
                Check(text.Text == "40%", "摘要跟着滑条走");

                compact.IsChecked = true;
                spacing.Text = "6";
                typeof(LTools.Views.ConfigHelperWindow).GetMethod("CaptureCompactionSpacing_Changed", Inst)!
                    .Invoke(settings, new object?[] { null, new RoutedEventArgs() });
                Pump(settings);
                flush.Invoke(null, null);
                Check(SelectionField(uiFile, "LayoutCompaction") == "true", "压缩开关落盘");
                Check(SelectionField(uiFile, "LayoutCompactionSpacing") == "6", "留白落盘");

                spacing.Text = "999";
                typeof(LTools.Views.ConfigHelperWindow).GetMethod("CaptureCompactionSpacing_Changed", Inst)!
                    .Invoke(settings, new object?[] { null, new RoutedEventArgs() });
                Check(spacing.Text == "100", "留白夹到 100（实际 " + spacing.Text + "）");

                // 回到 6 / 0：下面用真覆盖层验压缩导出（背景留到第 ③ 步单独测）
                spacing.Text = "6";
                typeof(LTools.Views.ConfigHelperWindow).GetMethod("CaptureCompactionSpacing_Changed", Inst)!
                    .Invoke(settings, new object?[] { null, new RoutedEventArgs() });
                slider.Value = 0;
                flush.Invoke(null, null);
            }
            finally
            {
                settings.Close();
            }

            // ============ ② 真覆盖层：两块选区压缩导出 ============
            var shot = BuildShot(500, 250);
            var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, 500, 250));
            try
            {
                window.Show();
                Pump(window);

                var regions = (System.Collections.IList)typeof(CaptureOverlayWindow)
                    .GetField("_regions", Inst)!.GetValue(window)!;
                var doc = Field(window, "_doc")!;

                regions.Clear();
                InvokeValue(window, "CommitBox", new Rect(10, 10, 50, 50));
                InvokeValue(window, "CommitBox", new Rect(300, 10, 50, 50));
                Invoke(window, "SyncArea");
                Pump(window);

                Check(doc.GetType().GetProperty("CompactLayout")!.GetValue(doc) is true,
                    "覆盖层按设置打开了版面压缩");
                var area = (Int32Rect)doc.GetType().GetProperty("Area")!.GetValue(doc)!;
                Check(area.Width >= 340, $"两块选区的外接框：{area.Width} × {area.Height}");

                // 每块 50 DIP = 64 像素（125% 缩放 + 含两端像素），中间留白 6 像素
                var compacted = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
                Save(compacted, Path.Combine(_outDir, "selection-compacted.png"));
                Check(compacted.PixelWidth == 134,
                    $"压缩后宽度 = 64 + 6 + 64（实际 {compacted.PixelWidth}）");
                Check(compacted.PixelHeight == 64, $"压缩后高度 = 64（实际 {compacted.PixelHeight}）");
                Check(AlphaAt(compacted, 0, 32) > 200, "第一块还在最左");
                Check(AlphaAt(compacted, 66, 32) < 40, "两块之间只剩 6 像素留白（透明）");
                Check(AlphaAt(compacted, 72, 32) > 200, "第二块被贴到 70 之后");

                doc.GetType().GetProperty("CompactLayout")!.SetValue(doc, false);
                var plain = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
                Check(plain.PixelWidth >= 340, $"关掉压缩：宽度回到 {plain.PixelWidth}");
                Check(AlphaAt(plain, 150, 32) < 40, "关掉压缩：中间那片空白仍在");

                // ============ ③ 圆角 + 背景：角上透出淡化的底图 ============
                regions.Clear();
                InvokeValue(window, "CommitBox", new Rect(20, 20, 60, 40));
                Invoke(window, "SyncArea");
                Pump(window);

                var style = doc.GetType().GetProperty("Style")!.GetValue(doc)!;
                var styleType = style.GetType();
                var updated = Activator.CreateInstance(styleType)!;
                foreach (var property in styleType.GetProperties())
                {
                    if (!property.CanWrite) continue;
                    object? value = property.Name == "CornerRadius" ? 6.0
                        : property.Name == "BackgroundOpacity" ? 40
                        : property.GetValue(style);
                    property.SetValue(updated, value);
                }

                doc.GetType().GetProperty("Style")!.SetValue(doc, updated);
                var shaped = (BitmapSource)doc.GetType().GetMethod("Render")!.Invoke(doc, null)!;
                Save(shaped, Path.Combine(_outDir, "selection-background.png"));
                int corner = AlphaAt(shaped, 0, 0);
                Check(corner is > 70 and < 150, $"圆角外透出淡化的底图（alpha {corner}）");
                Check(AlphaAt(shaped, shaped.PixelWidth / 2, shaped.PixelHeight / 2) > 200,
                    "内容仍是实色");
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            forget.Invoke(null, null);
            configPathSet.Invoke(null, new object[] { oldConfigPath });
        }
    }

    /// <summary>读 <c>ui.json</c> 里 <c>CaptureSelection</c> 段的某个字段（没写 = 空串）。</summary>
    private static string SelectionField(string uiFile, string name)
    {
        if (!File.Exists(uiFile)) return "";
        using var json = JsonDocument.Parse(File.ReadAllText(uiFile));
        if (!json.RootElement.TryGetProperty("CaptureSelection", out var section)) return "";
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
