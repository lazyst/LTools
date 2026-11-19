using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图输出（P7-d，照 Snow Shot 的 <c>screenshot/*</c> 输出设置）：文件名模板、格式 / 质量 /
/// 压缩真的写进文件、执行完自动保存、复制时带上文件、保存结果条。
/// </summary>
/// <remarks>
/// 真写文件到 <c>out/export</c>（不碰用户「图片」文件夹）；设置走 <see cref="ExportSettings"/>
/// 的静态缓存，段尾原样还原。系统另存为对话框没法自动点，只验「会不会走它」的分支。
/// </remarks>
internal static partial class Program
{
    private static void ExportSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var settingsType = asm.GetType("LTools.Features.CapturePro.ExportSettings")!;
        var fileType = asm.GetType("LTools.Config.ExportFile")!;
        var saveType = asm.GetType("LTools.Features.CapturePro.CaptureSave")!;
        var contextType = asm.GetType("LTools.Features.CapturePro.ExportNameContext")!;

        string dir = Path.Combine(_outDir, "export");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // 先备份当前设置，段尾还原（别把开发机的「截图输出」改掉）
        var backup = Activator.CreateInstance(fileType)!;
        settingsType.GetMethod("Capture")!.Invoke(null, new[] { backup });

        var names = contextType.GetMethod("Of", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { "LTSmoke.exe", "冒烟 窗口 - 标题" })!;

        object Apply(string format = null, string compression = null, int quality = 100,
            string manual = null, string auto = null, bool showResult = false, bool copyFile = false,
            string[] autoScenarios = null, string dialog = null)
        {
            var file = Activator.CreateInstance(fileType)!;
            void Set(string name, object value) =>
                fileType.GetProperty(name)!.SetValue(file, value);
            void SetList(string name, IEnumerable<string> values)
            {
                var property = fileType.GetProperty(name)!;
                property.SetValue(file, values.ToList());
            }

            Set("Format", format ?? "png");
            Set("Quality", quality);
            Set("Compression", compression ?? "medium");
            Set("SaveDirectory", dir);
            Set("ManualFileNameFormat", manual ?? "T_{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}");
            Set("AutoFileNameFormat", auto ?? "A_{YYYY}-{MM}-{DD}_{HH}-{mm}-{ss}");
            Set("SaveAsDialog", dialog ?? "builtin");
            Set("ShowSaveResult", showResult);
            Set("CopyImageFileToClipboard", copyFile);
            SetList("AutoSaveAfterExecutionActions", autoScenarios ?? Array.Empty<string>());

            settingsType.GetMethod("Apply")!.Invoke(null, new[] { file });
            return file;
        }

        string Write(BitmapSource image, string template) =>
            (string)CallStatic(saveType, "WriteExport", image, template, names)!;

        string[] Files() => Directory.GetFiles(dir).OrderBy(f => f).ToArray();

        var image = BuildShot(120, 80);

        // ---------- 1) 模板 + 目录 + 扩展名 ----------
        Apply("png");
        int before = Files().Length;

        string first = Write(image, "T_{YYYY}");
        Check(File.Exists(first), "按设置写出一份 png：" + Path.GetFileName(first));
        Check(first.StartsWith(dir, StringComparison.OrdinalIgnoreCase), "落在配置的目录里");
        Check(Path.GetExtension(first) == ".png", "png 扩展名");
        Check(first.Contains(DateTime.Now.ToString("yyyy")), "文件名模板里的 {YYYY} 展开了");

        byte[] bytes = File.ReadAllBytes(first);
        Check(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47,
            "写出来的是真 PNG（文件头）");
        Check(bytes.Length > 100, $"文件不是空的（{bytes.Length} 字节）");

        // 同一模板再来一次：不覆盖前一张，自动加序号
        string second = Write(image, "T_{YYYY}");
        Check(second != first && File.Exists(second), "重名自动错开：" + Path.GetFileName(second));
        Check(Files().Length == before + 2, "两次写文件真的多出两个文件");

        // ---------- 2) {AN} / {WN} ----------
        string withNames = Write(image, "N_{AN}");
        Check(Path.GetFileNameWithoutExtension(withNames).Contains("LTSmoke"), "{AN} 进文件名");

        string withWindow = Write(image, "W_{WN}");
        Check(Path.GetFileNameWithoutExtension(withWindow).Contains("冒烟"), "{WN} 进文件名");

        // 文件名里不能出现的字符被换掉（窗口标题里的 : / 等）
        string dirty = Write(image, "D_a:b|c");
        Check(Path.GetFileNameWithoutExtension(dirty) == "D_a_b_c", "非法字符换成 _：" + Path.GetFileName(dirty));

        // ---------- 3) 格式 / 质量 / 压缩 ----------
        Apply("jpeg", quality: 60);
        string jpeg = Write(image, "J_{YYYY}");
        Check(Path.GetExtension(jpeg) == ".jpg", "jpeg 扩展名（.jpg）");
        byte[] jpegBytes = File.ReadAllBytes(jpeg);
        Check(jpegBytes.Length > 2 && jpegBytes[0] == 0xFF && jpegBytes[1] == 0xD8, "写出来的是真 JPEG");

        Apply("bmp");
        string bmp = Write(image, "B");
        Check(Path.GetExtension(bmp) == ".bmp" && File.ReadAllBytes(bmp)[0] == 0x42, "bmp 扩展名与文件头");

        // PNG 压缩力度：同一张图的低 / 高两档，高不该更大
        var flat = ExportFlat(200, 150);
        Apply("png", compression: "low");
        long low = new FileInfo(Write(flat, "C_low")).Length;
        Apply("png", compression: "high");
        long high = new FileInfo(Write(flat, "C_high")).Length;
        Check(high <= low, $"PNG 压缩力度真的起作用（低 {low} / 高 {high}）");

        // ---------- 4) 执行完自动保存 ----------
        Apply("png", autoScenarios: Array.Empty<string>());
        int beforeAuto = Files().Length;
        object auto = CallStatic(saveType, "AutoSave", image, "copy_screenshot", names)!;
        Check(auto == null, "没勾「复制截图」时不自动保存");
        Check(Files().Length == beforeAuto, "没勾时一个文件都不写");

        Apply("png", autoScenarios: new[] { "copy_screenshot" });
        object saved = CallStatic(saveType, "AutoSave", image, "copy_screenshot", names)!;
        Check(saved is string path && File.Exists(path), "勾了「复制截图」就顺带存一份");
        Check(saved is string p2 && Path.GetFileName(p2).StartsWith("A_"), "自动保存用自己的模板");

        object other = CallStatic(saveType, "AutoSave", image, "pin_to_screen", names)!;
        Check(other == null, "没勾的场景不会顺带写");

        // ---------- 5) 复制截图（带不带文件） ----------
        Apply("png", copyFile: false);
        int beforeCopy = Files().Length;
        bool copied = (bool)CallStatic(saveType, "CopyImage", image, "copy_screenshot", names, null)!;
        Check(copied, "复制到剪贴板成功");
        Check(Files().Length == beforeCopy, "没开「复制图片文件」时不写文件");
        // 剪贴板是全局独占资源：别的进程（输入法 / 剪贴板工具 / 我们的另一次复制）可能正好占着，
        // 读一次拿不到就再试几次。**别只读一次**——以前这里偶发红过一次，单独重跑又是绿的。
        bool imageOnClipboard = false;
        for (int attempt = 0; attempt < 12 && !imageOnClipboard; attempt++)
        {
            try { imageOnClipboard = Clipboard.ContainsImage(); }
            catch { /* 剪贴板被占，过一会儿再试 */ }
            if (!imageOnClipboard) Thread.Sleep(40);
        }
        Check(imageOnClipboard, "剪贴板里真有图片");

        Apply("png", copyFile: true);
        int beforeFiles = Files().Length;
        bool copiedWithFile = (bool)CallStatic(saveType, "CopyImage", image, "copy_screenshot", names, null)!;
        Check(copiedWithFile, "带文件复制成功");
        Check(Files().Length == beforeFiles + 1, "开「复制图片文件」时真的多写一份");

        var drop = new List<string>();
        for (int attempt = 0; attempt < 12 && drop.Count == 0; attempt++)
        {
            try { drop = Clipboard.GetFileDropList().Cast<string>().ToList(); }
            catch { /* 剪贴板被占，过一会儿再试 */ }
            if (drop.Count == 0) Thread.Sleep(40);
        }
        Check(drop.Count == 1 && File.Exists(drop[0]), "剪贴板里带着那个文件（CF_HDROP）");
        Check(drop.Count == 1 && drop[0].StartsWith(dir, StringComparison.OrdinalIgnoreCase),
            "带的是配置目录里刚写的那一份：" + (drop.Count > 0 ? Path.GetFileName(drop[0]) : "（没读到）"));

        // ---------- 6) 快速保存 ----------
        Apply("png", auto: "Q_{YYYY}");
        int beforeQuick = Files().Length;
        bool quick = (bool)saveType.GetMethod("TryQuickSave", new[] { typeof(BitmapSource) })!
            .Invoke(null, new object[] { image })!;
        Check(quick, "快速保存成功");
        Check(Files().Length == beforeQuick + 1, "快速保存写了一份");
        Check(Files().Any(f => Path.GetFileName(f).StartsWith("Q_")), "快速保存用自动保存模板");

        // ---------- 7) 另存为两种对话框 ----------
        Apply("png", dialog: "system");
        Check((bool)settingsType.GetProperty("UseSystemDialog")!.GetValue(null)!,
            "设置成 system 时走 Windows 对话框");
        Check((string)settingsType.GetField("SaveAsDialog")!.GetValue(null)! == "system",
            "system 被原样读进缓存");
        Apply("png", dialog: "builtin");
        Check(!(bool)settingsType.GetProperty("UseSystemDialog")!.GetValue(null)!,
            "builtin 走自绘对话框");

        // ---------- 8) 保存结果条（自绘对话框：「保存后显示结果」） ----------
        Apply("png", showResult: true);
        var window = (CaptureSaveWindow)typeof(CaptureSaveWindow)
            .GetConstructor(new[] { typeof(BitmapSource), typeof(string), contextType })!
            .Invoke(new[] { (object)image, "结果条_B", names });
        window.Show();
        Pump(window);

        var resultBar = (FrameworkElement)Field(window, "ResultBar")!;
        var saveButton = (System.Windows.Controls.Button)Field(window, "SaveBtn")!;
        var cancelButton = (System.Windows.Controls.Button)Field(window, "CancelBtn")!;
        Check(resultBar.Visibility != Visibility.Visible, "还没保存时不显示结果条");

        typeof(CaptureSaveWindow).GetMethod("Save_Click", Private)!
            .Invoke(window, new object[] { null, new RoutedEventArgs() });
        Pump(window);

        var savedProperty = typeof(CaptureSaveWindow).GetProperty("Saved")!;
        var pathProperty = typeof(CaptureSaveWindow).GetProperty("SavedPath")!;
        Check((bool)savedProperty.GetValue(window)!, "点保存真的存下去了");
        string savedPath = (string)pathProperty.GetValue(window)!;
        Check(File.Exists(savedPath), "保存的文件在磁盘上：" + Path.GetFileName(savedPath));
        Check(resultBar.Visibility == Visibility.Visible, "保存后结果条显示出来");
        Check(((System.Windows.Controls.TextBlock)Field(window, "ResultText")!).Text.Contains("已保存"),
            "结果条写了「已保存」");
        Check(saveButton.Visibility != Visibility.Visible, "结果条出来时「保存」按钮收起");
        Check(Equals(cancelButton.Content, "关闭"), "「取消」变成「关闭」（Esc 也能关）");

        window.Close();
        Pump(window);

        // ---------- 9) 段尾还原 ----------
        settingsType.GetMethod("Apply")!.Invoke(null, new[] { backup });

        string restored = (string)settingsType.GetField("Format")!.GetValue(null)!;
        Check(restored.Length > 0, "设置已还原（格式 = " + restored + "）");

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// 新版的「另存为」对话框构造（<c>(图, 建议名, 文件名上下文)</c>）：
    /// 段名叫的 <see cref="CaptureSaveWindow"/> 是 public，但第三种参数是 <c>ExportNameContext</c>，
    /// 直接 <c>Activator.CreateInstance</c> 找不到四参数以外的签名。
    /// </summary>
    private static Window NewSaveWindow(Type windowType, BitmapSource image, string name)
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var contextType = asm.GetType("LTools.Features.CapturePro.ExportNameContext")!;
        object empty = contextType.GetField("Empty", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

        return (Window)windowType
            .GetConstructor(new[] { typeof(BitmapSource), typeof(string), contextType })!
            .Invoke(new[] { (object)image, name, empty });
    }

    /// <summary>一整片同色的位图（压缩力度对比用：渐变图两档差得太小，容易看着像没生效）。</summary>
    private static BitmapSource ExportFlat(int width, int height)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 200;      // B
            pixels[i + 1] = 120;  // G
            pixels[i + 2] = 40;   // R
            pixels[i + 3] = 255;  // A
        }
        return BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null,
            pixels, stride);
    }
}



