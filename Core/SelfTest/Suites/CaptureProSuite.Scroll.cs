using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>CaptureProSuite 的「滚动长图：检测 / 拼接 / 水平 / 节奏」部分。</summary>
internal static partial class CaptureProSuite
{
    private static void ScrollDetection()
    {
        var page = NewScrollPage(420);

        Check.Equal(-50, ShiftOf(Frame(page, 0), Frame(page, 50)), "往下滚 50 行");
        Check.Equal(-5, ShiftOf(Frame(page, 0), Frame(page, 5)), "小幅位移 5 行");
        Check.Equal(0, ShiftOf(Frame(page, 0), Frame(page, 0)), "没动 = 0");
        Check.Equal(-130, ShiftOf(Frame(page, 0), Frame(page, 130)), "大位移 130 行（一次滚过大半屏）");

        // 两页毫无关系的内容：认不出（null）或没动（0）都不接，绝不能把两张无关的图接起来
        int? unrelated = ShiftOf(Frame(NewScrollPage(242, seed: 7), 0), Frame(NewScrollPage(242, seed: 99), 0));
        Check.True(unrelated is null or 0, $"内容对不上 → 不接（实际 {unrelated?.ToString() ?? "null"}）");

        // 帧尺寸变了（换了窗口 / 改了选区）也认不出
        var stitch = new ScrollStitch();
        stitch.Start(Frame(page, 0));
        Check.True(stitch.Append(CropHeight(Frame(page, 0), 200)) is null, "高度不一致 → 认不出");
        Check.Equal(ViewHeight, stitch.Extent, "认不出的帧不会接进画布");

        // 底下一截固定页脚（滚动时不动，但太矮、够不上固定条门槛）：照样认得出位移
        var a = PaintBand(Frame(page, 20), ViewHeight - 4, 4, 0x10);
        var b = PaintBand(Frame(page, 50), ViewHeight - 4, 4, 0x10);
        Check.Equal(-30, ShiftOf(a, b), "页脚不动也能认出位移");

        // 中间一段在动（视频 / 光标闪烁 / 加载中的图）：按候选打分，照样认得位移
        var animated = PaintBand(Frame(page, 60), ViewHeight / 3, 20, 0xFF);
        Check.Equal(-60, ShiftOf(Frame(page, 0), animated), "一段画面在动也能认出位移");
    }

    /// <summary>把两帧喂给新的拼接器，返回第二帧的位移（<c>null</c> = 认不出）。</summary>
    private static int? ShiftOf(BitmapSource first, BitmapSource next)
    {
        var stitch = new ScrollStitch();
        stitch.Start(first);
        return stitch.Append(next);
    }

