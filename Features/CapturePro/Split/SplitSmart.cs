using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 智能切分：先在等分位置附近找「内容最平」的那一列 / 一行，再落成切分线。照上游
/// <c>prepareImageSplitLayout</c> / <c>imagesplitsmartlayout.cpp</c>。
/// </summary>
/// <remarks>
/// <para>
/// 找线的分数只看**相邻像素差**的投影：把每条候选轴的「水平邻居差之和」（横向切）或
/// 「垂直邻居差之和」（纵向切）算出来，切分线落在邻居差最小的地方——也就是穿过一行 / 一列
/// 纯色内容时最不容易切断东西。
/// </para>
/// <para>
/// 每条线只在**自己的活动域**里找（等分位置 ±21 像素，且左右各让给对方的中点），所以拖不
/// 出「跨过相邻那条线」的乱序结果；域退化成一个点（图太窄）时直接落在标称位置。
/// </para>
/// <para>
/// 计分是字典序：先比「切分线两侧的邻居差」（越小越好），再比「左右各两像素的总差」（越小
/// 越好，避免切在细线上），再比离标称位置的距离，最后比坐标本身（保证确定性）。
/// </para>
/// </remarks>
internal static class SplitSmart
{
    private const int Radius = 21;
    private const int MaximumAxisBatch = 4096;

    /// <summary>
    /// 算一份布局：非智能 / 宫格 = 标称布局；智能 = 标称布局 + 找线。取消时返回 null
    /// （调用方按「这次结果过期」处理，不当作失败）。
    /// </summary>
    public static SplitPrepared? Prepare(BitmapSource image, SplitSettings settings,
        Func<bool>? cancelled = null)
    {
        if (cancelled?.Invoke() == true) return null;

        var layout = SplitLayoutCalculator.Calculate(image.PixelWidth, image.PixelHeight, settings);
        if (!layout.IsValid || settings.Type != SplitType.Smart ||
            settings.Direction == SplitDirection.Grid)
        {
            return new SplitPrepared(settings, layout);
        }

        var source = Source(image);
        try
        {
            if (!Refine(source, settings, layout, cancelled) || cancelled?.Invoke() == true)
                return null;
        }
        catch (Exception)
        {
            // 上游对内存不足的处理：清空两条轴、给一条「线太多」的错，界面照常可用
            layout.Vertical.ReplaceWith(Array.Empty<int>());
            layout.Horizontal.ReplaceWith(Array.Empty<int>());
            layout.Errors["image"] = "切分线太多，内存不够";
        }

        return new SplitPrepared(settings, layout);
    }

