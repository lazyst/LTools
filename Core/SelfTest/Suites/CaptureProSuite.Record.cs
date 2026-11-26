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

/// <summary>CaptureProSuite 的「录屏：跟随 / 区域 / 残片恢复 / 记账」部分。</summary>
internal static partial class CaptureProSuite
{
    // ============ 录屏 ============

    /// <summary>
    /// 录屏参数 + ffmpeg 命令行 + 定位顺序。
    /// 命令行是「拼错了要录半天才发现」的那类东西，关键参数逐条断言（顺序也守着）。
    /// </summary>
    private static void Record()
    {
        var plan = RecordPlan.FromValues("window", "gif", 120, 99);
        Check.Equal(RecordMode.Window, plan.Mode, "范围按取值解析");
        Check.Equal(RecordFormat.Gif, plan.Format, "格式按取值解析");
        Check.Equal(RecordPlan.MaxFps, plan.EffectiveFps, "帧率夹到上限");
        Check.Equal(RecordPlan.MaxCountdown, plan.Countdown, "倒计时夹到上限");

        Check.Equal(RecordMode.Region, RecordPlan.FromValues("???", null, 0, -5).Mode, "认不出的范围退回框选");
        Check.Equal(RecordFormat.Mp4, RecordPlan.FromValues(null, "webm", 0, 0).Format, "认不出的格式退回 MP4");
        Check.Equal(RecordPlan.DefaultFps, RecordPlan.FromValues(null, null, 0, 0).EffectiveFps, "帧率 0 = 默认值");
        Check.Equal(RecordPlan.MinFps, RecordPlan.FromValues(null, null, 1, 0).EffectiveFps, "帧率太小夹到下限");
        Check.Equal(0, RecordPlan.FromValues(null, null, 30, -5).Countdown, "倒计时负数夹到 0");

        Check.Equal(RecordPlan.GifMaxFps,
            RecordPlan.FromValues(null, "gif", 60, 0, null,
                new RecordOptions { AnimatedFrameRate = 30 }).GifFps, "GIF 帧率最多 15");
        Check.Equal(RecordOptions.DefaultAnimatedFrameRate, RecordPlan.FromValues(null, "gif", 30, 0).GifFps,
            "动图帧率取设置（默认 10），不看录制帧率");
        Check.Equal(30, RecordPlan.FromValues(null, "apng", 30, 0, null,
            new RecordOptions { AnimatedFrameRate = 30 }).GifFps, "APNG 不受 GIF 的 15 上限");
        Check.Equal("gif", RecordPlan.FromValues(null, "gif", 30, 0).Extension, "GIF 扩展名");
        Check.Equal("mp4", RecordPlan.FromValues(null, null, 30, 0).Extension, "MP4 扩展名");

        // 四个格式（上游下拉的同四项）：解析 / 扩展名 / 动图判定 / 显示名
        Check.Equal(RecordFormat.Apng, RecordPlan.ParseFormat("apng"), "APNG 格式解析");
        Check.Equal(RecordFormat.Webp, RecordPlan.ParseFormat("WebP"), "WebP 格式解析不分大小写");
        Check.Equal("apng", RecordPlan.FromValues(null, "apng", 30, 0).Extension, "APNG 扩展名");
        Check.Equal("webp", RecordPlan.FromValues(null, "webp", 30, 0).Extension, "WebP 扩展名");
        Check.True(RecordPlan.FromValues(null, "gif", 30, 0).IsAnimated, "GIF 是动图");
        Check.True(RecordPlan.FromValues(null, "apng", 30, 0).IsAnimated, "APNG 是动图");
        Check.True(RecordPlan.FromValues(null, "webp", 30, 0).IsAnimated, "WebP 是动图");
        Check.False(RecordPlan.FromValues(null, "mp4", 30, 0).IsAnimated, "MP4 不是动图");
        Check.Equal(4, RecordPlan.Formats.Count, "格式下拉 4 项");
        Check.Equal("APNG", RecordPlan.FormatName(RecordFormat.Apng), "APNG 显示名");
        Check.Equal("WebP", RecordPlan.FormatName(RecordFormat.Webp), "WebP 显示名");

        // 面板时长一律 HH:MM:SS（上游 setRecordingDuration）
        Check.Equal("00:00:00", RecordPlan.FormatDuration(0), "时钟空闲态");
        Check.Equal("00:00:59", RecordPlan.FormatDuration(59_999), "时钟未满一分钟");
        Check.Equal("01:02:03", RecordPlan.FormatDuration(3_723_500), "时钟超过一小时");

        Check.Equal("选区", RecordPlan.ModeLabel(RecordMode.Region), "范围文案");
        Check.Equal("全部屏幕", RecordPlan.ModeLabel(RecordMode.All), "全部屏幕文案");
        Check.Equal("MP4", RecordPlan.FormatLabel("mp4"), "格式文案");
        Check.Equal("GIF", RecordPlan.FormatLabel("GIF"), "格式文案不分大小写");
        Check.Equal("APNG", RecordPlan.FormatLabel("apng"), "APNG 文案");
        Check.Equal("WebP", RecordPlan.FormatLabel("webp"), "WebP 文案");

        // 采集历史存成品时不能把动图扩展名改回 mp4（否则 .mp4 里装的是 apng / webp）
        Check.Equal("records/x.apng", CaptureCatalog.RecordRelative("x", "apng"), "APNG 成品保留 .apng");
        Check.Equal("records/x.webp", CaptureCatalog.RecordRelative("x", "webp"), "WebP 成品保留 .webp");
        Check.Equal("records/x.gif", CaptureCatalog.RecordRelative("x", "GIF"), "GIF 成品保留 .gif");
        Check.Equal("records/x.mp4", CaptureCatalog.RecordRelative("x", "bmp"), "不认识的扩展名回到 .mp4");
        Check.Equal("00:07", RecordPlan.FormatClock(7000), "不足一分钟");
        Check.Equal("01:02", RecordPlan.FormatClock(62000), "超过一分钟");
        Check.Equal("1:00:00", RecordPlan.FormatClock(3600000), "超过一小时");

        // MP4：原始 BGRA 从管道进来 → libx264 / yuv420p，奇数尺寸补成偶数，moov 前置
        var mp4 = RecordPlan.Mp4Args(new Int32Rect(0, 0, 1921, 1081), 30, "out.mp4", RecordEncoders.X264);
        string mp4Line = string.Join(" ", mp4);
        Check.True(mp4Line.Contains("rawvideo") && mp4Line.Contains("bgra"), "原始帧格式是 bgra");
        Check.True(mp4Line.Contains("1921x1081"), "尺寸原样交给编码器");
        Check.True(mp4Line.Contains("-framerate 30"), "帧率写进命令行");
        Check.True(mp4Line.Contains("pipe:0"), "从标准输入读原始帧");
        Check.True(mp4Line.Contains("libx264") && mp4Line.Contains("yuv420p"), "H.264 + yuv420p");
        Check.True(mp4Line.Contains("pad=ceil(iw/2)*2:ceil(ih/2)*2"), "奇数宽高补成偶数");
        Check.True(mp4Line.Contains("+frag_keyframe+empty_moov"), "录的过程写分片 MP4（崩溃可恢复）");
        Check.True(!mp4Line.Contains("+faststart"), "边录边写不要求 faststart（收尾再转）");
        Check.Equal("out.mp4", mp4[^1], "输出路径是最后一个参数");

        // 收尾：只搬容器不重编码，转成普通 faststart MP4
        var remux = RecordPlan.RemuxArgs("in.mp4", "out.mp4");
        string remuxLine = string.Join(" ", remux);
        Check.True(remuxLine.Contains("-c copy"), "重封装不重编码");
        Check.True(remuxLine.Contains("+faststart"), "收尾转成 faststart");
        Check.Equal("out.mp4", remux[^1], "重封装输出是最后一个参数");

        // GIF：两遍（调色板 → 编码），帧率与宽度都压下来
        var palette = RecordPlan.GifPaletteArgs("in.mp4", "p.png", 60);
        string paletteLine = string.Join(" ", palette);
        Check.True(paletteLine.Contains("fps=15"), "GIF 抽帧压到 15");
        Check.True(paletteLine.Contains("palettegen"), "第一遍做调色板");
        Check.True(paletteLine.Contains("min(960,iw)"), "宽度上限 960");
        Check.Equal("p.png", palette[^1], "调色板文件是最后一个参数");

        var gif = RecordPlan.GifEncodeArgs("in.mp4", "p.png", "out.gif", 60);
        string gifLine = string.Join(" ", gif);
        Check.True(gifLine.Contains("paletteuse"), "第二遍用调色板");
        Check.True(gifLine.Contains("-loop 0"), "无限循环");
        Check.Equal("out.gif", gif[^1], "GIF 输出是最后一个参数");

        // APNG / WebP：单遍转码（不像 GIF 要两遍调色板），同样压帧率 / 宽度，且不带音轨
        var apng = RecordPlan.AnimatedArgs("in.mp4", "out.apng", RecordFormat.Apng, 15);
        string apngLine = string.Join(" ", apng);
        Check.True(apngLine.Contains("fps=15"), "APNG 帧率按设置");
        Check.True(apngLine.Contains("min(960,iw)"), "没给清晰度时宽度上限 960");
        Check.True(apngLine.Contains("-an"), "动图不带音轨");
        Check.True(apngLine.Contains("-c:v apng"), "APNG 用 apng 编码器");
        Check.Equal("out.apng", apng[^1], "APNG 输出是最后一个参数");

        var webp = RecordPlan.AnimatedArgs("in.mp4", "out.webp", RecordFormat.Webp, 30);
        string webpLine = string.Join(" ", webp);
        Check.True(webpLine.Contains("libwebp_anim"), "WebP 动图用 libwebp_anim");
        Check.True(webpLine.Contains("-loop 0"), "WebP 无限循环");
        Check.True(webpLine.Contains("-an"), "WebP 也不带音轨");
        Check.Equal("out.webp", webp[^1], "WebP 输出是最后一个参数");

        // 定位：随包优先，其次 PATH（要剥引号），都没有返回 null
        string bundled = Path.Combine("C:\\app", "runtime", "ffmpeg", "ffmpeg.exe");
        Check.Equal(bundled, RecordPlan.Locate("C:\\app", "D:\\tools", p => p == bundled), "随包优先");

        string fromPath = Path.Combine("D:\\tools", "ffmpeg.exe");
        Check.Equal(fromPath, RecordPlan.Locate("C:\\app", "D:\\tools", p => p == fromPath), "没有随包就用 PATH");

        string quoted = Path.Combine("D:\\with space", "ffmpeg.exe");
        Check.Equal(quoted,
            RecordPlan.Locate("", "D:\\none;\"D:\\with space\"", p => p == quoted), "PATH 里的引号要剥掉");

        Check.True(RecordPlan.Locate("C:\\app", "D:\\none", _ => false) == null, "都没有就返回 null");
    }

