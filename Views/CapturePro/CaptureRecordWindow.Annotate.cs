using System;
using System.Windows;
using System.Windows.Controls;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录制期标注：面板第一行的绘制工具驱动 <see cref="RecordAnnotationWindow"/>。
/// </summary>
/// <remarks>
/// <para>
/// 标注层是**真的覆盖在录制区上的透明窗口**（会被一起录进成品），不是合成进帧的图层：
/// 用户看到什么，成品里就是什么。工具、颜色、撤销 / 重做全部走它。
/// </para>
/// <para>
/// 开录那一刻抓一张底图交给它——马赛克 / 滤镜要取那块像素（Snow Shot 也是这样，
/// 打码的内容是**放上去那一刻**的画面，不做实时跟踪）。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    /// <summary>录制期的标注层（没开始录 / 建失败时为 null）。</summary>
    internal RecordAnnotationWindow? Annotations { get; private set; }

    /// <summary>按钮当前选中的绘制工具。</summary>
    internal RecordTool CurrentTool
    {
        get
        {
            foreach (var button in _toolButtons)
                if (button.IsChecked == true && button.Tag is RecordTool tool) return tool;

            return RecordTool.Select;
        }
    }

    /// <summary>有没有选中任何一个绘制工具（决定标注层收不收鼠标）。</summary>
    private bool IsToolSelected => _toolButtons.Exists(button => button.IsChecked == true);

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: RecordTool tool }) return;

        if (Annotations != null)
        {
            Annotations.Tool = tool;
            // 有工具 = 画布收鼠标（照上游：绘制时选区铺 alpha 2 的输入面）
            Annotations.InputActive = true;
        }
    }

    /// <summary>开录：把标注层摆到录制区上，并在它之后把工具条顶回最上面。</summary>
    private void ShowAnnotations()
    {
        if (Annotations != null) return;

        try
        {
            var image = ScreenShot.Capture(_area);
            double scale = ScreenShot.ScaleOf(_area);

            var layer = new RecordAnnotationWindow(_area, image, scale);
            layer.Changed += RefreshUndo;
            layer.Tool = CurrentTool;
            // 开录前就选好工具（或在录制中已选中）→ 画布直接收鼠标；没选 → 点击穿到被录的程序
            layer.InputActive = IsToolSelected;
            layer.Show();

            Annotations = layer;
            RaiseAbove();
            RefreshUndo();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "建标注层失败（这一轮不能画标注）：" + ex.Message);
        }
    }

    /// <summary>收尾 / 取消 / 关窗：标注层跟着走，别留一个透明窗口挂在屏幕上。</summary>
    private void CloseAnnotation()
    {
        var layer = Annotations;
        Annotations = null;
        layer?.CloseQuietly();
    }

    private void RefreshUndo()
    {
        _undoBtn.IsEnabled = Annotations?.CanUndo == true;
        _redoBtn.IsEnabled = Annotations?.CanRedo == true;
    }
}
