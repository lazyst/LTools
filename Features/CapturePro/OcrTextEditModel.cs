using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>
/// 识别文字层的编辑模型：逐行文字 + 字符级选区 + 撤销 / 重做。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshotocrtexteditingsession.cpp</c> 的语义：**选区跨行**（起点 / 终点各是
/// 「第几行第几个字符」），复制 / 剪切 / 粘贴 / 删除 / 全选都作用在选区上；双击一行是选中整行
/// （对应上游「点一行进整行编辑」的入口）。
/// </para>
/// <para>
/// 纯数据、无 WPF：撤销栈存**整份行文本快照**（几十行、每行几十字，代价可以忽略），
/// 不搞增量 diff——增量一旦算错就是「撤销后文字错位」这种最难查的问题。
/// </para>
/// </remarks>
internal sealed class OcrTextEditModel
{
    private const int UndoLimit = 50;

    private readonly List<string> _lines = new();
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    /// <summary>文字或选区变了（界面据此重画）。</summary>
    public event Action? Changed;

    public IReadOnlyList<string> Lines => _lines;

    public int LineCount => _lines.Count;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public int AnchorLine { get; private set; }

    public int AnchorChar { get; private set; }

    public int CaretLine { get; private set; }

    public int CaretChar { get; private set; }

    /// <summary>有非空选区。</summary>
    public bool HasSelection
    {
        get
        {
            var (sl, sc, el, ec) = Normalized();
            return sl != el || sc != ec;
        }
    }

    /// <summary>全部文字（换行连接）。</summary>
    public string AllText => string.Join("\n", _lines);

    /// <summary>灌入一批行文本（重置撤销栈）。</summary>
    public void Load(IEnumerable<string> lines)
    {
        _lines.Clear();
        foreach (string line in lines) _lines.Add(line ?? "");
        if (_lines.Count == 0) _lines.Add("");

        _undo.Clear();
        _redo.Clear();
        AnchorLine = CaretLine = 0;
        AnchorChar = CaretChar = 0;
        Changed?.Invoke();
    }

    /// <summary>把光标放到某处（丢弃选区）。</summary>
    public void SetCaret(int line, int ch)
    {
        (line, ch) = Clamp(line, ch);
        AnchorLine = CaretLine = line;
        AnchorChar = CaretChar = ch;
        Changed?.Invoke();
    }

    /// <summary>拖动选区：不动起点，只挪终点。</summary>
    public void ExtendCaret(int line, int ch)
    {
        (line, ch) = Clamp(line, ch);
        CaretLine = line;
        CaretChar = ch;
        Changed?.Invoke();
    }

    /// <summary>选中一行（双击该行）。</summary>
    public void SelectLine(int line)
    {
        line = Math.Clamp(line, 0, _lines.Count - 1);
        AnchorLine = CaretLine = line;
        AnchorChar = 0;
        CaretChar = _lines[line].Length;
        Changed?.Invoke();
    }

    /// <summary>全选。</summary>
    public void SelectAll()
    {
        AnchorLine = 0;
        AnchorChar = 0;
        CaretLine = _lines.Count - 1;
        CaretChar = _lines[^1].Length;
        Changed?.Invoke();
    }

    /// <summary>清掉选区（保留光标位置）。</summary>
    public void ClearSelection()
    {
        AnchorLine = CaretLine;
        AnchorChar = CaretChar;
        Changed?.Invoke();
    }

    /// <summary>选区的文字；没有选区返回空串（调用方自己回落「整份文字」）。</summary>
    public string SelectionText()
    {
        var (sl, sc, el, ec) = Normalized();
        if (sl == el) return sc == ec ? "" : _lines[sl][sc..ec];
        if (sl >= _lines.Count || el >= _lines.Count) return "";

        var parts = new List<string>(el - sl + 1)
        {
            _lines[sl][sc..],
        };

        for (int i = sl + 1; i < el; i++) parts.Add(_lines[i]);
        parts.Add(_lines[el][..ec]);
        return string.Join("\n", parts);
    }

    /// <summary>删掉选区；没有选区返回 false（调用方自己处理退格）。</summary>
    public bool DeleteSelection()
    {
        var (sl, sc, el, ec) = Normalized();
        if (sl == el && sc == ec) return false;

        PushUndo();
        string head = _lines[sl][..sc];
        string tail = _lines[el][ec..];
        _lines.RemoveRange(sl, el - sl + 1);
        _lines.Insert(sl, head + tail);
        AnchorLine = CaretLine = sl;
        AnchorChar = CaretChar = sc;
        Changed?.Invoke();
        return true;
    }

    /// <summary>在选区处插入文字（替换选区）；换行会拆成多行。</summary>
    public void InsertText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        PushUndo();
        var (sl, sc, el, ec) = Normalized();
        string head = _lines[sl][..sc];
        string tail = _lines[el][ec..];

        string[] parts = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _lines.RemoveRange(sl, el - sl + 1);

        if (parts.Length == 1)
        {
            _lines.Insert(sl, head + parts[0] + tail);
            AnchorLine = CaretLine = sl;
            AnchorChar = CaretChar = head.Length + parts[0].Length;
        }
        else
        {
            var inserted = new List<string>(parts.Length);
            inserted.Add(head + parts[0]);
            for (int i = 1; i < parts.Length - 1; i++) inserted.Add(parts[i]);
            inserted.Add(parts[^1] + tail);
            _lines.InsertRange(sl, inserted);

            AnchorLine = CaretLine = sl + parts.Length - 1;
            AnchorChar = CaretChar = parts[^1].Length;
        }

