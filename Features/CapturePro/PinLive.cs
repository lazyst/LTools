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
/// 实时贴图的纯逻辑：源窗口状态判定、把窗口整图裁到选区那块、状态徽标文案。
/// </summary>
/// <remarks>
/// <para>
/// 贴图画布**永远是选区那块矩形**（与普通贴图同一个坐标系）：源窗口挪开之后，露出来的地方留透明，
/// 不跟着窗口走——否则一拖窗口贴图自己也跑了，用户找不到它。
/// </para>
/// <para>
/// 抓帧是 <see cref="ScreenShot.CaptureWindow"/>（<c>PrintWindow</c> 整窗 + 按
/// <c>GetWindowRect</c> 同一套几何裁剪），所以「窗口里 (10,20) 那个像素」落在屏幕的哪个位置
/// 与画布偏移严格一致。窗口只露一半时裁出来只有半张，其余透明。
/// </para>
/// </remarks>
internal static class PinLive
{
    /// <summary>抓帧间隔（5 fps）。再快只是白烧 CPU，滚动 / 视频的观感也够了。</summary>
    public const int IntervalMs = 200;

    /// <summary>源窗口现在的状态（<paramref name="exists"/> = <c>IsWindow</c>，<paramref name="minimized"/> = <c>IsIconic</c>）。</summary>
    public static LiveState StateOf(bool exists, bool minimized)
    {
        if (!exists) return LiveState.Closed;
        return minimized ? LiveState.Minimized : LiveState.Live;
    }

    /// <summary>这个状态要不要真去抓帧（最小化时抓到的多半是旧画面，跳过并挂徽标）。</summary>
    public static bool ShouldCapture(LiveState state) => state == LiveState.Live;

    /// <summary>
    /// 源窗口整图 → 画布：返回「从窗口哪儿裁」与「贴到画布哪儿」；不相交（窗口完全挪开）时返回 <c>null</c>，
    /// 调用方交一张全透明的画布（贴图还在原地，只是暂时什么都看不见）。
    /// </summary>
    public static LiveCrop? Crop(Int32Rect window, Int32Rect area)
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

    /// <summary>右下角那枚状态徽标写什么。</summary>
    public static string Badge(LiveState state) => state switch
    {
        LiveState.Minimized => "源窗口已最小化",
        LiveState.Closed => "源窗口已关闭",
        _ => "● 实时",
    };
}
