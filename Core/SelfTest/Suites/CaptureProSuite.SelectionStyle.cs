using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// P3 选区：外观（圆角 / 阴影 / 边框）、比例锁定的几何、阴影强度曲线，
/// 以及「导出尺寸 = 内容 + 两侧效果宽」的合成结果（逐像素）。
/// </summary>
/// <remarks>
/// 全部是纯数据 / 纯几何，不开窗口；覆盖层上的信息条与参数框走仓库外冒烟。
/// 期望值一律来自上游：<c>kScreenshotSelectionCornerRadiusMax = 256</c>、
/// <c>kScreenshotSelectionShadowWidthMax = 64</c>、<c>screenshotshadowprofile.h</c> 的
/// 36% 峰值 + 四次方衰减。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void SelectionStyleRules()
    {
        // 上限 / 负数：夹到 0..256 与 0..64
        var clamped = new SelectionStyle
        {
            CornerRadius = 999,
            ShadowWidth = -5,
            BorderWidth = 999,
        }.Clamp();
        Check.Equal(SelectionStyle.CornerRadiusMax, clamped.CornerRadius, "圆角上限 256");
        Check.Equal(0, clamped.ShadowWidth, "负数按 0");
        Check.Equal(SelectionStyle.EffectWidthMax, clamped.BorderWidth, "边框宽上限 64");

        // 效果宽度 / 颜色按类型取，切类型不会抹掉另一种
        var shadow = new SelectionStyle { ShadowWidth = 12, BorderWidth = 4, BorderType = SelectionBorderType.Shadow };
        Check.Equal(12, shadow.EffectWidth, "阴影类型取阴影宽");
        Check.Equal(DefaultShadow(), shadow.EffectColor, "阴影类型取阴影色");
        Check.Equal(4, shadow.WithBorderType(SelectionBorderType.Border).EffectWidth, "切到边框取边框宽");
        Check.Equal(20, shadow.WithEffectWidth(20).ShadowWidth, "改宽度只动当前类型");
        Check.Equal(4, shadow.WithEffectWidth(20).BorderWidth, "另一种宽度原样保留");
        Check.Equal(Colors.Red, shadow.WithEffectColor(Colors.Red).ShadowColor, "改颜色只动当前类型");
        Check.Equal(SelectionStyle.DefaultBorderColor, shadow.BorderColor, "边框色没被改");
        Check.Equal(SelectionStyle.EffectWidthMax,
            new SelectionStyle { BorderType = SelectionBorderType.Border, BorderWidth = 999 }
                .WithEffectWidth(1000).BorderWidth,
            "改宽度也夹到上限");

        // 直角 + 无效果 = 走快路径（导出尺寸不变）
        Check.True(SelectionStyle.None.IsPlain, "默认外观是「平」的");
        Check.False((SelectionStyle.None with { CornerRadius = 8 }).IsPlain, "有圆角就不是平的");
        Check.False((SelectionStyle.None with { ShadowWidth = 1 }).IsPlain, "有阴影就不是平的");

        // 圆角在选区里的实际半径 = 不超过短边一半
        var box = new Rect(0, 0, 100, 20);
        Check.Equal(10d, (SelectionStyle.None with { CornerRadius = 50 }).EffectiveRadius(box),
            "圆角夹到短边一半");
        Check.Equal(8d, (SelectionStyle.None with { CornerRadius = 8 }).EffectiveRadius(box), "小圆角原样");

        // DIP → 物理像素按显示器缩放（125% 屏 1.25）
        Check.Equal(10, (SelectionStyle.None with { CornerRadius = 8 }).RadiusPixels(1.25), "8 DIP × 1.25 = 10 像素");
        Check.Equal(5, (SelectionStyle.None with { ShadowWidth = 4 }).EffectPixels(1.25), "4 DIP × 1.25 = 5 像素");
        Check.Equal(4, (SelectionStyle.None with { ShadowWidth = 4 }).EffectPixels(0), "缩放 0 按 1 兜底（不炸）");

        // 比例表：Id 往返 + 高 / 宽
        foreach (var aspect in SelectionAspects.All)
        {
            Check.Equal(aspect, SelectionAspects.FromId(SelectionAspects.Id(aspect)),
                "比例 Id 往返：" + SelectionAspects.Label(aspect));
        }
        Check.Equal("free", SelectionAspects.Id(SelectionAspect.Free), "自由写 free");
        Check.Equal(SelectionAspect.Free, SelectionAspects.FromId("16:10"), "认不出的比例按自由");
        Check.Equal(9.0 / 16.0, SelectionAspects.HeightOverWidth(SelectionAspect.Landscape16x9), "16:9 的高 / 宽");
        Check.Equal(16.0 / 9.0, SelectionAspects.HeightOverWidth(SelectionAspect.Portrait9x16), "9:16 的高 / 宽");
        Check.Equal(16.0 / 9.0, SelectionAspects.WidthOverHeight(SelectionAspect.Landscape16x9), "16:9 的宽 / 高");
        Check.Equal(0d, SelectionAspects.HeightOverWidth(SelectionAspect.Free), "自由没有比例");

        // 最接近的预设（上游 screenshotSelectionClosestAspectRatioPreset）
        Check.Equal(SelectionAspect.Square, SelectionAspects.Closest(new Rect(0, 0, 100, 100)), "正方形 → 1:1");
        Check.Equal(SelectionAspect.Landscape16x9, SelectionAspects.Closest(new Rect(0, 0, 1920, 1080)),
            "1920×1080 → 16:9");
        Check.Equal(SelectionAspect.Portrait9x16, SelectionAspects.Closest(new Rect(0, 0, 1080, 1920)),
            "1080×1920 → 9:16");
        Check.Equal(SelectionAspect.Free, SelectionAspects.Closest(Rect.Empty), "空选区 = 自由");

        // 阴影强度：贴着选区最浓（36%），往外单调衰减到 0
        Check.True(Math.Abs(ShadowProfile.Falloff(0, 10) - 1.0) < 1e-9, "紧贴选区 = 满强度");
        Check.Equal(0d, ShadowProfile.Falloff(10, 10), "到边缘衰减到 0");
        Check.Equal(0d, ShadowProfile.Falloff(20, 10), "超出宽度仍是 0");
        Check.True(ShadowProfile.Falloff(2, 10) > ShadowProfile.Falloff(6, 10), "越往外越淡");
        Check.True(Math.Abs(ShadowProfile.Falloff(5, 10) - Math.Pow(0.5, 4)) < 1e-9, "四次方衰减");
        Check.True(ShadowProfile.RingAlpha(1, 8) > ShadowProfile.RingAlpha(8, 8), "内圈比外圈浓");
        Check.True(ShadowProfile.RingAlpha(1, 8) < ShadowProfile.PeakAlphaScale, "第一圈不超过峰值");
        Check.Equal(0d, ShadowProfile.RingAlpha(0, 8), "第 0 圈不存在（clip 到 0）");
    }

    private static Color DefaultShadow() => Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>比例锁定：拖八个把手、框选、把已有选区套成比例三条纯几何。</summary>
    private static void SelectionAspectGeometry()
    {
        // 拖右边：高度按比例推出来，左边不动
        // （上游对「另一轴没动」的把手按**中心**推另一轴，所以测试框放在画布中间才装得下）
        var origin = new Rect(400, 475, 100, 50);
        var room = new Rect(0, 0, 1000, 1000);
        var wide = AspectLock.Drag(origin, FrameHandle.Right, new Vector(100, 0), room, 1, 1.0);
        Check.Equal(200d, wide.Width, "拖右边宽度翻倍");
        Check.Equal(200d, wide.Height, "高度跟着按 1:1 走");
        Check.Equal(400d, wide.Left, "左边是锚点，不动");

        // 拖下边：宽度按比例推出来（高度到 100，宽也就 100）
        var tall = AspectLock.Drag(origin, FrameHandle.Bottom, new Vector(0, 50), room, 1, 1.0);
        Check.Equal(100d, tall.Height, "拖下边高度加到 100");
        Check.True(Math.Abs(tall.Width - 100) < 1e-9, "宽度跟着按 1:1 走");

        // 角把手：往反向拖（翻面）时锚点是对角，比例不变、框不会塌
        var flipped = AspectLock.Drag(origin, FrameHandle.BottomRight, new Vector(-200, -200), room, 1, 1.0);
        Check.True(flipped.Width >= 1 && flipped.Height >= 1, "翻面不留负尺寸");
        Check.True(Math.Abs(flipped.Width - flipped.Height) < 1e-9, "翻面仍保持 1:1");

        // 16:9（高 / 宽 = 9/16）：拖右边，高度永远是宽的 0.5625
        var ratio = SelectionAspects.HeightOverWidth(SelectionAspect.Landscape16x9);
        var video = AspectLock.Drag(origin, FrameHandle.Right, new Vector(60, 0), room, 1, ratio);
        Check.True(Math.Abs(video.Height - video.Width * ratio) < 1e-6, "16:9 保持比例");
        Check.Equal(400d, video.Left, "16:9 左边仍是锚点");

        // 放不下时二分收缩，绝不越界
        var small = new Rect(0, 0, 50, 50);
        var clamped = AspectLock.Drag(small, FrameHandle.Right, new Vector(5000, 0), small, 1, 1.0);
        Check.True(clamped.Right <= 50 + 1e-6, "超出右边界被收回来");
        Check.True(clamped.Bottom <= 50 + 1e-6, "按比例连高度一起收回来");

        // 没有比例 / 移动把手：原样退回
        Check.Equal(origin, AspectLock.Drag(origin, FrameHandle.Right, new Vector(10, 0), room, 1, 0),
            "比例 0 = 不锁，原样退回");
        Check.Equal(origin, AspectLock.Drag(origin, FrameHandle.Move, new Vector(10, 0), room, 1, 1),
            "移动不归比例锁管");

        // 框选：以按下点为锚，短边撑到符合比例（左上往右下拖）
        var marquee = AspectLock.Marquee(new Point(0, 0), new Point(100, 20), room, 1.0);
        Check.Equal(new Rect(0, 0, 100, 100), marquee, "按 1:1 把高度撑到 100");
        // 反向拖（右下往左上）：锚点在右下
        var back = AspectLock.Marquee(new Point(100, 100), new Point(0, 40), room, 1.0);
        Check.Equal(new Rect(0, 0, 100, 100), back, "反向拖也是 100×100");
        // 贴边时按可用空间收缩
        var edgy = AspectLock.Marquee(new Point(90, 90), new Point(100, 95), new Rect(0, 0, 100, 100), 1.0);
        Check.True(edgy.Right <= 100 + 1e-6 && edgy.Bottom <= 100 + 1e-6, "不越界");
        Check.True(Math.Abs(edgy.Width - edgy.Height) < 1e-9, "贴边也保持 1:1");

        // 把已有选区套成比例：左上不动、宽度不动、高度按比例
        var applied = AspectLock.Apply(new Rect(10, 20, 200, 40), room, 1.0, 1);
        Check.Equal(new Rect(10, 20, 200, 200), applied, "按 1:1 重排高度");
        // 放不下时先缩宽度
        var tight = AspectLock.Apply(new Rect(10, 10, 200, 40), new Rect(0, 0, 100, 100), 1.0, 1);
        Check.Equal(100d, tight.Width, "区域装不下就缩到可用宽");
        Check.Equal(100d, tight.Height, "高度跟着缩");
        // 自由 / 非法输入给空矩形（调用方按「做不到」处理）
        Check.True(AspectLock.Apply(Rect.Empty, room, 1.0, 1).IsEmpty, "没有选区做不到");
        Check.True(AspectLock.Apply(new Rect(0, 0, 10, 10), room, 0, 1).IsEmpty, "没有比例做不到");

        // 把手方向表（与 FrameHandle 一一对应）
        Check.Equal((-1, -1), AspectLock.Directions(FrameHandle.TopLeft), "左上");
        Check.Equal((1, 1), AspectLock.Directions(FrameHandle.BottomRight), "右下");
        Check.Equal((0, -1), AspectLock.Directions(FrameHandle.Top), "上边");
        Check.Equal((1, 0), AspectLock.Directions(FrameHandle.Right), "右边");
        Check.Equal((0, 0), AspectLock.Directions(FrameHandle.None), "没命中");
    }

    /// <summary>圆角矩形合成：外接框不变、角上不再算「在选区里」，导出角上透明。</summary>
    private static void SelectionCornerGeometry()
    {
        var add = SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 100, 100));
        var sharp = SelectionSet.Combine(new[] { add })!;
        var rounded = SelectionSet.Combine(new[] { add }, 20)!;

        Check.Equal(sharp.Bounds, rounded.Bounds, "圆角不改外接框");
        Check.True(sharp.FillContains(new Point(2, 2)), "直角：角上算在选区里");
        Check.False(rounded.FillContains(new Point(2, 2)), "圆角：角上不算在选区里");
        Check.True(rounded.FillContains(new Point(50, 50)), "圆角：中间仍在选区里");
        Check.False(SelectionSet.Contains(new[] { add }, new Point(2, 2), 20), "命中测试也认圆角");
        Check.True(SelectionSet.Contains(new[] { add }, new Point(50, 50), 20), "命中测试：内部仍是内部");

        // 圆角半径超过短边一半时按一半（不塌成 0）
        var thin = SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 100, 10));
        var pill = SelectionSet.Combine(new[] { thin }, 999)!;
        Check.True(pill.FillContains(new Point(50, 5)), "胶囊：中间仍有内容");
        Check.False(pill.FillContains(new Point(1, 1)), "胶囊：左上角被切掉");

        // 点列块不受圆角影响（上游对自定义路径 cornerRadius = 0）
        var poly = SelectionRegion.FromPoints(RegionMode.Add,
            new[] { new Point(0, 0), new Point(40, 0), new Point(0, 40) });
        var polyGeo = SelectionSet.Combine(new[] { poly }, 20)!;
        Check.True(polyGeo.FillContains(new Point(2, 2)), "点列块照旧吃到尖角");
    }

    /// <summary>导出合成：输出尺寸 = 内容 + 两侧效果宽（物理像素），内容按圆角裁，效果画在外面。</summary>
    private static void SelectionExportLayout()
    {
        var white = WhiteBitmap(8, 8);

        // 平（直角无效果）：尺寸不变、整块不透明
        var plain = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0);
        plain.Area = new Int32Rect(100, 100, 8, 8);
        var plainShot = plain.Render();
        Check.Equal(8, plainShot.PixelWidth, "无效果不改宽度");
        Check.Equal(8, plainShot.PixelHeight, "无效果不改高度");
        Check.Equal((byte)0xFF, AlphaAt(plainShot, 0, 0), "直角：左上角不透明");

        // 圆角：尺寸不变，角上透明
        var rounded = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0)
        {
            Area = new Int32Rect(100, 100, 8, 8),
            Style = SelectionStyle.None with { CornerRadius = 3 },
        };
        var roundShot = rounded.Render();
        Check.Equal(8, roundShot.PixelWidth, "只有圆角不改尺寸");
        Check.True(AlphaAt(roundShot, 0, 0) < 32, "圆角：左上角透明（抗锯齿级别）");
        Check.Equal((byte)0xFF, AlphaAt(roundShot, 4, 4), "圆角：中间不透明");

        // 边框：输出每边多 2 像素
        var border = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0)
        {
            Area = new Int32Rect(100, 100, 8, 8),
            Style = SelectionStyle.None with
            {
                BorderType = SelectionBorderType.Border,
                BorderWidth = 2,
                BorderColor = Colors.Red,
            },
        };
        var borderShot = border.Render();
        Check.Equal(12, borderShot.PixelWidth, "边框：输出宽 = 内容 + 2 × 2");
        Check.Equal(12, borderShot.PixelHeight, "边框：输出高 = 内容 + 2 × 2");
        Check.Equal((byte)0xFF, AlphaAt(borderShot, 6, 6), "边框：内容仍不透明");
        var band = Pixel(borderShot, 1, 6);                 // 内容左外侧那一圈 = 边框
        Check.Equal((byte)0xFF, band.A, "边框是实色（不透明）");
        Check.Equal(Colors.Red.R, band.R, "边框用选的颜色");
        Check.Equal((byte)0xFF, AlphaAt(borderShot, 5, 0), "边框：边中点是实色");
        Check.True(AlphaAt(borderShot, 0, 0) < 200, "边框：最外角没铺满（圆角连接，不是尖角）");

        // 阴影：外圈是半透明，内圈更淡；内容照旧
        var shadow = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0)
        {
            Area = new Int32Rect(100, 100, 8, 8),
            Style = SelectionStyle.None with { ShadowWidth = 4, ShadowColor = Colors.Black },
        };
        var shadowShot = shadow.Render();
        Check.Equal(16, shadowShot.PixelWidth, "阴影：输出宽 = 内容 + 2 × 4");
        Check.Equal((byte)0xFF, AlphaAt(shadowShot, 8, 8), "阴影：内容不透明");
        byte inner = AlphaAt(shadowShot, 3, 8);      // 距内容边 1 像素
        byte outer = AlphaAt(shadowShot, 1, 8);      // 距内容边 3 像素
        Check.True(inner > outer, "阴影：贴着选区比外圈浓");
        Check.True(inner < 0xFF, "阴影：内圈也不是实色");
        Check.True(outer > 0, "阴影：外圈还有一点点");

        // 2 倍屏：8 像素内容 + 2 DIP 效果 → 输出 8 + 2 × round(2 × 2) = 16
        var hidpi = new CaptureDocument(WhiteBitmap(16, 16), new Int32Rect(0, 0, 16, 16), 2.0)
        {
            Area = new Int32Rect(0, 0, 16, 16),
            Style = SelectionStyle.None with { ShadowWidth = 2 },
        };
        Check.Equal(24, hidpi.Render().PixelWidth, "2 倍屏：2 DIP 效果 = 4 像素/边");

        // 布局纯函数与导出同一口径
        var (content, output, effect) = SelectionEffects.Layout(new Size(8, 8),
            SelectionStyle.None with { ShadowWidth = 2 }, 1.5);
        Check.Equal(3d, effect, "2 DIP × 1.5 = 3 像素");
        Check.Equal(new Rect(3, 3, 8, 8), content, "内容偏移一个效果宽");
        Check.Equal(new Rect(0, 0, 14, 14), output, "输出 = 内容 + 两侧效果宽");
    }

    private static BitmapSource WhiteBitmap(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF; pixels[i + 1] = 0xFF; pixels[i + 2] = 0xFF; pixels[i + 3] = 0xFF;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte AlphaAt(BitmapSource image, int x, int y) => Pixel(image, x, y).A;

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var pixels = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }
}
