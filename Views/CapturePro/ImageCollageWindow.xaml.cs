using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.Collage;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图像拼接窗（照 snow-shot 的 <c>ImageStitchingWindow</c> + <c>ImageStitchingEditorWidget</c>）：
/// 左边是拼接预览（棋盘格 + 背景 + 逐图裁剪绘制 + 选中框 / 八把手 + 两条浮动工具条）
/// 加一条缩略图，右边是布局表单，底部是尺寸 / 张数 + 取消 / 图像编辑 / 复制 / 保存。
/// </summary>
/// <remarks>
/// <para>
/// 内容真源是 <see cref="CollageDocument"/>（图片清单 + 设置 + 选中项 + 手摆位置），画面走
/// <see cref="CollageCanvas"/>。这个文件只做窗口骨架：读图 / 文档事件 / 表单与缩略图的同步 /
/// 浮动工具条定位 / 拖放与 Esc / 关窗确认；表单在 <c>.Form.cs</c>、缩略图在 <c>.Thumbs.cs</c>、
/// 导出在 <c>.Export.cs</c>。
/// </para>
/// <para>
/// 导出成功（复制 / 保存）照上游直接关窗；失败只在页脚状态行说一句、把窗口留着，
/// 用户还能另存为或改设置重试。加载 / 导出期间表单与画布关掉编辑，
/// **不假装成功**（<see cref="CollageRender"/> 抛错就照实报）。
/// </para>
/// </remarks>
public partial class ImageCollageWindow : Window
{
    private enum StatusKind
    {
        None,
        Progress,
        Success,
        Error,
    }

    private readonly CollageDocument _doc = new();
    private readonly List<CollageThumbnail> _cards = new();

    private CollageSettings _saved = CollageSettings.Default;
    private CaptureOriginalPreviewWindow? _preview;

    /// <summary>同步表单 / 缩略图时不回写文档（照上游 <c>syncing</c>）。</summary>
    private bool _syncing;

    private bool _loading;
    private bool _exporting;
    private bool _closing;
    private bool _confirming;

    /// <summary>异步加载 / 导出 / 关窗的世代号：过期的回调一律丢弃。</summary>
    private int _generation;

    private string _status = "";
    private StatusKind _statusKind;

    public ImageCollageWindow()
    {
        InitializeComponent();

        _doc.SetSettings(CollageStore.Load());
        _saved = _doc.Settings;

        _doc.Changed += OnDocumentChanged;
        _doc.SelectionChanged += OnSelectionChanged;
        Preview.Document = _doc;
        Preview.ViewportChanged += PositionToolbars;

        Loaded += Window_Loaded;
        CollageSession.Track(this);
    }

    /// <summary>没有任何图（会话据此复用空窗）。</summary>
    internal bool IsEmpty => _doc.IsEmpty;

    /// <summary>现在能不能再接新图（加载 / 导出 / 关窗期间会拒绝）。</summary>
    internal bool CanAcceptImages => !_loading && !_exporting && !_closing;

    /// <summary>当前拼接设置（测试 / 冒烟读）。</summary>
    internal CollageSettings Settings => _doc.Settings;

    /// <summary>当前选中的图（测试 / 冒烟读）。</summary>
    internal long SelectedId => _doc.SelectedId;

    // ============ 入口 ============

    /// <summary>空窗（托盘「图像拼接…」）：设置取上次的，画布等第一张图。</summary>
    internal void PresentEmpty()
    {
        SetStatus("", StatusKind.None);
        if (IsLoaded) Preview.Focus();
    }

    /// <summary>按文件添加（拖放 / 托盘 / 关联打开）：不是图片的跳过。</summary>
    internal void PresentFiles(IReadOnlyList<string> paths)
    {
        var images = paths?.Where(PinFiles.IsImage).ToList() ?? new List<string>();
        if (images.Count == 0)
        {
            SetStatus("没有可用的图像文件", StatusKind.Error);
            return;
        }

        LoadFiles(images);
    }

