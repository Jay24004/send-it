using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SendIt.Core.Models;

namespace SendIt.Core.Network;

public class NetworkAdapterService
{
    public event Action? AdaptersChanged;

    public NetworkAdapterService()
    {
        try
        {
            NetworkChange.NetworkAddressChanged += (_, _) => AdaptersChanged?.Invoke();
            NetworkChange.NetworkAvailabilityChanged += (_, _) => AdaptersChanged?.Invoke();
        }
        catch
        {
            // NetworkChange might not be supported in some restricted contexts
        }
    }

    private static bool IsVirtualAdapter(NetworkInterface ni)
    {
        string name = ni.Name.ToLowerInvariant();
        string desc = ni.Description.ToLowerInvariant();

        string[] virtualKeywords = [
            "vethernet", "hyper-v", "wsl", "virtual", "vmware", "virtualbox",
            "tap-", "tap0", "tap1", "vpn", "npcap", "pseudo", "loopback",
            "tailscale", "zerotier", "wireguard", "tunnel", "bluetooth", "p2p",
            "docker", "container", "wintun", "teamviewer", "anyconnect", "fortinet"
        ];

        return virtualKeywords.Any(k => name.Contains(k) || desc.Contains(k));
    }

    public List<NetworkAdapterInfo> GetActiveAdapters()
    {
        var physicalList = new List<NetworkAdapterInfo>();
        var allCandidateList = new List<NetworkAdapterInfo>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                // IPv4 unicast addresses only
                var ipProperties = ni.GetIPProperties();
                var unicastV4 = ipProperties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Where(u => !u.Address.ToString().StartsWith("127.") && !u.Address.ToString().StartsWith("169.254.")) // Exclude loopback and APIPA
                    .ToList();

                if (unicastV4.Count == 0)
                    continue;

                var ipv4Addresses = unicastV4.Select(u => u.Address.ToString()).ToList();
                var subnetMasks = unicastV4.Select(u => u.IPv4Mask?.ToString() ?? "255.255.255.0").ToList();

                bool isWireless = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                string typeName = isWireless ? "Wi-Fi" : (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : ni.NetworkInterfaceType.ToString());

                var adapterInfo = new NetworkAdapterInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    InterfaceType = typeName,
                    SpeedBitsPerSecond = ni.Speed > 0 ? ni.Speed : 1_000_000_000,
                    IpAddresses = ipv4Addresses,
                    SubnetMasks = subnetMasks,
                    IsOperational = true,
                    IsWireless = isWireless
                };

                allCandidateList.Add(adapterInfo);

                // Exclude virtual adapters (Hyper-V, WSL, VMware, VirtualBox, VPNs, etc.)
                if (!IsVirtualAdapter(ni))
                {
                    physicalList.Add(adapterInfo);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error scanning adapters: {ex.Message}");
        }

        // Return only physical adapters if any exist, fallback to all candidates if running purely inside a VM
        return physicalList.Count > 0 ? physicalList : allCandidateList;
    }

    public List<string> GetAllLocalIPv4Addresses()
    {
        return GetActiveAdapters()
            .SelectMany(a => a.IpAddresses)
            .Distinct()
            .ToList();
    }

    public List<NetworkAdapterInfo> GetReachableAdaptersForPeer(IReadOnlyList<string> peerIps)
    {
        var active = GetActiveAdapters();
        if (peerIps.Count == 0) return active;

        var reachable = new List<NetworkAdapterInfo>();
        foreach (var adapter in active)
        {
            if (peerIps.Any(ip => adapter.CanReach(ip)))
            {
                reachable.Add(adapter);
            }
        }

        // If subnet matching found specific adapters, return them; otherwise all adapters (e.g. routed network/VPN)
        return reachable.Count > 0 ? reachable : active;
    }

    public List<NetworkAdapterInfo> FilterAdaptersByPreference(bool useAllAdapters, string preferredAdapterName)
    {
        var active = GetActiveAdapters();
        if (active.Count == 0) return active;

        if (!useAllAdapters && !string.IsNullOrWhiteSpace(preferredAdapterName) && !preferredAdapterName.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            var match = active.FirstOrDefault(a => 
                string.Equals(a.Name, preferredAdapterName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.InterfaceType, preferredAdapterName, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return [match];
            }
        }

        return active;
    }
}
