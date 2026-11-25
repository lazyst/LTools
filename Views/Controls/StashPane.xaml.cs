using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views;

namespace LTools.Views.Controls;

/// <summary>
/// 面板左栏的「暂存区」页（照 Quicker：暂存区是面板里的一个页面，不在设置里）。
/// </summary>
/// <remarks>
/// <para>
/// 卡片来自 <see cref="DraftStore"/>（AI / 用户写进 <c>staging/</c> 的草稿）。**单击卡片直接运行**（照 Quicker），
/// 右键菜单才是「在编辑器中打开 / 采纳为新动作 / 覆盖原动作 / 删除」等。**会弹模态框的操作先关面板**
/// （见 AGENTS.md：隐藏的 Topmost 面板当 Owner 会让模态框闪黑，而且面板仍算「已打开」会拦住长按右键手势）。
/// 读草稿失败这类页内轻提示不关面板、也不拿面板当 Owner：<see cref="ConfirmDialog"/> 无 Owner 时自己置顶。
/// </para>
/// <para>
/// 刷新按「草稿元数据 + program.json / files 的写入时间」签名判断：agent 用自己的 fs 工具直接改文件时
/// 不会更新 <c>draft.json</c>，只靠元数据会看不到变化。面板可见时 1 秒轮询一次。
/// </para>
/// </remarks>
public sealed partial class StashPane : UserControl
{
    private const int MaxCards = 200;

    private readonly DispatcherTimer _timer;
    private string _signature = "";
    private bool _active;

    public StashPane()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Reload(force: false);

        // 右键空白处：暂存区自己的页面菜单（卡片有自己的菜单，离得更近，优先）
        ContextMenu = BuildPageMenu();

