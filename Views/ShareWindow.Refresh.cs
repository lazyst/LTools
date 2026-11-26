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

/// <summary>ShareWindow 的「刷新与行模型」部分。</summary>
public partial class ShareWindow : Window
{
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
        _syncingInbound = true;
        InboundCombo.SelectedValue = options.Inbound;
        InboundHint.Text = ShareOptions.DescribeInbound(options.Inbound);
        _syncingInbound = false;
        ResetTokenBtn.Visibility = options.RequireToken ? Visibility.Visible : Visibility.Collapsed;

        DeviceText.Text = ShareServer.LastDevice == null
            ? "还没有设备连接"
            : $"最近：{ShareServer.LastDevice} · {Ago(ShareServer.LastSeen)}";

        // 正在打字时别把输入框里的内容冲掉。
        if (!PortBox.IsKeyboardFocusWithin) PortBox.Text = options.Port.ToString();
        if (!UploadBox.IsKeyboardFocusWithin) UploadBox.Text = options.MaxUploadMb.ToString();

        bool customInbox = options.InboxDir.Length > 0;
        InboxPathText.Text = customInbox ? _store.InboxDir : "share/inbox";
        InboxPathText.ToolTip = _store.InboxDir;
        InboxDirHint.Text = customInbox
            ? "自定义目录：这里面的文件对方都能下载。「默认」回到共享区的 inbox。"
            : "默认在共享区的 inbox；换到别处时注意，那个目录里的文件对方都能下载。";

        RefreshLists();
    }

    private void RefreshLists()
    {
        IReadOnlyList<ShareItem> items = _store.Items();
        IReadOnlyList<ShareInboxFile> inbox = _store.Inbox();

        string signature = _store.InboxDir + "#"
            + string.Join("|", items.Select(i => i.Id + (i.Exists ? "+" : "-")))
            + "#" + string.Join("|", inbox.Select(f => f.Name + f.Size));
        if (signature == _listSignature) return;
        _listSignature = signature;

        OutTitle.Text = $"发出去的 {items.Count} 项";
        OutList.ItemsSource = items.Reverse().Select(ToRow).ToList();

        InTitle.Text = $"收到的 {inbox.Count} 项";
        InHeader.Visibility = inbox.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        InList.ItemsSource = inbox.Reverse().Select(ToInboxRow).ToList();

        EmptyText.Visibility = items.Count == 0 && inbox.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SyncPickedRows();
        SyncBatchButtons();
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
                    ? $"文字 · {When(item.AddedAt)} · {ShareStore.FormatSize(item.Size)}"
                    : $"{kind} · {When(item.AddedAt)} · {ShareStore.FormatSize(item.Size)} · {(item.Copied ? "副本" : "原地")}",
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
            PickVisibility = _batchOut ? Visibility.Visible : Visibility.Collapsed,
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
                ? $"文字 · {When(file.Modified)} · {ShareStore.FormatSize(file.Size)}"
                : $"收到 · {When(file.Modified)} · {ShareStore.FormatSize(file.Size)}",
            Tip = isText ? "双击查看全文" : "双击打开位置",
            Action1 = isText ? "复制" : "打开文件",
            Action1Tip = isText ? "把这段文字放进剪贴板" : "用默认程序打开这个文件",
            Action2 = "查看",
            Action2Tip = "打开只读窗口看全文",
            Action2Visibility = isText ? Visibility.Visible : Visibility.Collapsed,
            SaveVisibility = Visibility.Visible,
            IsText = isText,
            IsInbox = true,
            PickVisibility = _batchIn ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    /// <summary>时间短写法：今天只给 HH:mm，跨天补 MM-dd（发出去的 / 收到的行共用）。</summary>
    private static string When(DateTime time) =>
        time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");

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


    private static string Ago(DateTime? time)
    {
        if (time == null) return "";
        TimeSpan delta = DateTime.Now - time.Value;
        if (delta.TotalSeconds < 60) return $"{Math.Max(1, (int)delta.TotalSeconds)} 秒前";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        return $"{(int)delta.TotalHours} 小时前";
    }

}
