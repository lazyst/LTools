using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core.Share;

namespace ShareSmoke;

/// <summary>
/// 共享窗口的界面冒烟：真开窗口、真点按钮、真走拖放 / 粘贴。
/// </summary>
/// <remarks>
/// 产品窗口要 STA + <c>Application</c> + 主题字典，而 <c>Program.cs</c> 是 top-level 语句（MTA），
/// 所以这里自己起一条 STA 线程跑完就退出。断言盯的是「用户看得见的结果」：输入框结构、
/// 回车发送、拖进来的文字 / 文件、剪贴板按钮、单实例。<c>ShareStore</c> / <c>ShareItem</c> 是
/// internal，照本项目冒烟的既定做法用反射读。
/// </remarks>
internal static class UiSmoke
{
    private static readonly Type StoreType =
        typeof(ShareOptions).Assembly.GetType("LTools.Core.Share.ShareStore", true)!;

    private static readonly Type ItemType =
        typeof(ShareOptions).Assembly.GetType("LTools.Core.Share.ShareItem", true)!;

    private static Window _window = null!;

    public static void Run(Action<bool, string> check, string tempRoot, bool explorerDrag = false)
    {
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try { RunCore(check, tempRoot, explorerDrag); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            Console.WriteLine(error);
            check(false, "界面冒烟未抛异常：" + error.Message);
        }
    }

    private static void RunCore(Action<bool, string> check, string tempRoot, bool explorerDrag)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        LTools.Views.ShareWindow.ShowOrActivate();
        _window = Application.Current.Windows.OfType<LTools.Views.ShareWindow>().Single();
        Flush();

        var box = (TextBox)Field(_window, "ComposeBox");
        var send = (Button)Field(_window, "SendBtn");
        var clip = (Button)Field(_window, "ClipboardBtn");
        var plus = (Button)Field(_window, "PlusBtn");
        var host = (Border)Field(_window, "ComposeHost");
        var outList = (ItemsControl)Field(_window, "OutList");

        Console.WriteLine("== 界面：拖放通道 ==");
        // 共享窗口只活在普通权限宿主里（asInvoker），所以 OLE 拖放无条件开着；
        // 那层替提权窗口兜底的 WM_DROPFILES 桥已经删了，不再有「提权时关掉 OLE」这条分支。
        check(_window.AllowDrop, "窗口注册了 OLE 拖放（AllowDrop=True）");

        // 输入框在左栏里（右边缘不超过右栏），不再横跨整个窗口。
        var side = (Border)Field(_window, "SidePanel");
        double hostRight = host.TransformToAncestor(_window).Transform(new Point(host.ActualWidth, 0)).X;
        double sideLeft = side.TransformToAncestor(_window).Transform(new Point(0, 0)).X;
        check(hostRight <= sideLeft + 1, $"输入框只占左栏（右缘 {hostRight:0} ≤ 右栏 {sideLeft:0}）");

        // 端口 / 上传上限是就地可编辑的输入框（不再弹 InputDialog）
        var portBox = (TextBox)Field(_window, "PortBox");
        var uploadBox = (TextBox)Field(_window, "UploadBox");
        check(int.TryParse(portBox.Text, out int portInBox) && portInBox is >= 1024 and <= 65535,
            "端口是输入框，就地显示当前端口（" + portBox.Text + "）");
        check(uploadBox.Text.Length > 0 && uploadBox.Text.All(char.IsDigit), "上传上限是输入框，显示当前值");

        // 填错就地退回原值，不弹对话框
        portBox.Text = "123";
        Invoke(_window, "CommitPort", false);
        Flush();
        check(portBox.Text == portInBox.ToString(), "端口填错就地退回原值（不弹窗）");

        // 「接收」三档：下拉三项、跟着配置、换档写回运行配置（锚点指到临时目录，别碰仓库的 configs/）。
        var inboundCombo = (ComboBox)Field(_window, "InboundCombo");
        var inboundHint = (TextBlock)Field(_window, "InboundHint");
        List<string> inboundLabels = inboundCombo.Items.Cast<object>()
            .Select(i => (string)i.GetType().GetProperty("Value")!.GetValue(i)!)
            .ToList();
        check(inboundLabels.SequenceEqual(new[] { "自动接收", "需要确认", "不接受" }),
            "「接收」下拉是 自动接收 / 需要确认 / 不接受（实际：" + string.Join(" / ", inboundLabels) + "）");
        check(Equals(inboundCombo.SelectedValue, ShareInbound.allow), "默认选中「自动接收」");
        check(inboundHint.Text.Length > 0, "下拉下面有一行说明：" + inboundHint.Text);

