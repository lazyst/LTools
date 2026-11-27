using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Features;
using LTools.Views.Controls;

namespace PanelDragSmoke;

/// <summary>
/// 面板拖放的冒烟：真开一扇 <c>SuperPanelWindow</c>，真鼠标把文件拖到磁贴网格上，
/// 拖动期间高频采样**虚线占位还挂不挂着**。
/// </summary>
/// <remarks>
/// <para>
/// 回归的是 2026-11-25 报的那个 bug：占位是 <c>IsHitTestVisible=false</c> 的，插到光标那一格时
/// 命中会穿到块容器上；旧实现靠 <c>InputHitTest</c> 找网格 → 判不出来 → 每个 <c>DragOver</c>
/// 都先把占位清掉再加回来，看起来就是「一直闪」。
/// </para>
/// <para>
/// 所以断言的是「拖停在网格上、光标来回抖的时候，占位始终在可视树里」（采样绝大多数挂着），
/// 而不是「拖完能建动作」——后者只证明能落，证明不了过程稳。
/// </para>
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    private static readonly Assembly Lt = typeof(PanelConfig).Assembly;

    private static int _pass, _fail;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        string root = Path.Combine(Path.GetTempPath(), "ltools-paneldrag-" + Guid.NewGuid().ToString("N")[..8]);
        string configPath = Path.Combine(root, "LTools.json");
        Window? panel = null;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        try
        {
            // ---- ① 临时数据根：6 个动作塞进一个分组（和真面板长得一样）----
            Directory.CreateDirectory(root);
            Type store = Lt.GetType("LTools.Features.ConfigStore", true)!;
            Type panels = Lt.GetType("LTools.Features.Panel.PanelLibrary", true)!;

            Call(store, "Initialize", configPath);

            var ids = new List<string>();
            for (int i = 1; i <= 6; i++)
            {
                var action = new ActionDto { Name = "冒烟动作 " + i, Icon = "app" };
                Call(store, "AddAction", action);
                ids.Add(action.Id);
            }
            Call(store, "Save");

            var config = (PanelConfig)Get(panels, "Current")!;
            config.Preferences.Width = 760;
            config.Preferences.Height = 560;
            config.Preferences.ShowGroupTitles = true;

            if (config.Global.Scenes.Count == 0) config.Global.Scenes.Add(new PanelSceneDto { Id = "s1", Name = "全局" });
            var group = config.Global.Scenes[0].Groups.FirstOrDefault();
            if (group == null)
            {
                group = new PanelGroupDto { Id = "g1", Name = "常用" };
                config.Global.Scenes[0].Groups.Add(group);
            }
            group.Actions.Clear();
            group.Actions.AddRange(ids);
            Call(panels, "Save");

            // ---- ② 开面板 ----
            Type panelType = Lt.GetType("LTools.Views.SuperPanelWindow", true)!;
            panel = (Window)Activator.CreateInstance(panelType, Any, null, new object?[] { null, 0, 0 }, null)!;
            panel.WindowStartupLocation = WindowStartupLocation.Manual;
            panel.Show();
            panel.Left = 520;
            panel.Top = 120;
            panel.Width = 760;
            panel.Height = 560;
            Pump(panel);

            Check("面板出来了", panel.IsVisible && panel.ActualWidth > 400);

            TileGridPanel? grid = FindGrid(panel);
            Check("磁贴网格里有 6 个磁贴", grid != null && Tiles(grid).Count == 6,
                grid == null ? "没找到网格" : Tiles(grid).Count.ToString());
            if (grid == null) return Finish();

            // ---- ③ 真鼠标：源窗口 → 网格中间，停住并来回抖，期间采样占位 ----
            string file = Path.Combine(root, "拖我.txt");
            File.WriteAllText(file, "panel-drag");

            Button tile = Tiles(grid)[Math.Min(2, Tiles(grid).Count - 1)];
            Point to = tile.PointToScreen(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));

            FieldInfo holeField = panelType.GetField("_dropHole", Any)!;
            int samples = 0, attached = 0;
            bool sampling = false;

            var sampler = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
            sampler.Tick += (_, _) =>
            {
                if (!sampling) return;
                samples++;
                if (holeField.GetValue(panel) is FrameworkElement { Parent: not null }) attached++;
            };
            sampler.Start();

            int before = group.Actions.Count;
            DragDropEffects effect = DragOnce(file, to,
                onHold: () => sampling = true,
                onDone: () => sampling = false,
                panel);
            sampler.Stop();

            double ratio = samples == 0 ? 0 : (double)attached / samples;
            Check("拖动期间采到样本", samples >= 20, $"样本 {samples}");
            Check("拖停在网格上时占位一直挂着（不再一明一灭）", ratio >= 0.95,
                $"挂着 {attached}/{samples} = {ratio:P0}");
            Check("松手后文件落成动作", group.Actions.Count == before + 1,
                $"{before} → {group.Actions.Count}，源返回 {effect}");

            // ---- ④ 拖到面板上再拖走（不松手在面板上）：占位要收掉，动作不加 ----
            int afterDrop = group.Actions.Count;
            Point away = new(40, 300);
            DragOnce(file, to, onHold: null, onDone: null, panel, cancelTo: away);
            Pump(panel, 400);

            Check("拖走（不在面板上松手）不建动作", group.Actions.Count == afterDrop,
                $"{afterDrop} → {group.Actions.Count}");
            Check("拖走后占位收掉", holeField.GetValue(panel) == null,
                holeField.GetValue(panel) == null ? "" : "占位还在");

            // ---- ⑤ 钉住 = 工具盘（2026-11-26）：点动作 / 右键编辑都不收，只有主动关闭才关 ----
            // 让控制器认识这扇窗（正式链路里由 SuperPanel.Show 建窗）。
            Type controller = Lt.GetType("LTools.Features.SuperPanel", true)!;
            controller.GetField("_window", Any)!.SetValue(null, panel);

            panelType.GetMethod("SetPinned", Any)!.Invoke(panel, new object[] { true });
            Pump(panel);
            Check("钉住：进入工具盘态", (bool)Get(controller, "IsPinned")!);

            panelType.GetMethod("RequestEscape", Any)!.Invoke(panel, null);
            Pump(panel);
            Check("钉住：Esc 不收面板", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            Call(controller, "CloseAfterUse");   // 执行动作 / 草稿那一下
            Pump(panel);
            Check("钉住：用完即收不收面板", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            var aside = (IDisposable)Call(controller, "StepAside")!;   // 右键「编辑动作…」
            Pump(panel);
            Check("钉住：开编辑器前面板让开（隐藏但没关）",
                !panel.IsVisible && (bool)Get(controller, "IsOpen")!);
            aside.Dispose();
            Pump(panel);
            Check("钉住：编辑器关掉后自己回来", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            panelType.GetMethod("SetPinned", Any)!.Invoke(panel, new object[] { false });
            Pump(panel);
            var aside2 = (IDisposable)Call(controller, "StepAside")!;
            Pump(panel);
            Check("未钉住：开编辑器前面板照旧关掉",
                !panel.IsVisible && !(bool)Get(controller, "IsOpen")!);
            aside2.Dispose();

            // ---- ⑥ 真右键菜单：多级菜单往回走不能被关掉（2026-11-26 用户报）----
            panel = (Window)Activator.CreateInstance(panelType, Any, null, new object?[] { null, 0, 0 }, null)!;
            panel.WindowStartupLocation = WindowStartupLocation.Manual;
            panel.Show();
            // 靠左上摆：三级菜单要往右下展开，别让屏幕右边缘把 popup 夹住（鼠标会被夹到锥外）
            panel.Left = 60;
            panel.Top = 60;
            panel.Width = 700;
            panel.Height = 500;
            Pump(panel, 300);

            var groups = (Panel)panelType.GetField("GlobalGroups", Any)!.GetValue(panel)!;
            var block = groups.Children.OfType<StackPanel>().FirstOrDefault();
            var title = block?.Children.OfType<TextBlock>().FirstOrDefault();
            Check("⑥ 前置：面板有分组标题", title != null);
            if (title != null)
            {
                RClick(Center(title));
                Pump(panel, 400);

                var menu = (ContextMenu?)panelType.GetField("_openMenu", Any)!.GetValue(panel);
                Check("⑥ 右键弹出了分组菜单", menu is { IsOpen: true });
                if (menu is { IsOpen: true })
                {
                    // ⑧ 菜单结构：右键不再有「从文件 / 文件夹创建动作」，换成「新建简单动作 ▸」。
                    var simple = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "新建简单动作");
                    Check("⑧ 菜单里有「新建简单动作 ▸」", simple != null);
                    Check("⑧ 菜单里有「新建组合动作…」",
                        menu.Items.OfType<MenuItem>().Any(m => m.Header as string == "新建组合动作…"));
                    Check("⑧ 菜单里没有「从文件创建动作…」",
                        !menu.Items.OfType<MenuItem>().Any(m => (m.Header as string)?.StartsWith("从文件") == true));
                    Check("⑧ 菜单里没有「从文件夹创建动作…」",
                        !menu.Items.OfType<MenuItem>().Any(m => (m.Header as string)?.StartsWith("从文件夹") == true));

                    if (simple != null)
                    {
                        var names = simple.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
                        Check("⑧ 简单动作子菜单有七项", names.Count == 7, string.Join(" / ", names));
                        foreach (string expected in new[]
                                 { "打开文件", "打开文件夹", "启动程序", "打开网址", "回放键鼠宏", "运行命令", "模拟按键" })
                            Check($"⑧ 简单动作含「{expected}」", names.Contains(expected));
                    }

                    var top = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "常用功能");
                    var other = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "LTools 功能");
                    Check("⑥ 菜单里有「常用功能」子菜单", top != null);
                    if (top != null)
                    {
                        MoveTo(Center(Rect(top)));
                        Pump(panel, 350);
                        Check("⑥ 悬停一级项展开二级", top.IsSubmenuOpen, $"top.sub={top.IsSubmenuOpen}");

                        var mid = top.Items.OfType<MenuItem>().FirstOrDefault();
                        Check("⑥ 二级有分类项", mid != null);
                        if (mid != null)
                        {
                            MoveTo(Center(Rect(mid)));
                            Pump(panel, 350);
                            Check("⑥ 悬停二级项展开三级", mid.IsSubmenuOpen && top.IsSubmenuOpen,
                                $"top.sub={top.IsSubmenuOpen} mid.sub={mid.IsSubmenuOpen}");

                            // 在三级菜单里停 700ms（> WPF 的 400ms + MenuFx 的 260ms）：
                            // 一级项的收起计时器到点时指针还在它的后代菜单里，整支不许被收。
                            if (mid.Template?.FindName("PART_Popup", mid) is Popup { Child: FrameworkElement three })
                            {
                                MoveTo(Center(Rect(three)));
                                Pump(panel, 700);
                                Check("⑥ 三级菜单里停 700ms：整支仍在",
                                    menu.IsOpen && top.IsSubmenuOpen && mid.IsSubmenuOpen,
                                    $"menu={menu.IsOpen} top.sub={top.IsSubmenuOpen} mid.sub={mid.IsSubmenuOpen}");
                            }

                            // 三级 → 二级（慢慢往回走）
                            if (mid.Template?.FindName("PART_Popup", mid) is Popup { Child: FrameworkElement leafPopup })
                            {
                                MoveTo(Center(Rect(leafPopup)));
                                Pump(panel, 250);
                                var leafRect = Rect(leafPopup);
                                var midRect = Rect(mid);
                                // 回程从**两边中心**走（不是左上角）：沿菜单的通道，别贴着菜单上边缘
                                Point from = Center(leafRect), toward = Center(midRect);
                                for (int i = 1; i <= 10; i++)
                                {
                                    MoveTo(new Point(
                                        from.X + (toward.X - from.X) * i / 10.0,
                                        from.Y + (toward.Y - from.Y) * i / 10.0));
                                    Pump(panel, 40);
                                }
                                Pump(panel, 350);
                                Check("⑥ 三级回二级：整份菜单仍在", menu.IsOpen, $"menu.IsOpen={menu.IsOpen}");
                                Check("⑥ 三级回二级：二级仍展开", mid.IsSubmenuOpen && top.IsSubmenuOpen,
                                    $"top.sub={top.IsSubmenuOpen} mid.sub={mid.IsSubmenuOpen}");

                                // 二级 → 一级（回到同一个父项）
                                var topRect = Rect(top);
                                from = Center(midRect);
                                toward = Center(topRect);
                                for (int i = 1; i <= 10; i++)
                                {
                                    MoveTo(new Point(
                                        from.X + (toward.X - from.X) * i / 10.0,
                                        from.Y + (toward.Y - from.Y) * i / 10.0));
                                    Pump(panel, 40);
                                }
                                Pump(panel, 400);
                                Check("⑥ 二级回一级：整份菜单仍在", menu.IsOpen, $"menu.IsOpen={menu.IsOpen}");
                                Check("⑥ 二级回一级：一级子菜单仍开", top.IsSubmenuOpen,
                                    $"top.sub={top.IsSubmenuOpen} mid.sub={mid.IsSubmenuOpen}");
                            }
                        }

                        // ⑦ 用户 2026-11-26 报的精确路径：指针停在二级「剪贴板」上（开着三级），
                        //    往根菜单里的「新建分组」斜着走（左下，会穿过二级菜单左边缘与两个 popup
                        //    之间的空隙），整个右键菜单不许关。
                        var clip = top.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "剪贴板");
                        var newGroup = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Header as string == "新建分组");
                        Check("⑦ 菜单里有「剪贴板」与「新建分组」", clip != null && newGroup != null);
                        if (clip != null && newGroup != null)
                        {
                            MoveTo(Center(Rect(top)));
                            Pump(panel, 350);
                            MoveTo(Center(Rect(clip)));
                            Pump(panel, 400);
                            Check("⑦ 前置：剪贴板的三级开着", clip.IsSubmenuOpen,
                                $"clip.sub={clip.IsSubmenuOpen} top.sub={top.IsSubmenuOpen}");

                            Point a = Center(Rect(clip)), b = Center(Rect(newGroup));
                            for (int i = 1; i <= 14; i++)
                            {
                                MoveTo(new Point(a.X + (b.X - a.X) * i / 14.0,
                                                 a.Y + (b.Y - a.Y) * i / 14.0));
                                Pump(panel, 40);
                                if (!menu.IsOpen)
                                {
                                    Console.WriteLine($"]] closed at step {i}");
                                    break;
                                }
                            }
                            Pump(panel, 350);
                            Check("⑦ 剪贴板 → 新建分组：整份菜单仍在", menu.IsOpen,
                                $"menu.IsOpen={menu.IsOpen} top.sub={top.IsSubmenuOpen} clip.sub={clip.IsSubmenuOpen}");
                        }
                    }

                    menu.IsOpen = false;
                    Pump(panel, 150);
                }
            }

            // ---- ⑨ 简单动作表单：七种预设都能建出完整表单（不弹模态）----
            Type simpleActions = Lt.GetType("LTools.Features.Panel.SimpleActions", true)!;
            Type dialogType = Lt.GetType("LTools.Views.SimpleActionDialog", true)!;
            var catalog = ((System.Collections.IEnumerable)simpleActions.GetField("Catalog", Any)!.GetValue(null)!)
                .Cast<object>().ToList();
            Check("⑨ 简单动作目录有七项", catalog.Count == 7, catalog.Count.ToString());
            foreach (object kind in catalog)
            {
                string name = (string)kind.GetType().GetProperty("Name")!.GetValue(kind)!;
                object dlg = dialogType.GetConstructors(Any).Single()
                    .Invoke(new object?[] { kind, null });
                var window = (Window)dlg;
                var form = window.FindName("FormHost") as StackPanel;
                var convert = window.FindName("ConvertBtn") as Button;
                Check($"⑨ 「{name}」表单建得出来", form is { Children.Count: >= 2 },
                    form == null ? "FormHost=null" : $"rows={form.Children.Count}");
                Check($"⑨ 「{name}」新建时不显示「转换为组合动作」",
                    convert is { Visibility: Visibility.Collapsed });
                Check($"⑨ 「{name}」标题是新建简单动作",
                    window.Title.StartsWith("新建简单动作", StringComparison.Ordinal), window.Title);
                window.Close();
            }

            // ⑨ 表单 → 结果：填了路径就能读出名称与参数（不弹模态，直接调 TryBuild）
            object openFile = catalog.Single(k =>
                (string)k.GetType().GetProperty("Name")!.GetValue(k)! == "打开文件");
            object openDialog = dialogType.GetConstructors(Any).Single()
                .Invoke(new object?[] { openFile, null });
            var openWindow = (Window)openDialog;
            var pathBox = (TextBox)openWindow.GetType().GetField("_text", Any)!.GetValue(openWindow)!;
            pathBox.Text = @"C:\Work\季度报表.xlsx";
            object? edit = openWindow.GetType().GetMethod("TryBuild", Any)!
                .Invoke(openWindow, new object?[] { true, false });
            Check("⑨ 打开文件：表单读得出结果", edit != null);
            if (edit != null)
            {
                Check("⑨ 打开文件：名称取文件名",
                    (string)edit.GetType().GetProperty("Name")!.GetValue(edit)! == "季度报表");
                var values = (Dictionary<string, string>)edit.GetType().GetProperty("Values")!.GetValue(edit)!;
                Check("⑨ 打开文件：path 读得回来",
                    values.TryGetValue("path", out string? path) && path == @"C:\Work\季度报表.xlsx",
                    values.TryGetValue("path", out string? p2) ? p2 : "(没有 path)");
                Check("⑨ 打开文件：普通确定不转换",
                    (bool)edit.GetType().GetProperty("Convert")!.GetValue(edit)! == false);
            }
            openWindow.Close();

            // ⑩ 完整创建：表单结果 → AddSimple → 动作进分组 + 落盘（不经过模态框）
            if (edit != null)
            {
                Type ops = Lt.GetType("LTools.Features.Panel.PanelActionOps", true)!;
                var add = ops.GetMethods(Any).First(m => m.Name == "AddSimple"
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[1].ParameterType == typeof(PanelGroupDto));
                int beforeCount = group.Actions.Count;
                ActionDto? created = (ActionDto?)add.Invoke(null,
                    new object?[] { null, group, openFile, edit });
                Check("⑩ 简单动作建得出来", created != null && created.SimpleKind == "openFile",
                    created?.SimpleKind ?? "null");
                if (created != null)
                {
                    Check("⑩ 动作进了分组", group.Actions.Count == beforeCount + 1 && group.Actions.Contains(created.Id));
                    Check("⑩ 动作体是一步「打开文件」",
                        created.Program is { Steps.Count: 1 } && created.Program.Steps[0].Unit == "file.openFile");
                    string actionFile = Path.Combine(root, "actions", created.Id, "action.json");
                    Check("⑩ 动作落盘（actions/<id>/action.json）", File.Exists(actionFile), actionFile);
                }
            }
            // ---- ⑪ 未配置提示：宿主里的面板（_preview 恒为 true）也必须显示 ----
            // 回归 2026-11-28 的 bug：BuildContext 原来写成 `!_preview && …`，面板只活在宿主里，
            // 于是「尚未为 X 添加专用设置」那条永远不显示，设置里「重新显示未配置提示」点了也没反应。
            var appRole = Lt.GetType("LTools.Core.AppRole", true)!;
            Call(appRole, "MarkUiHost");
            Check("⑪ 以宿主身份跑（面板 = 只读预览）", (bool)Get(appRole, "IsUiHost")!);

            var fgType = Lt.GetType("LTools.Native.ForegroundApp", true)!;
            object fg = fgType.GetConstructors(Any)[0].Invoke(new object?[]
            {
                IntPtr.Zero, @"C:\smoke\noscene.exe", "noscene.exe", "冒烟窗口", "SmokeWnd",
            });

            config.HintDismissed = false;
            Call(panels, "Save");

            Window OpenHintPanel()
            {
                var window = (Window)Activator.CreateInstance(panelType, Any, null,
                    new object?[] { fg, 0, 0 }, null)!;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Show();
                Pump(window);
                return window;
            }

            Window hintPanel = OpenHintPanel();
            var hintBar = (FrameworkElement?)hintPanel.FindName("ContextHintBar");
            var hintText = (TextBlock?)hintPanel.FindName("ContextHintText");
            Check("⑪ 未配置提示条显示出来", hintBar?.Visibility == Visibility.Visible,
                hintBar?.Visibility.ToString() ?? "null");
            Check("⑪ 文案带程序名", hintText?.Text == "尚未为“noscene”添加专用设置", hintText?.Text ?? "null");

            var dismiss = (Button?)hintPanel.FindName("HintDismissButton");
            dismiss!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(hintPanel);
            Check("⑪ 点关闭后立刻收起", hintBar!.Visibility == Visibility.Collapsed,
                hintBar.Visibility.ToString());
            Check("⑪ 关闭写进配置", config.HintDismissed);
            Check("⑪ 关闭落盘 panel.json",
                File.ReadAllText(Path.Combine(root, "configs", "panel.json")).Contains("\"HintDismissed\": true"));
            hintPanel.Close();
            Pump(null, 100);

            // 设置里「重新显示未配置提示」= 置回 false 再保存 → 下次开面板又能看到
            config.HintDismissed = false;
            Call(panels, "Save");
            Window reopened = OpenHintPanel();
            var barAgain = (FrameworkElement?)reopened.FindName("ContextHintBar");
            Check("⑪ 重置后重新打开又能看到", barAgain?.Visibility == Visibility.Visible,
                barAgain?.Visibility.ToString() ?? "null");
            reopened.Close();
            Pump(null, 100);
        }
        catch (Exception ex)
        {
            Check("冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            try { panel?.Close(); } catch { /* 已经在关 */ }
            Pump(null, 200);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }

        return Finish();
    }

    // ============ 拖放驱动（真鼠标，照 tests/ShareSmoke/DragSmoke 的 Drive） ============

    private static DragDropEffects DragOnce(string file, Point to, Action? onHold, Action? onDone,
                                            Window host, Point? cancelTo = null)
    {
        var source = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 40,
            Top = 120,
            Width = 140,
            Height = 140,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
        };

        var handle = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            CornerRadius = new CornerRadius(8),
        };

        source.Content = handle;
        source.Show();
        Pump(source);

        DragDropEffects effect = DragDropEffects.None;
        handle.MouseLeftButtonDown += (_, _) =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file });
            effect = DragDrop.DoDragDrop(handle, data, DragDropEffects.Copy);
        };

        Point from = new(source.Left + source.Width / 2, source.Top + source.Height / 2);
        var driver = new Thread(() => Drive(from, to, onHold, onDone, cancelTo)) { IsBackground = true };
        driver.SetApartmentState(ApartmentState.STA);
        driver.Start();

        Wait(driver, host);
        source.Close();
        Pump(host);
        return effect;
    }

    private static void Drive(Point from, Point to, Action? onHold, Action? onDone, Point? cancelTo)
    {
        SetCursorPos((int)from.X, (int)from.Y);
        Thread.Sleep(200);
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(250);

        for (int step = 1; step <= 24; step++)
        {
            double t = step / 24.0;
            SetCursorPos((int)(from.X + (to.X - from.X) * t), (int)(from.Y + (to.Y - from.Y) * t));
            Thread.Sleep(20);
        }

        Thread.Sleep(250);
        onHold?.Invoke();

        // 停在网格上左右抖：每次移动都会重算落点，占位只要被清掉一次就会看出来。
        for (int i = 0; i < 40; i++)
        {
            SetCursorPos((int)to.X + (i % 2 == 0 ? -5 : 5), (int)to.Y + (i % 4 < 2 ? -3 : 3));
            Thread.Sleep(12);
        }

        onDone?.Invoke();
        Thread.Sleep(120);

        if (cancelTo is { } away)
        {
            for (int step = 1; step <= 12; step++)
            {
                double t = step / 12.0;
                SetCursorPos((int)(to.X + (away.X - to.X) * t), (int)(to.Y + (away.Y - to.Y) * t));
                Thread.Sleep(20);
            }

            Thread.Sleep(200);
        }
        else
        {
            SetCursorPos((int)to.X, (int)to.Y);
            Thread.Sleep(100);
        }

        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(300);
    }

    private static void Wait(Thread driver, Window? host)
    {
        var frame = new DispatcherFrame();
        var pump = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
            (_, _) => { if (!driver.IsAlive) frame.Continue = false; }, host?.Dispatcher);
        var deadline = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, host?.Dispatcher);

        pump.Start();
        deadline.Start();
        Dispatcher.PushFrame(frame);
        pump.Stop();
        deadline.Stop();
    }

    // ============ 视觉树 ============

    private static TileGridPanel? FindGrid(DependencyObject root)
    {
        if (root is TileGridPanel grid && Tiles(grid).Count > 0) return grid;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindGrid(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    /// <summary>真磁贴（排除「＋」加号格：它的 <c>ActionId</c> 是 null）。</summary>
    private static List<Button> Tiles(TileGridPanel grid) =>
        grid.Children.OfType<Button>()
            .Where(b => b.Tag?.GetType().GetProperty("ActionId")?.GetValue(b.Tag) is string && b.IsVisible)
            .ToList();

    private static void Pump(Window? window, int ms = 150)
    {
        Dispatcher dispatcher = window?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
    }

    // ============ 反射小工具 ============

    private static object? Call(Type type, string name, params object?[] args) =>
        Find(type, name, args.Length).Invoke(null, args);

    private static object? Get(Type type, string name) => type.GetProperty(name, Any)!.GetValue(null);

    private static MethodInfo Find(Type type, string name, int count) =>
        type.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count)
        ?? throw new MissingMethodException(type.Name + "." + name + "/" + count);

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
    }

    // ============ Win32 ============

    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;

    /// <summary>移到屏幕物理像素点。`SetCursorPos` 自己会生成 WM_MOUSEMOVE，别再叠相对的 mouse_event
    /// （相对移动会走系统鼠标加速，多次调用累积出几十像素的偏移，冒烟会把指针甩到菜单外）。</summary>
    private static void MoveTo(Point p) => SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));

    private static void RClick(Point p)
    {
        SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
        Thread.Sleep(60);
        mouse_event(RightDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(RightUp, 0, 0, 0, IntPtr.Zero);
    }

    /// <summary>元素在屏幕上的物理像素矩形（<c>PointToScreen</c> 给物理像素，尺寸按 DPI 换算）。</summary>
    private static Rect Rect(FrameworkElement e)
    {
        try
        {
            var origin = e.PointToScreen(new Point(0, 0));
            var scale = VisualTreeHelper.GetDpi(e);
            return new Rect(origin,
                new Size(e.ActualWidth * scale.DpiScaleX, e.ActualHeight * scale.DpiScaleY));
        }
        catch
        {
            return System.Windows.Rect.Empty;
        }
    }

    private static Point Center(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static Point Center(FrameworkElement e) => Center(Rect(e));

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
