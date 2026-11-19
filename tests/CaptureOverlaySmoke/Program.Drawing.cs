using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 绘制行为设置（P9-b，照上游 <c>drawing/*</c>）：默认值 / 落盘顺序 / 设置页勾选 /
/// 两份工具清单窗口 / 工具组首按钮 / 记住上次工具 / 自动选中 / 快速选择 / Delete 吞键。
/// </summary>
/// <remarks>
/// 配置根指向冒烟自己的临时目录（不碰仓库里的 <c>configs/settings.json</c>），段尾搬回原来的
/// <c>ConfigStore.ConfigPath</c> 并丢掉所有缓存。真鼠标事件都打在真覆盖层上。
/// </remarks>
internal static partial class Program
{
    private static void DrawingSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var settingsType = asm.GetType("LTools.Features.CapturePro.DrawingSettings")!;
        var memoryType = asm.GetType("LTools.Features.CapturePro.CaptureToolMemory")!;
        var idsType = asm.GetType("LTools.Features.CapturePro.CaptureToolIds")!;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;

        object Tool(string name) => Enum.Parse(toolType, name);

        bool Flag(string name) => (bool)settingsType.GetProperty(name, Stat)!.GetValue(null)!;
        void SetFlag(string method, bool value) =>
            settingsType.GetMethod(method, Stat)!.Invoke(null, new object[] { value });

        IReadOnlyList<string> List(string name) =>
            (IReadOnlyList<string>)settingsType.GetProperty(name, Stat)!.GetValue(null)!;

        string Describe(IEnumerable<string> ids) =>
            (string)idsType.GetMethod("Describe", Stat)!.Invoke(null, new object?[] { ids })!;

        void Invalidate()
        {
            settingsType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
            memoryType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
        }

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "drawing-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");
        string uiFile = Path.Combine(configRoot, "configs", "ui.json");

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        var overlays = new List<Window>();
        Window? settings = null;
        Window? toolList = null;
        try
        {
            // ============ ① 默认值（上游 configurationschema）============
            Check(!Flag("AutoSelectNew"), "自动选中新元素默认关");
            Check(Flag("DeleteTextAfterCursor"), "Delete 删光标后的字符默认开");
            Check(!Flag("RememberLastTool"), "记住上次工具默认关");
            Check(!Flag("AlwaysShowFirstGroup"), "组首按钮常显默认关");
            Check(Describe(List("AutoSelectExcluded")) == "气泡 / 画笔 / 画笔滤镜 / 文字",
                "自动选中排除默认 4 个：" + Describe(List("AutoSelectExcluded")));
            Check(Describe(List("QuickSelectionDisabled")) == "画笔 / 画笔滤镜",
                "禁用快速选择默认 2 个：" + Describe(List("QuickSelectionDisabled")));
            Check(!File.Exists(settingsFile), "只读缓存不会平白落一份 settings.json");

            // ============ ② 落盘：写了才写文件，顺序照上游，认不出的丢掉 ============
            SetFlag("SetAutoSelectNew", true);
            Check(File.Exists(settingsFile), "改设置才出现 settings.json");
            string text = File.ReadAllText(settingsFile);
            Check(text.Contains("\"Drawing\"") && text.Contains("\"AutomaticallySelectNewlyCreatedElements\": true"),
                "settings.json 里有 Drawing 段的自动选中开");
            Invalidate();
            Check(Flag("AutoSelectNew"), "清缓存重新读盘：自动选中仍是开");

            settingsType.GetMethod("SetQuickSelectionDisabled", Stat)!
                .Invoke(null, new object[] { new[] { "text", "nope", "shape" } });
            Invalidate();
            Check(string.Join("|", List("QuickSelectionDisabled")) == "shape|text",
                "清单按上游顺序落盘（不是用户写的顺序），认不出的丢掉："
                + string.Join("|", List("QuickSelectionDisabled")));

            settingsType.GetMethod("SetAutoSelectExcluded", Stat)!
                .Invoke(null, new object?[] { Array.Empty<string>() });
            Invalidate();
            Check(List("AutoSelectExcluded").Count == 0, "空清单照样保存（= 所有工具都参与）");

            // 回到默认，后面的断言才有确定答案
            SetFlag("SetAutoSelectNew", false);
            SetFlag("SetDeleteTextAfterCursor", true);
            SetFlag("SetRememberLastTool", false);
            SetFlag("SetAlwaysShowFirstGroup", false);
            settingsType.GetMethod("SetAutoSelectExcluded", Stat)!
                .Invoke(null, new object[] { new[] { "free-draw", "pen-filter", "text", "speech-bubble" } });
            settingsType.GetMethod("SetQuickSelectionDisabled", Stat)!
                .Invoke(null, new object[] { new[] { "free-draw", "pen-filter" } });
            Invalidate();

            // ============ ③ 设置页：六个控件回显 + 改动即落盘 ============
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);

            settings.GetType().GetField("_initializing", Inst)!.SetValue(settings, false);
            settings.GetType().GetMethod("PopulateDrawing", Inst)!.Invoke(settings, null);

            CheckBox? Box(string name) => settings!.FindName(name) as CheckBox;
            Check(Box("CaptureAutoSelectBox") != null, "设置页有「自动选中」勾选框");
            Check(Box("CaptureDeleteTextAfterCursorBox")?.IsChecked == true, "Delete 勾选框回显为勾上");
            Check(Box("CaptureRememberToolBox")?.IsChecked == false, "记住上次工具回显为不勾");
            Check(Box("CaptureAlwaysShowFirstBox")?.IsChecked == false, "组首常显回显为不勾");
            Check(settings.FindName("CaptureAutoSelectExcludedButton") is Button, "「自动选中排除」有编辑按钮");
            Check(settings.FindName("CaptureQuickSelectionDisabledButton") is Button, "「禁用快速选择」有编辑按钮");

            var excludedHint = settings.FindName("CaptureAutoSelectExcludedHint") as TextBlock;
            Check(excludedHint != null && excludedHint.Text.Contains("文字") && excludedHint.Text.Contains("气泡"),
                "排除清单摘要写明当前工具：" + excludedHint?.Text);

            SetFlag("SetAutoSelectNew", false);
            settings.GetType().GetMethod("PopulateDrawing", Inst)!.Invoke(settings, null);
            Box("CaptureAutoSelectBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("AutoSelectNew"), "勾上「自动选中」立刻写进设置");
            Check(File.ReadAllText(settingsFile).Contains("\"AutomaticallySelectNewlyCreatedElements\": true"),
                "同一个勾也落到 settings.json");
            Box("CaptureAutoSelectBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("AutoSelectNew"), "取消勾选同样即时生效");

            // ============ ④ 两份工具清单窗口（16 项 / 默认勾选 / 恢复默认）============
            var listWindowType = asm.GetType("LTools.Views.CapturePro.CaptureToolListWindow")!;
            Window OpenToolList(bool quick)
            {
                var w = (Window)Activator.CreateInstance(listWindowType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new object[] { quick }, null)!;
                w.Show();
                Pump(w);
                return w;
            }

            List<CheckBox> Boxes(Window w) =>
                ((ItemsControl)w.FindName("ToolList")!).Items.Cast<CheckBox>().ToList();

            toolList = OpenToolList(false);
            var rows = Boxes(toolList);
            Check(rows.Count == 16, $"排除清单窗口列出 16 个工具（实际 {rows.Count}）");
            Check(rows.Count(b => b.IsChecked == true) == 4, "默认勾上 4 个（画笔 / 画笔滤镜 / 文字 / 气泡）");
            Check(rows.Select(b => b.Content?.ToString()).SequenceEqual(new[]
            {
                "形状", "气泡", "箭头", "直线", "测距", "测角", "括号", "画笔",
                "矩形荧光", "画笔荧光", "聚光", "放大镜", "矩形滤镜", "画笔滤镜", "文字", "序号",
            }), "顺序照上游 drawingSelectableToolOptions");
            Check(((Window)toolList).Title == "不自动选中的工具", "标题写的是「不自动选中」");

            foreach (var box in rows) box.IsChecked = false;
            listWindowType.GetMethod("Reset_Click", Inst)!
                .Invoke(toolList, new object?[] { null, new RoutedEventArgs() });
            Check(Boxes(toolList).Count(b => b.IsChecked == true) == 4, "「恢复默认」勾回 4 个");
            toolList.Close();
            toolList = null;

            toolList = OpenToolList(true);
            Check(((Window)toolList).Title == "禁用快速选择的工具", "标题写的是「禁用快速选择」");
            Check(Boxes(toolList).Count(b => b.IsChecked == true) == 2, "禁用清单默认勾 2 个");
            toolList.Close();
            toolList = null;

            // ============ ⑤ 工具条：组首按钮常显 vs 上次点过的那个 ============
            var shot = BuildShot(1200, 800);
            var monitor = new System.Drawing.Rectangle(0, 0, 1200, 800);

            var window = NewOverlay(shot, monitor);
            overlays.Add(window);
            window.Show();
            window.Activate();
            Pump(window);
            SetSelection(window, new Rect(20, 20, 900, 700));
            typeof(CaptureOverlayWindow).GetMethod("SyncArea", Inst)!.Invoke(window, null);
            Pump(window);

            var setToolMethod = typeof(CaptureOverlayWindow).GetMethod("SetTool", Inst)!;
            void SetTool(Window w, string name) => setToolMethod.Invoke(w, new[] { Tool(name) });

            var toolbar = Field(window, "Toolbar")!;
            var cells = (IList)Field(toolbar, "_cells")!;
            object? ArrowCell = null;
            foreach (var cell in cells)
            {
                var tools = (IList)cell.GetType().GetProperty("Tools")!.GetValue(cell)!;
                if (tools.Cast<object>().Any(t => t.ToString() == "Arrow")) ArrowCell = cell;
            }

            Check(cells.Count == 13, $"全新配置（没有 ui.json）工具栏按目录默认分 13 格，实际 {cells.Count}");
            Check(ArrowCell != null, "工具栏里有「箭头 / 直线」那一格（默认布局把两个合成一格）");
            var catalogType = asm.GetType("LTools.Features.CapturePro.ToolCatalog")!;
            string NameOfTool(object tool)
            {
                var info = catalogType.GetMethod("Of", Stat)!.Invoke(null, new[] { tool })!;
                return (string)info.GetType().GetProperty("Name")!.GetValue(info)!;
            }

            var cellTools = (IList)ArrowCell!.GetType().GetProperty("Tools")!.GetValue(ArrowCell)!;
            string firstToolName = NameOfTool(cellTools[0]!);
            var showIcon = toolbar.GetType().GetMethod("ShowCellIcon", Inst)!;
            string TipOf(object cell) =>
                ((RadioButton)cell.GetType().GetProperty("Button")!.GetValue(cell)!).ToolTip?.ToString() ?? "";

            // 模拟「这一格上次点过直线」
            ArrowCell!.GetType().GetProperty("Current")!.SetValue(ArrowCell, Tool("Line"));
            showIcon.Invoke(toolbar, new[] { ArrowCell });
            Check(TipOf(ArrowCell).StartsWith("直线", StringComparison.Ordinal),
                "组首常显关着：这一格显示上次点过的「直线」：" + TipOf(ArrowCell).Split('\n')[0]);

            SetFlag("SetAlwaysShowFirstGroup", true);
            Check(Flag("AlwaysShowFirstGroup"), "组首常显的开关真的变成开（读回设置）");
            showIcon.Invoke(toolbar, new[] { ArrowCell });
            Check(TipOf(ArrowCell).StartsWith(firstToolName, StringComparison.Ordinal),
                $"组首常显打开：固定显示组里第一个「{firstToolName}」：" + TipOf(ArrowCell).Split('\n')[0]);
            SetFlag("SetAlwaysShowFirstGroup", false);

            // ============ ⑥ 自动选中：真鼠标画一条 → 选中它、工具不变 ============
            PointScreen(window, new Point(120, 120));
            SetTool(window, "Shape");
            Pump(window);

            void Drag(Point from, Point to)
            {
                PointScreen(window, from);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(90);
                PointScreen(window, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), 80);
                PointScreen(window, to, 120);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(260);
                Pump(window);
            }

            void ClickAt(Point at)
            {
                PointScreen(window, at);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(90);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(260);
                Pump(window);
            }

            SetFlag("SetAutoSelectNew", true);
            Drag(new Point(120, 120), new Point(360, 300));

            var doc = Member(window, "Document");
            var marks = (IList)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
            var selected = (IList)Field(window, "_selected")!;
            Check(marks.Count == 1, $"画完一个形状（标注 {marks.Count} 条）");
            Check(selected.Count == 1 && (int)selected[0]! == marks.Count - 1,
                "自动选中：刚画的那一条被选中");
            Check(Field(window, "_tool")!.ToString() == "Shape", "自动选中不改当前工具（还能接着画）");

            // ============ ⑦ 快速选择：同类元素上单击 = 选中并拖它，不是再画一条 ============
            SetFlag("SetAutoSelectNew", false);
            typeof(CaptureOverlayWindow).GetMethod("SelectMark", Inst)!.Invoke(window, new object[] { -1 });
            Pump(window);

            ClickAt(new Point(240, 121));    // 形状的上边
            Check(marks.Count == 1, $"单击已有形状没有再画一条（标注 {marks.Count} 条）");
            selected = (IList)Field(window, "_selected")!;
            Check(selected.Count == 1, $"快速选择：点中已有形状就选中它（选中 {selected.Count} 条）");

            if (selected.Count == 0)
            {
                var mark = marks[0]!;
                Console.WriteLine("debug: mark=" + mark.GetType().Name +
                                  " bounds=" + mark.GetType().GetProperty("Bounds")!.GetValue(mark) +
                                  " hit=" + doc.GetType().GetMethod("HitTest")!
                                      .Invoke(doc, new object[] { new Point(240, 121), 4d }) +
                                  " tool=" + Field(window, "_tool"));
            }

            // 列进「禁用快速选择」就不吃这一下：仍是画新形状的起手（没拖动 = 不落标注）
            settingsType.GetMethod("SetQuickSelectionDisabled", Stat)!
                .Invoke(null, new object[] { new[] { "free-pen", "shape" } });
            Invalidate();
            typeof(CaptureOverlayWindow).GetMethod("SelectMark", Inst)!.Invoke(window, new object[] { -1 });
            Pump(window);

            ClickAt(new Point(240, 121));
            selected = (IList)Field(window, "_selected")!;
            Check(marks.Count == 1 && selected.Count == 0,
                $"禁用快速选择后同一处单击不再选中（选中 {selected.Count} 条）");

            settingsType.GetMethod("SetQuickSelectionDisabled", Stat)!
                .Invoke(null, new object[] { new[] { "free-draw", "pen-filter" } });
            Invalidate();

            // ============ ⑧ 记住上次工具：ui.json 里存 / 新会话直接进那个工具 ============
            SetTool(window, "Highlighter");
            Pump(window);
            Check(File.Exists(uiFile) && File.ReadAllText(uiFile).Contains("\"CaptureLastTool\": \"pen-highlight\""),
                "换工具时把「上次工具」写进 ui.json");

            SetFlag("SetRememberLastTool", true);
            Invalidate();

            var second = NewOverlay(shot, monitor);
            overlays.Add(second);
            second.Show();
            Pump(second);
            Check(Field(second, "_tool")!.ToString() == "Highlighter",
                "记住上次工具：新会话直接从「画笔荧光」开始（实际 " + Field(second, "_tool") + "）");
            Check(Field(second, "Toolbar")!.GetType().GetProperty("CurrentTool", Inst)!
                    .GetValue(Field(second, "Toolbar"))!.ToString() == "Highlighter",
                "工具栏也停在同一格");

            SetFlag("SetRememberLastTool", false);
            Invalidate();

            var third = NewOverlay(shot, monitor);
            overlays.Add(third);
            third.Show();
            Pump(third);
            Check(Field(third, "_tool")!.ToString() == "Select",
                "关掉「记住上次工具」后新会话仍从「选择」开始");

            // ============ ⑨ Delete 吞键（drawing/delete_text_after_cursor）============
            third.Activate();
            Pump(third);
            SetTool(third, "Text");
            Pump(third);
            SetSelection(third, new Rect(30, 30, 700, 500));
            typeof(CaptureOverlayWindow).GetMethod("SyncArea", Inst)!.Invoke(third, null);
            Pump(third);

            ClickAt2(third, new Point(120, 120));
            var entry = (TextBox)Field(third, "TextEntry")!;
            Check(entry.Visibility == Visibility.Visible, "文字工具单击弹出输入框");

            // 直接调那个处理器（真按 Delete 时 TextBox 自己也会把它标成已处理，测不出我们的守卫）
            var textKeyDown = typeof(CaptureOverlayWindow).GetMethod("TextEntry_KeyDown", Inst)!;
            bool GuardHandled()
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(entry), Environment.TickCount, Key.Delete)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                textKeyDown.Invoke(third, new object?[] { entry, args });
                return args.Handled;
            }

            SetFlag("SetDeleteTextAfterCursor", false);
            Check(GuardHandled(), "关掉「Delete 删光标后的字符」时 Delete 被吞掉（什么都不删）");
            SetFlag("SetDeleteTextAfterCursor", true);
            Check(!GuardHandled(), "打开时 Delete 照常交给输入框");
            typeof(CaptureOverlayWindow).GetMethod("CancelText", Inst)!.Invoke(third, null);

            Console.WriteLine("drawing：设置读写 / 设置页 / 清单窗口 / 组首按钮 / 自动选中 / 快速选择 / 上次工具 / Delete 全部验过");
        }
        finally
        {
            foreach (var window in overlays)
                try { window.Close(); } catch { }
            try { toolList?.Close(); } catch { }
            try { settings?.Close(); } catch { }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
            asm.GetType("LTools.Features.CapturePro.CaptureStyleStore")!
                .GetMethod("Forget", Stat)!.Invoke(null, null);
            asm.GetType("LTools.Features.CapturePro.ToolLayoutStore")!
                .GetMethod("Forget", Stat)!.Invoke(null, null);
        }
    }

    /// <summary>把 DIP 点换成屏幕物理像素并停一下（等覆盖层把 MouseMove 处理完）。</summary>
    private static void PointScreen(Window window, Point dip, int settle = 220)
    {
        Point screen = window.PointToScreen(dip);
        SetCursorPos((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        Thread.Sleep(settle);
        Pump(window);
    }

    /// <summary>在指定覆盖层上单击（那个窗口的坐标）。</summary>
    private static void ClickAt2(Window window, Point dip)
    {
        PointScreen(window, dip);
        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(260);
        Pump(window);
    }
}