        Type configStoreType = typeof(ShareOptions).Assembly.GetType("LTools.Features.ConfigStore", true)!;
        MethodInfo usePathOnly = configStoreType.GetMethod("UsePathOnly", BindingFlags.NonPublic | BindingFlags.Static)!;
        PropertyInfo serverOptions = typeof(ShareOptions).Assembly
            .GetType("LTools.Core.Share.ShareServer", true)!
            .GetProperty("Options", BindingFlags.Public | BindingFlags.Static)!;

        usePathOnly.Invoke(null, new object[] { Path.Combine(tempRoot, "ltools.json") });
        inboundCombo.SelectedValue = ShareInbound.confirm;
        Flush();
        check(((ShareOptions)serverOptions.GetValue(null)!).Inbound == ShareInbound.confirm,
            "换到「需要确认」写进运行配置");
        check(inboundHint.Text.Contains("弹框", StringComparison.Ordinal),
            "说明跟着换成需确认：" + inboundHint.Text);

        inboundCombo.SelectedValue = ShareInbound.allow;
        Flush();
        usePathOnly.Invoke(null, new object[] { "" });
        check(((ShareOptions)serverOptions.GetValue(null)!).Inbound == ShareInbound.allow, "换回「自动接收」");

        // 所有网卡的地址都列出来，每条带「复制」
        var urlList = (ItemsControl)Field(_window, "UrlList");
        var urlRows = ((IEnumerable)urlList.ItemsSource!).Cast<object>().ToList();
        check(urlRows.Count > 0, $"共享地址列出了 {urlRows.Count} 个网卡地址");
        string firstUrl = (string)urlRows[0].GetType().GetProperty("Url")!.GetValue(urlRows[0])!;
        string firstFull = (string)urlRows[0].GetType().GetProperty("Full")!.GetValue(urlRows[0])!;
        check(firstUrl.StartsWith("http://") && firstFull.StartsWith(firstUrl),
            "地址是可打开的完整 URL：" + firstFull);

        var urlTemplate = (DataTemplate)urlList.ItemTemplate;
        var urlContent = (FrameworkElement)urlTemplate.LoadContent();
        List<Button> urlButtons = LogicalDescendants(urlContent).OfType<Button>().ToList();
        check(urlButtons.Count == 1 && (urlButtons[0].Content as string) == "复制", "每条地址后面有「复制」按钮");

        // 二维码可切换：默认第一条，点二维码换下一条，点地址行回到那一条
        var qrHint = (TextBlock)Field(_window, "QrHint");
        var qrImage = (Image)Field(_window, "QrImage");

        if (urlRows.Count > 1)
        {
            check(IsSelected(urlRows[0]) && !IsSelected(urlRows[1]), "二维码默认用第一条地址（行上是选中态）");
            check(qrImage.Source != null, "二维码已经画出来");
            check(qrHint.Text.Contains("1/" + urlRows.Count, StringComparison.Ordinal),
                "二维码下面写着「第 1/N 条」：" + qrHint.Text);

            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = Mouse.MouseDownEvent,
            };
            Invoke(_window, "Qr_Click", Field(_window, "QrBorder"), mouse);
            Flush();

            List<object> afterQr = ((IEnumerable)urlList.ItemsSource!).Cast<object>().ToList();
            check(IsSelected(afterQr[1]) && !IsSelected(afterQr[0]), "点二维码换到下一条地址");
            check(qrImage.Source != null && qrHint.Text.Contains("2/" + afterQr.Count, StringComparison.Ordinal),
                "换过之后二维码重画、提示改成「第 2/N 条」：" + qrHint.Text);

            var rowBorder = new Border { Tag = 0 };
            Invoke(_window, "UrlRow_Click", rowBorder, mouse);
            Flush();

