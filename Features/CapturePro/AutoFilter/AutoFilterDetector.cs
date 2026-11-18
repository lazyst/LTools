using System;
using System.Collections.Generic;
using System.Linq;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>检测到的一块区域（<see cref="Id"/> = 输出序号，1 起；<see cref="Category"/> 为上游类型名）。</summary>
internal readonly record struct DetectedRegion(int Id, RRect Bounds, string Category);

/// <summary>区域类型的显示名（同时也是上游的 <c>type</c> 字符串）。</summary>
internal static class RegionCategories
{
    public const string Text = "text";
    public const string TextInBox = "text_in_box";
    public const string Image = "image";
    public const string Avatar = "avatar";
    public const string Icon = "icon";
    public const string MessageBox = "message_box";
    public const string TextBlock = "text_block";

    /// <summary>下拉顺序（照上游 <c>kCategories</c>）。</summary>
    public static readonly string[] All =
    {
        Text, TextInBox, Image, Avatar, Icon, MessageBox, TextBlock,
    };

    public static string Label(string category) => category switch
    {
        Text => "正文",
        TextInBox => "框内文字",
        Image => "图片",
        Avatar => "头像",
        Icon => "图标",
        MessageBox => "消息框",
        TextBlock => "整段文字",
        _ => category,
    };
}

/// <summary>
/// 自动打码的区域检测入口，逐行照抄上游 <c>pipeline.rs</c>
/// （准备证据 → 媒体候选 → 文字与最终分类）。
/// </summary>
internal static class AutoFilterDetector
{
    private sealed class PreparedImage
    {
        public required BgrImg Bgr { get; init; }
        public required double[] Bg { get; init; }
        public required BgrImg LocalBg { get; init; }
        public required List<RRect> Surfaces { get; init; }
        public required List<RRect> Controls { get; init; }
        public required MaskGrid Border { get; init; }
        public required MaskGrid Fg { get; init; }
        public required MaskGrid LocalFg { get; init; }
    }

    private sealed class MediaCandidates
    {
        public required List<RRect> Images { get; init; }
        public required MaskGrid ImageMask { get; init; }
        public required List<RRect> Avatars { get; init; }
        public required MaskGrid BadgeMask { get; init; }
        public required List<RRect> Dots { get; init; }
    }

    /// <summary>检测一块 BGR 图里的所有可打码区域，坐标是源图半开区间像素。</summary>
    public static List<DetectedRegion> Detect(BgrImg source)
    {
        if (source.W == 0 || source.H == 0) return new List<DetectedRegion>();
        var prepared = Prepare(source);
        var media = DetectMedia(prepared);
        var regions = DetectTextAndClassify(prepared, media);
        var output = new List<DetectedRegion>(regions.Count);
        for (int i = 0; i < regions.Count; i++) output.Add(new DetectedRegion(i + 1, regions[i].Rect, regions[i].Kind));
        return output;
    }

    private static PreparedImage Prepare(BgrImg source)
    {
        var bgr = source.Clone();
        var bg = ColorStats.Background(bgr);
        var (localBg, surfaces, border) = Stages.EstimateSurfaces(bgr);
        var (controls, outlines) = Edges.OutlinedControls(bgr);
        controls = Selection.Merge(controls, 0.8);
        border = MaskOps.Or(border, outlines);
        var fg = ColorStats.Foreground(bgr, bg, 24.0);
        var localFg = MaskOps.Suppress(ColorStats.LocalForeground(bgr, localBg, 20), border);
        return new PreparedImage
        {
            Bgr = bgr,
            Bg = bg,
            LocalBg = localBg,
            Surfaces = surfaces,
            Controls = controls,
            Border = border,
            Fg = fg,
            LocalFg = localFg,
        };
    }

    private static MediaCandidates DetectMedia(PreparedImage prepared)
    {
        var bgr = prepared.Bgr;
        var bg = prepared.Bg;
        var surfaces = prepared.Surfaces;
        var fg = prepared.Fg;
        var localFg = prepared.LocalFg;
        int w = bgr.W;
        int h = bgr.H;

        var images = Stages.DetectImages(bgr, fg)
            .Where(r => ColorStats.SurfaceFraction(bgr, r) < 0.65)
            .ToList();
        var rawBadges = Stages.SquareBadges(bgr, fg, bg, MaskOps.Rasterize(images, w, h));
        images.AddRange(Stages.Textures(bgr, localFg)
            .Where(r => rawBadges.Count(a => Geo.Containment(a, r) > 0.8) < 2));
        images.AddRange(Stages.DetectLogos(bgr, fg)
            .Where(r => r.Y < 0.06 * h && !surfaces.Any(s => Geo.Containment(r, s) > 0.8)));
        images = Selection.Merge(images, 0.2);

        var imageMask = Morphology.Dilate(MaskOps.Rasterize(images, w, h), 3);
        var avatarCandidates = rawBadges
            .Where(r => !images.Any(im => Geo.Containment(r, im) > 0.6)
                        && !surfaces.Any(s => Geo.Containment(r, s) > 0.9
                                              && s.Area < 4 * r.Area
                                              && s.W > 1.6 * s.H))
            .ToList();

        var accepted = Stages.RejectGlyphs(avatarCandidates, localFg, surfaces);
        var glyphs = avatarCandidates.Where(r => !accepted.Contains(r)).ToList();
        accepted.AddRange(surfaces.Where(s =>
            Math.Min(s.W, s.H) is >= 18 and <= 90
            && Math.Max(s.W, s.H) <= 1.2 * Math.Min(s.W, s.H)
            && !images.Any(r => Geo.Containment(s, r) > 0.7)
            && !glyphs.Any(r => Geo.Containment(s, r) > 0.7)));

        var avatars = Selection.Merge(accepted, 0.3);
        var badgeMask = Morphology.Dilate(MaskOps.Rasterize(avatars, w, h), 3);
        var dots = Stages.DotIcons(localFg);
        return new MediaCandidates
        {
            Images = images,
            ImageMask = imageMask,
            Avatars = avatars,
            BadgeMask = badgeMask,
            Dots = dots,
        };
    }

