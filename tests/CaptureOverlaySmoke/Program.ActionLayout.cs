using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 完成动作布局（P9-d4，照上游 <c>screenshot_toolbar/action_tools_layout</c>）：
/// 默认顺序 / 分组线、设置页编辑器改顺序与显隐、落盘往返、新覆盖层立刻照新布局排、
/// 图片编辑窗按可用集过滤、恢复默认。
/// </summary>
internal static partial class Program
{
    private static void ActionLayoutSection(BitmapSource shot)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.CaptureActionLayoutStore")!;
        var layoutType = asm.GetType("LTools.Features.CapturePro.CaptureActionLayout")!;
        var windowType = asm.GetType("LTools.Views.CapturePro.CaptureActionLayoutWindow")!;
        var toolbarType = asm.GetType("LTools.Views.CapturePro.CaptureToolbar")!;
        var styleStoreType = asm.GetType("LTools.Features.CapturePro.CaptureStyleStore")!;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var modeType = toolbarType.GetNestedType("ToolbarMode", BindingFlags.Public | BindingFlags.NonPublic)!;

        object CurrentLayout() => storeType.GetProperty("Current", Stat)!.GetValue(null)!;

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "actionlayout-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string uiFile = Path.Combine(configRoot, "configs", "ui.json");

        configPathSet.Invoke(null, new object[] { configPath });
        storeType.GetMethod("Forget", Stat)!.Invoke(null, null);

        var overlays = new List<Window>();
        Window? editor = null;
        try
        {
            IReadOnlyList<string> OrderOf(object toolbar) =>
                ((System.Collections.IEnumerable)toolbarType.GetProperty("ActionOrder", Inst)!
                    .GetValue(toolbar)!)
                .Cast<object>().Select(o => o.ToString()!).ToList();

            object ToolbarOf(Window w) => Field(w, "Toolbar")!;

            // 分隔线按资源实例认（工具条的竖线与「选择」那一条是同一个 Style）；
            // 只数**最后一个工具格之后**的那些，才是「完成动作」的分组线。
            int GroupLines(Window w)
            {
                var toolbar = (FrameworkElement)ToolbarOf(w);
                var style = (Style)toolbar.FindResource("SeparatorBar");
                var row = ToolRow(w)!;
                int lastCell = row.Children.OfType<RadioButton>()
                    .Select(c => row.Children.IndexOf(c)).DefaultIfEmpty(-1).Max();

                int count = 0;
                for (int i = lastCell + 1; i < row.Children.Count; i++)
                    if (row.Children[i] is Border b && ReferenceEquals(b.Style, style)) count++;
                return count;
            }

            Window OpenOverlay()
            {
                var w = NewOverlay(shot, new System.Drawing.Rectangle(0, 0, shot.PixelWidth, shot.PixelHeight));
                overlays.Add(w);
                w.Show();
                Pump(w);
                return w;
            }

            Window OpenEditor()
            {
                var w = (Window)Activator.CreateInstance(windowType, Inst, null, Array.Empty<object>(), null)!;
                w.Show();
                Pump(w);
                return w;
            }

            StackPanel RowHost(Window w) => (StackPanel)w.FindName("RowHost")!;
            List<Border> Rows(Window w) => RowHost(w).Children.OfType<Border>().ToList();
            CheckBox VisibleBox(Border row) => ((Grid)row.Child).Children.OfType<CheckBox>().First();
            CheckBox GroupBox(Border row) => ((Grid)row.Child).Children.OfType<CheckBox>().Skip(1).First();
            List<Button> RowButtons(Border row) => ((Grid)row.Child).Children.OfType<Button>().ToList();

            var defaultOrder = new[]
            {
                "Recognize", "QrCode", "Record", "Pin", "Live", "Scroll",
                "Print", "QuickSave", "SaveAs", "More", "Cancel", "Copy",
            };

            // ============ ① 默认布局：顺序照上游 actionDefaultPositions ============
            var first = OpenOverlay();
            var order = OrderOf(ToolbarOf(first));
            Check(order.SequenceEqual(defaultOrder),
                "默认顺序：识别 / 二维码 │ 录屏 │ 贴图 / 实时 │ 长截图 │ 打印 / 快速保存 / 另存为 │ 更多 │ 取消 / 复制："
                + string.Join(",", order));

            // 1 条（工具格与撤销 / 重做之间）+ 1 条（撤销 / 重做与完成动作之间）+ 6 条（组与组之间）
            Check(GroupLines(first) == 8, $"默认 7 组 → 8 条分隔线（实际 {GroupLines(first)}）");

            // ============ ② 编辑器：行 = 目录默认顺序，第一行不能「另起一组」 ============
            editor = OpenEditor();
            var rows = Rows(editor);
            Check(rows.Count == 12, $"编辑器列出 12 个动作（实际 {rows.Count}）");
            Check(string.Join(",", rows.Select(r => r.Tag?.ToString())) ==
                  "recognize,qrcode,record-screen,pin-to-screen,pin-live,scrolling-screenshot,"
                  + "print,quick-save,save-as-file,more,cancel,copy",
                "编辑器行顺序照默认布局：" + string.Join(",", rows.Select(r => r.Tag)));
            Check(!GroupBox(rows[0]).IsEnabled, "第一行不能「另起一组」（前面没有东西）");
            Check(GroupBox(rows[1]).IsEnabled, "第二行可以「另起一组」");
            Check(GroupBox(rows[2]).IsChecked == true,
                "第三行（录屏）默认就是新组的开头（识别 / 二维码 与 录屏 分开）");
            Check(VisibleBox(rows[0]).IsChecked == true, "默认都显示");
            Check(RowButtons(rows[0]).Count == 2, "每行有上移 / 下移两个按钮");

            // ============ ③ 改：复制上移一格（排到取消前面）+ 收起录屏 ============
            var copyRow = rows.First(r => (string?)r.Tag == "copy");
            RowButtons(copyRow)[0].RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(editor);

            rows = Rows(editor);
            Check((string?)rows[^2].Tag == "copy" && (string?)rows[^1].Tag == "cancel",
                "「上移」把复制挪到取消前面：" + string.Join(",", rows.Skip(rows.Count - 2).Select(r => r.Tag)));

            var recordRow = rows.First(r => (string?)r.Tag == "record-screen");
            VisibleBox(recordRow).IsChecked = false;
            Pump(editor);
            Check(Rows(editor).Count == 12, "收起不等于从清单里删掉（行还在，只是勾掉）");

            // 保存（Ok_Click 会设 DialogResult，Show 出来的窗口设不了，所以直接走 Save）
            windowType.GetMethod("Save", Inst)!.Invoke(editor, null);
            editor.Close();
            editor = null;

            Check((int)layoutType.GetProperty("VisibleCount", Inst)!.GetValue(CurrentLayout())! == 11,
                "收起录屏后可见 11 个");

            // ============ ④ 落盘：ui.json 两个字段 ============
            string json = File.ReadAllText(uiFile);
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                var groups = root.GetProperty("CaptureActionGroups").EnumerateArray()
                    .Select(e => e.GetString()).ToList();
                var hidden = root.GetProperty("CaptureActionHidden").EnumerateArray()
                    .Select(e => e.GetString()).ToList();

                Check(groups[^1] == "copy,cancel", "落盘最后一组是「复制 / 取消」：" + groups[^1]);
                Check(hidden.Count == 1 && hidden[0] == "record-screen", "落盘隐藏项是录屏");
                Check(!groups.Any(g => g!.Contains("record-screen")), "隐藏的动作不在任何一组里");
            }

