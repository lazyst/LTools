using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Features;

namespace LTools.Views.Controls;

/// <summary>
/// 设置 → AI：MCP 连接、暂存区入口、知识包、审计。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="McpServer"/> 的分工：这里只读写 <see cref="AiSettings"/> 并触发
/// <see cref="McpServer.Apply"/>（后台线程，自己绝不等 HTTP）。MCP 回调线程永远不会碰这个控件。
/// </para>
/// <para>
/// **草稿列表不在这里**：暂存区是面板左栏的一页（照 Quicker），草稿卡片、试运行、采纳都在
/// <see cref="StashPane"/>。这一页只留连接 / 知识包 / 审计和一个「打开面板暂存区」入口。
/// </para>
/// </remarks>
public partial class AiView : UserControl
{
    private DispatcherTimer? _timer;
    private AiSettings _settings = new();
    private bool _loading;
    private bool _loaded;
    private string _page = "connect";

    public AiView()
    {
        InitializeComponent();

        ShowPage("connect");

        Loaded += (_, _) =>
        {
            if (!_loaded) Reload();
        };
        Unloaded += (_, _) => _timer?.Stop();
    }

    /// <summary>
    /// 页面是否可见：1 秒状态刷新只在「AI」页可见时跑，窗口开着但停在别的页（Collapsed）
    /// 不常驻；由 <c>ConfigHelperWindow.ApplyNavPage</c> 切页时驱动。
    /// </summary>
    public void SetActive(bool active)
    {
        if (!active) { _timer?.Stop(); return; }
        EnsureTimer();
        _timer?.Start();
    }