    /// <summary>
    /// 长截图：拼接只接新露出来的行、往回滚不变长、预览按比例缩、上限会自己停；面板优先落在选区外面。
    /// </summary>
    private static void ScrollBuild()
    {
        var page = NewScrollPage(520);
        var stitch = new ScrollStitch();
        stitch.Start(Frame(page, 0));

        Check.Equal(ViewHeight, stitch.Extent, "起点 = 第一帧高度");
        Check.Equal(ScrollWidth, stitch.OutputWidth, "宽度 = 帧宽");
        Check.Equal(-50, stitch.Append(Frame(page, 50)) ?? 1, "第二帧位移 50（往下滚是负数）");
        Check.Equal(ViewHeight + 50, stitch.Extent, "只接了新露出来的 50 行");
        Check.Equal(-40, stitch.Append(Frame(page, 90)) ?? 1, "第三帧位移 40");
        Check.Equal(ViewHeight + 90, stitch.Extent, "再接 40 行");
        Check.Equal(0, stitch.Append(Frame(page, 90)) ?? 1, "重复帧（到底）位移 0");
        Check.Equal(ViewHeight + 90, stitch.Extent, "没动就不接");

        // 往回滚 10 行：内容已经在画布里（Contained），画布不变长
        Check.Equal(10, stitch.Append(Frame(page, 80)) ?? 1, "往回滚 10 行");
        Check.Equal(ViewHeight + 90, stitch.Extent, "往回滚不改成品长度");

        // 再往下 10 行回到原处：还是落在画布里，不变长
        Check.Equal(-10, stitch.Append(Frame(page, 90)) ?? 1, "再往下 10 行回到原处");
        Check.Equal(ViewHeight + 90, stitch.Extent, "回到原处仍不变长");

        var longImage = stitch.Render();
        Check.Equal(ViewHeight + 90, longImage.PixelHeight, "拼出来的长图高度");
        Check.Equal(ScrollWidth, longImage.PixelWidth, "拼出来的长图宽度");

        // 每一行都是「整页」对应位置的那一行（帧全是同一页的裁切，没有任何偏差）
        for (int y = 0; y < longImage.PixelHeight; y += 37)
            Check.True(RowAt(longImage, y).SequenceEqual(RowAt(page, y)), $"第 {y} 行与整页一致");
        Check.True(RowAt(longImage, longImage.PixelHeight - 1)
                .SequenceEqual(RowAt(page, longImage.PixelHeight - 1)), "最后一行与整页一致");

        var preview = stitch.Preview(30, 200);
        Check.True(preview.PixelWidth <= 30 && preview.PixelHeight <= 200, "预览装进框里");
        Check.True(preview.PixelHeight >= 100, $"预览按比例缩小（{longImage.PixelHeight} 行 → {preview.PixelHeight}）");

        // 就地预览取的是拼接图的**末尾若干行**（不是整张长图重画）
        var tail = stitch.Tail(100);
        Check.Equal(100, tail.PixelHeight, "末尾一屏按行取");
        Check.Equal(ScrollWidth, tail.PixelWidth, "末尾一屏的宽不变");
        Check.Equal(ViewHeight + 90, stitch.Tail(longImage.PixelHeight).PixelHeight, "要的行比拼接图还多 → 给整张");

        Check.True(RowAt(tail, tail.PixelHeight - 1)
                .SequenceEqual(RowAt(longImage, longImage.PixelHeight - 1)), "末尾一屏的最后一行 = 长图的最后一行");

        // 高度上限：到顶就 Full，调用方据此收尾（一直滚下去也不会吃满内存）
        var tall = NewScrollPage(900);
        var limited = new ScrollStitch(StitchAxis.Vertical, 600);
        limited.Start(Frame(tall, 0));
        for (int offset = 100; !limited.Full && offset <= 600; offset += 100)
        {
            if (limited.Append(Frame(tall, offset)) is null) break;
        }
        Check.True(limited.Full, "滚到长度上限后 Full = true");
        Check.True(limited.Extent >= 600, $"停在 {limited.Extent} 行（上限 600）");

        // 小面板落点（画在覆盖层窗口里）：右侧放得下就放右侧，都放不下塞右下角
        var view = new Size(1920, 1040);
        var bar = new Size(400, 320);
        var inRight = ScrollLayout.PlaceIn(new Rect(100, 100, 800, 600), bar, view);
        Check.True(inRight.Left >= 900, $"候选条放选区右侧（Left={inRight.Left:0}）");
        Check.Equal(100d, inRight.Top, "与选区顶边对齐");

        var inBelow = ScrollLayout.PlaceIn(new Rect(0, 0, 400, 300), bar, new Size(420, 1040));
        Check.Equal(0d, inBelow.Left, "右侧不够 → 放到下方");
        Check.Equal(312d, inBelow.Top, "下方留出间距");

        var inClamp = ScrollLayout.PlaceIn(new Rect(0, 0, 1920, 1040), bar, view);
        Check.True(inClamp.Right <= 1920 && inClamp.Bottom <= 1040, "窗口放满 → 夹回右下角");
    }