        Changed?.Invoke();
    }

    /// <summary>整行替换（界面上「双击整行改写」用），不改选区。</summary>
    public void ReplaceLine(int index, string text)
    {
        if (index < 0 || index >= _lines.Count) return;
        if (_lines[index] == text) return;

        PushUndo();
        _lines[index] = text ?? "";
        CaretLine = index;
        CaretChar = Math.Min(CaretChar, _lines[index].Length);
        AnchorLine = CaretLine;
        AnchorChar = CaretChar;
        Changed?.Invoke();
    }

    /// <summary>删除选区，没有选区时删光标前一个字符 / 吞掉换行。</summary>
    public void Backspace()
    {
        if (DeleteSelection()) return;

        if (CaretChar > 0)
        {
            PushUndo();
            _lines[CaretLine] = _lines[CaretLine].Remove(CaretChar - 1, 1);
            AnchorChar = CaretChar = CaretChar - 1;
        }
        else if (CaretLine > 0)
        {
            PushUndo();
            int previous = CaretLine - 1;
            int join = _lines[previous].Length;
            _lines[previous] += _lines[CaretLine];
            _lines.RemoveAt(CaretLine);
            AnchorLine = CaretLine = previous;
            AnchorChar = CaretChar = join;
        }
        else
        {
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>删光标后一个字符 / 合并下一行（Delete 键）。</summary>
    public void DeleteForward()
    {
        if (DeleteSelection()) return;

        if (CaretChar < _lines[CaretLine].Length)
        {
            PushUndo();
            _lines[CaretLine] = _lines[CaretLine].Remove(CaretChar, 1);
        }
        else if (CaretLine < _lines.Count - 1)
        {
            PushUndo();
            _lines[CaretLine] += _lines[CaretLine + 1];
            _lines.RemoveAt(CaretLine + 1);
        }
        else
        {
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>光标左右移动（跨行）；<paramref name="extend"/> = 按住 Shift 拖选区。</summary>
    public void MoveCaret(int delta, bool extend)
    {
        int line = CaretLine, ch = CaretChar;

        if (delta < 0)
        {
            if (ch > 0) ch--;
            else if (line > 0)
            {
                line--;
                ch = _lines[line].Length;
            }
        }
        else if (delta > 0)
        {
            if (ch < _lines[line].Length) ch++;
            else if (line < _lines.Count - 1)
            {
                line++;
                ch = 0;
            }
        }

        if (extend) ExtendCaret(line, ch);
        else SetCaret(line, ch);
    }

    /// <summary>上下移动一行（记住目标行的列号）。</summary>
    public void MoveLine(int delta, bool extend)
    {
        int line = Math.Clamp(CaretLine + delta, 0, _lines.Count - 1);
        int ch = Math.Min(CaretChar, _lines[line].Length);
        if (extend) ExtendCaret(line, ch);
        else SetCaret(line, ch);
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;

        _redo.Add(Capture());
        if (_redo.Count > UndoLimit) _redo.RemoveAt(0);

        Apply(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;

        _undo.Add(Capture());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);

        Apply(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        Changed?.Invoke();
        return true;
    }

    // ============ 内部 ============

    private readonly record struct Snapshot(string[] Lines, int AnchorLine, int AnchorChar,
        int CaretLine, int CaretChar);

    private Snapshot Capture() => new(_lines.ToArray(), AnchorLine, AnchorChar, CaretLine,
        CaretChar);

    private void Apply(Snapshot snapshot)
    {
        _lines.Clear();
        _lines.AddRange(snapshot.Lines);
        if (_lines.Count == 0) _lines.Add("");

        AnchorLine = snapshot.AnchorLine;
        AnchorChar = snapshot.AnchorChar;
        CaretLine = snapshot.CaretLine;
        CaretChar = snapshot.CaretChar;
        ClampSelection();
    }

    private void PushUndo()
    {
        _undo.Add(Capture());
        if (_undo.Count > UndoLimit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void ClampSelection()
    {
        AnchorLine = Math.Clamp(AnchorLine, 0, _lines.Count - 1);
        CaretLine = Math.Clamp(CaretLine, 0, _lines.Count - 1);
        AnchorChar = Math.Clamp(AnchorChar, 0, _lines[AnchorLine].Length);
        CaretChar = Math.Clamp(CaretChar, 0, _lines[CaretLine].Length);
    }

    private (int StartLine, int StartChar, int EndLine, int EndChar) Normalized()
    {
        ClampSelection();
        return AnchorLine < CaretLine || (AnchorLine == CaretLine && AnchorChar <= CaretChar)
            ? (AnchorLine, AnchorChar, CaretLine, CaretChar)
            : (CaretLine, CaretChar, AnchorLine, AnchorChar);
    }

    private (int Line, int Char) Clamp(int line, int ch)
    {
        line = Math.Clamp(line, 0, _lines.Count - 1);
        return (line, Math.Clamp(ch, 0, _lines[line].Length));
    }
}