        Unloaded += (_, _) => SetActive(false);
    }

    /// <summary>草稿数变化（面板左栏提示 / 页头计数用）。</summary>
    internal event Action? ItemsChanged;

    /// <summary>
    /// 只读预览（宿主进程）：草稿照常列出来，但卡片背后的写操作（编辑器 / 试运行 / 采纳 / 删除）
    /// 一律经 <c>panel.op stash.*</c> 转给提权进程——草稿目录、编辑器、自动化都只归它。
    /// </summary>
    internal bool PreviewMode { get; set; }

    /// <summary>当前列出的草稿数。</summary>
    internal int Count { get; private set; }

    /// <summary>页面是否在前台：决定是否轮询草稿目录。</summary>
    internal void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            Reload(force: true);
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>重建卡片（签名没变时直接返回，除非 <paramref name="force"/>）。</summary>
    internal void Reload(bool force)
    {
        if (!_active && !force) return;

        IReadOnlyList<DraftMeta> metas;
        try { metas = DraftStore.List(); }
        catch (Exception ex)
        {
            AppLog.Warn("Stash", "读暂存目录失败：" + ex.Message);
            metas = Array.Empty<DraftMeta>();
        }

        // 目录名不合法的（手抄 / 外部工具留下的）当坏数据跳过：否则签名 / 卡片一读就抛，整个面板打不开
        var invalid = metas.Where(m => !DraftStore.IsValidId(m.Id)).ToList();
        if (invalid.Count > 0)
        {
            AppLog.Warn("Stash", $"跳过 {invalid.Count} 个 id 不合法的暂存目录：{string.Join(", ", invalid.Select(m => m.Id))}");
            metas = metas.Where(m => DraftStore.IsValidId(m.Id)).ToList();
        }

        string signature = string.Join("\n", metas.Select(Signature));
        if (!force && signature == _signature) return;
        _signature = signature;

        var cards = new List<UIElement>(metas.Count);
        foreach (var meta in metas.Take(MaxCards))
        {
            ActionProgram? program = null;
            try { program = DraftStore.Read(meta.Id).Program; }
            catch { /* 坏草稿也列出来，至少能删掉 */ }
            cards.Add(BuildCard(meta, program));
        }

        CardGrid.Children.Clear();
        foreach (var card in cards) CardGrid.Children.Add(card);

        Count = metas.Count;
        CountText.Text = Count == 0 ? "" : $"（{Count}）";
        EmptyBox.Visibility = Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CardGrid.Visibility = Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        ItemsChanged?.Invoke();
    }

    /// <summary>
    /// 刷新签名：元数据 + program.json / files 目录的写入时间（agent 直接改文件时 draft.json 不会变）。
    /// </summary>
    private static string Signature(DraftMeta meta)
    {
        string programPath = DraftStore.ProgramPath(meta.Id);
        long programTicks = File.Exists(programPath) ? File.GetLastWriteTimeUtc(programPath).Ticks : 0;
        string filesDir = DraftStore.FilesDir(meta.Id);
        long filesTicks = Directory.Exists(filesDir) ? Directory.GetLastWriteTimeUtc(filesDir).Ticks : 0;

        return $"{meta.Id}:{meta.Title}:{meta.UpdatedAt}:{meta.SavedRevision}:{meta.PromotedActionId}:" +
               $"{meta.Summary}:{programTicks}:{filesTicks}";
    }

    // ============ 卡片 ============

    private Button BuildCard(DraftMeta meta, ActionProgram? program)
    {
        string title = DisplayName(meta, program);
        int steps = program?.Steps.Count ?? 0;

        (string status, Brush statusBrush) = meta.Status switch
        {
            DraftStatus.promoted => ("已采纳", Res("SuccessBrush")),
            DraftStatus.saved => ("已保存", Res("SecondaryTextBrush")),
            _ => ("草稿", Res("HintTextBrush")),
        };

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        content.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = Res("SurfaceAltBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = IconCatalog.GetGlyph(program?.Icon ?? IconCatalog.Default),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 17,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res("TextBrush"),
            },
        });

        content.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 12,
            MaxWidth = 92,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res("TextBrush"),
        });

        var statusPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        };
        statusPanel.Children.Add(new TextBlock
        {
            Text = steps > 0 ? $"{status} · {steps} 步" : status,
            FontSize = 10,
            Foreground = statusBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 92,
        });
        content.Children.Add(statusPanel);

        var btn = new Button
        {
            Style = (Style)FindResource("TileBtn"),
            Height = 82,
            Margin = new Thickness(0),
            Content = content,
            Tag = meta.Id,
            ToolTip = BuildTooltip(meta, program, steps),
        };
        AutomationProperties.SetName(btn, title);
        // 照 Quicker：单击卡片直接运行（编辑在右键菜单里）
        btn.Click += (_, _) => RunDraft(meta.Id);
        btn.ContextMenu = BuildCardMenu(meta, program);
        return btn;
    }

    private static string DisplayName(DraftMeta meta, ActionProgram? program) =>
        StashOps.DisplayName(meta, program);

    private static string BuildTooltip(DraftMeta meta, ActionProgram? program, int steps)
    {
        var lines = new List<string> { DisplayName(meta, program) };

        var parts = new List<string> { meta.Origin == "user" ? "手动创建" : "AI 编写" };
        if (steps > 0) parts.Add($"{steps} 步");
        if (!string.IsNullOrWhiteSpace(meta.BaseActionId)) parts.Add($"来源动作 {meta.BaseActionId}");
        parts.Add("更新 " + FormatTime(meta.UpdatedAt));
        lines.Add(string.Join(" · ", parts));

        if (meta.Status == DraftStatus.promoted && !string.IsNullOrWhiteSpace(meta.PromotedActionId))
            lines.Add($"已采纳 → {ActionRegistry.FindById(meta.PromotedActionId)?.Name ?? meta.PromotedActionId}");

        string summary = meta.Summary ?? program?.Description ?? "";
        if (!string.IsNullOrWhiteSpace(summary)) lines.Add(summary);

        lines.Add("单击运行 · 右键更多");
        return string.Join("\n", lines);
    }

    // ============ 菜单 ============

    private ContextMenu BuildCardMenu(DraftMeta meta, ActionProgram? program)
    {
        var menu = new ContextMenu();
        Track(menu);

        Add(menu, "在编辑器中打开", () => OpenDraft(meta.Id));
        Add(menu, "试运行", () => RunDraft(meta.Id));
        // 草稿名与它采纳后的动作同名（都取 program.Name），Id 是草稿目录名。
        Add(menu, "复制名称", () => ClipboardCopy.Text(DisplayName(meta, program), "StashPane.CopyName"));
        Add(menu, "复制 Id", () => ClipboardCopy.Text(meta.Id, "StashPane.CopyId"));
        menu.Items.Add(new Separator());

        Add(menu, meta.Status == DraftStatus.promoted ? "再次采纳为新动作…" : "采纳为新动作…",
            () => Promote(meta.Id, "new"));
        if (!string.IsNullOrWhiteSpace(meta.BaseActionId))
            Add(menu, "覆盖原动作…", () => Promote(meta.Id, "replace"));

        menu.Items.Add(new Separator());
        Add(menu, "打开草稿目录", () => OpenPath(DraftStore.DirectoryOf(meta.Id)));
        Add(menu, "删除草稿…", () => DeleteDraft(meta.Id));
        menu.Items.Add(new Separator());
        Add(menu, "刷新", () => Reload(force: true));

        menu.Placement = PlacementMode.MousePoint;
        return menu;
    }

    private ContextMenu BuildPageMenu()
    {
        var menu = new ContextMenu();
        Track(menu);

        Add(menu, "刷新", () => Reload(force: true));
        Add(menu, "打开暂存目录", () => OpenPath(DraftStore.Root));
        menu.Items.Add(new Separator());
        Add(menu, "清空暂存区…", ClearAll);
        Add(menu, "AI 接入设置…", () =>
        {
            Features.SuperPanel.Close();
            Features.ConfigHelper.OpenAi();
        });

        menu.Placement = PlacementMode.MousePoint;
        return menu;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload(force: true);

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildPageMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>交给面板窗口记账：菜单打开时钩子不关面板、悬浮项继续盖在上面。</summary>
    private void Track(ContextMenu menu)
    {
        if (Window.GetWindow(this) is SuperPanelWindow panel) panel.AttachMenu(menu);
    }

    /// <summary>菜单项点击往往要弹模态框：推迟到菜单关掉之后的 Dispatcher 周期。</summary>
    private void Add(ContextMenu menu, string header, Action invoke)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => Dispatcher.InvokeAsync(invoke);
        menu.Items.Add(item);
    }

    // ============ 操作 ============

    private void OpenDraft(string id)
    {
        Features.SuperPanel.Close();
        if (PreviewMode)
        {
            PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashOpen, Id: id));
            return;
        }

        StashOps.Open(id);
    }

    private void RunDraft(string id)
    {
        Features.SuperPanel.Close();
        if (PreviewMode)
        {
            PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashRun, Id: id));
            return;
        }

        StashOps.Run(id);
    }

    private void Promote(string id, string mode)
    {
        // 覆盖原动作要弹差异确认框，面板必须先关（Topmost 的隐藏面板会让模态框闪黑）。
        if (string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase)) Features.SuperPanel.Close();

        if (PreviewMode)
        {
            Features.SuperPanel.Close();
            PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashPromote, Id: id, Kind: mode));
            return;
        }

        StashOps.Promote(id, mode);
        Reload(force: true);
    }

    private void DeleteDraft(string id)
    {
        Features.SuperPanel.Close();
        if (PreviewMode)
        {
            PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashDelete, Id: id));
            return;
        }

        StashOps.Delete(id);
    }

    private void ClearAll()
    {
        Features.SuperPanel.Close();
        if (PreviewMode)
        {
            PanelHostBridge.TryActionOp(new ActionOpRequest(PanelOps.StashClear));
            return;
        }

        StashOps.ClearAll();
    }

    // ============ 小工具 ============

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private static string FormatTime(string value)
    {
        if (DateTime.TryParse(value, out var time))
            return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
        return value;
    }

    private static void OpenPath(string path)
    {
        // 打开的是外部资源管理器窗口：面板留着只会挡在它前面（用户预期是「打开目录」后回到桌面）。
        Features.SuperPanel.Close();
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开失败：" + ex.Message, "暂存区", 3000, ToastKind.error);
        }
    }
}
