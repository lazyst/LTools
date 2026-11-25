using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 全屏画布：相机（缩放 / 平移 / 视图矩阵）、工具条摆位与不透明度、激光笔拖尾。
/// </summary>
/// <remarks>
/// <para>
/// 全部是纯函数 / 纯数据，常量与算法照 <c>globalcanvascontroller.cpp</c> 与
/// <c>snow-recording-effects/src/laser_trail.rs</c>。真窗口（画 / 穿透 / 工具条仍可点 /
/// 拖尾可见）由仓库外 <c>capture-overlay-smoke</c> 的 <c>globalcanvas</c> 段端到端验。
/// </para>
/// <para>
/// 这里任何一条红都说明「缩放曲线 / 锚点 / 落点 / 拖尾收细」变了，别顺手改期望值凑绿。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void GlobalCanvasLayoutCases()
    {
        // ============ 缩放：exp(amount × 0.001)，夹 0.1–8 ============
        Check.Equal(1.0, GlobalCanvasLayout.ZoomAfterWheel(1, 0), "滚轮增量 0 不改缩放");
        Check.True(Math.Abs(GlobalCanvasLayout.ZoomAfterWheel(1, 120) - Math.Exp(0.12)) < 1e-9,
            "一格放大 exp(0.12)");
        Check.True(Math.Abs(GlobalCanvasLayout.ZoomAfterWheel(2, -120) - 2 * Math.Exp(-0.12)) < 1e-9,
            "一格缩小按 exp(-0.12) 递减");
        Check.Equal(GlobalCanvasLayout.MaxZoom, GlobalCanvasLayout.ZoomAfterWheel(7.5, 1000),
            "放大到上限夹 8");
        Check.Equal(GlobalCanvasLayout.MinZoom, GlobalCanvasLayout.ZoomAfterWheel(0.2, -1000),
            "缩小到下限夹 0.1");
        Check.True(Math.Abs(GlobalCanvasLayout.ZoomAfterWheel(double.NaN, 120) - Math.Exp(0.12)) < 1e-9,
            "缩放值坏掉时按 1 起算，再照常乘滚轮增量");
        Check.Equal(1.0, GlobalCanvasLayout.ZoomAfterWheel(1, double.NaN), "滚轮增量坏掉时不动");

        // ============ 平移：一格 120 走 40，精确滚轮直接用像素 ============
        Check.Equal(40.0, GlobalCanvasLayout.PanDistance(120, precise: false), "一格 = 40");
        Check.Equal(20.0, GlobalCanvasLayout.PanDistance(60, precise: false), "半格 = 20");
        Check.Equal(7.5, GlobalCanvasLayout.PanDistance(7.5, precise: true), "精确滚轮直接用像素");

        // 视口 800×600、中心 (100,50)、缩放 1：画布原点落在 (300,250)
        var view = GlobalCanvasLayout.ViewMatrix(100, 50, 1, 800, 600);
        Check.Equal(new Point(300, 250), view.Transform(new Point(0, 0)), "中心偏左上时画布原点右移下移");
        Check.Equal(new Point(400, 300), view.Transform(new Point(100, 50)), "画布中心对上视口中心");

        // 中心对齐视口中心、缩放 1 → 恒等
        var identity = GlobalCanvasLayout.ViewMatrix(400, 300, 1, 800, 600);
        Check.Equal(new Point(0, 0), identity.Transform(new Point(0, 0)), "对齐时画布原点就是窗口原点");
        Check.Equal(1.0, identity.M11, "对齐且不缩放时矩阵是恒等");

        // 缩放 2：画布 1 单位 = 窗口 2 DIP
        var zoomed = GlobalCanvasLayout.ViewMatrix(400, 300, 2, 800, 600);
        Check.Equal(new Point(-400, -300), zoomed.Transform(new Point(0, 0)), "缩放 2 时原点被推出左上");
        Check.Equal(20.0, (zoomed.Transform(new Point(10, 10)) - zoomed.Transform(new Point(0, 0))).X,
            "缩放 2 时画布 10 单位 = 窗口 20 DIP");

        // 往返：窗口 → 画布 → 窗口
        var back = GlobalCanvasLayout.ToCanvas(zoomed, zoomed.Transform(new Point(37, -12)));
        Check.True(Math.Abs(back.X - 37) < 1e-9 && Math.Abs(back.Y + 12) < 1e-9, "视图矩阵往返不丢点");

        // 退化矩阵：原样返回（不抛、不掉点）
        Check.Equal(new Point(5, 6), GlobalCanvasLayout.ToCanvas(new Matrix(0, 0, 0, 0, 0, 0), new Point(5, 6)),
            "不可逆矩阵退回原点输入");

        // 视口矩形：对齐时就是整个视口
        var rect = GlobalCanvasLayout.ViewRect(identity, 800, 600);
        Check.Equal(new Rect(0, 0, 800, 600), rect, "对齐时视口矩形就是画布矩形");

        // ============ 以光标为锚点缩放：光标下那个画布点不动 ============
        var pointer = new Point(120, 90);
        var anchorBefore = GlobalCanvasLayout.ToCanvas(view, pointer);
        double next = GlobalCanvasLayout.ZoomAfterWheel(1, 240);
        var center = GlobalCanvasLayout.ZoomedCenter(view, pointer, next, 800, 600);
        var after = GlobalCanvasLayout.ViewMatrix(center.X, center.Y, next, 800, 600);
        var anchorAfter = GlobalCanvasLayout.ToCanvas(after, pointer);
        Check.True(Math.Abs(anchorBefore.X - anchorAfter.X) < 1e-6 &&
                   Math.Abs(anchorBefore.Y - anchorAfter.Y) < 1e-6, "缩放锚点：光标下的画布点不动");
        Check.True(center.X < 100, "往视口左上缩放时中心往左上挪");

        // 缩放值越界也按夹取后的算（别在别处再夹一次）
        var clamped = GlobalCanvasLayout.ZoomedCenter(view, pointer, 1000, 800, 600);
        Check.True(double.IsFinite(clamped.X) && double.IsFinite(clamped.Y), "超范围缩放也能算出有限中心");

        // ============ 平移：内容跟着手走（中心反向移动 delta / zoom） ============
        var panned = GlobalCanvasLayout.PannedCenter(100, 50, new Vector(30, -20), 1);
        Check.Equal(new Point(70, 70), panned, "平移：往右下拖 30/−20，中心左上挪同样多");
        var pannedZoomed = GlobalCanvasLayout.PannedCenter(100, 50, new Vector(30, -20), 2);
        Check.Equal(new Point(85, 60), pannedZoomed, "缩放 2 时平移位移减半");
        Check.Equal(new Point(100, 50), GlobalCanvasLayout.PannedCenter(100, 50, new Vector(0, 0), 1),
            "没拖动就不动");

        // ============ 工具条落点：右下各留 15 ============
        Check.Equal(new Point(1605, 1005), GlobalCanvasLayout.ToolbarOrigin(1920, 1080, 300, 60),
            "工具条贴右下各留 15");
        Check.Equal(new Point(0, 0), GlobalCanvasLayout.ToolbarOrigin(200, 40, 300, 60),
            "工具条比工作区还大时夹到左上（不出现负坐标）");
        Check.Equal(new Point(15, 15), GlobalCanvasLayout.ToolbarOrigin(100, 50, 70, 20),
            "刚好放下时仍留 15");

        // ============ 穿透时的不透明度 ============
        Check.Equal(1.0, GlobalCanvasLayout.ToolbarOpacity(false, false), "不穿透时工具条不透明");
        Check.Equal(0.32, GlobalCanvasLayout.ToolbarOpacity(true, false), "穿透时工具条 0.32");
        Check.Equal(1.0, GlobalCanvasLayout.ToolbarOpacity(true, true), "穿透但指针停在工具条上恢复 1.0");
        Check.Equal(50, GlobalCanvasLayout.OpacityPollMs, "指针轮询 50ms");

        // ============ 穿透期间窗口要不要吃鼠标（WS_EX_TRANSPARENT） ============
        // 工具条与画布同窗：指针在工具条上时必须把鼠标要回来，否则工具条自己也点不到。
        Check.True(!GlobalCanvasLayout.MouseTransparent(false, false), "不穿透时窗口永远吃鼠标");
        Check.True(!GlobalCanvasLayout.MouseTransparent(false, true), "不穿透时指针在工具条上也照常吃鼠标");
        Check.True(GlobalCanvasLayout.MouseTransparent(true, false), "穿透时指针不在工具条上 = 全透过去");
        Check.True(!GlobalCanvasLayout.MouseTransparent(true, true), "穿透时指针在工具条上 = 不吃鼠标（工具条要能点）");
        Check.True(GlobalCanvasLayout.ClickThroughPollMs <= 16, "穿透期间按帧轮询（慢一拍就会漏点）");
    }

    private static void GlobalCanvasLaserCases()
    {
        // ============ 常量照上游 ============
        Check.Equal(500, GlobalCanvasLaser.LifetimeMs, "拖尾寿命 500ms");
        Check.Equal(50, GlobalCanvasLaser.MaxPoints, "最多 50 个点");
        Check.Equal(2.0, GlobalCanvasLaser.Radius, "笔头半径 2");
        Check.Equal(0.6, GlobalCanvasLaser.Streamline, "流线平滑 0.6");
        Check.Equal(Color.FromRgb(255, 0, 0), GlobalCanvasLaser.Ink, "拖尾是纯红不透明");

        var trail = new GlobalCanvasLaser();

        // 第一个点没有前驱：不产生线段
        Check.True(trail.Observe(new Point(0, 0), 0), "第一个点入队");
        Check.Equal(0, trail.Segments(0).Count, "只有一个点没有线段");

        // 位置没变就不加点
        Check.False(trail.Observe(new Point(0, 0), 16), "位置没变不重复入队");
        Check.Equal(1, trail.Count, "重复采样不涨点数");

        // 流线：新点 = 旧点 + (目标 − 旧点) × 0.6
        Check.True(trail.Observe(new Point(100, 0), 16), "第二个点入队");
        Check.True(Math.Abs(trail.Points[1].X - 60) < 1e-9, "新点往旧点收 40%（0 → 60）");

        var segments = trail.Segments(16);
        Check.Equal(1, segments.Count, "两个点一条线段");
        Check.Equal(new Point(0, 0), segments[0].From, "线段起点是最初那个点");
        Check.True(Math.Abs(segments[0].To.X - 60) < 1e-9, "线段终点是流线之后的点");

        // 长度收细：越靠尾部越粗（上限 2 × 半径 = 4）
        for (int i = 1; i <= 40; i++) trail.Observe(new Point(60 + i * 10, 0), 20 + i);
        var grown = trail.Segments(80);
        Check.True(grown.Count >= 40, "点数够时线段够长");
        Check.True(grown[0].Width < grown[^1].Width, "尾端比首端粗（首端被长度收细）");
        Check.True(grown[^1].Width <= GlobalCanvasLaser.Radius * 2 + 1e-9, "最粗不超过直径 4");
        Check.True(grown[0].Width > 0, "首端也不是零宽（还没过期时有下限）");

        // 整条线宽单调不降（收细是平滑的，不是忽粗忽细）
        bool monotonic = true;
        for (int i = 1; i < grown.Count; i++)
            if (grown[i].Width < grown[i - 1].Width - 1e-9) monotonic = false;
        Check.True(monotonic, "线宽从首端到尾端单调变粗");

        // 过期：500ms 之后整段清掉
        var shortTrail = new GlobalCanvasLaser();
        shortTrail.Observe(new Point(0, 0), 0);
        shortTrail.Observe(new Point(10, 0), 10);
        Check.True(shortTrail.HasActive(10), "刚画完是活的");
        Check.Equal(1, shortTrail.Segments(10).Count, "刚画完有一条线段");
        Check.False(shortTrail.HasActive(GlobalCanvasLaser.LifetimeMs + 11), "过 500ms 就没了");
        Check.Equal(0, shortTrail.Segments(GlobalCanvasLaser.LifetimeMs + 11).Count, "过期后没有线段");
        Check.True(shortTrail.Observe(new Point(20, 0), GlobalCanvasLaser.LifetimeMs + 11), "过期后重新起笔入队");
        Check.Equal(1, shortTrail.Count, "过期后只剩新起的那个点");

        // 点数上限：50
        var capped = new GlobalCanvasLaser();
        for (int i = 0; i < 80; i++) capped.Observe(new Point(i * 3, 0), i);
        Check.Equal(GlobalCanvasLaser.MaxPoints, capped.Count, "点数封顶 50");
        Check.True(capped.Points[^1].X > capped.Points[0].X, "封顶后保留的是最近的一段");

        // Clear
        capped.Clear();
        Check.Equal(0, capped.Count, "Clear 清空拖尾");
        Check.Equal(0, capped.Segments(80).Count, "清空后没有线段");

        // 只有一个点时不算活（线段要两点）
        var single = new GlobalCanvasLaser();
        single.Observe(new Point(0, 0), 0);
        Check.False(single.HasActive(0), "只有一个点不算活动拖尾");
    }
}
