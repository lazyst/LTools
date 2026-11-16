using System.Collections.Generic;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图组：所有还开着的贴图窗口的登记处（「关闭全部贴图」、把贴图顶回最前）。
/// </summary>
/// <remarks>
/// <para>
/// 贴图**不进配置**（与悬浮项同一口径）：关掉程序就没了，重启不恢复——
/// 它们是一次性的临时视图，不是用户资产。
/// </para>
/// <para>
/// 只在 UI 线程用：建窗口、<c>Show()</c>、关闭都必须在 WPF 的 Dispatcher 上。
/// 关窗由窗口自己 <see cref="Forget"/> 摘掉，所以用户的 ✕ / 双击 / 关闭全部三条路都对得上。
/// </para>
/// </remarks>
internal static class CapturePinManager
{
    private static readonly List<CapturePinWindow> Pins = new();

    /// <summary>还开着的贴图数量。</summary>
    public static int Count => Pins.Count;

    /// <summary>当前贴图（按出现的先后）。</summary>
    public static IReadOnlyList<CapturePinWindow> All => Pins;

    /// <summary>把一张图贴到屏幕上（正好盖住它截下来的那块区域）。</summary>
    public static CapturePinWindow Pin(BitmapSource image, Int32Rect area, double scale)
    {
        var pin = new CapturePinWindow(image, area, scale);
        Pins.Add(pin);
        pin.Show();          // 无激活窗口：ShowActivated=False + WS_EX_NOACTIVATE
        pin.RaiseToTop();
        return pin;
    }

    /// <summary>全关（右键菜单「关闭全部贴图」）。</summary>
    public static void CloseAll()
    {
        foreach (var pin in Pins.ToArray()) pin.Close();
        Pins.Clear();
    }

    /// <summary>把贴图顶回最前（面板 / 悬浮抢到前面之后调；不抢焦点）。</summary>
    public static void RaiseAll()
    {
        foreach (var pin in Pins) pin.RaiseToTop();
    }

    /// <summary>窗口关闭时摘掉自己（重复调用无害）。</summary>
    internal static void Forget(CapturePinWindow pin) => Pins.Remove(pin);
}
