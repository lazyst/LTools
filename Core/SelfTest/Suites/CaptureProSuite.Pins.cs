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

/// <summary>CaptureProSuite 的「贴图：缩放 / 实时裁切 / 分组」部分。</summary>
internal static partial class CaptureProSuite
{
    private static void Pin()
    {
        Check.Equal(1.1, PinMath.NextZoom(1.0, 120), "滚一格放大 10%");
        Check.Equal(0.9, Math.Round(PinMath.NextZoom(1.0, -120), 4), "反向缩回 10%");
        Check.Equal(PinMath.ZoomMin, PinMath.NextZoom(0.1, -120), "缩到下限就停");
        Check.Equal(PinMath.ZoomMax, PinMath.NextZoom(4.0, 120), "放大到上限就停");
        Check.Equal(1.0, PinMath.NextZoom(1.0, 0), "没有滚动就不动");

        Check.Equal(0.5, Math.Round(PinMath.NextOpacity(0.45, 120), 4), "不透明度一格 5%");
        Check.Equal(PinMath.OpacityMin, PinMath.NextOpacity(0.2, -120), "不透明度下限");
        Check.Equal(PinMath.OpacityMax, PinMath.NextOpacity(1.0, 120), "不透明度上限");
        Check.Equal(1.0, PinMath.NextOpacity(1.0, 0), "没有滚动就不动");

        // 锚点：窗口从 400×300 放大到 800×600，窗口里 (100, 75) 那个点在屏幕上必须还在原处
        var origin = new Point(100, 200);
        var at = new Point(100, 75);
        Point moved = PinMath.AnchoredOrigin(origin, new Size(400, 300), new Size(800, 600), at);
        Check.Equal(0d, moved.X, "放大一倍：左上角左移，光标下的点不动");
        Check.Equal(125d, moved.Y, "纵向同理");
        Check.Equal(origin.X + at.X, moved.X + at.X * 2, "屏幕 X 不变（缩小一半算回去也一样）");

        // 出场落点：物理矩形按屏幕缩放换回 DIP，正好盖住刚截的那块
        var placed = PinMath.PlaceAt(new Rect(200, 100, 400, 300), 2);
        Check.Equal(new Rect(100, 50, 200, 150), placed, "物理矩形 ÷ 缩放 = DIP 落点");
        Check.Equal(1d, PinMath.PlaceAt(new Rect(0, 0, 0, 0), 1).Width, "空区域至少 1 DIP（窗口不能是 0 尺寸）");

        // ============ 局部显示：内容在取景框里平移 ============
        var big = new Size(800, 600);
        var view = new Size(400, 300);

        Check.Equal(new Vector(0, 0), PinMath.ClampPan(new Vector(0, 0), big, view), "内容顶到左上时偏移 0");
        Check.Equal(new Vector(0, 0), PinMath.ClampPan(new Vector(80, 60), big, view),
            "往右 / 下拖不动（会露出左上空白）");
        Check.Equal(new Vector(-400, -300), PinMath.ClampPan(new Vector(-900, -900), big, view),
            "往左 / 上拖到底就停（右下不留空白）");
        Check.Equal(new Vector(-200, -150), PinMath.CenterPan(big, view), "进局部显示先看中间那块");

        var small = new Size(200, 100);
        Check.Equal(new Vector(100, 100), PinMath.ClampPan(new Vector(0, 0), small, view),
            "内容比取景框小就居中");
        Check.Equal(new Vector(100, 100), PinMath.ClampPan(new Vector(-999, -999), small, view),
            "小内容也拖不动（居中钉死）");

        // 换缩放时光标下的内容不动（与 AnchoredOrigin 同一件事，只是挪内容而不是挪窗口）
        Vector anchored = PinMath.ZoomAnchoredPan(new Vector(-200, -150), new Point(200, 150), 1, 2);
        Check.Equal(new Vector(-600, -450), anchored, "放大一倍：锚点下的内容补偿一半偏移");
        Check.Equal(200d, anchored.X + 400 * 2, "取景框中心那个点在屏幕上没动");

        Check.Equal(new Size(800, 600), PinMath.ViewportSize(big, new Rect(0, 0, 1000, 700)),
            "内容装得下就按内容大小");
        Check.Equal(new Size(1000, 700), PinMath.ViewportSize(new Size(3000, 3000), new Rect(0, 0, 1000, 700)),
            "内容比工作区大就按工作区");

        // ============ 成组移动：一个位移，谁都不许被推出桌面 ============
        var desktop = new Rect(0, 0, 1000, 800);
        double margin = PinMath.KeepVisible;

        Check.Equal(new Vector(50, 50), PinMath.ConstrainGroupMove(new Vector(50, 50),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin), "桌面里随便拖就原样走");

        Check.Equal(960d, PinMath.ConstrainGroupMove(new Vector(2000, 0),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin).X, "拖到右边界停住（留 40）");

        Check.Equal(-60d, PinMath.ConstrainGroupMove(new Vector(-500, 0),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin).X, "拖到左边界停住（留 40）");

        // 两张一左一右：整组共用一个位移，右边那张先到就先停，左边那张跟着一起停（相对位置不变）
        var nearLeft = new Rect(0, 0, 100, 100);
        var nearRight = new Rect(900, 0, 100, 100);
        Vector group = PinMath.ConstrainGroupMove(new Vector(500, 0), new[] { nearLeft, nearRight },
            desktop, margin);
        Check.Equal(60d, group.X, "成组移动被最靠边那张卡住");
        Check.Equal(960d, nearRight.Left + group.X, "右边那张正好停在「留 40」的位置");
        Check.Equal(60d, nearLeft.Left + group.X, "左边那张跟着走同一个位移（相对位置不变）");

        Check.Equal(new Vector(10, 10), PinMath.ConstrainGroupMove(new Vector(10, 10),
            Array.Empty<Rect>(), desktop, margin), "一张都没有就没有约束（调用方自己判断要不要动）");

        // ============ 缩略图模式：固定小方块，悬停按钮在小窗口 / 方块里都不给 ============
        Check.Equal(83d, PinMath.ThumbnailSide, "缩略图边长固定 83 DIP（照 Snow Shot）");

        Check.True(PinMath.ShowsHoverButtons(400, 300, false), "正常贴图悬停时给 ✕ / 标注");
        Check.True(PinMath.ShowsHoverButtons(PinMath.HoverButtonMinSide, 80, false),
            "正好卡在最小边长（含）仍给按钮");
        Check.False(PinMath.ShowsHoverButtons(PinMath.HoverButtonMinSide - 1, 80, false),
            "窄一点就不给（按钮会盖住内容）");
        Check.False(PinMath.ShowsHoverButtons(80, PinMath.HoverButtonMinSide - 1, false),
            "矮一点也不给");
        Check.False(PinMath.ShowsHoverButtons(PinMath.ThumbnailSide, PinMath.ThumbnailSide, true),
            "缩略图里一律不给（83 已过尺寸门槛，拦它的是「缩略图」这条）");
    }

