using System;
using System.IO;
using System.Text.Json.Serialization;

namespace SendIt.Core.Models;

public class AppSettings
{
    [JsonPropertyName("profile")]
    public UserProfile Profile { get; set; } = new();

    [JsonPropertyName("autoApprove")]
    public bool AutoApprove { get; set; } = false;

    [JsonPropertyName("defaultDownloadLocation")]
    public string DefaultDownloadLocation { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "SendIt"
    );

    [JsonPropertyName("askDownloadLocationEveryTime")]
    public bool AskDownloadLocationEveryTime { get; set; } = false;

    /// <summary>
    /// Retention period in days for transfer history: 7, 30, or 0 (Keep forever / Never delete)
    /// </summary>
    [JsonPropertyName("autoDeleteHistoryDays")]
    public int AutoDeleteHistoryDays { get; set; } = 7;

    [JsonPropertyName("parallelStreams")]
    public int ParallelStreams { get; set; } = 8;

    [JsonPropertyName("chunkSizeMb")]
    public int ChunkSizeMb { get; set; } = 2;

    [JsonPropertyName("useAllNetworkAdapters")]
    public bool UseAllNetworkAdapters { get; set; } = true;

    [JsonPropertyName("preferredAdapterName")]
    public string PreferredAdapterName { get; set; } = "Auto";

    [JsonPropertyName("tcpPort")]
    public int TcpPort { get; set; } = 42426;

    [JsonPropertyName("discoveryPort")]
    public int DiscoveryPort { get; set; } = 42425;
}
