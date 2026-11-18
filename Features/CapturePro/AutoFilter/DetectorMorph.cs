using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LTools.Features.CapturePro.AutoFilter;

/// <summary>形态学操作（对应上游 <c>MorphOp</c>）。</summary>
internal enum MorphOp
{
    Dilate,
    Open,
    Close,
    Gradient,
}

/// <summary>核形状（对应上游 <c>KernelShape</c>）。</summary>
internal enum KernelShape
{
    Rect,
    Ellipse,
}

/// <summary>一个连通域（对应上游 <c>Component</c>：矩形 + 面积 + 1 起的 id）。</summary>
internal readonly record struct Component(RRect Rect, int Area, int Id);

/// <summary>
/// 形态学 / 连通域 / 行模糊 / 行中值 / 中值模糊，逐行照抄上游
/// <c>backend/pure_rust/morph.rs</c> 与 <c>median.rs</c>（对齐 OpenCV 的语义）。
/// </summary>
internal static class Morphology
{
    public static MaskGrid Apply(MaskGrid input, MorphOp op, int width, int height, KernelShape shape)
    {
        if (shape == KernelShape.Rect)
        {
            switch (op)
            {
                case MorphOp.Dilate:
                    return MorphRect(input, true, width, height);
                case MorphOp.Open:
                    return MorphRect(MorphRect(input, false, width, height), true, width, height);
                case MorphOp.Close:
                    return MorphRect(MorphRect(input, true, width, height), false, width, height);
                default:
                    return Subtract(MorphRect(input, true, width, height), MorphRect(input, false, width, height));
            }
        }

        var offsets = KernelOffsets(width, height, shape);
        switch (op)
        {
            case MorphOp.Dilate:
                return MorphOnce(input, offsets, true);
            case MorphOp.Open:
                return MorphOnce(MorphOnce(input, offsets, false), offsets, true);
            case MorphOp.Close:
                return MorphOnce(MorphOnce(input, offsets, true), offsets, false);
            default:
                return Subtract(MorphOnce(input, offsets, true), MorphOnce(input, offsets, false));
        }
    }

    public static MaskGrid Dilate(MaskGrid input, int kernel) =>
        Apply(input, MorphOp.Dilate, kernel, kernel, KernelShape.Rect);

    public static MaskGrid Erode(MaskGrid input, int kernel) =>
        MorphRect(input, false, Math.Max(1, kernel), Math.Max(1, kernel));

    public static MaskGrid Open(MaskGrid input, int width, int height) =>
        Apply(input, MorphOp.Open, width, height, KernelShape.Rect);

    public static MaskGrid Close(MaskGrid input, int width, int height) =>
        Apply(input, MorphOp.Close, width, height, KernelShape.Rect);

    public static MaskGrid Gradient(MaskGrid input, int width, int height) =>
        Apply(input, MorphOp.Gradient, width, height, KernelShape.Rect);

    private static MaskGrid Subtract(MaskGrid dilated, MaskGrid eroded)
    {
        var output = new MaskGrid(dilated.W, dilated.H);
        for (int i = 0; i < output.Data.Length; i++)
            output.Data[i] = (byte)Math.Max(0, dilated.Data[i] - eroded.Data[i]);
        return output;
    }

    /// <summary>
    /// 核偏移：anchor = size/2（OpenCV 的 <c>normalizeAnchor</c>，偶数核偏向右下）。
    /// 椭圆核逐行算半宽，与上游 <c>kernel_offsets</c> 一致。
    /// </summary>
    private static List<(int Dx, int Dy)> KernelOffsets(int width, int height, KernelShape shape)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        int anchorX = width / 2;
        int anchorY = height / 2;
        var offsets = new List<(int, int)>(width * height);
        for (int ky = 0; ky < height; ky++)
        {
            int xStart, xEnd;
            if (shape == KernelShape.Rect || (width == 1 && height == 1))
            {
                xStart = 0;
                xEnd = width;
            }
            else
            {
                int radiusY = height / 2;
                int radiusX = width / 2;
                int dy = ky - radiusY;
                if (Math.Abs(dy) > radiusY)
                {
                    xStart = 0;
                    xEnd = 0;
                }
                else
                {
                    double normalized = radiusY == 0
                        ? 0.0
                        : (double)(radiusY * radiusY - dy * dy) / (radiusY * radiusY);
                    int dx = (int)Math.Round(radiusX * Math.Sqrt(normalized), MidpointRounding.ToEven);
                    xStart = Math.Max(radiusX - dx, 0);
                    xEnd = Math.Min(radiusX + dx + 1, width);
                }
            }

            for (int kx = xStart; kx < xEnd; kx++)
                offsets.Add((kx - anchorX, ky - anchorY));
        }

