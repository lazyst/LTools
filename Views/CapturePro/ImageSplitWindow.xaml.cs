using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Features.CapturePro.Split;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 切图窗（照 snow-shot 的 <c>ImageSplitWindow</c> + <c>ImageSplitEditorWidget</c>）：
/// 一张图切成多张 png，切分线可以手拖、改完立刻在画布上看得见。
/// </summary>
/// <remarks>
/// <para>
/// 内容真源是 <see cref="SplitDocument"/>（设置 + 两条轴上的线 + 撤销栈），画面走
/// <see cref="SplitPreviewCanvas"/>。表单只负责把「用户正在输入的原文」交给
/// <see cref="SplitFormDraft"/> 解析：算得出来就提交给文档，算不出来就在那个输入框下面
/// 挂一句错误——**不动文档，也不抹掉用户刚打的字**。
/// </para>
/// <para>
/// 智能切分要在后台扫一遍像素，所以设置提交有三种结果：当场生效 / 只改了重叠 / 需要后台算线
/// （见 <see cref="SplitDocument.TryApplySettings"/>）。后台结果用「修订号」做 CAS，
/// 回来晚了就直接丢掉（用户已经改成别的设置了）。
/// </para>
/// <para>
/// 关窗 / 取消 / 替换图片都要先问一句（照上游）：图片和当前的切分修改会被丢弃。
/// </para>
/// </remarks>
public partial class ImageSplitWindow : Window
{
    private enum StatusKind
    {
        None,
        Progress,
        Success,
        Error,
    }

    private readonly SplitDocument _doc = new();
    private readonly SplitFormDraft _draft = new();
    private readonly DispatcherTimer _progressTimer;

    private SplitSettings? _syncedSettings;

    private int _generation;       // 装图 / 导出 / 关闭的世代号（后一次作废前一次）
    private int _prepareToken;     // 智能找线的令牌（改设置就作废正在算的那一次）
    private bool _calculating;
    private bool _loading;
    private bool _exporting;
    private bool _confirming;
    private bool _syncing;
    private bool _closing;
    private bool _pendingSave;

    private long _progressDone;
    private long _progressTotal;

    private string _fileName = "";
    private string? _sourcePath;
    private string _status = "";
    private StatusKind _statusKind = StatusKind.None;
    private int _errorCount = 1;

    public ImageSplitWindow()
    {
        InitializeComponent();
        SplitSession.Track(this);

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _progressTimer.Tick += (_, _) => RefreshStatus();

        _doc.Changed += OnDocumentChanged;
        _doc.HistoryChanged += UpdateChrome;

        Preview.Document = _doc;

        SizeChanged += (_, _) => Preview.InvalidateVisual();
        Loaded += (_, _) =>
        {
            Preview.Focus();
            UpdateChrome();
        };

        SyncForm();
        UpdateOutputs();
        UpdateChrome();
    }

    /// <summary>还没有载入图片（切图会话据此复用空窗）。</summary>
    internal bool IsEmpty => _doc.Image == null;

    private bool Busy => _loading || _exporting || _confirming;

    // ============ 打开 ============

    /// <summary>切到空态（不显示图片）。</summary>
    public void PresentEmpty()
    {
        ++_generation;
        ++_prepareToken;
        _calculating = false;
        _loading = false;
        _exporting = false;
        _pendingSave = false;
        _progressTimer.Stop();

        _doc.Reset();
        _fileName = "";
        _sourcePath = null;
        _status = "";
        _statusKind = StatusKind.None;

        EmptyState.Visibility = Visibility.Visible;
        SyncForm();
        UpdateOutputs();
        UpdateChrome();
    }

