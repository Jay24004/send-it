using System;
using System.Collections.Generic;

namespace SendIt.Core.Models;

public class DiscoveredPeer
{
    public string PeerId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string AvatarColor { get; set; } = "#00D26A";
    public string AvatarGlyph { get; set; } = "🚀";
    public string DeviceName { get; set; } = string.Empty;
    public string ClientVersion { get; set; } = "1.0.0";
    public int TcpPort { get; set; } = 42426;
    public List<string> IpAddresses { get; set; } = [];
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public bool IsOnline => (DateTime.UtcNow - LastSeenUtc).TotalSeconds < 10;

    public string FormattedAddresses => string.Join(", ", IpAddresses);
    public string Initials => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName[0].ToString().ToUpperInvariant() : "?";
}
