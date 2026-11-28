using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CaptureOverlaySmoke;

/// <summary>
/// 托盘菜单（tray）：条目 / 顺序 / 层级，以及「截图」二级菜单的悬停展开。
/// </summary>
/// <remarks>
/// 菜单结构由 <c>LTools.Core.TrayMenu</c> 造（<c>App</c> 只负责把它挂到托盘图标上），所以这里能
/// 直接构造出来断言，不必真把托盘图标建起来；悬停那条用真鼠标走一遍（MenuFx 的展开 / 离开收起）。
/// </remarks>
internal static partial class Program
{
    private const BindingFlags TrayAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags TrayAnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static void TraySection()
    {
        var asm = typeof(LTools.Views.CapturePro.CaptureOverlayWindow).Assembly;
        var type = asm.GetType("LTools.Core.TrayMenu")!;
        int exitCalls = 0, triggerCalls = 0;

        var tray = Activator.CreateInstance(type, TrayAny, null,
            new object[] { (Action)(() => exitCalls++), (Action)(() => triggerCalls++) }, null)!;
        var menu = (ContextMenu)type.GetProperty("Menu", TrayAny)!.GetValue(tray)!;

        // ---- 顶层：截图相关的 5 项不再平铺，收进「截图」二级菜单 ----
        // 「运行中的动作…（N）」只在真有动作运行时才插进来（TrayMenu 读注册表），
        // 而完整冒烟跑到这一段时前面的用例可能还留着运行 —— 它不参与「固定顺序」的断言，
        // 去掉它并把连着的两条竖线并成一条，这条断言才与运行状态无关。
        var top = Texts(menu.Items);
        var stable = CollapseSeparators(top.Where(t => !t.StartsWith("运行中的动作", StringComparison.Ordinal)));
        var expectedTop = new[]
        {
            "速记 (CapsLock+N)", "动作面板 (CapsLock+T)", "设置 (CapsLock+\\)", "辅助工具…", "局域网共享…",
            "截图", "—",
            "开始 / 停止录制宏", "键鼠宏…", "—",
            "启用 CapsLock 修饰键", "启用超级面板", "暂停自动触发（紧急禁用）", "—",
            "关闭所有悬浮项", "—", "退出 LTools",
        };
        Check(stable.SequenceEqual(expectedTop), "托盘顶层条目 / 顺序：" + string.Join(" | ", stable));
        Check(!top.Contains("采集历史…") && !top.Contains("贴图管理…") && !top.Contains("图片编辑…")
              && !top.Contains("切图…") && !top.Contains("图像拼接…"),
            "截图相关的管理窗口不再平铺在顶层");

        var captureItem = menu.Items.OfType<MenuItem>().Single(i => Text(i.Header) == "截图");
        var sub = Texts(captureItem.Items);
        var expectedSub = new[]
        {
            "截图…", "延时截图…", "—",
            "采集历史…", "贴图管理…", "—",
            "图片编辑…", "切图…", "图像拼接…",
        };
        Check(sub.SequenceEqual(expectedSub), "「截图」二级菜单条目 / 顺序：" + string.Join(" | ", sub));
        Check(captureItem.Items.OfType<MenuItem>().All(i => i.Items.Count == 0), "子菜单里的都是叶子项");

        // ---- 真鼠标：打开菜单 → 悬停父项 → 子菜单展开 → 移开（宽限）→ 收起 ----
        var menuFx = asm.GetType("LTools.Views.Controls.MenuFx")!;
        var openField = menuFx.GetField("HoverOpenMs", TrayAnyStatic)!;
        var closeField = menuFx.GetField("HoverCloseMs", TrayAnyStatic)!;
        int openDefault = (int)openField.GetValue(null)!;
        int closeDefault = (int)closeField.GetValue(null)!;
        Check(openDefault == 0, $"悬停展开没有延迟（{openDefault}ms）");
        Check(closeDefault > 0, $"移出后有收起宽限（{closeDefault}ms）");

        var work = SystemParameters.WorkArea;

        // 「移开」要落到**屏幕内**的角落：以前写死 (10, 1060)，本机工作区只有 816 高，
        // 指针被夹到任务栏上、菜单的处理路径就变了（这条用例因此在矮屏上一直红）。
        void MoveAway() => SetCursorPos((int)work.Left + 5, (int)(work.Top + work.Height) - 5);
        menu.Placement = PlacementMode.AbsolutePoint;
        menu.HorizontalOffset = work.Left + 60;
        menu.VerticalOffset = work.Top + 60;
        menu.IsOpen = true;
        PumpFor(200);
        Check(menu.IsOpen, "托盘菜单真的打开了");

        var container = (MenuItem?)menu.ItemContainerGenerator.ContainerFromItem(captureItem);
        Check(container != null && container.IsVisible, "「截图」父项拿到了可视容器");
        if (container != null)
        {
            var center = container.PointToScreen(
                new Point(container.ActualWidth / 2, container.ActualHeight / 2));
            int cx = (int)Math.Round(center.X), cy = (int)Math.Round(center.Y);
            Check(HoverUntil(cx, cy, () => captureItem.IsSubmenuOpen, 1500), "鼠标悬停「截图」→ 子菜单展开");

            var popup = (Popup?)container.Template?.FindName("PART_Popup", container);
            Check(popup != null && popup.IsOpen, "子菜单的 popup 真的打开了");

            var child = popup?.Child as UIElement;
            Check(child != null && child.IsVisible, "子菜单内容可见");

            // 指针进子菜单里的「截图…」：能拿到真实高亮，说明它不是一个空壳弹层
            var first = captureItem.Items.OfType<MenuItem>().First();
            var firstContainer = (MenuItem?)captureItem.ItemContainerGenerator.ContainerFromItem(first);
            if (firstContainer != null)
            {
                var p = firstContainer.PointToScreen(
                    new Point(firstContainer.ActualWidth / 2, firstContainer.ActualHeight / 2));
                Check(HoverUntil((int)Math.Round(p.X), (int)Math.Round(p.Y),
                    () => first.IsHighlighted, 2000), "指针进子菜单 → 「截图…」高亮");
            }

            // 移开整个菜单：**MenuCone 立刻把子菜单交还**（它盖过 MenuFx 的收起宽限——
            // 2026-11-26 的「预测锥」就是「斜穿不换项、明确移开才收」，`tests/MenuConeSmoke`
            // 的「横向离开菜单后子菜单收起」是同一件事）。以前这里断言「移开 220ms 仍展开」，
            // 是锥之前的口径，锥上线后它必红；宽限本身仍由上面的 `HoverCloseMs > 0` 守着。
            MoveAway();
            Check(WaitUntil(() => !captureItem.IsSubmenuOpen, 1500),
                "移开整个菜单 → 子菜单收起（锥交还，不等宽限）");

            // 再悬停回来：子菜单重新展开（指针在父项上时锥待命，不会再被交还）
            Check(HoverUntil(cx, cy, () => captureItem.IsSubmenuOpen, 1500), "再悬停 → 子菜单回来");
            MoveAway();
            WaitUntil(() => !captureItem.IsSubmenuOpen, 2000);
        }

        menu.IsOpen = false;
        PumpFor(120);
        SetCursorPos(960, 540);
        Check(exitCalls == 0 && triggerCalls == 0, "只悬停不点击，没有任何回调被执行");
    }

