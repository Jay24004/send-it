using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SendIt.Core.Models;

public enum TransferDirection
{
    Sent,
    Received
}

public enum TransferStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled,
    Rejected
}

public class TransferHistoryItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("peerId")]
    public string PeerId { get; set; } = string.Empty;

    [JsonPropertyName("peerName")]
    public string PeerName { get; set; } = string.Empty;

    [JsonPropertyName("direction")]
    public TransferDirection Direction { get; set; }

    [JsonPropertyName("fileNames")]
    public List<string> FileNames { get; set; } = [];

    [JsonPropertyName("fileCount")]
    public int FileCount { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("transferredBytes")]
    public long TransferredBytes { get; set; }

    [JsonPropertyName("status")]
    public TransferStatus Status { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("targetDirectory")]
    public string TargetDirectory { get; set; } = string.Empty;

    [JsonPropertyName("timestampUtc")]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("durationSeconds")]
    public double DurationSeconds { get; set; }

    [JsonPropertyName("averageSpeedBps")]
    public double AverageSpeedBps { get; set; }

    [JsonIgnore]
    public string FormattedTotalSize => FormatBytes(TotalBytes);

    [JsonIgnore]
    public string FormattedSpeed => $"{FormatBytes((long)AverageSpeedBps)}/s";

    [JsonIgnore]
    public string SummaryText => FileCount == 1 
        ? (FileNames.Count > 0 ? FileNames[0] : "1 file")
        : $"{FileCount} files ({FormattedTotalSize})";

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }
}
