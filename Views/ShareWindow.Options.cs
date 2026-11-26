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

/// <summary>ShareWindow 的「设置（就地改）」部分。</summary>
public partial class ShareWindow : Window
{
    // ============ 设置：就地改 ============

    private void PortBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitPort(notify: true);
            ComposeBox.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PortBox.Text = ShareServer.Options.Port.ToString();
            ComposeBox.Focus();
        }
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e) => CommitPort(notify: false);

    private void UploadBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitUploadLimit(notify: true);
            ComposeBox.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            UploadBox.Text = ShareServer.Options.MaxUploadMb.ToString();
            ComposeBox.Focus();
        }
    }

    private void UploadBox_LostFocus(object sender, RoutedEventArgs e) => CommitUploadLimit(notify: false);

    private void CommitPort(bool notify)
    {
        ShareOptions current = ShareServer.Options;
        string text = PortBox.Text.Trim();
        if (text == current.Port.ToString()) return;

        if (!int.TryParse(text, out int port) || port is < 1024 or > 65535)
        {
            PortBox.Text = current.Port.ToString();
            if (notify) ShowSettingsHint("端口要填 1024–65535 之间的整数");
            return;
        }

        ShareOptions next = Clone(current);
        next.Port = port;
        ApplyOptions(next);

        if (notify) ToastService.Show($"端口改成 {port}", null, 1800, ToastKind.success);
    }

    private void CommitUploadLimit(bool notify)
    {
        ShareOptions current = ShareServer.Options;
        string text = UploadBox.Text.Trim();
        if (text == current.MaxUploadMb.ToString()) return;

        if (!int.TryParse(text, out int mb) || mb is < 1 or > 4096)
        {
            UploadBox.Text = current.MaxUploadMb.ToString();
            if (notify) ShowSettingsHint("上传上限要填 1–4096 之间的整数（MB）");
            return;
        }

        ShareOptions next = Clone(current);
        next.MaxUploadMb = mb;
        ApplyOptions(next);

        if (notify) ToastService.Show($"上传上限改成 {mb} MB", null, 1800, ToastKind.success);
    }

    private void ApplyOptions(ShareOptions next)
    {
        ShareServer.Apply(next);
        SettingsStore.Modify(file => file.Share = next);
        Refresh();
    }

    private void ShowSettingsHint(string message)
    {
        SettingsHint.Text = message;
        SettingsHint.Visibility = Visibility.Visible;

        _hintTimer?.Stop();
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer?.Stop();
            SettingsHint.Visibility = Visibility.Collapsed;
        };
        _hintTimer.Start();
    }

    private sealed class ShareItemRow
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Glyph { get; init; } = "件";
        public string Meta { get; init; } = "";
        public string Body { get; init; } = "";
        public string Tip { get; init; } = "";
        public string Action1 { get; init; } = "";
        public string Action1Tip { get; init; } = "";
        public string Action2 { get; init; } = "";
        public string Action2Tip { get; init; } = "";
        public Visibility Action2Visibility { get; init; } = Visibility.Visible;
        public Visibility SaveVisibility { get; init; } = Visibility.Collapsed;
        public bool IsText { get; init; }
        public bool Stale { get; init; }
        public bool IsInbox { get; init; }
    }

    private sealed class ShareUrlRow
    {
        public int Index { get; init; }
        public string Url { get; init; } = "";
        public string Token { get; init; } = "";
        public string Full { get; init; } = "";
        public bool Selected { get; init; }
    }

    /// <summary>文字条目铺正文（方案 B），其它还是单行。</summary>
    private sealed class ShareRowSelector : DataTemplateSelector
    {
        public DataTemplate Plain { get; init; } = null!;
        public DataTemplate Text { get; init; } = null!;

        public override DataTemplate SelectTemplate(object item, DependencyObject container) =>
            item is ShareItemRow { IsText: true } ? Text : Plain;
    }
}