    /// <summary>
    /// 长截图：页底那条固定条（置顶 / 置底按钮条）只出现一次——认出后每帧先剪掉末尾那几线、成品末尾留一条，
    /// `Extent` 一直把这一截算在内、`Finish` 幂等；纯色空白（只是空白、不是固定条）不能被当成固定条剪掉。
    /// </summary>
    private static void ScrollSticky()
    {
        const int footer = 30;
        var page = NewScrollPage(900);

        var stitch = new ScrollStitch();
        var first = FooterFrame(page, 0, footer);
        stitch.Start(first);
        Check.Equal(0, stitch.StickyBottom, "首帧还不知道有没有固定条");

        var second = FooterFrame(page, 60, footer);
        Check.Equal(-60, stitch.Append(second) ?? 1, "有固定页脚也认得出位移");
        Check.Equal(footer, stitch.StickyBottom, "认出 30 行的固定页脚");
        Check.Equal(ViewHeight + 60, stitch.Extent, "首帧尾部那条已经剪掉，新内容照接（成品末尾仍算固定条）");

        var third = FooterFrame(page, 120, footer);
        Check.Equal(-60, stitch.Append(third) ?? 1, "第三帧位移 60");
        Check.Equal(ViewHeight + 120, stitch.Extent, "每一帧只接固定条上面那一截");

        var footerRow = RowAt(third, ViewHeight - 10);
        Check.Equal(1, CountRows(stitch.Render(), footerRow), "拼到一半时固定条只出现一次（成品末尾）");

        stitch.Finish();
        Check.Equal(ViewHeight + 120, stitch.Extent, "Finish 幂等：固定条已经算在里面，不会再补一条");

        var finished = stitch.Render();
        Check.Equal(1, CountRows(finished, footerRow), "整张长图里固定条只出现一次");
        Check.True(RowAt(finished, finished.PixelHeight - 1).SequenceEqual(RowAt(third, ViewHeight - 1)),
            "最下面那行就是固定条");

        stitch.Finish();
        Check.Equal(ViewHeight + 120, stitch.Extent, "重复调用 Finish 不改长度");

        // 整片纯色的固定条（比如一条纯色底、没有字的页脚）：认不出「有信息」的行，
        // 宁可中间重复一遍也不剪内容（剪错的代价比重复大）
        var plain = new ScrollStitch();
        plain.Start(FooterFrame(page, 0, footer, flat: true));
        Check.Equal(-60, plain.Append(FooterFrame(page, 60, footer, flat: true)) ?? 1,
            "纯色页脚在也不影响位移检测");
        Check.Equal(0, plain.StickyBottom, "纯色页脚不当固定条");
        Check.Equal(ViewHeight + 60, plain.Extent, "没剪内容");
    }

    /// <summary>
    /// 长截图：横向拼接。同一条「按特征对齐」的算法，把「沿轴」换成**列**——
    /// 横向平移对纵向拼接器完全不可见，横向才认得出。
    /// </summary>
    private static void ScrollHorizontal()
    {
        var page = NewWideScrollPage(520);

        // 把横向平移的帧喂给纵向拼接器：认不出（这正是「横向必须单独一个方向」的理由）
        var vertical = new ScrollStitch();
        vertical.Start(ColFrame(page, 0));
        int? sideways = vertical.Append(ColFrame(page, 50));
        Check.True(sideways is null or 0, $"横向平移 → 纵向拼接器不接（实际 {sideways?.ToString() ?? "null"}）");

        // 拼接器：横向模式拼出来的是「更宽的图」，只接新露出来的列
        var stitch = new ScrollStitch(StitchAxis.Horizontal);
        Check.Equal(StitchAxis.Horizontal, stitch.Axis, "方向 = 横向");
        stitch.Start(ColFrame(page, 0));
        Check.Equal(ViewHeight, stitch.Extent, "横向起点 = 一屏的宽");
        Check.Equal(ViewHeight, stitch.OutputWidth, "横向拼图的宽 = 已接的列数");
        Check.Equal(ViewHeight, stitch.OutputHeight, "横向拼图的高 = 帧高");

        Check.Equal(-50, stitch.Append(ColFrame(page, 50)) ?? 1, "横向第二帧位移 50（往右滚是负数）");
        Check.Equal(ViewHeight + 50, stitch.Extent, "横向只接新露出来的 50 列");
        Check.Equal(-40, stitch.Append(ColFrame(page, 90)) ?? 1, "横向第三帧位移 40");
        Check.Equal(ViewHeight + 90, stitch.Extent, "横向再接 40 列");

        var wideImage = stitch.Render();
        Check.Equal(ViewHeight + 90, wideImage.PixelWidth, "横向拼图的宽");
        Check.Equal(ViewHeight, wideImage.PixelHeight, "横向拼图的高");

        // 拼出来的像素要真的来自页面：每一列都对得上整页的对应列
        for (int x = 0; x < wideImage.PixelWidth; x += 41)
            Check.True(ColumnAt(wideImage, x).SequenceEqual(ColumnAt(page, x)), $"第 {x} 列与整页一致");
        Check.True(ColumnAt(wideImage, wideImage.PixelWidth - 1)
                .SequenceEqual(ColumnAt(ColFrame(page, 90), ViewHeight - 1)),
            "最右一列 = 最后一帧的最后一列");

        // 就地预览：横向取的是「末尾若干列」，尺寸按宽高各自缩
        var tail = stitch.Tail(100);
        Check.Equal(100, tail.PixelWidth, "横向末尾一屏按列取");
        Check.Equal(ViewHeight, tail.PixelHeight, "横向末尾一屏的高不变");

        var preview = stitch.Preview(300, 40);
        Check.True(preview.PixelWidth <= 300 && preview.PixelHeight <= 40, "横向预览装进框里");

        // 尺寸变了（换了窗口）认不出
        Check.True(stitch.Append(ColFrame(page, 90, ViewHeight - 20)) == null, "横向帧尺寸变了 → 认不出");
    }

