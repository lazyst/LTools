using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼接窗的入口：托盘「图像拼接…」、贴图右键、采集历史、拖放 / 关联打开，以及需要「把这张图
/// 拿去拼」的其它功能。
/// </summary>
/// <remarks>
/// 拼接和切图不一样：切图一次只服务一张、每张开一个窗口；拼接是**往同一个会话里加图**，
/// 所以有能接图的窗口就复用、没有才新建（上游同样是「再点一次添加」而不是新开一张白板）。
/// 窗口在加载 / 导出时 <see cref="ImageCollageWindow.CanAcceptImages"/> 为假，这时宁可再开一个，
/// 也不要把用户的图悄悄丢掉。入口一律从 UI 线程进来，非 UI 线程先投递。
/// </remarks>
internal static class CollageSession
{
    private static readonly List<ImageCollageWindow> Open = new();

    /// <summary>打开一个空窗（托盘「图像拼接…」）；已经有窗口就把它拿到前台。</summary>
    public static void OpenEmpty()
    {
        if (!Ui.IsUiThread)
        {
            Ui.Post(OpenEmpty);
            return;
        }

        if (TakeWindow() is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = Create();
        window.Show();
        window.PresentEmpty();
        window.Activate();
    }

    /// <summary>按文件添加（拖放 / 关联打开）：不是图片的跳过，全部加进同一个拼接会话。</summary>
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
            ToastService.Show("没有可拼接的图像文件", "图像拼接", 2800, ToastKind.warning,
                key: "image-collage-open");
            return;
        }

        var window = TakeWindow() ?? Create();
        window.Show();
        window.PresentFiles(images);
        window.Activate();
    }

    /// <summary>把内存里的一张图加进拼接（贴图右键 / 采集历史 / 复制到拼接）。</summary>
    public static bool OpenImage(BitmapSource? image, string name)
    {
        if (image == null) return false;

        if (!Ui.IsUiThread)
        {
            Ui.Post(() => OpenImage(image, name));
            return true;
        }

        var window = TakeWindow() ?? Create();
        window.Show();
        window.PresentImage(image, name);
        window.Activate();
        return true;
    }

    /// <summary>把采集历史里的一条加进拼接；录屏条目没有静态图，返回 false。</summary>
    public static bool OpenCapture(CaptureEntry entry)
    {
        if (entry == null || entry.IsRecord) return false;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id));
        return OpenImage(image, $"采集历史 {entry.TimeText}");
    }

    /// <summary>把贴图历史里的一条加进拼接（贴图本身可能已经关掉了，但 png 还在）。</summary>
    public static bool OpenPinned(string pinId)
    {
        if (string.IsNullOrWhiteSpace(pinId)) return false;

        var image = PinStore.LoadAsset(pinId);
        if (image == null) return false;

        var record = PinStore.Load().FirstOrDefault(item => item.Id == pinId);
        string name = record != null ? $"贴图 {record.TimeText}" : "贴图";
        return OpenImage(image, name);
    }

    /// <summary>窗口自报家门（构造函数里调）；关窗时自动摘掉。</summary>
    internal static void Track(ImageCollageWindow window)
    {
        if (Open.Contains(window)) return;

        Open.Add(window);
        window.Closed += (_, _) => Open.Remove(window);
    }

    private static ImageCollageWindow Create() => new();

    /// <summary>从最新往里找第一个还能接图的窗口（加载 / 导出中的跳过）。</summary>
    private static ImageCollageWindow? TakeWindow()
    {
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var window = Open[i];
            if (!window.IsLoaded || !window.IsVisible) continue;
            if (window.CanAcceptImages) return window;
        }

        return null;
    }
}
