using System;
using System.Windows;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图「收到屏幕顶部」（P5-i，照上游 <c>presentation/pinned/screenshotpinnedhidetotopcontroller.cpp</c>
/// 与 <c>tests/screenshot_pinned_hide_to_top_tests.cpp</c>）：手柄占位 / 命中区 / 缓动与淡出 /
/// 展开落点 / 换屏重排 / accent 分配。
/// </summary>
/// <remarks>
/// 纯几何与纯函数，**不开窗口**；真窗口的手柄显示 / 悬停展开 / 右键菜单由仓库外冒烟验。
/// 坐标一律物理像素：<see cref="Int32Rect"/> 的右 / 下边界是开区间（上游 QRect 是闭区间）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static ScreenGeometry Screen(int left, int top, int width, int height,
        int workLeft, int workTop, int workWidth, int workHeight, uint dpi) =>
        new(left, top, width, height, workLeft, workTop, workWidth, workHeight, dpi, true);

    private static void HideToTopCases()
    {
        // ============ 常量（照上游 kHandleWidth / kHandleHeight / kHandleGap …） ============
        Check.Equal(30.0, PinHideToTop.HandleWidthDip, "手柄宽 30 DIP");
        Check.Equal(6.0, PinHideToTop.HandleHeightDip, "手柄高 6 DIP");
        Check.Equal(4.0, PinHideToTop.GapDip, "两个手柄之间至少空 4 DIP");
        Check.Equal(2.0, PinHideToTop.HitMarginDip, "命中区左右 / 下方各多 2 DIP");
        Check.Equal(2, PinHideToTop.HandleBorderDevicePixels, "描边 2 物理像素");
        Check.Equal(250, PinHideToTop.EnterDurationMs, "进入动画 250ms");
        Check.Equal(100, PinHideToTop.HideDelayMs, "离开之后 100ms 才收起");
        Check.Equal(13, PinHideToTop.AccentCount, "13 个 accent 色");

        // ============ 手柄占位（上游 placement 段逐条） ============
        var work = new Int32Rect(-1200, -780, 1200, 740);

        Check.Equal(new Int32Rect(-900, -780, 30, 6),
            PinHideToTop.PlaceHandle(work, -900, 1.0, null),
            "首选位置原样保留（含负坐标显示器的工作区顶边）");

        Check.Equal(-866,
            PinHideToTop.PlaceHandle(work, -900, 1.0, new[] { new Int32Rect(-900, -780, 30, 6) }).X,
            "撞上右边的手柄要往右让，间距 4 像素");

        Check.Equal(-64,
            PinHideToTop.PlaceHandle(work, -30, 1.0, new[] { new Int32Rect(-30, -780, 30, 6) }).X,
            "贴住右边缘撞了要往左找");

        Check.Equal(new Int32Rect(20, 0, 30, 6),
            PinHideToTop.PlaceHandle(new Int32Rect(0, 0, 50, 20), 40, 1.0,
                new[] { new Int32Rect(0, 0, 30, 6) }),
            "哪儿都放不下时宁可重叠也要留在工作区里");

        Check.Equal(new Int32Rect(0, 0, 10, 3),
            PinHideToTop.PlaceHandle(new Int32Rect(0, 0, 10, 3), -200, 2.0, null),
            "工作区比手柄还小也要把完整手柄夹进来");

        var fractional = PinHideToTop.PlaceHandle(work, 5000, 1.25, null);
        Check.Equal(38, fractional.Width, "1.25 缩放：宽 37.5 → 38");
        Check.Equal(8, fractional.Height, "1.25 缩放：高 7.5 → 8");
        Check.Equal(-38, fractional.X, "首选位置超出右边就夹到最右");

        var handle = PinHideToTop.PlaceHandle(work, -1200, 1.5, null);
        Check.Equal(new Int32Rect(-1200, -780, 45, 9), handle, "1.5 缩放的手柄尺寸");
        Check.Equal(new Int32Rect(-1200, -780, 48, 12),
            PinHideToTop.HitGeometry(handle, work, 1.5),
            "命中区随缩放放大，并被显示器边缘裁掉");

        Check.True(PinHideToTop.IsEmpty(PinHideToTop.PlaceHandle(PinHideToTop.Empty, 0, 1, null)),
            "没有工作区就占不到手柄");

        // ============ 缓动 / 淡出 / 落点（上游 animationAndHover 段） ============
        Check.Equal(0.0, PinHideToTop.EaseInOutCubic(0), "InOutCubic：0 → 0");
        Check.Equal(0.5, PinHideToTop.EaseInOutCubic(0.5), "InOutCubic：一半还是一半");
        Check.Equal(1.0, PinHideToTop.EaseInOutCubic(1), "InOutCubic：1 → 1");
        Check.Equal(0.0, PinHideToTop.EaseInOutCubic(-5), "InOutCubic 夹住下界");
        Check.Equal(1.0, PinHideToTop.EaseInOutCubic(9), "InOutCubic 夹住上界");

        Check.Equal(170, PinHideToTop.InterpolateTop(300, 40, 0.5), "进入一半：300 → 170 → 40");
        Check.Equal(40, PinHideToTop.InterpolateTop(300, 40, 1), "进入结束贴住工作区顶边");
        Check.Equal(300, PinHideToTop.InterpolateTop(300, 40, 0), "进度 0 还在原位");

        Check.Equal(0.5, PinHideToTop.OpacityFactor(hidden: false, entering: true, progress: 0.5),
            "进入一半 → 淡出因子 0.5");
        Check.Equal(0.0, PinHideToTop.OpacityFactor(hidden: true, entering: false, progress: 0),
            "藏起来 → 全透明");
        Check.Equal(1.0, PinHideToTop.OpacityFactor(hidden: false, entering: false, progress: 0),
            "展开 / 正常 → 不变暗");

        // 手柄落在 (100,40,30,6)：展开的窗口左上角 = 手柄左下角
        var shown = new Point(100 / 1.0, (40 + 6) / 1.0);
        Check.Equal(new Point(100, 46), shown, "展开位置在手柄正下方");

        // ============ 命中 / 相交 / 空 ============
        var rect = new Int32Rect(10, 20, 30, 40);
        Check.True(PinHideToTop.Contains(rect, 10, 20), "左上角在矩形里");
        Check.True(PinHideToTop.Contains(rect, 39, 59), "右下角最后一像素还在");
        Check.False(PinHideToTop.Contains(rect, 40, 30), "右边界是开区间");
        Check.False(PinHideToTop.Contains(rect, 20, 60), "下边界是开区间");

        Check.Equal(new Int32Rect(15, 25, 15, 5),
            PinHideToTop.Intersect(new Int32Rect(0, 0, 30, 30), new Int32Rect(15, 25, 30, 30)),
            "两矩形求交");
        Check.True(PinHideToTop.IsEmpty(
            PinHideToTop.Intersect(new Int32Rect(0, 0, 10, 10), new Int32Rect(20, 20, 10, 10))),
            "不相交返回空");

        // ============ 屏幕判定 ============
        var left = Screen(-1920, 0, 1920, 1080, -1920, 0, 1920, 1040, 120);
        var main = Screen(0, 0, 2560, 1440, 0, 0, 2560, 1400, 120);
        var screens = new[] { left, main };

        Check.Equal(left, PinHideToTop.MonitorAt(screens, -100, 500), "点在左屏里就归左屏");
        Check.Equal(main, PinHideToTop.MonitorAt(screens, 100, 500), "点在主屏里就归主屏");
        Check.Equal(left, PinHideToTop.MonitorAt(screens, -5000, 500), "屏幕外的点归最近的屏");
        Check.True(PinHideToTop.MonitorAt(Array.Empty<ScreenGeometry>(), 0, 0) == null,
            "一块屏都没有返回 null");

        Check.Equal(new Int32Rect(0, 0, 2560, 1400), PinHideToTop.WorkAreaOf(main), "工作区取 rcWork");
        Check.True(PinHideToTop.SameScreen(left, left), "同一块屏");
        Check.False(PinHideToTop.SameScreen(left, main), "不同屏");
        Check.True(PinHideToTop.SameScreen(main,
                Screen(0, 0, 2560, 1440, 0, 60, 2560, 1340, 120)),
            "还是同一块显示器（工作区 / DPI 变了不算换屏，上游按 QScreen 指针判定）");
        Check.False(PinHideToTop.SameScreen(main,
                Screen(0, 0, 1920, 1080, 0, 0, 1920, 1040, 120)),
            "显示器矩形变了才是换屏");

        // ============ 换屏 / 改缩放后重排（上游 reservationsAndRestore 段尾） ============
        Check.Equal(new Int32Rect(555, 60, 45, 9),
            PinHideToTop.PlaceHandle(new Int32Rect(0, 60, 600, 500), 970, 1.5, null),
            "工作区变成 (0,60,600,500) 且缩放 1.5 时手柄重算并夹进右边");

        // 同屏避让 / 跨屏不避让
        var otherScreen = Screen(0, -700, 1000, 600, 0, -700, 1000, 560, 96);
        var onFirst = PinHideToTop.PlaceHandle(new Int32Rect(0, 40, 1000, 700), 100, 1.0, null);
        Check.Equal(100, onFirst.X, "第一块屏首选 100 就是 100");
        Check.Equal(134,
            PinHideToTop.PlaceHandle(new Int32Rect(0, 40, 1000, 700), 100, 1.0,
                new[] { onFirst }).X,
            "同屏第二个手柄往右让 4 + 30");
        Check.Equal(100,
            PinHideToTop.PlaceHandle(PinHideToTop.WorkAreaOf(otherScreen), 100, 1.0,
                Array.Empty<Int32Rect>()).X,
            "别的屏的手柄不参与避让（调用方按屏筛过 occupied）");

        // ============ accent ============
        int a = PinHideToTop.AccentIndexFor("01HZZZZZZZZZZZZZZZZZZZZZZZ");
        Check.Equal(a, PinHideToTop.AccentIndexFor("01HZZZZZZZZZZZZZZZZZZZZZZZ"),
            "同一张贴图每次都是同一个颜色");
        Check.True(a >= 0 && a < PinHideToTop.AccentCount, "accent 序号落在 0–12");
        int emptyAccent = PinHideToTop.AccentIndexFor("");
        Check.Equal(emptyAccent, PinHideToTop.AccentIndexFor(""), "空 Id 也有确定的颜色（每次同一个）");
        Check.True(emptyAccent >= 0 && emptyAccent < PinHideToTop.AccentCount,
            "空 Id 的 accent 也在 0–12");

        bool sawDifferent = false;
        for (int i = 0; i < 64 && !sawDifferent; i++)
            if (PinHideToTop.AccentIndexFor("pin-" + i) != a) sawDifferent = true;
        Check.True(sawDifferent, "不同贴图的颜色会分散（不是全挤在一个）");

        Check.Equal(PinHideToTop.AccentColor(0), PinHideToTop.AccentColor(-3), "序号越界夹回 0");
        Check.Equal(PinHideToTop.AccentColor(12), PinHideToTop.AccentColor(99), "序号过大夹到 12");
        Check.Equal(255, PinHideToTop.AccentColor(4).A, "accent 是不透明的实心色");
    }
}
