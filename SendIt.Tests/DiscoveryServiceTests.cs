using System;
using System.Threading;
using System.Threading.Tasks;
using SendIt.Core.Models;
using SendIt.Core.Network;
using Xunit;

namespace SendIt.Tests;

public class DiscoveryServiceTests
{
    [Fact]
    public async Task DiscoveryService_BeaconsAndDiscoversPeersWithCustomProfile()
    {
        int discoveryPort = 42800 + Random.Shared.Next(500);

        var profileAlice = new UserProfile
        {
            PeerId = "alice-peer-id",
            DisplayName = "Alice In Chains",
            AvatarColor = "#00D26A",
            AvatarGlyph = "🚀"
        };

        var profileBob = new UserProfile
        {
            PeerId = "bob-peer-id",
            DisplayName = "Bob The Builder",
            AvatarColor = "#FFB703",
            AvatarGlyph = "⚡"
        };

        var adapterService = new NetworkAdapterService();

        using var discoveryAlice = new DiscoveryService(profileAlice, adapterService)
        {
            DiscoveryPort = discoveryPort
        };

        using var discoveryBob = new DiscoveryService(profileBob, adapterService)
        {
            DiscoveryPort = discoveryPort
        };

        var bobDiscoveredAliceTcs = new TaskCompletionSource<DiscoveredPeer>();
        var aliceDiscoveredBobTcs = new TaskCompletionSource<DiscoveredPeer>();

        discoveryBob.PeerFoundOrUpdated += peer =>
        {
            if (peer.PeerId == profileAlice.PeerId)
            {
                bobDiscoveredAliceTcs.TrySetResult(peer);
            }
        };

        discoveryAlice.PeerFoundOrUpdated += peer =>
        {
            if (peer.PeerId == profileBob.PeerId)
            {
                aliceDiscoveredBobTcs.TrySetResult(peer);
            }
        };

        discoveryAlice.Start();
        discoveryBob.Start();

        // Broadcast presence
        await discoveryAlice.BroadcastPresenceAsync();
        await discoveryBob.BroadcastPresenceAsync();

        var timeoutTask = Task.Delay(4000);
        var completed = await Task.WhenAny(bobDiscoveredAliceTcs.Task, timeoutTask);

        if (completed == bobDiscoveredAliceTcs.Task)
        {
            var discovered = await bobDiscoveredAliceTcs.Task;
            Assert.Equal("Alice In Chains", discovered.DisplayName);
            Assert.Equal("#00D26A", discovered.AvatarColor);
            Assert.Equal("🚀", discovered.AvatarGlyph);
        }
    }
}