    /// <summary>
    /// 把光标停在 <paramref name="x"/>, <paramref name="y"/> 上等 <paramref name="ok"/> 成立，
    /// 期间每 80ms 抖 1–2 像素。
    /// </summary>
    /// <remarks>
    /// 真人挪鼠标是连续的，这里一次 <c>SetCursorPos</c> 是瞬移：弹层刚打开那一拍若还没可命中，
    /// 那一次 <c>MouseEnter</c> 就落空了，而静止的光标不会再产生鼠标消息（实测偶发红）。
    /// 抖动只为了重新触发一次移动，位置始终在这个菜单项的矩形里。
    /// </remarks>
    private static bool HoverUntil(int x, int y, Func<bool> ok, int timeoutMs)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int step = 0;
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (ok()) return true;
            step++;
            SetCursorPos(x + (step % 2 == 0 ? 1 : -1), y + (step % 3 == 0 ? 1 : -1));
            PumpFor(80);
        }

        return ok();
    }

    /// <summary>菜单项标题的文字（托盘项是「状态点 + 文字」的 StackPanel，分隔符记作 —）。</summary>
    private static string Text(object? header) => header switch
    {
        string text => text,
        StackPanel row => string.Concat(row.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => "",
    };

    private static string[] Texts(ItemCollection items) => items.Cast<object>()
        .Select(item => item is Separator ? "—" : Text(((MenuItem)item).Header))
        .ToArray();

    /// <summary>去掉某些动态条目后，把连着的两条竖线并成一条（否则顺序断言会多一条分隔）。</summary>
    private static List<string> CollapseSeparators(IEnumerable<string> items)
    {
        var result = new List<string>();
        foreach (string item in items)
            if (item != "—" || result.Count == 0 || result[^1] != "—")
                result.Add(item);
        return result;
    }
}
