using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace LTools.Features.CapturePro.ImageEdit;

/// <summary>
/// 图片编辑窗口的内容真源：一张底图 + 标注列表 + 撤销栈。
/// </summary>
/// <remarks>
/// <para>
/// 绘图、命中、导出全部走 <see cref="CaptureDocument"/>（与截图覆盖层同一套画布与渲染器），
/// 这里只补两件覆盖层没有的事：**换底图**（裁剪）与**整表恢复标注**（撤销）。
/// </para>
/// <para>
/// 撤销栈是「底图 + 标注」整份快照（照上游把裁剪也并进 <c>hostHistoryState</c> 的做法）：
/// 底图引用共享，只有裁剪会新建一张，所以一次撤销只多存一个引用。
/// 「有没有改过」按内容指纹比（底图实例 + 标注实例），保存 / 导出后重新记一次基准。
/// </para>
/// </remarks>
internal sealed class ImageEditDocument
{
    /// <summary>撤销栈保留的快照份数。</summary>
    public const int MaxUndo = 50;

    private sealed record Snapshot(BitmapSource Image, Mark[] Marks);

    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    private CaptureDocument? _doc;
    private string _saved = "";

    /// <summary>底图 + 标注（没有图时为 null）。</summary>
    public CaptureDocument? Doc => _doc;

    /// <summary>当前底图（没有图时为 null）。</summary>
    public BitmapSource? Image => _doc?.Base;

    public bool HasImage => _doc != null;

    /// <summary>打开时的文件名（显示在标题旁边）。</summary>
    public string FileName { get; private set; } = "";

    /// <summary>打开时的来源路径（空 = 剪贴板 / 采集历史 / 贴图记录）。</summary>
    public string? SourcePath { get; private set; }

    /// <summary>标题：文件名，没有就用「未命名」。</summary>
    public string Title => string.IsNullOrWhiteSpace(FileName) ? "未命名" : FileName;

    public int Width => Image?.PixelWidth ?? 0;
    public int Height => Image?.PixelHeight ?? 0;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>相对上次保存 / 导出有没有改过（裁剪、标注、换图都算）。</summary>
    public bool Dirty => HasImage && Signature() != _saved;

    /// <summary>
    /// 装一张新图（打开文件 / 粘贴 / 从采集历史或贴图记录打开）。
    /// 标注、撤销栈、来源一起重置。
    /// </summary>
    public void Load(BitmapSource image, string fileName = "", string? sourcePath = null)
    {
        if (image == null) throw new ArgumentNullException(nameof(image));

        var area = new Int32Rect(0, 0, Math.Max(1, image.PixelWidth), Math.Max(1, image.PixelHeight));
        _doc = new CaptureDocument(image, area, 1);
        _doc.Area = area;
        _doc.Style = SelectionStyle.None;

        _undo.Clear();
        _redo.Clear();
        FileName = fileName ?? "";
        SourcePath = sourcePath;
        _saved = Signature();
    }

    /// <summary>清空（关掉图片回到空态）。</summary>
    public void Clear()
    {
        _doc = null;
        _undo.Clear();
        _redo.Clear();
        FileName = "";
        SourcePath = null;
        _saved = "";
    }

    /// <summary>改之前记一份（标注手势、样式改动、粘贴替换都先调它）。</summary>
    public void PushUndo()
    {
        if (_doc == null) return;

        _undo.Add(new Snapshot(_doc.Base, _doc.Marks.ToArray()));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_doc == null || _undo.Count == 0) return false;

        _redo.Add(new Snapshot(_doc.Base, _doc.Marks.ToArray()));
        var snapshot = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Apply(snapshot);
        return true;
    }

    public bool Redo()
    {
        if (_doc == null || _redo.Count == 0) return false;

        _undo.Add(new Snapshot(_doc.Base, _doc.Marks.ToArray()));
        var snapshot = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Apply(snapshot);
        return true;
    }

    /// <summary>把一份快照放回来（底图 + 标注；撤销栈不动）。</summary>
    private void Apply(Snapshot snapshot)
    {
        if (_doc == null) return;

        _doc.SwapBase(snapshot.Image);
        _doc.ResetMarks(snapshot.Marks);
        _doc.Area = new Int32Rect(0, 0, Math.Max(1, snapshot.Image.PixelWidth),
            Math.Max(1, snapshot.Image.PixelHeight));
    }

    /// <summary>
    /// 剪掉一条行 / 列条带（<paramref name="first"/> / <paramref name="last"/> 是拖动起止像素，
    /// 顺序随便）。返回 false = 没剪（长度 0 / 整张 / 没有图）。
    /// </summary>
    public bool Crop(CropAxis axis, int first, int last)
    {
        if (_doc == null) return false;

        var image = _doc.Base;
        int extent = axis == CropAxis.Rows ? image.PixelHeight : image.PixelWidth;
        var span = ImageEditGeometry.Resolve(first, last, extent);
        if (!span.IsValid(extent)) return false;

        var cropped = ImageEditBitmap.RemoveStrip(image, axis, span);
        if (ReferenceEquals(cropped, image)) return false;

        PushUndo();
        _doc.SwapBase(cropped);
        _doc.Area = new Int32Rect(0, 0, cropped.PixelWidth, cropped.PixelHeight);
        return true;
    }

    /// <summary>
    /// 导出成品（底图 + 标注**重绘**，不是截屏再裁）。只在 UI 线程调用。
    /// </summary>
    public BitmapSource Render()
    {
        if (_doc == null) throw new InvalidOperationException("还没有图片，不能导出");
        return _doc.Render();
    }

    /// <summary>保存 / 导出成功了：把当前内容记成基准（之后不再算「改过」）。</summary>
    public void MarkSaved() => _saved = Signature();

    /// <summary>内容指纹（底图实例 + 标注实例序列）。</summary>
    private string Signature()
    {
        var doc = _doc;
        if (doc == null) return "";

        var marks = string.Join(",", doc.Marks.Select(mark => RuntimeHelpers.GetHashCode(mark).ToString()));
        return $"{RuntimeHelpers.GetHashCode(doc.Base)}:{doc.Base.PixelWidth}x{doc.Base.PixelHeight}|{marks}";
    }
}