    /// <summary>
    /// 实时贴图：源窗口状态判定 / 跟住源窗口里的那一块 / 跟着源窗口挪 / 徽标文案。
    /// </summary>
    /// <remarks>
    /// 关键判据有两条：① 跟的是**窗口坐标里的那一块**（<c>_liveRegion</c> 型的参数），窗口挪到哪儿
    /// <see cref="PinLive.Region"/> 的结果都一样（= 拖源窗口时画面不变样）；② 贴图窗口自己靠
    /// <see cref="LiveAnchor"/> 平移，用户拖它只换「挂」的位置，不改变显示哪一块。
    /// </remarks>
    private static void PinLiveCrop()
    {
        Check.Equal(LiveState.Live, PinLive.StateOf(true, false), "窗口在、没最小化 = 实时");
        Check.Equal(LiveState.Minimized, PinLive.StateOf(true, true), "最小化 = 跳过抓帧");
        Check.Equal(LiveState.Closed, PinLive.StateOf(false, false), "窗口没了 = 关闭贴图");
        Check.True(PinLive.ShouldCapture(LiveState.Live), "实时才抓帧");
        Check.False(PinLive.ShouldCapture(LiveState.Minimized), "最小化时不抓帧");
        Check.False(PinLive.ShouldCapture(LiveState.Closed), "关掉了更不抓");

        Check.Equal("● 实时", PinLive.Badge(LiveState.Live), "徽标：实时");
        Check.Equal("源窗口已最小化", PinLive.Badge(LiveState.Minimized), "徽标：最小化");
        Check.Equal("源窗口已关闭", PinLive.Badge(LiveState.Closed), "徽标：关闭");

        // 跟住的那一块：源窗口 (100,100) 起、400×300 里的 (60,40) 200×150（= 屏幕 160,140 200×150）
        var window = new Int32Rect(100, 100, 400, 300);
        var region = new Int32Rect(60, 40, 200, 150);
        var block = PinLive.Screen(window, region);
        Check.Equal(new Int32Rect(160, 140, 200, 150), block, "那一块在屏幕上的位置 = 窗口左上 + 窗口内偏移");

        // ① 整块都露在窗口里：从 (60,40) 裁，铺到贴图 (0,0)
        Check.Equal(
            new LiveCrop(new Int32Rect(60, 40, 200, 150), new Int32Rect(0, 0, 200, 150)),
            PinLive.Region(window, region),
            "整块可见：从窗口那一块裁、铺满贴图");

        // ② 拖源窗口 / 拖贴图都换不掉内容：同一次截图里「窗口坐标的那一块」是定死的
        var moved = new Int32Rect(350, 230, 400, 300);
        Check.Equal(PinLive.Region(window, region), PinLive.Region(moved, region),
            "源窗口挪到哪儿，裁的都是同一块（拖窗口画面不变样）");

        // ③ 窗口缩到比那块还小：只画露得出来的部分，其余留透明
        Check.Equal(
            new LiveCrop(new Int32Rect(60, 40, 40, 60), new Int32Rect(0, 0, 40, 60)),
            PinLive.Region(new Int32Rect(100, 100, 100, 100), region),
            "窗口比那块小：只画窗口里还看得见的部分");

        // ④ 那块有一部分在窗口外（框选跨出了窗口）：露不出来的边透明，内容按窗口内偏移落位
        Check.Equal(
            new LiveCrop(new Int32Rect(0, 0, 160, 120), new Int32Rect(40, 30, 160, 120)),
            PinLive.Region(window, new Int32Rect(-40, -30, 200, 150)),
            "那块左上在窗口外：贴图对应位置留透明、内容不挪位");

        // ⑤ 整块都在窗口外 / 空矩形：没有可画的东西（调用方交一张全透明画布）
        Check.True(PinLive.Region(window, new Int32Rect(900, 900, 200, 150)) == null, "整块挪到窗口外 = 不画");
        Check.True(PinLive.Region(window, new Int32Rect(0, 0, 0, 0)) == null, "空区块 = 不画");
        Check.True(PinLive.Region(new Int32Rect(100, 100, 0, 0), region) == null, "空窗口矩形 = 不画");

        // ⑥ 跟着源窗口走：锚点相对「那一块」，源窗口挪多少，贴图挪多少，尺寸不变
        var pin = new Int32Rect(160, 140, 200, 150);
        var anchor = LiveAnchor.Of(pin, block);
        Check.Equal(new LiveAnchor(0, 0), anchor, "贴图正好盖在那块上：锚点 = (0,0)");

        Check.Equal(new Int32Rect(410, 270, 200, 150),
            anchor.Place(PinLive.Screen(moved, region), 200, 150),
            "源窗口往右下挪 250 / 130：贴图跟着挪同样的距离");
        Check.Equal(anchor,
            LiveAnchor.Of(new Int32Rect(410, 270, 200, 150), PinLive.Screen(moved, region)),
            "跟完之后锚点不变（再挪一次还是同样的相对位置）");

        // ⑦ 用户自己拖过贴图：新锚点按拖完的位置算，之后继续跟着走（显示内容不受影响）
        var dragged = new Int32Rect(20, 30, 200, 150);
        var retaken = LiveAnchor.Of(dragged, block);
        Check.Equal(new LiveAnchor(-140, -110), retaken, "用户拖完贴图：锚点按拖完的位置重算");
        Check.Equal(new Int32Rect(20 + 250, 30 + 130, 200, 150),
            retaken.Place(PinLive.Screen(moved, region), 200, 150),
            "重定锚点之后源窗口再挪，贴图从新位置继续跟着走");
        Check.Equal(PinLive.Region(window, region), PinLive.Region(moved, region),
            "拖过贴图不改变显示哪一块（画面还是从贴图左上角铺开）");

        // ⑧ 最小化：窗口被挪到 (-32000,-32000) 哨兵位置、尺寸只剩标题栏，不能当基准
        var minimized = new Int32Rect(-32000, -32000, 160, 28);
        var desktop = new Int32Rect(0, 0, 1920, 1080);
        Check.False(PinLive.Trackable(minimized, true), "最小化的窗口矩形不能当「那块在哪」的基准");
        Check.False(PinLive.Trackable(minimized, false, desktop),
            "哨兵矩形本身也不可信（它离桌面十万八千里）");
        Check.False(PinLive.Trackable(new Int32Rect(100, 100, 0, 0), false), "空矩形不可信");
        Check.True(PinLive.Trackable(window, false, desktop), "正常窗口可以当基准");
        Check.True(PinLive.Trackable(new Int32Rect(-200, -100, 400, 300), false, desktop),
            "拖到屏幕外的窗口只要还露着一角就算可信（别把贴边 / 跨屏判成不可信）");

        // 老 bug 的复现：拿哨兵算锚点，贴图会被甩到屏幕外（用户报的「最小化再打开实时贴图没了」）
        var sentinelAnchor = LiveAnchor.Of(pin, PinLive.Screen(minimized, region));
        Check.True(Math.Abs(sentinelAnchor.Place(block, 200, 150).X) > 10000,
            "拿哨兵矩形算锚点会把贴图甩到屏幕外（所以最小化期间必须沿用上一次的好值）");
        // 正确做法：最小化期间 block 沿用上一次的值 → 贴图原地不动，还原之后接着跟
        Check.Equal(pin, anchor.Place(block, 200, 150), "最小化期间沿用旧 block：贴图留在原地");
        var afterRestore = new Int32Rect(300, 260, 400, 300);        // 还原回到别的位置
        Check.Equal(new Int32Rect(360, 300, 200, 150),
            anchor.Place(PinLive.Screen(afterRestore, region), 200, 150),
            "还原之后从原地接着跟（锚点没被哨兵污染）");

        // ⑨ 最小化占位：太小的贴图只留图标（两行字会被裁成一团）
        Check.True(PinLive.CoverShowsText(240, 160), "正常大小的贴图放得下占位文字");
        Check.True(PinLive.CoverShowsText(96, 56), "刚好到门槛也放文字");
        Check.False(PinLive.CoverShowsText(95, 160), "太窄只留图标");
        Check.False(PinLive.CoverShowsText(240, 55), "太矮只留图标");

        // ⑩ 「源窗口没了」只信连续几拍：瞬时读不到就把贴图收掉，正是用户报的
        //    「最小化再打开，实时贴图就自动关闭了」（窗口正在销毁重建的那一拍句柄是失效的）。
        Check.Equal(3, PinLive.GoneTicks, "要连续 3 拍（600ms）都读不到才算真的关了");
        Check.True(PinLive.GoneTicks > 1, "至少两拍：单拍误判不能收贴图");

        // ⑪ 句柄没了要不要改跟同程序的另一个窗口：只认「原地重建」，不是随便挑一个
        var live = new Int32Rect(100, 100, 200, 150);                  // 跟住的那块现在在哪
        Check.True(PinLive.CanSucceed(live, new Int32Rect(90, 90, 260, 200)),
            "原地重建的同尺寸窗口可以接替（整块都在里面）");
        Check.True(PinLive.CanSucceed(live, new Int32Rect(150, 150, 400, 300)),
            "盖住一半以上也接（窗口重建后可能挪了一点）");
        Check.True(!PinLive.CanSucceed(live, new Int32Rect(100, 100, 60, 40)),
            "太小、只盖住一小块的不能接");
        Check.True(!PinLive.CanSucceed(live, new Int32Rect(900, 700, 300, 200)),
            "同程序里另一个不相干的窗口不能接（否则关掉一个 Chrome 窗口会跟到别的窗口上）");
        Check.Equal(0L, PinLive.Overlap(live, new Int32Rect(400, 400, 50, 50)), "不相交 = 0");
        Check.Equal(10000L, PinLive.Overlap(live, new Int32Rect(150, 150, 100, 100)),
            "相交面积只算重叠那部分（100×100）");
    }