            List<object> afterRow = ((IEnumerable)urlList.ItemsSource!).Cast<object>().ToList();
            check(IsSelected(afterRow[0]), "点第一条地址行，二维码切回第一条");
        }

        Console.WriteLine("== 界面：输入框 ==");
        check(box.AcceptsReturn && box.TextWrapping == TextWrapping.Wrap, "输入框是多行、自动换行");
        check(host.ActualHeight >= 40, $"输入框有正常高度（{host.ActualHeight:0}），不是被压成一条线");
        check(IsInside(clip, host) && IsInside(plus, host), "剪贴板与加号两个按钮在输入框里面");
        check(!send.IsEnabled, "空输入时「发送」置灰");

        box.Text = "第一句";
        Flush();
        check(send.IsEnabled, "有文字后「发送」可用");

        // 字形必须落在 TextBlock 的局部值上（全局 TextBlock 样式会把 FontFamily 钉成 UiFont）
        check(IconFont(clip) == "Segoe MDL2 Assets", "剪贴板按钮是 MDL2 字形（不是空方框）");
        check(IconFont(plus) == "Segoe MDL2 Assets", "加号按钮是 MDL2 字形");

        List<MenuItem> menu = box.ContextMenu?.Items.OfType<MenuItem>().ToList() ?? new List<MenuItem>();
        check(menu.Select(m => (string)m.Header).SequenceEqual(new[] { "剪切", "复制", "粘贴", "全选" }),
            "输入框右键菜单是剪切 / 复制 / 粘贴 / 全选");

        Console.WriteLine("== 界面：发送 ==");
        int before = Items().Count;
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Flush();

        List<object> items = Items();
        check(items.Count == before + 1, "点「发送」把输入框的文字收进共享区");
        check(Str(items[^1], "Name").StartsWith("文字 ", StringComparison.Ordinal), "文字条目叫「文字 <时间>.txt」");
        check(File.ReadAllText(Str(items[^1], "Path")) == "第一句", "发出去的文字原样落盘");
        check(Str(items[^1], "Mime").StartsWith("text/", StringComparison.Ordinal), "文字条目的类型是 text/*");
        check(box.Text.Length == 0, "发送后输入框清空");
        check(RowCount(outList) == items.Count, "列表跟着刷新");

        // 文字走正文卡片（方案 B），文件还是单行；行内是「复制 / 打开文件」这类文字按钮。
        var inList = (ItemsControl)Field(_window, "InList");
        var selector = (DataTemplateSelector)outList.ItemTemplateSelector;
        var inboxRows = ((IEnumerable)inList.ItemsSource!).Cast<object>().ToList();

        object textRow = inboxRows.First(r => IsTextRow(r));
        object fileRow = inboxRows.First(r => !IsTextRow(r));

        DataTemplate textTemplate = selector.SelectTemplate(textRow, inList);
        DataTemplate plainTemplate = selector.SelectTemplate(fileRow, inList);
        check(!ReferenceEquals(textTemplate, plainTemplate), "文字条目用正文卡片模板，文件条目还是单行");

        string body = (string)textRow.GetType().GetProperty("Body")!.GetValue(textRow)!;
        string meta = (string)textRow.GetType().GetProperty("Meta")!.GetValue(textRow)!;
        check(body.Length > 0, "文字条目带正文预览（" + body.Length + " 字）");
        check(meta.StartsWith("文字 · ", StringComparison.Ordinal), "文字条目的摘要行是「文字 · 时间 · 大小」");

        // 绑定只在真渲染出来的行上求值（DataTemplate.LoadContent 出来的树不跑绑定）。
        inList.UpdateLayout();

        List<string> RowButtonsInList(object row)
        {
            var container = inList.ItemContainerGenerator.ContainerFromItem(row) as DependencyObject;
            check(container != null, "行已生成：" + row.GetType().GetProperty("Name")!.GetValue(row));
            return container == null
                ? new List<string>()
                : Descendants(container).OfType<Button>().Select(b => b.Content as string ?? "").ToList();
        }

        List<string> textButtons = RowButtonsInList(textRow);
        string actualTextButtons = string.Join(" / ", textButtons);
        check(textButtons.SequenceEqual(new[] { "复制", "查看", "另存为", "✕" }),
            "文字行的按钮是 复制 / 查看 / 另存为 / ✕（实际：" + actualTextButtons + "）");

        List<string> fileButtons = RowButtonsInList(fileRow);
        string actualFileButtons = string.Join(" / ", fileButtons);
        check(fileButtons.FirstOrDefault() == "打开文件",
            "文件行的第一个按钮是「打开文件」（实际：" + actualFileButtons + "）");

        // 点「复制」= 文字进剪贴板
        var copyButton = new Button { CommandParameter = textRow.GetType().GetProperty("Key")!.GetValue(textRow) };
        Invoke(_window, "RowAction1_Click", copyButton, new RoutedEventArgs());
        Flush();
        check(Clipboard.ContainsText() && Clipboard.GetText() == "手机发来的", "文字行「复制」把正文放进剪贴板");

        box.Text = "回车发送";
        var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window)!,
            Environment.TickCount, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        box.RaiseEvent(enter);
        Flush();
        check(enter.Handled, "回车被输入框吃掉（不会变成换行）");
        check(Items().Count == before + 2, "回车也能发送");

        Console.WriteLine("== 界面：拖放 ==");
        var textDrop = new DataObject();
        textDrop.SetData(DataFormats.UnicodeText, "拖进来的文字");
        DragEventArgs textArgs = Drop(textDrop);

        Invoke(_window, "Window_PreviewDragOver", _window, textArgs);
        check(textArgs.Effects == DragDropEffects.Copy, "拖文字进来时显示「可以放」");
        check(textArgs.Handled, "拖放事件不会被文本框再处理一遍");

        Invoke(_window, "Window_PreviewDrop", _window, textArgs);
        Flush();
        check(box.Text == "拖进来的文字", "拖进来的文字落在输入框里（改完再回车）");

        string dropped = Path.Combine(tempRoot, "拖进来的文件.txt");
        File.WriteAllText(dropped, "drop-me");
        var fileDrop = new DataObject();
        fileDrop.SetData(DataFormats.FileDrop, new[] { dropped });
        DragEventArgs fileArgs = Drop(fileDrop);

        Invoke(_window, "Window_PreviewDragOver", _window, fileArgs);
        Invoke(_window, "Window_PreviewDrop", _window, fileArgs);
        Flush();

        object? added = Items().FirstOrDefault(i => Str(i, "Name") == "拖进来的文件.txt");
        check(added != null, "拖进来的文件进了共享区");
        check(added != null && !Bool(added, "Copied") && Bool(added, "Exists"), "拖进来的文件是原地记路径（不复制）");

        DragEventArgs emptyArgs = Drop(new DataObject());
        Invoke(_window, "Window_PreviewDragOver", _window, emptyArgs);
        check(emptyArgs.Effects == DragDropEffects.None, "拖不认识的东西进来时不接受");

        // 同一个文件再拖一次：面板 / 共享区都按路径去重（不再走 WM_DROPFILES 那条老路）。
        int beforeRepeat = Items().Count;
        DragEventArgs repeatArgs = Drop(fileDrop);
        Invoke(_window, "Window_PreviewDragOver", _window, repeatArgs);
        Invoke(_window, "Window_PreviewDrop", _window, repeatArgs);
        Flush();
        check(Items().Count == beforeRepeat, "同一个文件再拖一次不会多出一条");

        Console.WriteLine("== 界面：收到的文字 ==");
        object? inboxText = ((IEnumerable)StoreType.GetMethod("Inbox")!.Invoke(Store, null)!)
            .Cast<object>()
            .FirstOrDefault(f => ((string)f.GetType().GetProperty("Name")!.GetValue(f)!)
                .EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        check(inboxText != null, "「收到的」里有手机发来的文字");
        if (inboxText != null)
        {
            string inboxName = (string)inboxText.GetType().GetProperty("Name")!.GetValue(inboxText)!;
            Invoke(_window, "ViewKey", "inbox:" + inboxName);
            Flush();

            Window? viewer = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType().Name == "TextViewWindow");
            check(viewer != null, "双击收到的文字能打开只读文本窗口");
            viewer?.Close();
            Flush();
        }

        Console.WriteLine("== 界面：剪贴板按钮 ==");
        string? oldText = null;
        try { if (Clipboard.ContainsText()) oldText = Clipboard.GetText(); } catch { /* 被占用 */ }

        try
        {
            Clipboard.SetText("剪贴板里的文字");
            int count = Items().Count;
            clip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Flush();

            List<object> pasted = Items();
            check(pasted.Count == count + 1, "剪贴板按钮把文字收进共享区");
            check(pasted[^1] is { } last
                  && Str(last, "Name").StartsWith("文字 ", StringComparison.Ordinal)
                  && File.ReadAllText(Str(last, "Path")) == "剪贴板里的文字",
                "剪贴板文字内容对得上");
        }
        catch (Exception ex)
        {
            check(false, "剪贴板用例跑不通：" + ex.Message);
        }
        finally
        {
            try { if (oldText != null) Clipboard.SetText(oldText); else Clipboard.Clear(); }
            catch { /* 被占用 */ }
        }

        Console.WriteLine("== 界面：时间 / 收到的目录 / 批量管理 ==");

        var inboxList = (ItemsControl)Field(_window, "InList");
        var when = new Regex(@"· (\d{2}:\d{2}|\d{2}-\d{2} \d{2}:\d{2}) ·");
        List<object> Rows(ItemsControl list) => ((IEnumerable)list.ItemsSource!).Cast<object>().ToList();
        string Prop(object row, string name) => row.GetType().GetProperty(name)!.GetValue(row)!.ToString()!;
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Visibility Vis(object row, string name) =>
            (Visibility)row.GetType().GetProperty(name)!.GetValue(row)!;

        // 危险动作会弹确认框：让计时器在模态循环里替我们点「确定」（同 CaptureOverlaySmoke 的做法）。
        DispatcherTimer? answer = null;
        void AutoConfirm()
        {
            answer?.Stop();
            answer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
            {
                Window? dialog = Application.Current.Windows.OfType<Window>()
                    .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                if (dialog?.FindName("OkBtn") is not Button ok) return;

                answer!.Stop();
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }, Dispatcher.CurrentDispatcher);
            answer.Start();
        }

        // 每一行都带时间：今天 HH:mm，跨天补 MM-dd。
        check(Rows(outList).Count > 0 && Rows(outList).All(r => when.IsMatch(Prop(r, "Meta"))),
            "发出去的每一行都有时间");
        check(Rows(inboxList).Count > 0 && Rows(inboxList).All(r => when.IsMatch(Prop(r, "Meta"))),
            "收到的每一行都有时间：" + string.Join(" / ", Rows(inboxList).Select(r => Prop(r, "Meta"))));

        // 收到的目录：默认显示 share/inbox，并写明这个目录里的文件对方都能下载。
        check(((TextBlock)Field(_window, "InboxPathText")).Text == "share/inbox", "「收到的目录」默认是 share/inbox");
        check(((TextBlock)Field(_window, "InboxDirHint")).Text.Contains("都能下载", StringComparison.Ordinal),
            "目录下面写明那个目录里的文件对方都能下载");

        // 换到自定义目录：标签显示完整路径、提示改口；再回默认（后面的批量用例按默认目录走）。
        Type serverType = typeof(ShareOptions).Assembly.GetType("LTools.Core.Share.ShareServer", true)!;
        ShareOptions now = (ShareOptions)serverType.GetProperty("Options",
            BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        ShareOptions With(string inbox) => new()
        {
            Port = now.Port,
            Running = now.Running,
            Inbound = now.Inbound,
            InboxDir = inbox,
            MaxUploadMb = now.MaxUploadMb,
            RequireToken = now.RequireToken,
            Token = now.Token,
        };

        string customInbox = Path.Combine(tempRoot, "ui-inbox");
        Invoke(_window, "ApplyOptions", With(customInbox));
        Flush();
        check(((TextBlock)Field(_window, "InboxPathText")).Text == customInbox,
            "自定义目录在界面上显示完整路径");
        check(((TextBlock)Field(_window, "InboxDirHint")).Text.StartsWith("自定义目录", StringComparison.Ordinal),
            "换过目录后提示改口");

        Invoke(_window, "ApplyOptions", With(""));
        Flush();
        check(((TextBlock)Field(_window, "InboxPathText")).Text == "share/inbox", "回到默认目录");

        check(ServerInboxDir() == Path.Combine(tempRoot, "inbox"), "服务端也回到默认目录");

        // 造两条一次性内容，验批量移除（真点按钮 + 自动点确认框）
        MethodInfo addPath = StoreType.GetMethod("AddPath")!;
        string bulkA = Path.Combine(tempRoot, "批量-A.txt");
        string bulkB = Path.Combine(tempRoot, "批量-B.txt");
        File.WriteAllText(bulkA, "a");
        File.WriteAllText(bulkB, "b");
        addPath.Invoke(Store, new object[] { bulkA, false });
        addPath.Invoke(Store, new object[] { bulkB, false });

        string inboxDir = (string)StoreType.GetProperty("InboxDir")!.GetValue(Store)!;
        string bulkIn = Path.Combine(inboxDir, "批量收到.txt");
        File.WriteAllText(bulkIn, "收到的");
        Invoke(_window, "Refresh");
        Flush();

        int beforeOut = RowCount(outList);
        Click((Button)Field(_window, "OutBatchBtn"));
        Flush();
        check((string)((Button)Field(_window, "OutBatchBtn")).Content! == "完成", "「批量管理」进去后变「完成」");
        check(Vis(Rows(outList)[0], "PickVisibility") == Visibility.Visible, "每行露出勾选框");
        check(((Button)Field(_window, "OutAllBtn")).Visibility == Visibility.Visible
              && ((Button)Field(_window, "OutDeleteBtn")).Visibility == Visibility.Visible,
            "露出「全选」与「删除选中」");

        Click((Button)Field(_window, "OutAllBtn"));
        Flush();
        check(Rows(outList).All(IsSelected), "全选把每一行都勾上");

        // 批量管理态截一张，人工核对三列（勾选框 / 名称 / 按钮）没挤在一起。
        try
        {
            string batchPng = Path.Combine(Path.GetTempPath(), "opencode", "share-smoke", "batch.png");
            Directory.CreateDirectory(Path.GetDirectoryName(batchPng)!);
            Save(Render(), batchPng);
            Console.WriteLine("  截图：" + batchPng);
        }
        catch (Exception ex)
        {
            check(false, "批量管理态能渲染成 png：" + ex.Message);
        }
        check(((string)((Button)Field(_window, "OutDeleteBtn")).Content!)
                .Contains("(" + RowCount(outList) + ")", StringComparison.Ordinal),
            "「删除选中」带上数量");

        Click((Button)Field(_window, "OutAllBtn"));
        Flush();
        check(Rows(outList).All(r => !IsSelected(r)), "再点一次 = 取消全选");

        object rowA = Rows(outList).First(r => Prop(r, "Name") == "批量-A.txt");
        Invoke(_window, "ToggleRowPick", rowA);
        Flush();
        check(IsSelected(rowA), "点整行就勾上");
        check(((string)((Button)Field(_window, "OutDeleteBtn")).Content!).Contains("(1)", StringComparison.Ordinal),
            "计数跟着变成 1");

        // 1 秒轮询会重建列表：勾选按行键记着，重建后不该掉。
        Invoke(_window, "ReloadLists");
        Flush();
        check(Rows(outList).All(r => IsSelected(r) == (Prop(r, "Name") == "批量-A.txt")),
            "列表重建后勾选还在（没被刷新冲掉）");

        AutoConfirm();
        Click((Button)Field(_window, "OutDeleteBtn"));
        Flush();
        check(RowCount(outList) == beforeOut - 1, "批量删掉勾上的那一项");
        check(Rows(outList).All(r => Prop(r, "Name") != "批量-A.txt"), "被删的那条不在了");
        check(Rows(outList).Any(r => Prop(r, "Name") == "批量-B.txt"), "没勾的那条还在");
        check((string)((Button)Field(_window, "OutBatchBtn")).Content! == "批量管理", "删完自动退出批量管理");
        check(Items().All(i => Str(i, "Name") != "批量-A.txt"), "共享清单里也真的没了");

        int beforeIn = RowCount(inboxList);
        Click((Button)Field(_window, "InBatchBtn"));
        Flush();
        check((string)((Button)Field(_window, "InBatchBtn")).Content! == "完成", "收到的也能进批量管理");
        Click((Button)Field(_window, "InAllBtn"));
        Flush();
        check(Rows(inboxList).All(IsSelected), "收到的全选");
        Click((Button)Field(_window, "InAllBtn"));
        Flush();

        object rowIn = Rows(inboxList).First(r => Prop(r, "Name") == "批量收到.txt");
        Invoke(_window, "ToggleRowPick", rowIn);
        Flush();
        AutoConfirm();
        Click((Button)Field(_window, "InDeleteBtn"));
        Flush();
        check(RowCount(inboxList) == beforeIn - 1, "收到的批量删除");
        check(!File.Exists(bulkIn), "收到的文件真的从磁盘删掉");

        Console.WriteLine("== 界面：单实例 / 渲染 ==");
        LTools.Views.ShareWindow.ShowOrActivate();
        Flush();
        check(Application.Current.Windows.OfType<LTools.Views.ShareWindow>().Count() == 1,
            "再打开一次只是激活同一个窗口");

        // 右栏滚到底再截图：设置区（含「接收」那三档）要看得见。
        if (side.Child is ScrollViewer sideScroll) sideScroll.ScrollToEnd();
        Flush();
        check(inboundCombo.IsVisible && inboundCombo.ActualWidth > 0,
            $"「接收」下拉看得见（{inboundCombo.ActualWidth:0}×{inboundCombo.ActualHeight:0}）");

        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "opencode", "share-smoke");
            Directory.CreateDirectory(dir);
            string png = Path.Combine(dir, "window.png");
            Save(Render(), png);
            Console.WriteLine("  截图：" + png);
        }
        catch (Exception ex)
        {
            check(false, "窗口能渲染成 png：" + ex.Message);
        }

        Console.WriteLine("== 界面：真鼠标拖放 ==");
        try { DragSmoke.Run(check, tempRoot); }
        catch (Exception ex) { check(false, "真鼠标拖放跑不通：" + ex.Message); }
        Flush();

        if (explorerDrag)
        {
            Console.WriteLine("== 界面：从资源管理器拖文件 ==");
            try { DragSmoke.RunExplorer(check, tempRoot); }
            catch (Exception ex) { check(false, "资源管理器拖放跑不通：" + ex.Message); }
            Flush();
        }

        _window.Close();
        app.Shutdown();
    }

    // ============ 反射 ============

    private static object Store => StoreType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!
        .GetValue(null)!;

    private static List<object> Items() =>
        ((IEnumerable)StoreType.GetMethod("Items")!.Invoke(Store, null)!).Cast<object>().ToList();

    private static string ServerInboxDir() =>
        (string)StoreType.GetProperty("InboxDir")!.GetValue(Store)!;

    private static string Str(object item, string name) =>
        (string)ItemType.GetProperty(name)!.GetValue(item)!;

    private static bool Bool(object item, string name) =>
        (bool)ItemType.GetProperty(name)!.GetValue(item)!;

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?.GetValue(target)
        ?? throw new InvalidOperationException($"找不到字段 {name}");

    private static void Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(target, args);

    /// <summary><c>DragEventArgs</c> 只有 internal 构造函数，照冒烟既有做法反射建一个。</summary>
    private static DragEventArgs Drop(IDataObject data)
    {
        ConstructorInfo ctor = typeof(DragEventArgs)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .First(c => c.GetParameters().Length == 5);

        var args = (DragEventArgs)ctor.Invoke(new object[]
        {
            data, DragDropKeyStates.None, DragDropEffects.Copy, _window, new Point(20, 20),
        });

        // 给个非空 RoutedEvent，否则 set_Handled 会抛「每个 RoutedEventArgs 都必须有一个关联事件」。
        args.RoutedEvent = DragDrop.PreviewDragOverEvent;
        return args;
    }

    // ============ 视觉树 / 渲染 ============


    private static void Flush()
    {
        _window.UpdateLayout();
        _window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static bool IsTextRow(object row) =>
        (bool)row.GetType().GetProperty("IsText")!.GetValue(row)!;

    private static bool IsSelected(object row) =>
        (bool)row.GetType().GetProperty("Selected")!.GetValue(row)!;

    private static int RowCount(ItemsControl list) => list.ItemsSource is IEnumerable source
        ? source.Cast<object>().Count()
        : list.Items.Count;

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node != null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, ancestor)) return true;
        return false;
    }

    /// <summary>图标按钮里那个 TextBlock 的字体（字形只在 Button 上写 FontFamily 会被全局样式覆盖）。</summary>
    private static string IconFont(Button button)
    {
        button.ApplyTemplate();
        return Descendants(button).OfType<TextBlock>().FirstOrDefault()?.FontFamily.Source ?? "";
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>还没进可视树的模板内容：按逻辑树走（<c>VisualTreeHelper</c> 这时是空的）。</summary>
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            yield return node;
            foreach (DependencyObject nested in LogicalDescendants(node)) yield return nested;
        }
    }

    private static BitmapSource Render()
    {
        _window.UpdateLayout();
        int width = Math.Max(1, (int)_window.ActualWidth);
        int height = Math.Max(1, (int)_window.ActualHeight);

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
