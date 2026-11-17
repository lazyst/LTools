using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>实时贴图这一拍看到的状态：正在跟 / 源窗口最小化 / 源窗口没了。</summary>
public enum LiveState
{
    Live,
    Minimized,
    Closed,
}

/// <summary>源窗口整图里该裁哪一块（<see cref="Source"/>）、贴到贴图画布的哪个位置（<see cref="Target"/>）。</summary>
internal readonly record struct LiveCrop(Int32Rect Source, Int32Rect Target);

/// <summary>
/// 贴图窗口相对它「要跟的那块」的固定偏移：那块怎么挪，贴图跟着挪同样的距离
/// （跟着走的是**位置**，不是尺寸——尺寸永远是当初截的那一块）。
/// </summary>
internal readonly record struct LiveAnchor(int Dx, int Dy)
{
    /// <summary>现在的相对位置就是锚点（进入实时时定一次；用户自己拖过贴图之后再定一次）。</summary>
    public static LiveAnchor Of(Int32Rect pin, Int32Rect anchor) =>
        new(pin.X - anchor.X, pin.Y - anchor.Y);

    /// <summary>锚点在这个矩形时，贴图窗口该落在哪（尺寸保持 <paramref name="width"/>×<paramref name="height"/>）。</summary>
    public Int32Rect Place(Int32Rect anchor, int width, int height) =>
        new(anchor.X + Dx, anchor.Y + Dy, width, height);
}

/// <summary>
/// 实时贴图的纯逻辑：源窗口状态判定、跟着源窗口挪、把窗口里的那一块铺到贴图画布、状态徽标文案。
/// </summary>
/// <remarks>
/// <para>
/// 实时跟的是**源窗口里的那一块**（<paramref name="region"/>，窗口坐标）：贴图窗口跟着源窗口走
/// （照 Snow Shot 的 <c>livewindowpresentation.cpp</c>，宿主每次 <c>WM_WINDOWPOSCHANGED</c> 就
/// <c>SetWindowPos</c> 贴上去），所以用户拖着源窗口走，贴图里的画面跟着一起走、还是同一块内容。
/// 用户自己拖贴图不改变「显示哪一块」——画面永远从贴图左上角铺开（贴图是一张随时更新的贴图，
/// 不是源窗口上的取景框）。
/// </para>
/// <para>
/// 抓帧是 <see cref="ScreenShot.CaptureWindow"/>（<c>PrintWindow</c> 整窗 + 按
/// <c>GetWindowRect</c> 同一套几何裁剪），所以「窗口里 (10,20) 那个像素」落在屏幕的哪个位置
/// 与画布偏移严格一致。窗口变小 / 那块被窗口边缘切掉时，露不出来的边留透明。
/// </para>
/// </remarks>
internal static class PinLive
{
    /// <summary>抓帧间隔（5 fps）。再快只是白烧 CPU，滚动 / 视频的观感也够了。</summary>
    public const int IntervalMs = 200;

    /// <summary>
    /// 源窗口「没了」要连续看到几拍才认（3 拍 = 600ms）。瞬时读不到（窗口正在销毁重建、
    /// 系统忙、句柄刚好在这一拍失效）不该把贴图收掉——用户报过「最小化再打开，实时贴图就自动关闭了」。
    /// </summary>
    public const int GoneTicks = 3;

    /// <summary>源窗口现在的状态（<paramref name="exists"/> = <c>IsWindow</c>，<paramref name="minimized"/> = <c>IsIconic</c>）。</summary>
    public static LiveState StateOf(bool exists, bool minimized)
    {
        if (!exists) return LiveState.Closed;
        return minimized ? LiveState.Minimized : LiveState.Live;
    }

    /// <summary>这个状态要不要真去抓帧（最小化时抓到的多半是旧画面，跳过并挂徽标）。</summary>
    public static bool ShouldCapture(LiveState state) => state == LiveState.Live;

