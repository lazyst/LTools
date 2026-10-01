using System.IO;

namespace LTools.Features;

/// <summary>速记单条值对象（标题存于文件名、正文存于文件内容、分类=子目录）。由 <see cref="NoteRepository"/> 读写。</summary>
internal sealed class NoteEntry
{
    /// <summary>绝对路径；新建/未保存时为空。</summary>
    public string Path { get; set; } = "";

    /// <summary>标题（不含日期前缀）。编辑框回显，保存时拼进文件名。</summary>
    public string Title { get; set; } = "";

    /// <summary>分类= <see cref="NoteRepository"/> 根目录下的一级子目录名。</summary>
    public string Category { get; set; } = "";

    /// <summary>文件最后修改时间。列表按此倒序。</summary>
    public DateTime Mtime { get; set; }

    /// <summary>正文（纯文本，UTF-8 无 BOM）。</summary>
    public string Body { get; set; } = "";

    /// <summary>是否尚未落盘（新建或原文件已删）。</summary>
    public bool IsNew => string.IsNullOrEmpty(Path) || !File.Exists(Path);
}