        if (offsets.Count == 0) offsets.Add((0, 0));
        return offsets;
    }

    private static MaskGrid MorphOnce(MaskGrid input, List<(int Dx, int Dy)> offsets, bool dilating)
    {
        var output = new MaskGrid(input.W, input.H);
        for (int y = 0; y < input.H; y++)
            for (int x = 0; x < input.W; x++)
            {
                byte value = dilating ? (byte)0 : (byte)255;
                foreach (var (dx, dy) in offsets)
                {
                    int sx = x + dx;
                    int sy = y + dy;
                    byte sample = sx < 0 || sx >= input.W || sy < 0 || sy >= input.H
                        ? (dilating ? (byte)0 : (byte)255)
                        : input.At(sx, sy);
                    value = dilating ? Math.Max(value, sample) : Math.Min(value, sample);
                }

                output.Set(x, y, value);
            }

        return output;
    }

    /// <summary>可分离矩形核：先横一遍、转置、竖一遍、再转置回来。</summary>
    private static MaskGrid MorphRect(MaskGrid input, bool dilating, int kernelWidth, int kernelHeight)
    {
        kernelWidth = Math.Max(1, kernelWidth);
        kernelHeight = Math.Max(1, kernelHeight);
        if (input.W == 0 || input.H == 0 || (kernelWidth == 1 && kernelHeight == 1)) return input.Clone();

        byte pad = dilating ? (byte)0 : (byte)255;
        var work = input.Clone();
        if (kernelWidth > 1) FilterRows(work, kernelWidth, kernelWidth / 2, pad, dilating);
        if (kernelHeight > 1)
        {
            work = Transpose(work);
            FilterRows(work, kernelHeight, kernelHeight / 2, pad, dilating);
            work = Transpose(work);
        }

        return work;
    }

    private static MaskGrid Transpose(MaskGrid input)
    {
        var output = new MaskGrid(input.H, input.W);
        for (int y = 0; y < input.H; y++)
            for (int x = 0; x < input.W; x++)
                output.Set(y, x, input.At(x, y));
        return output;
    }

    /// <summary>逐行滑动 min/max（行之间互不影响，按行并行）。</summary>
    private static void FilterRows(MaskGrid mask, int kernel, int anchor, byte pad, bool dilating)
    {
        int width = mask.W;
        int height = mask.H;
        if (width == 0) return;
        int workers = height < 64 ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 8);
        int chunkRows = (height + workers - 1) / Math.Max(1, workers);

        if (workers == 1 || chunkRows < 1)
        {
            var rowOut = new byte[width];
            var deque = new int[kernel + 2];
            for (int y = 0; y < height; y++)
            {
                int offset = y * width;
                SlidingMinMax(mask.Data, offset, width, kernel, anchor, pad, dilating, rowOut, deque);
                Array.Copy(rowOut, 0, mask.Data, offset, width);
            }

            return;
        }

        int bands = (height + chunkRows - 1) / chunkRows;
        Parallel.For(0, bands, band =>
        {
            int y0 = band * chunkRows;
            int y1 = Math.Min(height, y0 + chunkRows);
            var rowOut = new byte[width];
            var deque = new int[kernel + 2];
            for (int y = y0; y < y1; y++)
            {
                int offset = y * width;
                SlidingMinMax(mask.Data, offset, width, kernel, anchor, pad, dilating, rowOut, deque);
                Array.Copy(rowOut, 0, mask.Data, offset, width);
            }
        });
    }

    private static void SlidingMinMax(byte[] src, int offset, int n, int kernel, int anchor, byte pad,
        bool dilating, byte[] output, int[] deque)
    {
        int cap = deque.Length;
        int head = 0;
        int count = 0;
        int right = kernel - anchor - 1;
        int start = -anchor;
        int end = n - 1 + right;

        byte Sample(int index) => index < 0 || index >= n ? pad : src[offset + index];

        for (int index = start; index <= end; index++)
        {
            byte value = Sample(index);
            while (count > 0)
            {
                byte back = Sample(deque[(head + count - 1) % cap]);
                if (!(dilating ? value >= back : value <= back)) break;
                count--;
            }

            deque[(head + count) % cap] = index;
            count++;

            int outX = index - right;
            if (outX < 0 || outX >= n) continue;
            int windowStart = outX - anchor;
            while (count > 0 && deque[head] < windowStart)
            {
                head = (head + 1) % cap;
                count--;
            }

            output[outX] = Sample(deque[head]);
        }
    }

    /// <summary>
    /// 8 连通域（上游只用左上 / 上 / 右上 / 左四个已扫过的邻居做 union-find），
    /// id 按光栅序首次出现顺序 1 起编号。
    /// </summary>
    public static (int[] Labels, Component[] Components) ConnectedComponents(MaskGrid input)
    {
        int n = input.W * input.H;
        if (n == 0) return (Array.Empty<int>(), Array.Empty<Component>());

        var provisional = new int[n];
        var parent = new List<int> { 0 };
        var neighborLabels = new int[4];
        int[] dxs = { -1, 0, 1, -1 };
        int[] dys = { -1, -1, -1, 0 };
        int nextLabel = 1;

        for (int y = 0; y < input.H; y++)
            for (int x = 0; x < input.W; x++)
            {
                if (input.At(x, y) == 0) continue;
                int count = 0;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + dxs[d];
                    int ny = y + dys[d];
                    if (nx < 0 || nx >= input.W || ny < 0 || ny >= input.H) continue;
                    int label = provisional[ny * input.W + nx];
                    if (label == 0) continue;
                    neighborLabels[count++] = label;
                }

                if (count == 0)
                {
                    parent.Add(nextLabel);
                    provisional[y * input.W + x] = nextLabel;
                    nextLabel++;
                    continue;
                }

                int chosen = neighborLabels[0];
                for (int i = 1; i < count; i++) chosen = Math.Min(chosen, neighborLabels[i]);
                provisional[y * input.W + x] = chosen;
                for (int i = 0; i < count; i++) Union(parent, chosen, neighborLabels[i]);
            }

        var rootToId = new int[parent.Count];
        var labels = new int[n];
        var stats = new List<(int X0, int Y0, int X1, int Y1, int Area)>();
        int id = 0;
        for (int y = 0; y < input.H; y++)
            for (int x = 0; x < input.W; x++)
            {
                int index = y * input.W + x;
                int label = provisional[index];
                if (label == 0) continue;
                int root = Find(parent, label);
                int mapped;
                if (rootToId[root] == 0)
                {
                    id++;
                    rootToId[root] = id;
                    stats.Add((x, y, x, y, 0));
                    mapped = id;
                }
                else
                {
                    mapped = rootToId[root];
                }

                labels[index] = mapped;
                var stat = stats[mapped - 1];
                stat.X0 = Math.Min(stat.X0, x);
                stat.Y0 = Math.Min(stat.Y0, y);
                stat.X1 = Math.Max(stat.X1, x);
                stat.Y1 = Math.Max(stat.Y1, y);
                stat.Area += 1;
                stats[mapped - 1] = stat;
            }

        var components = new Component[stats.Count];
        for (int i = 0; i < stats.Count; i++)
        {
            var (x0, y0, x1, y1, area) = stats[i];
            components[i] = new Component(new RRect(x0, y0, x1 - x0 + 1, y1 - y0 + 1), area, i + 1);
        }

        return (labels, components);
    }

    private static int Find(List<int> parent, int label)
    {
        int root = label;
        while (parent[root] != root) root = parent[root];
        while (parent[label] != root)
        {
            int next = parent[label];
            parent[label] = root;
            label = next;
        }

        return root;
    }

    private static void Union(List<int> parent, int a, int b)
    {
        a = Find(parent, a);
        b = Find(parent, b);
        if (a == b) return;
        if (a < b) parent[b] = a;
        else parent[a] = b;
    }

    private static int ClampIndex(int index, int length) => Math.Clamp(index, 0, length - 1);

    /// <summary>OpenCV 的 <c>BORDER_REFLECT_101</c>。</summary>
    private static int Reflect101(int index, int length)
    {
        if (length <= 1) return 0;
        while (index < 0 || index >= length)
            index = index < 0 ? -index : 2 * length - index - 2;
        return index;
    }

    /// <summary>盒式模糊（单行，<c>reflect_101</c> 边界）。</summary>
    public static float[] BlurRow(float[] input, int kernel)
    {
        kernel = Math.Max(1, kernel);
        if (input.Length == 0 || kernel == 1) return (float[])input.Clone();

        int n = input.Length;
        int anchor = kernel / 2;
        int right = kernel - anchor - 1;
        float Sample(int index) => input[Reflect101(index, n)];
        float sum = 0;
        for (int i = -anchor; i <= right; i++) sum += Sample(i);

        var output = new float[n];
        output[0] = sum / kernel;
        for (int x = 1; x < n; x++)
        {
            sum += Sample(x + right) - Sample(x - 1 - anchor);
            output[x] = sum / kernel;
        }

        return output;
    }

    /// <summary>单行中值（clamp 边界，直方图取 rank = kernel/2）。</summary>
    public static byte[] MedianRow(byte[] input, int kernel)
    {
        if (kernel % 2 == 0) kernel += 1;
        kernel = Math.Max(1, kernel);
        if (input.Length == 0 || kernel == 1) return (byte[])input.Clone();

        int n = input.Length;
        int anchor = (kernel - 1) / 2;
        int right = kernel - anchor - 1;
        var histogram = new int[256];
        byte Sample(int index) => input[ClampIndex(index, n)];
        for (int offset = -anchor; offset <= right; offset++) histogram[Sample(offset)]++;

        int rank = kernel / 2;
        var output = new byte[n];
        output[0] = HistogramMedian(histogram, rank);
        for (int x = 1; x < n; x++)
        {
            histogram[Sample(x - 1 - anchor)]--;
            histogram[Sample(x + right)]++;
            output[x] = HistogramMedian(histogram, rank);
        }

        return output;
    }

    private static byte HistogramMedian(int[] histogram, int rank)
    {
        int remaining = rank;
        for (int value = 0; value < histogram.Length; value++)
        {
            if (remaining < histogram[value]) return (byte)value;
            remaining -= histogram[value];
        }

        return 255;
    }

    /// <summary>
    /// 8 位三通道中值模糊：左右 replicate 补边后走 OpenCV <c>medianBlur_8u_O1</c> 的
    /// 列条带 + 两级直方图（每个像素 O(1)），逐行与上游 <c>median.rs</c> 对齐。
    /// </summary>
    public static BgrImg MedianBlurImage(BgrImg input, int kernel)
    {
        if (kernel % 2 == 0) kernel += 1;
        kernel = Math.Max(1, kernel);
        if (kernel == 1 || input.W == 0 || input.H == 0) return input.Clone();

        int radius = (kernel - 1) / 2;
        var padded = new BgrImg(input.W + 2 * radius, input.H);
        for (int y = 0; y < input.H; y++)
            for (int x = 0; x < padded.W; x++)
            {
                int srcX = Math.Clamp(x - radius, 0, input.W - 1);
                int si = (y * input.W + srcX) * 3;
                int di = (y * padded.W + x) * 3;
                padded.Data[di] = input.Data[si];
                padded.Data[di + 1] = input.Data[si + 1];
                padded.Data[di + 2] = input.Data[si + 2];
            }

        var output = new BgrImg(input.W, input.H);
        MedianBlur8uO1(padded, output, radius);
        return output;
    }

    private static void MedianBlur8uO1(BgrImg padded, BgrImg output, int radius)
    {
        int width = output.W;
        int height = output.H;
        if (width == 0 || height == 0) return;

        int stripe = Math.Max(1, Math.Min(512 / 3, width));
        var ranges = new List<(int X0, int StripeW)>();
        for (int x0 = 0; x0 < width; x0 += stripe)
            ranges.Add((x0, Math.Min(width - x0, stripe)));

        if (ranges.Count == 1 || height < 48)
        {
            foreach (var (x0, stripeW) in ranges)
                FillStripe(padded, output.Data, x0, stripeW, radius, height, width, x0);
            return;
        }

        var bands = new byte[ranges.Count][];
        Parallel.For(0, ranges.Count, i =>
        {
            var (x0, stripeW) = ranges[i];
            bands[i] = new byte[height * stripeW * 3];
            FillStripe(padded, bands[i], x0, stripeW, radius, height, stripeW, 0);
        });

        for (int i = 0; i < ranges.Count; i++)
        {
            var (x0, stripeW) = ranges[i];
            var band = bands[i];
            for (int y = 0; y < height; y++)
                Array.Copy(band, y * stripeW * 3, output.Data, (y * width + x0) * 3, stripeW * 3);
        }
    }

    private static void FillStripe(BgrImg padded, byte[] dest, int x0, int stripeW, int radius, int height,
        int destStride, int destX0)
    {
        const int Channels = 3;
        int n = stripeW + 2 * radius;
        int rank = 2 * radius * radius + 2 * radius;
        int srcWidth = padded.W;
        var coarse = new ushort[16 * n * Channels];
        var fine = new ushort[16 * 16 * n * Channels];

        for (int channel = 0; channel < Channels; channel++)
        {
            for (int column = 0; column < n; column++)
                Hop(coarse, fine, n, channel, column, padded.Data[(x0 + column) * 3 + channel], radius + 2);

            for (int row = 1; row < radius; row++)
            {
                int y = Math.Min(row, Math.Max(height - 1, 0));
                int source = (y * srcWidth + x0) * 3;
                for (int column = 0; column < n; column++)
                    Hop(coarse, fine, n, channel, column, padded.Data[source + column * 3 + channel], 1);
            }
        }

        for (int row = 0; row < height; row++)
        {
            int oldY = Math.Max(0, row - (radius + 1));
            int newY = Math.Min(row + radius, height - 1);
            int oldBase = (oldY * srcWidth + x0) * 3;
            int newBase = (newY * srcWidth + x0) * 3;
            for (int channel = 0; channel < Channels; channel++)
            {
                for (int column = 0; column < n; column++)
                {
                    Hop(coarse, fine, n, channel, column, padded.Data[oldBase + column * 3 + channel], -1);
                    Hop(coarse, fine, n, channel, column, padded.Data[newBase + column * 3 + channel], 1);
                }

                var window = new int[16];
                var luc = new int[16];
                var fineWindow = new int[16, 16];
                for (int column = 0; column < 2 * radius; column++)
                {
                    int source = 16 * (n * channel + column);
                    for (int bin = 0; bin < 16; bin++) window[bin] += coarse[source + bin];
                }

                for (int local = 0; local < stripeW; local++)
                {
                    int column = local + radius;
                    int addAt = Math.Min(column + radius, n - 1);
                    int addSource = 16 * (n * channel + addAt);
                    for (int bin = 0; bin < 16; bin++) window[bin] += coarse[addSource + bin];

                    int sum = 0;
                    int bucket = 0;
                    while (bucket < 16)
                    {
                        sum += window[bucket];
                        if (sum > rank)
                        {
                            sum -= window[bucket];
                            break;
                        }

                        bucket += 1;
                    }

                    if (bucket < 16)
                        UpdateFineWindow(fine, fineWindow, luc, n, channel, bucket, column, radius);

                    int subAt = column - radius;
                    int subSource = 16 * (n * channel + subAt);
                    for (int bin = 0; bin < 16; bin++) window[bin] -= coarse[subSource + bin];

                    byte value = 255;
                    if (bucket < 16)
                    {
                        for (int fineBin = 0; fineBin < 16; fineBin++)
                        {
                            sum += fineWindow[bucket, fineBin];
                            if (sum <= rank) continue;
                            value = (byte)(bucket * 16 + fineBin);
                            break;
                        }
                    }

                    dest[((row * destStride + destX0 + local) * 3) + channel] = value;
                }
            }
        }
    }

    private static void Hop(ushort[] coarse, ushort[] fine, int n, int channel, int column, byte value, int delta)
    {
        int coarseBin = value >> 4;
        int fineBin = value & 0x0f;
        int coarseIndex = 16 * (n * channel + column) + coarseBin;
        int fineIndex = 16 * (n * (16 * channel + coarseBin) + column) + fineBin;
        if (delta >= 0)
        {
            coarse[coarseIndex] += (ushort)delta;
            fine[fineIndex] += (ushort)delta;
        }
        else
        {
            coarse[coarseIndex] -= (ushort)-delta;
            fine[fineIndex] -= (ushort)-delta;
        }
    }

    private static void UpdateFineWindow(ushort[] fine, int[,] fineWindow, int[] luc, int n, int channel, int bucket,
        int column, int radius)
    {
        if (luc[bucket] <= column - radius)
        {
            for (int bin = 0; bin < 16; bin++) fineWindow[bucket, bin] = 0;
            luc[bucket] = column - radius;
            while (luc[bucket] < Math.Min(column + radius + 1, n))
            {
                int source = 16 * (n * (16 * channel + bucket) + luc[bucket]);
                for (int bin = 0; bin < 16; bin++) fineWindow[bucket, bin] += fine[source + bin];
                luc[bucket] += 1;
            }

            if (luc[bucket] < column + radius + 1)
            {
                int extra = column + radius + 1 - n;
                int source = 16 * (n * (16 * channel + bucket) + (n - 1));
                for (int bin = 0; bin < 16; bin++) fineWindow[bucket, bin] += extra * fine[source + bin];
                luc[bucket] = column + radius + 1;
            }
        }
        else
        {
            int baseIndex = 16 * n * (16 * channel + bucket);
            while (luc[bucket] < column + radius + 1)
            {
                int addColumn = Math.Min(luc[bucket], n - 1);
                int subColumn = Math.Max(luc[bucket] - (2 * radius + 1), 0);
                int add = baseIndex + 16 * addColumn;
                int sub = baseIndex + 16 * subColumn;
                for (int bin = 0; bin < 16; bin++)
                    fineWindow[bucket, bin] = fineWindow[bucket, bin] + fine[add + bin] - fine[sub + bin];
                luc[bucket] += 1;
            }
        }
    }
}
