using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 贴图的显示形态：局部显示（取景框）、缩略图小方块、标注回填，以及它们共用的内容尺寸 / 偏移落地。
/// </summary>
public partial class CapturePinWindow
{
    // ============ 局部显示 ============

    /// <summary>
    /// 开 / 关局部显示。开：窗口缩成取景框（不超过内容，也不超过工作区），内容居中；
    /// 关：窗口回到整张图的尺寸，偏移归零。两种切换都以窗口中心为锚，图不会突然跳走。
    /// </summary>
    public void SetViewport(bool on)
    {
        if (_live) return;                    // 实时贴图不切取景框：窗口尺寸一变就和选区那块脱钩了
        if (_viewport == on) return;
        if (on && _thumb) SetThumbnail(false);        // 两种特殊形态互斥
        _viewport = on;

        var content = ContentSize;

        if (on)
        {
            var view = PinMath.ViewportSize(content, WindowSizing.WorkAreaOf(this));
            ResizeCentered(view);
            _pan = PinMath.CenterPan(content, new Size(Width, Height));
        }
        else
        {
            ResizeCentered(content);
            _pan = default;
        }

        ApplyContent();
        ApplyFrame();
        ApplyCursor();
        RefreshHoverChrome();
    }

    /// <summary>改窗口尺寸但守住中心（进入 / 退出取景框、缩小到工作区里都用它）。</summary>
    private void ResizeCentered(Size size)
    {
        double cx = Left + Width / 2;
        double cy = Top + Height / 2;

        Width = Math.Max(1, size.Width);
        Height = Math.Max(1, size.Height);

        var box = new Rect(cx - Width / 2, cy - Height / 2, Width, Height);
        Vector delta = PinMath.ConstrainGroupMove(new Vector(0, 0), new[] { box },
            PinMath.VirtualDesktop, PinMath.KeepVisible);

        Left = box.Left + delta.X;
        Top = box.Top + delta.Y;
    }

    /// <summary>把内容尺寸与偏移写到界面（缩放 / 平移 / 进出取景框 / 缩略图都只调它）。</summary>
    private void ApplyContent()
    {
        if (_thumb)
        {
            // 缩略图：整张图等比塞进方块（按 100% 铺的话只能看见左上角一块）
            Shot.Width = Math.Max(1, Width);
            Shot.Height = Math.Max(1, Height);
            PanTransform.X = 0;
            PanTransform.Y = 0;
            return;
        }

        var content = ContentSize;
        Shot.Width = content.Width;
        Shot.Height = content.Height;
        PanTransform.X = _pan.X;
        PanTransform.Y = _pan.Y;
    }

    /// <summary>关掉局部显示后窗口还该多大：整张图的尺寸（滚轮 / 菜单换缩放时用）。</summary>
    private void FitWindowToContent()
    {
        if (_viewport || _thumb) return;
        var content = ContentSize;
        Width = content.Width;
        Height = content.Height;
    }

    // ============ 缩略图模式 ============

    /// <summary>
    /// 缩略图模式：缩成一个 <see cref="PinMath.ThumbnailSide"/> 的小方块（图等比放进去），再点一次还原。
    /// 与局部显示互斥（一个是「只露一块」、一个是「整张缩小」）；开关放在右键菜单里，双击也能还原。
    /// </summary>
    public void SetThumbnail(bool on)
    {
        if (_live && on) return;              // 缩成方块就看不见实时的内容了；退出实时之后随便切
        if (_thumb == on) return;

        if (on)
        {
            if (_clickThrough) SetClickThrough(false);  // 穿透态里这块方块也点不到，先退出去（照上游 setThumbnailMode）
            if (_viewport) SetViewport(false);        // 两种特殊形态互斥
            _thumb = true;
            Shot.Stretch = Stretch.Uniform;
            ResizeCentered(new Size(PinMath.ThumbnailSide, PinMath.ThumbnailSide));
            _pan = default;
        }
        else
        {
            _thumb = false;
            Shot.Stretch = Stretch.Fill;
            ResizeCentered(ContentSize);              // 回到整张图（缩放与位置都不变）
        }

        ApplyContent();
        ApplyFrame();
        ApplyCursor();
        RefreshHoverChrome();
        RefreshBadges();                              // 方块里不挂徽标
    }

    // ============ 标注 ============

    /// <summary>
    /// 标注这张贴图：覆盖层开在贴图上面（冻结画面就是它自己），标完的结果换回这张贴图。
    /// 热键 / 鼠标钩子的让路在 <see cref="CaptureSession.TryEdit"/> 里，别在这里另写一份。
    /// </summary>
    private void AnnotateContent()
    {
        if (_live) return;                    // 实时贴图的内容每 200ms 换一次，标了也留不住

        // 覆盖层只能把图按「像素 1:1」铺开，所以标注前先回到整张图的 100%：
        // 缩略图 / 取景框里都只看得见一部分，缩放 200% 时覆盖层与贴图本身对不上。
        if (_thumb) SetThumbnail(false);
        if (_viewport) SetViewport(false);
        if (Math.Abs(_zoom - 1) > 0.001) SetZoom(1);

        var result = CaptureSession.TryEdit(_content, PhysicalArea());
        if (result == null) return;                   // 取消 = 什么都不变

        ReplaceContent(result);
    }

    /// <summary>窗口现在占着的屏幕物理像素矩形（被拖到别的显示器时按当前 DPI 算）。</summary>
    private Int32Rect PhysicalArea()
    {
        double sx = _scale, sy = _scale;
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            var ttd = target.TransformToDevice;
            if (ttd.M11 > 0) sx = ttd.M11;
            if (ttd.M22 > 0) sy = ttd.M22;
        }

        return new Int32Rect(
            (int)Math.Round(Left * sx), (int)Math.Round(Top * sy),
            Math.Max(2, (int)Math.Round(Width * sx)), Math.Max(2, (int)Math.Round(Height * sy)));
    }

    /// <summary>换掉贴的内容（标注之后）：回到 100% 并按新图尺寸贴合窗口。</summary>
    private void ReplaceContent(BitmapSource image)
    {
        _content = image;
        Shot.Source = image;
        _zoom = 1;
        _copies = Array.Empty<PinCardCopy>();     // 标注之后卡片上的复制热区对不上了
        _baseWidth = Math.Max(1, image.PixelWidth / _scale);
        _baseHeight = Math.Max(1, image.PixelHeight / _scale);

        if (!_viewport && !_thumb) ResizeCentered(ContentSize);
        ApplyContent();
        ApplyCursor();
        RefreshHoverChrome();
    }
}