    /// <summary>
    /// 自动滚动的节奏：实测成本越大间隔越长、连续拼不上会退避、接上了立刻恢复、
    /// 上下限夹住（别快过页面落定、也别慢成「一格格挪」）。
    /// </summary>
    private static void ScrollCadenceRules()
    {
        var cadence = new ScrollCadence();

        // 还没有样本：就用用户 / 默认节奏
        Check.Equal(ScrollCadence.MinIntervalMs, cadence.NextInterval(0, 0),
            "没有样本、没有基准 → 夹到下限");
        Check.Equal(200, cadence.NextInterval(200, 0), "没有样本 → 用基准节奏");
        Check.Equal(500, cadence.NextInterval(200, 500), "落定时间比基准还长 → 按落定走");

        // 抓帧慢（160ms × 1.25 = 200）比基准慢：取成本那一侧
        cadence.Reset();
        cadence.RecordCapture(160);
        Check.Equal(200, cadence.NextInterval(50, 0), "成本高于基准 → 按成本走");
        Check.Equal(250, cadence.NextInterval(250, 0), "基准高于成本 → 按基准走");

        // 成本算出来比下限还快：夹到下限（页面来不及落定，滚太快反而拼不上）
        cadence.Reset();
        cadence.RecordCapture(20);
        Check.Equal(ScrollCadence.MinIntervalMs, cadence.NextInterval(0, 0), "成本很低 → 夹到下限");

        // 拼接更慢时按拼接算（两者串行，瓶颈取大的）
        cadence.RecordStitch(400);
        Check.Equal(500, cadence.NextInterval(0, 0), "取抓帧与拼接里更慢的那个");

        // EWMA：第二次采样只挪 25%
        cadence.Reset();
        cadence.RecordCapture(100);
        cadence.RecordCapture(200);
        Check.True(cadence.CaptureMs > 100 && cadence.CaptureMs < 200,
            $"EWMA 平滑（100 → 200 之间，实际 {cadence.CaptureMs:0.#}）");

        // 退避：连续拼不上，间隔逐档变长
        cadence.Reset();
        int plain = cadence.NextInterval(200, 0);
        cadence.NoteProgress(false);
        int once = cadence.NextInterval(200, 0);
        cadence.NoteProgress(false);
        int twice = cadence.NextInterval(200, 0);
        Check.True(once > plain, $"一档退避更慢（{plain} → {once}）");
        Check.True(twice > once, $"两档退避更慢（{once} → {twice}）");

        // 接上了就立刻恢复
        cadence.NoteProgress(true);
        Check.Equal(0, cadence.Penalty, "接上内容 → 退避清零");
        Check.Equal(plain, cadence.NextInterval(200, 0), "退避清零 → 回到原节奏");

        // 退避有上限（再多也不会一直翻倍）
        cadence.Reset();
        for (int i = 0; i < 20; i++) cadence.NoteProgress(false);
        Check.Equal(ScrollCadence.MaxPenalty, cadence.Penalty, "退避最多累计几档");

        // 上下限
        cadence.Reset();
        cadence.RecordCapture(20000);
        Check.Equal(ScrollCadence.MaxIntervalMs, cadence.NextInterval(200, 0), "成本极大 → 夹到上限");

        cadence.Reset();
        cadence.RecordCapture(1);
        Check.True(cadence.NextInterval(0, 0) >= ScrollCadence.MinIntervalMs,
            "无论怎么算都不会快过下限");

        // 非法样本不影响节奏
        cadence.Reset();
        cadence.RecordCapture(double.NaN);
        cadence.RecordCapture(-5);
        Check.Equal(0d, cadence.CaptureMs, "NaN / 负数样本被忽略");
        Check.Equal(200, cadence.NextInterval(200, 0), "没有有效样本 → 仍是基准节奏");
    }