    private static List<RegionBox> DetectTextAndClassify(PreparedImage prepared, MediaCandidates media)
    {
        var bgr = prepared.Bgr;
        var surfaces = prepared.Surfaces;
        int w = bgr.W;
        int h = bgr.H;

        var textSuppress = MaskOps.Or(
            MaskOps.Or(MaskOps.Or(media.ImageMask, media.BadgeMask), prepared.Border),
            MaskOps.Rasterize(media.Dots, w, h));
        var (texts, ink, charHeight) = Stages.TextLines(bgr, prepared.LocalBg, textSuppress);

        var boxes = surfaces
            .Where(s => s.W > 2 * s.H
                        && s.H >= 1.5 * charHeight
                        && s.W < 0.4 * w
                        && s.H < 0.2 * h
                        && s.Area > 2000
                        && ColorStats.BoundaryContrast(bgr, s) >= 20.0
                        && !media.Images.Any(r => Geo.Containment(s, r) > 0.7)
                        && ColorStats.SurfaceFraction(bgr, s) < 0.97)
            .ToList();
        boxes.AddRange(prepared.Controls);
        boxes = Selection.Merge(boxes, 0.8);

        var textMask = Morphology.Dilate(MaskOps.Rasterize(texts, w, h), 3);
        var sup = MaskOps.Or(MaskOps.Or(media.ImageMask, textMask), media.BadgeMask);
        var (avatars, extraIcons) = Stages.RefineAvatars(media.Avatars, texts, w, h);
        var icons = Stages.Icons(prepared.LocalFg, sup, textMask, texts);
        icons.AddRange(extraIcons);
        icons.AddRange(media.Dots);

        var blocks = Stages.Paragraphs(texts);
        texts = Stages.SplitStyledLines(Stages.SplitSurfaceText(texts, surfaces, ink), bgr, ink);

        var regions = Finalize(texts, media.Images, avatars, icons, boxes, w, h);
        regions.AddRange(blocks.Select(r => new RegionBox(r, RegionCategories.TextBlock)));
        regions = Selection.Nms(regions, 0.8);
        return Selection.SortRegions(regions);
    }

    private static List<RegionBox> Finalize(List<RRect> texts, List<RRect> images, List<RRect> avatars,
        List<RRect> icons, List<RRect> boxes, int w, int h)
    {
        avatars = avatars.Where(r => !images.Any(im => Geo.Containment(r, im) > 0.6)).ToList();
        icons = icons
            .Where(r => !images.Any(im => Geo.Containment(r, im) > 0.6)
                        && !avatars.Any(a => Geo.IoU(r, a) > 0.5))
            .ToList();

        var regions = new List<RegionBox>();
        foreach (var t in texts)
        {
            if (images.Any(im => Geo.Containment(t, im) >= 0.72)
                || avatars.Any(a => Geo.Containment(t, a) >= 0.72))
            {
                continue;
            }

            double aspect = (double)t.W / Math.Max(t.H, 1);
            if (t.Y < 0.12 * h
                && t.X > 0.82 * w
                && aspect is >= 0.7 and <= 1.45
                && Math.Max(t.W, t.H) <= 0.04 * h)
            {
                icons.Add(t);
                continue;
            }

            regions.Add(new RegionBox(t,
                boxes.Any(b => Geo.Containment(t, b) >= 0.8) ? RegionCategories.TextInBox : RegionCategories.Text));
        }

        regions.AddRange(images.Select(r => new RegionBox(r, RegionCategories.Image)));
        regions.AddRange(avatars.Select(r => new RegionBox(r, RegionCategories.Avatar)));
        regions.AddRange(icons.Select(r => new RegionBox(r, RegionCategories.Icon)));
        regions.AddRange(boxes.Select(r => new RegionBox(r, RegionCategories.MessageBox)));
        return regions;
    }
}
