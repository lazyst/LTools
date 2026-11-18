using System;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// P3 选区信息条与参数框的纯数据部分：信息条摆位（上 → 右 → 左 → 下 → 夹回）、
/// 参数在「屏幕物理像素」与「窗口 DIP」之间的往返、两份效果宽度 / 颜色各自保留。
/// </summary>
/// <remarks>
/// 期望值一律来自上游 <c>screenshotgeometrymapper.cpp</c> 的
/// <c>selectionToolbarContentPosition</c>（<c>kSelectionToolbarGap = 4</c>，
/// 第一个能完整放下的位置，四处都放不下才夹回可见区）与
/// <c>screenshotselectionresizemodalcontent.cpp</c>（阴影 / 边框两份宽与色各存）。
/// 覆盖层上的真实控件（滚轮 / 悬停 / 下拉）走仓库外冒烟。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void SelectionBarRules()
    {
        var bar = new Size(100, 26);
        var screen = new Rect(0, 0, 200, 200);

        // 上方放得下：就放上方（间距 4）
        var middle = new Rect(50, 60, 40, 40);
        Check.Equal(new Point(50, 60 - 26 - 4), SelectionBarPlacement.Place(middle, bar, screen),
            "信息条优先放选区上方（间距 4）");
        Check.Equal(4d, SelectionBarPlacement.Gap, "间距常量与上游一致");

        // 贴着顶边：上方放不下 → 换右侧
        var top = new Rect(10, 0, 40, 40);
        Check.Equal(new Point(10 + 40 + 4, 0), SelectionBarPlacement.Place(top, bar, screen),
            "上方放不下换右侧");

        // 顶边 + 右边都不行 → 左侧
        var topRight = new Rect(150, 0, 40, 40);
        Check.Equal(new Point(150 - 100 - 4, 0), SelectionBarPlacement.Place(topRight, bar, screen),
            "右侧也放不下换左侧");

        // 三处都不行 → 下方
        var wide = new Rect(10, 0, 100, 40);
        Check.Equal(new Point(10, 40 + 4), SelectionBarPlacement.Place(wide, bar, screen),
            "上 / 右 / 左都不行换下方");

        // 四边都放不下：夹回可见区（左上角仍在屏幕内）
        var tiny = new Rect(0, 0, 200, 200);
        var clamped = SelectionBarPlacement.Place(tiny, bar, new Rect(0, 0, 50, 50));
        Check.Equal(new Point(0, 0), clamped, "四边都放不下就夹回可见区");

        // 负坐标的显示器（多屏时 left / top 可为负）也要跟着走
        var screen2 = new Rect(-1920, 0, 1920, 1080);
        var sel2 = new Rect(-1800, 300, 100, 100);
        Check.Equal(new Point(-1800, 300 - 26 - 4), SelectionBarPlacement.Place(sel2, bar, screen2),
            "负坐标显示器照样放上方");

        // 信息条没量出来（尺寸为 0）：退回选区左上角，别算出 NaN
        Check.Equal(new Point(50, 60), SelectionBarPlacement.Place(middle, new Size(0, 0), screen),
            "尺寸为 0 时退回选区左上角");
    }

    private static void SelectionParamsRules()
    {
        var area = new Int32Rect(100, 200, 400, 300);
        var style = SelectionStyle.None with
        {
            CornerRadius = 8,
            ShadowWidth = 4,
            BorderWidth = 3,
            ShadowColor = Colors.Black,
            BorderColor = Colors.Red,
            BorderType = SelectionBorderType.Shadow,
        };

        // 外观（DIP）→ 参数（物理像素）：125% 屏上 8 DIP = 10 px、4 DIP = 5 px、3 DIP = 3.75 → 4 px
        var p = SelectionParams.From(area, lockAspect: true, style, 1.25);
        Check.Equal(new Int32Rect(100, 200, 400, 300), p.Area, "参数里的几何是物理像素，原样带过来");
        Check.True(p.LockAspect, "比例锁跟着带过来");
        Check.Equal(10, p.RadiusPixels, "8 DIP 圆角在 125% 屏上是 10 px");
        Check.Equal(5, p.ShadowWidthPixels, "4 DIP 阴影在 125% 屏上是 5 px");
        Check.Equal(4, p.BorderWidthPixels, "3 DIP 边框在 125% 屏上是 4 px（四舍五入）");
        Check.Equal(SelectionBorderType.Shadow, p.BorderType, "效果类型跟着带过来");
        Check.Equal(5, p.EffectWidthPixels, "当前生效宽度 = 阴影那一份");
        Check.Equal(Colors.Black, p.EffectColor, "当前生效颜色 = 阴影色");

        // 参数（物理像素）→ 外观（DIP）：10 px 在 125% 屏上是 8 DIP（**不取整到 1 DIP**）
        var back = p.ApplyTo(SelectionStyle.None, 1.25);
        Check.Equal(8d, back.CornerRadius, "10 px 圆角回到 8 DIP");
        Check.Equal(4d, back.ShadowWidth, "5 px 阴影回到 4 DIP");
        Check.Equal(3.2d, back.BorderWidth, "4 px 边框回到 3.2 DIP（不取整）");
        Check.Equal(Colors.Black, back.ShadowColor, "阴影色原样回去");
        Check.Equal(Colors.Red, back.BorderColor, "边框色原样回去（即使当前是阴影）");
        Check.Equal(SelectionBorderType.Shadow, back.BorderType, "效果类型原样回去");

        // 切到边框：宽度 / 颜色换成另一份，两份都还在
        var asBorder = p with { BorderType = SelectionBorderType.Border };
        Check.Equal(4, asBorder.EffectWidthPixels, "切到边框：生效宽度 = 边框那一份");
        Check.Equal(Colors.Red, asBorder.EffectColor, "切到边框：生效颜色 = 边框色");
        var borderStyle = asBorder.ApplyTo(back, 1.25);
        Check.Equal(SelectionBorderType.Border, borderStyle.BorderType, "类型写回边框");
        Check.Equal(4d, borderStyle.ShadowWidth, "切类型不动阴影宽");
        Check.Equal(3.2d, borderStyle.BorderWidth, "切类型不动边框宽");

        // 改生效宽度 / 颜色只动当前那一份
        var wider = p.WithEffectWidth(12);
        Check.Equal(12, wider.ShadowWidthPixels, "改生效宽度：阴影跟着变");
        Check.Equal(4, wider.BorderWidthPixels, "改生效宽度：边框那份不动");
        Check.Equal(12, wider.EffectWidthPixels, "生效宽度读回新值");
        var borderWider = asBorder.WithEffectWidth(9);
        Check.Equal(9, borderWider.BorderWidthPixels, "边框类型下改的是边框宽");
        Check.Equal(5, borderWider.ShadowWidthPixels, "边框类型下阴影宽不动");
        var recolored = p.WithEffectColor(Colors.Lime);
        Check.Equal(Colors.Lime, recolored.ShadowColor, "改生效颜色：阴影跟着变");
        Check.Equal(Colors.Red, recolored.BorderColor, "改生效颜色：边框不动");

        // 上限：宽度夹到 64，圆角不动（圆角由 ApplyTo 之后的 Clamp 兜）
        Check.Equal(SelectionStyle.EffectWidthMax, p.WithEffectWidth(999).EffectWidthPixels, "效果宽上限 64");
        Check.Equal(0, p.WithEffectWidth(-20).EffectWidthPixels, "效果宽负数按 0");

        // ClampTo：宽高先夹到屏幕大小，再夹左上角
        var outside = p.WithArea(new Int32Rect(-50, -80, 5000, 4000)).ClampTo(
            new Int32Rect(0, 0, 1920, 1080));
        Check.Equal(new Int32Rect(0, 0, 1920, 1080), outside.Area, "超宽超高 + 负坐标整块夹回屏幕");
        var shrunk = p.WithArea(new Int32Rect(1800, 1000, 400, 300)).ClampTo(
            new Int32Rect(0, 0, 1920, 1080));
        Check.Equal(new Int32Rect(1520, 780, 400, 300), shrunk.Area, "右下出界只挪左上角");
        Check.Equal(1, p.WithArea(new Int32Rect(0, 0, 0, 0)).ClampTo(
            new Int32Rect(0, 0, 1920, 1080)).Width, "宽至少 1");

        // 两次往返不变形（快速集 → 信息条 → 快速集）
        var once = SelectionParams.From(area, true, back, 1.25);
        Check.Equal(p.ShadowWidthPixels, once.ShadowWidthPixels, "往返：阴影宽不变");
        Check.Equal(p.BorderWidthPixels, once.BorderWidthPixels, "往返：边框宽不变");
        Check.Equal(p.RadiusPixels, once.RadiusPixels, "往返：圆角不变");
    }
}
