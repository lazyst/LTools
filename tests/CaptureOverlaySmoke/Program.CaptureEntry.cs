using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 采集入口（P7-a，照 Snow Shot 的 quick 动作）：快捷入口的「选完就做」不清工具条、
/// 选区一落定就执行；以及「截取当前显示器 / 前台窗口」共用的直接截图路径。
/// </summary>
internal static partial class Program
{
    private static void CaptureEntrySection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var pendingType = typeof(CaptureOverlayWindow).GetNestedType("PendingAction", BindingFlags.Public)!;
        object Pending(string name) => Enum.Parse(pendingType, name);

        var shot = BuildShot(400, 300);
        var monitor = new System.Drawing.Rectangle(0, 0, 400, 300);

        var ctor = typeof(CaptureOverlayWindow)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 6);

        CaptureOverlayWindow Make(object pending, bool editOnly = false)
        {
            var overlay = (CaptureOverlayWindow)ctor.Invoke(
                new object[] { shot, monitor, 1.0, 1.0, editOnly, pending });

            // 与 NewOverlay 一致：关掉智能选区，别让预选干扰「pending 会不会执行」的判定
            var smart = Field(overlay, "_smart");
            var modeType = asm.GetType("LTools.Features.CapturePro.SmartSelectionMode")!;
            smart.GetType().GetMethod("BeginSession")!
                .Invoke(smart, new[] { Enum.Parse(modeType, "Disabled") });
            return overlay;
        }

        bool HasPending(CaptureOverlayWindow w) => (bool)Member(w, "HasPending");
        string DefaultFinish(CaptureOverlayWindow w) => Member(w, "DefaultFinish")!.ToString()!;
        string Finish(CaptureOverlayWindow w) =>
            typeof(CaptureOverlayWindow).GetProperty("Finish", Private)!.GetValue(w)!.ToString()!;
        bool PinRequested(CaptureOverlayWindow w) =>
            (bool)typeof(CaptureOverlayWindow).GetProperty("PinRequested")!.GetValue(w)!;

        // ---------- 1) 普通截图：没有 pending，框完照常摆工具条 ----------
        var plain = Make(Pending("None"));
        plain.Show();
        Pump(plain);

        Check(!HasPending(plain), "普通截图没有「选完就做」");
        Check(DefaultFinish(plain) == "Copy", "普通截图的默认出口 = 复制");

        SetSelection(plain, new Rect(50, 40, 200, 120));
        Pump(plain);

        var plainToolbar = Field(plain, "Toolbar") as FrameworkElement;
        var plainShapeBar = Field(plain, "ShapeBar") as FrameworkElement;
        Check(plainToolbar is { Visibility: Visibility.Visible }, "普通截图框完摆工具条");
        Check(plainShapeBar is { Visibility: Visibility.Visible }, "普通截图框完摆形状条");

        plain.Close();
        Pump(plain);

        // ---------- 2) 截图并贴图：工具条 / 形状条都不摆，选区落定就贴图并关窗 ----------
        var pin = Make(Pending("Pin"));
        pin.Show();
        Pump(pin);

        Check(HasPending(pin), "「截图并贴图」带着 pending");
        Check(DefaultFinish(pin) == "Pin", "「截图并贴图」的默认出口 = 贴图");

        SetSelection(pin, new Rect(30, 20, 160, 100));
        Pump(pin);

        var pinToolbar = Field(pin, "Toolbar") as FrameworkElement;
        var pinShapeBar = Field(pin, "ShapeBar") as FrameworkElement;
        Check(pinToolbar?.Visibility != Visibility.Visible, "pending 时不摆工具条（照上游 suppressCaptureToolbar）");
        Check(pinShapeBar?.Visibility != Visibility.Visible, "pending 时不摆形状条");

        Invoke(pin, "FirePendingAction");
        Pump(pin);

        Check(PinRequested(pin), "pending 执行后请求贴图");
        Check(Finish(pin) == "Pin", "完成动作记为贴图");
        Check(!pin.IsVisible, "pending 执行后覆盖层关掉");

        // ---------- 3) 截图并复制：pending = Copy，直接进剪贴板 ----------
        var copy = Make(Pending("Copy"));
        copy.Show();
        Pump(copy);

        Check(HasPending(copy) && DefaultFinish(copy) == "Copy", "「截图并复制」的默认出口 = 复制");
        SetSelection(copy, new Rect(20, 20, 120, 80));
        Pump(copy);
        Invoke(copy, "FirePendingAction");
        Pump(copy);

        Check(Finish(copy) == "Copy", "「截图并复制」完成动作记为复制");
        Check(!copy.IsVisible, "「截图并复制」执行后覆盖层关掉");

        bool hasImage = false;
        try { hasImage = Clipboard.ContainsImage(); } catch { /* 剪贴板被别的程序占着 */ }
        Check(hasImage, "pending 复制后剪贴板里真的有图片");

        // ---------- 4) 贴图内标注：pending 强制 None（那条路只有「完成」） ----------
        var edit = Make(Pending("Pin"), editOnly: true);
        Check(!HasPending(edit), "贴图内标注不吃 pending");
        edit.Close();

        // ---------- 5) 直接截图：抓图 → 剪贴板 → 采集历史（当前显示器 / 前台窗口共用） ----------
        var sessionType = asm.GetType("LTools.Features.CapturePro.CaptureSession")!;
        var catalogType = asm.GetType("LTools.Features.CapturePro.CaptureCatalog")!;

        int before = ((ICollection)catalogType.GetMethod("Load")!.Invoke(null, null)!).Count;
        var direct = sessionType.GetMethod("CaptureDirect")!.Invoke(null,
            new object[] { new System.Drawing.Rectangle(0, 0, 64, 48), "当前显示器", "copy_screenshot" })
            as BitmapSource;

        Check(direct != null && direct.PixelWidth == 64 && direct.PixelHeight == 48,
            $"直接截图按给定矩形抓图：{direct?.PixelWidth}×{direct?.PixelHeight}");
        Check(direct is { IsFrozen: true }, "直接截图的位图已冻结（可跨线程）");

        System.Threading.Thread.Sleep(150);   // SaveAssets 在后台

        var entries = ((IEnumerable)catalogType.GetMethod("Load")!.Invoke(null, null)!)
            .Cast<object>().ToList();
        string sourceOfNewest = entries.Count > 0
            ? (string)entries[0].GetType().GetProperty("Source")!.GetValue(entries[0])!
            : "";
        Check(entries.Count == before + 1, $"直接截图多记一条采集历史（{before} → {entries.Count}）");
        Check(sourceOfNewest == "当前显示器", "采集历史记下来源：" + sourceOfNewest);

        // 收尾：把冒烟造的这条记录清掉，别污染开发机上的采集历史
        if (entries.Count > 0)
        {
            var id = (string)entries[0].GetType().GetProperty("Id")!.GetValue(entries[0])!;
            catalogType.GetMethod("Remove")!.Invoke(null, new object[] { id });
        }
    }
}