    /// <summary>计时器只建一次、Tick 只挂一次（照原来构造里的 1 秒设置）。</summary>
    private void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => AutoRefresh();
    }

    /// <summary>当前子页（connect / audit）。</summary>
    public string CurrentPage => _page;

    /// <summary>
    /// 设置左栏「AI 接入」的二级导航切页：子页同层，只切可见性。
    /// 供 <c>ConfigHelperWindow.ApplyNavPage</c> 调用；旧的 prompt / knowledge / stash 都已并入 connect。
    /// </summary>
    public void ShowPage(string page)
    {
        _page = page == "audit" ? "audit" : "connect";

        Page_Connect.Visibility = _page == "connect" ? Visibility.Visible : Visibility.Collapsed;
        Page_Audit.Visibility = _page == "audit" ? Visibility.Visible : Visibility.Collapsed;

        // 只在看得见时刷新，免得每个子页都白干一次
        if (_page == "connect") RefreshPrompt();
        if (_page == "audit") RefreshAudit();
    }

    /// <summary>设置页导航到「AI」时调用：重读配置。</summary>
    public void Reload()
    {
        _loaded = true;
        _loading = true;
        try
        {
            _settings = AiSettings.Load();

            EnabledBox.IsChecked = _settings.Enabled;
            PortBox.Text = _settings.Port.ToString();
            AllowWritesBox.IsChecked = _settings.AllowWrites;
            ApprovalBox.SelectedIndex = _settings.ApprovalMode switch
            {
                AiApprovalMode.readOnly => 0,
                AiApprovalMode.full => 2,
                _ => 1,
            };
            ShowTokenBox.IsChecked = false;
            TokenBox.Text = _settings.MaskedToken;
            CopyHint.Text = "";

            string exe = Environment.ProcessPath ?? "LTools.exe";
            StdioHintText.Text = $"只支持 stdio 的客户端：在上面的下拉选「stdio（命令行）」，把复制到的命令填进 command / args。" +
                                 $"桥会拉起 \"{exe}\" 并转发给本机服务（所以 LTools 要先开着、服务已启用）。";

            KnowledgePathText.Text = Path.Combine(KnowledgePack.Dir, "format.md · units.md · examples.md");
            RefreshPrompt();
            RefreshStatus();
            RefreshAudit();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// 切到本页时调用：已建页就不重复 Reload（首次建页时 Loaded 与 ApplyNavPage 只让
    /// 第一次真正 Reload）；数据真变时调用方仍直接调 <see cref="Reload"/>。
    /// </summary>
    public void RefreshIfNeeded()
    {
        if (!_loaded) Reload();
    }

    // ============ 周期刷新 ============

    private void AutoRefresh() => RefreshStatus();

    private void RefreshStatus()
    {
        bool running = McpServer.IsRunning;
        int port = McpServer.RunningPort;
        string mode = _settings.ApprovalMode switch
        {
            AiApprovalMode.readOnly => "只读",
            AiApprovalMode.full => "全部放行",
            _ => "沙箱",
        };

        EndpointText.Text = running ? $"http://127.0.0.1:{port}/mcp" : "服务未运行";
        StatusDot.Fill = (Brush)FindResource(running ? "SuccessBrush" : "HintTextBrush");

        string? error = McpServer.LastError ?? _settings.LastError;
        if (running)
        {
            StatusText.Text = $"运行中 · 审批 {mode} · 写入 {(_settings.WritesAllowed ? "开" : "关")}";
            StatusText.Foreground = (Brush)FindResource("SecondaryTextBrush");
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            StatusText.Text = "启动失败：" + error;
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
        }
        else
        {
            StatusText.Text = _settings.Enabled ? "已启用，正在启动…" : "未启用";
            StatusText.Foreground = (Brush)FindResource("HintTextBrush");
        }

        ApprovalHint.Text = _settings.ApprovalMode switch
        {
            AiApprovalMode.readOnly => "所有写工具都会拒绝（等同关闭写入）。",
            AiApprovalMode.full => "采纳 / 删除不再弹确认（仍写审计）。",
            _ => "草稿可改可跑；采纳 / 删除会弹确认框。",
        };
    }

    // ============ 连接设置 ============

    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplySettings(restart: true);
        RefreshStatus();
    }

    private void ApplySettings(bool restart)
    {
        _settings.Enabled = EnabledBox.IsChecked == true;
        _settings.AllowWrites = AllowWritesBox.IsChecked == true;
        _settings.ApprovalMode = ApprovalBox.SelectedIndex switch
        {
            0 => AiApprovalMode.readOnly,
            2 => AiApprovalMode.full,
            _ => AiApprovalMode.sandbox,
        };
        _settings.LastError = null;

        try { _settings.Save(); }
        catch (Exception ex)
        {
            ToastService.Show("保存 ai/mcp.json 失败：" + ex.Message, "AI 接入", 4000, ToastKind.error);
            return;
        }

        if (restart) RestartServer();
        RefreshPrompt();   // 端口 / 令牌变了，「让 agent 帮你连接」的说明要跟着换
    }

    private void RestartServer() => _ = Task.Run(McpServer.Apply);

    private void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port is < 1024 or > 65535)
        {
            ToastService.Show("端口要填 1024–65535 之间的数字", "AI 接入", 3000, ToastKind.error);
            PortBox.Text = _settings.Port.ToString();
            return;
        }

        if (port == _settings.Port)
        {
            ApplySettings(restart: true);
            return;
        }

        _settings.Port = port;
        ApplySettings(restart: true);
        ToastService.Show($"MCP 服务改用端口 {port}", "AI 接入", 2500, ToastKind.success);
    }

    private void ShowToken_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        TokenBox.Text = ShowTokenBox.IsChecked == true ? _settings.Token : _settings.MaskedToken;
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_settings.Token);
            CopyHint.Text = "令牌已复制";
            ToastService.Show("访问令牌已复制到剪贴板", "AI 接入", 2000, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("复制失败：" + ex.Message, "AI 接入", 3000, ToastKind.error);
        }
    }

    private void ResetToken_Click(object sender, RoutedEventArgs e)
    {
        if (!AiUi.Confirm("重置令牌", "重置后已连接的 AI 客户端会立刻断开，需要重新填新令牌。继续？")) return;

        try
        {
            _settings.ResetToken();
            TokenBox.Text = ShowTokenBox.IsChecked == true ? _settings.Token : _settings.MaskedToken;
            RestartServer();
            RefreshPrompt();
            RefreshStatus();
            ToastService.Show("令牌已重置，记得更新 AI 客户端的配置", "AI 接入", 3500, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("重置失败：" + ex.Message, "AI 接入", 3000, ToastKind.error);
        }
    }

    private void CopyConfig_Click(object sender, RoutedEventArgs e)
    {
        string kind = SelectedClientKind();
        try
        {
            Clipboard.SetText(_settings.ClientConfigJson(kind));
            CopyHint.Text = kind == "stdio" ? "命令已复制（填 command / args）" : "配置已复制";
            ToastService.Show("客户端配置已复制（记得先启用服务）", "AI 接入", 2500, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("复制失败：" + ex.Message, "AI 接入", 3000, ToastKind.error);
        }
    }

    // ============ 让 agent 帮你连接（一句提示词，直接粘给客户端） ============

    private string SelectedClientKind() =>
        (ClientKindBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "opencode";

    private void HideToken_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        RefreshPrompt();
    }

    private void RefreshPrompt()
    {
        if (PromptBox == null) return;
        PromptBox.Text = BuildPrompt(HideTokenBox.IsChecked == true);
        PromptBox.ScrollToHome();   // 换文案后别停在末尾（只读框看不到滚动条位置）
    }

    /// <summary>提示词当前是不是 stdio 版（下拉「方式」选的）。</summary>
    private bool PromptIsStdio =>
        (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string == "stdio";

    private void PromptMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PromptBox == null) return;

        bool stdio = PromptIsStdio;
        HideTokenBox.IsEnabled = !stdio;
        HideTokenBox.ToolTip = stdio ? "stdio 的提示词里没有令牌" : "截图 / 分享时勾上：令牌位置换成占位符";
        PromptHint.Text = stdio
            ? "stdio 由客户端拉起 LTools.exe --mcp-stdio，桥自己读本机令牌。"
            : "整段粘给 AI 助手即可。这段说明含访问令牌，只粘给你信任的客户端。";
        RefreshPrompt();
    }

    /// <summary>
    /// 生成「让 agent 帮你连接」的接入提示词：**只要一句**——怎么连、接上后先干什么。
    /// HTTP / stdio 各一份（下拉切换）；客户端 JSON 不塞进正文（那是「复制配置」按钮的事），
    /// 工作细节让 AI 自己读知识包。
    /// </summary>
    /// <param name="hideToken">截图 / 分享时用占位符替换令牌（只影响 HTTP 版）。</param>
    private string BuildPrompt(bool hideToken)
    {
        if (PromptIsStdio)
        {
            string exe = Environment.ProcessPath ?? "LTools.exe";
            return $"""
                帮我把 LTools 的 MCP 服务接到你的客户端（stdio），然后用它帮我写动作。

                命令："{exe}" --mcp-stdio
                （客户端的 command / args 填这个；桥自己读本机令牌，不需要 URL 和 Bearer）

                接上后先调 ltools_health；写动作前读 ltools_docs_get("format") 和 ltools_units_list，按单元定义写、别猜单元名。
                """;
        }

        string url = $"http://127.0.0.1:{_settings.Port}/mcp";
        string token = hideToken ? "<令牌>" : _settings.Token;

        return $"""
            帮我把 LTools 的 MCP 服务接到你的客户端，然后用它帮我写动作。

            地址：{url}（Streamable HTTP、无状态）
            令牌：Authorization: Bearer {token}（也接受 X-LTools-Token 或 ?token=）

            接上后先调 ltools_health；写动作前读 ltools_docs_get("format") 和 ltools_units_list，按单元定义写、别猜单元名。
            """;
    }

    private void CopyPrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PromptBox.Text ?? "");
            PromptHint.Text = "已复制，粘给 AI 即可";
            ToastService.Show("连接说明已复制", "AI 接入", 2500, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("复制失败：" + ex.Message, "AI 接入", 3000, ToastKind.error);
        }
    }

    // ============ 知识包 / 审计 ============

    private void RegenerateKnowledge_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int count = KnowledgePack.RegenerateUnits();
            ToastService.Show($"units.md 已重建（{count} 个单元）", "知识包", 2500, ToastKind.success);
        }
        catch (Exception ex)
        {
            ToastService.Show("生成失败：" + ex.Message, "知识包", 4000, ToastKind.error);
        }
    }

    private void OpenKnowledge_Click(object sender, RoutedEventArgs e)
    {
        try { KnowledgePack.Ensure(); } catch { /* 打开目录本身不依赖生成成功 */ }
        OpenPath(KnowledgePack.Dir);
    }

    private void RefreshAudit_Click(object sender, RoutedEventArgs e) => RefreshAudit();

    private void RefreshAudit()
    {
        var rows = AiAudit.TailEntries(20).Reverse().ToList();
        AuditGrid.ItemsSource = rows;
        AuditEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenAudit_Click(object sender, RoutedEventArgs e)
    {
        string path = AiAudit.FilePath;
        if (File.Exists(path)) OpenPath(path, selectFile: true);
        else OpenPath(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// 把审计表格里的滚轮转交给外层页面。DataGrid 模板内的 ScrollViewer 即使纵向 Disabled，
    /// 也会在冒泡阶段把 MouseWheel 标成 Handled（自己滚 0 像素），外层 Page_Audit 就收不到，
    /// 表现为「鼠标停在表格里页面滚不动」。隧道阶段先接管（挡住内层），再从 Page_Audit
    /// 重新发一条 MouseWheel 冒泡事件，让它的 ScrollViewer 用原生滚轮逻辑滚页面。
    /// 表格外的滚轮不经过这个处理器，照常冒泡给 Page_Audit，不受影响。
    /// </summary>
    private void AuditGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        Page_Audit.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = Page_Audit,
        });
    }

    // ============ 小工具 ============

    private static void OpenPath(string path, bool selectFile = false)
    {
        try
        {
            if (!File.Exists(path)) Directory.CreateDirectory(path);
            string args = selectFile && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开失败：" + ex.Message, "AI 接入", 3000, ToastKind.error);
        }
    }
}
