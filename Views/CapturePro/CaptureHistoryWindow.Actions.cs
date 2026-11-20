using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史的操作出口（卡片按钮 / 更多菜单 / 批量 / 页头 / 删除）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshothistorypagewidget</c> 的接线：卡片 = 编辑 + 复制 + 更多（贴到屏幕 / 拼接 /
/// 切图 / 图片编辑 / 复制图片路径 / 在资源管理器中显示 / 删除），双击卡片 = 编辑、中键 = 复制，
/// 选择栏 = 删除 / 批量操作（拼接图片 · 贴到屏幕）/ 全选本页 / 取消选择。
/// </para>
/// <para>
/// 本地多出来的能力放在同一套入口里：录屏卡片把「编辑 / 复制」换成「播放」，更多菜单多
/// 「裁剪 / 倍速」「另存为…」「复制识别文字」——上游没有录屏条目，这些不是假按钮，
/// 各自都落到既有的 <see cref="RecordTrimSession"/> / <see cref="CaptureSave"/> / 剪贴板。
/// </para>
/// </remarks>
public partial class CaptureHistoryWindow
{
    // ============ 卡片 ============

    /// <summary>双击卡片 = 编辑（录屏 = 播放）；中键 = 复制成品图（录屏不复制）。</summary>
    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        // 行内按钮 / 勾选框自己处理鼠标，别再叠一层「双击整卡」
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            if (row.IsRecord) PlayEntry(row.Entry);
            else CopyEntry(row.Entry);
            return;
        }

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            e.Handled = true;
            if (row.IsRecord) PlayEntry(row.Entry);
            else EditEntry(row.Entry);
        }
    }

    /// <summary>点预览 = 看大图（识别文字在大图右侧；大图停在这一张预览上）。</summary>
    private void CardPreview_Click(object sender, MouseButtonEventArgs e)
    {
        // 行内按钮（翻页箭头 / 勾选 / 更多）自己处理鼠标，别再叠一层「点预览看大图」
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;

        e.Handled = true;
        if (RowOf(sender) is not { } row) return;

        CaptureViewerWindow.Show(this, row.Entry, row.SlideIndex);
    }

    /// <summary>预览左下 / 右的翻页箭头（多张预览才出现）。</summary>
    private void CardPrevSlide_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (RowOf(sender) is { } row) row.PreviousSlide();
    }

    private void CardNextSlide_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (RowOf(sender) is { } row) row.NextSlide();
    }

    private void CardEdit_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (row.IsRecord) PlayEntry(row.Entry);
        else EditEntry(row.Entry);
    }

    private void CardCopy_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) CopyEntry(row.Entry);
    }

    private void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) PlayEntry(row.Entry);
    }

    /// <summary>「更多」：上游那七项 + 本地录屏 / 另存为 / 复制识别文字。</summary>
    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host || RowOf(sender) is not { } row) return;

        var menu = BuildCardMenu(row.Entry);
        menu.PlacementTarget = host;
        menu.IsOpen = true;
    }

    /// <summary>卡片「更多」菜单（单独一层便于冒烟直接检查项与可用性）。</summary>
    private ContextMenu BuildCardMenu(CaptureEntry entry)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        var row = new CaptureHistoryRow(entry, null, 0);

        var pin = new MenuItem { Header = "贴到屏幕", IsEnabled = !entry.IsRecord };
        pin.Click += (_, _) => PinEntry(entry);
        menu.Items.Add(pin);

        var stitch = new MenuItem { Header = "拼接图片", IsEnabled = !entry.IsRecord };
        stitch.Click += (_, _) => StitchEntry(entry);
        menu.Items.Add(stitch);

        var split = new MenuItem { Header = "切图…", IsEnabled = !entry.IsRecord };
        split.Click += (_, _) => SplitEntry(entry);
        menu.Items.Add(split);

        var edit = new MenuItem { Header = "图片编辑", IsEnabled = !entry.IsRecord };
        edit.Click += (_, _) => EditEntry(entry);
        menu.Items.Add(edit);

        var reopen = new MenuItem { Header = "重新编辑截图…", IsEnabled = entry.HasDisplay };
        reopen.ToolTip = entry.HasDisplay
            ? "用当时那张整屏画面重开截图工具：选区、外观和画过的标注（连撤销）都在（会记一条新的采集）"
            : "这条记录没有整屏底图（老版本记录或录屏）";
        reopen.Click += (_, _) => ReopenEntry(entry);
        menu.Items.Add(reopen);

        var copyPath = new MenuItem { Header = "复制图片路径" };
        copyPath.Click += (_, _) => CopyPath(entry);
        menu.Items.Add(copyPath);

        var reveal = new MenuItem { Header = "在资源管理器中显示" };
        reveal.Click += (_, _) => Reveal(entry);
        menu.Items.Add(reveal);

        menu.Items.Add(new Separator());

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) => SaveAs(entry);
        menu.Items.Add(save);

        if (row.HasText)
        {
            var copyText = new MenuItem { Header = "复制识别文字" };
            copyText.Click += (_, _) => CopyText(entry);
            menu.Items.Add(copyText);
        }

        if (entry.IsRecord)
        {
            var play = new MenuItem { Header = "播放" };
            play.Click += (_, _) => PlayEntry(entry);
            menu.Items.Add(play);

            var trim = new MenuItem { Header = "裁剪 / 倍速…" };
            trim.Click += (_, _) => TrimEntry(entry);
            menu.Items.Add(trim);
        }

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "删除" };
        delete.Click += (_, _) => DeleteEntry(entry);
        menu.Items.Add(delete);

        return menu;
    }

    // ============ 单条操作 ============

    private void EditEntry(CaptureEntry entry)
    {
        if (entry.IsRecord)
        {
            PlayEntry(entry);
            return;
        }

        if (ImageEditSession.OpenCapture(entry)) return;
        ToastService.Show("图片文件不见了，没法编辑", "采集历史", 2200, ToastKind.error);
    }

    /// <summary>用当时那张整屏画面重开覆盖层重新编辑（选区 / 外观 / 标注都摆回来，记一条新的采集）。</summary>
    private static void ReopenEntry(CaptureEntry entry)
    {
        if (!entry.HasDisplay)
        {
            ToastService.Show("这条记录没有整屏底图（老版本记录或录屏），没法重新编辑",
                "采集历史", 2600, ToastKind.warning);
            return;
        }

        CaptureSession.TryReopen(entry);
    }

    private void CopyEntry(CaptureEntry entry)
    {
        if (entry.IsRecord) return;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id), 0);
        if (image == null)
        {
            ToastService.Show("图片文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        if (NativeClipboard.SetImage(image)) ToastService.Show("已复制图片", "采集历史", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "采集历史", 0, ToastKind.error);
    }

    private void CopyText(CaptureEntry entry)
    {
        string text = entry.Text;
        if (string.IsNullOrEmpty(text))
        {
            ToastService.Show("没有识别文字", "采集历史", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show("已复制识别文字", "采集历史", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "采集历史", 0, ToastKind.error);
    }

    private void CopyPath(CaptureEntry entry)
    {
        string path = PathOf(entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        if (NativeClipboard.SetText(path)) ToastService.Show("已复制图片路径", "采集历史", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "采集历史", 0, ToastKind.error);
    }

    private void Reveal(CaptureEntry entry)
    {
        string path = PathOf(entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开资源管理器失败：" + ex.Message, "采集历史", 0, ToastKind.error);
        }
    }

    private void PlayEntry(CaptureEntry entry)
    {
        string path = PathOf(entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("录制文件不见了", "采集历史", 2200, ToastKind.error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打不开录制文件：" + ex.Message, "采集历史", 0, ToastKind.error);
        }
    }

    /// <summary>裁剪 / 倍速：导出成新文件（原片不动）。</summary>
    private void TrimEntry(CaptureEntry entry)
    {
        string path = PathOf(entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("录屏文件不在了", "采集历史", 2200, ToastKind.error);
            return;
        }

        try
        {
            RecordTrimSession.Open(entry);
        }
        catch (Exception ex)
        {
            ToastService.Show("打开裁剪窗口失败：" + ex.Message, "采集历史", 3200, ToastKind.error);
            AppLog.Warn("CaptureHistory.Trim", "打开裁剪窗口失败：" + ex.Message);
        }
    }

    private void PinEntry(CaptureEntry entry)
    {
        if (CapturePinManager.PinHistory(entry) == null)
        {
            ToastService.Show("图片文件不见了，没法贴回屏幕", "采集历史", 2200, ToastKind.error);
            return;
        }

        // 按原选区贴回的会正好盖在当初那块；长截图 / 老记录居中贴到光标所在屏
        ToastService.Show(entry.CanPin ? "已按原选区贴到屏幕" : "已居中贴到屏幕",
            "采集历史", 1600);
    }

    private void StitchEntry(CaptureEntry entry)
    {
        if (CollageSession.OpenCapture(entry)) return;
        ToastService.Show("图片文件不见了，没法拼接", "采集历史", 2200, ToastKind.error);
    }

    private void SplitEntry(CaptureEntry entry)
    {
        if (SplitSession.OpenCapture(entry)) return;
        ToastService.Show("图片文件不见了，没法切图", "采集历史", 2200, ToastKind.error);
    }

    private void SaveAs(CaptureEntry entry)
    {
        string path = PathOf(entry);
        if (entry.IsRecord)
        {
            if (path.Length == 0 || !File.Exists(path))
            {
                ToastService.Show("录制文件不见了", "采集历史", 2200, ToastKind.error);
                return;
            }

            CaptureSave.TrySaveFile(this, path, CaptureCatalog.ExtensionOf(path));
            return;
        }

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id), 0);
        if (image == null)
        {
            ToastService.Show("图片文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        CaptureSave.TrySave(this, image);
    }

    /// <summary>删一条（菜单 / 键盘都走这里）：弹一次确认，删完列表自己刷新。</summary>
    private void DeleteEntry(CaptureEntry entry)
    {
        if (!ConfirmDialog.Confirm(this, "删除采集记录",
                "这条记录和它的图片会被删除，不能撤销。", danger: true, okText: "删除"))
            return;

        _selected.Remove(entry.Id);
        CaptureCatalog.Remove(entry.Id);
    }

    /// <summary>成品文件：录屏是视频、其余是成品图。</summary>
    private static string PathOf(CaptureEntry entry) => entry.IsRecord
        ? CaptureCatalog.FilePath(entry)
        : CaptureCatalog.AssetPath(entry.Id);

    // ============ 页头 ============

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;

        if (!ConfirmDialog.Confirm(this, "清空采集历史",
                $"会永久删除全部 {_all.Count} 条记录和它们的图片，不能撤销。", danger: true, okText: "清空"))
            return;

        _selected.Clear();
        CaptureCatalog.Clear();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // 显式刷新：缩略图、预览轮播与文件大小都重读（别处可能换过文件）
        _thumbs.Clear();
        _bytes.Clear();
        _slides.Clear();
        Reload();
        ToastService.Show($"已刷新 · 共 {_all.Count} 条", "采集历史", 1400);
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CaptureCatalog.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CaptureCatalog.Root}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开目录失败：" + ex.Message, "采集历史", 0, ToastKind.error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ============ 选择栏 ============

    private void SelectPage_Click(object sender, RoutedEventArgs e) => SelectCurrentPage();

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    /// <summary>批量：拼接图片 / 贴到屏幕（照上游，作用对象是全部勾选，不只本页）。</summary>
    private void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;

        var menu = BuildBatchMenu();
        menu.PlacementTarget = host;
        menu.IsOpen = true;
    }

    /// <summary>批量菜单（两项可用性按当前勾选算；单独一层便于冒烟直接检查）。</summary>
    private ContextMenu BuildBatchMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.Bottom };
        var selected = CaptureHistoryPaging.Selected(_all, _selected);
        if (selected.Count == 0) return menu;

        var stitch = new MenuItem
        {
            Header = $"拼接图片（{selected.Count} 张）",
            IsEnabled = CaptureHistoryPaging.AnyStitchable(selected, _selected),
        };
        stitch.Click += (_, _) => StitchSelected(selected);
        menu.Items.Add(stitch);

        var pin = new MenuItem
        {
            Header = $"贴到屏幕（{selected.Count} 张）",
            IsEnabled = CaptureHistoryPaging.AnyPinnable(selected, _selected),
        };
        pin.Click += (_, _) => PinSelected(selected);
        menu.Items.Add(pin);

        return menu;
    }

    private void StitchSelected(List<CaptureEntry> selected)
    {
        var paths = selected
            .Where(entry => !entry.IsRecord)
            .Select(entry => CaptureCatalog.AssetPath(entry.Id))
            .Where(File.Exists)
            .ToList();

        if (paths.Count == 0)
        {
            ToastService.Show("选中的记录里没有可拼接的图片", "采集历史", 2200, ToastKind.warning);
            return;
        }

        CollageSession.OpenFiles(paths);
    }

    private void PinSelected(List<CaptureEntry> selected)
    {
        int shown = 0;
        foreach (var entry in selected)
        {
            if (entry.IsRecord) continue;
            if (CapturePinManager.PinHistory(entry) != null) shown++;
        }

        if (shown == 0)
        {
            ToastService.Show("选中的记录里没有可贴回的图片", "采集历史", 2200, ToastKind.error);
            return;
        }

        ToastService.Show($"已贴回 {shown} 张", "采集历史", 1600);
    }

    /// <summary>删除全部勾选（键盘 Delete 与选择栏按钮共用）。</summary>
    private void DeleteSelected()
    {
        var selected = CaptureHistoryPaging.Selected(_all, _selected);
        if (selected.Count == 0) return;

        if (!ConfirmDialog.Confirm(this, "删除选中的记录",
                $"会删除选中的 {selected.Count} 条记录和它们的图片，不能撤销。",
                danger: true, okText: "删除"))
            return;

        _selected.Clear();
        foreach (var entry in selected) CaptureCatalog.Remove(entry.Id);
    }

    // ============ 小工具 ============

    /// <summary>按钮 / 卡片 → 它的行（行内按钮不会自动选中那一行，只能按 DataContext 认）。</summary>
    private static CaptureHistoryRow? RowOf(object? sender) =>
        (sender as FrameworkElement)?.DataContext as CaptureHistoryRow;

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T hit) return hit;

            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);

            if (d is not (Visual or System.Windows.Media.Media3D.Visual3D)) return null;
        }
        return null;
    }
}
