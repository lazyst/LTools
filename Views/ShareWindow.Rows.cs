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

/// <summary>ShareWindow 的「行操作与预览」部分。</summary>
public partial class ShareWindow : Window
{
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
        if (sender is not FrameworkElement element || element.Tag is not string key) return;

        DependencyObject? source = e.OriginalSource as DependencyObject;
        if (VisualTree.FindAncestor<Button>(source) != null) return;
        if (VisualTree.FindAncestor<CheckBox>(source) != null) return;

        // 批量管理：单击整行 = 勾上 / 取消，双击也不打开文件。
        if (IsBatchRow(key))
        {
            if (element.DataContext is ShareItemRow row) ToggleRowPick(row);
            e.Handled = true;
            return;
        }

        if (e.ClickCount != 2) return;
        ViewKey(key);
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

}
