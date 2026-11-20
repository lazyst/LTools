using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 光标旁那块取色 / 坐标卡（P9-r，照上游 <c>ScreenshotColorPickerWindow</c>）：
/// 选择工具下就跟着光标、11 × 11 放大预览（中间一格描边）、X / Y 坐标、
/// 色带（底色 = 取到的颜色、字色按底色挑）、显示时机、Shift+P 坐标口径、取色工具照旧。
/// </summary>
internal static partial class Program
{
    /// <summary>单跑这一段：<c>dotnet run --no-build -- picker</c>。</summary>
    private static void PickerCardSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        // 临时配置锚点：这一段会改 ui 的取色显示时机 / 坐标口径
        string root = Path.Combine(_outDir, "picker-config");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "configs"));

        configPathSet.Invoke(null, new object[] { Path.Combine(root, "LTools.json") });
        asm.GetType("LTools.Features.CapturePro.OverlayUiSettings")!
            .GetMethod("Invalidate", Stat)!.Invoke(null, null);

        var shot = BuildShot(1600, 900);
        var window = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, 1600, 900));

        try
        {
            window.Show();
            window.Activate();
            Pump(window);

            SetSelection(window, new Rect(200, 150, 600, 400));
            Pump(window);

            var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
            object Tool(string name) => Enum.Parse(toolType, name);

            var doc = typeof(CaptureOverlayWindow).GetProperty("Document", Private)!.GetValue(window)!;
            var marks = doc.GetType().GetProperty("Marks")!.GetValue(doc) as System.Collections.IList;

            PickerCardChecks(window, doc, Tool, marks?.Count ?? 0);
        }
        finally
        {
            window.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
            asm.GetType("LTools.Features.CapturePro.OverlayUiSettings")!
                .GetMethod("Invalidate", Stat)!.Invoke(null, null);
        }
    }

    /// <summary>卡片本身的断言（主链路与 <c>picker</c> 段共用）。</summary>
    /// <param name="marksBefore">调用前文档里有多少条标注（取色不该再加）。</param>
    private static void PickerCardChecks(CaptureOverlayWindow window, object doc,
        Func<string, object> Tool, int marksBefore)
    {
        var chip = Field(window, "PickerChip") as Border;
        var chipText = Field(window, "PickerText") as TextBlock;
        var chipPos = Field(window, "PickerPosText") as TextBlock;
        var preview = Field(window, "PickerPreview") as Image;
        var colorBar = Field(window, "PickerColorBar") as Border;

        var uiType = typeof(CaptureOverlayWindow).Assembly
            .GetType("LTools.Features.CapturePro.OverlayUiSettings")!;
        string savedMode = (string)uiType.GetProperty("ColorPickerDisplayMode")!.GetValue(null)!;
        string savedCoord = (string)uiType.GetProperty("ColorPickerCoordinateMode")!.GetValue(null)!;
        string savedFormat = (string)uiType.GetProperty("ColorPickerFormat")!.GetValue(null)!;
        double docScale = (double)doc.GetType().GetProperty("Scale")!.GetValue(doc)!;
        bool logical = (bool)uiType.GetProperty("LogicalUnits")!.GetValue(null)!;
        var monitorRect = (System.Windows.Int32Rect)doc.GetType().GetProperty("Monitor")!.GetValue(doc)!;
        int Shown(int physical) => logical ? (int)Math.Round(physical / docScale) : physical;

        var toImage = typeof(CaptureOverlayWindow).GetMethod("ToImage", Private)!;
        var (pointX, pointY) = ((int X, int Y))toImage.Invoke(window,
            new object[] { new Point(260, 210) })!;

        InvokeWith(window, "SetTool", Tool("Select"));
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));   // 底图上的红色块（250..450 × 200..320）
        Check(chip != null && chip.Visibility == Visibility.Visible, "选择工具下光标旁就跟着取色卡");
        Check(chipText != null && chipText.Text.StartsWith("#E15B4E"),
            "取到的是底图色块的颜色：" + chipText?.Text);
        Check(chipPos?.Text ==
              $"X: {Shown(monitorRect.X + pointX)} Y: {Shown(monitorRect.Y + pointY)}",
            "卡片里有全局坐标：" + chipPos?.Text);

        Check(preview?.Source is BitmapSource { PixelWidth: 11, PixelHeight: 11 },
            $"预览格是 11 × 11 源像素（{preview?.Source?.GetType().Name}）");
        if (preview?.Source is BitmapSource patchBitmap)
        {
            var one = new byte[4];
            patchBitmap.CopyPixels(new System.Windows.Int32Rect(5, 5, 1, 1), one, 4, 0);
            var center = Color.FromRgb(one[2], one[1], one[0]);
            Check(center == Color.FromRgb(0xE1, 0x5B, 0x4E),
                $"预览格正中那一格就是取样点（{center}）");
        }

        Check(colorBar?.Background is SolidColorBrush barBrush &&
              barBrush.Color == Color.FromRgb(0xE1, 0x5B, 0x4E),
            "色带底色 = 取到的颜色");
        Check(chipText?.Foreground is SolidColorBrush inkBrush &&
              inkBrush.Color == Color.FromArgb(224, 0, 0, 0),
            "浅底色上的色值用黑字（照 readableTextColor）");
        Check(Math.Abs(Canvas.GetLeft(chip!) - (260 + 16)) <= 1 &&
              Math.Abs(Canvas.GetTop(chip!) - (210 + 16)) <= 1,
            $"卡片摆在光标右下（{Canvas.GetLeft(chip!):0.#},{Canvas.GetTop(chip!):0.#}）");

        // 显示时机：默认「选区外不显示」——光标出了选区就收起
        var outside = new Point(1100, 640);
        InvokeWith(window, "ShowPickerAt", outside);
        Check(chip!.Visibility != Visibility.Visible, "默认「选区外不显示」：光标出了选区就收起");

        uiType.GetMethod("SetColorPickerDisplayMode")!.Invoke(null, new object[] { "always_show" });
        InvokeWith(window, "ShowPickerAt", outside);
        Check(chip.Visibility == Visibility.Visible, "「总是显示」：选区外也显示");
        uiType.GetMethod("SetColorPickerDisplayMode")!.Invoke(null, new object[] { savedMode });

        // Shift+P：全局 / 相对选区左上角
        var selTopLeft = ((int X, int Y))toImage.Invoke(window,
            new object[] { new Rect(200, 150, 600, 400).TopLeft })!;
        uiType.GetMethod("SetColorPickerCoordinateMode")!.Invoke(null, new object[] { "global" });
        InvokeWith(window, "RunScreenshotShortcut", "toggle_coordinate_mode");
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));
        Check(chip.Visibility == Visibility.Visible, "切到相对坐标后卡片还在");
        Check(chipPos?.Text ==
              $"X: {Shown(monitorRect.X + pointX - selTopLeft.X)} " +
              $"Y: {Shown(monitorRect.Y + pointY - selTopLeft.Y)}",
            "相对坐标以选区左上角为原点：" + chipPos?.Text);
        uiType.GetMethod("SetColorPickerCoordinateMode")!.Invoke(null, new object[] { savedCoord });
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));

        // HSB 写法（取色弹窗的第三种格式，取色卡也能切过去）
        uiType.GetMethod("SetColorPickerFormat")!.Invoke(null, new object[] { "hsb" });
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));
        Check(chipText?.Text == "hsb(5, 65%, 88%)",
            "取色卡按 hsb() 写色值：" + chipText?.Text);
        uiType.GetMethod("SetColorPickerFormat")!.Invoke(null, new object[] { savedFormat });
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));

        // 只有选择 / 取色工具下有这块卡片
        InvokeWith(window, "SetTool", Tool("Shape"));
        Check(chip!.Visibility != Visibility.Visible, "画标注的工具下不显示取色卡");
        InvokeWith(window, "SetTool", Tool("Select"));
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));
        Check(chip.Visibility == Visibility.Visible, "切回选择工具又出现");

        // 取色工具：同一块卡片，单击按当前格式复制（不关覆盖层、不落标注）
        InvokeWith(window, "SetTool", Tool("Picker"));
        InvokeWith(window, "ShowPickerAt", new Point(260, 210));
        Check(chip.Visibility == Visibility.Visible, "取色工具下也有这块卡片");
        InvokeWith(window, "PickColor", new Point(260, 210));
        Check(chip.Visibility == Visibility.Visible, "取色后覆盖层不关，卡片还在（可以连着取）");

        var marks = doc.GetType().GetProperty("Marks")!.GetValue(doc) as System.Collections.IList;
        Check((marks?.Count ?? 0) == marksBefore, "取色不落标注");

        Save(Render(window), Path.Combine(_outDir, "overlay-picker.png"));

        InvokeWith(window, "SetTool", Tool("Select"));
    }
}
