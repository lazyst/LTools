using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>
/// Canny 边缘 / Suzuki 轮廓 / OpenCV 粗线绘制，逐行照抄上游
/// <c>backend/pure_rust/{canny,contours,draw}.rs</c>。
/// </summary>
internal static class Edges
{
    private const int XyShift = 16;
    private const long XyOne = 1L << XyShift;

    private static readonly (int Dx, int Dy)[] Deltas =
    {
        (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1), (0, 1), (1, 1),
    };

    /// <summary>OpenCV <c>COLOR_BGR2GRAY</c> 的 8 位系数（BY15 / GY15 / RY15）。</summary>
    public static byte Grayscale(byte b, byte g, byte r)
    {
        int value = 3735 * b + 19235 * g + 9798 * r + (1 << 14);
        return (byte)(value >> 15);
    }

    /// <summary>标量 Canny（对齐 OpenCV 的 8 位 L1 路径：low 20 / high 60）。</summary>
    public static MaskGrid CannyEdges(BgrImg input)
    {
        if (input.W == 0 || input.H == 0) return new MaskGrid(input.W, input.H);

        int width = input.W;
        int height = input.H;
        var gray = new byte[width * height];
        for (int i = 0, p = 0; i < gray.Length; i++, p += 3)
            gray[i] = Grayscale(input.Data[p], input.Data[p + 1], input.Data[p + 2]);

        var dx = new short[gray.Length];
        var dy = new short[gray.Length];
        Sobel(gray, width, height, dx, dy);

        int mapstep = width + 2;
        var mag = new int[(height + 2) * mapstep];
        var map = new byte[(height + 2) * mapstep];
        Array.Fill(map, (byte)1);
        for (int y = 0; y < height; y++)
        {
            int row = (y + 1) * mapstep + 1;
            for (int x = 0; x < width; x++)
            {
                int index = y * width + x;
                mag[row + x] = Math.Abs((int)dx[index]) + Math.Abs((int)dy[index]);
                map[row + x] = 1;
            }
        }

        const int tg22 = 13_573;
        const int low = 20;
        const int high = 60;
        var stack = new Stack<int>();
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y + 1) * mapstep + (x + 1);
                int m = mag[index];
                if (m <= low)
                {
                    map[index] = 1;
                    continue;
                }

                int src = y * width + x;
                int xs = dx[src];
                int ys = dy[src];
                int absX = Math.Abs(xs);
                int absY = Math.Abs(ys) << 15;
                int tg22x = absX * tg22;
                bool isMaximum;
                if (absY < tg22x)
                {
                    isMaximum = m > mag[index - 1] && m >= mag[index + 1];
                }
                else
                {
                    int tg67x = tg22x + (absX << 16);
                    if (absY > tg67x)
                    {
                        isMaximum = m > mag[index - mapstep] && m >= mag[index + mapstep];
                    }
                    else
                    {
                        int step = (xs ^ ys) < 0 ? -1 : 1;
                        isMaximum = m > mag[index - mapstep - step] && m > mag[index + mapstep + step];
                    }
                }

