using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Share;
using LTools.Features;
using LTools.Features.Units.Screen;

namespace LTools.Views;

/// <summary>
/// 局域网共享的管理窗口（布局 A：左内容流、右二维码 / 地址 / 开关）。
/// </summary>
/// <remarks>
/// 关窗**不停止**共享；只有关程序或在这里点「停止」才停。列表靠 1 秒轮询，
/// 也接受资源管理器直接往 <c>share/files/</c> 放文件（清单是唯一来源，不放这里）。
/// </remarks>
public partial class ShareWindow : Window
{
    private readonly ShareStore _store = ShareStore.Default;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private string? _qrUrl;
    private int _qrIndex;
    private string _listSignature = "";
    private bool _closed;
    private bool _dragLogged;

    /// <summary>文字条目的正文预览（按 路径 + 修改时间 + 大小 缓存，1 秒轮询不会反复读盘）。</summary>
    private readonly Dictionary<string, (DateTime Stamp, long Size, string Text)> _previews = new();

    private DispatcherTimer? _hintTimer;

    private static ShareWindow? _open;

    public ShareWindow()
    {
        InitializeComponent();

        SendBtn.IsEnabled = false;
        ComposeBox.ContextMenu = BuildComposeMenu();
        PreviewKeyDown += Window_PreviewKeyDown;

        var rows = new ShareRowSelector
        {
            Plain = (DataTemplate)FindResource("ShareRowTemplate"),
            Text = (DataTemplate)FindResource("ShareTextRowTemplate"),
        };
        OutList.ItemTemplateSelector = rows;
        InList.ItemTemplateSelector = rows;

        // Explorer 拖文件走 WM_DROPFILES（提权进程也能收）；WPF 的 OLE 拖放被 UIPI 拦住。
        FileDropBridge.Attach(this, AddFilesCore);

        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            ShareServer.Changed += OnServerChanged;
            Refresh();
            _timer.Start();
            ComposeBox.Focus();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            ShareServer.Changed -= OnServerChanged;
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    /// <summary>打开或激活同一个窗口（内置功能 / 托盘都走这里）。</summary>
    /// <summary>
    /// 打开（或唤起）**本进程**里的共享窗口。
    /// </summary>
    /// <remarks>
    /// 只有两处会用它：共享助手（<c>ShareHost.exe</c>，普通权限）与冒烟。
    /// 主程序里别调它——主程序是提权的，在这里开的窗口收不到 Explorer 的拖放；
    /// 托盘 / 内置功能走 <see cref="ShareHelper.Open"/>（把助手拉起来 / 唤起）。
    /// </remarks>
    public static void ShowOrActivate()
    {
        if (_open is { IsLoaded: true })
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        _open = new ShareWindow();
        _open.Show();
    }

    private void OnServerChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Ui.Post(Refresh);
            return;
        }
        Refresh();
    }

    // ============ 刷新 ============

    private void Refresh()
    {
        if (_closed) return;

        ShareOptions options = ShareServer.Options;
        bool running = ShareServer.IsRunning;

        // 所有网卡的地址都列出来（服务绑的是 0.0.0.0，手机连哪块网卡都行）。
        IReadOnlyList<LanEndpoint> endpoints = ShareServer.Endpoints.Count > 0
            ? ShareServer.Endpoints
            : LanAddress.Candidates();
        LanEndpoint? endpoint = endpoints.FirstOrDefault();

        StatusDot.Fill = (Brush)FindResource(running ? "SuccessBrush" : "HintTextBrush");
        if (running)
        {
            StatusText.Text = endpoint != null
                ? $"共享中 · {endpoint.Address}:{options.Port}"
                : $"共享中 · 端口 {options.Port}";
        }
        else
        {
            StatusText.Text = string.IsNullOrWhiteSpace(ShareServer.LastError)
                ? "已关闭"
                : "启动失败：" + ShareServer.LastError;
        }

        ToggleBtn.Content = running ? "停止共享" : "开始共享";

        string tokenPath = options.RequireToken ? "s/" + options.Token : "";
        if (_qrIndex >= endpoints.Count) _qrIndex = 0;

        List<ShareUrlRow> urls = new();
        for (int i = 0; i < endpoints.Count; i++)
        {
            urls.Add(new ShareUrlRow
            {
                Index = i,
                Url = endpoints[i].Url(options.Port),
                Token = tokenPath.Length == 0 ? "" : "/" + tokenPath,
                Full = endpoints[i].Url(options.Port) + tokenPath,
                Selected = i == _qrIndex,
            });
        }

        UrlList.ItemsSource = urls;
        UrlHint.Text = urls.Count == 0 ? "当前没有局域网连接，先连上网络。" : "";
        UrlHint.Visibility = urls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShareUrlRow? qr = urls.ElementAtOrDefault(_qrIndex);
        string qrHint = urls.Count > 1
            ? $"扫码打开 · 第 {_qrIndex + 1}/{urls.Count} 条（{endpoints[_qrIndex].Name}）· 点这里换一条"
            : "扫码打开，或复制下面的地址。";
        UpdateQr(qr?.Full ?? "", qrHint);

        TokenCheck.IsChecked = options.RequireToken;
        InboundCheck.IsChecked = options.AllowInbound;
        ResetTokenBtn.Visibility = options.RequireToken ? Visibility.Visible : Visibility.Collapsed;

        DeviceText.Text = ShareServer.LastDevice == null
            ? "还没有设备连接"
            : $"最近：{ShareServer.LastDevice} · {Ago(ShareServer.LastSeen)}";

        // 正在打字时别把输入框里的内容冲掉。
        if (!PortBox.IsKeyboardFocusWithin) PortBox.Text = options.Port.ToString();
        if (!UploadBox.IsKeyboardFocusWithin) UploadBox.Text = options.MaxUploadMb.ToString();

        RefreshLists();
    }

    private void RefreshLists()
    {
        IReadOnlyList<ShareItem> items = _store.Items();
        IReadOnlyList<ShareInboxFile> inbox = _store.Inbox();

        string signature = string.Join("|", items.Select(i => i.Id + (i.Exists ? "+" : "-")))
            + "#" + string.Join("|", inbox.Select(f => f.Name + f.Size));
        if (signature == _listSignature) return;
        _listSignature = signature;

        OutTitle.Text = $"发出去的 {items.Count} 项";
        OutList.ItemsSource = items.Reverse().Select(ToRow).ToList();

        InTitle.Text = $"收到的 {inbox.Count} 项";
        InTitle.Visibility = inbox.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InList.ItemsSource = inbox.Reverse().Select(ToInboxRow).ToList();

        EmptyText.Visibility = items.Count == 0 && inbox.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateQr(string url, string hint)
    {
        if (string.IsNullOrEmpty(url))
        {
            _qrUrl = "";
            QrImage.Source = null;
            QrHint.Text = "没有局域网地址，先连上网络。";
            return;
        }

        if (url != _qrUrl)
        {
            _qrUrl = url;

            try
            {
                using System.Drawing.Bitmap bitmap = QrCodeAlgorithms.Generate(url, 360, 2, "M");
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                QrImage.Source = image;
            }
            catch (Exception ex)
            {
                QrImage.Source = null;
                QrHint.Text = "二维码生成失败：" + ex.Message;
                return;
            }
        }

        QrHint.Text = hint;
    }

    private ShareItemRow ToRow(ShareItem item)
    {
        bool isText = item.Exists && item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
        string kind = item.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "图片"
            : item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "文字"
            : "文件";

        return new ShareItemRow
        {
            Key = "item:" + item.Id,
            Name = item.Name,
            Glyph = Glyph(item),
            Body = isText ? PreviewOf(item.Path) : "",
            Meta = !item.Exists
                ? "已失效 · 原文件不在了"
                : isText
                    ? $"文字 · {item.AddedAt:HH:mm} · {FormatSize(item.Size)}"
                    : $"{kind} · {FormatSize(item.Size)} · {(item.Copied ? "副本" : "原地")}",
            Tip = !item.Exists
                ? "原文件被移动或删除了"
                : isText ? "双击查看全文" : "双击打开位置",
            Action1 = isText ? "复制" : "打开文件",
            Action1Tip = !item.Exists ? "原文件不在了"
                : isText ? "把这段文字放进剪贴板" : "用默认程序打开这个文件",
            Action2 = isText ? "查看" : "复制路径",
            Action2Tip = isText ? "打开只读窗口看全文" : "复制文件的完整路径",
            SaveVisibility = Visibility.Collapsed,
            IsText = isText,
            Stale = !item.Exists,
            IsInbox = false,
        };
    }

    private ShareItemRow ToInboxRow(ShareInboxFile file)
    {
        bool isText = IsTextName(file.Name);
        string path = Path.Combine(_store.InboxDir, file.Name);

        return new ShareItemRow
        {
            Key = "inbox:" + file.Name,
            Name = file.Name,
            Glyph = Glyph(file.Name),
            Body = isText ? PreviewOf(path) : "",
            Meta = isText
                ? $"文字 · {file.Modified:HH:mm} · {FormatSize(file.Size)}"
                : $"收到 · {FormatSize(file.Size)}",
            Tip = isText ? "双击查看全文" : "双击打开位置",
            Action1 = isText ? "复制" : "打开文件",
            Action1Tip = isText ? "把这段文字放进剪贴板" : "用默认程序打开这个文件",
            Action2 = "查看",
            Action2Tip = "打开只读窗口看全文",
            Action2Visibility = isText ? Visibility.Visible : Visibility.Collapsed,
            SaveVisibility = Visibility.Visible,
            IsText = isText,
            IsInbox = true,
        };
    }

    /// <summary>文字条目的正文预览（只读开头一段，铺两行）。</summary>
    private string PreviewOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return "";

            if (_previews.TryGetValue(path, out var cached)
                && cached.Stamp == info.LastWriteTimeUtc && cached.Size == info.Length)
            {
                return cached.Text;
            }

            string head;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var buffer = new char[600];
                int read = reader.ReadBlock(buffer, 0, buffer.Length);
                head = new string(buffer, 0, read);
            }

            string text = Collapse(head, 160);
            _previews[path] = (info.LastWriteTimeUtc, info.Length, text);
            return text;
        }
        catch
        {
            return "";
        }
    }

    private static string Collapse(string text, int limit)
    {
        string flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (flat.Length == 0) return "";
        return flat.Length <= limit ? flat : flat[..limit] + "…";
    }

    private static string Glyph(ShareItem item) =>
        item.Mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "图"
        : item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? "文"
        : Glyph(item.Name);

    private static string Glyph(string name)
    {
        string ext = Path.GetExtension(name).TrimStart('.');
        return ext.Length == 0 ? "件" : ext[..Math.Min(3, ext.Length)].ToUpperInvariant();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
    }

    private static string Ago(DateTime? time)
    {
        if (time == null) return "";
        TimeSpan delta = DateTime.Now - time.Value;
        if (delta.TotalSeconds < 60) return $"{Math.Max(1, (int)delta.TotalSeconds)} 秒前";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        return $"{(int)delta.TotalHours} 小时前";
    }

    // ============ 开关 ============

    private void Toggle_Click(object sender, RoutedEventArgs e) => ApplyOptions(toggleRunning: true);

    private void Token_Click(object sender, RoutedEventArgs e) => ApplyOptions();

    private void Inbound_Click(object sender, RoutedEventArgs e) => ApplyOptions();

    private void ResetToken_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(this, "重置令牌？",
                "旧地址会立刻失效，之前发出去的链接都需要重新扫码。", danger: true, okText: "重置")) return;

        ShareOptions current = ShareServer.Options;
        var options = Clone(current, token: ShareOptions.NewToken());
        ShareServer.Apply(options);
        SettingsStore.Modify(file => file.Share = options);
        Refresh();
    }

    private void ApplyOptions(bool toggleRunning = false)
    {
        ShareOptions current = ShareServer.Options;
        bool running = toggleRunning ? !ShareServer.IsRunning : current.Running;
        bool needToken = TokenCheck.IsChecked == true;

        var options = new ShareOptions
        {
            Port = current.Port,
            Running = running,
            AllowInbound = InboundCheck.IsChecked == true,
            MaxUploadMb = current.MaxUploadMb,
            RequireToken = needToken,
            Token = current.Token,
        };

        if (needToken && string.IsNullOrEmpty(options.Token)) options.Token = ShareOptions.NewToken();

        ShareServer.Apply(options);
        SettingsStore.Modify(file => file.Share = options);
        Refresh();
    }

    private static ShareOptions Clone(ShareOptions source, string? token = null) => new()
    {
        Port = source.Port,
        Running = source.Running,
        AllowInbound = source.AllowInbound,
        MaxUploadMb = source.MaxUploadMb,
        RequireToken = source.RequireToken,
        Token = token ?? source.Token,
    };

    // ============ 发送 ============

    private void ComposeBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SendBtn.IsEnabled = ComposeBox.Text.Trim().Length > 0;

    private void ComposeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 输入法组字期间的回车 / 空格是给它自己的，别当成发送。
        if (e.Key == Key.ImeProcessed) return;

        // 回车发送、Shift+回车换行（多行框默认把回车当换行）。
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            SendCompose();
            return;
        }

        // 剪贴板里是图片 / 文件时别交给文本框（它只会粘出几个文件名或什么都不做）。
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && TryAddFromClipboard(richOnly: true))
        {
            e.Handled = true;
        }
    }

    private void Send_Click(object sender, RoutedEventArgs e) => SendCompose();

    /// <summary>输入框右键菜单：剪切 / 复制 / 全选走标准命令，粘贴照「图片 / 文件进共享区」的口径。</summary>
    private ContextMenu BuildComposeMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = "剪切", Command = ApplicationCommands.Cut, CommandTarget = ComposeBox });
        menu.Items.Add(new MenuItem { Header = "复制", Command = ApplicationCommands.Copy, CommandTarget = ComposeBox });

        var paste = new MenuItem { Header = "粘贴" };
        paste.Click += (_, _) =>
        {
            if (!TryAddFromClipboard(richOnly: true)) ComposeBox.Paste();
        };
        menu.Items.Add(paste);

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "全选", Command = ApplicationCommands.SelectAll, CommandTarget = ComposeBox,
        });

        return menu;
    }

    private void SendCompose()
    {
        string text = ComposeBox.Text.Trim();
        if (text.Length == 0) return;
        if (!TryAdd(() => _store.AddText(text, $"文字 {ShareStore.Stamp()}.txt"))) return;

        ComposeBox.Clear();
        ComposeBox.Focus();
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        if (TryAddFromClipboard(richOnly: false)) return;

        ConfirmDialog.Info(this, "剪贴板是空的",
            "先复制一段文字、一张图片或一个文件，再点这个按钮（也可以直接在这里粘贴）。");
    }

    /// <summary>把剪贴板里的文件 / 图片 / 文字收进共享区（文件优先，其次图片，最后文字）。</summary>
    /// <param name="richOnly">true = 只管图片 / 文件，文字留给文本框自己粘（Ctrl+V 在输入框里用）。</param>
    private bool TryAddFromClipboard(bool richOnly)
    {
        try
        {
            if (Native.NativeClipboard.TryGetFileList(out List<string> files) && files.Count > 0)
            {
                AddFilesCore(files);
                return true;
            }

            if (Native.NativeClipboard.TryGetImage(out BitmapSource? image) && image != null)
                return TryAdd(() => AddImage(image, $"图片 {ShareStore.Stamp()}.png"));

            if (richOnly) return false;

            if (Native.NativeClipboard.TryGetText(out string text) && text.Trim().Length > 0)
                return TryAdd(() => _store.AddText(text, $"文字 {ShareStore.Stamp()}.txt"));
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "读剪贴板失败", ex.Message);
        }

        return false;
    }

    /// <summary>把一批路径收进共享区：不存在、以及已经在清单里的都跳过（拖两次不会出现两条）。</summary>
    private void AddFilesCore(IEnumerable<string> paths)
    {
        TryAdd(() =>
        {
            HashSet<string> known = _store.Items()
                .Where(i => i.Exists)
                .Select(i => i.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int before = known.Count;
            int added = 0;
            foreach (string raw in paths)
            {
                string path;
                try { path = Path.GetFullPath(raw); } catch { continue; }
                if (!File.Exists(path) || !known.Add(path)) continue;
                _store.AddPath(path);
                added++;
            }

            AppLog.Info("Share", $"拖放：收到 {paths.Count()} 个路径，已有 {before} 条，新增 {added} 条");
        });
    }

    private void ChooseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要共享的文件",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;
        AddFilesCore(dialog.FileNames);
    }

    /// <summary>拖进来 / 粘进来的图片存进 <c>share/files/</c>（副本），再记进清单。</summary>
    private void AddImage(BitmapSource image, string fileName)
    {
        _store.EnsureLayout();
        string path = ShareStore.UniquePath(_store.FilesDir, fileName);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);

        _store.AddPath(path, copied: true);
    }

    private bool TryAdd(Action action)
    {
        try
        {
            action();
            _listSignature = "";
            Refresh();
            return true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "添加失败", ex.Message);
            return false;
        }
    }

    // ============ 拖放 / 粘贴 ============

    private static bool CanDrop(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop)
        || data.GetDataPresent(DataFormats.Bitmap)
        || data.GetDataPresent(DataFormats.UnicodeText);

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (_dragLogged) return;
        _dragLogged = true;
        AppLog.Info("Share", "拖放：OLE 进入窗口（" + Describe(e.Data) + "）");
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e) => _dragLogged = false;

    private static string Describe(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) ? "文件"
        : data.GetDataPresent(DataFormats.Bitmap) ? "图片"
        : data.GetDataPresent(DataFormats.UnicodeText) ? "文字"
        : "不认识的内容";

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLogged = false;
        AppLog.Info("Share", "拖放：OLE 松手（" + Describe(e.Data) + "）");

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            AddFilesCore(paths);
            return;
        }

        if (TryGetDroppedImage(e.Data, out BitmapSource? image) && image != null)
        {
            TryAdd(() => AddImage(image, $"图片 {ShareStore.Stamp()}.png"));
            return;
        }

        if (e.Data.GetData(DataFormats.UnicodeText) is string text && text.Trim().Length > 0)
            InsertIntoCompose(text);
    }

    /// <summary>从拖放数据里取图片（PNG 流优先，其次位图）。</summary>
    private static bool TryGetDroppedImage(IDataObject data, out BitmapSource? image)
    {
        image = null;

        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream stream)
            {
                image = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                image.Freeze();
                return true;
            }

            if (!data.GetDataPresent(DataFormats.Bitmap)) return false;

            switch (data.GetData(DataFormats.Bitmap))
            {
                case BitmapSource source:
                    image = source;
                    return true;

                case System.Drawing.Bitmap bitmap:
                    using (var memory = new MemoryStream())
                    {
                        bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                        memory.Position = 0;
                        image = BitmapFrame.Create(memory, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        image.Freeze();
                    }
                    return true;
            }
        }
        catch
        {
            // 拖放数据来自别的进程，坏掉就当没取到。
        }

        return false;
    }

    /// <summary>把一段文字塞进输入框，接着写 / 回车发送。</summary>
    private void InsertIntoCompose(string text)
    {
        if (text.Length == 0) return;

        ComposeBox.Text = ComposeBox.Text.TrimEnd() is { Length: > 0 } draft
            ? draft + Environment.NewLine + text
            : text;

        ComposeBox.CaretIndex = ComposeBox.Text.Length;
        ComposeBox.Focus();
    }

    /// <summary>焦点不在输入框时的 Ctrl+V：图片 / 文件进共享区，文字填进输入框。</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (ComposeBox.IsKeyboardFocusWithin) return;

        if (TryAddFromClipboard(richOnly: true))
        {
            e.Handled = true;
            return;
        }

        if (Native.NativeClipboard.TryGetText(out string text) && text.Trim().Length > 0)
        {
            InsertIntoCompose(text);
            e.Handled = true;
        }
    }

    // ============ 行操作 ============

    /// <summary>行内第一个按钮：文字「复制」正文，文件「打开文件」。</summary>
    private void RowAction1_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;

        string? path = PathOf(key);
        if (path == null) return;

        if (!File.Exists(path))
        {
            ToastService.Show("原文件不在了，重新添加一次即可", null, 2600, ToastKind.warning);
            return;
        }

        if (IsTextName(Path.GetFileName(path))) CopyFileText(path);
        else OpenPath(path);
    }

    /// <summary>行内第二个按钮：文字「查看」全文，发出去的文件「复制路径」。</summary>
    private void RowAction2_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;

        if (key.StartsWith("item:", StringComparison.Ordinal))
        {
            ShareItem? item = _store.Find(key[5..]);
            if (item == null) return;

            if (item.Exists && item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                ShowText(item);
                return;
            }

            Copy(item.Path);
            ToastService.Show("路径已复制", null, 1600, ToastKind.success);
            return;
        }

        ViewKey(key);
    }

    /// <summary>行内「另存为」（只有收到的内容有）。</summary>
    private void RowAction3_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;
        if (!key.StartsWith("inbox:", StringComparison.Ordinal)) return;

        string name = key[6..];
        string path = Path.Combine(_store.InboxDir, name);
        if (File.Exists(path)) SaveInboxAs(path, name);
    }

    private void RowRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string key }) return;

        if (key.StartsWith("item:", StringComparison.Ordinal)) RemoveItem(key[5..]);
        else if (key.StartsWith("inbox:", StringComparison.Ordinal)) RemoveInbox(key[6..]);
    }

    /// <summary>行键 → 文件路径（<c>item:&lt;id&gt;</c> / <c>inbox:&lt;name&gt;</c>）。</summary>
    private string? PathOf(string key)
    {
        if (key.StartsWith("item:", StringComparison.Ordinal))
            return _store.Find(key[5..])?.Path;

        if (key.StartsWith("inbox:", StringComparison.Ordinal))
            return Path.Combine(_store.InboxDir, key[6..]);

        return null;
    }

    /// <summary>文字条目的「复制」：整篇进剪贴板（超过 4 MB 不读，免得卡窗口）。</summary>
    private void CopyFileText(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                ToastService.Show("文字太大（超过 4 MB），用「查看」打开后再复制", null, 3000, ToastKind.warning);
                return;
            }

            Copy(File.ReadAllText(path));
            ToastService.Show("已复制到剪贴板", null, 1600, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("复制失败：" + ex.Message, null, 3000, ToastKind.error);
        }
    }

    /// <summary>用默认程序打开文件（走 explorer，别让提权进程把目标程序也拉成提权）。</summary>
    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打不开这个文件：" + ex.Message, null, 3000, ToastKind.error);
        }
    }

    /// <summary>双击一行：文字打开只读窗口，其它文件在资源管理器里定位（行内按钮不算）。</summary>
    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (AncestorButton(e.OriginalSource as DependencyObject) != null) return;
        if (sender is FrameworkElement { Tag: string key }) ViewKey(key);
    }

    private static Button? AncestorButton(DependencyObject? node)
    {
        for (; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is Button button) return button;
        return null;
    }

    private void ViewKey(string key)
    {
        if (key.StartsWith("item:", StringComparison.Ordinal))
        {
            ShareItem? item = _store.Find(key[5..]);
            if (item == null) return;

            if (!item.Exists)
            {
                ConfirmDialog.Info(this, "文件已失效", "原文件被移动或删除了，重新添加一次即可。");
                return;
            }

            if (item.Mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                ShowText(item);
                return;
            }

            Reveal(item.Path);
            return;
        }

        if (key.StartsWith("inbox:", StringComparison.Ordinal))
        {
            string name = key[6..];
            string path = Path.Combine(_store.InboxDir, name);
            if (!File.Exists(path)) return;

            if (IsTextName(name)) ShowTextFile(path, name, key);
            else Reveal(path);
        }
    }

    private static bool IsTextName(string name) =>
        name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private void ShowText(ShareItem item) => ShowTextFile(item.Path, item.Name, "share:" + item.Id);

    /// <summary>只读文本窗口看全文（文字条目与对方发来的 .txt 共用）。</summary>
    private void ShowTextFile(string path, string title, string cacheKey)
    {
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024)
            {
                ConfirmDialog.Info(this, "文字太大", "超过 4 MB，用「打开位置」直接看文件。");
                return;
            }

            TextWindowService.Open(cacheKey, title, File.ReadAllText(path),
                editable: false, monospace: false, append: false, activate: false);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "读不到文字", ex.Message);
        }
    }

    private void RemoveItem(string id)
    {
        ShareItem? item = _store.Find(id);
        if (item == null) return;

        if (!item.Copied)
        {
            string message = $"只从共享列表移除「{item.Name}」，原文件不会被删除。";
            if (!ConfirmDialog.Confirm(this, "移除这一项？", message, okText: "移除")) return;
        }

        _store.Remove(id);
        _listSignature = "";
        Refresh();
    }

    private void RemoveInbox(string name)
    {
        if (!ConfirmDialog.Confirm(this, "删除收到的文件？", $"「{name}」会从本机删掉，不能恢复。",
                danger: true, okText: "删除")) return;

        _store.RemoveInbox(name);
        _listSignature = "";
        Refresh();
    }

    private void SaveInboxAs(string path, string name)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "另存收到的文件",
            FileName = name,
        };

        if (dialog.ShowDialog(this) != true) return;
        try { File.Copy(path, dialog.FileName, overwrite: true); }
        catch (Exception ex) { ConfirmDialog.Info(this, "保存失败", ex.Message); }
    }

    private void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "打不开位置", ex.Message);
        }
    }

    private void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* 剪贴板被占用时忽略 */ }
    }

    // ============ 其它 ============

    private void CopyUrlRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: string url } && url.Length > 0)
        {
            Copy(url);
            ToastService.Show("地址已复制：" + url, null, 2200, ToastKind.success);
        }
    }

    /// <summary>点二维码 = 换下一条地址（多网卡时）。</summary>
    private void Qr_Click(object sender, MouseButtonEventArgs e)
    {
        if (UrlList.ItemsSource is not IEnumerable<ShareUrlRow> rows) return;

        int count = rows.Count();
        if (count < 2) return;

        _qrIndex = (_qrIndex + 1) % count;
        Refresh();
    }

    /// <summary>点某条地址 = 用它画二维码。</summary>
    private void UrlRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int index }) return;
        if (index == _qrIndex) return;

        _qrIndex = index;
        Refresh();
    }

    /// <summary>打开目录本身（进入共享目录；`/select` 只会停在它的上一级并选中它）。</summary>
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = _store.Root;
            if (!Directory.Exists(path))
            {
                ConfirmDialog.Info(this, "目录不存在", "已经找不到这个目录了：" + path);
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "打不开位置", ex.Message);
        }
    }

    // ============ 设置：就地改 ============

    private void PortBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitPort(notify: true);
            ComposeBox.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PortBox.Text = ShareServer.Options.Port.ToString();
            ComposeBox.Focus();
        }
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e) => CommitPort(notify: false);

    private void UploadBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitUploadLimit(notify: true);
            ComposeBox.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            UploadBox.Text = ShareServer.Options.MaxUploadMb.ToString();
            ComposeBox.Focus();
        }
    }

    private void UploadBox_LostFocus(object sender, RoutedEventArgs e) => CommitUploadLimit(notify: false);

    private void CommitPort(bool notify)
    {
        ShareOptions current = ShareServer.Options;
        string text = PortBox.Text.Trim();
        if (text == current.Port.ToString()) return;

        if (!int.TryParse(text, out int port) || port is < 1024 or > 65535)
        {
            PortBox.Text = current.Port.ToString();
            if (notify) ShowSettingsHint("端口要填 1024–65535 之间的整数");
            return;
        }

        ShareOptions next = Clone(current);
        next.Port = port;
        ApplyOptions(next);

        if (notify) ToastService.Show($"端口改成 {port}", null, 1800, ToastKind.success);
    }

    private void CommitUploadLimit(bool notify)
    {
        ShareOptions current = ShareServer.Options;
        string text = UploadBox.Text.Trim();
        if (text == current.MaxUploadMb.ToString()) return;

        if (!int.TryParse(text, out int mb) || mb is < 1 or > 4096)
        {
            UploadBox.Text = current.MaxUploadMb.ToString();
            if (notify) ShowSettingsHint("上传上限要填 1–4096 之间的整数（MB）");
            return;
        }

        ShareOptions next = Clone(current);
        next.MaxUploadMb = mb;
        ApplyOptions(next);

        if (notify) ToastService.Show($"上传上限改成 {mb} MB", null, 1800, ToastKind.success);
    }

    private void ApplyOptions(ShareOptions next)
    {
        ShareServer.Apply(next);
        SettingsStore.Modify(file => file.Share = next);
        Refresh();
    }

    private void ShowSettingsHint(string message)
    {
        SettingsHint.Text = message;
        SettingsHint.Visibility = Visibility.Visible;

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            SettingsHint.Visibility = Visibility.Collapsed;
        };
        _hintTimer.Start();
    }

    private sealed class ShareItemRow
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Glyph { get; init; } = "件";
        public string Meta { get; init; } = "";
        public string Body { get; init; } = "";
        public string Tip { get; init; } = "";
        public string Action1 { get; init; } = "";
        public string Action1Tip { get; init; } = "";
        public string Action2 { get; init; } = "";
        public string Action2Tip { get; init; } = "";
        public Visibility Action2Visibility { get; init; } = Visibility.Visible;
        public Visibility SaveVisibility { get; init; } = Visibility.Collapsed;
        public bool IsText { get; init; }
        public bool Stale { get; init; }
        public bool IsInbox { get; init; }
    }

    private sealed class ShareUrlRow
    {
        public int Index { get; init; }
        public string Url { get; init; } = "";
        public string Token { get; init; } = "";
        public string Full { get; init; } = "";
        public bool Selected { get; init; }
    }

    /// <summary>文字条目铺正文（方案 B），其它还是单行。</summary>
    private sealed class ShareRowSelector : DataTemplateSelector
    {
        public DataTemplate Plain { get; init; } = null!;
        public DataTemplate Text { get; init; } = null!;

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is ShareItemRow { IsText: true } ? Text : Plain;
    }
}
