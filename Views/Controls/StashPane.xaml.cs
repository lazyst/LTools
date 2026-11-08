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
using LTools.Features;

namespace LTools.Views.Controls;

/// <summary>
/// 面板左栏的「暂存区」页（照 Quicker：暂存区是面板里的一个页面，不在设置里）。
/// </summary>
/// <remarks>
/// <para>
/// 卡片来自 <see cref="DraftStore"/>（AI / 用户写进 <c>staging/</c> 的草稿）。**单击卡片直接运行**（照 Quicker），
/// 右键菜单才是「在编辑器中打开 / 采纳为新动作 / 覆盖原动作 / 删除」等。**所有会弹对话框的操作先关面板**
/// （见 AGENTS.md：隐藏的 Topmost 面板当 Owner 会让模态框闪黑，而且面板仍算「已打开」会拦住长按右键手势）。
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
        btn.ContextMenu = BuildCardMenu(meta);
        return btn;
    }

    private static string DisplayName(DraftMeta meta, ActionProgram? program)
    {
        if (!string.IsNullOrWhiteSpace(program?.Name)) return program!.Name.Trim();
        if (!string.IsNullOrWhiteSpace(meta.Title)) return meta.Title!.Trim();
        return "未命名草稿";
    }

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

    private ContextMenu BuildCardMenu(DraftMeta meta)
    {
        var menu = new ContextMenu();
        Track(menu);

        Add(menu, "在编辑器中打开", () => OpenDraft(meta.Id));
        Add(menu, "试运行", () => RunDraft(meta.Id));
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
        ActionProgram program;
        try
        {
            var snapshot = DraftStore.Read(id);
            if (snapshot.Program == null)
            {
                MessageBox.Show(snapshot.Error ?? "草稿内容不完整，先让 AI 重新写一遍。",
                    "LTools · 打开草稿", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            program = snapshot.Program;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LTools · 打开草稿", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var draft = new ActionDto
        {
            Name = string.IsNullOrWhiteSpace(program.Name) ? "AI 草稿" : program.Name,
            Icon = string.IsNullOrWhiteSpace(program.Icon) ? IconCatalog.Default : program.Icon,
            Type = ActionType.composite,
            Program = program.Clone(),
        };

        Features.SuperPanel.Close();
        var result = ActionEditor.Show(null, "编辑草稿 · " + draft.Name, draft, DraftStore.FilesDir(id));
        if (result?.Program == null) return;

        try
        {
            DraftStore.WriteProgram(id, result.Program);
            DraftStore.Save(id, null, "在编辑器中修改");
            ToastService.Show("草稿已更新（AI 下次读取会拿到这一版）", result.Name, 2500, ToastKind.success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LTools · 保存草稿失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RunDraft(string id)
    {
        ActionProgram program;
        string revision;
        string filesDir;
        try
        {
            var snapshot = DraftStore.Read(id);
            if (snapshot.Program == null)
            {
                MessageBox.Show(snapshot.Error ?? "草稿内容不完整，无法试运行。",
                    "LTools · 试运行", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            program = snapshot.Program;
            revision = snapshot.Revision ?? "";
            filesDir = DraftStore.FilesDir(id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LTools · 试运行", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Features.SuperPanel.Close();

        var validation = ActionValidator.Validate(program, revision.Length > 0 ? revision : null, filesDir);
        if (!validation.Ok && !ShowIssues(validation, "还不能试运行")) return;

        string name = string.IsNullOrWhiteSpace(program.Name) ? "AI 草稿" : program.Name;
        if (AiRuns.Start(id, name, program, null, filesDir, notify: true) == null)
        {
            ToastService.Show("已有自动化在运行，等它结束再试", "试运行", 3000, ToastKind.warning);
            return;
        }
        ToastService.Show("试运行中（停止热键可中止）", name, 3000, ToastKind.info);
    }

    private void Promote(string id, string mode)
    {
        var meta = DraftStore.Find(id);
        if (meta == null) return;

        bool replace = mode == "replace";
        string title = DisplayName(meta, null);

        if (replace)
        {
            Features.SuperPanel.Close();
            if (!AiUi.Confirm("覆盖原动作",
                    $"用草稿「{title}」覆盖来源动作 {meta.BaseActionId}？\n\n" +
                    "覆盖前会再确认来源动作自复制以来没有被改过；改过就只允许另存为新动作。"))
                return;
        }

        try
        {
            string? revision = DraftStore.Read(id).Revision;
            var result = DraftPromoter.Promote(id, replace ? "replace" : "new", revision);
            string toast = result.Created
                ? $"已采纳为新动作「{title}」"
                : $"已覆盖动作「{title}」";
            string hint = result.Created
                ? "已进动作库；要放上面板，在「动作管理」里拖进来。"
                : "";
            ToastService.Show(hint.Length > 0 ? toast + "。" + hint : toast, "暂存区", 3500, ToastKind.success);
        }
        catch (Exception ex)
        {
            // 采纳（新建）时面板还开着：用常驻提示而不是 MessageBox，免得模态框被 Topmost 面板挡在后面
            if (replace) MessageBox.Show(ex.Message, "LTools · 采纳失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            else ToastService.Show(ex.Message, "采纳失败", ToastService.StickyMs, ToastKind.error, key: "stash:" + id);
        }

        Reload(force: true);
    }

    private void DeleteDraft(string id)
    {
        var meta = DraftStore.Find(id);
        if (meta == null) return;

        Features.SuperPanel.Close();
        if (!AiUi.Confirm("删除草稿",
                $"删除草稿「{DisplayName(meta, null)}」？\n\n草稿目录（program.json / files / history）会一起删掉，" +
                "不可恢复；已经采纳出去的正式动作不受影响。"))
            return;

        try
        {
            DraftStore.Delete(id);
            ToastService.Show("草稿已删除", "暂存区", 2000, ToastKind.success);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "LTools · 删除草稿失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearAll()
    {
        var metas = DraftStore.List();
        if (metas.Count == 0)
        {
            ToastService.Show("暂存区已经是空的", "清空暂存区", 2000, ToastKind.info);
            return;
        }

        Features.SuperPanel.Close();
        if (!AiUi.Confirm("清空暂存区",
                $"删除全部 {metas.Count} 份草稿？\n\n草稿目录会一起删掉，不可恢复；已经采纳出去的正式动作不受影响。"))
            return;

        int deleted = 0;
        foreach (var meta in metas)
        {
            try { DraftStore.Delete(meta.Id); deleted++; }
            catch (Exception ex) { AppLog.Warn("Stash", $"删除草稿 {meta.Id} 失败：{ex.Message}"); }
        }
        ToastService.Show($"已删除 {deleted} 份草稿", "清空暂存区", 2500, ToastKind.success);
    }

    // ============ 小工具 ============

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    /// <summary>校验没通过时列出前几条。返回 true = 用户选择「仍然继续」（只有试运行给这个出口）。</summary>
    private static bool ShowIssues(ActionValidationResult validation, string title, bool allowContinue = true)
    {
        string detail = string.Join("\n", validation.Issues
            .Where(i => i.Severity == IssueSeverity.error)
            .Take(6)
            .Select(i => $"· {i.Label}{i.Message}"));

        if (!allowContinue)
        {
            MessageBox.Show(detail, "LTools · " + title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        return MessageBox.Show($"{detail}\n\n仍然继续？", "LTools · " + title,
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private static string FormatTime(string value)
    {
        if (DateTime.TryParse(value, out var time))
            return time.Date == DateTime.Today ? time.ToString("HH:mm") : time.ToString("MM-dd HH:mm");
        return value;
    }

    private static void OpenPath(string path)
    {
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