    /// <summary>按文件打开（后台解码 + 后台找线；失败了只在页脚说一句）。</summary>
    public void PresentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!File.Exists(path))
        {
            SetStatus("图片打不开：文件不存在", StatusKind.Error);
            return;
        }

        try
        {
            if (new FileInfo(path).Length > PinFiles.MaximumImageBytes)
            {
                SetStatus("图片太大或内存不足，打不开", StatusKind.Error);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ImageSplitWindow.PresentFile", ex.Message);
            SetStatus("图片打开失败", StatusKind.Error);
            return;
        }

        int generation = ++_generation;
        BeginLoad();
        SetStatus("正在载入图片…", StatusKind.Progress);

        Task.Run(() => ImageEditSession.LoadFile(path)).ContinueWith(task =>
        {
            var image = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation != _generation || _closing) return;
                if (image == null)
                {
                    _loading = false;
                    SetStatus("图片打开失败", StatusKind.Error);
                    UpdateChrome();
                    return;
                }

                Apply(image, Path.GetFileName(path), path);
            }));
        });
    }

    /// <summary>装一张已经解码好的图（剪贴板 / 采集历史 / 贴图记录）。</summary>
    public void PresentImage(BitmapSource image, string fileName, string? sourcePath = null)
    {
        ++_generation;
        BeginLoad();
        Dispatcher.BeginInvoke(DispatcherPriority.Background,
            new Action(() => Apply(image, fileName, sourcePath)));
    }

    private void BeginLoad()
    {
        ++_prepareToken;
        _calculating = false;
        _loading = true;
        _pendingSave = false;
        _progressTimer.Stop();
        _statusKind = StatusKind.None;
        _status = "";
        UpdateChrome();
    }

    /// <summary>把图装进文档：先在后台算一次默认设置（纵向 · 智能 · 2 张）的切分线。</summary>
    private void Apply(BitmapSource image, string fileName, string? sourcePath)
    {
        int generation = _generation;
        var settings = new SplitSettings();

        Task.Run(() => SplitSmart.Prepare(image, settings,
            () => Volatile.Read(ref _generation) != generation)).ContinueWith(task =>
        {
            var prepared = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation != _generation || _closing) return;

                _loading = false;
                if (prepared == null || !_doc.SetPreparedImage(image, prepared))
                {
                    SetStatus("图片打开失败", StatusKind.Error);
                    UpdateChrome();
                    return;
                }

                _fileName = string.IsNullOrWhiteSpace(fileName) ? "截图" : fileName;
                _sourcePath = sourcePath;
                _statusKind = StatusKind.None;
                _status = "";

                EmptyState.Visibility = Visibility.Collapsed;
                SyncForm();
                UpdateOutputs();
                UpdateChrome();
                Preview.FitToCanvas();
                Preview.Focus();
            }));
        });
    }

    // ============ 文档 / 表单 ============

    private void OnDocumentChanged()
    {
        if (_syncing) return;

        var settings = _doc.State.Settings;
        if (_syncedSettings == null || !settings.Equals(_syncedSettings)) SyncForm();

        UpdateOutputs();
        UpdateChrome();
    }

    /// <summary>把文档里的设置写回表单（格式化过的原文，照上游 <c>sync</c>）。</summary>
    private void SyncForm()
    {
        _syncing = true;
        try
        {
            var settings = _doc.State.Settings;
            var draft = SplitFormDraft.From(settings);

            _draft.Direction = draft.Direction;
            _draft.Type = draft.Type;
            _draft.Unit = draft.Unit;
            _draft.Count = draft.Count;
            _draft.Overlap = draft.Overlap;
            _draft.Rows = draft.Rows;
            _draft.Columns = draft.Columns;
            _draft.Pixels = draft.Pixels;
            _draft.Percentage = draft.Percentage;

            DirVertical.IsChecked = settings.Direction == SplitDirection.Vertical;
            DirHorizontal.IsChecked = settings.Direction == SplitDirection.Horizontal;
            DirGrid.IsChecked = settings.Direction == SplitDirection.Grid;

            TypeSmart.IsChecked = settings.Type == SplitType.Smart;
            TypeEqual.IsChecked = settings.Type == SplitType.Equal;
            TypePrecise.IsChecked = settings.Type == SplitType.Precise;

            UnitPixels.IsChecked = settings.Unit == SplitUnit.Pixels;
            UnitPercentage.IsChecked = settings.Unit == SplitUnit.Percentage;

            CountBox.Text = draft.Count;
            OverlapBox.Text = draft.Overlap;
            RowsBox.Text = draft.Rows;
            ColumnsBox.Text = draft.Columns;
            SizeBox.Text = settings.Unit == SplitUnit.Pixels ? draft.Pixels : draft.Percentage;

            _syncedSettings = settings.Clone();
            ShowErrors(new Dictionary<string, string>());
        }
        finally
        {
            _syncing = false;
        }

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool grid = _draft.DirectionValue == SplitDirection.Grid;
        bool equal = _draft.TypeValue != SplitType.Precise;

        TypeLabel.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        TypeGroup.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        CountRow.Visibility = !grid && equal ? Visibility.Visible : Visibility.Collapsed;
        SizeRow.Visibility = !grid && !equal ? Visibility.Visible : Visibility.Collapsed;
        RowsRow.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;

        SizeLabel.Text = _draft.DirectionValue == SplitDirection.Horizontal ? "每张高度" : "每张宽度";
        SizeSuffix.Text = _draft.UnitValue == SplitUnit.Pixels ? "px" : "%";
    }

    private void UpdateOutputs()
    {
        var layout = _doc.Layout;
        DimensionsText.Text = layout.Width > 0 ? $"{layout.Width} × {layout.Height} px" : "";
        TotalText.Text = layout.Width > 0 ? $"共 {layout.OutputCount} 张" : "";
    }

    /// <summary>
    /// 表单上的按钮 / 画布可用性。照上游 <c>updateActions</c>：算线期间**不让编辑切分线**，
    /// 但「全部保存」保持可用（点下去会排队，算完自动继续）。
    /// </summary>
    private void UpdateChrome()
    {
        bool hasImage = _doc.Image != null;
        bool available = !Busy && !_calculating;

        EmptyState.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        CanvasToolbar.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        FormPanel.IsEnabled = !Busy;

        UndoBtn.IsEnabled = available && _doc.CanUndo;
        RedoBtn.IsEnabled = available && _doc.CanRedo;
        FitBtn.IsEnabled = hasImage;
        ReloadBtn.IsEnabled = !Busy;

        bool valid = hasImage && _doc.Layout.IsValid && _errorCount == 0;
        SaveAllBtn.IsEnabled = !Busy && valid;

        bool displayed = _draft.DirectionValue == _doc.State.Settings.Direction;
        Preview.SetEditingEnabled(available && hasImage && _doc.Layout.IsValid && displayed);

        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (_exporting)
        {
            StatusText.Text = $"正在保存：{Volatile.Read(ref _progressDone)} / {Volatile.Read(ref _progressTotal)}";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
            return;
        }

        if (_calculating)
        {
            StatusText.Text = "正在寻找切分线…";
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
            return;
        }

        StatusText.Text = _status;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(_statusKind switch
        {
            StatusKind.Error => "DangerBrush",
            StatusKind.Success => "PrimaryBrush",
            _ => "HintTextBrush",
        });
    }

    private void SetStatus(string text, StatusKind kind)
    {
        _status = text ?? "";
        _statusKind = string.IsNullOrWhiteSpace(text) ? StatusKind.None : kind;
        RefreshStatus();
    }

    // ============ 表单交互 ============

    private void Direction_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || Busy) return;
        _draft.Direction = SplitFormDraft.KeyOf(DirectionOf());
        UpdateVisibility();
        CommitDraft();
    }

    private void Type_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || Busy) return;
        _draft.Type = SplitFormDraft.KeyOf(TypeOf());
        UpdateVisibility();
        CommitDraft();
    }

    private void Unit_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || Busy) return;

        var unit = UnitPixels.IsChecked == true ? SplitUnit.Pixels : SplitUnit.Percentage;
        if (unit == _draft.UnitValue) return;

        int dimension = 0;
        if (_doc.Image is { } image)
        {
            dimension = _draft.DirectionValue == SplitDirection.Horizontal
                ? image.PixelHeight
                : image.PixelWidth;
        }

        // 切换单位时换算「每张尺寸」：百分比 → 像素，像素 → 百分比（照上游 changeUnit）
        string converted = _draft.ConvertSize(unit == SplitUnit.Pixels, dimension);
        _draft.Unit = SplitFormDraft.KeyOf(unit);

        _syncing = true;
        try
        {
            SizeBox.Text = converted;
        }
        finally
        {
            _syncing = false;
        }

        if (unit == SplitUnit.Pixels) _draft.Pixels = converted;
        else _draft.Percentage = converted;

        UpdateVisibility();
        CommitDraft();
    }

    private SplitDirection DirectionOf()
    {
        if (DirVertical.IsChecked == true) return SplitDirection.Vertical;
        if (DirHorizontal.IsChecked == true) return SplitDirection.Horizontal;
        return SplitDirection.Grid;
    }

    private SplitType TypeOf()
    {
        if (TypeEqual.IsChecked == true) return SplitType.Equal;
        if (TypePrecise.IsChecked == true) return SplitType.Precise;
        return SplitType.Smart;
    }

    private void Overlap_TextChanged(object sender, TextChangedEventArgs e) => OnTextChanged(() => _draft.Overlap = OverlapBox.Text);

    private void Count_TextChanged(object sender, TextChangedEventArgs e) => OnTextChanged(() => _draft.Count = CountBox.Text);

    private void Rows_TextChanged(object sender, TextChangedEventArgs e) => OnTextChanged(() => _draft.Rows = RowsBox.Text);

    private void Columns_TextChanged(object sender, TextChangedEventArgs e) => OnTextChanged(() => _draft.Columns = ColumnsBox.Text);

    private void Size_TextChanged(object sender, TextChangedEventArgs e) => OnTextChanged(() =>
    {
        if (_draft.UnitValue == SplitUnit.Pixels) _draft.Pixels = SizeBox.Text;
        else _draft.Percentage = SizeBox.Text;
    });

    /// <summary>原文变化：只更新草案与错误提示，**不**提交（照上游 <c>textChanged</c>）。</summary>
    private void OnTextChanged(Action capture)
    {
        if (_syncing) return;
        capture();
        ValidateDraft();
    }

    private void Field_LostFocus(object sender, RoutedEventArgs e) => CommitDraft();

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitDraft();
        e.Handled = true;
    }

    private void ValidateDraft()
    {
        var (_, errors) = ResolveDraft();
        ShowErrors(errors);
        UpdateChrome();
    }

    private (SplitSettings Settings, Dictionary<string, string> Errors) ResolveDraft()
    {
        var image = _doc.Image;
        return _draft.Resolve(_doc.State.Settings, image?.PixelWidth ?? 0, image?.PixelHeight ?? 0);
    }

    /// <summary>提交草案：算得出来就交给文档，需要找线就丢到后台。</summary>
    private void CommitDraft()
    {
        if (_syncing || Busy || _doc.Image == null) return;

        var (settings, errors) = ResolveDraft();
        ShowErrors(errors);
        if (errors.Count > 0)
        {
            UpdateChrome();
            return;
        }

        var apply = _doc.TryApplySettings(settings);
        switch (apply)
        {
            case SplitApply.NeedsPrepare:
                BeginPrepare(settings);
                break;

            case SplitApply.Applied:
                CancelPrepare();
                break;

            default:
                CancelPrepare();
                break;
        }

        UpdateChrome();
    }

    private void BeginPrepare(SplitSettings settings)
    {
        var image = _doc.Image;
        if (image == null) return;

        int token = ++_prepareToken;
        long revision = _doc.Revision;
        _calculating = true;
        _status = "";
        _statusKind = StatusKind.None;
        UpdateChrome();

        Task.Run(() => SplitSmart.Prepare(image, settings,
            () => Volatile.Read(ref _prepareToken) != token)).ContinueWith(task =>
        {
            var prepared = task.Status == TaskStatus.RanToCompletion ? task.Result : null;
            bool failed = task.IsFaulted;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (token != _prepareToken || _closing) return;

                _calculating = false;
                if (prepared != null) _doc.ApplyPrepared(prepared, revision);
                else if (failed) SetStatus("切分线没算出来，请换个切分方式", StatusKind.Error);

                UpdateChrome();
                RunPendingSave();
            }));
        });
    }

    private void CancelPrepare()
    {
        ++_prepareToken;
        _calculating = false;
    }

    // ============ 错误提示 ============

    private void ShowErrors(IReadOnlyDictionary<string, string> errors)
    {
        SetFieldError(OverlapError, errors, "overlap");
        SetFieldError(CountError, errors, "count");
        SetFieldError(RowsError, errors, "rows");
        SetFieldError(ColumnsError, errors, "columns");

        string sizeKey = _draft.SizeKey;
        SetFieldError(SizeError, errors, sizeKey);

        SetFieldError(DirectionError, errors, "direction");
        SetFieldError(TypeError, errors, "type");

        _errorCount = errors.Count;
        if (errors.TryGetValue("image", out var message) && !string.IsNullOrEmpty(message))
        {
            SetStatus(message, StatusKind.Error);
        }
    }

    private static void SetFieldError(TextBlock target, IReadOnlyDictionary<string, string> errors,
        string key)
    {
        if (errors.TryGetValue(key, out var message) && !string.IsNullOrWhiteSpace(message))
        {
            target.Text = message;
            target.Visibility = Visibility.Visible;
        }
        else
        {
            target.Text = "";
            target.Visibility = Visibility.Collapsed;
        }
    }

    // ============ 导出 ============

    private void SaveAll_Click(object sender, RoutedEventArgs e)
    {
        if (Busy) return;

        CommitDraft();
        if (_calculating)
        {
            // 上游把这次输出排队：算完线自动接着导出
            _pendingSave = true;
            return;
        }

        if (!_doc.Layout.IsValid) return;
        ChooseDirectoryAndExport();
    }

    private void RunPendingSave()
    {
        if (!_pendingSave || _calculating || Busy) return;
        _pendingSave = false;
        if (_doc.Layout.IsValid) ChooseDirectoryAndExport();
    }

    private void ChooseDirectoryAndExport()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择保存位置" };
        if (dialog.ShowDialog(this) != true) return;
        if (string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        StartExport(dialog.FolderName);
    }

    private void StartExport(string directory)
    {
        var image = _doc.Image;
        if (image == null || !_doc.Layout.IsValid) return;

        var layout = _doc.Layout.Clone();
        string name = _fileName;
        int token = ++_generation;

        _exporting = true;
        Interlocked.Exchange(ref _progressDone, 0);
        Interlocked.Exchange(ref _progressTotal, layout.OutputCount);
        _statusKind = StatusKind.None;
        _status = "";
        _progressTimer.Start();
        UpdateChrome();

        Task.Run(() => SplitExport.Export(image, layout, directory, name,
            () => Volatile.Read(ref _generation) != token,
            (done, total) =>
            {
                Interlocked.Exchange(ref _progressDone, done);
                Interlocked.Exchange(ref _progressTotal, total);
            })).ContinueWith(task =>
        {
            var result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new SplitExportResult(false, "", 0, 0, "导出失败");
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (token != _generation || _closing) return;

                _exporting = false;
                _progressTimer.Stop();
                if (result.Ok)
                {
                    SetStatus($"已保存 {result.Saved} 张到 {result.Directory}", StatusKind.Success);
                    OpenDirectory(result.Directory);
                }
                else
                {
                    SetStatus(result.Error, StatusKind.Error);
                }

                UpdateChrome();
            }));
        });
    }

    private static void OpenDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("ImageSplitWindow.OpenDirectory", ex.Message);
        }
    }

    // ============ 画布按钮 ============

    private void Undo_Click(object sender, RoutedEventArgs e) => _doc.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => _doc.Redo();

    private void Fit_Click(object sender, RoutedEventArgs e) => Preview.FitToCanvas();

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void SelectImage_Click(object sender, RoutedEventArgs e) => Reload();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>重选图片：先问一句「替换会丢掉当前修改」，再开文件框（照上游 <c>reload</c>）。</summary>
    private void Reload()
    {
        if (Busy) return;

        if (!ConfirmReplace()) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择图片",
            Filter = ImageFilter(),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        PresentFile(dialog.FileName);
    }

    private static string ImageFilter() =>
        "图片|" + string.Join(";", PinFiles.ImageExtensions.Select(ext => "*." + ext)) +
        "|所有文件|*.*";

    /// <summary>替换前的确认（没有图片时直接放行）。</summary>
    private bool ConfirmReplace()
    {
        if (_doc.Image == null) return true;
        if (_confirming) return false;

        _confirming = true;
        try
        {
            return ConfirmDialog.Confirm(this, "替换图片", "替换图片会丢弃当前的修改",
                danger: true, okText: "替换图片");
        }
        finally
        {
            _confirming = false;
            UpdateChrome();
        }
    }

    // ============ 剪贴板 / 拖放 ============

    /// <summary>Ctrl+V：剪贴板里有图就直接用，有图片文件就替换成第一张（照上游 <c>paste</c>）。</summary>
    private void Paste()
    {
        if (Busy) return;

        if (NativeClipboard.TryGetImage(out var image) && image != null)
        {
            if (!ConfirmReplace()) return;
            PresentImage(image, "剪贴板图片");
            return;
        }

        if (NativeClipboard.TryGetFileList(out var files))
        {
            var first = files.FirstOrDefault(PinFiles.IsImage);
            if (first != null)
            {
                if (!ConfirmReplace()) return;
                PresentFile(first);
                return;
            }
        }

        SetStatus("剪贴板里没有图片", StatusKind.Error);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHintTitle.Text = _doc.Image == null ? "松开载入要切分的图片" : "松开替换当前图片";
        DropHint.Visibility = files && !Busy ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) =>
        DropHint.Visibility = Visibility.Collapsed;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;

        if (Busy || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        // 一次只切一张（照上游 dropFiles）：多文件 / 非图片都只提示一句
        if (paths.Length != 1 || !PinFiles.IsImage(paths[0]))
        {
            SetStatus("一次拖入一个图片文件", StatusKind.Error);
            return;
        }

        if (!ConfirmReplace()) return;
        PresentFile(paths[0]);
    }

    // ============ 键盘 ============

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool text = Keyboard.FocusedElement is TextBoxBase;

        if (!text && Preview.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (control && e.Key == Key.V && !text)
        {
            Paste();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && !text)
        {
            Close();
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

        if (_doc.Image != null)
        {
            // 有图时先问一句：**取消这次关闭**，等用户在确认框里点了「放弃」再重排一次 Close
            // （Close() 已经返回，不能在 OnClosing 里改主意就直接关）。
            e.Cancel = true;
            _confirming = true;
            bool discard;
            try
            {
                discard = ConfirmDialog.Confirm(this, "放弃这些切图？",
                    "图片和当前的切分修改都会被丢弃", danger: true, okText: "放弃");
            }
            finally
            {
                _confirming = false;
            }

            if (!discard) return;

            _closing = true;
            ++_generation;
            ++_prepareToken;
            _progressTimer.Stop();
            Dispatcher.BeginInvoke(new Action(Close));
            return;
        }

        _closing = true;
        ++_generation;
        ++_prepareToken;
        _progressTimer.Stop();
        base.OnClosing(e);
    }
}