    /// <summary>
    /// 贴图分组与批量：组名清洗 / 忽略大小写判重、批量另存落盘（不覆盖同名）。
    /// </summary>
    private static void PinGrouping()
    {
        Check.Equal("默认", PinGroups.Default, "默认组名");
        Check.Equal(16, PinGroups.MaxName, "组名最多 16 字");

        Check.Equal("外部", PinGroups.Clean("  外部  "), "去首尾空白");
        Check.Equal("外部 素材", PinGroups.Clean("外部   素材"), "内部连续空白压成一个");
        Check.True(PinGroups.Clean("   ") == null, "全是空白 = 没有名字");
        Check.True(PinGroups.Clean("") == null, "空串 = 没有名字");
        Check.True(PinGroups.Clean(null) == null, "null = 没有名字");
        Check.Equal("一二三四五六七八九十一二三四五六", PinGroups.Clean("一二三四五六七八九十一二三四五六七八"),
            "超长截到 16 字");

        Check.True(PinGroups.Same("External", "external"), "判重忽略大小写");
        Check.False(PinGroups.Same("外部", "外部2"), "不同名不是一组");

        var groups = new[] { "默认", "外部", "素材" };
        Check.Equal("外部", PinGroups.Find("外部", groups), "查到就返回它原本的写法");
        Check.Equal("素材", PinGroups.Find(" 素材 ", groups), "传进来的名字先清洗，带空白也认得出");
        Check.Equal("外部", PinGroups.Find("外部", new[] { "默认", "外部" }), "多个组里挑对那个");
        Check.True(PinGroups.Find("不存在", groups) == null, "没有就是 null");
        Check.True(PinGroups.Find(null, groups) == null, "null 不匹配任何组");

        // 批量另存：一个文件夹里逐个写 png，同名不覆盖
        string dir = Path.Combine(Path.GetTempPath(), "ltools-selftest-pin-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var first = NewPage(4);                    // 16×4
            var second = NewPage(6);                   // 16×6

            var saved = CaptureSave.SaveManyTo(dir, new[] { first, second });
            Check.Equal(2, saved.Count, "两张各写一个文件");
            Check.True(saved.All(File.Exists), "文件真的落盘了");
            Check.True(saved.All(Path.IsPathRooted), "返回的是完整路径");
            Check.True(saved[0].EndsWith("-1.png", StringComparison.Ordinal) &&
                       saved[1].EndsWith("-2.png", StringComparison.Ordinal), "按序号命名");

            using (var stream = File.OpenRead(saved[0]))
            {
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check.Equal(16, decoded.PixelWidth, "写出来的是 png（宽 16）");
                Check.Equal(4, decoded.PixelHeight, "第一张高 4");
            }

            using (var stream = File.OpenRead(saved[1]))
            {
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check.Equal(6, decoded.PixelHeight, "第二张高 6（不是把同一张写两遍）");
            }

            var again = CaptureSave.SaveManyTo(dir, new[] { first });
            Check.True(again[0] != saved[0], "同一秒再存一次自动错开名字");
            Check.True(File.Exists(saved[0]) && File.Exists(again[0]), "前一张没被覆盖");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 工具栏布局：一格多工具（并到左边 / 拆出来）、收起与放回、落盘往返、脏数据与新工具。
    /// </summary>
}
