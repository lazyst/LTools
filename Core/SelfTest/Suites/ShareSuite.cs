using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using LTools.Core.Share;

namespace LTools.Core.SelfTest;

/// <summary>
/// 局域网共享的纯逻辑：文件名清洗、MIME、清单、目录重名、配置归一、子网判断。
/// 不碰 Kestrel / WPF——真服务与页面靠 <c>tests/ShareSmoke</c> 与手点。
/// </summary>
internal static class ShareSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("share", "安全文件名：非法字符 / 保留名 / 空 / 限长", SafeNames));
        cases.Add(new("share", "URL 名字只认纯文件名", PlainNames));
        cases.Add(new("share", "MIME 推断", Mimes));
        cases.Add(new("share", "目录重名加 (2)", UniquePaths));
        cases.Add(new("share", "清单：原地添加 / 持久化 / 移除不动原文件", Items));
        cases.Add(new("share", "文字落盘进 files/ 并加清单", Texts));
        cases.Add(new("share", "对方发来的文字只进 inbox/，不进清单", InboxTexts));
        cases.Add(new("share", "配置归一：端口 / 上限 / 令牌形状", Options));
        cases.Add(new("share", "「接收」三档：选项文案 / 落盘用英文键", InboundModes));
        cases.Add(new("share", "子网判断与地址优先级", Subnets));
    }

    private static void SafeNames()
    {
        Check.Equal("abc.png", ShareStore.SafeName("abc.png"), "普通名不变");
        Check.Equal("b.png", ShareStore.SafeName("a\\b.png"), "路径被剥成文件名");
        Check.Equal("ab.png", ShareStore.SafeName("a<b>.png"), "非法字符去掉");
        Check.Equal("_CON", ShareStore.SafeName("CON"), "保留名加前缀");
        Check.Equal("file", ShareStore.SafeName("   "), "空名兜底");
        Check.Equal("file", ShareStore.SafeName("..."), "全是点也兜底");
        Check.Equal(120, ShareStore.SafeName(new string('a', 200)).Length, "限长 120");
    }

    private static void PlainNames()
    {
        Check.True(ShareStore.IsPlainName("a.png"), "普通名");
        Check.False(ShareStore.IsPlainName("../a.png"), "带 .. 拒绝");
        Check.False(ShareStore.IsPlainName("a/b.png"), "带斜杠拒绝");
        Check.False(ShareStore.IsPlainName("C:a.png"), "带冒号拒绝");
        Check.False(ShareStore.IsPlainName(""), "空拒绝");
        Check.False(ShareStore.IsPlainName("."), "单点拒绝");
        Check.False(ShareStore.IsPlainName(null), "null 拒绝");
    }

    private static void Mimes()
    {
        Check.Equal("image/png", ShareStore.MimeFor("a.PNG"), "大小写不敏感");
        Check.Equal("video/mp4", ShareStore.MimeFor("a.mp4"), "视频");
        Check.Equal("application/octet-stream", ShareStore.MimeFor("a.xyz"), "未知兜底");
        Check.Equal("application/octet-stream", ShareStore.MimeFor("noext"), "无扩展名兜底");
    }

    private static void UniquePaths()
    {
        string dir = TempDir();
        try
        {
            string first = ShareStore.UniquePath(dir, "a.txt");
            File.WriteAllText(first, "1");
            string second = ShareStore.UniquePath(dir, "a.txt");
            Check.Equal("a (2).txt", Path.GetFileName(second), "第二个加序号");
            File.WriteAllText(second, "2");
            Check.Equal("a (3).txt", Path.GetFileName(ShareStore.UniquePath(dir, "a.txt")), "第三个继续加");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static void Items()
    {
        string root = TempDir();
        try
        {
            var store = new ShareStore(root);
            store.EnsureLayout();
            string src = Path.Combine(root, "src.txt");
            File.WriteAllText(src, "hello");

            ShareItem item = store.AddPath(src);
            Check.False(item.Copied, "默认原地引用，不复制");
            Check.Equal(5L, item.Size, "大小");
            Check.Equal("text/plain; charset=utf-8", item.Mime, "MIME");
            Check.True(item.Exists, "原文件还在");
            Check.Equal(8, item.Id.Length, "Id 是 8 位短 id");

            var reopened = new ShareStore(root);
            Check.Equal(1, reopened.Items().Count, "清单落盘后可读回");
            Check.Equal(item.Id, reopened.Items()[0].Id, "Id 不变");

            // 原文件没了 → Exists 变假，但清单还在（界面据此标「已失效」）
            File.Delete(src);
            Check.False(reopened.Find(item.Id)!.Exists, "原文件删掉后标记失效");

            Check.True(store.Remove(item.Id), "移除成功");
            Check.Equal(0, store.Items().Count, "清单空了");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void Texts()
    {
        string root = TempDir();
        try
        {
            var store = new ShareStore(root);
            ShareItem item = store.AddText("多行\n文字");

            Check.True(item.Copied, "文字是副本");
            Check.True(item.Path.StartsWith(store.FilesDir, StringComparison.OrdinalIgnoreCase), "落在 files/");
            Check.Equal("多行\n文字", File.ReadAllText(item.Path), "内容一致");
            Check.True(item.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), "扩展名是 .txt");

            Check.True(store.Remove(item.Id), "移除成功");
            Check.False(File.Exists(item.Path), "副本文件跟着删掉");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void InboxTexts()
    {
        string root = TempDir();
        try
        {
            var store = new ShareStore(root);
            store.EnsureLayout();

            string path = store.AddInboxText("手机发来的");
            Check.True(path.StartsWith(store.InboxDir, StringComparison.OrdinalIgnoreCase), "落在 inbox/");
            Check.True(File.Exists(path), "文件写出来了");
            Check.Equal("手机发来的", File.ReadAllText(path), "内容一致");
            Check.Equal(0, store.Items().Count, "不进本机发出去的清单");
            Check.Equal(1, store.Inbox().Count, "「收到的」里能看到");
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static void Options()
    {
        Check.Equal(ShareOptions.DefaultPort,
            new ShareOptions { Port = 80 }.Normalized().Port, "端口越界回落默认");
        Check.Equal(4096, new ShareOptions { MaxUploadMb = 99999 }.Normalized().MaxUploadMb, "上限钳制");
        Check.Equal(1, new ShareOptions { MaxUploadMb = 0 }.Normalized().MaxUploadMb, "下限钳制");

        Check.Equal("abcdef0123456789",
            new ShareOptions { Token = "ABCDEF0123456789" }.Normalized().Token, "令牌转小写");

        string fixedToken = new ShareOptions { Token = "xx" }.Normalized().Token;
        Check.Equal(ShareOptions.TokenHexLength, fixedToken.Length, "形状不对换新的");
        Check.True(fixedToken.All(Uri.IsHexDigit), "新令牌是十六进制");

        Check.Equal("", new ShareOptions { Token = "" }.Normalized().Token, "空令牌保持空（不偷偷生成）");

        string fresh = ShareOptions.NewToken();
        Check.Equal(16, fresh.Length, "新令牌 16 位");
        Check.True(fresh.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "小写十六进制");
    }

    private static void InboundModes()
    {
        // 三档的键就是配置里存的字（英文），中文名只在界面上用。
        Check.Equal("allow", ShareInbound.allow.ToString(), "默认档的键");
        Check.Equal("confirm", ShareInbound.confirm.ToString(), "需确认的键");
        Check.Equal("block", ShareInbound.block.ToString(), "不接受的键");

        IReadOnlyList<KeyValuePair<ShareInbound, string>> choices = ShareOptions.InboundChoices;
        Check.Equal(3, choices.Count, "下拉三项");
        Check.Equal("自动接收", choices[0].Value, "第一项文案");
        Check.Equal("需要确认", choices[1].Value, "第二项文案");
        Check.Equal("不接受", choices[2].Value, "第三项文案");
        Check.True(choices.All(c => Enum.IsDefined(c.Key)), "选项键都是合法枚举值");

        foreach (ShareInbound mode in Enum.GetValues<ShareInbound>())
        {
            Check.True(ShareOptions.DescribeInbound(mode).Length > 0, mode + " 有一句说明");
            Check.True(ShareOptions.LabelInbound(mode).Length > 0, mode + " 有日志短名");
        }

        Check.Equal(ShareInbound.allow, new ShareOptions().Inbound, "默认直接收（升级后老配置不动也不拦）");
        Check.Equal(ShareInbound.confirm,
            new ShareOptions { Inbound = ShareInbound.confirm }.Normalized().Inbound, "归一保留档位");
        Check.Equal(ShareInbound.allow,
            new ShareOptions { Inbound = (ShareInbound)99 }.Normalized().Inbound, "越界的档位回落自动接收");

        // settings.json 里存的是英文键（不是 0/1/2），与 LTools.example.json 一致。
        Check.Equal("\"confirm\"", JsonSerializer.Serialize(ShareInbound.confirm), "落盘是英文键");
        Check.Equal(ShareInbound.block, JsonSerializer.Deserialize<ShareInbound>("\"block\""), "读回英文键");
    }

    private static void Subnets()
    {
        var endpoint = new LanEndpoint { Address = "192.168.1.7", PrefixLength = 24 };

        Check.True(endpoint.Contains(IPAddress.Parse("192.168.1.9")), "同子网");
        Check.False(endpoint.Contains(IPAddress.Parse("192.168.2.9")), "不同子网");
        Check.False(endpoint.Contains(IPAddress.Parse("10.0.0.1")), "另一网段");
        Check.True(endpoint.Contains(IPAddress.Parse("::ffff:192.168.1.9")), "IPv4-mapped 转回来");
        Check.False(endpoint.Contains(null), "空来源");

        Check.True(new LanEndpoint { Address = "10.0.1.5", PrefixLength = 16 }
            .Contains(IPAddress.Parse("10.0.2.9")), "公司 /16 网段");

        Check.Equal(3, LanAddress.Rank("192.168.1.7"), "192.168 最高");
        Check.Equal(2, LanAddress.Rank("10.1.2.3"), "10 次之");
        Check.Equal(1, LanAddress.Rank("172.16.0.1"), "企业网最后");

        // 列清单的口径照 LocalSend：除回环与链接本地外全列，虚拟网卡 / VPN 也算
        Check.True(LanAddress.IsListable(IPAddress.Parse("192.168.110.31")), "家里的 WLAN 地址");
        Check.True(LanAddress.IsListable(IPAddress.Parse("192.168.100.1")), "VMware 虚拟网卡也算一块");
        Check.True(LanAddress.IsListable(IPAddress.Parse("100.82.124.82")), "Tailscale 地址也算一块");
        Check.True(LanAddress.IsListable(IPAddress.Parse("198.18.0.1")), "代理 TUN 地址也算一块");
        Check.False(LanAddress.IsListable(IPAddress.Parse("127.0.0.1")), "回环不列");
        Check.False(LanAddress.IsListable(IPAddress.Parse("169.254.122.42")), "169.254 链接本地不列");
        Check.False(LanAddress.IsListable(IPAddress.Parse("::1")), "IPv6 回环不列");
        Check.False(LanAddress.IsListable(IPAddress.Parse("fe80::1")), "IPv6 链路本地不列");
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ltools-share-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 测试目录，删不掉不影响结果 */ }
    }
}