    /// <summary>
    /// 屏幕选字的候选切分：按句末标点、逗号、长度上限逐级切，空白与完全重复的候选丢掉；
    /// 切完的字符总数不变（不许「切着切着少字」）。
    /// </summary>
    private static void ScreenTextSplit()
    {
        var sentences = ScreenTextCandidates.Build(new[] { "第一句话。第二句话！", "  ", "第三行" });
        Check.Equal(3, sentences.Count, "空白行丢掉");
        Check.Equal("第一句话。", sentences[0], "按句末标点切开、标点留在句尾");
        Check.Equal("第二句话！", sentences[1], "第二句照切");
        Check.Equal("第三行", sentences[2], "没有标点的整行就是一条");

        var burst = ScreenTextCandidates.Build(new[] { "真的吗？！不会吧。" });
        Check.Equal("真的吗？！", burst[0], "连续标点算同一句的结尾");
        Check.Equal("不会吧。", burst[1], "后半句照切");

        var dup = ScreenTextCandidates.Build(new[] { "重复", "重复", "不同" });
        Check.Equal(2, dup.Count, "完全相同的候选只留一条");
        Check.Equal("重复", dup[0], "先出现的留着");

        // 超长：先按逗号切，仍超长就硬切；切完不丢字（内容互不相同，免得撞上「相同候选去重」）
        var longLine = new string(Enumerable.Range(0, 300).Select(i => (char)(0x4E00 + i)).ToArray()) + "，后半句";
        var pieces = ScreenTextCandidates.Build(new[] { longLine }, 50);
        Check.True(pieces.Count >= 6, $"超长行切成了 {pieces.Count} 条");
        Check.True(pieces.All(p => p.Length <= 50), $"每条不超过上限（最长 {pieces.Max(p => p.Length)}）");
        Check.Equal(304, pieces.Sum(p => p.Length), "切完字符总数不变");

        Check.Equal(0, ScreenTextCandidates.Build(Array.Empty<string>()).Count, "没有文字 → 没有候选");
        Check.Equal(0, ScreenTextCandidates.Build(new[] { "   ", "\t" }).Count, "只有空白 → 没有候选");
        Check.Equal(1, ScreenTextCandidates.Build(new[] { "a,b" }, 50).Count, "逗号只在超长时才切");
    }

    /// <summary>长截图的「屏幕」高度：一次看到多少行。</summary>
    private const int ViewHeight = 240;

    /// <summary>合成一页「整页内容」：每行颜色固定，按 offset 截出来就是滚动中的某一帧。</summary>
    private static BitmapSource NewPage(int height, int seed = 0)
    {
        const int width = 16;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)((y * 7 + seed) & 0xFF);
                pixels[i + 1] = (byte)((x * 11 + y * 3) & 0xFF);
                pixels[i + 2] = (byte)((y * 13) & 0xFF);
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>长截图合成帧的宽度（200 像素：ORB 的特征点要离边缘 31 像素，太窄会一个点都找不到）。</summary>
    private const int ScrollWidth = 200;

