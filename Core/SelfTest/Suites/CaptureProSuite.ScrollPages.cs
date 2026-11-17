using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Stitch;

namespace LTools.Core.SelfTest;

/// <summary>
/// 长截图：照 snow-shot 的 <c>estimator.rs</c> 夹具测「真实页面形态」。
/// </summary>
/// <remarks>
/// 原来只测「4×4 哈希纹理整页裁出来的帧」——干净、无固定条、无浮层、无动画，
/// 真实网页一条都不占。这里把 snow-shot 自带的几套夹具原样搬过来（同样的尺寸、同样的哈希、
/// 同样的滚动量），断言也照抄：<c>-24</c> / <c>+24</c> / <c>-17</c>，以及「动画不动就不许报位移」。
/// 它们守着的是「固定顶 / 底 / 侧栏 + 动态区 + 大浮层 + 重复横线 + 稀疏窄纹理 + 空白转文字」这些
/// 真页面里必然出现的东西，光靠合成纹理测不出来。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ScrollRealPages()
    {
        // 1. 固定页眉 40 行 / 页脚 32 行 / 左栏 64 px，中间在滚；右下角还有一块每帧都在变的动态区
        //    （视频 / 加载中的图）。这正是浏览器里一个普通页面。
        Check.Equal(-24, OffsetOf(ScrollingFixture(0, 0), ScrollingFixture(24, 1)),
            "固定顶 / 底 / 左栏 + 动态区：滚 24 行");

        // 2. 往回滚：同一对帧反过来喂
        Check.Equal(24, OffsetOf(ScrollingFixture(24, 0), ScrollingFixture(0, 1)),
            "往回滚 24 行");

        // 3. 横向页面：固定左 32 / 右 24 / 顶 32
        Check.Equal(-24, OffsetOf(StitchAxis.Horizontal,
                HorizontalScrollingFixture(0), HorizontalScrollingFixture(24)),
            "横向：固定左右栏 + 顶栏，往右 24 列");
        Check.Equal(24, OffsetOf(StitchAxis.Horizontal,
                HorizontalScrollingFixture(24), HorizontalScrollingFixture(0)),
            "横向：往回 24 列");

        // 4. 只有动态区在变、页面没滚：绝不能报出位移（报了就会接出一张错位的长图）
        Check.False(EstimateOf(ScrollingFixture(0, 0), ScrollingFixture(0, 1)).IsMotion,
            "只有一块画面在动 = 不是滚动");

        // 5. 连续滚 4 次（每次 16 行、动态区每次也变）：每次都认得出 -16
        var previous = ScrollingFixture(0, 0);
        var scrolling = new StitchEstimator(StitchAxis.Vertical, previous.Width, previous.Height);
        for (int step = 1; step <= 4; step++)
        {
            var incoming = ScrollingFixture((uint)(step * 16), (byte)step);
            var estimate = scrolling.Estimate(previous, previous, incoming);
            Check.Equal(-16, estimate.IsMotion ? estimate.Offset : 99,
                $"连续滚动第 {step} 次：-16（实际 {StageOf(estimate)}）");
            previous = incoming;
        }

        // 6. 方向 / 宽度 / 位移量：竖横 × 320·1024·2048 宽 × 17 px
        foreach (var axis in new[] { StitchAxis.Vertical, StitchAxis.Horizontal })
        {
            foreach (int cross in new[] { 320, 1024, 2048 })
            {
                var first = DirectionalFixture(axis, cross, 256, 0, false);
                var second = DirectionalFixture(axis, cross, 256, 17, false);
                Check.Equal(-17, OffsetOf(axis, first, second),
                    $"{AxisName(axis)} / 宽 {cross}：往下 17");
                Check.Equal(17, OffsetOf(axis, second, first),
                    $"{AxisName(axis)} / 宽 {cross}：往回 17");

                // 1 px 的位移在降采样下本来就说不准：只要求别把小位移报成大位移
                var tiny = OffsetOf(axis, first, DirectionalFixture(axis, cross, 256, 1, false));
                Check.True(tiny is null or 0 || Math.Abs(tiny.Value) <= 1,
                    $"{AxisName(axis)} / 宽 {cross}：1 px 不许报成 {tiny}");
            }
        }

        // 7. 稀疏窄纹理：内容只占 cross 400–560，其余是纯白（左右留白很大的正文页）
        foreach (var axis in new[] { StitchAxis.Vertical, StitchAxis.Horizontal })
        {
            Check.Equal(-17, OffsetOf(axis,
                    DirectionalFixture(axis, 2048, 320, 0, true),
                    DirectionalFixture(axis, 2048, 320, 17, true)),
                $"{AxisName(axis)}：只有一条窄纹理也要认出 -17");
        }

        // 8. 大固定浮层（cross < 128 / 主轴 < 32 那两条）+ 每 24 行一条重复横线：
        //    重复纹理最容易把位移认成 24 的倍数，四个候选位移都得是 -17。
        foreach (var axis in new[] { StitchAxis.Vertical, StitchAxis.Horizontal })
        {
            var stepPrevious = FixedOverlayFrame(axis, 0);
            var overlay = new StitchEstimator(axis, stepPrevious.Width, stepPrevious.Height);
            for (int step = 1; step <= 4; step++)
            {
                var incoming = FixedOverlayFrame(axis, (uint)(step * 17));
                var estimate = overlay.Estimate(stepPrevious, stepPrevious, incoming);
                Check.Equal(-17, estimate.IsMotion ? estimate.Offset : 99,
                    $"{AxisName(axis)} 大浮层 + 重复横线第 {step} 次：-17（实际 {StageOf(estimate)}）");
                stepPrevious = incoming;
            }
        }

        // 9. 窄纹理落在一块「看起来没变」的瓦片里：也要靠细化找出 -17，不能当成没动
        foreach (var axis in new[] { StitchAxis.Vertical, StitchAxis.Horizontal })
        {
            var reference = NarrowTextureFrame(axis, 0);
            var incoming = NarrowTextureFrame(axis, 17);
            var estimate = EstimateOf(axis, reference, incoming);
            Check.Equal(-17, estimate.IsMotion ? estimate.Offset : 99,
                $"{AxisName(axis)} 窄纹理在空白瓦片里：-17（实际 {StageOf(estimate)}）");
            Check.Equal(0f, estimate.Diagnostics.Candidates.Count > 0
                    ? estimate.Diagnostics.Candidates[0].PreciseAlignmentError ?? -1f
                    : -1f,
                $"{AxisName(axis)} 窄纹理：最强候选细化后零误差");
        }

        // 10. 空白 → 文字（页面顶部留白逐渐被文字顶上来）：每一步都要与「新建一个估计器」一致，
        //     也就是历史内容不许影响当前这一对的判断。
        foreach (var axis in new[] { StitchAxis.Vertical, StitchAxis.Horizontal })
        {
            var options = new MotionEstimatorOptions { MaxFeatures = 128 };
            var blankPrevious = BlankToTextFrame(axis, 0);
            var blank = new StitchEstimator(axis, blankPrevious.Width, blankPrevious.Height, options);
            uint previousScroll = 0;
            var steps = new uint[] { 32, 64, 96, 128, 160, 192, 224, 256, 224, 192, 160, 128, 96, 64, 32, 0 };
            foreach (uint scroll in steps)
            {
                var incoming = BlankToTextFrame(axis, scroll);
                var actual = blank.Estimate(blankPrevious, blankPrevious, incoming);

                var fresh = new StitchEstimator(axis, blankPrevious.Width, blankPrevious.Height, options);
                var expected = fresh.Estimate(blankPrevious, blankPrevious, incoming);

                int stepOffset = (int)previousScroll - (int)scroll;
                Check.True(expected.IsMotion && expected.Offset == stepOffset,
                    $"{AxisName(axis)} 空白转文字 scroll={scroll}：应为 {stepOffset}（实际 {StageOf(expected)} / {expected.Offset}）");
                Check.Equal(expected.Kind, actual.Kind,
                    $"{AxisName(axis)} 空白转文字 scroll={scroll}：复用估计器与新建结论一致");
                Check.Equal(expected.Offset, actual.Offset,
                    $"{AxisName(axis)} 空白转文字 scroll={scroll}：位移一致");

                blankPrevious = incoming;
                previousScroll = scroll;
            }
        }

        // 11. 两帧毫无关系（换了一页）：不许接。
        var firstPage = UnrelatedFixture(0);
        var sceneCut = new StitchEstimator(StitchAxis.Vertical, firstPage.Width, firstPage.Height);
        var cutPrevious = firstPage;
        MotionEstimate? lastCut = null;
        for (uint seed = 1; seed <= 3; seed++)
        {
            var incoming = UnrelatedFixture(seed);
            lastCut = sceneCut.Estimate(cutPrevious, cutPrevious, incoming);
            Check.False(lastCut.IsMotion, $"无关内容第 {seed} 次不许报位移（实际 {StageOf(lastCut)}）");
            cutPrevious = incoming;
        }

        Check.Equal(MotionStage.SceneCut, lastCut!.Diagnostics.Stage,
            "连续三次认不出 → 判定换页");
    }

    /// <summary>把一对帧喂给一个新的估计器，返回它认出的位移（<c>null</c> = 没认出位移）。</summary>
    private static int? OffsetOf(StitchFrame first, StitchFrame second) =>
        OffsetOf(StitchAxis.Vertical, first, second);

    private static int? OffsetOf(StitchAxis axis, StitchFrame first, StitchFrame second)
    {
        var estimate = EstimateOf(axis, first, second);
        return estimate.IsMotion ? estimate.Offset : null;
    }

    private static MotionEstimate EstimateOf(StitchFrame first, StitchFrame second) =>
        EstimateOf(StitchAxis.Vertical, first, second);

    private static MotionEstimate EstimateOf(StitchAxis axis, StitchFrame first, StitchFrame second)
    {
        var estimator = new StitchEstimator(axis, first.Width, first.Height);
        return estimator.Estimate(first, first, second, new EstimationCache());
    }

    private static string AxisName(StitchAxis axis) => axis == StitchAxis.Vertical ? "竖向" : "横向";

    /// <summary>
    /// 长截图回放：把一整页按 snow-shot 的 <c>stitch_test_frames.rs</c> 切成一帧帧
    /// （固定页眉 140 / 滚动切片 600 / 固定页脚 72，每步滚 125），再交给拼接器，
    /// 断言拼出来的图与整页**逐像素一致**。
    /// </summary>
    /// <remarks>
    /// 这是「端到端」的那一层：估计器自己认得对（<see cref="ScrollRealPages"/> 已经守住了），
    /// 拼起来仍可能错——接错行、固定条重复、参考帧合成偏一行、画布少接一截。
    /// snow-shot 的 <c>corpus_benchmark</c> / <c>stitch_test_frames</c> 就是这么验的：
    /// 不看到位移数字，只看最终那张图的像素。
    /// </remarks>
    private static void ScrollReplay()
    {
        const int fixedTop = 140;
        const int fixedBottom = 72;
        const int slice = 600;
        const int step = 125;

        var page = NewArticlePage(400, 2600);
        int scrolling = page.PixelHeight - fixedTop - fixedBottom;
        int lastStart = scrolling - slice;

        // ① 生产管线（带固定条，不裁）：snow-shot 走的就是这一条
        var stitcher = new StitchStitcher(StitchFrame.FromBitmap(ArticleFrame(page, 0, fixedTop, fixedBottom, slice)), StitchAxis.Vertical);
        for (int scroll = 0; scroll < lastStart;)
        {
            int next = Math.Min(lastStart, scroll + step);
            var decision = stitcher.Process(StitchFrame.FromBitmap(ArticleFrame(page, next, fixedTop, fixedBottom, slice)));
            Check.Equal(-(next - scroll), decision.AcceptedOffset ?? 99,
                $"回放（不裁固定条）第 {next} 行：接 {next - scroll} 行");
            scroll = next;
        }

        var direct = stitcher.Materialize();
        Check.Equal(page.PixelWidth, direct.Width, "回放（不裁固定条）：宽 = 整页宽");
        Check.Equal(page.PixelHeight, direct.Height, "回放（不裁固定条）：高 = 整页高");
        var directWrong = MismatchedRows(direct, page);
        Check.True(directWrong.Count == 0,
            $"回放（不裁固定条）：拼出来与整页逐像素一致（不匹配行 {string.Join(",", directWrong)}）");

        // ② 适配层（LTools 实际用的那条：剪掉末尾固定条再接）
        var stitch = new ScrollStitch();
        stitch.Start(ArticleFrame(page, 0, fixedTop, fixedBottom, slice));
        for (int scroll = 0; scroll < lastStart;)
        {
            int next = Math.Min(lastStart, scroll + step);
            Check.Equal(-(next - scroll), stitch.Append(ArticleFrame(page, next, fixedTop, fixedBottom, slice)) ?? 99,
                $"回放（适配层）第 {next} 行：接 {next - scroll} 行");
            scroll = next;
        }

        Check.Equal(fixedBottom, stitch.StickyBottom, "回放（适配层）：认出 72 行固定页脚");
        stitch.Finish();
        var result = stitch.Render();
        Check.Equal(page.PixelWidth, result.PixelWidth, "回放（适配层）：宽 = 整页宽");
        Check.Equal(page.PixelHeight, result.PixelHeight, "回放（适配层）：高 = 整页高");

        var wrong = MismatchedRows(StitchFrame.FromBitmap(result), page);
        Check.True(wrong.Count == 0,
            $"回放（适配层）：拼出来与整页逐像素一致（不匹配行 {string.Join(",", wrong)}）");

        // 固定页眉 / 页脚各只出现一次：成品长度正好等于整页高（多接一条就多一行）
        Check.Equal(page.PixelHeight, stitch.Extent, "成品长度 = 整页高（固定条只算一次）");
    }

    /// <summary>
    /// <c>stitch_test_frames.rs</c> 的 <c>SplitFrames::frame_at</c>：
    /// 固定页眉（整页最上面那几行）+ 从 <c>fixedTop + scroll</c> 起的 <paramref name="slice"/> 行 + 固定页脚。
    /// </summary>
    private static BitmapSource ArticleFrame(BitmapSource page, int scroll, int fixedTop, int fixedBottom, int slice)
    {
        int height = fixedTop + slice + fixedBottom;
        int stride = page.PixelWidth * 4;
        var source = new byte[stride * page.PixelHeight];
        page.CopyPixels(source, stride, 0);

        var pixels = new byte[stride * height];
        Buffer.BlockCopy(source, 0, pixels, 0, stride * fixedTop);
        Buffer.BlockCopy(source, stride * (fixedTop + scroll), pixels, stride * fixedTop, stride * slice);
        Buffer.BlockCopy(source, stride * (page.PixelHeight - fixedBottom), pixels, stride * (fixedTop + slice),
            stride * fixedBottom);

        var frame = BitmapSource.Create(page.PixelWidth, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>拼出来的帧与整页不匹配的行号（最多列 12 个，够定位就行）。</summary>
    private static List<int> MismatchedRows(StitchFrame result, BitmapSource page)
    {
        var expected = StitchFrame.FromBitmap(page);
        var wrong = new List<int>();
        int rows = Math.Min(result.Height, expected.Height);
        for (int y = 0; y < rows; y++)
        {
            int offset = y * result.RowBytes;
            var actualRow = new ReadOnlySpan<byte>(result.Pixels, offset, result.RowBytes);
            var expectedRow = new ReadOnlySpan<byte>(expected.Pixels, y * expected.RowBytes, expected.RowBytes);
            if (!actualRow.SequenceEqual(expectedRow) && wrong.Count < 12) wrong.Add(y);
            if (wrong.Count >= 12) break;
        }

        if (wrong.Count == 0 && (result.Height != expected.Height || result.Width != expected.Width))
            wrong.Add(-1); // 尺寸不一致
        return wrong;
    }

    /// <summary>
    /// 合成一张像真网页的整页图：140 行页眉（标题 / 导航）、正文（每 22 行一条文字线、偶尔一张配图、
    /// 段落序号）、72 行页脚（按钮条）。文字线由哈希决定长度，整页没有两条一样的行。
    /// </summary>
    private static BitmapSource NewArticlePage(int width, int height)
    {
        const int fixedTop = 140;
        const int fixedBottom = 72;

        int stride = width * 4;
        var pixels = new byte[stride * height];
        FillRows(pixels, stride, 0, height, 0xFF, 0xFF, 0xFF);

        // 页眉：深色导航条 + 标题块
        FillRows(pixels, stride, 0, fixedTop, 0x2A, 0x2A, 0x30);
        FillRows(pixels, stride, 12, 24, 0x50, 0x50, 0x58);
        for (int x = 20; x < width - 20; x += 44)
            FillRect(pixels, stride, 96, 104, 0xFF, 0xFF, 0xFF, x, 26);
        FillRect(pixels, stride, 20, 28, 0xFF, 0xFF, 0xFF, 20, 180);

        // 正文：一行一条文字线，长度和位置由 (行号, 列) 哈希决定
        int bodyEnd = height - fixedBottom;
        for (int y = fixedTop + 10; y < bodyEnd - 6; y += 22)
        {
            int line = (y - fixedTop) / 22;
            int left = 24 + (int)(TileHash(0, line, 3) & 0x0F);
            int length = 120 + TileHash(1, line, 5) % (width - left - 60);
            int thickness = 5 + TileHash(2, line, 7) % 4;
            byte shade = (byte)(0x28 + TileHash(3, line, 11) % 0x40);
            FillRect(pixels, stride, y, y + thickness, shade, shade, shade, left, length);
        }

        // 每 400 行插一张配图（彩色块，给 ORB 一点结构）
        for (int y = fixedTop + 240; y < bodyEnd - 200; y += 400)
        {
            for (int dy = 0; dy < 140; dy++)
            {
                for (int dx = 0; dx < width - 96; dx++)
                {
                    int i = (y + dy) * stride + (48 + dx) * 4;
                    pixels[i] = (byte)(dx * 3);
                    pixels[i + 1] = (byte)(dy * 2);
                    pixels[i + 2] = (byte)((dx + dy) * 2);
                }
            }
        }

        // 页脚：浅灰条 + 三个按钮
        FillRows(pixels, stride, bodyEnd, height, 0xEE, 0xEE, 0xF0);
        FillRows(pixels, stride, bodyEnd, bodyEnd + 2, 0xC8, 0xC8, 0xD0);
        for (int i = 0; i < 3; i++)
        {
            int left = 60 + i * 110;
            FillRect(pixels, stride, bodyEnd + 18, bodyEnd + 50, 0x38, 0x38, 0x40, left, 84);
            FillRect(pixels, stride, bodyEnd + 28, bodyEnd + 40, 0xFF, 0xFF, 0xFF, left + 8, 68);
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    private static void FillRows(byte[] pixels, int stride, int start, int end, byte r, byte g, byte b) =>
        FillRect(pixels, stride, start, end, r, g, b, 0, 0);

    /// <summary>把 [<paramref name="start"/>, <paramref name="end"/>) 行里从 <paramref name="left"/> 起的一段填成一种颜色。</summary>
    private static void FillRect(byte[] pixels, int stride, int start, int end, byte r, byte g, byte b,
        int left, int width)
    {
        int rows = pixels.Length / stride;
        int rowWidth = stride / 4;
        int from = Math.Clamp(left, 0, rowWidth);
        int to = width > 0 ? Math.Clamp(left + width, 0, rowWidth) : rowWidth;

        for (int y = Math.Max(0, start); y < Math.Min(end, rows); y++)
        {
            for (int x = from; x < to; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = 0xFF;
            }
        }
    }

    private static string StageOf(MotionEstimate estimate) =>
        $"{estimate.Kind} · {estimate.Diagnostics.Stage}";


    // ============ 夹具（照抄 snow-shot estimator.rs） ============

    /// <summary>
    /// <c>scrolling_fixture</c>：320×224，固定顶 40 行 / 底 32 行 / 左栏 64 px，右下角一块每帧都变的动态区。
    /// </summary>
    private static StitchFrame ScrollingFixture(uint scroll, byte animation)
    {
        const int width = 320;
        const int height = 224;
        const int top = 40;
        const int bottom = 32;
        const int sidebar = 64;

        return GrayFrame(width, height, (x, y) =>
        {
            unchecked
            {
                bool isFixed = y < top || y >= height - bottom || x < sidebar;
                uint sourceY = isFixed ? (uint)y : (uint)(y - top) + scroll;
                uint hash = isFixed
                    ? (uint)x * 0x9e37_79b9u ^ (uint)y * 0x85eb_ca6bu
                    : (uint)x * 0xc2b2_ae35u ^ sourceY * 0x27d4_eb2du;
                hash ^= hash >> 16;
                hash *= 0x7feb_352du;
                hash ^= hash >> 15;
                byte value = (byte)(hash >> 24);

                if (x is >= 192 and < 320 && y is >= 64 and < 160)
                {
                    uint dynamicHash = (uint)x * 0x1656_67b1u ^ (uint)y * 0xd3a2_646cu ^ (uint)animation * 0xfd70_46c5u;
                    dynamicHash ^= dynamicHash >> 16;
                    dynamicHash *= 0x7feb_352du;
                    value = (byte)(dynamicHash >> 24);
                }

                return value;
            }
        });
    }

    /// <summary><c>horizontal_scrolling_fixture</c>：224×192，固定左 32 / 右 24 / 顶 32。</summary>
    private static StitchFrame HorizontalScrollingFixture(uint scroll)
    {
        const int width = 224;
        const int height = 192;
        const int left = 32;
        const int right = 24;
        const int top = 32;

        return GrayFrame(width, height, (x, y) =>
        {
            unchecked
            {
                bool isFixed = x < left || x >= width - right || y < top;
                uint sourceX = isFixed ? (uint)x : (uint)(x - left) + scroll;
                uint hash = isFixed
                    ? (uint)x * 0x9e37_79b9u ^ (uint)y * 0x85eb_ca6bu
                    : sourceX * 0xc2b2_ae35u ^ (uint)y * 0x27d4_eb2du;
                hash ^= hash >> 16;
                hash *= 0x7feb_352du;
                hash ^= hash >> 15;
                return (byte)(hash >> 24);
            }
        });
    }

    /// <summary><c>directional_fixture</c>：整页哈希纹理，<paramref name="sparse"/> 时只留 cross 400–560。</summary>
    private static StitchFrame DirectionalFixture(StitchAxis axis, int cross, int primary, uint scroll, bool sparse)
    {
        int width = axis == StitchAxis.Vertical ? cross : primary;
        int height = axis == StitchAxis.Vertical ? primary : cross;

        return GrayFrame(width, height, (x, y) =>
        {
            unchecked
            {
                int alongCross = axis == StitchAxis.Vertical ? x : y;
                uint along = (uint)(axis == StitchAxis.Vertical ? y : x) + scroll;
                if (sparse && (alongCross <= 400 || alongCross >= 560)) return 255;

                uint hash = (uint)alongCross * 0xc2b2_ae35u ^ along * 0x27d4_eb2du;
                hash ^= hash >> 16;
                hash *= 0x7feb_352du;
                hash ^= hash >> 15;
                return (byte)(hash >> 24);
            }
        });
    }

    /// <summary>大固定浮层（cross &lt; 128 / 主轴 &lt; 32）+ 每 24 行一条重复横线。</summary>
    private static StitchFrame FixedOverlayFrame(StitchAxis axis, uint scroll)
    {
        var frame = DirectionalFixture(axis, 1024, 320, scroll, false);
        var fixedFrame = DirectionalFixture(axis, 1024, 320, 0, false);

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int cross = axis == StitchAxis.Vertical ? x : y;
                int primary = axis == StitchAxis.Vertical ? y : x;
                int index = (y * frame.Width + x) * StitchFrame.Channels;

                if (cross < 128 || primary < 32)
                {
                    frame.Pixels[index] = fixedFrame.Pixels[index];
                    frame.Pixels[index + 1] = fixedFrame.Pixels[index + 1];
                    frame.Pixels[index + 2] = fixedFrame.Pixels[index + 2];
                }
                else if ((primary + (int)scroll) % 24 == 0)
                {
                    frame.Pixels[index] = 0;
                    frame.Pixels[index + 1] = 0;
                    frame.Pixels[index + 2] = 0;
                }
            }
        }

        return frame;
    }

    /// <summary><c>narrow_texture_in_a_mostly_unchanged_tile</c>：只留 cross 420–500 有纹理。</summary>
    private static StitchFrame NarrowTextureFrame(StitchAxis axis, uint scroll)
    {
        var frame = DirectionalFixture(axis, 2048, 320, scroll, false);

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int cross = axis == StitchAxis.Vertical ? x : y;
                if (cross is >= 420 and < 500) continue;

                int index = (y * frame.Width + x) * StitchFrame.Channels;
                frame.Pixels[index] = 255;
                frame.Pixels[index + 1] = 255;
                frame.Pixels[index + 2] = 255;
            }
        }

        return frame;
    }

    /// <summary><c>blank_to_text</c>：主轴 192 以前全是空白，之后才有文字。</summary>
    private static StitchFrame BlankToTextFrame(StitchAxis axis, uint scroll)
    {
        var frame = DirectionalFixture(axis, 320, 320, scroll, false);

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int primary = axis == StitchAxis.Vertical ? y : x;
                if ((uint)primary + scroll >= 192) continue;

                int index = (y * frame.Width + x) * StitchFrame.Channels;
                frame.Pixels[index] = 255;
                frame.Pixels[index + 1] = 255;
                frame.Pixels[index + 2] = 255;
            }
        }

        return frame;
    }

    /// <summary><c>unrelated_fixture</c>：320×224，换 <paramref name="seed"/> 就是另一页内容。</summary>
    private static StitchFrame UnrelatedFixture(uint seed)
    {
        return GrayFrame(320, 224, (x, y) =>
        {
            unchecked
            {
                uint hash = (uint)x * 0x9e37_79b9u ^ (uint)y * 0x85eb_ca6bu ^ seed * 0xc2b2_ae35u;
                hash ^= hash >> 16;
                hash *= 0x7feb_352du;
                return (byte)(hash >> 24);
            }
        });
    }

    /// <summary>按 <paramref name="shade"/> 生成一帧 Bgra32 灰度图（alpha 全满）。</summary>
    private static StitchFrame GrayFrame(int width, int height, Func<int, int, byte> shade)
    {
        var frame = StitchFrame.Create(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte gray = shade(x, y);
                int index = (y * width + x) * StitchFrame.Channels;
                frame.Pixels[index] = gray;
                frame.Pixels[index + 1] = gray;
                frame.Pixels[index + 2] = gray;
                frame.Pixels[index + 3] = 0xFF;
            }
        }

        return frame;
    }
}
