using System.Collections.Generic;
using System.Windows;
using LTools.Views.Controls;

namespace LTools.Core.SelfTest;

/// <summary>
/// 右键菜单「预测锥」的纯几何（<see cref="MenuCone"/>）：三角包含 + 子菜单相邻那条边的锥底。
/// 不碰窗口、不动鼠标——真机行为（斜穿不换项 / 停住让位）靠仓库外的 <c>menu-cone-smoke</c>。
/// </summary>
internal static class MenuConeSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("menuCone", "三角包含：内部 / 边上 / 外侧 / 退化", Triangle));
        cases.Add(new("menuCone", "锥底取子菜单与父项相邻的那条边（左右上下）", NearEdge));
        cases.Add(new("menuCone", "锥底外扩常量与停住宽限", Constants));
    }

    private static void Triangle()
    {
        var apex = new Point(100, 100);
        var a = new Point(300, 50);
        var b = new Point(300, 250);

        // 正中间一条（斜着往右下走）必须算在锥里
        Check.True(MenuCone.InTriangle(apex, a, b, new Point(200, 150)), "中点在内");
        Check.True(MenuCone.InTriangle(apex, a, b, new Point(100, 100)), "锥顶算在内");
        Check.True(MenuCone.InTriangle(apex, a, b, new Point(300, 150)), "锥底中点算在内");
        Check.True(MenuCone.InTriangle(apex, a, b, new Point(250, 100)), "靠近上半边");
        Check.True(MenuCone.InTriangle(apex, a, b, new Point(250, 200)), "靠近下半边");

        // 三角形外面
        Check.False(MenuCone.InTriangle(apex, a, b, new Point(200, 40)), "上外侧不在");
        Check.False(MenuCone.InTriangle(apex, a, b, new Point(200, 260)), "下外侧不在");
        Check.False(MenuCone.InTriangle(apex, a, b, new Point(400, 150)), "越过锥底不算");
        Check.False(MenuCone.InTriangle(apex, a, b, new Point(60, 100)), "锥顶后面不算");

        // 绕法反过来（顺时针）也一样
        Check.True(MenuCone.InTriangle(apex, b, a, new Point(200, 150)), "反向绕法同样命中");

        // 退化三角形（三点重合 / 共线）不算
        Check.False(MenuCone.InTriangle(apex, apex, apex, new Point(200, 150)), "退化：三点重合");
        Check.False(MenuCone.InTriangle(apex, new Point(300, 100), new Point(200, 100), new Point(250, 150)),
            "退化：三点共线");
    }

    private static void NearEdge()
    {
        var owner = new Rect(100, 100, 200, 200);          // 父项
        var right = new Rect(400, 80, 200, 300);           // 子菜单在右边
        var (a, b) = MenuCone.NearEdge(right, owner);
        Check.Equal(right.Left, a.X, "右边子菜单：锥底在左边那条竖边");
        Check.Equal(right.Left, b.X, "锥底两端同一 x");
        Check.Equal(right.Top - MenuCone.Spread, a.Y, "上端外扩");
        Check.Equal(right.Bottom + MenuCone.Spread, b.Y, "下端外扩");

        var left = new Rect(-200, 120, 200, 300);          // 子菜单在左边
        var (l1, l2) = MenuCone.NearEdge(left, owner);
        Check.Equal(left.Right, l1.X, "左边子菜单：锥底取右边那条竖边");
        Check.Equal(l1.X, l2.X, "锥底两端同一 x");

        var below = new Rect(80, 400, 300, 200);           // 子菜单在下方
        var (t1, t2) = MenuCone.NearEdge(below, owner);
        Check.Equal(below.Top, t1.Y, "下方子菜单：锥底取上边");
        Check.Equal(t1.Y, t2.Y, "锥底两端同一 y");
        Check.True(t1.X < below.Left && t2.X > below.Right, "上边两端各外扩");

        var above = new Rect(80, -200, 300, 200);          // 子菜单在上方
        var (b1, b2) = MenuCone.NearEdge(above, owner);
        Check.Equal(above.Bottom, b1.Y, "上方子菜单：锥底取下边");
        Check.Equal(b1.Y, b2.Y, "锥底两端同一 y");

        // 空矩形（还没量出尺寸）不炸，锥底退化成一个点
        var (e1, e2) = MenuCone.NearEdge(Rect.Empty, owner);
        Check.Equal(0.0, e1.X, "空矩形退化成一个点");
        Check.Equal(0.0, e2.X, "空矩形退化成一个点");
    }

    private static void Constants()
    {
        Check.True(MenuCone.Spread > 0, "外扩量必须是正数（手抖出边一点点仍算在通道里）");
        Check.True(MenuCone.DwellMs >= 300, "停住让位的宽限别太短（默认 450ms，覆盖 WPF 的 400ms）");
    }
}
