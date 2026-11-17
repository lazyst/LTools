using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro.Stitch;

/// <summary>拼接的分支：往下接 / 往上补 / 落在已有范围内 / 没动。</summary>
internal enum StitchBranch
{
    /// <summary>滚出了底部新内容，画布变长。</summary>
    Append,

    /// <summary>滚回了顶部之上，画布在前面变长。</summary>
    Prepend,

    /// <summary>只是往回滚了一点，内容已经在画布里。</summary>
    Contained,

    /// <summary>画面没动。</summary>
    Skip,

    /// <summary>位移被拒绝（置信度不够 / 超出一屏）。</summary>
    NoMovement,
}

/// <summary>视口在画布里的位置：<see cref="Position"/> 是顶边，画布长 = 一屏 + <see cref="MaxPosition"/>。</summary>
/// <remarks>移植自 <c>snow-stitch-images/src/state.rs</c>。</remarks>
internal readonly struct ViewportState
{
    public ViewportState(long position, long maxPosition)
    {
        Position = position;
        MaxPosition = maxPosition;
    }

    /// <summary>视口顶边在画布里的位置。</summary>
    public long Position { get; }

    /// <summary>滚到过的最大位置（= 画布比一屏多出来的长度）。</summary>
    public long MaxPosition { get; }

    /// <summary>画布沿拼接轴的总长。</summary>
    public int CanvasExtent(int viewportExtent) => checked(viewportExtent + (int)MaxPosition);

    /// <summary>
    /// 把一次位移换算成新的视口状态。
    /// </summary>
    /// <param name="offset">
    /// 运动估计给出的位移（沿轴，负数 = 内容朝上走，也就是「往下滚」）。
    /// 注意候选位置是 <c>position - offset</c>。
    /// </param>
    public ViewportTransition Transition(int offset)
    {
        long candidate = Position - offset;
        if (candidate < 0)
        {
            long growth = -candidate;
            return new ViewportTransition(
                this, candidate,
                new ViewportState(0, checked(MaxPosition + growth)),
                StitchBranch.Prepend,
                checked((int)growth));
        }

        if (candidate > MaxPosition)
        {
            long growth = candidate - MaxPosition;
            return new ViewportTransition(
                this, candidate,
                new ViewportState(candidate, candidate),
                StitchBranch.Append,
                checked((int)growth));
        }

        return new ViewportTransition(this, candidate, new ViewportState(candidate, MaxPosition), StitchBranch.Contained, 0);
    }
}

internal readonly struct ViewportTransition
{
    public ViewportTransition(ViewportState previous, long candidatePosition, ViewportState next, StitchBranch branch, int growth)
    {
        Previous = previous;
        CandidatePosition = candidatePosition;
        Next = next;
        Branch = branch;
        Growth = growth;
    }

    public ViewportState Previous { get; }

    public long CandidatePosition { get; }

    public ViewportState Next { get; }

    public StitchBranch Branch { get; }

    /// <summary>这一帧让画布变长了多少。</summary>
    public int Growth { get; }
}

/// <summary>
/// 分块画布：沿轴按 <see cref="TileSpan"/> 一段存，接 / 补 / 裁都只动头尾那几块。
/// </summary>
/// <remarks>
/// <para>
/// 移植自 <c>snow-stitch-images/src/tiled_canvas.rs</c>。原实现用共享的 <c>Arc</c> 块做零拷贝快照
/// （给预览只读一份），LTools 的预览是每帧单独重画的，不需要快照共享，所以这里只保留
/// 「分块 + 接 / 补 / 截断 / 取一段 / 整张物化」这几件事。
/// </para>
/// <para>
/// 块内永远是**行主序的小图**：纵向时宽 = 画布宽、高 = 块内行数；横向时宽 = 块内列数、高 = 画布高。
/// 这样把一帧的若干行 / 若干列写进块，和把块写成位图走的是同一套排布。
/// </para>
/// </remarks>
internal sealed class TiledCanvas
{
    /// <summary>一块存多少个沿轴单位（行或列）。</summary>
    public const int TileSpan = 256;

