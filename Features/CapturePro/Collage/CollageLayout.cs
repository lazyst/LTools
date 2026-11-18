using System;
using System.Collections.Generic;
using System.Windows;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 一次拼接算出来的几何：输出尺寸 + 每张图的自动矩形 / 实际矩形 / 所在格的裁剪矩形。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageStitchingLayout</c>：<see cref="AutomaticRects"/> 是自动布局给出的位置，
/// <see cref="ImageRects"/> 才是手摆之后真正要画的位置（没摆过的与自动矩形相同），
/// 而输出尺寸**始终按自动布局算**——手摆只让图片在自己的格里挪，不会把成品撑大。
/// </remarks>
internal sealed class CollageLayout
{
    public static readonly CollageLayout Invalid = new();

    private CollageLayout()
    {
        AutomaticRects = Array.Empty<Int32Rect>();
        ImageRects = Array.Empty<Int32Rect>();
        Clips = Array.Empty<Int32Rect>();
        Error = "";
    }

    private CollageLayout(int width, int height, Int32Rect[] automatic, Int32Rect[] images,
        Int32Rect[] clips)
    {
        Width = width;
        Height = height;
        AutomaticRects = automatic;
        ImageRects = images;
        Clips = clips;
        Error = "";
    }

    private CollageLayout(string error)
    {
        AutomaticRects = Array.Empty<Int32Rect>();
        ImageRects = Array.Empty<Int32Rect>();
        Clips = Array.Empty<Int32Rect>();
        Error = error;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>自动布局给出的图片位置（手摆时算尺寸 / 判空用这一份）。</summary>
    public IReadOnlyList<Int32Rect> AutomaticRects { get; }

    /// <summary>实际要画的图片位置（与 <see cref="CollageDocument.Items"/> 一一对应）。</summary>
    public IReadOnlyList<Int32Rect> ImageRects { get; }

    /// <summary>每张图的裁剪矩形（所在格）。</summary>
    public IReadOnlyList<Int32Rect> Clips { get; }

    /// <summary>算不出来时给人看的一句话；空 = 正常。</summary>
    public string Error { get; }

    public bool IsValid => Error.Length == 0 && Width > 0 && Height > 0 && ImageRects.Count > 0;

    internal static CollageLayout Create(int width, int height, IReadOnlyList<Int32Rect> automatic,
        IReadOnlyList<Int32Rect> images, IReadOnlyList<Int32Rect> clips) => new(width, height,
        Copy(automatic), Copy(images), Copy(clips));

    internal static CollageLayout Fail(string error) => new(error);

    private static Int32Rect[] Copy(IReadOnlyList<Int32Rect> rects)
    {
        var copy = new Int32Rect[rects.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = rects[i];
        return copy;
    }
}
