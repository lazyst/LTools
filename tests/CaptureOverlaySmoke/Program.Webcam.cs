using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 录屏空闲期的摄像头画中画（P8-f，照上游 <c>recordingwebcamcontrol.cpp</c> +
/// <c>snow-recording-model/src/webcam.rs</c>）：
/// 窗口几何 = 画中画 ∪ 方向工具条、真鼠标拖动 / 拖右上角圆钮缩放 / 点四个方向按钮、
/// 以及面板侧的「显示真实预览 → 开录收掉 → 提交落 settings.json」。
/// </summary>
internal static partial class Program
{
    private const BindingFlags WebcamAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags WebcamStat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static void RecordWebcamSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var windowType = asm.GetType("LTools.Views.CapturePro.RecordWebcamWindow")!;
        var layoutType = asm.GetType("LTools.Features.CapturePro.RecordWebcamLayout")!;
        var shapeType = asm.GetType("LTools.Features.CapturePro.RecordCameraShape")!;
        var actionType = asm.GetType("LTools.Features.CapturePro.RecordWebcamAction")!;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
        var controllerType = asm.GetType("LTools.Features.CapturePro.RecordController")!;
        var barType = asm.GetType("LTools.Views.CapturePro.CaptureRecordWindow")!;
        var gateType = asm.GetType("LTools.Features.CapturePro.RecordStartGate")!;
        var appStateType = asm.GetType("LTools.Core.AppState")!;
        var configLocatorType = asm.GetType("LTools.Core.ConfigLocator")!;

        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var (work, scale) = PrimaryWorkArea(asm);
        Console.WriteLine($"工作区 {work.X},{work.Y} {work.Width}×{work.Height} 物理像素 · 缩放 {scale:0.##}");

        var area = new Int32Rect(work.X + 80, work.Y + 60, 800, 600);
        object square = Enum.Parse(shapeType, "square");

        var present = windowType.GetMethod("Present", WebcamAny)!;
        var orientationButtonOf = windowType.GetMethod("OrientationButton", WebcamAny)!;
        var previewProperty = windowType.GetProperty("PreviewPhysicalBounds", WebcamAny)!;
        var toolbarProperty = windowType.GetProperty("ToolbarPhysicalBounds", WebcamAny)!;

