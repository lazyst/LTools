using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 拖放替换内容与「加载新内容」（照上游 <c>screenshotpinnedwindow.cpp</c> 的
/// <c>acceptsDrop</c> / <c>dragEnter</c> / <c>dropEvent</c> / <c>requestContentReplacement</c>
/// 与右键菜单的「加载新内容」子菜单）。
/// </summary>
/// <remarks>
/// <para>
/// 拖进来可以是图片文件、文本 / HTML / Markdown 文件、位图、或一段文字，顺序照上游：
/// 文件 → 位图 → 文本。判定（实时 / 穿透 / 正在拖出 / 有没有能贴的东西）全在
/// <see cref="PinDropRules.Check"/>，窗口几何在 <see cref="PinDropRules.Plan"/>——这里只负责取数据与落地。
/// </para>
/// <para>
/// 拖入时边框变强调色（上游 <c>setFileDragActive</c>），拖走变回原样。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    private bool _fileDragActive;

    /// <summary>拖入高亮开着没有（冒烟直接断言）。</summary>
    internal bool FileDragActive => _fileDragActive;

    /// <summary>构造函数里挂一次：打开拖放并接上四个事件。</summary>
    private void InitDrop()
    {
        AllowDrop = true;
        DragEnter += Window_DragEnter;
        DragOver += Window_DragOver;
        DragLeave += Window_DragLeave;
        Drop += Window_Drop;
    }

    // ============ 拖入 ============

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDropEffects(e);

    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropEffects(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        SetFileDragActive(false);
        e.Handled = true;
    }

    private void SetFileDragActive(bool on)
    {
        if (_fileDragActive == on) return;
        _fileDragActive = on;
        ApplyFrame();
        SyncShadow();
    }

    private void UpdateDropEffects(DragEventArgs e)
    {
        var verdict = VerdictOf(e.Data, out _);
        bool accept = verdict == PinDropVerdict.Accept;

        SetFileDragActive(accept);
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private PinDropVerdict VerdictOf(IDataObject data, out string[] files)
    {
        files = Array.Empty<string>();
        if (data.GetDataPresent(DataFormats.FileDrop))
            files = data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

        bool hasFiles = files.Length > 0;
        bool supported = hasFiles && files.Any(PinFiles.IsSupported);
        bool hasBitmap = data.GetDataPresent(DataFormats.Bitmap);
        bool hasText = data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text);

        return PinDropRules.Check(_live, _clickThrough, _exporting, hasFiles, hasBitmap, hasText,
            supported);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        SetFileDragActive(false);

        var verdict = VerdictOf(e.Data, out var files);
        e.Handled = true;
        if (verdict != PinDropVerdict.Accept) return;

        if (files.Length > 0)
        {
            var usable = PinFiles.Filter(files, out _);
            if (usable.Count > 0) LoadFile(usable[0]);   // 一次只换一个（上游也是换「这份内容」）
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.Bitmap) &&
            e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            if (bitmap.CanFreeze) bitmap.Freeze();
            ReplaceWithImage(bitmap);
            return;
        }

        if (e.Data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
            ReplaceWithText(text);
    }

    // ============ 加载新内容 ============

    /// <summary>右键菜单里的「加载新内容」（文件 / 剪贴板）。</summary>
    private void AddLoadMenu(ContextMenu menu)
    {
        var root = MenuFx.Submenu(menu, "加载新内容");
        root.IsEnabled = !_live;

        var file = new MenuItem { Header = "图片 / 文本文件…" };
        file.Click += (_, _) =>
        {
            string? path = PickFile();
            if (path != null) LoadFile(path);
        };
        root.Items.Add(file);

        var clipboard = new MenuItem { Header = "剪贴板" };
        clipboard.Click += (_, _) => LoadFromClipboard();
        root.Items.Add(clipboard);
    }

    /// <summary>选一个能贴的文件（贴图窗口不抢焦点，框不挂 Owner）。</summary>
    private static string? PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "加载新内容",
            Filter = PinFiles.DialogFilter,
            Multiselect = false,
            CheckFileExists = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>读一个文件换上去（解码丢后台：大图 + 慢盘会卡住 UI 线程与低级鼠标钩子）。</summary>
    private void LoadFile(string path)
    {
        Task.Run(() =>
        {
            var item = PinFiles.Decode(path);
            Ui.Post(() =>
            {
                if (!IsVisible) return;
                if (item == null)
                {
                    ToastService.Show($"读不出来：{System.IO.Path.GetFileName(path)}", "贴图", 3600,
                        ToastKind.warning, key: "pin-replace");
                    return;
                }

                if (item.Image != null) ReplaceWithImage(item.Image);
                else ReplaceWithText(item.Text);
            });
        });
    }

    /// <summary>
    /// 用剪贴板里的内容替换（照上游 <c>loadClipboardContent</c>）：文件 → 位图 → 文本。
    /// </summary>
    private void LoadFromClipboard()
    {
        if (NativeClipboard.TryGetFileList(out var files))
        {
            var usable = PinFiles.Filter(files, out _);
            if (usable.Count > 0)
            {
                LoadFile(usable[0]);
                return;
            }
        }

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            ReplaceWithImage(image);
            return;
        }

        if (NativeClipboard.TryGetText(out string text) && !string.IsNullOrWhiteSpace(text))
        {
            ReplaceWithText(text);
            return;
        }

        ToastService.Show("剪贴板里没有能贴的内容", "贴图", 2800, ToastKind.warning, key: "pin-replace");
    }

    // ============ 落地 ============

    private bool ReplaceWithImage(BitmapSource image) =>
        ApplyReplacement(PinContentKind.Image, image, "", 0, null, Array.Empty<PinCardCopy>());

    private bool ReplaceWithText(string text)
    {
        var style = PinTextStyle.Current();
        var card = PinCardRenderer.Render(text, style, _scale);
        int cardWidth = card.Kind == PinContentKind.Color
            ? Math.Max(1, (int)Math.Round(card.Logical.Width))
            : 0;

        return ApplyReplacement(card.Kind, card.Image, text, cardWidth, PinTextSnapshot.Of(style),
            card.Copies);    }

    /// <summary>
    /// 把新内容换上去：先按 <see cref="PinDropRules.Plan"/> 定几何（锁定且尺寸变了就拒），
    /// 再改内容 / 卡片热区 / 历史记录。返回换成功没有。
    /// </summary>
    private bool ApplyReplacement(PinContentKind kind, BitmapSource image, string text,
        int cardWidth, PinTextSnapshot? snapshot, IReadOnlyList<PinCardCopy> copies)
    {
        if (image == null || !IsVisible) return false;

        double baseWidth = Math.Max(1, image.PixelWidth / _scale);
        double baseHeight = Math.Max(1, image.PixelHeight / _scale);

        var plan = PinDropRules.Plan(baseWidth, baseHeight, _zoom, Left, Top, Width, Height,
            _locked, _viewport, _thumb, PinMath.VirtualDesktop);

        if (plan.Reject != PinReplaceReject.None)
        {
            ToastService.Show(PinDropRules.Describe(plan.Reject), "贴图", 4000, ToastKind.warning,
                key: "pin-replace");
            return false;
        }

        _content = image;
        Shot.Source = image;
        _baseWidth = plan.BaseWidth;
        _baseHeight = plan.BaseHeight;
        _cardKind = kind;
        _cardText = text ?? "";
        _copies = copies ?? Array.Empty<PinCardCopy>();

        if (!_thumb && !_viewport && plan.Resized)
        {
            Left = plan.Left;
            Top = plan.Top;
            Width = plan.Width;
            Height = plan.Height;
        }

        _pan = _viewport ? PinMath.CenterPan(ContentSize, new Size(Width, Height)) : default;

        ApplyContent();
        ApplyFrame();
        ApplyCursor();
        RefreshHoverChrome();

        // 历史：种类 / 原文 / 尺寸 / 源区域一起换；图片丢后台重写（与贴图出场同一条路径）
        var area = PhysicalArea();
        string recordKind = kind == PinContentKind.Color ? PinStore.KindColor
            : kind == PinContentKind.Text ? PinStore.KindText
            : PinStore.KindImage;

        var record = PinStore.UpdateContent(PinId, recordKind, _cardText, cardWidth,
            image.PixelWidth, image.PixelHeight, area.X, area.Y, area.Width, area.Height,
            Width, Height, snapshot);

        if (record != null)
        {
            var frozen = image;
            Task.Run(() => PinStore.SaveAssets(record, frozen));
        }

        return true;
    }
}
