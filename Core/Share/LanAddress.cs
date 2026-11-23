using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LTools.Core.Share;

/// <summary>一块可用的局域网 IPv4 地址（一块网卡可能有多块地址）。</summary>
internal sealed class LanEndpoint
{
    public string Address { get; init; } = "";
    public string Name { get; init; } = "";
    public int PrefixLength { get; init; } = 24;
    public bool HasGateway { get; init; }

    public string Url(int port) => $"http://{Address}:{port}/";

    /// <summary>
    /// 来源是否属于这块地址所在子网。Kestrel 只绑 `0.0.0.0`（IPv4），所以只处理 IPv4；
    /// IPv4-mapped IPv6 先转回来。
    /// </summary>
    public bool Contains(IPAddress? remote)
    {
        if (remote == null) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;

        try
        {
            return IPNetwork.Parse($"{Address}/{PrefixLength}").Contains(remote);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 枚举本机所有 IPv4 地址，口径照 LocalSend（Dart <c>NetworkInterface.list</c>）：
/// **除回环与 169.254 链接本地外全部列出**，虚拟网卡 / VPN / 隧道网卡也算一块。
/// </summary>
/// <remarks>
/// <para>
/// 不按「有没有默认网关」或网卡名筛：用户装了 VMware / Hyper-V / Tailscale / 代理 TUN 时，
/// 每块网卡都可能是一部手机真正能连到的入口，漏列就是「手机连不上」。窗口把每一条都列出来，
/// 让用户自己扫对的那张二维码。
/// </para>
/// <para>
/// 排序只影响「二维码默认画哪一条」：有网关的在前，其次 192.168 &gt; 10 &gt; 其它。
/// 共享服务的入站校验认**每一块**地址所在子网（<see cref="ShareServer.Endpoints"/>）。
/// </para>
/// </remarks>
internal static class LanAddress
{
    public static IReadOnlyList<LanEndpoint> Candidates()
    {
        var list = new List<LanEndpoint>();

        IEnumerable<NetworkInterface> interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return list; }

        foreach (var ni in interfaces)
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                // 不看 OperationalStatus：LocalSend/Dart 也只按「网卡上有没有地址」列，
                // 拔掉的网线只要地址还在就仍是一条可用入口（插回去就能连）。
                IPInterfaceProperties props = ni.GetIPProperties();
                bool gateway = HasGateway(props);

                foreach (var ua in props.UnicastAddresses)
                {
                    if (!IsListable(ua.Address)) continue;
                    if (ua.PrefixLength is <= 0 or > 32) continue;

                    list.Add(new LanEndpoint
                    {
                        Address = ua.Address.ToString(),
                        Name = ni.Name,
                        PrefixLength = ua.PrefixLength,
                        HasGateway = gateway,
                    });
                }
            }
            catch
            {
                // 单块网卡读属性失败不影响其它网卡（虚拟网卡常见）。
            }
        }

        return list
            .GroupBy(e => e.Address, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderByDescending(e => e.HasGateway)
            .ThenByDescending(e => Rank(e.Address))
            .ThenBy(e => e.Address, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 这条地址列不列进清单：IPv4、非回环、非链接本地（169.254/16，DHCP 没拿到地址时的临时地址）。
    /// </summary>
    internal static bool IsListable(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        if (IPAddress.IsLoopback(ip)) return false;

        byte[] bytes = ip.GetAddressBytes();
        return !(bytes[0] == 169 && bytes[1] == 254);
    }

    /// <summary>地址优先级：192.168 &gt; 10 &gt; 其它（172.16–31 之类的企业网排最后）。</summary>
    internal static int Rank(string address) =>
        address.StartsWith("192.168.", StringComparison.Ordinal) ? 3
        : address.StartsWith("10.", StringComparison.Ordinal) ? 2
        : 1;

    private static bool HasGateway(IPInterfaceProperties props)
    {
        try
        {
            foreach (var g in props.GatewayAddresses)
            {
                if (g.Address == null) continue;
                if (g.Address.Equals(IPAddress.Any) || g.Address.Equals(IPAddress.IPv6Any)) continue;
                return true;
            }
        }
        catch
        {
            // 部分虚拟网卡读网关会抛，按没有网关处理。
        }
        return false;
    }
}