    /// <summary>添加一张文件（单张入口）。</summary>
    internal void PresentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("没有可用的图像文件", StatusKind.Error);
            return;
        }

        LoadFiles(new[] { path });
    }

    /// <summary>把内存里的一张图直接加进来（长截图 / 贴图 / 采集历史 / 剪贴板）。</summary>
    internal void PresentImage(BitmapSource? image, string label)
    {
        if (image == null)
        {
            SetStatus("无法读取图像，或图像已不可用", StatusKind.Error);
            return;
        }

        _doc.Append(image, string.IsNullOrWhiteSpace(label) ? "图像" : label);
        Preview.FitToCanvas();
    }

    // ============ 加载 ============

    private void LoadFiles(IReadOnlyList<string> files)
    {
        if (_loading || _exporting || _closing || files.Count == 0) return;

        int generation = ++_generation;
        int total = files.Count;
        _loading = true;
        SetStatus(total == 1 ? "正在读取选中的图像文件…" : $"正在加载图像：0 / {total}", StatusKind.Progress);
        UpdateChrome();

        Task.Run(() =>
        {
            var loaded = new List<(string Path, BitmapSource? Image)>(total);
            for (int i = 0; i < total; i++)
            {
                loaded.Add((files[i], ImageEditSession.LoadFile(files[i])));
                int done = i + 1;
                if (done < total)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (_closing || generation != _generation) return;
                        SetStatus($"正在加载图像：{done} / {total}", StatusKind.Progress);
                    }));
                }
            }

            return loaded;
        }).ContinueWith(task =>
        {
            var loaded = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing || generation != _generation) return;

                _loading = false;
                if (loaded == null)
                {
                    SetStatus("无法读取选中的图像文件，请重试", StatusKind.Error);
                    UpdateChrome();
                    return;
                }

                var failures = new List<string>();
                foreach (var (path, image) in loaded)
                {
                    if (image == null)
                    {
                        failures.Add(Path.GetFileName(path));
                        continue;
                    }

                    _doc.Append(image, Path.GetFileName(path));
                }

                UpdateChrome();
                if (failures.Count > 0)
                {
                    SetStatus("部分图像无法加载：\n" + string.Join("\n", failures), StatusKind.Error);
                }
                else
                {
                    SetStatus("", StatusKind.None);
                }
            }));
        });
    }

    /// <summary>换掉某一张的像素（缩略图 / 工具条的「替换图像」）。</summary>
    private void ReplaceFile(long id, string path)
    {
        if (_loading || _exporting || _closing) return;
        if (_doc.Find(id) == null) return;

        int generation = ++_generation;
        _loading = true;
        SetStatus("正在读取选中的图像文件…", StatusKind.Progress);
        UpdateChrome();

        Task.Run(() => ImageEditSession.LoadFile(path)).ContinueWith(task =>
        {
            var image = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_closing || generation != _generation) return;

                _loading = false;
                if (image == null)
                {
                    SetStatus("无法解码图像，或图像已不可用", StatusKind.Error);
                    UpdateChrome();
                    return;
                }

                _doc.Replace(id, image, Path.GetFileName(path));
                SetStatus("", StatusKind.None);
                UpdateChrome();
            }));
        });
    }

    // ============ 文档事件 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SyncForm();
        SyncThumbnails();
        UpdateChrome();
        PositionToolbars();
        if (_doc.IsEmpty) Preview.Focus();
    }

    private void OnDocumentChanged()
    {
        if (_closing) return;
        SyncForm();
        SyncThumbnails();
        UpdateChrome();
        PositionToolbars();
        SaveSettings();
    }

    private void OnSelectionChanged()
    {
        if (_closing) return;
        SyncThumbnails();
        UpdateChrome();
        PositionToolbars();
    }

    /// <summary>设置变了才写盘（照上游 <c>settingsEdited</c> 落 <c>collagesettingsstore</c>）。</summary>
    private void SaveSettings()
    {
        // 拖取色滑块会连着改几十次，写盘按 250ms 合并（关窗时 FlushSettings 一定补写）。
        if (_settingsTimer == null)
        {
            _settingsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _settingsTimer.Tick += (_, _) => FlushSettings();
        }

        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    /// <summary>把待写的设置落盘（攒批计时器到点 / 关窗时调用）。</summary>
    private void FlushSettings()
    {
        _settingsTimer?.Stop();

        var settings = _doc.Settings;
        if (settings == _saved) return;

        _saved = settings;
        CollageStore.Save(settings);
    }

    // ============ 外观 ============

    /// <summary>
    /// 按文档更新按钮可用性 / 空态 / 页脚（照上游 <c>enabled()</c> + <c>refresh()</c>）。
    /// **加载与导出期间关掉编辑**，但「取消」永远可用。
    /// </summary>
    private void UpdateChrome()
    {
        var layout = _doc.Layout;
        int count = _doc.Items.Count;
        bool empty = count == 0;
        bool busy = _loading || _exporting;
        bool valid = layout.IsValid;
        bool hasSelection = _doc.SelectedId != 0;
        bool editing = !busy && !_confirming;

        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ThumbnailRow.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        CanvasToolbar.Visibility = Visibility.Visible;

        AddBtn.IsEnabled = !busy;
        EmptyAddBtn.IsEnabled = !busy;
        ClearBtn.IsEnabled = !_exporting && !_confirming && !empty;
        ReverseBtn.IsEnabled = !busy && count > 1;
        FitBtn.IsEnabled = Preview.CanFitToCanvas;

        FormPanel.IsEnabled = !_exporting;
        Preview.Editing = editing;
        foreach (var card in _cards) card.SetEditing(editing);

        CancelBtn.IsEnabled = !_confirming;
        EditBtn.IsEnabled = valid && !busy && !_confirming;
        CopyBtn.IsEnabled = valid && !busy && !_confirming;
        SaveBtn.IsEnabled = valid && !busy && !_confirming;
        EditBtn.Visibility = Visibility.Visible;
        CopyBtn.Visibility = Visibility.Visible;

        DimensionsText.Text = valid ? $"{layout.Width} × {layout.Height} px" : "";
        TotalText.Text = $"共 {count} 张图像";
        TotalText.Visibility = Visibility.Visible;

        RefreshStatus();
        SyncSelectionToolbarEnabled(hasSelection, editing);
    }

    private void SyncSelectionToolbarEnabled(bool hasSelection, bool editing)
    {
        RotateCwBtn.IsEnabled = hasSelection && editing;
        RotateCcwBtn.IsEnabled = hasSelection && editing;
        FlipHBtn.IsEnabled = hasSelection && editing;
        FlipVBtn.IsEnabled = hasSelection && editing;
        ResetTransformBtn.IsEnabled = hasSelection && editing;
        ReplaceBtn.IsEnabled = hasSelection && editing;
        PreviewOriginalBtn.IsEnabled = hasSelection && !_exporting;
        DeleteSelectedBtn.IsEnabled = hasSelection && editing;
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status = text ?? "";
        _statusKind = string.IsNullOrWhiteSpace(text) ? StatusKind.None : kind;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        StatusText.Text = _status;
        StatusText.ToolTip = _status.Length > 24 ? _status : null;
        StatusText.Foreground = (Brush)FindResource(_statusKind switch
        {
            StatusKind.Error => "DangerBrush",
            StatusKind.Success => "PrimaryBrush",
            StatusKind.Progress => "SecondaryTextBrush",
            _ => "HintTextBrush",
        });
    }

    /// <summary>
    /// 浮动工具条定位（照上游 <c>geometryChanged</c>）：贴着选中框上边 12，上方放不下时翻到左侧 /
    /// 右侧（离边 12），左右也放不下就贴在选中框顶部；整体夹在画布内 8。
    /// 布局算不出来但还有选中项时（「布局恢复」），贴底居中，用户还能删图 / 换图救回来。
    /// </summary>
    private void PositionToolbars()
    {
        if (_closing) return;

        var selection = Preview.SelectionFrame;
        bool layoutRecovery = !_doc.Layout.IsValid && _doc.SelectedId != 0;
        bool hasSelection = _doc.SelectedId != 0;
        var bounds = new Rect(Preview.RenderSize);
        bool intersects = !selection.IsEmpty && bounds.IntersectsWith(selection);

        if (!hasSelection || (!layoutRecovery && !intersects))
        {
            SelectionToolbar.Visibility = Visibility.Collapsed;
            return;
        }

        SelectionToolbar.Visibility = Visibility.Visible;
        SelectionToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Max(SelectionToolbar.DesiredSize.Width, SelectionToolbar.ActualWidth);
        double height = Math.Max(SelectionToolbar.DesiredSize.Height, SelectionToolbar.ActualHeight);
        if (width <= 0 || height <= 0) return;

        double maximumX = Math.Max(8, bounds.Width - width - 8);
        double maximumY = Math.Max(8, bounds.Height - height - 8);
        double x = layoutRecovery ? (bounds.Width - width) / 2 : selection.X + selection.Width / 2 - width / 2;
        double y = layoutRecovery ? bounds.Height - height - 16 : selection.Top - height - 12;

        // 上方没地方、左右却有余量时把工具条放到图旁边（照上游：别挡住图片顶边）。
        if (!layoutRecovery && y < 8 && selection.Left >= width + 20)
        {
            x = selection.Left - width - 12;
            y = selection.Top;
        }
        else if (!layoutRecovery && y < 8 && selection.Right + width + 20 <= bounds.Width)
        {
            x = selection.Right + 12;
            y = selection.Top;
        }

        Canvas.SetLeft(SelectionToolbar, Math.Clamp(x, 8, maximumX));
        Canvas.SetTop(SelectionToolbar, Math.Clamp(y, 8, maximumY));
    }

    // ============ 工具栏动作 ============

    private void ApplyToSelected(Action<long> action)
    {
        if (_loading || _exporting || _closing) return;
        long id = _doc.SelectedId;
        if (id == 0) return;

        action(id);
    }

    private void RotateCw_Click(object sender, RoutedEventArgs e) => ApplyToSelected(id => _doc.Rotate(id, true));

    private void RotateCcw_Click(object sender, RoutedEventArgs e) => ApplyToSelected(id => _doc.Rotate(id, false));

    private void FlipH_Click(object sender, RoutedEventArgs e) => ApplyToSelected(id => _doc.Flip(id, true));

    private void FlipV_Click(object sender, RoutedEventArgs e) => ApplyToSelected(id => _doc.Flip(id, false));

    private void ResetTransform_Click(object sender, RoutedEventArgs e) =>
        ApplyToSelected(id => _doc.ResetTransform(id));

    private void DeleteSelected_Click(object sender, RoutedEventArgs e) =>
        ApplyToSelected(id => _doc.Remove(id));

    private void Fit_Click(object sender, RoutedEventArgs e) => Preview.FitToCanvas();

    /// <summary>取消 = 关窗；有图时走 <see cref="OnClosing"/> 里的「放弃此图像拼接？」确认。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Reverse_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _closing) return;
        _doc.Reverse();
    }

    /// <summary>「添加图像」：多选（照上游 <c>ExistingFiles</c>）。</summary>
    private void AddImages_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _closing) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图像",
            Filter = ImageFilter(),
            CheckFileExists = true,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        LoadFiles(dialog.FileNames);
    }

    /// <summary>替换选中的图：只选一张（照上游 <c>ExistingFile</c>）。</summary>
    private void ReplaceSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _exporting || _closing) return;
        long id = _doc.SelectedId;
        if (id == 0) return;
        PickReplacement(id);
    }

    /// <summary>清空（照上游 <c>requestClear</c>：先确认，再清文档）。</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting || _confirming || _closing || _doc.IsEmpty) return;

        if (!ConfirmDialog.Confirm(this, "清空全部图像？", "将从此图像拼接中移除全部图像",
                danger: true, okText: "全部清空"))
            return;

        _doc.Clear();
        SetStatus("", StatusKind.None);
        UpdateChrome();
    }

    /// <summary>预览原图（照上游 <c>OriginalPreview</c>：再点一次关掉）。</summary>
    private void PreviewOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_preview != null)
        {
            _preview.Close();
            _preview = null;
            return;
        }

        var item = _doc.Selected;
        if (item == null) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (!(scale > 0.1)) scale = 1;

        var preview = new CaptureOriginalPreviewWindow(item.Source, scale);
        preview.Closed += (_, _) => _preview = null;
        preview.Show();

        // 工作区取窗口所在显示器（多屏时按窗口中心判），物理像素再按窗口 DPI 换 DIP。
        Rect work = SystemParameters.WorkArea;
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && Win32.GetWindowRect(handle, out var rect))
            {
                var screen = CaptureRecordWindow.ScreenOf(new Int32Rect(rect.Left, rect.Top,
                    Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top)));
                if (screen != null)
                {
                    work = new Rect(screen.WorkLeft / scale, screen.WorkTop / scale,
                        screen.WorkWidth / scale, screen.WorkHeight / scale);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "读取显示器工作区失败：" + ex.Message);
        }

        preview.PlaceBeside(new Rect(Left, Top, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)), work);
        _preview = preview;
    }

    // ============ 拖放 / 粘贴 ============

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool accepts = !_loading && !_exporting && !_closing &&
                       e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        DropHintTitle.Text = "松开鼠标，将图像添加到图像拼接";
        DropHint.Visibility = accepts ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (_loading || _exporting || _closing) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var images = paths.Where(PinFiles.IsImage).ToList();
        if (images.Count == 0)
        {
            SetStatus("没有可用的图像文件", StatusKind.Error);
            return;
        }

        LoadFiles(images);
    }

    /// <summary>Ctrl+V：剪贴板里的图片文件优先，其次位图（照上游 <c>paste</c>）。</summary>
    private void Paste()
    {
        if (_loading || _exporting || _closing) return;

        if (NativeClipboard.TryGetFileList(out var files))
        {
            var images = files.Where(PinFiles.IsImage).ToList();
            if (images.Count > 0)
            {
                LoadFiles(images);
                return;
            }
        }

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            PresentImage(image, "剪贴板图像");
            return;
        }

        SetStatus("剪贴板中没有图片", StatusKind.Error);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 取色弹层先收（用户按 Esc 是想关它，不是想放弃整个拼接）
            if (ColorPopup.IsOpen)
            {
                ColorPopup.IsOpen = false;
                e.Handled = true;
                return;
            }

            if (_dragSource != 0)
            {
                CancelThumbnailDrag();
                e.Handled = true;
                return;
            }

            if (_confirming) return;
            Close();
            e.Handled = true;
            return;
        }

        bool text = Keyboard.FocusedElement is TextBoxBase;
        if (!text && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.V)
        {
            Paste();
            e.Handled = true;
        }
    }

    // ============ 关窗 ============

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closing || _confirming)
        {
            base.OnClosing(e);
            return;
        }

        if (!_doc.IsEmpty)
        {
            // 关窗前问一句（照上游 <c>requestClose</c>）：先取消这次关闭，确认后再关一次。
            e.Cancel = true;
            _confirming = true;
            bool discard;
            try
            {
                discard = ConfirmDialog.Confirm(this, "放弃此图像拼接？",
                    "将放弃此图像拼接中的图像和编辑", danger: true, okText: "放弃");
            }
            finally
            {
                _confirming = false;
            }

            if (!discard) return;

            _closing = true;
            ++_generation;
            FlushSettings();
            Dispatcher.BeginInvoke(new Action(Close));
            return;
        }

        _closing = true;
        ++_generation;
        FlushSettings();
        ReleasePreview();
        base.OnClosing(e);
    }

    private void ReleasePreview()
    {
        if (_preview == null) return;
        _preview.Close();
        _preview = null;
    }

    private static string ImageFilter() =>
        "图片|" + string.Join(";", PinFiles.ImageExtensions.Select(ext => "*." + ext)) +
        "|所有文件|*.*";
}
