using System;
using System.Text.Json.Serialization;

namespace SendIt.Core.Models;

public class UserProfile
{
    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = Guid.NewGuid().ToString("N")[..12];

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = Environment.UserName;

    [JsonPropertyName("avatarColor")]
    public string AvatarColor { get; set; } = "#00D26A"; // Emerald

    [JsonPropertyName("avatarGlyph")]
    public string AvatarGlyph { get; set; } = "🚀";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = Environment.MachineName;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "1.0.0";
}