    private readonly StitchAxis _axis;
    private readonly int _cross;
    private readonly List<Block> _blocks = new();

    private TiledCanvas(StitchAxis axis, int cross)
    {
        _axis = axis;
        _cross = cross;
    }

    public StitchAxis Axis => _axis;

    /// <summary>垂直于拼接方向的长度（纵向 = 宽，横向 = 高）。</summary>
    public int Cross => _cross;

    /// <summary>沿拼接方向已存的单位数（纵向 = 行数，横向 = 列数）。</summary>
    public int Extent { get; private set; }

    /// <summary>成品输出宽。</summary>
    public int OutputWidth => _axis == StitchAxis.Vertical ? _cross : Extent;

    /// <summary>成品输出高。</summary>
    public int OutputHeight => _axis == StitchAxis.Vertical ? Extent : _cross;

    /// <summary>每单位（一行或一列）的字节数。</summary>
    private int UnitBytes => _cross * StitchFrame.Channels;

    public static TiledCanvas FromFrame(StitchFrame frame, StitchAxis axis)
    {
        int cross = axis == StitchAxis.Vertical ? frame.Width : frame.Height;
        var canvas = new TiledCanvas(axis, cross);
        canvas.AppendAxis(frame, 0, frame.AxisExtent(axis));
        return canvas;
    }

    /// <summary>接上 <paramref name="frame"/> 的 [<paramref name="start"/>, <paramref name="end"/>) 这一段。</summary>
    /// <remarks>
    /// 尾块没写满就先补齐（滚动时每帧只接几十行，每次都新开一块会留下一大片空容量）。
    /// </remarks>
    public void AppendAxis(StitchFrame frame, int start, int end)
    {
        ValidateRange(frame, start, end);
        int source = start;
        int remaining = end - start;
        while (remaining > 0)
        {
            Block? last = _blocks.Count > 0 ? _blocks[^1] : null;
            if (last != null && last.Units < TileSpan)
            {
                int take = Math.Min(TileSpan - last.Units, remaining);
                WriteBlock(last, last.Units, frame, source, take);
                last.Units += take;
                source += take;
                remaining -= take;
                Extent += take;
                continue;
            }

            int create = Math.Min(TileSpan, remaining);
            var block = new Block(new byte[(long)TileSpan * UnitBytes], create);
            WriteBlock(block, 0, frame, source, create);
            _blocks.Add(block);
            source += create;
            remaining -= create;
            Extent += create;
        }
    }

    /// <summary>在前面补上 <paramref name="frame"/> 的 [<paramref name="start"/>, <paramref name="end"/>) 这一段。</summary>
    /// <remarks>头块没写满就先把它已有的内容往后挪，再写进新的这一段（同样避免空容量越滚越多）。</remarks>
    public void PrependAxis(StitchFrame frame, int start, int end)
    {
        ValidateRange(frame, start, end);
        int source = start;
        int remaining = end - start;
        while (remaining > 0)
        {
            Block? first = _blocks.Count > 0 ? _blocks[0] : null;
            if (first != null && first.Units < TileSpan)
            {
                int take = Math.Min(TileSpan - first.Units, remaining);
                ShiftRight(first, take);
                WriteBlock(first, 0, frame, source, take);
                first.Units += take;
                source += take;
                remaining -= take;
                Extent += take;
                continue;
            }

            int create = Math.Min(TileSpan, remaining);
            var block = new Block(new byte[(long)TileSpan * UnitBytes], create);
            WriteBlock(block, 0, frame, source, create);
            _blocks.Insert(0, block);
            source += create;
            remaining -= create;
            Extent += create;
        }
    }

    /// <summary>从尾部截到 <paramref name="newExtent"/>。</summary>
    public void TruncateEnd(int newExtent)
    {
        if (newExtent < 0 || newExtent > Extent)
            throw new ArgumentOutOfRangeException(nameof(newExtent), "截断长度超出画布范围");

        while (Extent > newExtent && _blocks.Count > 0)
        {
            Block last = _blocks[^1];
            int excess = Extent - newExtent;
            if (excess >= last.Units)
            {
                _blocks.RemoveAt(_blocks.Count - 1);
                Extent -= last.Units;
                continue;
            }

            last.Units -= excess;
            Extent -= excess;
        }
    }

