using System;
using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图的点击穿透（控件几何 / 不透明度口径）与「抖动隐藏同组其他贴图」的识别器。
/// </summary>
/// <remarks>
/// <para>
/// 两条都是纯函数，真窗口由仓库外 <c>capture-overlay-smoke</c> 端到端验：几何来自上游
/// <c>screenshotpinnedclickthroughgeometry.cpp</c>，抖动的八个用例**逐条**移植自
/// <c>tests/screenshot_pinned_shake_detector_tests.cpp</c>。
/// </para>
/// <para>
/// 这里任何一条红都说明「穿透控件摆在哪 / 什么时候算抖动」变了，别顺手改期望值凑绿。
/// </para>
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinClickThroughCases()
    {
        var screen = new Rect(0, 0, 1920, 1040);

        // ============ 上方放得下 ============
        var above = PinClickThrough.Layout(new Rect(100, 100, 400, 300), screen);
        Check.True(!above.IsEmpty, "常规位置摆得下");
        Check.Equal(new Rect(252, 52, 152, 32), above.OpacityEditor, "上方放得下：胶囊贴内容右上角内侧");
        Check.Equal(new Rect(412, 52, 32, 32), above.MoveButton, "移动按钮在胶囊右 8");
        Check.Equal(new Rect(452, 52, 32, 32), above.ExitButton, "退出按钮在最右，整条 232 宽");
        Check.Equal(new Point(252, 52), above.Origin, "整条的原点 = 胶囊左上角");

        // ============ 上方放不下就翻到下方 ============
        var below = PinClickThrough.Layout(new Rect(100, 10, 400, 300), screen);
        Check.True(!below.IsEmpty, "贴近顶边也摆得下");
        Check.Equal(326.0, below.Origin.Y, "上方放不下翻到内容下方（10 + 300 + 16）");
        Check.Equal(252.0, below.Origin.X, "翻到下方时横向不变");

        // ============ 上下都放不下：退回内容内侧 ============
        var inside = PinClickThrough.Layout(new Rect(100, 0, 400, 1040), screen);
        Check.True(!inside.IsEmpty, "整屏高的贴图也摆得下");
        Check.Equal(16.0, inside.Origin.Y, "上下都没有空位时退回内容内侧 16");
        Check.Equal(252.0, inside.Origin.X, "退回内侧时横向不变");

        // ============ 右边缘 / 负坐标都夹进工作区 ============
        var right = PinClickThrough.Layout(new Rect(1800, 100, 400, 300), screen);
        Check.Equal(1688.0, right.Origin.X, "越过右边缘时夹到工作区右边界（1920 − 232）");

        var left = PinClickThrough.Layout(new Rect(-300, 100, 200, 100), screen);
        Check.Equal(0.0, left.Origin.X, "越过左边缘时夹到工作区左边");

        var nearBottom = PinClickThrough.Layout(new Rect(100, 1000, 200, 100), screen);
        Check.Equal(952.0, nearBottom.Origin.Y, "贴近底边时摆到内容上方（1000 − 16 − 32）");

        var belowScreen = PinClickThrough.Layout(new Rect(100, 1100, 200, 100), screen);
        Check.Equal(1008.0, belowScreen.Origin.Y, "内容整个在工作区下面时夹到工作区下边界（1040 − 32）");

        // ============ 摆不下就返回空布局（贴图据此退出穿透） ============
        Check.True(PinClickThrough.Layout(new Rect(0, 0, 100, 100), new Rect(0, 0, 231, 1040)).IsEmpty,
            "工作区宽度装不下 232 时返回空");
        Check.True(PinClickThrough.Layout(new Rect(0, 0, 100, 100), new Rect(0, 0, 1920, 31)).IsEmpty,
            "工作区高度装不下 32 时返回空");
        Check.True(PinClickThrough.Layout(Rect.Empty, screen).IsEmpty, "没有内容时返回空");
        Check.True(PinClickThrough.Layout(new Rect(0, 0, 100, 100), Rect.Empty).IsEmpty, "没有工作区时返回空");

        // ============ 常量 = 上游 kControlHeight / kOpacityEditorWidth / kControlSpacing / 16 ============
        Check.Equal(32.0, PinClickThrough.ControlHeight, "控件高 32");
        Check.Equal(152.0, PinClickThrough.OpacityEditorWidth, "胶囊宽 152");
        Check.Equal(232.0, PinClickThrough.TotalWidth, "整条宽 232");

        // ============ 不透明度口径 ============
        Check.Equal(0, PinClickThrough.NormalizeOpacity(-5), "不透明度负数夹到 0");
        Check.Equal(50, PinClickThrough.NormalizeOpacity(50), "不透明度原样");
        Check.Equal(100, PinClickThrough.NormalizeOpacity(150), "不透明度超过 100 夹到 100");
        Check.Equal(50, PinClickThrough.DefaultOpacity, "穿透不透明度出厂 50");

        Check.Equal(0.5, PinClickThrough.EffectiveOpacity(true, 100, 50), "穿透态用穿透不透明度");
        Check.Equal(1.0, PinClickThrough.EffectiveOpacity(false, 100, 50), "普通态用用户不透明度");
        Check.Equal(0.0, PinClickThrough.EffectiveOpacity(true, 100, 0), "穿透不透明度可以是 0");
        Check.Equal(0.6, PinClickThrough.EffectiveOpacity(false, 60, 20), "用户不透明度 60%");
    }

    private static void PinShakeCases()
    {
        // ---- 1. 小幅来回 / 拖拽纠偏不算抖动 ----
        foreach (double amplitude in new[] { 28.0, 40.0 })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            for (int leg = 1; leg <= 12; leg++)
                Check.True(!detector.Sample(leg % 2 == 1 ? amplitude : 0, 0, leg * 40),
                    $"小幅来回 {amplitude} 不许藏别的贴图（第 {leg} 腿）");
        }

        var rounds = new PinShakeDetector();
        rounds.Begin(0, 0);
        for (int leg = 1; leg <= 4; leg++)
            Check.True(!rounds.Sample(leg % 2 == 1 ? 96 : 0, 0, leg * 100),
                "两次大幅来回还不够（第 " + leg + " 腿）");

        // ---- 2. 六个交替腿只认一次 ----
        foreach (var axis in new[] { new Point(1, 0), new Point(0, 1), new Point(1, 1), new Point(-1, -1) })
        {
            var detector = new PinShakeDetector();
            var origin = new Point(-800, 300);
            detector.Begin(origin.X, origin.Y);
            for (int leg = 1; leg <= 6; leg++)
            {
                var position = origin + new Vector(axis.X * (leg % 2 == 1 ? 64 : 0),
                    axis.Y * (leg % 2 == 1 ? 64 : 0));
                Check.True(detector.Sample(position.X, position.Y, leg * 80) == (leg == 6),
                    $"六腿必须只认一次（轴 {axis.X},{axis.Y}，第 {leg} 腿）");
                Check.True(!detector.Sample(position.X, position.Y, leg * 80), "同一个点重复采样不许再触发");
            }

            Check.True(!detector.Sample(origin.X + axis.X * 100, origin.Y + axis.Y * 100, 650),
                "触发后继续移动保持锁存");
            detector.Reset();
            Check.True(!detector.Sample(origin.X, origin.Y, 700), "Reset 之后不武装");

            detector.Begin(origin.X, origin.Y);
            for (int leg = 1; leg <= 6; leg++)
            {
                double x = origin.X + axis.X * (leg % 2 == 1 ? 64 : 0);
                double y = origin.Y + axis.Y * (leg % 2 == 1 ? 64 : 0);
                Check.True(detector.Sample(x, y, leg * 80) == (leg == 6), "新的一段拖动可以再触发一次");
            }
        }

        // ---- 3. 距离与时间的边界都是闭区间 ----
        foreach (double amplitude in new[] { 47.99, 48.0, 48.01 })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            bool recognized = false;
            for (int leg = 1; leg <= 12; leg++)
                recognized |= detector.Sample(leg % 2 == 1 ? amplitude : 0, 0, leg * 40);
            Check.True(recognized == (amplitude >= 48), $"阈值 48 含界线（幅度 {amplitude}）");
        }

        foreach (int last in new[] { 699, 700, 701 })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            for (int leg = 1; leg <= 5; leg++)
                Check.True(!detector.Sample(leg % 2 == 1 ? 48 : 0, 0, leg * 100),
                    "五腿凑不满三个来回");
            Check.True(detector.Sample(0, 0, last) == (last <= 700), $"整串 700ms 含界线（{last}）");
        }

        foreach (int first in new[] { 249, 250, 251 })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            Check.True(!detector.Sample(48, 0, first), "第一腿永远不够");
            Check.True(!detector.Sample(0, 0, first + 50), "第二腿永远不够");
            Check.True(!detector.Sample(48, 0, first + 100), "第三腿永远不够");
            Check.True(!detector.Sample(0, 0, first + 150), "第四腿永远不够");
            Check.True(!detector.Sample(48, 0, first + 200), "第五腿永远不够");
            Check.True(detector.Sample(0, 0, first + 250) == (first <= 250),
                $"单腿 250ms 含界线（起点 {first}）");
        }

        // ---- 4. 普通移动 / 手抖 / 慢速锯齿都不算 ----
        var straight = new PinShakeDetector();
        var jitter = new PinShakeDetector();
        var slow = new PinShakeDetector();
        straight.Begin(0, 0);
        jitter.Begin(0, 0);
        slow.Begin(0, 0);
        for (int i = 1; i < 2000; i++)
        {
            Check.True(!straight.Sample(i * 3, i * 2, i * 4), "一路扫过去不算抖动");
            Check.True(!jitter.Sample(i % 5 - 2, i % 7 - 3, i * 2), "手抖不算抖动");
            Check.True(!slow.Sample(i % 2 == 1 ? 80 : 0, 0, i * 300), "慢速锯齿不算抖动");
        }

        var mixedAxes = new PinShakeDetector();
        mixedAxes.Begin(0, 0);
        var mixed = new[]
        {
            new Point(64, 0), new Point(0, 64), new Point(64, 0),
            new Point(0, 64), new Point(64, 0), new Point(64, 64),
        };
        for (int i = 0; i < mixed.Length; i++)
            Check.True(!mixedAxes.Sample(mixed[i].X, mixed[i].Y, (i + 1) * 80),
                "不同轴的来回不许相加");

        // ---- 5. 极值点忽略小幅反向 ----
        var extrema = new PinShakeDetector();
        extrema.Begin(0, 0);
        var samples = new[]
        {
            new Point(64, 0), new Point(61, 0), new Point(69, 0), new Point(5, 0),
            new Point(8, 0), new Point(4, 0), new Point(68, 0), new Point(67, 0),
            new Point(4, 0), new Point(7, 0), new Point(68, 0), new Point(65, 0),
            new Point(4, 0),
        };
        for (int i = 0; i < samples.Length; i++)
            Check.True(extrema.Sample(samples[i].X, samples[i].Y, (i + 1) * 40) == (i + 1 == samples.Length),
                "真正的折返点要吞掉阈值内的小幅回摆（第 " + (i + 1) + " 个采样）");

        // ---- 6. 停顿与过长的一腿会作废旧动作 ----
        var pause = new PinShakeDetector();
        pause.Begin(0, 0);
        Check.True(!pause.Sample(64, 0, 50), "第一腿");
        Check.True(!pause.Sample(0, 0, 100), "第二腿");
        Check.True(!pause.Sample(0, 0, 351), "停顿太久要重来");
        Check.True(!pause.Sample(64, 0, 400), "重新数第一腿");
        Check.True(!pause.Sample(0, 0, 450), "重新数第二腿");
        Check.True(!pause.Sample(64, 0, 500), "重新数第三腿");
        Check.True(!pause.Sample(0, 0, 550), "重新数第四腿");
        Check.True(!pause.Sample(64, 0, 600), "重新数第五腿");
        Check.True(pause.Sample(0, 0, 650), "停顿之后完整甩一次照样触发");

        var longLeg = new PinShakeDetector();
        longLeg.Begin(0, 0);
        for (int i = 1; i <= 6; i++)
            Check.True(!longLeg.Sample(i * 64, 0, i * 50), "一直朝一个方向走（第 " + i + " 个采样）");
        Check.True(!longLeg.Sample(320, 0, 350), "长腿不能算进抖动");
        Check.True(!longLeg.Sample(384, 0, 400), "重新数第二腿");
        Check.True(!longLeg.Sample(320, 0, 450), "重新数第三腿");
        Check.True(!longLeg.Sample(384, 0, 500), "重新数第四腿");
        Check.True(!longLeg.Sample(320, 0, 550), "重新数第五腿");
        Check.True(longLeg.Sample(384, 0, 600), "长扫之后重新甩六腿照样触发");

        // ---- 7. 采样频率不影响结果 ----
        foreach (int subdivisions in new[] { 1, 2, 4, 20, 100 })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            int recognized = 0;
            for (int leg = 0; leg < 6; leg++)
            {
                for (int i = 1; i <= subdivisions; i++)
                {
                    double portion = (double)i / subdivisions;
                    double x = leg % 2 == 1 ? 64 * (1 - portion) : 64 * portion;
                    if (detector.Sample(x, 0, leg * 80 + i * 80 / subdivisions)) recognized++;
                }
            }

            Check.Equal(1, recognized, $"粗采样与高采样必须一致（{subdivisions} 细分）");
        }

        var irregular = new PinShakeDetector();
        irregular.Begin(0, 0);
        Check.True(!irregular.Sample(64, 1, 71), "不规则第一腿");
        Check.True(!irregular.Sample(0, 2, 166), "不规则第二腿");
        Check.True(!irregular.Sample(64, 0, 249), "不规则第三腿");
        Check.True(!irregular.Sample(0, 1, 341), "不规则第四腿");
        Check.True(!irregular.Sample(64, 0, 424), "不规则第五腿");
        Check.True(irregular.Sample(0, 1, 503), "不规则第六腿照样触发");

        // ---- 8. 无效采样不能把旧的动作串完 ----
        foreach (double invalid in new[] { double.PositiveInfinity, double.NaN })
        {
            var detector = new PinShakeDetector();
            detector.Begin(0, 0);
            Check.True(!detector.Sample(64, 0, 50), "第一腿");
            Check.True(!detector.Sample(invalid, 0, 60), "非有限数会让识别器重新武装");
            Check.True(!detector.Sample(0, 0, 100), "无效位置不能变成折返");
        }

        var backwards = new PinShakeDetector();
        backwards.Begin(0, 0);
        Check.True(!backwards.Sample(64, 0, 100), "第一腿");
        Check.True(!backwards.Sample(0, 0, 90), "时间倒流会重开一段");
        Check.True(!backwards.Sample(64, 0, 150), "新的第一腿");
        Check.True(!backwards.Sample(0, 0, 200), "新的第二腿");
        Check.True(!backwards.Sample(64, 0, 250), "新的第三腿");
        Check.True(!backwards.Sample(0, 0, 300), "新的第四腿");
        Check.True(!backwards.Sample(64, 0, 350), "新的第五腿");
        Check.True(backwards.Sample(0, 0, 400), "时钟重来之后新的一串照样触发");
    }
}
