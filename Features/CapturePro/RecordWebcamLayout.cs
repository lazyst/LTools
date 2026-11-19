using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>空闲期摄像头预览上的四个方向按钮（照上游 <c>orientationRequested</c> 的 0…3）。</summary>
internal enum RecordWebcamAction
{
    /// <summary>顺时针转 90°。</summary>
    rotateClockwise,

    /// <summary>逆时针转 90°。</summary>
    rotateCounterclockwise,

    /// <summary>左右翻转。</summary>
    flipHorizontal,

    /// <summary>上下翻转。</summary>
    flipVertical,
}

/// <summary>
/// 录屏空闲期「摄像头画中画可拖 / 可缩放」的纯几何（照上游
/// <c>presentation/recording/recordingwebcamcontrol.cpp</c> 与
/// <c>snow-recording-model/src/webcam.rs</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 坐标一律是**屏幕物理像素**（与录屏区域 / 鼠标注入同一套）；只有 <c>scale</c> 是「DIP → 物理像素」。
/// 上游的起点是 `initialFittedOffset`（落盘边距已经按区域夹过），这里的
/// <see cref="OffsetForMove"/> 也按「夹过的起点 + 位移」算，所以拖到边上不会越拖越远。
/// </para>
/// <para>
/// 缩放用上游同一个式子：<c>size = clamp(initialSide + (dx − dy) / 2, 50, 500)</c>（拖动右下角对角线），
/// 边距保持不动。所有函数都不碰窗口，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class RecordWebcamLayout
{
    /// <summary>缩放把手的边长（DIP，上游 32）。</summary>
    public const double HandleSize = 32;

    /// <summary>方向工具条与画中画的间距（DIP，上游 6）。</summary>
    public const int ToolbarGap = 6;

    /// <summary>方向工具条里每个按钮的边长（DIP，antd <c>SizeClass::Small</c> 的 controlHeightSM = 24）。</summary>
    public const int ToolbarButton = 24;

    /// <summary>工具条内边距（DIP，上游 contentsMargins 3,2,3,2）。</summary>
    public const int ToolbarPadX = 3;
    public const int ToolbarPadY = 2;

    /// <summary>方向按钮个数（旋转顺时针 / 逆时针 / 左右翻 / 上下翻）。</summary>
    public const int OrientationButtons = 4;

    public static bool IsEmpty(Int32Rect rect) => rect.Width <= 0 || rect.Height <= 0;

    /// <summary>真正画出来的边长：取请求值与区域短边里的小者（上游 <c>placement</c>）。</summary>
    public static int FittedSide(Int32Rect area, int size) =>
        Math.Max(1, Math.Min(size, Math.Min(area.Width, area.Height)));

    /// <summary>画中画在屏幕上的矩形（物理像素）。</summary>
    public static Int32Rect PreviewRect(Int32Rect area, int size, int offsetLeft, int offsetBottom)
    {
        var (x, y, side) = RecordOptions.CameraPlacement(
            area.Width, area.Height, size, offsetLeft, offsetBottom);
        return new Int32Rect(area.X + x, area.Y + y, side, side);
    }

    /// <summary>
    /// 落盘边距按区域夹过之后的「实际边距」（上游 <c>placement</c> 的 fitted offset）：
    /// 拖动是从这个值起算的，所以区域比画中画还小、或者边距写大了都不会一拖就跳。
    /// </summary>
    public static (int Left, int Bottom) FittedOffset(Int32Rect area, int size, int left, int bottom)
    {
        if (IsEmpty(area)) return (Math.Max(0, left), Math.Max(0, bottom));

        int side = FittedSide(area, size);
        return (
            Math.Clamp(left, 0, Math.Max(0, area.Width - side)),
            Math.Clamp(bottom, 0, Math.Max(0, area.Height - side)));
    }

    /// <summary>
    /// 拖动后的边距（上游 <c>updateInteraction</c> 的非缩放分支）：按夹过的起点加位移再夹进区域，
    /// 纵向用「离底边」所以鼠标往上 → 边距变大。
    /// </summary>
    public static (int Left, int Bottom) OffsetForMove(Int32Rect area, int size,
        int startLeft, int startBottom, int dx, int dy)
    {
        if (IsEmpty(area)) return (Math.Max(0, startLeft), Math.Max(0, startBottom));

        int side = FittedSide(area, size);
        int maxLeft = Math.Max(0, area.Width - side);
        int maxBottom = Math.Max(0, area.Height - side);

        int left = Math.Clamp(Math.Max(0, startLeft) + dx, 0, maxLeft);
        int bottom = Math.Clamp(Math.Max(0, startBottom) - dy, 0, maxBottom);
        return (left, bottom);
    }

    /// <summary>缩放后的边长（上游：<c>initialSide + (dx − dy) / 2</c>，夹到 50–500）。</summary>
    public static int SizeForResize(int startSize, int dx, int dy)
    {
        double delta = (dx - dy) / 2.0;
        int size = (int)Math.Round(startSize + delta, MidpointRounding.AwayFromZero);
        return Math.Clamp(size, RecordOptions.MinCameraSize, RecordOptions.MaxCameraSize);
    }

    /// <summary>
    /// 缩放的起点：上游用的是「已经按区域夹过」的边长（<c>initialSide = min(size, region)</c>），
    /// 所以区域很短时从那个值开始长，不会跳一下。
    /// </summary>
    public static int ResizeAnchor(Int32Rect area, int size) =>
        IsEmpty(area) ? Math.Max(RecordOptions.MinCameraSize, size) : FittedSide(area, size);

    /// <summary>缩放把手矩形（物理像素）：贴着画中画右上角、朝内，边长夹到画中画本身大小。</summary>
    public static Int32Rect HandleRect(Int32Rect preview, double scale)
    {
        if (IsEmpty(preview)) return default;

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;
        int side = Math.Clamp((int)Math.Round(HandleSize * scale, MidpointRounding.AwayFromZero),
            1, Math.Min(preview.Width, preview.Height));

        return new Int32Rect(preview.X + preview.Width - side, preview.Y, side, side);
    }

    /// <summary>方向工具条的尺寸（物理像素，照上游 4 个 24 DIP 按钮 + 3,2,3,2 内边距）。</summary>
    public static (int Width, int Height) ToolbarSize(double scale)
    {
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;
        int button = Math.Max(1, (int)Math.Round(ToolbarButton * scale, MidpointRounding.AwayFromZero));
        int padX = Math.Max(1, (int)Math.Round(ToolbarPadX * scale, MidpointRounding.AwayFromZero));
        int padY = Math.Max(1, (int)Math.Round(ToolbarPadY * scale, MidpointRounding.AwayFromZero));
        return (button * OrientationButtons + padX * 2, button + padY * 2);
    }

    /// <summary>
    /// 方向工具条落在哪儿（物理像素）：贴在画中画正上方 6 DIP 居中；上面放不下就翻到下方；
    /// 横向夹回屏幕里（上游 <c>updateToolbar</c>）。
    /// </summary>
    public static Int32Rect ToolbarRect(Int32Rect preview, Int32Rect screen, double scale)
    {
        if (IsEmpty(preview) || IsEmpty(screen)) return default;

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;
        var (width, height) = ToolbarSize(scale);
        int gap = Math.Max(1, (int)Math.Round(ToolbarGap * scale, MidpointRounding.AwayFromZero));

        int x = preview.X + (preview.Width - width) / 2;
        int maxX = Math.Max(screen.X, screen.X + screen.Width - width);
        x = Math.Clamp(x, screen.X, maxX);

        int y = preview.Y - height - gap;
        if (y < screen.Y) y = preview.Y + preview.Height + gap;
        int maxY = Math.Max(screen.Y, screen.Y + screen.Height - height);
        y = Math.Clamp(y, screen.Y, maxY);

        return new Int32Rect(x, y, width, height);
    }

    /// <summary>
    /// 四个方向按钮的换算（上游 <c>screenrecordingcontroller.cpp</c> 的 <c>webcamOrientationRequested</c>）：
    /// 旋转只动低两位；两个翻转同时改镜像位并把圈数改成「镜面后的等价角度」。
    /// </summary>
    public static RecordCameraOrientation Next(RecordCameraOrientation current, RecordWebcamAction action)
    {
        int raw = (int)current & 7;
        int quarter = raw & 3;
        int mirror = raw & 4;

        int next = action switch
        {
            RecordWebcamAction.rotateClockwise => ((quarter + 1) & 3) | mirror,
            RecordWebcamAction.rotateCounterclockwise => ((quarter + 3) & 3) | mirror,
            RecordWebcamAction.flipHorizontal => ((4 - quarter) & 3) | (mirror ^ 4),
            RecordWebcamAction.flipVertical => ((6 - quarter) & 3) | (mirror ^ 4),
            _ => raw,
        };

        return (RecordCameraOrientation)(next & 7);
    }

    /// <summary>方向按钮的提示文案（悬停与无障碍名，照上游 <c>retranslateUi</c>）。</summary>
    public static string ActionLabel(RecordWebcamAction action) => action switch
    {
        RecordWebcamAction.rotateClockwise => "顺时针转 90°",
        RecordWebcamAction.rotateCounterclockwise => "逆时针转 90°",
        RecordWebcamAction.flipHorizontal => "左右翻转",
        RecordWebcamAction.flipVertical => "上下翻转",
        _ => "方向",
    };

    /// <summary>方向按钮的图标资源键（照上游四个图标）。</summary>
    public static string ActionIcon(RecordWebcamAction action) => action switch
    {
        RecordWebcamAction.rotateClockwise => "IconRotateRight",
        RecordWebcamAction.rotateCounterclockwise => "IconRotateLeft",
        RecordWebcamAction.flipHorizontal => "IconFlipHorizontal",
        RecordWebcamAction.flipVertical => "IconFlipVertical",
        _ => "IconRecCamera",
    };

    /// <summary>当前方向的短说明（设置页 / 提示用）。</summary>
    public static string Describe(RecordCameraOrientation orientation)
    {
        int raw = (int)orientation & 7;
        string rotation = (raw & 3) switch
        {
            1 => "顺时针 90°",
            2 => "180°",
            3 => "逆时针 90°",
            _ => "原方向",
        };

        return (raw & 4) != 0 ? "左右翻转 · " + rotation : rotation;
    }
}
