using System;
using System.Windows;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.ImageEdit;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗的行 / 列裁剪（照上游 <c>ImageEditingCanvas::cropStrip</c> +
/// <c>ImageEditingDocument::prepareCrop</c>）。
/// </summary>
/// <remarks>
/// 交互：工具条勾上「裁剪行 / 裁剪列」→ 鼠标在图上是悬停导引线，按下拖动拉出一条红带并显示
/// 「N px」，松手提交（长度 0 / 等于整条边长时不动）；Esc 退出裁剪模式。
/// 提交后模式留在裁剪上（可以接着剪第二刀），与上游一致。
/// 两个格子由 <see cref="CaptureToolbar"/> 自己记勾选态，宿主只回显 <see cref="CaptureToolbar.SetCrop"/>。
/// </remarks>
public partial class ImageEditWindow
{
    /// <summary>切 / 退裁剪模式（null = 退出；由工具条的裁剪格子驱动）。</summary>
    private void SetCropAxis(CropAxis? axis)
    {
        _cropping = false;
        _cropPointerInside = false;

        _cropAxis = axis is { } value && _doc.HasImage ? value : null;

        Toolbar.SetCrop(_cropAxis);
        UpdateCanvasCursor();
        UpdateCropLayer();
    }

    /// <summary>退出裁剪模式（Esc / 打开别的图）。</summary>
    private void CancelCropMode() => SetCropAxis(null);

    /// <summary>松手提交：真的剪掉了才换文档，进撤销栈。</summary>
    private void CommitCrop()
    {
        if (_cropAxis is not { } axis)
        {
            UpdateCropLayer();
            return;
        }

        bool cropped = _doc.Crop(axis, _cropFirst, _cropLast);

        // 松手后上游按指针重算：条带塌成一条导引线（长度 0）
        _cropLast = _cropFirst;

        if (cropped)
        {
            Canvas.Document = _doc.Doc;
            Canvas.Width = _doc.Width;
            Canvas.Height = _doc.Height;
            SelectMark(-1);
            Canvas.InvalidateVisual();
            KeepCameraAfterResize();
            Notice($"已裁剪：{_doc.Width} × {_doc.Height} px");
        }

        UpdateCropLayer();
        UpdateChrome();
        PlaceOverlay();

        // 尺寸变了：自动打码的识别结果作废，重新扫一遍
        SyncAutoFilter(detect: true);
    }

    /// <summary>把当前条带摆到预览层上（窗口内容区坐标）。</summary>
    private void UpdateCropLayer()
    {
        if (_cropAxis is not { } axis || !_doc.HasImage || (!_cropping && !_cropPointerInside))
        {
            CropLayer.ImageRect = Rect.Empty;
            CropLayer.Strip = Rect.Empty;
            CropLayer.InvalidateVisual();
            return;
        }

        var content = ContentSize;
        int extent = axis == CropAxis.Rows ? (int)content.Height : (int)content.Width;
        if (extent <= 0)
        {
            CropLayer.ImageRect = Rect.Empty;
            CropLayer.Strip = Rect.Empty;
            CropLayer.InvalidateVisual();
            return;
        }

        var image = new Rect(_offset.X, _offset.Y, content.Width * _zoom, content.Height * _zoom);
        var span = ImageEditGeometry.Resolve(_cropFirst, _cropLast, extent);

        // 先在「图像像素」坐标里量出条带，再按缩放摆到窗口内容区
        var local = ImageEditGeometry.Strip(new Rect(0, 0, content.Width, content.Height), axis, span, extent);
        var strip = new Rect(
            image.X + local.X * _zoom, image.Y + local.Y * _zoom,
            local.Width * _zoom, local.Height * _zoom);

        CropLayer.ImageRect = image;
        CropLayer.Strip = strip;
        CropLayer.Rows = axis == CropAxis.Rows;
        CropLayer.Pixels = span.Length;
        CropLayer.DpiScale = _dpi > 0 ? _dpi : 1;
        CropLayer.InvalidateVisual();
    }
}
