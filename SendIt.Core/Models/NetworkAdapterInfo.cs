using System.Collections.Generic;
using System.Net;

namespace SendIt.Core.Models;

public class NetworkAdapterInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = "Ethernet";
    public long SpeedBitsPerSecond { get; set; }
    public List<string> IpAddresses { get; set; } = [];
    public List<string> SubnetMasks { get; set; } = [];
    public bool IsOperational { get; set; }
    public bool IsWireless { get; set; }

    public string SpeedDisplay => SpeedBitsPerSecond switch
    {
        >= 1_000_000_000_000 => $"{SpeedBitsPerSecond / 1_000_000_000_000.0:F1} Tbps",
        >= 1_000_000_000 => $"{SpeedBitsPerSecond / 1_000_000_000.0:F1} Gbps",
        >= 1_000_000 => $"{SpeedBitsPerSecond / 1_000_000.0:F0} Mbps",
        _ => $"{SpeedBitsPerSecond / 1000.0:F0} Kbps"
    };

    public string PrimaryIp => IpAddresses.Count > 0 ? IpAddresses[0] : "0.0.0.0";

    public bool CanReach(string targetIp)
    {
        if (string.IsNullOrWhiteSpace(targetIp)) return false;
        if (!IPAddress.TryParse(targetIp, out var target)) return false;

        for (int i = 0; i < IpAddresses.Count; i++)
        {
            if (IPAddress.TryParse(IpAddresses[i], out var localIp))
            {
                var mask = IPAddress.Parse("255.255.255.0");
                if (i < SubnetMasks.Count && IPAddress.TryParse(SubnetMasks[i], out var parsedMask))
                {
                    mask = parsedMask;
                }

                if (IsInSameSubnet(localIp, target, mask))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsInSameSubnet(IPAddress ip1, IPAddress ip2, IPAddress mask)
    {
        var b1 = ip1.GetAddressBytes();
        var b2 = ip2.GetAddressBytes();
        var m = mask.GetAddressBytes();

        if (b1.Length != b2.Length || b1.Length != m.Length) return false;

        for (int i = 0; i < b1.Length; i++)
        {
            if ((b1[i] & m[i]) != (b2[i] & m[i]))
                return false;
        }
        return true;
    }
}