            // ============ ⑤ 新覆盖层立刻照新布局排 ============
            var second = OpenOverlay();
            var order2 = OrderOf(ToolbarOf(second));
            Check(!order2.Contains("Record"), "新布局下工具条没有录屏");
            Check(order2[^2] == "Copy" && order2[^1] == "Cancel", "新布局下复制排在取消前面");
            Check(GroupLines(second) == 7, $"收起录屏后少一组 → 7 条分隔线（实际 {GroupLines(second)}）");

            // ============ ⑥ 图片编辑窗按可用集过滤（同一份布局） ============
            object styleSet = styleStoreType.GetProperty("Current", Stat)!.GetValue(null)!;
            object selectTool = Enum.Parse(toolType, "Select");
            object editMode = Enum.Parse(modeType, "Edit");

            var editToolbar = Activator.CreateInstance(toolbarType)!;
            toolbarType.GetMethod("Init", Inst)!
                .Invoke(editToolbar, new[] { styleSet, selectTool, editMode });

            var editOrder = OrderOf(editToolbar);
            Check(editOrder.SequenceEqual(new[]
            {
                "Recognize", "Pin", "Print", "QuickSave", "SaveAs", "Copy",
            }), "编辑窗只排用得到的：识别 / 贴图 / 打印 / 快速保存 / 另存为 / 复制：" + string.Join(",", editOrder));

            // ============ ⑦ 恢复默认 ============
            editor = OpenEditor();
            windowType.GetMethod("Reset_Click", Inst)!
                .Invoke(editor, new object?[] { null, new RoutedEventArgs() });
            Pump(editor);
            windowType.GetMethod("Save", Inst)!.Invoke(editor, null);
            editor.Close();
            editor = null;

            var third = OpenOverlay();
            Check(OrderOf(ToolbarOf(third)).SequenceEqual(defaultOrder),
                "「恢复默认」把顺序与显隐一起还原");
            Check(GroupLines(third) == 8, "恢复默认后又是 8 条分隔线（7 组）");
        }
        finally
        {
            try { editor?.Close(); } catch { /* 已经关了 */ }
            foreach (var w in overlays)
            {
                try { w.Close(); } catch { /* 已经关了 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            storeType.GetMethod("Forget", Stat)!.Invoke(null, null);
        }
    }
}