        // ---- 设置文件备份（面板侧的画中画拖 / 换方向**真写** settings.json，测完还原） ----
        string? settingsPath = null;
        string? settingsBackup = null;
        object? recordBackup = null;
        try
        {
            var anchor = (string)configLocatorType.GetMethod("FindPath", WebcamStat)!.Invoke(null, null)!;
            settingsPath = Path.Combine(Path.GetDirectoryName(anchor) ?? ".", "configs", "settings.json");
            if (File.Exists(settingsPath)) settingsBackup = File.ReadAllText(settingsPath);
            recordBackup = appStateType.GetField("Record", WebcamStat)!.GetValue(null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("备份录屏设置失败：" + ex.Message);
        }

        var webcam = (Window)Activator.CreateInstance(windowType, true)!;
        int commits = 0;
        int lastSize = 0, lastLeft = 0, lastBottom = 0;

        void OnCommitted(int size, int left, int bottom)
        {
            commits++;
            lastSize = size;
            lastLeft = left;
            lastBottom = bottom;
        }

        windowType.GetEvent("Committed")!.AddEventHandler(webcam, (Action<int, int, int>)OnCommitted);

        Window? bar = null;
        try
        {
            webcam.Show();
            PumpFor(200);

            var handle = new WindowInteropHelper(webcam).Handle;
            Check(handle != IntPtr.Zero, "画中画预览窗有句柄");

            long exStyle = GetWindowLongPtr(handle, -20).ToInt64();
            Check((exStyle & WsExNoactivate) != 0, "画中画预览不抢焦点（WS_EX_NOACTIVATE）");
            Check((exStyle & WsExToolwindow) != 0, "画中画预览不进 Alt+Tab（WS_EX_TOOLWINDOW）");
            Check(GetWindowDisplayAffinity(handle, out uint affinity) != 0 && affinity == WdaExcludeFromCapture,
                "画中画预览自己不入镜（WDA_EXCLUDEFROMCAPTURE）");

            // ============ 1) 几何：窗口 = 画中画 ∪ 工具条 ============
            present.Invoke(webcam, new object[] { area, 150, 48, 48, square, work, scale });
            PumpFor(220);

            var preview = (Int32Rect)previewProperty.GetValue(webcam)!;
            var expectedPreview = (Int32Rect)layoutType.GetMethod("PreviewRect", WebcamStat)!
                .Invoke(null, new object[] { area, 150, 48, 48 })!;
            Check(preview == expectedPreview,
                $"画中画落在区域左下（{preview.X},{preview.Y} {preview.Width}×{preview.Height}）");
            Check(preview.Width == 150, $"画中画边长按设置（{preview.Width}）");

            var toolbar = (Int32Rect)toolbarProperty.GetValue(webcam)!;
            var expectedToolbar = (Int32Rect)layoutType.GetMethod("ToolbarRect", WebcamStat)!
                .Invoke(null, new object[] { preview, work, scale })!;
            Check(toolbar == expectedToolbar,
                $"工具条贴在画中画正上方（{toolbar.X},{toolbar.Y} {toolbar.Width}×{toolbar.Height}）");

            var union = Union(preview, toolbar);
            var bounds = WindowBounds(webcam);
            Check(Math.Abs(bounds.Left - union.X) <= 2 && Math.Abs(bounds.Top - union.Y) <= 2 &&
                  Math.Abs(bounds.Right - (union.X + union.Width)) <= 2 &&
                  Math.Abs(bounds.Bottom - (union.Y + union.Height)) <= 2,
                $"窗口正好是两块并集（{bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}）");
            Check(Math.Abs(webcam.Width * scale - union.Width) <= 2 && Math.Abs(webcam.Height * scale - union.Height) <= 2,
                $"窗口尺寸按物理像素换算成 DIP（{webcam.Width:0.#}×{webcam.Height:0.#}）");

            try
            {
                webcam.UpdateLayout();
                var shot = new RenderTargetBitmap(
                    Math.Max(1, (int)(webcam.ActualWidth * 2)), Math.Max(1, (int)(webcam.ActualHeight * 2)),
                    192, 192, PixelFormats.Pbgra32);
                shot.Render(webcam);
                Save(shot, Path.Combine(_outDir, "record-webcam-preview.png"));

                // 工具条单独放 6 倍：四个方向图标要能一眼分清（人眼校对）
                var toolbarElement = (FrameworkElement)windowType.GetField("_toolbar", WebcamAny)!.GetValue(webcam)!;
                toolbarElement.UpdateLayout();
                var scaled = new RenderTargetBitmap(
                    Math.Max(1, (int)(toolbarElement.ActualWidth * 6)),
                    Math.Max(1, (int)(toolbarElement.ActualHeight * 6)),
                    576, 576, PixelFormats.Pbgra32);
                scaled.Render(toolbarElement);
                Save(scaled, Path.Combine(_outDir, "record-webcam-toolbar.png"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("渲染画中画预览失败：" + ex.Message);
            }

            // ============ 2) 真鼠标拖动画中画 ============
            int cx = preview.X + preview.Width / 2;
            int cy = preview.Y + preview.Height / 2;
            SetCursorPos(cx, cy);
            PumpFor(90);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(70);
            SetCursorPos(cx + 15, cy - 10);
            PumpFor(70);
            SetCursorPos(cx + 30, cy - 20);
            PumpFor(70);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(200);

            Check(commits == 1, $"拖动松手提交一次（{commits}）");
            Check(lastSize == 150, $"拖动不改边长（{lastSize}）");
            Check(lastLeft == 78 && lastBottom == 68,
                $"拖动 → 边距（左 {lastLeft} / 下 {lastBottom}，期望 78 / 68）");

            var moved = (Int32Rect)previewProperty.GetValue(webcam)!;
            var expectedMoved = (Int32Rect)layoutType.GetMethod("PreviewRect", WebcamStat)!
                .Invoke(null, new object[] { area, 150, 78, 68 })!;
            Check(moved == expectedMoved, $"松手后画中画跟着挪（{moved.X},{moved.Y}）");

            var movedBounds = WindowBounds(webcam);
            Check(Math.Abs(movedBounds.Left - Union(moved, (Int32Rect)toolbarProperty.GetValue(webcam)!).X) <= 2,
                "窗口跟着画中画一起挪");

            // 拖到边界：继续往左上拖，边距夹在 0，不会越拖越远
            SetCursorPos(moved.X + moved.Width / 2, moved.Y + moved.Height / 2);
            PumpFor(90);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(70);
            SetCursorPos(moved.X + moved.Width / 2 - 2000, moved.Y + moved.Height / 2 + 2000);
            PumpFor(90);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(220);
            Check(lastLeft == 0 && lastBottom == 0,
                $"拖到左下角外时边距夹成 0（左 {lastLeft} / 下 {lastBottom}）");

            // ============ 3) 拖右上角圆钮缩放 ============
            var before = (Int32Rect)previewProperty.GetValue(webcam)!;
            int handleSide = (int)Math.Round(32 * scale, MidpointRounding.AwayFromZero);
            int hx = before.X + before.Width - handleSide / 2;
            int hy = before.Y + handleSide / 2;
            SetCursorPos(hx, hy);
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(70);
            SetCursorPos(hx + 30, hy);
            PumpFor(70);
            SetCursorPos(hx + 60, hy);
            PumpFor(70);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(220);

            Check(lastSize == 180, $"右拖 60 → 边长 +30（{lastSize}，期望 180）");
            var resized = (Int32Rect)previewProperty.GetValue(webcam)!;
            Check(resized.Width == 180, $"画中画真的变大了（{resized.Width}）");
            Check(lastLeft == 0 && lastBottom == 0, "缩放不改边距（仍是夹过的 0 / 0）");

            // ============ 3.5) 拖动中按 Esc → 退回初始布局、不提交 ============
            int commitsBeforeEsc = commits;
            SetCursorPos(resized.X + resized.Width / 2, resized.Y + resized.Height / 2);
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(70);
            SetCursorPos(resized.X + resized.Width / 2 + 25, resized.Y + resized.Height / 2 - 15);
            PumpFor(110);
            keybd_event((byte)VkEsc, 0, 0, UIntPtr.Zero);
            PumpFor(180);
            keybd_event((byte)VkEsc, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
            PumpFor(160);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(160);

            Check(commits == commitsBeforeEsc, $"拖动中 Esc 取消不提交（{commits}）");
            var afterEsc = (Int32Rect)previewProperty.GetValue(webcam)!;
            Check(afterEsc == resized, $"Esc 取消退回初始布局（{afterEsc.X},{afterEsc.Y} {afterEsc.Width}）");
            Check(windowType.GetProperty("IsInteracting", WebcamAny)!.GetValue(webcam) is false,
                "Esc 取消后不再算拖动中");

            // ============ 4) 四个方向按钮：真鼠标点 ============
            var barRect = (Int32Rect)toolbarProperty.GetValue(webcam)!;
            int buttonSide = (int)Math.Round(24 * scale, MidpointRounding.AwayFromZero);
            int padX = (int)Math.Round(3 * scale, MidpointRounding.AwayFromZero);
            var actionNames = new[] { "rotateClockwise", "rotateCounterclockwise", "flipHorizontal", "flipVertical" };

            for (int i = 0; i < actionNames.Length; i++)
            {
                var button = (Button)orientationButtonOf.Invoke(webcam, new[] { Enum.Parse(actionType, actionNames[i]) })!;
                int clicked = 0;
                button.Click += (_, _) => clicked++;
                Check(button.ToolTip is string tip && tip.Length > 0, $"{actionNames[i]} 有悬停提示");

                int bx = barRect.X + padX + i * buttonSide + buttonSide / 2;
                int by = barRect.Y + barRect.Height / 2;
                SetCursorPos(bx, by);
                PumpFor(90);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                PumpFor(150);
                Check(clicked == 1, $"{actionNames[i]} 真鼠标点得中（{clicked}）");
            }

            Check(GetForegroundWindow() != handle, "点按钮 / 拖动都没把前台抢到预览窗");

            // ============ 4.5) 方向真的作用到像素上（照上游位语义：先左右翻、再顺时针转） ============
            var cameraType = asm.GetType("LTools.Features.CapturePro.RecordCamera")!;
            var probe = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(cameraType);
            var orientationField = cameraType.GetField("_orientation", WebcamAny)!;
            var applyOrientation = cameraType.GetMethod("ApplyOrientation", WebcamAny)!;
            const int n = 4;

            var source = new byte[n * n * 3];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 3;
                source[i] = (byte)(x * 30);              // B
                source[i + 1] = (byte)(y * 30);          // G
                source[i + 2] = (byte)((x == y ? 200 : 40) + y); // R：对角线上另有特征
            }

            foreach (var name in new[]
                     {
                         "identity", "clockwise90", "clockwise180", "clockwise270",
                         "mirrored", "mirroredClockwise90", "mirroredClockwise180", "mirroredClockwise270",
                     })
            {
                int raw = (int)Enum.Parse(cameraType.Assembly
                    .GetType("LTools.Features.CapturePro.RecordCameraOrientation")!, name);
                orientationField.SetValue(probe, raw);

                using var frame = new OpenCvSharp.Mat(n, n, OpenCvSharp.MatType.CV_8UC3);
                System.Runtime.InteropServices.Marshal.Copy(source, 0, frame.Data, source.Length);
                applyOrientation.Invoke(probe, new object[] { frame });

                var actual = new byte[source.Length];
                System.Runtime.InteropServices.Marshal.Copy(frame.Data, actual, 0, actual.Length);

                var expected = Orient(source, n, raw);
                Check(actual.SequenceEqual(expected), $"{name} 的像素变换与上游位语义一致（先翻再转）");
            }

            // ============ 5) 面板侧：真实预览 → 开录收掉 → 落盘 ============
            var plan = planType.GetMethod("FromValues")!.Invoke(null,
                new object[] { "all", "mp4", 30, 0, false, null })!;
            var options = plan.GetType().GetProperty("Options")!.GetValue(plan)!;
            options.GetType().GetProperty("CameraDevice")!.SetValue(options, 0);
            options.GetType().GetProperty("CameraSize")!.SetValue(options, 150);
            options.GetType().GetProperty("CameraOffsetLeft")!.SetValue(options, 48);
            options.GetType().GetProperty("CameraOffsetBottom")!.SetValue(options, 48);

            object controller = Activator.CreateInstance(controllerType)!;
            object gate = Activator.CreateInstance(gateType, true)!;
            bar = (Window)Activator.CreateInstance(barType, AnyCtor, null,
                new object[] { area, plan, controller, gate }, null)!;
            bar.Show();
            PumpFor(500);

            var visible = barType.GetProperty("WebcamPreviewVisible", WebcamAny)!;
            var previewCameraProperty = barType.GetProperty("PreviewCamera", WebcamAny)!;
            Check((bool)visible.GetValue(bar)!, "面板空闲期显示摄像头预览");

            var panelWindow = (Window?)barType.GetProperty("WebcamWindow", WebcamAny)!.GetValue(bar);
            Check(panelWindow != null, "预览是独立的不抢焦点小窗");

            if (panelWindow != null)
            {
                var rect = WindowBounds(panelWindow);
                Check(rect.Left >= work.X - 2 && rect.Top >= work.Y - 2 &&
                      rect.Right <= work.X + work.Width + 2 && rect.Bottom <= work.Y + work.Height + 2,
                    $"预览整块落在工作区里（{rect.Left},{rect.Top}–{rect.Right},{rect.Bottom}）");
            }

            Check(WaitUntil(() => previewCameraProperty.GetValue(bar) != null, 6000),
                "预览用的摄像头已经打开（开录时交给会话）");

            // 5.0 实时画面真的到了预览上（不是只开了设备）
            var liveCamera = previewCameraProperty.GetValue(bar)!;
            Check(WaitUntil(() => (bool)liveCamera.GetType().GetProperty("HasFrame")!.GetValue(liveCamera)!, 6000),
                "预览拿到摄像头帧");

            int frameSide = (int)liveCamera.GetType().GetProperty("Side")!.GetValue(liveCamera)!;
            var frameBuffer = new byte[frameSide * frameSide * 4];
            bool copied = (bool)liveCamera.GetType().GetMethod("TryCopyFrame")!
                .Invoke(liveCamera, new object[] { frameBuffer })!;
            var distinct = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < frameBuffer.Length; i += 4 * 512)
                distinct.Add(BitConverter.ToInt32(frameBuffer, i));
            Check(copied && distinct.Count > 4, $"摄像头画面不是纯色（采样 {distinct.Count} 种颜色）");

            if (panelWindow != null)
            {
                try
                {
                    panelWindow.UpdateLayout();
                    var live = new RenderTargetBitmap(
                        Math.Max(1, (int)(panelWindow.ActualWidth * 2)),
                        Math.Max(1, (int)(panelWindow.ActualHeight * 2)),
                        192, 192, PixelFormats.Pbgra32);
                    live.Render(panelWindow);
                    Save(live, Path.Combine(_outDir, "record-webcam-live.png"));
                }
                catch (Exception ex)
                {
                    Console.WriteLine("渲染实时预览失败：" + ex.Message);
                }
            }

            // 5.1 真鼠标拖动面板上的预览 → 写回 _plan.Options + settings.json
            var panelPreview = (Int32Rect)previewProperty.GetValue(panelWindow!)!;
            int px = panelPreview.X + panelPreview.Width / 2;
            int py = panelPreview.Y + panelPreview.Height / 2;
            SetCursorPos(px, py);
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(70);
            SetCursorPos(px + 10, py - 10);
            PumpFor(70);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(300);

            var planOptions = plan.GetType().GetProperty("Options")!.GetValue(plan)!;
            int planLeft = (int)planOptions.GetType().GetProperty("CameraOffsetLeft")!.GetValue(planOptions)!;
            int planBottom = (int)planOptions.GetType().GetProperty("CameraOffsetBottom")!.GetValue(planOptions)!;
            Check(planLeft == 58 && planBottom == 58,
                $"面板上的拖动写回运行期选项（左 {planLeft} / 下 {planBottom}，期望 58 / 58）");

            if (settingsPath != null && File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var record = document.RootElement.GetProperty("Record");
                Check(record.GetProperty("CameraOffsetLeft").GetInt32() == 58 &&
                      record.GetProperty("CameraOffsetBottom").GetInt32() == 58,
                    "拖动结果落进 settings.json 的 Record 段");
            }

            // 5.2 真鼠标点方向按钮 → 方向改掉、也写盘
            var panelToolbar = (Int32Rect)toolbarProperty.GetValue(panelWindow!)!;
            var rotateButton = (Button)orientationButtonOf.Invoke(panelWindow!,
                new[] { Enum.Parse(actionType, "rotateClockwise") })!;
            SetCursorPos(panelToolbar.X + padX + buttonSide / 2, panelToolbar.Y + panelToolbar.Height / 2);
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(300);

            var orientation = planOptions.GetType().GetProperty("CameraOrientation")!.GetValue(planOptions)!;
            Check(orientation.ToString() == "clockwise90", $"点顺时针按钮 → 方向变 clockwise90（{orientation}）");
            Check(rotateButton != null, "方向按钮实例可点");

            if (settingsPath != null && File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                Check(document.RootElement.GetProperty("Record").GetProperty("CameraOrientation").GetString()
                        == "clockwise90",
                    "方向也落进 settings.json");
            }

            // 5.2b 「录屏选项」里改方向（不走预览窗按钮）→ 心跳重算时直接推给预览摄像头
            object mirrored = Enum.Parse(
                asm.GetType("LTools.Features.CapturePro.RecordCameraOrientation")!, "mirrored");
            planOptions.GetType().GetProperty("CameraOrientation")!.SetValue(planOptions, mirrored);
            barType.GetMethod("ReconcileWebcam", WebcamAny)!.Invoke(bar, null);
            PumpFor(200);
            Check(liveCamera.GetType().GetProperty("Orientation")!.GetValue(liveCamera)!.ToString() == "mirrored",
                "设置里改的方向也推到预览摄像头");

            // 5.3 关掉摄像头 → 预览跟着消失；再打开 → 回来
            var cameraBtn = (ToggleButton)barType.GetField("_cameraBtn", WebcamAny)!.GetValue(bar)!;
            var togglePoint = ScreenPointOf(bar, cameraBtn, scale);
            SetCursorPos((int)togglePoint.X, (int)togglePoint.Y);
            PumpFor(120);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(400);
            Check(!(bool)visible.GetValue(bar)!, "关掉摄像头后预览消失");

            SetCursorPos((int)togglePoint.X, (int)togglePoint.Y);
            PumpFor(120);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Check(WaitUntil(() => (bool)visible.GetValue(bar)!, 5000), "再打开摄像头 → 预览回来");

            // 5.4 点录制（倒计时开始）→ 预览立刻收掉、摄像头留给会话
            barType.GetMethod("Record_Click", WebcamAny)!.Invoke(bar, new object[] { bar, new RoutedEventArgs() });
            PumpFor(200);
            Check(!(bool)visible.GetValue(bar)!, "点录制后预览收掉（上游 countdownActive 时不可见）");
            Check((bool)gateType.GetProperty("Started")!.GetValue(gate)!, "录制握手已经发出");
            Check(previewCameraProperty.GetValue(bar) != null, "摄像头留给会话，没有被提前释放");
        }
        finally
        {
            try { bar?.Close(); } catch { /* 已经关了 */ }
            PumpFor(200);

            windowType.GetEvent("Committed")!.RemoveEventHandler(webcam, (Action<int, int, int>)OnCommitted);
            try { if (webcam.IsVisible) webcam.Close(); } catch { /* 已经关了 */ }
            PumpFor(120);

            // 还原 settings.json 与运行期选项（别把冒烟摆的画中画写进用户配置）
            try
            {
                if (settingsPath != null)
                {
                    if (settingsBackup != null) File.WriteAllText(settingsPath, settingsBackup);
                    else if (File.Exists(settingsPath)) File.Delete(settingsPath);
                }

                if (recordBackup != null) appStateType.GetField("Record", WebcamStat)!.SetValue(null, recordBackup);
            }
            catch (Exception ex)
            {
                Console.WriteLine("还原录屏设置失败：" + ex.Message);
            }

            SetCursorPos(960, 540);
        }
    }

    // ============ 小工具 ============

    /// <summary>
    /// 独立实现的期望变换（上游 <c>WebcamOrientation</c> 的位语义：先左右翻、再顺时针转 k × 90°）。
    /// 与 <c>RecordCamera.ApplyOrientation</c> 的输出逐字节比对。
    /// </summary>
    private static byte[] Orient(byte[] source, int n, int raw)
    {
        // ① 左右翻（bit 2）：src(col=x, row=y) → dest(col=n-1-x, row=y)
        var flipped = new byte[source.Length];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int from = (y * n + x) * 3;
            int to = (y * n + (n - 1 - x)) * 3;
            flipped[to] = source[from];
            flipped[to + 1] = source[from + 1];
            flipped[to + 2] = source[from + 2];
        }

        var current = (raw & 4) != 0 ? flipped : source;

        // ② 顺时针转 q 次：src(col=x, row=y) → dest(col=n-1-y, row=x)
        int turns = raw & 3;
        for (int turn = 0; turn < turns; turn++)
        {
            var next = new byte[current.Length];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int from = (y * n + x) * 3;
                int to = (x * n + (n - 1 - y)) * 3;
                next[to] = current[from];
                next[to + 1] = current[from + 1];
                next[to + 2] = current[from + 2];
            }
            current = next;
        }