    /// <summary>
    /// 源窗口这一拍的矩形能不能当作「跟住的那块现在在哪」的基准。
    /// </summary>
    /// <param name="window">这一拍的窗口矩形（物理像素）。</param>
    /// <param name="minimized">窗口这一拍是不是最小化（<c>IsIconic</c>）。</param>
    /// <param name="desktop">虚拟桌面矩形；给了就要求窗口至少有一角落在桌面里。</param>
    /// <remarks>
    /// 最小化时 <c>GetWindowRect</c> 给的是 (-32000,-32000) 的哨兵位置、尺寸也只剩标题栏：
    /// 拿它算锚点会把贴图甩到屏幕外（用户报过「最小化再打开，实时贴图就没了」），
    /// 所以最小化期间「那块在哪」沿用上一次的好值，窗口还原之后再接着跟。
    /// 哨兵矩形离开桌面十万八千里，所以「跟桌面有交集」这条就够把它挡在门外——
    /// **别去比较 -32000 这个具体数字**：那是 Win32 的实现细节，换个系统 / DPI 就不一定一样。
    /// </remarks>
    public static bool Trackable(Int32Rect window, bool minimized, Int32Rect desktop = default) =>
        !minimized && window.Width > 0 && window.Height > 0 &&
        (desktop.Width <= 0 || Overlap(window, desktop) > 0);

    /// <summary>最小化占位里放不放文字：太小的贴图只留图标（两行字会被裁成一团）。</summary>
    public static bool CoverShowsText(double width, double height) => width >= 96 && height >= 56;

    /// <summary>
    /// 同程序的另一个窗口够不够格接替「源窗口句柄没了」——它得盖住跟住的那块的一半以上。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只按「同一个程序」判断太松：用户关掉一个 Chrome 窗口之后同进程还有别的窗口，
    /// 随便挑一个顶上会让贴图悄悄跟到完全不相干的窗口上（看着像「关掉的窗口还活着」）。
    /// 真正会重建自己窗口的程序都是**原地重建**，所以要求几何重合就够区分了。
    /// </para>
    /// <para>
    /// 另一半证据在调用方：类名相同、不是工具窗口 / 不抢焦点的窗口（那样能排除我们自己的
    /// 缩略图宿主与其它贴图），所以这里只看几何。
    /// </para>
    /// </remarks>
    public static bool CanSucceed(Int32Rect block, Int32Rect window)
    {
        if (block.Width <= 0 || block.Height <= 0) return false;

        long area = (long)block.Width * block.Height;
        return Overlap(block, window) * 2 >= area;
    }

    /// <summary>两个矩形的重叠面积（不相交为 0）。</summary>
    public static long Overlap(Int32Rect a, Int32Rect b)
    {
        int left = Math.Max(a.X, b.X);
        int top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right - left <= 0 || bottom - top <= 0) return 0;
        return (long)(right - left) * (bottom - top);
    }

    /// <summary>源窗口里的那一块（窗口坐标）在屏幕上落在哪。</summary>
    public static Int32Rect Screen(Int32Rect window, Int32Rect region) =>
        new(window.X + region.X, window.Y + region.Y, region.Width, region.Height);

    /// <summary>
    /// 窗口整图 → 画布（两者都是屏幕矩形）：返回「从窗口哪儿裁」与「贴到画布哪儿」；
    /// 不相交时返回 <c>null</c>，调用方交一张全透明的画布。
    /// </summary>
    public static LiveCrop? Intersect(Int32Rect window, Int32Rect area)
    {
        int left = Math.Max(window.X, area.X);
        int top = Math.Max(window.Y, area.Y);
        int right = Math.Min(window.X + window.Width, area.X + area.Width);
        int bottom = Math.Min(window.Y + window.Height, area.Y + area.Height);
        if (right - left <= 0 || bottom - top <= 0) return null;

        var source = new Int32Rect(left - window.X, top - window.Y, right - left, bottom - top);
        var target = new Int32Rect(left - area.X, top - area.Y, right - left, bottom - top);
        return new LiveCrop(source, target);
    }

    /// <summary>
    /// 固定区块 → 画布：<paramref name="region"/> 是源窗口里跟住的那一块（窗口坐标，进实时时定下），
    /// 画布**永远从贴图左上角铺开**（拖贴图不改变显示哪一块）。
    /// </summary>
    /// <remarks>
    /// 窗口缩到比那块还小 / 那块有一部分在窗口外时，只画露得出来的部分，露不出来的边留在画布上透明；
    /// 整块都在窗口外返回 <c>null</c>。
    /// </remarks>
    public static LiveCrop? Region(Int32Rect window, Int32Rect region) =>
        Intersect(window, Screen(window, region));

    /// <summary>右下角那枚状态徽标写什么。</summary>
    public static string Badge(LiveState state) => state switch
    {
        LiveState.Minimized => "源窗口已最小化",
        LiveState.Closed => "源窗口已关闭",
        _ => "● 实时",
    };
}
