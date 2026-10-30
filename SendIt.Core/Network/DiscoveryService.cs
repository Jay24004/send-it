using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SendIt.Core.Diagnostics;
using SendIt.Core.Models;

namespace SendIt.Core.Network;

public class DiscoveryService : IDisposable
{
    private readonly NetworkAdapterService _adapterService;
    private readonly ConcurrentDictionary<string, DiscoveredPeer> _peers = new();
    private readonly IPAddress _multicastAddress = IPAddress.Parse(TransferProtocolConstants.MulticastGroupIp);

    private CancellationTokenSource? _cts;
    private UdpClient? _listener;
    private Task? _listenTask;
    private Task? _beaconTask;
    private Task? _pruneTask;
    private DateTime _lastBroadcastLogTime = DateTime.MinValue;

    public event Action<DiscoveredPeer>? PeerFoundOrUpdated;
    public event Action<string>? PeerLost;
    public event Action<IReadOnlyList<DiscoveredPeer>>? PeersListChanged;

    public UserProfile CurrentProfile { get; set; }
    public int TcpListeningPort { get; set; } = TransferProtocolConstants.DefaultControlPort;
    public int DiscoveryPort { get; set; } = TransferProtocolConstants.DefaultDiscoveryPort;

    public IReadOnlyList<DiscoveredPeer> ActivePeers => _peers.Values
        .Where(p => p.IsOnline)
        .OrderBy(p => p.DisplayName)
        .ToList();

    public DiscoveryService(UserProfile profile, NetworkAdapterService adapterService)
    {
        CurrentProfile = profile;
        _adapterService = adapterService;
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            _listener = new UdpClient();
            _listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.ExclusiveAddressUse = false;
            _listener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            try
            {
                _listener.JoinMulticastGroup(_multicastAddress);
                AppLogger.Success("Discovery", $"Joined multicast discovery group {TransferProtocolConstants.MulticastGroupIp}:{DiscoveryPort}");
            }
            catch (Exception ex)
            {
                AppLogger.Security("Discovery", $"Multicast join restricted: {ex.Message}. Windows Firewall or Public Network mode may be blocking multicast.", ex);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Security("Discovery", $"Failed to bind UDP discovery port {DiscoveryPort}: {ex.Message}. Check firewall and port availability.", ex);
        }

        _listenTask = Task.Run(() => ListenLoopAsync(token), token);
        _beaconTask = Task.Run(() => BeaconLoopAsync(token), token);
        _pruneTask = Task.Run(() => PruneLoopAsync(token), token);

        // Immediate broadcast to announce presence
        _ = BroadcastPresenceAsync();
    }

