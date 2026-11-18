using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro.Split;

/// <summary>
/// 一条轴上排好序、互不重复的切分线（像素坐标，取值范围 <c>1..dimension-1</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游用「平衡树 + 等差数列游程」存百万级的线；本项目不做百万级宫格，用排好序的
/// <see cref="List{T}"/> 就够，但**语义完全一致**：坐标唯一、从 0 起算、切分线不属于任何一侧
/// （区域是半开区间，见 <see cref="SplitLayout.BaseRegionAt"/>）。
/// </para>
/// <para>
/// 唯一性由 <see cref="TryInsert"/> 保证：切分线落在同一像素上会让预览 / 导出出现 0 宽区域。
/// </para>
/// </remarks>
internal sealed class SplitLines
{
    private readonly List<int> _values;

    public SplitLines() => _values = new List<int>();

    public SplitLines(IEnumerable<int> values)
    {
        _values = new List<int>(values);
        _values.Sort();
    }

    public int Count => _values.Count;

    public bool IsEmpty => _values.Count == 0;

    public int this[int index] => _values[index];

    public IReadOnlyList<int> Values => _values;

    /// <summary>第一个 <c>&gt;= coordinate</c> 的下标；没有就是 <see cref="Count"/>。</summary>
    public int LowerBound(int coordinate)
    {
        int low = 0, high = _values.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_values[mid] < coordinate) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    /// <summary>第一个 <c>&gt; coordinate</c> 的下标。</summary>
    public int UpperBound(int coordinate)
    {
        int low = 0, high = _values.Count;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_values[mid] <= coordinate) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    public bool Contains(int coordinate)
    {
        int index = LowerBound(coordinate);
        return index < _values.Count && _values[index] == coordinate;
    }

    /// <summary>追加一条线（调用方保证坐标递增且不重复）；生成布局与撤销恢复用。</summary>
    public void Append(int coordinate) => _values.Add(coordinate);

    /// <summary>按顺序插一条线；同一坐标已存在时返回 false（照上游 <c>createLine</c>）。</summary>
    public bool TryInsert(int coordinate)
    {
        int index = LowerBound(coordinate);
        if (index < _values.Count && _values[index] == coordinate) return false;
        _values.Insert(index, coordinate);
        return true;
    }

    public bool TryRemoveAt(int index)
    {
        if (index < 0 || index >= _values.Count) return false;
        _values.RemoveAt(index);
        return true;
    }

    /// <summary>拖线落位：先删旧、再按新坐标插到正确的位置。</summary>
    public bool TryMove(int index, int coordinate)
    {
        if (index < 0 || index >= _values.Count) return false;
        if (_values[index] == coordinate) return false;
        int existing = LowerBound(coordinate);
        if (existing < _values.Count && _values[existing] == coordinate) return false;

        _values.RemoveAt(index);
        int insertion = LowerBound(coordinate);
        _values.Insert(insertion, coordinate);
        return true;
    }

    public void Clear() => _values.Clear();

    public void ReplaceWith(IEnumerable<int> values)
    {
        _values.Clear();
        _values.AddRange(values);
        _values.Sort();
    }

    /// <summary>
    /// 能不能拿来切：维度 &gt; 0、线数 &lt; 维度（每张至少 1 像素）、
    /// 且每条线都严格落在 <c>(0, dimension)</c> 里。
    /// </summary>
    public bool IsValid(int dimension)
    {
        if (dimension <= 0 || _values.Count >= dimension) return false;
        if (_values.Count == 0) return true;
        if (_values[0] <= 0 || _values[^1] >= dimension) return false;
        for (int i = 1; i < _values.Count; i++)
        {
            if (_values[i] <= _values[i - 1]) return false;
        }
        return true;
    }

    public int[] ToArray() => _values.ToArray();

    public bool SequenceEqual(SplitLines? other)
    {
        if (other == null || other.Count != Count) return false;
        for (int i = 0; i < Count; i++)
        {
            if (_values[i] != other._values[i]) return false;
        }
        return true;
    }
}
