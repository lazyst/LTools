using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro.ImageEdit;

/// <summary>
/// 图片编辑窗的入口：打开空窗、按文件 / 采集历史 / 贴图记录打开、剪贴板粘贴。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageEditingWindow::openFiles</c>：**一张图一个窗口**——第一个文件进当前空窗
/// （没有就新建），其余各自开新窗。窗口自己从 <see cref="Track"/> 登记，关掉即摘掉。
/// 入口一律从 UI 线程进来（托盘 / 采集历史 / 贴图菜单），非 UI 线程的调用先投递。
/// </remarks>
internal static class ImageEditSession
{
    private static readonly List<ImageEditWindow> Open = new();

    /// <summary>打开一个空窗（托盘「图片编辑…」）；已经有空窗就把它拿到前台。</summary>
    public static void OpenEmpty()
    {
        if (!Ui.IsUiThread)
        {
            Ui.Post(OpenEmpty);
            return;
        }

        if (TakeEmpty() is { } empty)
        {
            empty.Activate();
            return;
        }

        var window = Create();
        window.Show();
        window.PresentEmpty();
    }

    /// <summary>按文件打开（拖放 / 添加图片 / 关联打开）：不是图片的跳过，其余每张一个窗口。</summary>
    public static void OpenFiles(IReadOnlyList<string> paths)
    {
        if (!Ui.IsUiThread)
        {
            Ui.Post(() => OpenFiles(paths));
            return;
        }

        var images = paths.Where(PinFiles.IsImage).ToList();
        if (images.Count == 0)
        {
            ToastService.Show("没有可编辑的图片文件", "图片编辑", 2800, ToastKind.warning, key: "image-edit-open");
            return;
        }

        for (int i = 0; i < images.Count; i++)
        {
            var window = i == 0 ? TakeEmpty() ?? Create() : Create();
            window.Show();
            window.PresentFile(images[i]);
            window.Activate();
        }
    }

    /// <summary>打开采集历史里的一条（截图 / 识别）；录屏条目没有图，返回 false。</summary>
    public static bool OpenCapture(CaptureEntry entry)
    {
        if (entry == null || entry.IsRecord) return false;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id));
        if (image == null) return false;

        var window = TakeEmpty() ?? Create();
        window.Show();
        window.PresentImage(image, $"采集历史 {entry.TimeText}", null);
        window.Activate();
        return true;
    }

    /// <summary>把一张内存里的图直接交给编辑窗（长截图「编辑」/ 采集历史）。</summary>
    public static bool OpenImage(BitmapSource? image, string name, string? sourcePath = null)
    {
        if (image == null) return false;

        if (!Ui.IsUiThread)
        {
            Ui.Post(() => OpenImage(image, name, sourcePath));
            return true;
        }

        var window = TakeEmpty() ?? Create();
        window.Show();
        window.PresentImage(image, name, sourcePath);
        window.Activate();
        return true;
    }

    /// <summary>打开贴图历史里的一条（贴图本身可能已经关掉了，但 png 还在）。</summary>
    public static bool OpenPinned(string pinId)
    {
        if (string.IsNullOrWhiteSpace(pinId)) return false;

        var image = PinStore.LoadAsset(pinId);
        if (image == null) return false;

        var record = PinStore.Load().FirstOrDefault(item => item.Id == pinId);
        string name = record != null ? $"贴图 {record.TimeText}" : "贴图";

        var window = TakeEmpty() ?? Create();
        window.Show();
        window.PresentImage(image, name, PinStore.AssetPath(pinId));
        window.Activate();
        return true;
    }

    /// <summary>粘贴：剪贴板里的图，或复制的图片文件（第一张进窗口，其余各开一个）。</summary>
    public static void Paste()
    {
        if (!Ui.IsUiThread)
        {
            Ui.Post(Paste);
            return;
        }

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            var window = TakeEmpty() ?? Create();
            window.Show();
            window.PresentImage(image, "剪贴板图片", null);
            window.Activate();
            return;
        }

        if (NativeClipboard.TryGetFileList(out var files))
        {
            var images = files.Where(PinFiles.IsImage).ToList();
            if (images.Count > 0)
            {
                OpenFiles(images);
                return;
            }
        }

        ToastService.Show("剪贴板里没有图片", "图片编辑", 2800, ToastKind.warning, key: "image-edit-paste");
    }

    /// <summary>窗口自报家门（构造函数里调）；关窗时自动摘掉。</summary>
    internal static void Track(ImageEditWindow window)
    {
        if (!Open.Contains(window)) Open.Add(window);
        window.Closed += (_, _) => Open.Remove(window);
    }

    private static ImageEditWindow Create()
    {
        var window = new ImageEditWindow();
        return window;
    }

    /// <summary>现有空窗（从最新往里找）：新打开的图先填它，别堆一屏空白窗。</summary>
    private static ImageEditWindow? TakeEmpty()
    {
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var window = Open[i];
            if (!window.IsLoaded || !window.IsVisible) continue;
            if (window.IsEmpty) return window;
        }
        return null;
    }

    /// <summary>按文件路径打开（不做扩展名判断，调用方已经筛过）。</summary>
    internal static BitmapSource? LoadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            if (new System.IO.FileInfo(path).Length > PinFiles.MaximumImageBytes) return null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("ImageEditSession.LoadFile", ex.Message);
            return null;
        }
        return CaptureCatalog.LoadImage(path);
    }
}
