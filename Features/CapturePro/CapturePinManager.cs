using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图组：所有还开着的贴图窗口的登记处（「关闭全部贴图」、把贴图顶回最前、成组移动）。
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

    /// <summary>拖动开始时的落点快照（成组移动按「起点 + 同一个位移」算，逐次累加会攒误差）。</summary>
    private static readonly List<(CapturePinWindow Pin, Rect Box)> MoveBase = new();

    private static CapturePinWindow? _moving;

    /// <summary>还开着的贴图数量。</summary>
    public static int Count => Pins.Count;

    /// <summary>当前贴图（按出现的先后）。</summary>
    public static IReadOnlyList<CapturePinWindow> All => Pins;

    /// <summary>
    /// 成组移动：开着时拖任意一张，全组一起走（默认关，跟着进程，不落盘）。
    /// 只有一张时没有「组」可言，菜单里会置灰。
    /// </summary>
    public static bool GroupMove { get; set; }

    /// <summary>把一张图贴到屏幕上（正好盖住它截下来的那块区域）。</summary>
    public static CapturePinWindow Pin(BitmapSource image, Int32Rect area)
    {
        // 缩放比按这块区域所在显示器算：跨屏选区时不能用「覆盖层那块屏」的比例
        var pin = new CapturePinWindow(image, area, ScreenShot.ScaleOf(area));
        Pins.Add(pin);
        pin.Show();          // 无激活窗口：ShowActivated=False + WS_EX_NOACTIVATE
        pin.RaiseToTop();
        return pin;
    }

    /// <summary>全关（右键菜单「关闭全部贴图」）。</summary>
    public static void CloseAll()
    {
        EndMove();
        foreach (var pin in Pins.ToArray()) pin.Close();
        Pins.Clear();
    }

    /// <summary>把贴图顶回最前（面板 / 悬浮抢到前面之后调；不抢焦点）。</summary>
    public static void RaiseAll()
    {
        foreach (var pin in Pins) pin.RaiseToTop();
    }

    // ============ 拖动 / 成组移动 ============

    /// <summary>
    /// 拖动开始：记下这次的参与者与它们的落点。
    /// 成组移动开着就是全组，否则只有自己——<see cref="MoveBy"/> 因此只有一条路径。
    /// </summary>
    public static void BeginMove(CapturePinWindow source)
    {
        MoveBase.Clear();
        _moving = source;

        var participants = GroupMove ? Pins.ToArray() : new[] { source };
        foreach (var pin in participants)
            MoveBase.Add((pin, new Rect(pin.Left, pin.Top, pin.Width, pin.Height)));
    }

    /// <summary>
    /// 拖到哪：位移先按整组夹一次（谁都不许被推出桌面），再按「起点 + 位移」写回。
    /// 松手前重复调用也只累一个位移，不会越拖越飘。
    /// </summary>
    public static void MoveBy(CapturePinWindow source, double dx, double dy)
    {
        if (!ReferenceEquals(_moving, source) || MoveBase.Count == 0) return;

        var boxes = new Rect[MoveBase.Count];
        for (int i = 0; i < boxes.Length; i++) boxes[i] = MoveBase[i].Box;

        Vector delta = PinMath.ConstrainGroupMove(new Vector(dx, dy), boxes, PinMath.VirtualDesktop,
            PinMath.KeepVisible);

        foreach (var (pin, box) in MoveBase)
        {
            pin.Left = box.Left + delta.X;
            pin.Top = box.Top + delta.Y;
        }
    }

    /// <summary>松手：丢掉快照（下一次拖动重新取起点）。</summary>
    public static void EndMove()
    {
        _moving = null;
        MoveBase.Clear();
    }

    /// <summary>窗口关闭时摘掉自己（重复调用无害）。</summary>
    internal static void Forget(CapturePinWindow pin)
    {
        Pins.Remove(pin);
        MoveBase.RemoveAll(entry => ReferenceEquals(entry.Pin, pin));
        if (ReferenceEquals(_moving, pin)) EndMove();
    }
}