    /// <summary>从头部丢掉 <paramref name="span"/> 个单位。</summary>
    public void TruncateStart(int span)
    {
        if (span < 0 || span > Extent)
            throw new ArgumentOutOfRangeException(nameof(span), "截断长度超出画布范围");

        int remaining = span;
        while (remaining > 0 && _blocks.Count > 0)
        {
            Block first = _blocks[0];
            if (remaining >= first.Units)
            {
                _blocks.RemoveAt(0);
                remaining -= first.Units;
                Extent -= first.Units;
                continue;
            }

            DropFront(first, remaining);
            Extent -= remaining;
            remaining = 0;
        }
    }

    /// <summary>把画布的 [<paramref name="start"/>, <paramref name="end"/>) 物化成一帧（沿轴长度 = end-start）。</summary>
    public StitchFrame MaterializeAxis(int start, int end)
    {
        if (start < 0 || end < start || end > Extent)
            throw new ArgumentOutOfRangeException(nameof(start), "读取范围超出画布");

        int span = end - start;
        int width = _axis == StitchAxis.Vertical ? _cross : span;
        int height = _axis == StitchAxis.Vertical ? span : _cross;
        var frame = StitchFrame.Create(width, height);

        int dest = 0;
        int cursor = 0;
        foreach (Block block in _blocks)
        {
            int blockStart = cursor;
            int blockEnd = cursor + block.Units;
            cursor = blockEnd;
            if (blockEnd <= start) continue;
            if (blockStart >= end) break;

            int from = Math.Max(start, blockStart) - blockStart;
            int to = Math.Min(end, blockEnd) - blockStart;
            CopyBlockToFrame(block, from, to - from, frame, dest);
            dest += to - from;
        }

        return frame;
    }

    /// <summary>整张画布物化成一帧。</summary>
    public StitchFrame Materialize() => MaterializeAxis(0, Extent);

    /// <summary>把画布的 [<paramref name="start"/>, <paramref name="end"/>) 拷进 <paramref name="target"/> 的开头（不新建帧）。</summary>
    public void CopyAxisTo(int start, int end, StitchFrame target)
    {
        if (start < 0 || end < start || end > Extent)
            throw new ArgumentOutOfRangeException(nameof(start), "读取范围超出画布");
        int targetCross = _axis == StitchAxis.Vertical ? target.Width : target.Height;
        if (targetCross != _cross)
            throw new ArgumentException("目标帧宽度和画布不一致", nameof(target));
        if (end - start > target.AxisExtent(_axis))
            throw new ArgumentOutOfRangeException(nameof(end), "目标帧放不下这一段");

        int dest = 0;
        int cursor = 0;
        foreach (Block block in _blocks)
        {
            int blockStart = cursor;
            int blockEnd = cursor + block.Units;
            cursor = blockEnd;
            if (blockEnd <= start) continue;
            if (blockStart >= end) break;

            int from = Math.Max(start, blockStart) - blockStart;
            int count = Math.Min(end, blockEnd) - blockStart - from;
            CopyBlockToFrame(block, from, count, target, dest);
            dest += count;
        }
    }

    /// <summary>把画布的第 <paramref name="index"/> 条线（一行或一列）拷进 <paramref name="destination"/>。</summary>
    public void CopyLine(int index, byte[] destination)
    {
        if (index < 0 || index >= Extent)
            throw new ArgumentOutOfRangeException(nameof(index), "线号超出画布");
        int unitBytes = UnitBytes;
        if (destination.Length < unitBytes)
            throw new ArgumentException("目标缓冲区太小", nameof(destination));

        int cursor = 0;
        foreach (Block block in _blocks)
        {
            int blockStart = cursor;
            cursor += block.Units;
            if (index >= cursor) continue;

            int offset = index - blockStart;
            if (_axis == StitchAxis.Vertical)
            {
                Buffer.BlockCopy(block.Data, offset * unitBytes, destination, 0, unitBytes);
                return;
            }

            for (int y = 0; y < _cross; y++)
            {
                Buffer.BlockCopy(
                    block.Data, (y * TileSpan + offset) * StitchFrame.Channels,
                    destination, y * StitchFrame.Channels,
                    StitchFrame.Channels);
            }

            return;
        }
    }

