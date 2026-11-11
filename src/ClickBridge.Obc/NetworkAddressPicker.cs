using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClickBridge.Obc;

/// <summary>Picks the IPv4 address of the real LAN interface (skipping VPNs, Hyper-V/WSL switches, etc.).</summary>
public static class NetworkAddressPicker
{
    private static readonly string[] VirtualMarkers =
        ["virtual", "vethernet", "vmware", "virtualbox", "hyper-v", "wsl", "loopback", "bluetooth", "vpn", "tap-", "tailscale", "zerotier"];

    public static IReadOnlyList<(IPAddress Address, string Interface, bool HasGateway)> Candidates()
    {
        var result = new List<(IPAddress, string, bool)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var label = $"{nic.Name} ({nic.Description})";
            if (VirtualMarkers.Any(m => label.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                                              && !g.Address.Equals(IPAddress.Any));
            foreach (var unicast in props.UnicastAddresses)
            {
                var address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IsLinkLocal(address))
                {
                    result.Add((address, label, hasGateway));
                }
            }
        }

        return result;
    }

    /// <summary>Best candidate (interfaces with a default gateway first), or null when offline.</summary>
    public static IPAddress? Pick() =>
        Candidates().OrderByDescending(c => c.HasGateway).Select(c => c.Address).FirstOrDefault();

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