    /// <summary>统一成预乘 BGRA：投影只该看「看得见的颜色」（照上游 <c>pixelAt</c>）。</summary>
    private static BitmapSource Source(BitmapSource image)
    {
        if (image.Format == PixelFormats.Pbgra32) return image;
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    /// <summary>把标称线逐条挪到域内最平的位置；返回 false = 中途被取消。</summary>
    private static bool Refine(BitmapSource image, SplitSettings settings, SplitLayout layout,
        Func<bool>? cancelled)
    {
        bool horizontal = settings.Direction == SplitDirection.Horizontal;
        int dimension = horizontal ? image.PixelHeight : image.PixelWidth;
        int length = horizontal ? image.PixelWidth : image.PixelHeight;
        var lines = horizontal ? layout.Horizontal : layout.Vertical;

        if (lines.IsEmpty || length == 1 || settings.Count == dimension) return true;

        int section = dimension / settings.Count;
        int extra = dimension % settings.Count;

        int Nominal(int index)
        {
            long part = index + 1;
            return (int)(part * section + Math.Min(part, extra));
        }

        (int Nominal, int First, int Last) Domain(int index)
        {
            int center = Nominal(index);
            int first = (int)Math.Max(
                Math.Max(1L, (long)center - Radius),
                index == 0 ? 1L : ((long)Nominal(index - 1) + center) / 2 + 1);
            int last = (int)Math.Min(
                Math.Min((long)dimension - 1, (long)center + Radius),
                index + 1 == settings.Count - 1
                    ? (long)dimension - 1
                    : ((long)center + Nominal(index + 1)) / 2);
            return (center, first, last);
        }

        var projection = new Projection(image, horizontal, cancelled);
        var result = new List<int>(Math.Max(0, settings.Count - 1));
        var domains = new List<(int Nominal, int First, int Last)>();

        // 段宽只剩 1 像素后，剩下的线全是「标称位置」——直接按等差数列补齐，不必逐条算
        int movable = section == 1 ? extra : settings.Count - 1;
        int index = 0;
        while (index < movable)
        {
            if (cancelled?.Invoke() == true) return false;

            var initial = Domain(index);
            if (initial.First == initial.Last)
            {
                result.Add(initial.Nominal);
                index++;
                continue;
            }

            int first = Math.Max(0, initial.First - 2);
            int last = Math.Min(dimension - 1, initial.Last + 1);
            domains.Clear();
            domains.Add(initial);
            index++;

            // 合并活动域互不重叠、总量有限的相邻线，一次投影算多条（上游的批次上限 4096 轴）
            while (index < movable)
            {
                var next = Domain(index);
                int nextLast = Math.Min(dimension - 1, next.Last + 1);
                if (next.First == next.Last || (long)next.First - 2 > (long)last + 1 ||
                    (long)nextLast - first + 1 > MaximumAxisBatch)
                {
                    break;
                }

                domains.Add(next);
                last = nextLast;
                index++;
            }

            if (!projection.Calculate(first, last)) return false;

            foreach (var candidate in domains) result.Add(Score(projection, candidate));
        }

        // 末尾那批「段宽 1 像素」的标称线（从 movable 起，逐 1 递增）
        for (int i = movable; i < settings.Count - 1; i++) result.Add(Nominal(i));

        result.RemoveAll(coordinate => coordinate <= 0 || coordinate >= dimension);
        if (result.Count == 0) return true;
        result.Sort();

        lines.ReplaceWith(result);
        return true;
    }

    /// <summary>在候选域里挑一条线：字典序 (两侧最小差, 四邻和, 离标称距离, 坐标)。</summary>
    private static int Score(Projection projection, (int Nominal, int First, int Last) candidate)
    {
        // 照上游：标称位置本身就是第一个候选，**即使它落在域外**也保留（域被挤扁时会出现）
        int best = candidate.Nominal;
        var bestScore = ScoreAt(projection, candidate, best);

        for (int boundary = candidate.First; boundary <= candidate.Last; boundary++)
        {
            var score = ScoreAt(projection, candidate, boundary);
            if (Compare(score, bestScore) < 0)
            {
                best = boundary;
                bestScore = score;
            }
        }

        return best;
    }

    private static (ulong Edge, ulong Neighbours, int Distance, int Coordinate) ScoreAt(
        Projection projection, (int Nominal, int First, int Last) candidate, int boundary)
    {
        ulong before = projection.At(boundary - 1);
        ulong after = projection.At(boundary);
        return (Math.Min(before, after),
            projection.At(boundary - 2) + before + after + projection.At(boundary + 1),
            Math.Abs(boundary - candidate.Nominal),
            boundary);
    }

    private static int Compare((ulong Edge, ulong Neighbours, int Distance, int Coordinate) a,
        (ulong Edge, ulong Neighbours, int Distance, int Coordinate) b)
    {
        int result = a.Edge.CompareTo(b.Edge);
        if (result != 0) return result;
        result = a.Neighbours.CompareTo(b.Neighbours);
        if (result != 0) return result;
        result = a.Distance.CompareTo(b.Distance);
        return result != 0 ? result : a.Coordinate.CompareTo(b.Coordinate);
    }

    /// <summary>
    /// 一条轴上的「邻居差之和」。横向切（<c>horizontal = true</c>）按行累加水平邻居差，
    /// 纵向切按列累加垂直邻居差；只算 <c>first..last</c> 这几条轴。
    /// </summary>
    private sealed class Projection
    {
        private readonly BitmapSource _image;
        private readonly bool _horizontal;
        private readonly Func<bool>? _cancelled;
        private readonly int _dimension;
        private readonly int _length;
        private byte[] _buffer = Array.Empty<byte>();
        private ulong[] _values = Array.Empty<ulong>();
        private int _first;

        public Projection(BitmapSource image, bool horizontal, Func<bool>? cancelled)
        {
            _image = image;
            _horizontal = horizontal;
            _cancelled = cancelled;
            _dimension = horizontal ? image.PixelHeight : image.PixelWidth;
            _length = horizontal ? image.PixelWidth : image.PixelHeight;
        }

        public bool Calculate(int first, int last)
        {
            _first = first;
            int count = last - first + 1;
            if (_values.Length < count) _values = new ulong[count];
            Array.Clear(_values, 0, count);

            // 每批最多 128K 像素（512KB）：长截图也不会一次申请几十兆
            int axisStep = Math.Max(1, Math.Min(_dimension, 131072 / Math.Max(1, _length)));

            for (int axis = first; axis <= last; axis += axisStep)
            {
                if (_cancelled?.Invoke() == true) return false;

                int axisCount = Math.Min(axisStep, last - axis + 1);
                int stride = (_horizontal ? _length : axisCount) * 4;
                int bytes = stride * (_horizontal ? axisCount : _length);
                if (_buffer.Length < bytes) _buffer = new byte[bytes];

                var region = _horizontal
                    ? new Int32Rect(0, axis, _length, axisCount)
                    : new Int32Rect(axis, 0, axisCount, _length);
                _image.CopyPixels(region, _buffer, stride, 0);

                if (_horizontal) AccumulateRows(axis - first, axisCount, stride);
                else AccumulateColumns(axis - first, axisCount, stride);
            }

            return true;
        }

        /// <summary>
        /// 取某条轴的投影值。越界按边缘算（上游 <c>at</c> 的 clamp）：切分线在 1 与
        /// dimension-1 上时，四邻和会摸到 0 与末像素。
        /// </summary>
        public ulong At(int coordinate) =>
            _values[Math.Clamp(coordinate, 0, _dimension - 1) - _first];

        private void AccumulateRows(int offset, int axisCount, int stride)
        {
            var buffer = _buffer.AsSpan();
            for (int row = 0; row < axisCount; row++)
            {
                int start = row * stride;
                ulong sum = 0;
                int previous = start;
                for (int column = 1; column < _length; column++)
                {
                    int current = start + column * 4;
                    sum += Difference(buffer, previous, current);
                    previous = current;
                }

                _values[offset + row] += sum;
            }
        }

        private void AccumulateColumns(int offset, int axisCount, int stride)
        {
            var buffer = _buffer.AsSpan();
            for (int row = 1; row < _length; row++)
            {
                int current = row * stride;
                int previous = current - stride;
                for (int column = 0; column < axisCount; column++)
                {
                    int a = current + column * 4;
                    int b = previous + column * 4;
                    _values[offset + column] += Difference(buffer, b, a);
                }
            }
        }

        /// <summary>四个通道差的最大值（预乘 BGRA：B / G / R / A）。</summary>
        private static ulong Difference(ReadOnlySpan<byte> buffer, int a, int b)
        {
            uint blue = (uint)Math.Abs(buffer[a] - buffer[b]);
            uint green = (uint)Math.Abs(buffer[a + 1] - buffer[b + 1]);
            uint red = (uint)Math.Abs(buffer[a + 2] - buffer[b + 2]);
            uint alpha = (uint)Math.Abs(buffer[a + 3] - buffer[b + 3]);
            return Math.Max(Math.Max(blue, green), Math.Max(red, alpha));
        }
    }
}
