using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 速记的图片附件：粘贴 / 拖入的图落到笔记旁边的 <c>笔记名.files/</c>，正文里插相对路径。
/// </summary>
/// <remarks>
/// <para>
/// 目录跟笔记同目录同名（<c>2026-11-22 标题.md</c> ↔ <c>2026-11-22 标题.files/</c>）：
/// 笔记改名 / 换分类时目录跟着搬家，正文里的 <c>…files/</c> 路径一起改写（<see cref="ReconcileAttachments"/>）；
/// 删除笔记时目录由 <see cref="NoteRepository.Delete"/> 一并删掉。备份只要带上 <c>速记/</c> 就够。
/// </para>
/// <para>
/// 还没保存过的新笔记先放在速记根下的 <c>.draft-时间戳.files</c>（点前缀目录不算分类），
/// 第一次保存时再搬成正式名字；超过 7 天没保存的草稿夹在启动时清掉。
/// </para>
/// </remarks>
public partial class QuickNoteWindow
{
    /// <summary>当前这条速记的附件目录（绝对路径）；没有图片时为 null。</summary>
    private string? _attachmentDir;

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    // ============ 粘贴 / 拖入 ============

    /// <summary>剪贴板里有图就落盘并插入链接。返回是否接管了这次 Ctrl+V。</summary>
    private bool PasteImageFromClipboard()
    {
        bool hasImage;
        try { hasImage = Clipboard.ContainsImage(); }
        catch { return false; }
        if (!hasImage) return false;

        BitmapSource? image;
        try { image = Clipboard.GetImage(); }
        catch { image = null; }
        if (image == null) return false;

        try
        {
            InsertImageLink(SaveClipboardImage(image));
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(this, "粘贴图片", "图片没能存下来：" + ex.Message);
        }
        return true;
    }

    private string SaveClipboardImage(BitmapSource image)
    {
        string dir = EnsureAttachmentFolder();
        string name = "pasted-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        string path = UniquePath(dir, name);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private void BodyBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasImageFiles(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void BodyBox_PreviewDrop(object sender, DragEventArgs e)
    {
        if (!HasImageFiles(e.Data)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        foreach (string file in files.Where(IsImageFile))
        {
            try
            {
                string dir = EnsureAttachmentFolder();
                string target = UniquePath(dir, Path.GetFileName(file));
                File.Copy(file, target);
                InsertImageLink(target);
            }
            catch (Exception ex)
            {
                ConfirmDialog.Info(this, "插入图片", "「" + Path.GetFileName(file) + "」没能复制：" + ex.Message);
            }
        }
        e.Handled = true;
    }

    private static bool HasImageFiles(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop)
           && data.GetData(DataFormats.FileDrop) is string[] files
           && files.Any(IsImageFile);

    private static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ============ 附件目录 ============

    /// <summary>取（必要时建）当前笔记的附件目录；未保存的笔记先落在 <c>.draft-…files</c>。</summary>
    private string EnsureAttachmentFolder()
    {
        if (_attachmentDir != null && Directory.Exists(_attachmentDir)) return _attachmentDir;

        string? path = _current?.Path;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            string dir = NoteRepository.AttachmentsDirFor(path);
            Directory.CreateDirectory(dir);
            _attachmentDir = dir;
            return dir;
        }

        string draft = Path.Combine(AppPaths.NotesDir,
            ".draft-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".files");
        Directory.CreateDirectory(draft);
        _attachmentDir = draft;
        return draft;
    }

    /// <summary>这条笔记已有的附件目录（没有就是 null）。</summary>
    private static string? AttachmentFolderOf(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        string dir = NoteRepository.AttachmentsDirFor(path);
        return Directory.Exists(dir) ? dir : null;
    }

    private void InsertImageLink(string filePath)
    {
        string dirName = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
        string fileName = Path.GetFileName(filePath);
        string destination = dirName + "/" + fileName;
        // 笔记名里常有空格 / 括号：CommonMark 要求这种地址用尖括号包起来，否则 Markdig 与别的
        // Markdown 阅读器都会在空格处截断（正文里也仍然可读）
        if (destination.Any(c => c is ' ' or '(' or ')'))
            destination = "<" + destination + ">";
        string link = "![" + Path.GetFileNameWithoutExtension(fileName) + "](" + destination + ")";

        var document = BodyBox.Document;
        int offset = BodyBox.CaretOffset;
        var line = document.GetLineByOffset(offset);
        bool onEmptyLine = document.GetText(line).Trim().Length == 0;
        string prefix = onEmptyLine ? "" : (offset == line.Offset ? "\n" : "\n\n");
        string insert = prefix + link + "\n";

        document.Insert(offset, insert);
        BodyBox.CaretOffset = offset + insert.Length;
        BodyBox.Focus();
    }

    /// <summary>笔记改名 / 换分类后把附件夹搬过去，并改写正文里的相对路径。返回正文是否变了。</summary>
    private bool ReconcileAttachments(string newNotePath, out string updatedBody)
    {
        updatedBody = BodyBox.Text;
        if (string.IsNullOrEmpty(_attachmentDir) || !Directory.Exists(_attachmentDir)) return false;

        string target = NoteRepository.AttachmentsDirFor(newNotePath);
        if (string.Equals(_attachmentDir, target, StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            target = UniqueDirectory(target);
            Directory.Move(_attachmentDir, target);
        }
        catch (Exception ex)
        {
            CrashLog.Write("速记附件目录迁移失败: " + _attachmentDir, ex);
            return false;
        }

        string oldName = Path.GetFileName(_attachmentDir);
        string newName = Path.GetFileName(target);
        _attachmentDir = target;

        string body = BodyBox.Text;
        string replaced = body.Replace(oldName + "/", newName + "/", StringComparison.Ordinal);
        if (replaced == body) return false;

        updatedBody = replaced;
        return true;
    }

    private static string UniquePath(string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(dir, stem + " (" + i + ")" + ext);
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private static string UniqueDirectory(string path)
    {
        if (!Directory.Exists(path)) return path;
        for (int i = 2; i < 1000; i++)
        {
            string candidate = path + " (" + i + ")";
            if (!Directory.Exists(candidate)) return candidate;
        }
        return path;
    }
}