    /// <summary>
    /// 跟随鼠标：一比一移植 Snow Shot <c>snow-crates/crates/snow-media/src/follow_mouse.rs</c> 的用例
    /// （immediate_follow / jitter_reduction / clamped_edge / smooth_movement_catches_up /
    /// smooth_follow_catches_large_jumps / combined_options / easing_resumes）。
    /// 时间以毫秒显式传入，断言才是确定的；上游用厘米级的 Point 空间，这里全是物理像素，
    /// 所以「取半」按整像素 floor（与上游 PhysicalPixels 分支一致）。
    /// </summary>
    private static void RecordFollow()
    {
        var still = new RecordFollowOptions(SmoothMovement: false, ReduceJitter: false);
        var bounds = new Int32Rect(-200, -400, 1400, 1800);

        // ① 立即跟随：居中取整、夹在显示器里，光标在中间就不动
        var quick = new RecordFollowMotion(new Int32Rect(-600, 200, 101, 79),
            new Int32Rect(-800, 0, 600, 400), still, 0);

        Check.Equal(new Int32Rect(-600, 200, 101, 79), quick.Advance(-550, 239, 0),
            "立即跟随：光标落在正中间就不动");
        Check.Equal(new Int32Rect(-800, 0, 101, 79), quick.Advance(-799, 1, 0),
            "立即跟随：夹到左上边界");
        Check.Equal(new Int32Rect(-301, 321, 101, 79), quick.Advance(-201, 399, 0),
            "立即跟随：夹到右下边界");

        // ② 抑制抖动：中央 5/6 不动，进了外侧 1/12 才重新居中（两条边都要）
        var initial = new Int32Rect(150, 160, 120, 84);
        (int InX, int InY, int EdgeX, int EdgeY)[] jitter =
        {
            (161, 201, 160, 201),
            (259, 201, 260, 201),
            (211, 168, 211, 167),
            (211, 236, 211, 237),
        };

        foreach (var (inX, inY, edgeX, edgeY) in jitter)
        {
            var motion = new RecordFollowMotion(initial, bounds, new RecordFollowOptions(false, true), 0);
            Check.Equal(initial, motion.Advance(inX, inY, 0), $"抑制抖动：({inX},{inY}) 还在中央 5/6 里");

            var changed = motion.Advance(edgeX, edgeY, 0);
            Check.Equal(new Int32Rect(edgeX - 60, edgeY - 42, 120, 84), changed,
                $"抑制抖动：({edgeX},{edgeY}) 进了外侧 1/12 就重新居中");
            Check.Equal(changed, motion.Advance(edgeX, edgeY, 0), "已经居中后同一位置不再抖");
        }

        // ③ 被夹住的那一轴不会因为另一轴触发而移动（不平滑，目标即位置）
        var edge = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(SmoothMovement: false, ReduceJitter: true), 0);