                if (isMaximum)
                {
                    if (m > high)
                    {
                        map[index] = 2;
                        stack.Push(index);
                    }
                    else
                    {
                        map[index] = 0;
                    }
                }
                else
                {
                    map[index] = 1;
                }
            }

        int[] neighborOffsets =
        {
            -mapstep - 1, -mapstep, -mapstep + 1, -1, 1, mapstep - 1, mapstep, mapstep + 1,
        };
        while (stack.Count > 0)
        {
            int index = stack.Pop();
            foreach (int offset in neighborOffsets)
            {
                int neighbor = index + offset;
                if (map[neighbor] != 0) continue;
                map[neighbor] = 2;
                stack.Push(neighbor);
            }
        }

        var edges = new MaskGrid(width, height);
        for (int y = 0; y < height; y++)
        {
            int row = (y + 1) * mapstep + 1;
            for (int x = 0; x < width; x++)
                edges.Data[y * width + x] = map[row + x] == 2 ? (byte)255 : (byte)0;
        }

        return edges;
    }

    /// <summary>3x3 Sobel（可分离核，<c>BORDER_REPLICATE</c>）。</summary>
    private static void Sobel(byte[] gray, int width, int height, short[] dx, short[] dy)
    {
        int workers = height < 64 ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 8);
        int chunk = (height + workers - 1) / Math.Max(1, workers);

        void Rows(int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
            {
                int ym = Math.Max(0, y - 1);
                int yp = Math.Min(y + 1, height - 1);
                int top = ym * width;
                int mid = y * width;
                int bot = yp * width;
                for (int x = 0; x < width; x++)
                {
                    int xm = Math.Max(0, x - 1);
                    int xp = Math.Min(x + 1, width - 1);
                    int p00 = gray[top + xm];
                    int p01 = gray[top + x];
                    int p02 = gray[top + xp];
                    int p10 = gray[mid + xm];
                    int p12 = gray[mid + xp];
                    int p20 = gray[bot + xm];
                    int p21 = gray[bot + x];
                    int p22 = gray[bot + xp];
                    dx[mid + x] = (short)((p02 + 2 * p12 + p22) - (p00 + 2 * p10 + p20));
                    dy[mid + x] = (short)((p20 + 2 * p21 + p22) - (p00 + 2 * p01 + p02));
                }
            }
        }

        if (workers == 1 || chunk < 1)
        {
            Rows(0, height);
            return;
        }

        int bands = (height + chunk - 1) / chunk;
        Parallel.For(0, bands, band =>
        {
            int y0 = band * chunk;
            Rows(y0, Math.Min(height, y0 + chunk));
        });
    }

    /// <summary>轮廓 / 描边控制（Canny → 轮廓 → 过滤 → 3px 描边）。</summary>
    public static (List<RRect> Controls, MaskGrid Outlines) OutlinedControls(BgrImg input)
    {
        var edges = CannyEdges(input);
        var contours = FindContours(edges);
        var outlines = new MaskGrid(input.W, input.H);
        var controls = new List<RRect>();
        foreach (var contour in contours)
        {
            var rect = BoundingRect(contour);
            if (rect == null) continue;
            var r = rect.Value;
            double aspect = (double)r.W / Math.Max(r.H, 1);
            if (r.H < 20
                || r.H > 0.15 * input.H
                || r.W < 50
                || r.W > 0.6 * input.W
                || aspect <= 1.5
                || aspect >= 15.0)
            {
                continue;
            }

            if (ContourArea(contour) < 0.88 * (r.W * r.H)) continue;
            controls.Add(r);
            DrawContours(outlines, new List<List<(int X, int Y)>> { contour }, 3);
        }

        return (controls, outlines);
    }

    /// <summary>Suzuki 轮廓（<c>RETR_LIST</c> + <c>CHAIN_APPROX_SIMPLE</c>）。</summary>
    public static List<List<(int X, int Y)>> FindContours(MaskGrid mask)
    {
        var contours = new List<List<(int, int)>>();
        if (mask.W == 0 || mask.H == 0) return contours;

        int stride = mask.W + 2;
        int height = mask.H + 2;
        var image = new byte[stride * height];
        for (int y = 0; y < mask.H; y++)
            for (int x = 0; x < mask.W; x++)
            {
                if (mask.At(x, y) == 0) continue;
                image[(y + 1) * stride + (x + 1)] = 1;
            }

        int cx = 1;
        int cy = 1;
        var lastPos = (X: 0, Y: 1);
        int width = stride - 1;
        int scanHeight = height - 1;
        byte prev = image[cy * stride + cx - 1];

        while (cy < scanHeight)
        {
            while (cx < width)
            {
                byte pixel = image[cy * stride + cx];
                while (cx < width && pixel == prev)
                {
                    cx += 1;
                    if (cx < width) pixel = image[cy * stride + cx];
                }

                if (cx >= width) break;
                pixel = image[cy * stride + cx];
                var contour = TryContour(image, stride, prev, pixel, ref lastPos, cx, cy);
                if (contour != null)
                {
                    contours.Add(contour);
                    cx += 1;
                    prev = image[cy * stride + cx - 1];
                }
                else
                {
                    prev = pixel;
                    if ((pixel & 0xFE) != 0) lastPos.X = cx;
                    cx += 1;
                }
            }

            cy += 1;
            lastPos = (0, cy);
            cx = 1;
            prev = 0;
        }

        contours.Reverse();
        return contours;
    }

    private static List<(int X, int Y)>? TryContour(byte[] image, int stride, byte prev, byte pixel,
        ref (int X, int Y) lastPos, int x, int y)
    {
        bool isHole = false;
        if (!(prev == 0 && pixel == 1))
        {
            if (pixel != 0 || (sbyte)prev < 1) return null;
            if ((prev & 0xFE) != 0) lastPos.X = x - 1;
            isHole = true;
        }

        lastPos.X = x - (isHole ? 1 : 0);
        var start = (X: x - (isHole ? 1 : 0), Y: y);
        return FetchContour(image, stride, start, isHole);
    }

    private static List<(int X, int Y)> FetchContour(byte[] image, int stride, (int X, int Y) start, bool isHole)
    {
        var points = new List<(int, int)>();
        int startIndex = start.Y * stride + start.X;
        int sEnd = isHole ? 0 : 4;
        int s = sEnd;
        int i1;
        while (true)
        {
            s = (s - 1) & 7;
            i1 = Neighbor(startIndex, stride, s);
            if (image[i1] != 0 || s == sEnd) break;
        }

        var pt = (X: start.X - 1, Y: start.Y - 1);
        if (s == sEnd)
        {
            image[startIndex] = 0x82;
            points.Add(pt);
            return points;
        }

        int i3 = startIndex;
        int prevS = s ^ 4;
        while (true)
        {
            sEnd = s;
            s = Math.Min(s, 15);
            int i4 = i3;
            while (s < 15)
            {
                s += 1;
                i4 = Neighbor(i3, stride, s);
                if (image[i4] != 0) break;
            }

            s &= 7;
            if ((uint)(s - 1) < (uint)sEnd) image[i3] = 0x82;
            else if (image[i3] == 1) image[i3] = 2;

            if (s != prevS) points.Add(pt);
            prevS = s;
            pt.X += Deltas[s].Dx;
            pt.Y += Deltas[s].Dy;
            if (i4 == startIndex && i3 == i1) break;
            i3 = i4;
            s = (s + 4) & 7;
        }

        return points;
    }

    private static int Neighbor(int index, int stride, int direction)
    {
        var (dx, dy) = Deltas[direction & 7];
        return index + dx + dy * stride;
    }

    public static RRect? BoundingRect(List<(int X, int Y)> contour)
    {
        if (contour.Count == 0) return null;
        int x0 = contour[0].X, y0 = contour[0].Y, x1 = x0, y1 = y0;
        for (int i = 1; i < contour.Count; i++)
        {
            x0 = Math.Min(x0, contour[i].X);
            y0 = Math.Min(y0, contour[i].Y);
            x1 = Math.Max(x1, contour[i].X);
            y1 = Math.Max(y1, contour[i].Y);
        }

        return new RRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    public static double ContourArea(List<(int X, int Y)> contour)
    {
        if (contour.Count == 0) return 0.0;
        double area = 0;
        var prev = contour[^1];
        foreach (var point in contour)
        {
            area += (double)prev.X * point.Y - (double)prev.Y * point.X;
            prev = point;
        }

        return Math.Abs(area * 0.5);
    }

    public static void DrawContours(MaskGrid mask, List<List<(int X, int Y)>> contours, int thickness)
    {
        foreach (var contour in contours)
        {
            if (contour.Count == 0) continue;
            for (int i = 0; i < contour.Count; i++)
            {
                var p0 = contour[i];
                var p1 = contour[(i + 1) % contour.Count];
                ThickLine(mask, p0, p1, thickness, 2);
            }
        }
    }

    private static void ThickLine(MaskGrid mask, (int X, int Y) a, (int X, int Y) b, int thickness, int flags)
    {
        var p0 = ((long)a.X, (long)a.Y);
        var p1 = ((long)b.X, (long)b.Y);
        if (thickness > 1)
        {
            var size = ((long)mask.W, (long)mask.H);
            if (!Contains(size, p0) || !Contains(size, p1))
            {
                long margin = thickness;
                p0 = (p0.Item1 + margin, p0.Item2 + margin);
                p1 = (p1.Item1 + margin, p1.Item2 + margin);
                ClipLine((size.Item1 + 2 * margin, size.Item2 + 2 * margin), ref p0, ref p1);
                p0 = (p0.Item1 - margin, p0.Item2 - margin);
                p1 = (p1.Item1 - margin, p1.Item2 - margin);
            }
        }

        p0 = (p0.Item1 << XyShift, p0.Item2 << XyShift);
        p1 = (p1.Item1 << XyShift, p1.Item2 << XyShift);

        int oddThickness = thickness & 1;
        int shifted = thickness << (XyShift - 1);
        double inv = 1.0 / XyOne;
        double ddx = (p0.Item1 - p1.Item1) * inv;
        double ddy = (p1.Item2 - p0.Item2) * inv;
        double r = ddx * ddx + ddy * ddy;
        if (Math.Abs(r) > double.Epsilon)
        {
            r = (shifted + oddThickness * XyOne * 0.5) / Math.Sqrt(r);
            long dpx = CvRound(ddy * r);
            long dpy = CvRound(ddx * r);
            var quad = new List<(long, long)>
            {
                (p0.Item1 + dpx, p0.Item2 + dpy),
                (p0.Item1 - dpx, p0.Item2 - dpy),
                (p1.Item1 - dpx, p1.Item2 - dpy),
                (p1.Item1 + dpx, p1.Item2 + dpy),
            };
            FillConvexPoly(mask, quad);
        }

        int radius = (int)((shifted + (XyOne >> 1)) >> XyShift);
        var cursor = p0;
        for (int i = 0; i < 2; i++)
        {
            if ((flags & (i + 1)) != 0)
            {
                var center = ((int)((cursor.Item1 + (XyOne >> 1)) >> XyShift),
                    (int)((cursor.Item2 + (XyOne >> 1)) >> XyShift));
                FillCircle(mask, center, radius);
            }

            cursor = p1;
        }
    }

    private static bool Contains((long W, long H) size, (long X, long Y) point) =>
        point.X >= 0 && point.Y >= 0 && point.X < size.W && point.Y < size.H;

    private static int CvRound(double value) => (int)Math.Round(value, MidpointRounding.ToEven);

    private static bool ClipLine((long W, long H) size, ref (long X, long Y) pt1, ref (long X, long Y) pt2)
    {
        if (size.W <= 0 || size.H <= 0) return false;
        long right = size.W - 1;
        long bottom = size.H - 1;

        int Code(long x, long y) =>
            (x < 0 ? 1 : 0) + (x > right ? 2 : 0) + (y < 0 ? 4 : 0) + (y > bottom ? 8 : 0);

        int c1 = Code(pt1.X, pt1.Y);
        int c2 = Code(pt2.X, pt2.Y);
        if ((c1 & c2) == 0 && (c1 | c2) != 0)
        {
            if ((c1 & 12) != 0)
            {
                long a = c1 < 8 ? 0 : bottom;
                pt1.X += (long)((a - pt1.Y) * (double)(pt2.X - pt1.X) / (pt2.Y - pt1.Y));
                pt1.Y = a;
                c1 = (pt1.X < 0 ? 1 : 0) + (pt1.X > right ? 2 : 0);
            }

            if ((c2 & 12) != 0)
            {
                long a = c2 < 8 ? 0 : bottom;
                pt2.X += (long)((a - pt2.Y) * (double)(pt2.X - pt1.X) / (pt2.Y - pt1.Y));
                pt2.Y = a;
                c2 = (pt2.X < 0 ? 1 : 0) + (pt2.X > right ? 2 : 0);
            }

            if ((c1 & c2) == 0 && (c1 | c2) != 0)
            {
                if (c1 != 0)
                {
                    long a = c1 == 1 ? 0 : right;
                    pt1.Y += (long)((a - pt1.X) * (double)(pt2.Y - pt1.Y) / (pt2.X - pt1.X));
                    pt1.X = a;
                    c1 = 0;
                }

                if (c2 != 0)
                {
                    long a = c2 == 1 ? 0 : right;
                    pt2.Y += (long)((a - pt2.X) * (double)(pt2.Y - pt1.Y) / (pt2.X - pt1.X));
                    pt2.X = a;
                    c2 = 0;
                }
            }
        }

        return (c1 | c2) == 0;
    }

    private static void Line2(MaskGrid mask, (long X, long Y) a, (long X, long Y) b)
    {
        var pt1 = a;
        var pt2 = b;
        var size = ((long)mask.W << XyShift, (long)mask.H << XyShift);
        if (!ClipLine(size, ref pt1, ref pt2)) return;

        long dx = pt2.X - pt1.X;
        long dy = pt2.Y - pt1.Y;
        long j = dx < 0 ? -1 : 0;
        long ax = (dx ^ j) - j;
        long i = dy < 0 ? -1 : 0;
        long ay = (dy ^ i) - i;

        long xStep, yStep;
        int ecount;
        bool dominantX;
        if (ax > ay)
        {
            long dyAbs = (dy ^ j) - j;
            if (j != 0) (pt1, pt2) = (pt2, pt1);
            long yS = dyAbs * XyOne / (ax | 1);
            ecount = (int)((pt2.X - pt1.X) >> XyShift);
            xStep = XyOne;
            yStep = yS;
            dominantX = true;
        }
        else
        {
            long dxAbs = (dx ^ i) - i;
            if (i != 0) (pt1, pt2) = (pt2, pt1);
            long xS = dxAbs * XyOne / (ay | 1);
            ecount = (int)((pt2.Y - pt1.Y) >> XyShift);
            xStep = xS;
            yStep = XyOne;
            dominantX = false;
        }

        pt1.X += XyOne >> 1;
        pt1.Y += XyOne >> 1;
        Put(mask, (int)((pt2.X + (XyOne >> 1)) >> XyShift), (int)((pt2.Y + (XyOne >> 1)) >> XyShift));
        if (dominantX)
        {
            pt1.X >>= XyShift;
            while (ecount >= 0)
            {
                Put(mask, (int)pt1.X, (int)(pt1.Y >> XyShift));
                pt1.X += 1;
                pt1.Y += yStep;
                ecount -= 1;
            }
        }
        else
        {
            pt1.Y >>= XyShift;
            while (ecount >= 0)
            {
                Put(mask, (int)(pt1.X >> XyShift), (int)pt1.Y);
                pt1.X += xStep;
                pt1.Y += 1;
                ecount -= 1;
            }
        }
    }

    private sealed class Edge
    {
        public int Idx;
        public int Di;
        public long X;
        public long Dx;
        public int Ye;
    }

    private static void FillConvexPoly(MaskGrid mask, List<(long X, long Y)> vertices)
    {
        int npts = vertices.Count;
        if (npts == 0) return;

        long delta = 1L << (XyShift - 1);
        long delta1 = XyOne >> 1;
        long delta2 = XyOne >> 1;
        var p0 = vertices[npts - 1];
        long xmin = vertices[0].X;
        long xmax = vertices[0].X;
        long ymin = vertices[0].Y;
        long ymax = vertices[0].Y;
        int imin = 0;
        for (int i = 0; i < npts; i++)
        {
            var p = vertices[i];
            if (p.Y < ymin)
            {
                ymin = p.Y;
                imin = i;
            }

            ymax = Math.Max(ymax, p.Y);
            xmax = Math.Max(xmax, p.X);
            xmin = Math.Min(xmin, p.X);
            Line2(mask, p0, p);
            p0 = p;
        }

        xmin = (xmin + delta) >> XyShift;
        xmax = (xmax + delta) >> XyShift;
        ymin = (ymin + delta) >> XyShift;
        ymax = (ymax + delta) >> XyShift;
        if (npts < 3 || xmax < 0 || ymax < 0 || xmin >= mask.W || ymin >= mask.H) return;

        ymax = Math.Min(ymax, mask.H - 1);
        int edges = npts;
        int y = (int)ymin;
        var edge = new[]
        {
            new Edge { Idx = imin, Di = 1, X = -XyOne, Dx = 0, Ye = y },
            new Edge { Idx = imin, Di = npts - 1, X = -XyOne, Dx = 0, Ye = y },
        };

        while (true)
        {
            foreach (var item in edge)
            {
                if (y < item.Ye) continue;
                int idx0 = item.Idx;
                int di = item.Di;
                int idx = idx0 + di;
                if (idx >= npts) idx -= npts;
                while (true)
                {
                    int remaining = edges;
                    edges -= 1;
                    if (remaining <= 0) break;
                    int ty = (int)((vertices[idx].Y + delta) >> XyShift);
                    if (ty > y)
                    {
                        long xs = vertices[idx0].X;
                        long xe = vertices[idx].X;
                        item.Ye = ty;
                        item.Dx = ((xe - xs) * 2 + (ty - y)) / (2 * (ty - y));
                        item.X = xs;
                        item.Idx = idx;
                        break;
                    }

                    idx0 = idx;
                    idx += di;
                    if (idx >= npts) idx -= npts;
                }
            }

            if (edges < 0) break;

            if (y >= 0)
            {
                var (left, right) = edge[0].X > edge[1].X ? (1, 0) : (0, 1);
                int xx1 = (int)((edge[left].X + delta1) >> XyShift);
                int xx2 = (int)((edge[right].X + delta2) >> XyShift);
                if (xx2 >= 0 && xx1 < mask.W)
                {
                    xx1 = Math.Max(xx1, 0);
                    xx2 = Math.Min(xx2, mask.W - 1);
                    if (xx1 <= xx2)
                    {
                        int start = y * mask.W + xx1;
                        int end = y * mask.W + xx2;
                        for (int i = start; i <= end; i++) mask.Data[i] = 255;
                    }
                }
            }

            edge[0].X += edge[0].Dx;
            edge[1].X += edge[1].Dx;
            y += 1;
            if (y > ymax) break;
        }
    }

    private static void FillCircle(MaskGrid mask, (int X, int Y) center, int radius)
    {
        if (radius < 0) return;
        long err = 0;
        long dx = radius;
        long dy = 0;
        long plus = 1;
        long minus = ((long)radius << 1) - 1;
        long width = mask.W;
        long height = mask.H;
        while (dx >= dy)
        {
            long y11 = center.Y - dy;
            long y12 = center.Y + dy;
            long y21 = center.Y - dx;
            long y22 = center.Y + dx;
            long x11 = Math.Max(center.X - dx, 0);
            long x12 = Math.Min(center.X + dx, width - 1);
            long x21 = Math.Max(center.X - dy, 0);
            long x22 = Math.Min(center.X + dy, width - 1);
            HLine(mask, y11, x11, x12, width, height);
            HLine(mask, y12, x11, x12, width, height);
            HLine(mask, y21, x21, x22, width, height);
            HLine(mask, y22, x21, x22, width, height);
            dy += 1;
            err += plus;
            plus += 2;
            if (err > 0)
            {
                err -= minus;
                dx -= 1;
                minus -= 2;
            }
        }
    }

    private static void HLine(MaskGrid mask, long y, long x1, long x2, long width, long height)
    {
        if (y < 0 || y >= height || x2 < 0 || x1 >= width || x1 > x2) return;
        int start = (int)(y * width + x1);
        int end = (int)(y * width + x2);
        for (int i = start; i <= end; i++) mask.Data[i] = 255;
    }

    private static void Put(MaskGrid mask, int x, int y)
    {
        if (x >= 0 && y >= 0 && x < mask.W && y < mask.H) mask.Set(x, y, 255);
    }
}
