using System;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>
/// 拼接用的两块画面合成：接边带的高度，以及「参考帧」的重排。
/// </summary>
/// <remarks>
/// 移植自 Snow Shot 的 <c>snow-stitch-images/src/compositor.rs</c>。原文件还有
/// <c>append_and_repaint</c> / <c>prepend_and_repaint</c>，那两个只服务「一次性把两帧合成大图」的调用方，
/// 增量拼接走的是分块画布（<see cref="TiledCanvas"/>），这里用不到，所以不搬。
/// </remarks>
internal static class StitchCompositor
{
    /// <summary>
    /// 接边带有多高：取「半屏」和「四分之一屏 + 位移」里更大的那个。
    /// </summary>
    /// <remarks>
    /// 这块带子是**重新画过**的一段重叠：不能窄于位移（否则新露出来的内容和旧内容之间会出现缝），
    /// 也不能只有一小条（否则固定页眉页脚、动画贴片会正好落在边界上，接缝一眼就能看出来）。
    /// </remarks>
    public static int BandHeight(int viewportExtent, int shift)
    {
        if (shift < 0) throw new ArgumentOutOfRangeException(nameof(shift), "位移必须是非负整数");

        int quarterPlusShift = viewportExtent / 4 + shift;
        int band = Math.Max(viewportExtent / 2, quarterPlusShift);
        if (band > viewportExtent)
            throw new ArgumentOutOfRangeException(nameof(shift), "接边带比一屏还高");
        return band;
    }

    /// <summary>
    /// 往下滚之后的「参考帧」：旧参考帧上面的内容保留，底部那截用这一帧的重画带换掉。
    /// </summary>
    /// <remarks>
    /// 参考帧永远是**一屏大小**：它代表「现在屏幕上应该是什么样子」，供下一次运动估计做比较。
    /// 位移 <paramref name="shift"/> = 这一帧相对上一帧往上滚了多少像素。
    /// </remarks>
    public static StitchFrame SynthesizeAppend(StitchFrame reference, StitchFrame incoming, StitchAxis axis, int shift)
    {
        int extent = reference.AxisExtent(axis);
        int band = BandHeight(extent, shift);
        int keep = extent - band;
        int referenceStart = shift;
        int incomingStart = extent - band;

        var result = StitchFrame.Create(reference.Width, reference.Height);
        if (axis == StitchAxis.Vertical)
        {
            CopyRows(reference, referenceStart, result, 0, keep);
            CopyRows(incoming, incomingStart, result, keep, band);
        }
        else
        {
            CopyColumns(reference, referenceStart, result, 0, keep);
            CopyColumns(incoming, incomingStart, result, keep, band);
        }

        return result;
    }

    /// <summary>
    /// 往上滚之后的「参考帧」：这一帧顶部的重画带盖住旧的，旧参考帧下面那截接在后面。
    /// </summary>
    public static StitchFrame SynthesizePrepend(StitchFrame reference, StitchFrame incoming, StitchAxis axis, int shift)
    {
        int extent = reference.AxisExtent(axis);
        int band = BandHeight(extent, shift);
        int keep = extent - band;
        int referenceStart = band - shift;

        var result = StitchFrame.Create(reference.Width, reference.Height);
        if (axis == StitchAxis.Vertical)
        {
            CopyRows(incoming, 0, result, 0, band);
            CopyRows(reference, referenceStart, result, band, keep);
        }
        else
        {
            CopyColumns(incoming, 0, result, 0, band);
            CopyColumns(reference, referenceStart, result, band, keep);
        }

        return result;
    }

    /// <summary>把 <paramref name="source"/> 的若干行搬到 <paramref name="target"/> 的第 <paramref name="targetRow"/> 行起。</summary>
    public static void CopyRows(StitchFrame source, int sourceRow, StitchFrame target, int targetRow, int count)
    {
        if (count <= 0) return;
        Buffer.BlockCopy(source.Pixels, sourceRow * source.RowBytes, target.Pixels, targetRow * target.RowBytes, count * target.RowBytes);
    }

    /// <summary>把 <paramref name="source"/> 的若干列搬到 <paramref name="target"/> 的第 <paramref name="targetColumn"/> 列起。</summary>
    public static void CopyColumns(StitchFrame source, int sourceColumn, StitchFrame target, int targetColumn, int count)
    {
        if (count <= 0) return;

        int bytes = count * StitchFrame.Channels;
        for (int y = 0; y < target.Height; y++)
        {
            Buffer.BlockCopy(
                source.Pixels, (y * source.Width + sourceColumn) * StitchFrame.Channels,
                target.Pixels, (y * target.Width + targetColumn) * StitchFrame.Channels,
                bytes);
        }
    }
}