    public async Task BroadcastPresenceAsync()
    {
        try
        {
            var ips = _adapterService.GetAllLocalIPv4Addresses();
            var beacon = new DiscoveryBeacon
            {
                PeerId = CurrentProfile.PeerId,
                DisplayName = CurrentProfile.DisplayName,
                AvatarColor = CurrentProfile.AvatarColor,
                AvatarGlyph = CurrentProfile.AvatarGlyph,
                DeviceName = CurrentProfile.DeviceName,
                ClientVersion = CurrentProfile.ClientVersion,
                TcpPort = TcpListeningPort,
                IpAddresses = ips
            };

            var json = JsonSerializer.Serialize(beacon);
            var bytes = Encoding.UTF8.GetBytes(json);

            using var sender = new UdpClient();
            sender.EnableBroadcast = true;

            // Broadcast to 255.255.255.255
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
            await sender.SendAsync(bytes, bytes.Length, broadcastEndpoint);

            // Multicast to 239.255.42.99
            var multicastEndpoint = new IPEndPoint(_multicastAddress, DiscoveryPort);
            await sender.SendAsync(bytes, bytes.Length, multicastEndpoint);

            // Log broadcast occasionally (not spamming every 2s)
            if ((DateTime.UtcNow - _lastBroadcastLogTime).TotalSeconds >= 15)
            {
                _lastBroadcastLogTime = DateTime.UtcNow;
                AppLogger.Info("Discovery", $"Broadcasted beacon: '{beacon.DisplayName}' ({beacon.DeviceName}) | TCP Port: {beacon.TcpPort} | IPs: [{string.Join(", ", ips)}]");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Discovery", $"Beacon broadcast error: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        AppLogger.Info("Discovery", $"Listening for peer beacons on UDP port {DiscoveryPort}...");

        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var receiveResult = await _listener.ReceiveAsync(token);
                var json = Encoding.UTF8.GetString(receiveResult.Buffer);

                DiscoveryBeacon? beacon = null;
                try
                {
                    beacon = JsonSerializer.Deserialize<DiscoveryBeacon>(json);
                }
                catch
                {
                    // Invalid packet
                    continue;
                }

                if (beacon == null || string.IsNullOrWhiteSpace(beacon.PeerId))
                    continue;

                // Ignore self
                if (beacon.PeerId == CurrentProfile.PeerId)
                    continue;

                // Ensure remote IP is in the list
                var remoteIp = receiveResult.RemoteEndPoint.Address.ToString();
                if (!beacon.IpAddresses.Contains(remoteIp) && !remoteIp.StartsWith("127."))
                {
                    beacon.IpAddresses.Insert(0, remoteIp);
                }

                bool isBrandNew = !_peers.ContainsKey(beacon.PeerId);

                var peer = _peers.AddOrUpdate(
                    beacon.PeerId,
                    _ =>
                    {
                        var newPeer = new DiscoveredPeer
                        {
                            PeerId = beacon.PeerId,
                            DisplayName = beacon.DisplayName,
                            AvatarColor = beacon.AvatarColor,
                            AvatarGlyph = beacon.AvatarGlyph,
                            DeviceName = beacon.DeviceName,
                            ClientVersion = beacon.ClientVersion,
                            TcpPort = beacon.TcpPort,
                            IpAddresses = beacon.IpAddresses,
                            LastSeenUtc = DateTime.UtcNow
                        };
                        return newPeer;
                    },
                    (_, existing) =>
                    {
                        existing.DisplayName = beacon.DisplayName;
                        existing.AvatarColor = beacon.AvatarColor;
                        existing.AvatarGlyph = beacon.AvatarGlyph;
                        existing.DeviceName = beacon.DeviceName;
                        existing.ClientVersion = beacon.ClientVersion;
                        existing.TcpPort = beacon.TcpPort;
                        existing.IpAddresses = beacon.IpAddresses;
                        existing.LastSeenUtc = DateTime.UtcNow;
                        return existing;
                    });

                if (isBrandNew)
                {
                    AppLogger.Success("Discovery", $"✨ Peer discovered: '{peer.DisplayName}' ({peer.DeviceName}) | IP: {remoteIp}:{peer.TcpPort} | IPs: [{string.Join(", ", peer.IpAddresses)}]");
                }

                PeerFoundOrUpdated?.Invoke(peer);
                PeersListChanged?.Invoke(ActivePeers);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Discovery", $"UDP beacon receive error: {ex.Message}");
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> ProbeDirectPeerAsync(string targetIp, int port = TransferProtocolConstants.DefaultControlPort, string? preferredAdapter = null)
    {
        if (string.IsNullOrWhiteSpace(targetIp) || !IPAddress.TryParse(targetIp.Trim(), out var parsedIp))
        {
            AppLogger.Warn("Discovery", $"Direct connect: Invalid IP address '{targetIp}'");
            return false;
        }

        string cleanIp = parsedIp.ToString();
        AppLogger.Info("Discovery", $"Probing direct peer at {cleanIp}:{port} (Adapter preference: {preferredAdapter ?? "All Adapters"})...");

        var adapters = _adapterService.GetActiveAdapters();
        if (!string.IsNullOrWhiteSpace(preferredAdapter) &&
            !preferredAdapter.Equals("All Adapters", StringComparison.OrdinalIgnoreCase) &&
            !preferredAdapter.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            !preferredAdapter.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            adapters = adapters.Where(a => a.Name.Equals(preferredAdapter, StringComparison.OrdinalIgnoreCase) ||
                                           a.Description.Contains(preferredAdapter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var ips = _adapterService.GetAllLocalIPv4Addresses();
        var beacon = new DiscoveryBeacon
        {
            PeerId = CurrentProfile.PeerId,
            DisplayName = CurrentProfile.DisplayName,
            AvatarColor = CurrentProfile.AvatarColor,
            AvatarGlyph = CurrentProfile.AvatarGlyph,
            DeviceName = CurrentProfile.DeviceName,
            ClientVersion = CurrentProfile.ClientVersion,
            TcpPort = TcpListeningPort,
            IpAddresses = ips
        };

        var json = JsonSerializer.Serialize(beacon);
        var bytes = Encoding.UTF8.GetBytes(json);

        // Dispatch direct unicast UDP beacon
        if (adapters.Count == 0)
        {
            try
            {
                using var sender = new UdpClient();
                await sender.SendAsync(bytes, bytes.Length, new IPEndPoint(parsedIp, DiscoveryPort));
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Discovery", $"UDP probe send failed: {ex.Message}");
            }
        }
        else
        {
            foreach (var adapter in adapters)
            {
                var localIpStr = adapter.IpAddresses.FirstOrDefault();
                if (localIpStr != null && IPAddress.TryParse(localIpStr, out var localIp))
                {
                    try
                    {
                        using var sender = new UdpClient(new IPEndPoint(localIp, 0));
                        await sender.SendAsync(bytes, bytes.Length, new IPEndPoint(parsedIp, DiscoveryPort));
                        AppLogger.Info("Discovery", $"Dispatched probe beacon via {adapter.Name} ({localIpStr}) -> {cleanIp}:{DiscoveryPort}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("Discovery", $"Failed to send probe via {adapter.Name}: {ex.Message}");
                    }
                }
            }
        }

        // Test TCP connectivity to SendIt transfer service
        bool tcpReachable = false;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(parsedIp, port, cts.Token);
            tcpReachable = true;
            AppLogger.Success("Discovery", $"Direct TCP reachability confirmed for {cleanIp}:{port}!");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Discovery", $"Direct TCP probe to {cleanIp}:{port} did not respond: {ex.Message}");
        }

        var existingPeer = _peers.Values.FirstOrDefault(p => p.IpAddresses.Contains(cleanIp));
        if (existingPeer != null)
        {
            existingPeer.LastSeenUtc = DateTime.UtcNow;
            PeerFoundOrUpdated?.Invoke(existingPeer);
            PeersListChanged?.Invoke(ActivePeers);
            return true;
        }

        if (tcpReachable)
        {
            var directPeer = new DiscoveredPeer
            {
                PeerId = $"direct_{cleanIp}",
                DisplayName = $"Direct ({cleanIp})",
                DeviceName = cleanIp,
                AvatarColor = "#00E5FF",
                AvatarGlyph = "💻",
                TcpPort = port,
                IpAddresses = [cleanIp],
                LastSeenUtc = DateTime.UtcNow
            };
            _peers[directPeer.PeerId] = directPeer;
            PeerFoundOrUpdated?.Invoke(directPeer);
            PeersListChanged?.Invoke(ActivePeers);
            return true;
        }

        return false;
    }

    private async Task BeaconLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await BroadcastPresenceAsync();
            try
            {
                await Task.Delay(2500, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PruneLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);

                var expiredKeys = _peers
                    .Where(p => !p.Value.IsOnline)
                    .Select(p => p.Key)
                    .ToList();

                bool changed = false;
                foreach (var key in expiredKeys)
                {
                    if (_peers.TryRemove(key, out var removedPeer))
                    {
                        AppLogger.Warn("Discovery", $"Peer timed out / went offline: '{removedPeer.DisplayName}' ({removedPeer.DeviceName})");
                        PeerLost?.Invoke(key);
                        changed = true;
                    }
                }

                if (changed)
                {
                    PeersListChanged?.Invoke(ActivePeers);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Discovery", $"Peer prune loop error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Dispose();
            _listener = null;
        }
        catch
        {
            // Ignore
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
