using System;
using System.IO;
using SendIt.Core.Models;
using SendIt.Core.Storage;
using Xunit;

namespace SendIt.Tests;

public class SettingsAndHistoryTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsAndHistoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SendIt_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    [Fact]
    public void SettingsManager_SaveAndLoad_PreservesCustomProfileAndSettings()
    {
        string settingsFile = Path.Combine(_tempDir, "settings.json");
        var manager = new SettingsManager(settingsFile);

        var settings = manager.CurrentSettings;
        settings.Profile.DisplayName = "CyberNinja";
        settings.Profile.AvatarColor = "#9D4EDD";
        settings.Profile.AvatarGlyph = "⚡";
        settings.AutoApprove = true;
        settings.AskDownloadLocationEveryTime = true;
        settings.AutoDeleteHistoryDays = 30;
        settings.ParallelStreams = 12;

        manager.Save(settings);

        // Reload fresh instance from disk
        var reloadedManager = new SettingsManager(settingsFile);
        var loaded = reloadedManager.CurrentSettings;

        Assert.Equal("CyberNinja", loaded.Profile.DisplayName);
        Assert.Equal("#9D4EDD", loaded.Profile.AvatarColor);
        Assert.Equal("⚡", loaded.Profile.AvatarGlyph);
        Assert.True(loaded.AutoApprove);
        Assert.True(loaded.AskDownloadLocationEveryTime);
        Assert.Equal(30, loaded.AutoDeleteHistoryDays);
        Assert.Equal(12, loaded.ParallelStreams);
    }

    [Fact]
    public void TransferHistoryManager_PruneOlderThan_RemovesExpiredItems()
    {
        string historyFile = Path.Combine(_tempDir, "history.json");
        var manager = new TransferHistoryManager(historyFile);

        // Add 3 items: one recent, one 10 days ago, one 40 days ago
        var recent = new TransferHistoryItem
        {
            Id = "item-recent",
            PeerName = "Alice",
            TimestampUtc = DateTime.UtcNow.AddHours(-2),
            TotalBytes = 1024,
            Status = TransferStatus.Completed
        };

        var old10Days = new TransferHistoryItem
        {
            Id = "item-10days",
            PeerName = "Bob",
            TimestampUtc = DateTime.UtcNow.AddDays(-10),
            TotalBytes = 2048,
            Status = TransferStatus.Completed
        };

        var old40Days = new TransferHistoryItem
        {
            Id = "item-40days",
            PeerName = "Charlie",
            TimestampUtc = DateTime.UtcNow.AddDays(-40),
            TotalBytes = 4096,
            Status = TransferStatus.Completed
        };

        manager.Add(recent);
        manager.Add(old10Days);
        manager.Add(old40Days);

        Assert.Equal(3, manager.Items.Count);

        // Prune older than 7 days -> should remove old10Days and old40Days
        int pruned = manager.PruneOlderThan(7);
        Assert.Equal(2, pruned);
        Assert.Single(manager.Items);
        Assert.Equal("item-recent", manager.Items[0].Id);
    }

    [Fact]
    public void TransferHistoryManager_PruneWithZero_NeverDeletes()
    {
        string historyFile = Path.Combine(_tempDir, "history.json");
        var manager = new TransferHistoryManager(historyFile);

        var oldItem = new TransferHistoryItem
        {
            Id = "item-old",
            PeerName = "Dave",
            TimestampUtc = DateTime.UtcNow.AddDays(-90),
            TotalBytes = 5000,
            Status = TransferStatus.Completed
        };
        manager.Add(oldItem);

        int pruned = manager.PruneOlderThan(0); // 0 means Never
        Assert.Equal(0, pruned);
        Assert.Single(manager.Items);
    }
}
