using System;
using System.Collections.Generic;
using System.Linq;

namespace SendIt.Core.Models;

public class AdapterTransferStat
{
    public string AdapterName { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#00E5FF";
    public long BytesTransferred { get; set; }
    public double Percentage { get; set; }
    public string FormattedBytes => TransferProgress.FormatBytes(BytesTransferred);
}

public class TransferProgress
{
    public string TransferId { get; set; } = string.Empty;
    public string PeerName { get; set; } = string.Empty;
    public string PeerAvatarColor { get; set; } = "#00D26A";
    public string PeerAvatarGlyph { get; set; } = "🚀";
    public TransferDirection Direction { get; set; }
    public TransferStatus Status { get; set; } = TransferStatus.Pending;

    public List<string> FileNames { get; set; } = [];
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }

    public long TotalChunks { get; set; }
    public long ChunksCompleted { get; set; }
    public long ChunksVerified { get; set; }

    public double SpeedBytesPerSecond { get; set; }
    public double PeakSpeedBytesPerSecond { get; set; }
    public TimeSpan EstimatedRemaining { get; set; }
    public int ActiveConnections { get; set; }
    public int ActiveNics { get; set; }

    // Multi-NIC adapter breakdown: adapter name -> bytes transferred
    public Dictionary<string, long> AdapterBytes { get; set; } = [];
    public List<AdapterTransferStat> AdapterStats { get; set; } = [];

    public string TargetDirectory { get; set; } = string.Empty;
    public string DestinationFolder { get => TargetDirectory; set => TargetDirectory = value; }
    public string? ErrorMessage { get; set; }
    public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndTimeUtc { get; set; }

    public double Percent
    {
        get => TotalBytes > 0 ? Math.Min(100.0, (TransferredBytes / (double)TotalBytes) * 100.0) : 0;
        set { }
    }

    public bool IsPending => Status == TransferStatus.Pending;
    public bool IsInProgress => Status == TransferStatus.InProgress;
    public bool IsCompleted => Status == TransferStatus.Completed;
    public bool IsFailed => Status == TransferStatus.Failed || Status == TransferStatus.Rejected || Status == TransferStatus.Cancelled;

    public bool CanCancel => Status == TransferStatus.InProgress || Status == TransferStatus.Pending;
    public bool CanOpenFolder => Status == TransferStatus.Completed && !string.IsNullOrWhiteSpace(TargetDirectory);
    public bool CanDismiss => Status == TransferStatus.Completed || Status == TransferStatus.Failed || Status == TransferStatus.Rejected || Status == TransferStatus.Cancelled;

    public string StatusBadgeText => Status switch
    {
        TransferStatus.Pending => "WAITING FOR APPROVAL",
        TransferStatus.InProgress => Direction == TransferDirection.Sent ? "SENDING" : "RECEIVING",
        TransferStatus.Completed => "COMPLETED",
        TransferStatus.Rejected => "DECLINED",
        TransferStatus.Cancelled => "CANCELLED",
        TransferStatus.Failed => "FAILED",
        _ => Status.ToString().ToUpperInvariant()
    };

    public string StatusBadgeColor => Status switch
    {
        TransferStatus.Pending => "#FFB703",     // Amber
        TransferStatus.InProgress => "#00E5FF",  // Turbo Cyan
        TransferStatus.Completed => "#10B981",   // Emerald Green
        _ => "#FF4D6D"                          // Crimson Red
    };

    public string FormattedTotalSize => FormatBytes(TotalBytes);
    public string FormattedTransferredSize => FormatBytes(TransferredBytes);
    public string FormattedSpeed => $"{FormatBytes((long)SpeedBytesPerSecond)}/s";
    public string FormattedPeakSpeed => $"{FormatBytes((long)PeakSpeedBytesPerSecond)}/s";

    public string FormattedDuration
    {
        get
        {
            var end = EndTimeUtc ?? DateTime.UtcNow;
            var duration = (end - StartTimeUtc).TotalSeconds;
            return duration switch
            {
                >= 60 => $"{(int)(duration / 60)}m {(int)(duration % 60)}s",
                _ => $"{duration:F1}s"
            };
        }
    }

    public string FormattedEta => EstimatedRemaining.TotalHours >= 1 
        ? $"{EstimatedRemaining:hh\\:mm\\:ss}" 
        : $"{EstimatedRemaining:mm\\:ss}";

    public string DisplayTitle => FileCount == 1 && FileNames.Count > 0 
        ? FileNames[0] 
        : $"{FileCount} files ({FormattedTotalSize})";

    public string SubtitleDisplay
    {
        get
        {
            if (IsPending)
            {
                return Direction == TransferDirection.Sent
                    ? $"Waiting for {PeerName} to accept..."
                    : $"Incoming request from {PeerName}";
            }

            if (IsCompleted)
            {
                var avg = (EndTimeUtc.HasValue && (EndTimeUtc.Value - StartTimeUtc).TotalSeconds > 0)
                    ? TotalBytes / (EndTimeUtc.Value - StartTimeUtc).TotalSeconds
                    : SpeedBytesPerSecond;
                return $"Completed in {FormattedDuration} • Avg {FormatBytes((long)avg)}/s • {ChunksVerified} chunks verified";
            }

            if (IsFailed)
            {
                return ErrorMessage ?? "Transfer interrupted";
            }

            string nicText = ActiveNics > 1 ? $"{ActiveNics} Adapters Multi-NIC" : "1 Adapter";
            return $"{PeerName} • {ActiveConnections} streams ({nicText}) • {ChunksVerified}/{TotalChunks} chunks";
        }
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F2} MB",
            >= 1_024 => $"{bytes / 1_024.0:F1} KB",
            _ => $"{bytes} B"
        };
    }

    public void UpdateAdapterContribution(string adapterName, long bytes)
    {
        if (string.IsNullOrWhiteSpace(adapterName)) adapterName = "Primary";
        if (AdapterBytes.TryGetValue(adapterName, out long current))
        {
            AdapterBytes[adapterName] = current + bytes;
        }
        else
        {
            AdapterBytes[adapterName] = bytes;
        }

        // Recompute stats with assigned palette colors
        string[] palette = ["#00E5FF", "#B366FF", "#FFB703", "#FF007F", "#10B981"];
        long total = Math.Max(1, TransferredBytes);
        int colorIdx = 0;

        var list = new List<AdapterTransferStat>();
        foreach (var (k, v) in AdapterBytes)
        {
            list.Add(new AdapterTransferStat
            {
                AdapterName = k,
                ColorHex = palette[colorIdx % palette.Length],
                BytesTransferred = v,
                Percentage = Math.Min(100.0, (v / (double)total) * 100.0)
            });
            colorIdx++;
        }
        AdapterStats = list;
    }
}
