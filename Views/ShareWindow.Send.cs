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

/// <summary>ShareWindow 的「发送」部分。</summary>
public partial class ShareWindow : Window
{
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

}
