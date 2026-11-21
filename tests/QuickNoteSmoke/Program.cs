using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

/// <summary>
/// 速记 Markdown 编辑器技术冒烟：真窗口、真渲染、真落盘。
/// </summary>
/// <remarks>
/// 只断言「看得见的行为」：正文换成了 AvalonEdit、Markdown 高亮定义加载成功并按主题上色、
/// 空正文的占位文字会显隐、改了正文会标脏、Ctrl+S 那条保存路径仍写出完整正文；
/// 最后渲染一张 png 并逐像素确认代码色与链接色真的画出来了。
/// 中文输入法的组字手感自动化替不了，仍需真机人工点一遍。
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static int _pass, _fail;
    private static string _out = "";

    private const string Sample = """
        # 速记改造草稿

        把速记改成 **Markdown** 之后，长笔记应该更好读：

        - 列表要能 *续写*
        - 行内代码 `dotnet build -p:NoWin32Manifest=true`
        - 链接 [OpenCode](https://opencode.ai)
        - 待办 [ ] 验证中文输入法

        > 引用块：粘贴图片走 `笔记名.files/`，不要写绝对路径。

        ```csharp
        Console.WriteLine("围栏代码块");
        ```

        | 阶段 | 成本 |
        | --- | --- |
        | P1 存储 | 0.5 天 |
        """;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        _out = Path.Combine(Path.GetTempPath(), "opencode", "quicknote-smoke", "out");
        Directory.CreateDirectory(_out);

        try { Run(); }
        catch (Exception ex) { Check(false, "未抛异常", ex.ToString()); }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}（截图：{Path.Combine(_out, "editor.png")}）");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run()
    {
        Assembly lt = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "LTools.dll"));
        Assembly edit = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "ICSharpCode.AvalonEdit.dll"));
        Type editorType = edit.GetType("ICSharpCode.AvalonEdit.TextEditor", true)!;
        Type repoType = lt.GetType("LTools.Features.NoteRepository", true)!;
        Type winType = lt.GetType("LTools.Views.QuickNoteWindow", true)!;

        string root = Path.Combine(_out, "notes");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        object repo = Activator.CreateInstance(repoType, Any, null, new object[] { root }, null)!;
        var win = (Window)Activator.CreateInstance(winType, Any, null, new[] { repo }, null)!;
        win.Width = 980;
        win.Height = 620;
        win.Show();
        Flush(win);

        object body = Field(win, "BodyBox");
        object placeholder = Field(win, "BodyPlaceholder");
        object title = Field(win, "TitleBox");
        object status = Field(win, "StatusBar");
        object textArea = Prop(body, "TextArea")!;

        Check(body.GetType() == editorType, "正文是 AvalonEdit TextEditor（不再是 TextBox）");
        Check(Prop(body, "WordWrap") is true, "长行仍然自动折行（WordWrap=True，和原来的 TextBox 一致）");
        var menu = Prop(body, "ContextMenu") as System.Windows.Controls.ContextMenu;
        var menuItems = menu?.Items.OfType<System.Windows.Controls.MenuItem>().ToList() ?? new();
        Check(menuItems.Count == 6, "正文有右键菜单（撤销/重做/剪切/复制/粘贴/全选，实际 " + menuItems.Count + " 项）");
        Check(menuItems.All(m => m.Command != null), "右键菜单每一项都挂上了命令");
        Check(PlaceholderVisible(placeholder), "空正文时占位文字可见");

        object? syntax = Prop(body, "SyntaxHighlighting");
        Check(syntax != null, "Markdown 高亮定义已加载");
        Check(Prop(syntax, "Name") as string == "Markdown", "高亮定义名为 Markdown");
        object? codeBlock = syntax?.GetType().GetMethod("GetNamedColor", new[] { typeof(string) })
            ?.Invoke(syntax, new object[] { "CodeBlock" });
        Check(codeBlock != null, "高亮定义里有 CodeBlock");
        Check(Prop(codeBlock, "Background") != null, "CodeBlock 带背景色（围栏块能看出来）");

        int transformers = (int)Prop(Prop(Prop(textArea, "TextView"), "LineTransformers"), "Count")!;
        Check(transformers > 0, $"高亮器挂到了正文视图上（LineTransformers={transformers}）");

        var selection = Prop(textArea, "SelectionBrush") as SolidColorBrush;
        Check(selection?.Color == Color.FromRgb(0xD4, 0xD4, 0xD8),
            $"选区用主题的 EditorSelectionColor（实际 {selection?.Color}）");

        PropSet(title, "Text", "冒烟");
        PropSet(body, "Text", Sample);
        Flush(win);

        Check(!PlaceholderVisible(placeholder), "有正文后占位文字隐藏");
        Check((Prop(status, "Text") as string ?? "").Contains("未保存"), "改正文会标脏（状态栏提示未保存）");

        // 高亮「真的落在对的字符上」：围栏块要跨行、不能漏到下一段；粗体要带 FontWeight。
        // 直接拿同一个定义对整篇文本跑一遍（TextEditor 内部也是 DocumentHighlighter）。
        var doc = (ICSharpCode.AvalonEdit.Document.TextDocument)Prop(body, "Document")!;
        var highlighter = new ICSharpCode.AvalonEdit.Highlighting.DocumentHighlighter(
            doc, (ICSharpCode.AvalonEdit.Highlighting.IHighlightingDefinition)syntax!);
        string[] lines = Normalize(doc.Text).Split('\n');
        int LineOf(string needle)
        {
            int index = Array.FindIndex(lines, l => l.Contains(needle, StringComparison.Ordinal));
            return index + 1; // 1-based
        }

        var insideFence = highlighter.HighlightLine(LineOf("围栏代码块"));
        Check(insideFence.Sections.Any(s => s.Color.Name == "CodeBlock" && s.Color.Background != null),
            "围栏代码块的内容行上了 CodeBlock 色（multiline span 真的跨行）");
        var afterFence = highlighter.HighlightLine(LineOf("P1 存储"));
        Check(!afterFence.Sections.Any(s => s.Color.Name == "CodeBlock"),
            "围栏块在收尾 ``` 处正确闭合（没漏到表格）");
        var strong = highlighter.HighlightLine(LineOf("Markdown"));
        Check(strong.Sections.Any(s => s.Color.Name == "Strong" && s.Color.FontWeight == FontWeights.Bold),
            "**粗体** 命中 Strong 且带 FontWeight=Bold");

        // 正文有焦点时 Ctrl+Del 必须是「删到词尾」，不能被窗口当成「删除这条速记」。
        // 焦点只在窗口激活时才设得进去（无头 / 非前台时 Focus() 会静默失败），先激活。
        MethodInfo caretFocused = win.GetType().GetMethod("IsTextCaretFocused", Any)!;
        win.Activate();
        Flush(win);
        body.GetType().GetMethod("Focus", Type.EmptyTypes)!.Invoke(body, null);
        Flush(win);
        Check((bool)caretFocused.Invoke(win, null)!, "焦点在正文时 IsTextCaretFocused = true（Ctrl+Del 留给编辑）");
        var search = (UIElement)Field(win, "SearchBox");
        Keyboard.Focus(search);
        Flush(win);
        Check((bool)caretFocused.Invoke(win, null)!, "焦点在搜索框时也是 true（那是文本框，Ctrl+Del 同样留给编辑）");
        var list = (UIElement)Field(win, "NoteList");
        Keyboard.Focus(list);
        Flush(win);
        Check(!(bool)caretFocused.Invoke(win, null)!,
            $"焦点在列表时 IsTextCaretFocused = false（Ctrl+Del 删速记；实际焦点 {Keyboard.FocusedElement?.GetType().Name}）");

        BitmapSource shot = Render(win);
        string png = Path.Combine(_out, "editor.png");
        Save(shot, png);
        Save(Render(win, 2), Path.Combine(_out, "editor-2x.png"));

        int code = CountNear(shot, Color.FromRgb(0xB4, 0x53, 0x09), 40);
        int link = CountNear(shot, Color.FromRgb(0x3B, 0x6E, 0xA5), 40);
        Check(code > 20, $"代码色真的画出来了（{code} 像素）");
        Check(link > 10, $"链接色真的画出来了（{link} 像素）");

        Invoke(win, "SaveCurrent");
        Flush(win);

        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Check(files.Length == 1, $"保存后落盘一个文件（实际 {files.Length} 个）");
        if (files.Length == 1)
        {
            Check(files[0].EndsWith(".md", StringComparison.OrdinalIgnoreCase),
                "落盘是 .md（P1 之后重写扩展名）");
            Check(Normalize(File.ReadAllText(files[0])) == Normalize(Sample),
                "落盘正文与编辑器里的 Markdown 一字不差");
        }

        // 空态：新建后占位文字回来，截图留着人眼看一眼位置对不对。
        Invoke(win, "NewNote");
        Flush(win);
        Check(PlaceholderVisible(placeholder), "新建后占位文字重新出现");
        Save(Render(win), Path.Combine(_out, "empty.png"));

        win.Close();
        Flush(win);
    }

    private static bool PlaceholderVisible(object placeholder)
        => (Visibility)Prop(placeholder, "Visibility")! == Visibility.Visible;

    /// <summary>编辑器 / 文件两边的换行可能不同（AvalonEdit 与 File.WriteAllText 各自归一），比较前统一。</summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static BitmapSource Render(Window win, int scale = 1)
    {
        win.UpdateLayout();
        double dpi = 96.0 * scale;
        int w = Math.Max(1, (int)(win.ActualWidth * scale));
        int h = Math.Max(1, (int)(win.ActualHeight * scale));
        var bmp = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        bmp.Render(win);
        bmp.Freeze();
        return bmp;
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static int CountNear(BitmapSource bmp, Color target, int tolerance)
    {
        int stride = bmp.PixelWidth * 4;
        var buffer = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buffer, stride, 0);
        int hits = 0;
        for (int i = 0; i < buffer.Length; i += 4)
        {
            if (Math.Abs(buffer[i + 2] - target.R) <= tolerance
                && Math.Abs(buffer[i + 1] - target.G) <= tolerance
                && Math.Abs(buffer[i] - target.B) <= tolerance)
                hits++;
        }
        return hits;
    }

    private static void Flush(Window win)
        => win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static object Field(object target, string name)
        => target.GetType().GetField(name, Any)?.GetValue(target)
           ?? throw new InvalidOperationException($"找不到字段 {name}");

    private static object? Prop(object? target, string name)
        => target?.GetType().GetProperty(name, Any)?.GetValue(target);

    private static void PropSet(object target, string name, object value)
        => target.GetType().GetProperty(name, Any)!.SetValue(target, value);

    private static void Invoke(object target, string name)
        => target.GetType().GetMethod(name, Any)!.Invoke(target, null);

    private static void Check(bool ok, string what)
    {
        if (ok) { _pass++; Console.WriteLine($"  ok  {what}"); }
        else { _fail++; Console.WriteLine($"FAIL  {what}"); }
    }

    private static void Check(bool ok, string what, string detail)
    {
        Check(ok, what);
        if (!ok) Console.WriteLine(detail);
    }
}