        return current;
    }

    private static Int32Rect Union(Int32Rect a, Int32Rect b)
    {
        if (a.Width <= 0 || a.Height <= 0) return b;
        if (b.Width <= 0 || b.Height <= 0) return a;

        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Int32Rect(left, top, right - left, bottom - top);
    }

    /// <summary>控件中心在屏幕上的物理像素（窗口 Left/Top 是 DIP，乘缩放换过去）。</summary>
    private static Point ScreenPointOf(Window root, FrameworkElement element, double scale)
    {
        var local = new Point(element.ActualWidth / 2, element.ActualHeight / 2);
        var inRoot = element.TransformToAncestor(root).Transform(local);
        return new Point((root.Left + inRoot.X) * scale, (root.Top + inRoot.Y) * scale);
    }

    /// <summary>主显示器工作区（物理像素）+ 缩放（照 <c>LTools.Native.Screens.All()</c>）。</summary>
    private static (Int32Rect Work, double Scale) PrimaryWorkArea(Assembly asm)
    {
        try
        {
            var screensType = asm.GetType("LTools.Native.Screens")!;
            var all = (IEnumerable)screensType.GetMethod("All", WebcamStat)!.Invoke(null, null)!;
            var primary = all.Cast<object>()
                .First(s => (bool)s.GetType().GetProperty("Primary")!.GetValue(s)!);

            int left = (int)primary.GetType().GetProperty("WorkLeft")!.GetValue(primary)!;
            int top = (int)primary.GetType().GetProperty("WorkTop")!.GetValue(primary)!;
            int width = (int)primary.GetType().GetProperty("WorkWidth")!.GetValue(primary)!;
            int height = (int)primary.GetType().GetProperty("WorkHeight")!.GetValue(primary)!;
            double scale = (double)primary.GetType().GetProperty("Scale")!.GetValue(primary)!;
            return (new Int32Rect(left, top, width, height), scale > 0 ? scale : 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine("取工作区失败，退回主屏：" + ex.Message);
            return (new Int32Rect(0, 0, 1920, 1040), 1);
        }
    }
}
