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

/// <summary>ShareWindow 的「拖放 / 粘贴」部分。</summary>
public partial class ShareWindow : Window
{
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

}