        var centered = edge.Center(-195, 200, 0);
        Check.Equal(-200, centered.X, "居中时夹到显示器左边界");
        Check.Equal(centered, edge.Advance(-195, 212, 0), "夹住的那一轴不动，另一轴也还没到边缘");

        var moved = edge.Advance(-195, 234, 0);
        Check.Equal(-200, moved.X, "横向仍然夹在边界上");
        Check.Equal(194, moved.Y, "纵向已经重新居中");

        // ④ 平滑移动：按 50ms 半衰期逼近，任何帧率下 1 秒内都收敛到目标
        foreach (int fps in new[] { 15, 30, 60, 120 })
        {
            var smooth = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
                new RecordFollowOptions(true, false), 0);
            var start = smooth.Viewport;
            int at20Percent = 0;

            for (int frame = 1; frame <= fps; frame++)
            {
                long now = (long)Math.Round(frame * 1000.0 / fps);
                var crop = smooth.Advance(240, 200, now);

                if (frame == 1)
                    Check.True(start.X < crop.X && crop.X < 190, $"{fps} fps：第一帧只走一小段（{crop.X}）");

                if (frame * 5 == fps) at20Percent = crop.X;
            }

            Check.Equal(190, smooth.Viewport.X, $"{fps} fps：1 秒内收敛到目标");
            Check.True(Math.Abs(at20Percent - 187.5) <= 1,
                $"{fps} fps：20% 的时间走完 94%（{at20Percent}）");
        }

        // ⑤ 快跳：第一帧先把光标拉进视口（右边留 1 像素），之后才慢慢缓过去
        var jump = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80),
            new Int32Rect(-400, -300, 1400, 1000), new RecordFollowOptions(true, false), 0);

        var jumped = jump.Advance(750, -200, 16);
        Check.Equal(new Int32Rect(651, -200, 100, 80), jumped, "快跳：第一帧就把光标包进视口");

        var caught = jump.Advance(760, -200, 16);
        Check.Equal(new Int32Rect(661, -200, 100, 80), caught, "下一帧补到光标右侧留 1 像素");

        var eased = jump.Advance(760, -200, 66);
        Check.True(eased.X > caught.X && eased.X < 700, $"追平之后才开始缓动（{eased.X}）");
        Check.True(eased.Y < jumped.Y && eased.Y > -240, $"纵轴同时在缓动（{eased.Y}）");

        var reversed = jump.Advance(-400, 699, 82);
        Check.Equal(new Int32Rect(-400, 620, 100, 80), reversed, "反向快跳同样先追上");
        Check.False(jump.IsMoving, "追上就停住（不再抖）");

        // ⑥ 两个开关一起开：判断看「目标」而不是当前位置
        var combined = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(true, true), 0);

        Check.Equal(150, combined.Advance(241, 200, 0).X, "光标还在中央 5/6 里就不动");
        Check.Equal(171, combined.Advance(242, 200, 50).X, "进外侧 1/12：重新居中并走完一半");
        Check.True(Math.Abs(combined.Advance(263, 200, 100).X - 181.5) <= 1,
            "已经处在移动视口的右边缘、但没出目标的中区 → 不重定目标");
        Check.True(Math.Abs(combined.Advance(284, 200, 150).X - 207.75) <= 1,
            "出了目标的中区才重定目标");
        Check.Equal(234, combined.Advance(284, 200, 600).X, "半衰期收敛到新目标");

        // ⑦ 暂停恢复：冻住那一刻的位置接着走，暂停时长不算成缓动
        var paused = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(true, false), 0);

        var frozen = paused.Advance(250, 200, 50);
        Check.Equal(175, frozen.X, "50ms（一个半衰期）走完一半");

        paused.RestartClock();
        Check.Equal(frozen, paused.Advance(265, 200, 3050), "恢复第一帧仍停在冻结位置");
        Check.Equal(195, paused.Advance(265, 200, 3100).X, "恢复后按新间隔继续缓动");

        // ⑧ 视口尺寸：放得下原样、放不下等比缩、缩完居中；太小的区域直接拒绝
        Check.Equal(new Int32Rect(760, 390, 400, 300),
            RecordFollowMotion.Fit(new Int32Rect(100, 50, 400, 300), new Int32Rect(0, 0, 1920, 1080)),
            "放得下就只调位置：尺寸不动、居中到显示器里");

        Check.Equal(new Int32Rect(0, 0, 1920, 1080),
            RecordFollowMotion.Fit(new Int32Rect(0, 0, 3840, 2160), new Int32Rect(0, 0, 1920, 1080)),
            "比整屏还大就等比缩到整屏");

        Check.Equal(new Int32Rect(760, 390, 400, 300),
            RecordFollowMotion.Fit(new Int32Rect(5000, 0, 400, 300), new Int32Rect(0, 0, 1920, 1080)),
            "缩完之后在显示器里居中");

        Check.Equal(Int32Rect.Empty,
            RecordFollowMotion.Normalize(new Int32Rect(0, 0, 1, 1), new Int32Rect(0, 0, 1920, 1080)),
            "1 像素的选区直接拒绝");

        var widened = RecordFollowMotion.Normalize(new Int32Rect(10, 10, 4, 300),
            new Int32Rect(0, 0, 1920, 1080));
        Check.Equal(RecordFollowMotion.MinimumExtent, widened.Width, "太窄的选区补到最小宽度");
        Check.Equal(300, widened.Height, "另一边不动");
    }

    /// <summary>
    /// 空闲录屏区域框：边缘命中 / 拖拽（含翻面）/ 最小尺寸 / 移动 / 把手度量。
    /// </summary>
    /// <remarks>
    /// 规则抄自上游 <c>screenrecordingareawindow.cpp</c> + <c>resizegeometry.h</c>：
    /// 6 逻辑像素的抓取带、按住边翻面、最小 10 物理像素、32 逻辑像素的移动把手内缩 8。
    /// </remarks>
    private static void RecordRegion()
    {
        // ① 窗口比选区四周各大 3 物理像素（2 边框 + 1 透明缝）
        Check.Equal(new Int32Rect(97, 97, 206, 156),
            RecordRegionGeometry.FrameBounds(new Int32Rect(100, 100, 200, 150)),
            "区域框把选区和外面那圈一起盖住");

        // ② 抓边：四边 / 四角 / 中间不抓
        var area = new Int32Rect(100, 100, 200, 150);
        Check.Equal(ResizeEdges.Left | ResizeEdges.Top,
            RecordRegionGeometry.EdgesAt(area, 100, 100, 1), "左上角两条边都抓");
        Check.Equal(ResizeEdges.Right | ResizeEdges.Bottom,
            RecordRegionGeometry.EdgesAt(area, 299, 249, 1), "右下角两条边都抓");
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(area, 103, 180, 1), "左边");
        Check.Equal(ResizeEdges.Right, RecordRegionGeometry.EdgesAt(area, 297, 180, 1), "右边");
        Check.Equal(ResizeEdges.Top, RecordRegionGeometry.EdgesAt(area, 200, 103, 1), "上边");
        Check.Equal(ResizeEdges.Bottom, RecordRegionGeometry.EdgesAt(area, 200, 247, 1), "下边");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(area, 200, 175, 1), "中间不抓");
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(area, 98, 180, 1),
            "可见边框在选区外面那 3 像素也算抓边");

        // 小区域：带宽收窄到 1/4（20 → 5）
        var small = new Int32Rect(100, 100, 20, 20);
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(small, 104, 110, 1),
            "小区域带宽收窄到 1/4");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(small, 110, 110, 1),
            "收窄之后中间留得出来");

        // 125% DPI：6 逻辑像素 = 7.5 物理像素
        var scaled = new Int32Rect(0, 0, 200, 200);
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(scaled, 7, 100, 1.25),
            "抓边带宽跟着 DPI 放大");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(scaled, 9, 100, 1.25),
            "放大后 9 像素已经在里面");

        // ③ 拖右边 / 左边 / 角
        var right = RecordRegionGeometry.Resize(area, ResizeEdges.Right, 50, 0, ResizeEdges.Right);
        Check.Equal(new Int32Rect(100, 100, 250, 150), right.Rect, "拖右边：左边不动、变宽");
        Check.Equal(ResizeEdges.Right, right.Effective, "拖右边仍落在右边");

        var left = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 50, 0, ResizeEdges.Left);
        Check.Equal(new Int32Rect(150, 100, 150, 150), left.Rect, "拖左边：右边不动、变窄");

        var corner = RecordRegionGeometry.Resize(area, ResizeEdges.Left | ResizeEdges.Top, 20, 10,
            ResizeEdges.Left | ResizeEdges.Top);
        Check.Equal(new Int32Rect(120, 110, 180, 140), corner.Rect, "拖左上角：宽高同时收");

        // ④ 拖过对边翻面：贴着哪边就换哪边
        var flip = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 250, 0, ResizeEdges.Left);
        Check.Equal(ResizeEdges.Right, flip.Effective, "左边界被拖过右边界 → 换成右边界");
        Check.Equal(new Int32Rect(300, 100, 50, 150), flip.Rect, "翻面后锚点仍是原来的右边");

        var zero = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 200, 0, ResizeEdges.Left);
        Check.Equal(ResizeEdges.Left, zero.Effective, "距离正好为 0：保持上一帧的边（不来回抖）");

        // ⑤ 最小 10 物理像素
        var tiny = RecordRegionGeometry.Resize(area, ResizeEdges.Right, -195, 0, ResizeEdges.Right);
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tiny.Rect.Width, "窄到极限就停在 10 像素");
        var tinyCorner = RecordRegionGeometry.Resize(area, ResizeEdges.Right | ResizeEdges.Bottom,
            -199, -149, ResizeEdges.Right | ResizeEdges.Bottom);
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tinyCorner.Rect.Width, "角上同样有最小宽");
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tinyCorner.Rect.Height, "角上同样有最小高");

        // ⑥ 移动：整块平移、尺寸不变
        Check.Equal(new Int32Rect(80, 130, 200, 150),
            RecordRegionGeometry.Move(area, -20, 30), "拖把手整块挪");

        // ⑦ 抓取带厚度（DIP = 外面那圈 3 像素 + 往里 6 逻辑像素）
        Check.Equal(9.0, Math.Round(RecordRegionGeometry.HitBandDip(area, 1, true), 3),
            "100% 时横向抓取带 3 + 6");
        Check.Equal(7.5, Math.Round(RecordRegionGeometry.HitBandDip(scaled, 2, false), 3),
            "200% 时外面那圈折半、往里仍是 6 逻辑像素");
        Check.Equal(8.0, Math.Round(RecordRegionGeometry.HitBandDip(small, 1, true), 3),
            "小区域按 1/4 收窄");

        // ⑧ 把手：32 逻辑像素、内缩 8；区域太小跟着缩
        var handle = RecordRegionGeometry.HandleMetrics(area, 1);
        Check.Equal(32.0, handle.Side, "把手边长 32");
        Check.Equal(8.0, handle.Inset, "把手离右下角 8");

        var smallHandle = RecordRegionGeometry.HandleMetrics(small, 1);
        Check.Equal(20.0, smallHandle.Side, "区域比把手还小时把手跟着缩");
        Check.Equal(0.0, smallHandle.Inset, "缩到一样大时不再内缩");

        var dpiHandle = RecordRegionGeometry.HandleMetrics(new Int32Rect(0, 0, 200, 200), 2);
        Check.Equal(32.0, dpiHandle.Side, "200% 时把手仍按逻辑像素算");
    }

    /// <summary>
    /// 中断残片：从 <c>ffmpeg -i</c> 的 stderr 里读尺寸与时长（启动抢救靠这个判断能不能救）。
    /// </summary>
    private static void RecordRecoveryProbe()
    {
        const string output =
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'rec.mp4':\n" +
            "  Metadata:\n" +
            "    major_brand     : isom\n" +
            "  Duration: 00:00:12.34, start: 0.000000, bitrate: 812 kb/s\n" +
            "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, progressive), 320x240, 812 kb/s, 30 fps";

        Check.True(RecordRecovery.TryParseProbe(output, out int width, out int height, out int durationMs),
            "从 ffmpeg 输出里读出尺寸与时长");
        Check.Equal(320, width, "宽度");
        Check.Equal(240, height, "高度");
        Check.Equal(12340, durationMs, "时长换成毫秒");

        Check.False(RecordRecovery.TryParseProbe("garbage", out _, out _, out _),
            "读不出来就返回 false（残片会被清掉）");
        Check.False(RecordRecovery.TryParseProbe("Duration: 00:00:01.00\n  Stream: Video: h264, 0x0",
            out _, out _, out _), "尺寸为 0 也算读不出来");
    }

    /// <summary>
    /// 暂停不计时 + 采集历史里录屏条目的形状 / 文案。
    /// 计时断言只比「墙钟 − 活动时长 ≥ 暂停时长」，不给机器快慢留坑。
    /// </summary>
    private static void RecordBookkeeping()
    {
        var wall = Stopwatch.StartNew();
        var controller = new RecordController();
        Check.False(controller.HasStarted, "建出来还没开始计时（面板在等用户点录制）");
        Check.Equal(0, controller.ElapsedMs, "没开始时时长就是 0");
        controller.Start();
        Thread.Sleep(25);
        controller.Pause();
        Thread.Sleep(120);
        controller.Resume();
        Thread.Sleep(25);

        long total = wall.ElapsedMilliseconds;
        long active = controller.ElapsedMs;
        Check.True(total - active >= 100, $"暂停的 120ms 不算进活动时长（差 {total - active}ms）");
        Check.True(active >= 20, "活动时长照常累计");

        Check.False(controller.Finished, "还没结束");
        Check.False(controller.Cancelled, "完成不是取消");
        Check.True(controller.TogglePause(), "切一次变暂停");
        Check.True(controller.IsPaused, "确实是暂停态");
        Check.False(controller.TogglePause(), "再切回继续");

        controller.Stop();
        Check.True(controller.Finished, "点完成后就结束");
        Check.False(controller.Cancelled, "完成 != 取消");

        // 采集历史的录屏条目
        var record = new CaptureEntry
        {
            Id = "r1",
            Kind = CaptureCatalog.KindRecord,
            CreatedAt = "2026-11-16T11:00:00",
            Width = 1920,
            Height = 1080,
            Source = "录屏",
            File = "records/r1.mp4",
            DurationMs = 62000,
            State = "done",
        };

        Check.Equal("录屏", CaptureCatalog.KindLabel(CaptureCatalog.KindRecord), "record 的中文名");
        Check.True(record.IsRecord && !record.IsRecognize, "kind 判定");
        Check.Equal("MP4 · 录屏", CaptureCatalog.SummaryOf(record), "录屏摘要 = 格式 + 完成动作");
        Check.Equal("01:02", record.DurationText, "时长文案");
        Check.Equal("MP4", record.FormatText, "格式文案");
        Check.Equal("records/r1.mp4", CaptureCatalog.RecordRelative("r1", "mp4"), "条目里的相对路径");
        Check.True(CaptureCatalog.FilePath(record).EndsWith("records\\r1.mp4"), "绝对路径拼在数据根下");

        var gif = new CaptureEntry { File = "records/r2.gif", Kind = CaptureCatalog.KindRecord };
        Check.Equal("GIF", gif.FormatText, "GIF 条目");
        Check.Equal("GIF", CaptureCatalog.SummaryOf(gif), "没有完成动作就只写格式");
        Check.Equal("", new CaptureEntry { Kind = CaptureCatalog.KindShot }.FormatText, "截图没有格式");

        Check.Equal("gif", CaptureCatalog.ExtensionOf("shots/a.GIF"), "扩展名统一小写");
        Check.Equal("mp4", CaptureCatalog.ExtensionOf("C:\\a\\b.MP4"), "大写扩展名也认");
        Check.Equal("", CaptureCatalog.ExtensionOf(""), "没有文件名就没有扩展名");

        Check.Equal("820 KB", CaptureCatalog.FormatBytes(840_000), "不到 1 MB 用 KB");
        Check.Equal("3.4 MB", CaptureCatalog.FormatBytes(3_600_000), "MB 保留一位");
        Check.Equal("", CaptureCatalog.FormatBytes(0), "0 字节不写文案");

        // 录屏条目也进保留策略（与截图同一条路）
        var (keep, _) = CaptureCatalog.PlanPrune(
            new[] { record }, new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero), 200, 30);
        Check.Equal(1, keep.Count, "当天的录屏不会被剪掉");
    }

}
