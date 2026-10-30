using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SendIt.Core.Models;

public static class TransferProtocolConstants
{
    public const uint MagicHeader = 0x54444E53; // "SNDT" in little-endian
    public const byte PacketTypeChunk = 0x01;
    public const byte PacketTypeAck = 0x02;
    public const byte PacketTypeNack = 0x03;
    public const byte PacketTypeComplete = 0x04;
    public const byte PacketTypeCancel = 0x05;

    public const int DefaultControlPort = 42426;
    public const int DefaultDiscoveryPort = 42425;
    public const string MulticastGroupIp = "239.255.42.99";

    public const int DefaultChunkSize = 2 * 1024 * 1024; // 2 MB
}

public class DiscoveryBeacon
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "SENDIT_BEACON";

    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("avatarColor")]
    public string AvatarColor { get; set; } = "#00D26A";

    [JsonPropertyName("avatarGlyph")]
    public string AvatarGlyph { get; set; } = "🚀";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = "1.0.0";

    [JsonPropertyName("tcpPort")]
    public int TcpPort { get; set; } = TransferProtocolConstants.DefaultControlPort;

    [JsonPropertyName("ipAddresses")]
    public List<string> IpAddresses { get; set; } = [];
}

public class TransferRequestMessage
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("sender")]
    public UserProfile Sender { get; set; } = new();

    [JsonPropertyName("files")]
    public List<FileManifestItem> Files { get; set; } = [];

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("chunkSize")]
    public int ChunkSize { get; set; } = TransferProtocolConstants.DefaultChunkSize;

    [JsonPropertyName("totalChunks")]
    public long TotalChunks { get; set; }

    [JsonPropertyName("senderEndpoints")]
    public List<string> SenderEndpoints { get; set; } = [];
}

public class TransferResponseMessage
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("receiverEndpoints")]
    public List<string> ReceiverEndpoints { get; set; } = [];

    [JsonPropertyName("receiverPort")]
    public int ReceiverPort { get; set; } = TransferProtocolConstants.DefaultControlPort;
}

public class TransferFinalizeMessage
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