    /// <summary>把一块里已有的内容整体往后挪 <paramref name="units"/> 个单位（前面要补内容时用）。</summary>
    private void ShiftRight(Block block, int units)
    {
        if (units <= 0 || block.Units <= 0) return;
        int unitBytes = UnitBytes;
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(block.Data, 0, block.Data, units * unitBytes, block.Units * unitBytes);
            return;
        }

        int pixels = block.Units * StitchFrame.Channels;
        for (int y = 0; y < _cross; y++)
        {
            Buffer.BlockCopy(
                block.Data, y * TileSpan * StitchFrame.Channels,
                block.Data, (y * TileSpan + units) * StitchFrame.Channels,
                pixels);
        }
    }

    private void ValidateRange(StitchFrame frame, int start, int end)
    {
        int frameCross = _axis == StitchAxis.Vertical ? frame.Width : frame.Height;
        if (frameCross != _cross)
            throw new ArgumentException("帧尺寸和画布不一致", nameof(frame));
        if (start < 0 || end < start || end > frame.AxisExtent(_axis))
            throw new ArgumentOutOfRangeException(nameof(end), "写入范围超出这一帧");
    }

    /// <summary>把一帧的 [start, start+count) 写进块的第 <paramref name="blockUnit"/> 个单位起。</summary>
    private void WriteBlock(Block block, int blockUnit, StitchFrame frame, int start, int count)
    {
        if (count <= 0) return;
        int unitBytes = UnitBytes;
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(frame.Pixels, start * unitBytes, block.Data, blockUnit * unitBytes, count * unitBytes);
            return;
        }

        int pixels = count * StitchFrame.Channels;
        for (int y = 0; y < _cross; y++)
        {
            Buffer.BlockCopy(
                frame.Pixels, (y * frame.Width + start) * StitchFrame.Channels,
                block.Data, (y * TileSpan + blockUnit) * StitchFrame.Channels,
                pixels);
        }
    }

    /// <summary>把块的第 <paramref name="from"/> 个单位起的 <paramref name="count"/> 个写进目标帧（从第 <paramref name="destUnit"/> 个单位起）。</summary>
    private void CopyBlockToFrame(Block block, int from, int count, StitchFrame target, int destUnit)
    {
        if (count <= 0) return;
        int unitBytes = UnitBytes;
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(block.Data, from * unitBytes, target.Pixels, destUnit * unitBytes, count * unitBytes);
            return;
        }

        int pixels = count * StitchFrame.Channels;
        for (int y = 0; y < _cross; y++)
        {
            Buffer.BlockCopy(
                block.Data, (y * TileSpan + from) * StitchFrame.Channels,
                target.Pixels, (y * target.Width + destUnit) * StitchFrame.Channels,
                pixels);
        }
    }

    private void DropFront(Block block, int units)
    {
        int unitBytes = UnitBytes;
        int remaining = block.Units - units;
        var data = new byte[(long)TileSpan * unitBytes];
        if (_axis == StitchAxis.Vertical)
        {
            Buffer.BlockCopy(block.Data, units * unitBytes, data, 0, remaining * unitBytes);
        }
        else
        {
            int pixels = remaining * StitchFrame.Channels;
            for (int y = 0; y < _cross; y++)
            {
                Buffer.BlockCopy(
                    block.Data, (y * TileSpan + units) * StitchFrame.Channels,
                    data, (y * TileSpan) * StitchFrame.Channels,
                    pixels);
            }
        }

        block.Data = data;
        block.Units = remaining;
    }

    private sealed class Block
    {
        public Block(byte[] data, int units)
        {
            Data = data;
            Units = units;
        }

        public byte[] Data { get; set; }

        public int Units { get; set; }
    }
}
