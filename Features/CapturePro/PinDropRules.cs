using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>拖放能不能落到这张贴图上（照上游 <c>ScreenshotPinnedWindow::acceptsDrop</c>）。</summary>
internal enum PinDropVerdict
{
    /// <summary>可以替换内容。</summary>
    Accept,

    /// <summary>实时贴图：内容跟着源窗口刷新，拖进来的东西留不住。</summary>
    Live,

    /// <summary>点击穿透态：鼠标根本到不了贴图。</summary>
    ClickThrough,

    /// <summary>正在往外拖内容，别再收别人的拖放。</summary>
    Exporting,

    /// <summary>拖的东西里没有能贴的（不支持的扩展名 / 空文本）。</summary>
    Empty,
}

/// <summary>替换内容的结果：换成功，或因为什么被拒。</summary>
internal enum PinReplaceReject
{
    None,

    /// <summary>实时贴图。</summary>
    Live,

    /// <summary>贴图锁着，而新内容尺寸不同（会动窗口几何）。</summary>
    Locked,
}

/// <summary>替换内容的几何结论（纯数据，自检直接断言）。</summary>
internal readonly record struct PinReplacePlan(
    PinReplaceReject Reject,
    double BaseWidth,
    double BaseHeight,
    double Left,
    double Top,
    double Width,
    double Height,
    bool Resized);

/// <summary>
/// 贴图拖放与「替换内容」的纯规则：相不相信这次拖放、换了内容窗口该多大、锁着能不能换。
/// </summary>
/// <remarks>
/// <para>
/// 判定照上游：<c>acceptsDrop</c>（实时 / 穿透 / 正在拖出 / 必须是 Copy / 内容认得出来）与
/// <c>replaceContent</c>（实时拒绝；锁定且尺寸变化拒绝；尺寸不变时几何原样；变了按中心放大缩小，
/// 仍夹在桌面里）。窗口不动的那一半全在这里，自检能直接喂边界值。
/// </para>
/// <para>
/// 「相不相信」只按拖进来的内容算，不去读文件——真正的内容解码在替换那一刻做（与上游
/// <c>screenshotDropFilePaths</c> 只看 MIME 与后缀一致）。
/// </para>
/// </remarks>
internal static class PinDropRules
{
    /// <summary>这次拖放该不该收。（文件优先，其次位图，最后文本。）</summary>
    public static PinDropVerdict Check(bool live, bool clickThrough, bool exporting,
        bool hasFiles, bool hasBitmap, bool hasText, bool filesSupported)
    {
        if (live) return PinDropVerdict.Live;
        if (clickThrough) return PinDropVerdict.ClickThrough;
        if (exporting) return PinDropVerdict.Exporting;

        if (hasFiles) return filesSupported ? PinDropVerdict.Accept : PinDropVerdict.Empty;
        if (hasBitmap) return PinDropVerdict.Accept;
        if (hasText) return PinDropVerdict.Accept;
        return PinDropVerdict.Empty;
    }

    /// <summary>
    /// 换了内容之后窗口该多大 / 在哪。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 缩略图与局部显示保持窗口尺寸（缩略图里内容本来就会等比缩进方块；取景框不跟图走），
    /// 只换 100% 基准尺寸。普通贴图按**中心**长到新尺寸，再用 <see cref="PinMath.ConstrainGroupMove"/>
    /// 夹进桌面（与 <c>ResizeCentered</c> 同一口径）。
    /// </para>
    /// <para>
    /// 锁定只锁「位置 / 尺寸」：新内容尺寸一模一样（同一张图重贴、同尺寸替换）照样能换，
    /// 尺寸变了才拒——否则换一次内容贴图就跑了。
    /// </para>
    /// </remarks>
    public static PinReplacePlan Plan(double newBaseWidth, double newBaseHeight, double zoom,
        double left, double top, double width, double height,
        bool locked, bool viewport, bool thumbnail, Rect desktop)
    {
        newBaseWidth = Math.Max(1, newBaseWidth);
        newBaseHeight = Math.Max(1, newBaseHeight);
        if (zoom <= 0) zoom = 1;

        if (thumbnail || viewport)
        {
            return new PinReplacePlan(PinReplaceReject.None, newBaseWidth, newBaseHeight,
                left, top, Math.Max(1, width), Math.Max(1, height), false);
        }

        double targetWidth = Math.Max(1, newBaseWidth * zoom);
        double targetHeight = Math.Max(1, newBaseHeight * zoom);

        bool sameSize = Math.Abs(targetWidth - width) <= 0.5 && Math.Abs(targetHeight - height) <= 0.5;
        if (locked && !sameSize)
        {
            return new PinReplacePlan(PinReplaceReject.Locked, newBaseWidth, newBaseHeight,
                left, top, Math.Max(1, width), Math.Max(1, height), false);
        }

        if (sameSize)
        {
            return new PinReplacePlan(PinReplaceReject.None, newBaseWidth, newBaseHeight,
                left, top, targetWidth, targetHeight, false);
        }

        var box = new Rect(
            left + width / 2 - targetWidth / 2,
            top + height / 2 - targetHeight / 2,
            targetWidth, targetHeight);

        Vector delta = PinMath.ConstrainGroupMove(new Vector(0, 0), new[] { box },
            desktop, PinMath.KeepVisible);

        return new PinReplacePlan(PinReplaceReject.None, newBaseWidth, newBaseHeight,
            box.X + delta.X, box.Y + delta.Y, targetWidth, targetHeight, true);
    }

    /// <summary>拒绝原因的中文说明（toast 用）。</summary>
    public static string Describe(PinReplaceReject reject) => reject switch
    {
        PinReplaceReject.Live => "实时贴图不能替换内容（先「转为图像」）",
        PinReplaceReject.Locked => "贴图锁着，新内容尺寸不同；先解锁再换",
        _ => "",
    };
}
