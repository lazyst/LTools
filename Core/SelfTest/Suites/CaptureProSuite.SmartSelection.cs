using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// P3-d 智能选区：命中路径状态机（层级 / 细化 / Tab / 按下冻结）与换层过渡插值。
/// </summary>
/// <remarks>
/// 期望值逐条移植上游 <c>tests/screenshot_smart_selection_transition_tests.cpp</c>（14 组）与
/// <c>tests/screenshot_capture_workflow_tests.cpp</c> 的
/// <c>phasedSelectionPreservesUserIntent</c> / <c>intelligentSelectionTargetsPreserveElementPathBehavior</c>。
/// 覆盖层上的真实交互（悬停 / 滚轮 / 单击确认 / 拖框）走仓库外冒烟与真机 e2e。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static Rect Interpolated(Rect start, Rect target, double progress)
    {
        double eased = 1.0 - (1.0 - progress) * (1.0 - progress);
        return new Rect(
            start.X + (target.X - start.X) * eased,
            start.Y + (target.Y - start.Y) * eased,
            start.Width + (target.Width - start.Width) * eased,
            start.Height + (target.Height - start.Height) * eased);
    }

    private static bool Near(Rect actual, Rect expected)
    {
        const double tolerance = 0.000001;
        return Math.Abs(actual.X - expected.X) < tolerance &&
               Math.Abs(actual.Y - expected.Y) < tolerance &&
               Math.Abs(actual.Width - expected.Width) < tolerance &&
               Math.Abs(actual.Height - expected.Height) < tolerance;
    }

    private static readonly Rect SmartFirst = new(10, 20, 300, 200);
    private static readonly Rect SmartSecond = new(110, 70, 500, 400);
    private static readonly Rect SmartThird = new(30, 200, 100, 80);

    /// <summary>上游 14 组过渡用例：默认直接落位、绝对时间 + 二次缓出、改档保留进度、0.4 容差。</summary>
    private static void SmartTransitionCases()
    {
        // 1) 第一次预选直接显示，不动画
        {
            Rect presented = Rect.Empty;
            int updates = 0;
            var fx = new SmartSelectionTransition(r => { presented = r; updates++; });

            Check.Equal(SelectionTransition.Soft, fx.Animation, "过渡默认柔和");
            Check.True(fx.Update(SmartFirst, true, 1000), "第一次预选必须改变画面");
            Check.Equal(1, updates, "第一次预选立刻落地（回调一次）");
            Check.Equal(SmartFirst, presented, "第一次预选不做插值");
            Check.False(fx.IsRunning, "第一次预选不排动画");
            Check.False(fx.Advance(2000), "直接落位之后没有后续帧");
        }

        // 2) 按绝对时间 + p*(2-p) 缓出，8ms / 乱序 / 迟到帧都对得上
        {
            Rect presented = Rect.Empty;
            int updates = 0;
            var fx = new SmartSelectionTransition(r => { presented = r; updates++; });

            Check.True(fx.Update(SmartFirst, true, 1000), "首个预选落地");
            Check.True(fx.Update(SmartSecond, true, 1100), "换目标要开始过渡");
            Check.True(fx.IsRunning, "第二次预选要做动画");
            Check.Equal(SmartFirst, presented, "过渡从当前显示的那块起步");
            Check.False(fx.Advance(1100), "起点那一拍不重复发帧");

            Check.True(fx.Advance(1108), "8ms 一帧要推进显示");
            Check.True(Near(presented, Interpolated(SmartFirst, SmartSecond, 8.0 / 101)),
                "柔和档保持 101ms 的二次几何");
            Check.True(fx.Advance(1116), "高刷帧不必等下一次计时");
            Check.True(Near(presented, Interpolated(SmartFirst, SmartSecond, 16.0 / 101)),
                "高刷帧仍按配置时长走");
            Check.True(presented.Left < SmartFirst.Left + (SmartSecond.Left - SmartFirst.Left) * 0.5,
                "前 16ms 只走完一小半（二次缓出前段更慢）");

            int afterFrame = updates;
            Check.False(fx.Advance(1116), "同一时间戳不重复发帧");
            Check.False(fx.Advance(1110), "更早的时间戳不许倒放");
            Check.Equal(afterFrame, updates, "重复 / 更早的帧不该有回调");

            Check.True(fx.Advance(1148), "迟到帧要把时间全算进去");
            Check.True(Near(presented, Interpolated(SmartFirst, SmartSecond, 48.0 / 101)),
                "迟到帧按实际耗时追平");
            Check.True(fx.IsRunning, "没到时长就继续跑");
            Check.True(fx.Advance(1200), "100ms 那一拍还要动");
            Check.True(Near(presented, Interpolated(SmartFirst, SmartSecond, 100.0 / 101)) && fx.IsRunning,
                "满 101ms 之前一直按旧算法走");
            Check.True(fx.Advance(1201), "最后一拍精确落到目标");
            Check.False(fx.IsRunning, "到时长就停");
            Check.Equal(SmartSecond, presented, "过渡必须精确结束在目标上");
            Check.False(fx.Advance(100000), "结束之后不再发帧");
        }

        // 3) 帧率不改变轨迹
        {
            var regular = new SmartSelectionTransition();
            var irregular = new SmartSelectionTransition();
            var delayed = new SmartSelectionTransition();

            foreach (var fx in new[] { regular, irregular, delayed })
            {
                fx.Update(SmartFirst, true, 1000);
                fx.Update(SmartSecond, true, 1100);
            }

            for (long elapsed = 8; elapsed <= 64; elapsed += 8) regular.Advance(1100 + elapsed);
            foreach (long elapsed in new long[] { 5, 17, 41, 64 }) irregular.Advance(1100 + elapsed);
            delayed.Advance(1164);

            var expected = Interpolated(SmartFirst, SmartSecond, 64.0 / 101);
            Check.True(Near(regular.Displayed, expected) &&
                       Near(irregular.Displayed, expected) &&
                       Near(delayed.Displayed, expected),
                "规律 / 不规律 / 迟到帧在同一时刻得到同一几何");
        }

        // 4) 中途换目标：从当前显示的那一块接着走
        {
            Rect presented = Rect.Empty;
            var fx = new SmartSelectionTransition(r => presented = r);

            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1100);
            fx.Update(SmartThird, true, 1150);

            var retargetStart = Interpolated(SmartFirst, SmartSecond, 50.0 / 101);
            Check.True(Near(presented, retargetStart), "换目标先把上一段按当前时间采样");

            fx.Advance(1200);
            Check.True(Near(presented, Interpolated(retargetStart, SmartThird, 50.0 / 101)),
                "新一段从当前几何起步");
            fx.Advance(100000);
            Check.Equal(SmartThird, presented, "迟到帧直接落到最新目标");
            Check.False(fx.IsRunning, "终点那一拍停掉动画");
        }

        // 5) 换目标时给了更早的时间戳：时钟不许倒退
        {
            var fx = new SmartSelectionTransition();
            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1100);
            fx.Advance(1116);
            var current = fx.Displayed;

            fx.Update(SmartThird, true, 1108);
            Check.Equal(current, fx.Displayed, "更早的时间戳不许把画面倒回去");

            fx.Advance(1124);
            Check.True(Near(fx.Displayed, Interpolated(current, SmartThird, 8.0 / 101)),
                "更早的时间戳不许重复计已经用掉的时间");
        }

        // 6) 亚像素变化直接吸附（四条边都在 0.4 以内）
        {
            int updates = 0;
            var fx = new SmartSelectionTransition(_ => updates++);
            var target = new Rect(SmartFirst.X + 0.25, SmartFirst.Y - 0.25, SmartFirst.Width, SmartFirst.Height);

            fx.Update(SmartFirst, true, 1000);
            Check.True(fx.Update(target, true, 1100), "很近的目标也要报告变化");
            Check.True(fx.Displayed == target && !fx.IsRunning,
                "四条边都在 0.4 像素以内就吸附，不排动画");
            Check.True(!fx.Advance(1116) && updates == 2, "吸附只通知一次、不留动画帧");
        }

        // 7) 吸附要逐边检查（不是按「位置 + 尺寸」分两组）
        {
            var first = new Rect(0, 0, 100, 100);
            var targets = new[]
            {
                new Rect(0.25, 0, 100.25, 100),
                new Rect(0, 0.25, 100, 100.25),
                new Rect(0.5, 0, 99.75, 100),
                new Rect(0, 0.5, 100, 99.75),
            };

            foreach (var target in targets)
            {
                var fx = new SmartSelectionTransition();
                fx.Update(first, true, 1000);
                fx.Update(target, true, 1100);
                Check.True(fx.IsRunning, "任意一条边超过 0.4 就要做动画");
                fx.Advance(1101);
                Check.True(fx.IsRunning && Near(fx.Displayed, Interpolated(first, target, 1.0 / 101)),
                    "吸附必须逐条边比，而不是位置 / 尺寸分开比");
                fx.Advance(1201);
                Check.True(!fx.IsRunning && fx.Displayed == target, "小幅动画最后也要精确落位");
            }
        }

        // 8) 关掉动画：每次变化直接落位
        {
            Rect presented = Rect.Empty;
            int updates = 0;
            var fx = new SmartSelectionTransition(r => { presented = r; updates++; });

            fx.SetAnimation(SelectionTransition.None, 1000);
            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1008);

            Check.Equal(SelectionTransition.None, fx.Animation, "关掉的档位保持关闭");
            Check.False(fx.IsRunning, "关掉动画就不跑");
            Check.Equal(SmartSecond, presented, "关掉动画直接显示最新目标");
            Check.False(fx.Update(SmartSecond, true, 1016), "目标没变不再报告变化");
            Check.Equal(2, updates, "关掉动画时每次真变化只回调一次");
        }

        // 9) 动画跑到一半关掉：立刻落到目标，不留残帧
        {
            Rect presented = Rect.Empty;
            int updates = 0;
            var fx = new SmartSelectionTransition(r => { presented = r; updates++; });

            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1100);
            fx.Advance(1116);
            fx.SetAnimation(SelectionTransition.None, 1116);
            int afterDisable = updates;

            Check.Equal(SelectionTransition.None, fx.Animation, "运行中改档要生效");
            Check.False(fx.IsRunning, "改到关掉要停掉动画");
            Check.Equal(SmartSecond, presented, "改到关掉要立刻到目标");
            Check.False(fx.Advance(1201), "关掉之后没有残帧");
            Check.Equal(afterDisable, updates, "关掉之后不再回调");
        }

        // 10) 离开智能取景 / 清空选区都算「第一次」：下一次直接落位
        {
            Rect presented = Rect.Empty;
            var fx = new SmartSelectionTransition(r => presented = r);
            var manual = new Rect(30, 40, 100, 80);
            var nextFirst = new Rect(400, 300, 250, 180);

            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1100);
            fx.Update(manual, false, 1116);
            Check.Equal(manual, presented, "离开智能取景立刻显示手框几何");
            Check.False(fx.Advance(1201), "离开智能取景要取消所有动画帧");

            fx.Update(nextFirst, true, 1300);
            Check.False(fx.IsRunning, "重新进入智能取景的第一块不动画");
            Check.Equal(nextFirst, presented, "重新进入的第一块直接显示");

            fx.Update(SmartSecond, true, 1400);
            fx.Update(Rect.Empty, true, 1416);
            Check.True(presented.IsEmpty, "清空选区立刻显示空");
            Check.False(fx.Advance(1501), "清空要取消所有动画帧");

            fx.Update(SmartFirst, true, 1600);
            Check.False(fx.IsRunning, "清空之后的第一块不动画");
            Check.Equal(SmartFirst, presented, "清空之后的第一块直接显示");
        }

        // 11) 目标没变 / 手框几何重复都不发回调
        {
            int updates = 0;
            var fx = new SmartSelectionTransition(_ => updates++);

            Check.True(fx.Update(SmartFirst, true, 1000), "第一个预选报告变化");
            Check.False(fx.Update(SmartFirst, true, 1050), "目标没变不重启");
            Check.Equal(1, updates, "目标没变不回调");

            fx.Update(SmartSecond, true, 1100);
            Check.False(fx.Update(SmartSecond, true, 1150), "运行中重复同一个目标不重启");
            Check.Equal(1, updates, "投递目标本身不推进帧");

            Check.True(fx.Advance(1150), "画面帧仍要推进运行中的目标");
            Check.False(fx.Update(fx.Displayed, false, 1150), "手框几何没变不重发");
            Check.False(fx.IsRunning, "手框几何要停掉运行中的动画");
            Check.False(fx.Update(fx.Displayed, false, 1200), "重复的手框几何仍然没变");
            Check.Equal(2, updates, "手框重复不回调");
        }

        // 12) 快速 / 柔和都是固定时长（与距离无关）
        {
            foreach (var animation in new[] { SelectionTransition.Fast, SelectionTransition.Soft })
            {
                int duration = animation == SelectionTransition.Fast
                    ? SmartSelectionTransition.FastDurationMs
                    : SmartSelectionTransition.SoftDurationMs;

                Check.Equal(animation == SelectionTransition.Fast ? 67 : 101, duration,
                    "快速 67ms / 柔和 101ms");

                foreach (var target in new[]
                         {
                             new Rect(SmartFirst.X + 1, SmartFirst.Y - 1, SmartFirst.Width, SmartFirst.Height),
                             new Rect(-10000, -5000, 20000, 12000),
                         })
                {
                    var fx = new SmartSelectionTransition();
                    fx.SetAnimation(animation, 1000);
                    fx.Update(SmartFirst, true, 1000);
                    fx.Update(target, true, 1100);

                    fx.Advance(1116);
                    Check.True(Near(fx.Displayed, Interpolated(SmartFirst, target, 16.0 / duration)),
                        "快速 / 柔和都用二次几何，时长固定 67 / 101ms");

                    fx.Advance(1100 + duration - 1);
                    Check.True(fx.IsRunning &&
                               Near(fx.Displayed, Interpolated(SmartFirst, target, (duration - 1.0) / duration)),
                        "距离不影响时长");

                    Check.True(fx.Advance(1100 + duration) && !fx.IsRunning && fx.Displayed == target,
                        "任何大小的选区都在选定时长精确落位");
                    Check.False(fx.Advance(100000), "结束的动画不再发帧");
                }
            }
        }

        // 13) 换档保留已经走掉的进度，剩余时间用新时长
        {
            var fx = new SmartSelectionTransition();
            fx.Update(SmartFirst, true, 1000);
            fx.Update(SmartSecond, true, 1100);

            fx.SetAnimation(SelectionTransition.Fast, 1124);
            var current = Interpolated(SmartFirst, SmartSecond, 24.0 / 101);
            Check.True(Near(fx.Displayed, current), "换档先把旧时长的时间算掉");

            fx.Advance(1132);
            Check.True(Near(fx.Displayed, Interpolated(SmartFirst, SmartSecond, 24.0 / 101 + 8.0 / 67)),
                "换档之后保留进度、用新时长接着走");

            fx.SetAnimation(SelectionTransition.Fast, 1140);
            fx.Advance(1148);
            Check.True(Near(fx.Displayed, Interpolated(SmartFirst, SmartSecond, 24.0 / 101 + 24.0 / 67)),
                "重复设置同一档不许重置进度");

            fx.SetAnimation(SelectionTransition.None, 1148);
            fx.SetAnimation(SelectionTransition.Soft, 1200);
            fx.Update(SmartFirst, true, 1200);
            fx.Advance(1216);
            Check.True(Near(fx.Displayed, Interpolated(SmartSecond, SmartFirst, 16.0 / 101)),
                "重新开启动画要从最后直接显示的那块起步");

            fx.SetAnimation(SelectionTransition.Fast, 1216);
            fx.SetAnimation((SelectionTransition)(-1), 1216);
            Check.Equal(SelectionTransition.Soft, fx.Animation, "非法档位回落到柔和");
        }

        // 14) 换档不倒退时钟；换目标从零进度重算
        {
            var first = SmartFirst;
            var target = SmartSecond;
            var nextTarget = new Rect(-100, -200, 80, 60);

            foreach (var animation in new[] { SelectionTransition.Fast, SelectionTransition.Soft })
            {
                var fx = new SmartSelectionTransition();
                fx.SetAnimation(animation, 1000);
                fx.Update(first, true, 1000);
                fx.Update(target, true, 1100);
                fx.Advance(1116);

                var beforeSwitch = fx.Displayed;
                var next = animation == SelectionTransition.Fast
                    ? SelectionTransition.Soft
                    : SelectionTransition.Fast;
                int duration = animation == SelectionTransition.Fast
                    ? SmartSelectionTransition.FastDurationMs
                    : SmartSelectionTransition.SoftDurationMs;
                int nextDuration = next == SelectionTransition.Fast
                    ? SmartSelectionTransition.FastDurationMs
                    : SmartSelectionTransition.SoftDurationMs;

                fx.SetAnimation(next, 1108);
                Check.Equal(beforeSwitch, fx.Displayed, "更早的换档时间戳不许把曲线倒回去");

                fx.Advance(1124);
                var current = Interpolated(first, target, 16.0 / duration + 8.0 / nextDuration);
                Check.True(Near(fx.Displayed, current), "两个方向的换档都保留进度、不重复计时");

                fx.Update(nextTarget, true, 1124);
                fx.Advance(1132);
                Check.True(Near(fx.Displayed, Interpolated(current, nextTarget, 8.0 / nextDuration)),
                    "换档之后换目标要从零进度开新一段");

                fx.Advance(1124 + nextDuration);
                Check.True(fx.Displayed == nextTarget && !fx.IsRunning,
                    "换档之后按新时长精确结束");
            }
        }
    }

    /// <summary>上游两份状态机用例：命中路径、细化只增不减、显式层级、Tab 切目标、按下冻结。</summary>
    private static void SmartSelectionModelCases()
    {
        // 1) 上游 phasedSelectionPreservesUserIntent
        {
            var model = new SmartSelectionModel();
            var bounds = new Rect(0, 0, 100, 100);
            var parent = new Rect(0, 0, 80, 80);
            var child = new Rect(0, 0, 40, 40);
            var leaf = new Rect(0, 0, 20, 20);

            model.BeginSession(SmartSelectionMode.WindowSubElement);
            Check.True(model.ApplyHitPath(new[] { parent, bounds }, bounds, 1), "初始命中路径");
            Check.True(model.ApplyRefinementPath(new[] { child, parent, bounds }, bounds, 1) &&
                       model.CurrentSelection == child,
                "光标不动时细化自动往深处走");
            Check.False(model.ApplyRefinementPath(new[] { child, parent, bounds }, bounds, 1),
                "重复的细化不重画");
            Check.False(
                model.ApplyRefinementPath(new[] { leaf, child, new Rect(0, 0, 90, 90), bounds }, bounds, 1),
                "换了另一条祖先链不许顶掉正在显示的路径");

            Check.True(model.SelectIndex(1), "明确选父级");
            Check.True(model.ApplyRefinementPath(new[] { leaf, child, parent, bounds }, bounds, 1) &&
                       model.CurrentSelection == parent && model.Index == 2,
                "细化要按几何保住用户明确选中的那一块");

            model.BeginPress(new Point(5, 5), parent);
            Check.True(!model.ApplyRefinementPath(
                           new[] { new Rect(0, 0, 10, 10), leaf, child, parent, bounds }, bounds, 1) &&
                       model.TakePressSelection() == parent,
                "按下期间冻结细化与确认几何");

            model.ResetTargetPreference();
            Check.True(model.ApplyRefinementPath(
                           new[] { new Rect(0, 0, 10, 10), leaf, child, parent, bounds }, bounds, 1) &&
                       model.Index == 0,
                "目标变了要恢复自动选择");

            Check.False(model.ApplyRefinementPath(
                    new[]
                    {
                        new Rect(0, 0, 0.5, 0.5), new Rect(0, 0, 10, 10), leaf, child, parent, bounds,
                    }, bounds, 1),
                "最小边长过滤必须在扩展比较之前做");

            var deeper = new Rect(0, 0, 5, 5);
            Check.True(model.SelectIndex(2) &&
                       model.UpdateMode(SmartSelectionMode.Window) &&
                       model.UpdateMode(SmartSelectionMode.WindowSubElement) &&
                       model.ApplyRefinementPath(
                           new[] { deeper, new Rect(0, 0, 10, 10), leaf, child, parent, bounds }, bounds, 1) &&
                       model.CurrentSelection == deeper,
                "换过模式之后要恢复自动往深处细化");
        }

        // 2) 上游 intelligentSelectionTargetsPreserveElementPathBehavior
        {
            var selection = new SmartSelectionModel();
            var nestedElement = new Rect(30, 30, 20, 10);
            var childElement = new Rect(20, 20, 60, 40);
            var window = new Rect(10, 10, 100, 80);
            var bounds = new Rect(0, 0, 200, 160);

            selection.BeginSession(SmartSelectionMode.WindowSubElement);
            Check.True(selection.SmartSelectionEnabled &&
                       selection.Target == SmartSelectionTarget.WindowSubElement &&
                       selection.ApplyHitPath(new[] { nestedElement, childElement, window }, bounds, 1) &&
                       selection.Index == 0 && selection.CurrentSelection == nestedElement,
                "子元素模式先选最深的那个");

            Check.True(selection.SetIndex(1) && selection.CurrentSelection == childElement &&
                       selection.ApplyHitPath(new[] { nestedElement, childElement, window }, bounds, 1) &&
                       selection.Index == 1,
                "命中路径没变时保留选好的层级");

            var nextElement = new Rect(130, 30, 20, 10);
            var nextWindow = new Rect(120, 10, 70, 80);
            Check.True(selection.ApplyHitPath(new[] { nextElement, nextWindow }, bounds, 1) &&
                       selection.Index == 0 && selection.CurrentSelection == nextElement,
                "换了命中路径就回到最深的那个");

            Check.True(selection.SetIndex(99) && selection.Index == 1 &&
                       selection.CurrentSelection == nextWindow,
                "层级下标夹在路径范围内");

            Check.True(selection.ApplyHitPath(new[] { nextWindow }, bounds, 1) && selection.Index == 0 &&
                       selection.CurrentSelection == nextWindow,
                "路径只剩一项时照样落在它上面");

            Check.True(selection.ToggleSelectionTarget() &&
                       selection.Target == SmartSelectionTarget.Window &&
                       selection.CurrentSelection == nextWindow && selection.SetIndex(0) &&
                       selection.Index == 0,
                "整窗模式把层级锁在最外层");

            selection.BeginSession(SmartSelectionMode.WindowSubElement);
            Check.Equal(SmartSelectionTarget.WindowSubElement, selection.Target,
                "默认档位是子元素");

            selection.BeginSession(SmartSelectionMode.Disabled);
            Check.True(!selection.SmartSelectionEnabled &&
                       selection.Target == SmartSelectionTarget.Window &&
                       !selection.ToggleSelectionTarget() &&
                       !selection.ApplyHitPath(new[] { nestedElement, childElement, window }, bounds, 1) &&
                       !selection.HasCurrentSelection,
                "关掉智能选区就不收命中路径");

            selection.BeginSession(SmartSelectionMode.Window);
            Check.True(selection.ApplyHitPath(new[] { nestedElement, childElement, window }, bounds, 1) &&
                       selection.CurrentSelection == window,
                "整窗模式选最外层窗口");

            Check.True(selection.UpdateMode(SmartSelectionMode.WindowSubElement) &&
                       selection.Target == SmartSelectionTarget.WindowSubElement &&
                       selection.Index == 0 && selection.CurrentSelection == nestedElement &&
                       selection.UpdateMode(SmartSelectionMode.Window) &&
                       selection.Target == SmartSelectionTarget.Window &&
                       selection.Index == 2 && selection.CurrentSelection == window,
                "设置页改档位要立刻换目标策略");
        }

        // 3) 按下 / 拖动阈值 / 取走几何
        {
            var model = new SmartSelectionModel();
            var selection = new Rect(10, 10, 100, 100);
            model.BeginSession(SmartSelectionMode.WindowSubElement);

            Check.False(model.ShouldStartManualDrag(new Point(40, 40), 3), "没按下时不算拖框");

            model.BeginPress(new Point(20, 20), selection);
            Check.True(model.PressActive, "按下要记住");
            Check.Equal(new Point(20, 20), model.PressPosition, "按下点原样记住");
            Check.False(model.ShouldStartManualDrag(new Point(22, 21), 3), "位移小于阈值仍算单击");
            Check.True(model.ShouldStartManualDrag(new Point(24, 20), 3), "超过阈值就是拖框");
            Check.True(model.TakePressSelection() == selection, "取走按下时的几何");
            Check.False(model.PressActive, "取走之后按下状态结束");
        }

        // 4) 清空临时状态 / 整份重置
        {
            var model = new SmartSelectionModel();
            var bounds = new Rect(0, 0, 400, 400);
            model.BeginSession(SmartSelectionMode.WindowSubElement);
            model.ApplyHitPath(new[] { new Rect(0, 0, 40, 40), bounds }, bounds, 1);
            model.BeginPress(new Point(5, 5), model.CurrentSelection);

            model.ClearTransientState();
            Check.False(model.HasCurrentSelection, "清临时状态要把命中路径一起清掉");
            Check.False(model.PressActive, "清临时状态也要清按下");

            model.ApplyHitPath(new[] { new Rect(0, 0, 40, 40), bounds }, bounds, 1);
            model.Reset();
            Check.Equal(SmartSelectionMode.Disabled, model.Mode, "整份重置回到关闭");
            Check.False(model.HasCurrentSelection, "整份重置没有选区");
        }
    }
}