    /// <summary>
    /// 合成一页「整页内容」：每 4×4 像素一块，灰度由 (x/4, y/4, seed) 的哈希决定。
    /// 与 snow-shot 自带的合成帧（<c>stitcher_cache_tests.rs</c> 的 <c>texture</c>）同一个做法：
    /// 4 像素粒度的纹理上 ORB 特征最密，按 offset 裁出来就是滚动中的某一帧。
    /// </summary>
    private static BitmapSource NewScrollPage(int height, int seed = 0)
    {
        int stride = ScrollWidth * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < ScrollWidth; x++)
            {
                int i = y * stride + x * 4;
                byte gray = TileHash(x / 4, y / 4, seed);
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(ScrollWidth, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>4×4 块的灰度：同一页里每一块都不同，跨页（换 seed）则毫无关系。</summary>
    private static byte TileHash(int cross, int primary, int seed)
    {
        unchecked
        {
            uint hash = (uint)(cross * 0xc2b2ae35) ^ (uint)(primary * 0x27d4eb2d) ^ (uint)seed;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            return (byte)(hash >> 24);
        }
    }

    /// <summary>从整页里截出 offset 开始的 <see cref="ViewHeight"/> 行（= 滚到那里时屏幕上看得到的那一帧）。</summary>
    private static BitmapSource Frame(BitmapSource page, int offset)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * ViewHeight];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, ViewHeight), pixels, stride, 0);

        var frame = BitmapSource.Create(page.PixelWidth, ViewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>把一帧沿轴截短到 <paramref name="height"/> 行（验证「尺寸变了就认不出」）。</summary>
    private static BitmapSource CropHeight(BitmapSource frame, int height)
    {
        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * height];
        frame.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, height), pixels, stride, 0);

        var cropped = BitmapSource.Create(frame.PixelWidth, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>把一帧的第 [<paramref name="start"/>, +<paramref name="count"/>) 行涂成同一片灰（模拟不动的页脚 / 在动的画面）。</summary>
    private static BitmapSource PaintBand(BitmapSource frame, int start, int count, byte gray)
    {
        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        int end = Math.Min(frame.PixelHeight, start + count);
        for (int y = Math.Max(0, start); y < end; y++)
        {
            for (int x = 0; x < frame.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var painted = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        painted.Freeze();
        return painted;
    }

    /// <summary>
    /// 滚到 <paramref name="offset"/> 的一帧，底部 <paramref name="footer"/> 行换成「固定条」：
    /// 正常是带花纹的一条（有信息，认得出是固定条），<paramref name="flat"/> 时是一整片纯色（空白，不该当固定条）。
    /// </summary>
    private static BitmapSource FooterFrame(BitmapSource page, int offset, int footer, bool flat = false)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * ViewHeight];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, ViewHeight), pixels, stride, 0);

        for (int y = ViewHeight - footer; y < ViewHeight; y++)
        {
            for (int x = 0; x < page.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                if (flat)
                {
                    pixels[i] = 0xF0;
                    pixels[i + 1] = 0xF0;
                    pixels[i + 2] = 0xF0;
                }
                else
                {
                    // 每一行都不一样（同一行里也有灰度跨度）：后面要按「某一行出现了几次」数固定条
                    pixels[i] = (byte)(0x30 + x * 7);
                    pixels[i + 1] = (byte)(0x40 + (y - (ViewHeight - footer)) * 5);
                    pixels[i + 2] = 0xD0;
                }
                pixels[i + 3] = 0xFF;
            }
        }

        var frame = BitmapSource.Create(page.PixelWidth, ViewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>位图里第 <paramref name="y"/> 行（Bgra32 字节）。</summary>
    private static byte[] RowAt(BitmapSource image, int y)
    {
        int stride = image.PixelWidth * 4;
        var row = new byte[stride];
        image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
        return row;
    }

    /// <summary>整张图里有多少行与 <paramref name="row"/> 逐字节相同（数固定条重复了几遍）。</summary>
    private static int CountRows(BitmapSource image, byte[] row)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        int count = 0;
        for (int y = 0; y < image.PixelHeight; y++)
        {
            bool same = true;
            for (int i = 0; i < stride; i++)
            {
                if (pixels[y * stride + i] != row[i]) { same = false; break; }
            }
            if (same) count++;
        }
        return count;
    }

    // ============ 横向拼接的合成帧 ============

    /// <summary>合成一张「很宽的页面」：每 4×4 块一块哈希纹理，按 offset 截出来就是横向滚动中的某一帧。</summary>
    private static BitmapSource NewWideScrollPage(int width, int seed = 0)
    {
        int height = ViewHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                byte gray = TileHash(x / 4, y / 4, seed);
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>从宽页里截出 offset 开始的 <see cref="ViewHeight"/> 列（横向滚动到那里时的一帧）。</summary>
    private static BitmapSource ColFrame(BitmapSource page, int offset, int width = ViewHeight)
    {
        int stride = page.PixelWidth * 4;
        var source = new byte[stride * page.PixelHeight];
        page.CopyPixels(source, stride, 0);

        int outStride = width * 4;
        var pixels = new byte[outStride * page.PixelHeight];
        for (int y = 0; y < page.PixelHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int from = y * stride + (offset + x) * 4;
                int to = y * outStride + x * 4;
                pixels[to] = source[from];
                pixels[to + 1] = source[from + 1];
                pixels[to + 2] = source[from + 2];
                pixels[to + 3] = source[from + 3];
            }
        }

        var frame = BitmapSource.Create(width, page.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, outStride);
        frame.Freeze();
        return frame;
    }

    /// <summary>位图里第 <paramref name="x"/> 列（Bgra32 字节，按 y 递增）。</summary>
    private static byte[] ColumnAt(BitmapSource image, int x)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        var column = new byte[image.PixelHeight * 4];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            int from = y * stride + x * 4;
            int to = y * 4;
            column[to] = pixels[from];
            column[to + 1] = pixels[from + 1];
            column[to + 2] = pixels[from + 2];
            column[to + 3] = pixels[from + 3];
        }
        return column;
    }

}
