using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HitCam.Desktop.Services;

public static class LanAddresses
{
    // Adapters a phone on the home Wi-Fi can't reach even though they have an IPv4 address.
    private static readonly string[] VirtualMarkers =
    [
        "virtual", "hyper-v", "vpn", "tun", "tap-", "wintun", "wireguard", "radmin", "zerotier",
        "tailscale", "hamachi", "vmware", "virtualbox", "loopback", "bluetooth",
    ];

    /// <summary>
    /// IPv4 addresses a phone on the same network can reach, best first: physical Ethernet/Wi-Fi adapters
    /// with a default gateway (the real LAN) before VPN, TUN and other virtual adapters.
    /// </summary>
    public static IReadOnlyList<IPAddress> Get()
    {
        var candidates = new List<(IPAddress Address, int Rank)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            var properties = nic.GetIPProperties();
            var hasGateway = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                                  && !g.Address.Equals(IPAddress.Any));
            var physical = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                               or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                           && !VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase)
                                                       || nic.Name.Contains(m, StringComparison.OrdinalIgnoreCase));

            foreach (var unicast in properties.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) || IsLinkLocal(address))
                    continue;
                var rank = (physical ? 0 : 4) + (hasGateway ? 0 : 2) + (IsPrivate(address) ? 0 : 1);
                candidates.Add((address, rank));
            }
        }

        return candidates.OrderBy(c => c.Rank).Select(c => c.Address).Distinct().ToArray();
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
