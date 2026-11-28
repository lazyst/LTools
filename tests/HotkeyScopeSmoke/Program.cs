using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Config;
using LTools.Views;
using LTools.Views.Controls;

/// <summary>
/// 快捷键「组生效范围」（新功能）+ 生效范围编辑器（绑定 / 组共用一个控件）的界面冒烟。
/// </summary>
/// <remarks>
/// 只验界面这一层：XAML 能建出来、组生效范围进到组行与对话框、档位切换会收起 / 展开名单、
/// 录入框能把 CapsLock+H 记成 capslock+h。
/// 运行时匹配（绑定 > 组 > 全局默认）是纯函数，由 <c>--selftest --filter hotkeys</c> 断言；
/// CapsLock 的钩子放行要真按键（钩子忽略注入事件），只能手按。
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static int _pass, _fail;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Check("未抛异常", false, ex.ToString());
        }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var file = Fixture();
        LoadRegistry(file);

        var view = new HotkeysView();
        view.Reload();

        var list = (ListBox)Field(view, "GroupList")!;
        Check("组列表建出两行", list.Items.Count == 2, $"实际 {list.Items.Count}");
        if (list.Items.Count < 2) return;

        var scoped = (HotkeysView.GroupRow)list.Items[0]!;
        var plain = (HotkeysView.GroupRow)list.Items[1]!;

        Check("有范围的组：显示第二行", scoped.ScopeVisible, scoped.ScopeText);
        Check("有范围的组：短标签", scoped.ScopeText == "仅 chrome.exe", scoped.ScopeText);
        Check("跟随默认的组：不占第二行", !plain.ScopeVisible, plain.ScopeText);
        Check("跟随默认的组：文案", plain.ScopeText == "跟随全局默认", plain.ScopeText);
        Check("组行悬停说明写明绑定优先",
            scoped.ScopeTip.Contains("绑定 > 组", StringComparison.Ordinal), scoped.ScopeTip);
        Check("组行悬停说明列出名单",
            scoped.ScopeTip.Contains("chrome.exe", StringComparison.Ordinal), scoped.ScopeTip);

        // 模板实例化：组行只有 4 个操作图标（组生效范围挪到右边「添加快捷键」旁边了）
        var scopedHost = BuildRow(scoped, list.ItemTemplate);
        var plainHost = BuildRow(plain, list.ItemTemplate);

        Check("组行有 4 个操作图标（重命名 / 删除 / 上移 / 下移）",
            Descendants<Button>(scopedHost).Count() == 4, Descendants<Button>(scopedHost).Count().ToString());
        Check("组行里不再有「组生效范围」图标",
            !Descendants<Button>(scopedHost).Any(b => (b.CommandParameter as string) == "scope"),
            "还留着 CommandParameter=scope 的按钮");

        var scopedText = Descendants<TextBlock>(scopedHost).FirstOrDefault(t => t.Text == scoped.ScopeText);
        var plainText = Descendants<TextBlock>(plainHost).FirstOrDefault(t => t.Text == plain.ScopeText);
        Check("有范围的组：第二行可见", scopedText?.Visibility == Visibility.Visible,
            scopedText == null ? "没找到文本" : scopedText.Visibility.ToString());
        Check("跟随默认的组：第二行收起", plainText?.Visibility == Visibility.Collapsed,
            plainText == null ? "没找到文本" : plainText.Visibility.ToString());

        // 内置组不可删（「CapsLock 经典」「截图 Pro」）：✕ 收起、悬停说明写明，
        // 用户自建的组照常能删。判定只有 HotkeySeeding.IsBuiltInGroup 一处。
        Button? DeleteButton(FrameworkElement host) =>
            Descendants<Button>(host).FirstOrDefault(b => (b.CommandParameter as string) == "delete");
        Check("自建组：删除按钮可见", DeleteButton(plainHost)?.Visibility == Visibility.Visible,
            DeleteButton(plainHost)?.Visibility.ToString() ?? "没找到 ✕");
        Check("自建组：BuiltIn = false", !plain.BuiltIn, plain.NameTip);

        plain.Source.BuiltIn = true;
        var plainBuiltInHost = BuildRow(plain, list.ItemTemplate);
        Check("内置组：提示写明不能删除",
            plain.NameTip.Contains("不能删除", StringComparison.Ordinal), plain.NameTip);
        Check("内置组：删除按钮收起", DeleteButton(plainBuiltInHost)?.Visibility == Visibility.Collapsed,
            DeleteButton(plainBuiltInHost)?.Visibility.ToString() ?? "没找到 ✕");
        plain.Source.BuiltIn = false;

        // 右边「添加快捷键」旁边的组生效范围按钮：选中组时可用，没有组时禁用
        var scopeBtn = (Button)Field(view, "GroupScopeBtn")!;
        var addBtn = (Button)Field(view, "AddBindingBtn")!;
        Check("组生效范围按钮在右侧工具行", scopeBtn.Content as string == "组生效范围…",
            scopeBtn.Content as string ?? "(空)");
        Check("组生效范围在「添加快捷键」左边",
            Grid.GetColumn(scopeBtn) < Grid.GetColumn(addBtn),
            $"{Grid.GetColumn(scopeBtn)} / {Grid.GetColumn(addBtn)}");
        Check("有选中组时按钮可用", scopeBtn.IsEnabled, "被禁用了");

        // 组生效范围对话框：四档（含「跟随全局默认」），名单只在白 / 黑名单时显示
        var groupDialog = (Window)Activator.CreateInstance(typeof(HotkeyGroupDialog), Any, null,
            new object[] { file.Groups[0] }, null)!;
        var groupEditor = (AppScopeEditor)Field(groupDialog, "Editor")!;
        var groupCombo = (ComboBox)Field(groupEditor, "ScopeBox")!;

        Check("组对话框：四档范围", groupCombo.Items.Count == 4, groupCombo.Items.Count.ToString());
        Check("组对话框：第一档是「跟随全局默认」",
            (groupCombo.Items[0] as ComboBoxItem)?.Content as string == "跟随全局默认",
            (groupCombo.Items[0] as ComboBoxItem)?.Content as string ?? "(空)");
        Check("组对话框：回填白名单", groupEditor.SelectedScope == HotkeyScope.whitelist,
            groupEditor.SelectedScope.ToString());
        Check("组对话框：回填名单",
            groupEditor.SelectedFilter?.Processes.FirstOrDefault() == "chrome.exe",
            string.Join(",", groupEditor.SelectedFilter?.Processes ?? new List<string>()));
        Check("组对话框：白名单时名单面板可见", groupEditor.FilterVisible, "不可见");

        // 名单是「一行一个输入框 + 每行一个删除」：回填几行就是几行，＋ 添加 / 删除都只动自己那一行
        var processRows = (StackPanel)Field(groupEditor, "ProcessRows")!;
        static int RowCount(StackPanel host) => host.Children.Count;
        static TextBox RowBox(StackPanel host, int i) => (TextBox)((Grid)host.Children[i]).Children[0];
        static Button RowDelete(StackPanel host, int i) => (Button)((Grid)host.Children[i]).Children[1];

        Check("名单逐行显示：回填出 1 行进程", RowCount(processRows) == 1, RowCount(processRows).ToString());
        Check("每行都是单行输入框", RowBox(processRows, 0) is { Text: "chrome.exe" },
            RowBox(processRows, 0).Text);
        Check("每行都带删除按钮", RowDelete(processRows, 0).Content as string == "✕",
            RowDelete(processRows, 0).Content as string ?? "(空)");
        Check("行内不再挂「清除 ×」", !LTools.Views.Controls.TextBoxClear.GetAttach(RowBox(processRows, 0)),
            "还挂着清除按钮");

        var addProcess = (Button)Field(groupEditor, "AddProcessBtn")!;
        addProcess.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("「＋ 添加一行」加出一行空输入框",
            RowCount(processRows) == 2 && RowBox(processRows, 1).Text == "",
            RowCount(processRows) + " 行");
        RowBox(processRows, 1).Text = "code.exe";
        Check("新加那行进得了名单",
            groupEditor.SelectedFilter?.Processes.SequenceEqual(new[] { "chrome.exe", "code.exe" }) == true,
            string.Join(",", groupEditor.SelectedFilter?.Processes ?? new List<string>()));

        RowDelete(processRows, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("删除按钮只删自己那一行",
            RowCount(processRows) == 1
            && groupEditor.SelectedFilter?.Processes.SequenceEqual(new[] { "chrome.exe" }) == true,
            string.Join(",", groupEditor.SelectedFilter?.Processes ?? new List<string>()));

        RowDelete(processRows, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check("删空后补一行空输入框，不留死路",
            RowCount(processRows) == 1 && RowBox(processRows, 0).Text == "",
            RowCount(processRows) + " 行 / " + RowBox(processRows, 0).Text);

        Check("标题与类名也走同一套逐行名单",
            Field(groupEditor, "TitleRows") is StackPanel { Children.Count: 1 }
            && Field(groupEditor, "ClassRows") is StackPanel { Children.Count: 1 },
            "行数不对");

        groupCombo.SelectedIndex = 1;   // 所有窗口
        Check("切到「所有窗口」：名单面板收起", !groupEditor.FilterVisible, "还可见");
        Check("切到「所有窗口」：不写名单", groupEditor.SelectedFilter == null, "还带着名单");

        groupCombo.SelectedIndex = 0;   // 跟随全局默认
        Check("切到「跟随全局默认」：名单面板收起", !groupEditor.FilterVisible, "还可见");

        groupCombo.SelectedIndex = 3;   // 排除黑名单
        Check("切到「排除黑名单」：名单面板出现", groupEditor.FilterVisible, "没出现");

        // 绑定对话框共用同一个控件：第一档是「跟随组」，回填绑定的生效范围
        var binding = new HotkeyBinding
        {
            Id = "b1",
            Chord = "capslock+h",
            ActionId = "common.capture",
            Scope = HotkeyScope.blacklist,
            Filter = new AppFilter { Processes = { "chrome.exe" } },
        };
        var bindingDialog = (Window)Activator.CreateInstance(typeof(HotkeyEditDialog), Any, null,
            new object[] { binding }, null)!;
        var bindingEditor = (AppScopeEditor)Field(bindingDialog, "ScopeEditor")!;
        var bindingCombo = (ComboBox)Field(bindingEditor, "ScopeBox")!;

        Check("绑定对话框：四档范围", bindingCombo.Items.Count == 4, bindingCombo.Items.Count.ToString());
        Check("绑定对话框：第一档是「跟随组」",
            (bindingCombo.Items[0] as ComboBoxItem)?.Content as string == "跟随组",
            (bindingCombo.Items[0] as ComboBoxItem)?.Content as string ?? "(空)");
        Check("绑定对话框：回填黑名单", bindingEditor.SelectedScope == HotkeyScope.blacklist,
            bindingEditor.SelectedScope.ToString());
        Check("绑定对话框：回填名单",
            bindingEditor.SelectedFilter?.Processes.FirstOrDefault() == "chrome.exe",
            string.Join(",", bindingEditor.SelectedFilter?.Processes ?? new List<string>()));

        // 录入框：真按一次 CapsLock + H（不走钩子，直接喂键盘事件）→ 必须录成 capslock+h。
        // 钩子那一侧（放行 CapsLock、结束后收灯）在 Hooks/KeyboardHook，注入事件验不了，只能手按。
        // 需要真的显示窗口：KeyEventArgs 要一个 PresentationSource（.NET 10 不接受 null）。
        bindingDialog.Show();
        try
        {
            var source = PresentationSource.FromVisual((Visual)bindingDialog.Content)
                         ?? PresentationSource.FromVisual(bindingDialog);
            Check("录入框：窗口真的建出来了", source != null, "PresentationSource 为 null");
            if (source != null) RecordingCapsLock(bindingDialog, source);
        }
        finally { bindingDialog.Close(); }

        // 独立用：档位切换不依赖宿主窗口
        var standalone = new AppScopeEditor();
        standalone.SetOptions("跟随全局默认", "所有窗口");
        standalone.Load(HotkeyScope.inherit, new AppFilter { Processes = { "leftover.exe" } });
        Check("独立控件：跟随默认不吐名单", standalone.SelectedFilter == null, "还带着名单");
        Check("独立控件：跟随默认不算白 / 黑名单", !standalone.FilterVisible, "名单面板还开着");
        standalone.Load(HotkeyScope.all, new AppFilter { Processes = { "leftover.exe" } });
        Check("独立控件：所有窗口不吐名单", standalone.SelectedFilter == null, "还带着名单");
        standalone.Load(HotkeyScope.whitelist, new AppFilter { Titles = { "记事本" } });
        Check("独立控件：回填标题规则",
            standalone.SelectedFilter?.Titles.FirstOrDefault() == "记事本",
            string.Join(",", standalone.SelectedFilter?.Titles ?? new List<string>()));
        Check("独立控件：白名单时名单面板可见", standalone.FilterVisible, "不可见");

        // 人眼看一眼新的逐行名单（两行进程 + 一行标题 + 一行类名）
        var preview = new AppScopeEditor();
        preview.SetOptions("跟随全局默认", "所有窗口");
        preview.Load(HotkeyScope.whitelist, new AppFilter
        {
            Processes = { "chrome.exe", "Code.exe" },
            Titles = { "记事本" },
            Classes = { "Notepad" },
        });
        RenderPreview(preview, 560, 560, "scope-editor.png");

        // 没有组时按钮禁用（别让人对着空列表点出一个无名组）
        LoadRegistry(new HotkeyFile());
        view.Reload();
        Check("没有组时「组生效范围」禁用", !scopeBtn.IsEnabled, "还可用");
        LoadRegistry(file);
        view.Reload();

        RenderPreview(view, 1180, 700, "view.png");
        RenderPreview(view, 660, 700, "view-narrow.png");   // ≈ 设置窗口拉到最小 820 时的内容区

        // capslock 前缀的组合键解析由 --selftest --filter hotkeys 断言（HotkeyChord 是 internal）。
    }

    /// <summary>把一块界面渲染成 png（人眼看工具行会不会挤 / 组行第二行对不对 / 名单行对不对齐）。</summary>
    private static void RenderPreview(FrameworkElement content, int width, int height, string name)
    {
        var frame = new Window
        {
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.FindResource("BgBrush"),
            Content = content,
        };
        frame.Show();
        frame.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)(frame.ActualWidth * 2), (int)(frame.ActualHeight * 2),
            192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);

        string dir = Path.Combine(Path.GetTempPath(), "opencode", "hotkey-scope-smoke");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);

        frame.Content = null;   // 下一张要复用同一个 view
        frame.Close();
        Console.WriteLine("渲染：" + file);
    }

    /// <summary>
    /// 录入框：直接喂键盘事件（不经过钩子）→ 按 CapsLock 再按 H 必须录成 <c>capslock+h</c>，
    /// 不按 CapsLock 时照常录成 <c>h</c>。钩子那一侧（放行 CapsLock / 结束后收灯）只能用真按键验。
    /// </summary>
    private static void RecordingCapsLock(Window dialog, PresentationSource source)
    {
        var begin = typeof(HotkeyEditDialog).GetMethod("BeginCapture", Any)!;
        var onKey = typeof(HotkeyEditDialog).GetMethod("OnChordKeyDown", Any)!;
        var box = (TextBox)Field(dialog, "ChordBox")!;

        begin.Invoke(dialog, null);
        Check("录入开始：提示按键", box.Text.StartsWith("请按下", StringComparison.Ordinal), box.Text);

        onKey.Invoke(dialog, new object[] { box, KeyArgs(Key.CapsLock, source) });
        Check("按 CapsLock：预览带前缀", box.Text.StartsWith("CapsLock", StringComparison.Ordinal), box.Text);

        onKey.Invoke(dialog, new object[] { box, KeyArgs(Key.H, source) });
        string chord = (string)Field(dialog, "_chord")!;
        Check("CapsLock + H 录成 capslock+h", chord == "capslock+h", chord);
        Check("录入结束：显示成 CapsLock+H", box.Text == "CapsLock+H", box.Text);

        begin.Invoke(dialog, null);
        onKey.Invoke(dialog, new object[] { box, KeyArgs(Key.H, source) });
        chord = (string)Field(dialog, "_chord")!;
        Check("不按 CapsLock：照常录成 h", chord == "h", chord);
    }

    private static KeyEventArgs KeyArgs(Key key, PresentationSource source) =>
        new(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };

    private static KeyEventArgs KeyArgs(Key key) => new(Keyboard.PrimaryDevice, null!, 0, key)
    {
        RoutedEvent = Keyboard.PreviewKeyDownEvent,
    };

    private static HotkeyFile Fixture()
    {
        var file = new HotkeyFile();
        file.Groups.Add(new HotkeyGroup
        {
            Id = "g1",
            Name = "只在 Chrome",
            Scope = HotkeyScope.whitelist,
            Filter = new AppFilter { Processes = { "chrome.exe" } },
            Bindings =
            {
                new HotkeyBinding { Id = "b1", Chord = "capslock+h", ActionId = "common.capture" },
            },
        });
        file.Groups.Add(new HotkeyGroup
        {
            Id = "g2",
            Name = "不限制",
            Bindings =
            {
                new HotkeyBinding { Id = "b2", Chord = "f9", ActionId = "common.capture" },
            },
        });
        file.Normalize();
        return file;
    }

    private static void LoadRegistry(HotkeyFile file)
    {
        var assembly = typeof(HotkeyFile).Assembly;
        var registry = assembly.GetType("LTools.Core.HotkeyRegistry", true)!;
        registry.GetMethod("Load", new[] { typeof(HotkeyFile) })!.Invoke(null, new object[] { file });
    }

    private static ContentControl BuildRow(object row, DataTemplate template)
    {
        var host = new ContentControl { Content = row, ContentTemplate = template };
        var root = new Grid();
        root.Children.Add(host);
        root.Measure(new Size(340, 160));
        root.Arrange(new Rect(0, 0, 340, 160));
        root.UpdateLayout();
        return host;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static object? Field(object instance, string name) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance);

    private static void Check(string what, bool ok, string detail)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("PASS  " + what);
            return;
        }
        _fail++;
        Console.WriteLine("FAIL  " + what + "  —— " + detail);
    }
}
