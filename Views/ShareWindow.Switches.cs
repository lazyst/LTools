using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Share;
using LTools.Features;
using LTools.Features.Units.Screen;

namespace LTools.Views;

/// <summary>ShareWindow 的「开关」部分。</summary>
public partial class ShareWindow : Window
{
    // ============ 开关 ============

    private void Toggle_Click(object sender, RoutedEventArgs e) =>
        ApplyOptions(Clone(ShareServer.Options, running: !ShareServer.IsRunning, inbound: InboundOf()));

    private void Token_Click(object sender, RoutedEventArgs e)
    {
        ShareOptions current = ShareServer.Options;
        bool required = TokenCheck.IsChecked == true;

        ShareOptions next = Clone(current, inbound: InboundOf());
        next.RequireToken = required;
        if (required && string.IsNullOrEmpty(next.Token)) next.Token = ShareOptions.NewToken();

        ApplyOptions(next);
    }

    private void Inbound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingInbound) return;
        if (InboundCombo.SelectedValue is not ShareInbound inbound) return;
        if (inbound == ShareServer.Options.Inbound) return;

        ApplyOptions(Clone(ShareServer.Options, inbound: inbound));
    }

    private void ResetToken_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(this, "重置令牌？",
                "旧地址会立刻失效，之前发出去的链接都需要重新扫码。", danger: true, okText: "重置")) return;

        ShareOptions current = ShareServer.Options;
        ApplyOptions(Clone(current, token: ShareOptions.NewToken(), inbound: InboundOf()));
    }

    /// <summary>界面上选中的「接收」档位（下拉还没回填时以配置为准）。</summary>
    private ShareInbound InboundOf() =>
        InboundCombo.SelectedValue is ShareInbound inbound ? inbound : ShareServer.Options.Inbound;

    private static ShareOptions Clone(ShareOptions source, string? token = null,
        bool? running = null, ShareInbound? inbound = null, string? inboxDir = null) => new()
    {
        Port = source.Port,
        Running = running ?? source.Running,
        Inbound = inbound ?? source.Inbound,
        InboxDir = inboxDir ?? source.InboxDir,
        MaxUploadMb = source.MaxUploadMb,
        RequireToken = source.RequireToken,
        Token = token ?? source.Token,
    };
}
