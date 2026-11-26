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

    private void Toggle_Click(object sender, RoutedEventArgs e) => ApplyOptions(toggleRunning: true);

    private void Token_Click(object sender, RoutedEventArgs e) => ApplyOptions();

    private void Inbound_Click(object sender, RoutedEventArgs e) => ApplyOptions();

    private void ResetToken_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(this, "重置令牌？",
                "旧地址会立刻失效，之前发出去的链接都需要重新扫码。", danger: true, okText: "重置")) return;

        ShareOptions current = ShareServer.Options;
        var options = Clone(current, token: ShareOptions.NewToken());
        ShareServer.Apply(options);
        SettingsStore.Modify(file => file.Share = options);
        Refresh();
    }

    private void ApplyOptions(bool toggleRunning = false)
    {
        ShareOptions current = ShareServer.Options;
        bool running = toggleRunning ? !ShareServer.IsRunning : current.Running;
        bool needToken = TokenCheck.IsChecked == true;

        var options = new ShareOptions
        {
            Port = current.Port,
            Running = running,
            AllowInbound = InboundCheck.IsChecked == true,
            MaxUploadMb = current.MaxUploadMb,
            RequireToken = needToken,
            Token = current.Token,
        };

        if (needToken && string.IsNullOrEmpty(options.Token)) options.Token = ShareOptions.NewToken();

        ShareServer.Apply(options);
        SettingsStore.Modify(file => file.Share = options);
        Refresh();
    }

    private static ShareOptions Clone(ShareOptions source, string? token = null) => new()
    {
        Port = source.Port,
        Running = source.Running,
        AllowInbound = source.AllowInbound,
        MaxUploadMb = source.MaxUploadMb,
        RequireToken = source.RequireToken,
        Token = token ?? source.Token,
    };

}
