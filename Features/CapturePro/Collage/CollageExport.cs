using System;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Native;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼图成品的三条出口（照上游 <c>ImageStitchingWindow</c> 的复制 / 保存）：复制到剪贴板、
/// 另存为（复用 <see cref="CaptureSave"/> 的格式 / 尺寸 / 质量对话框）。
/// </summary>
internal static class CollageExport
{
    /// <summary>默认文件名（不含扩展名），照项目其它导出的时间戳习惯。</summary>
    public static string DefaultName => $"拼图-{DateTime.Now:yyyyMMdd-HHmmss}";

    /// <summary>复制成品到剪贴板（DIB + PNG，走项目统一的原始 Win32 剪贴板）。</summary>
    public static bool Copy(BitmapSource image) => NativeClipboard.SetImage(image);

    /// <summary>另存为；取消或失败返回 false。</summary>
    public static bool Save(Window? owner, BitmapSource image) =>
        CaptureSave.TrySave(owner, image, DefaultName);
}
