using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 切图窗的入口：打开空窗、按文件打开、把内存里的一张图丢进去切。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageSplitWindowCoordinator</c>：**一次切一张**，但允许多个窗口同时开着
/// （「打开」总是新建，空窗只在没有任何图时才复用）。入口一律从 UI 线程进来，
/// 非 UI 线程的调用先投递（与 <see cref="ImageEditSession"/> 同一套约定）。
/// </remarks>
internal static class SplitSession
{
    private static readonly List<ImageSplitWindow> Open = new();

    /// <summary>打开一个空窗（托盘「切图…」）；已经有空窗就把它拿到前台。</summary>
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
        window.Activate();
    }

    /// <summary>按文件打开（拖放 / 托盘 / 关联打开）：不是图片的跳过，每张图一个窗口。</summary>
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
            ToastService.Show("没有可切图的图片文件", "切图", 2800, ToastKind.warning, key: "image-split-open");
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

    /// <summary>
    /// 把内存里的一张图直接开去切（长截图完成 / 贴图右键 / 采集历史）。
    /// 已经有一个空窗就复用它。
    /// </summary>
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

    /// <summary>打开采集历史里的一条（截图 / 识别）；录屏条目没有图，返回 false。</summary>
    public static bool OpenCapture(CaptureEntry entry)
    {
        if (entry == null || entry.IsRecord) return false;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id));
        return OpenImage(image, $"采集历史 {entry.TimeText}");
    }

    /// <summary>打开贴图历史里的一条（贴图本身可能已经关掉了，但 png 还在）。</summary>
    public static bool OpenPinned(string pinId)
    {
        if (string.IsNullOrWhiteSpace(pinId)) return false;

        var image = PinStore.LoadAsset(pinId);
        if (image == null) return false;

        var record = PinStore.Load().FirstOrDefault(item => item.Id == pinId);
        string name = record != null ? $"贴图 {record.TimeText}" : "贴图";
        return OpenImage(image, name, PinStore.AssetPath(pinId));
    }

    /// <summary>窗口自报家门（构造函数里调）；关窗时自动摘掉。</summary>
    internal static void Track(ImageSplitWindow window)
    {
        if (!Open.Contains(window)) Open.Add(window);
        window.Closed += (_, _) => Open.Remove(window);
    }

    private static ImageSplitWindow Create() => new();

    /// <summary>现有空窗（从最新往里找）：新打开的图先填它，别堆一屏空白窗。</summary>
    private static ImageSplitWindow? TakeEmpty()
    {
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var window = Open[i];
            if (!window.IsLoaded || !window.IsVisible) continue;
            if (window.IsEmpty) return window;
        }
        return null;
    }
}
