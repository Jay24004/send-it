using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SendIt.Core.Models;

namespace SendIt.Core.Storage;

public class TransferHistoryManager
{
    private readonly string _historyFilePath;
    private readonly object _lock = new();
    private List<TransferHistoryItem> _items = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public event Action? HistoryUpdated;

    public IReadOnlyList<TransferHistoryItem> Items
    {
        get
        {
            lock (_lock)
            {
                return _items.OrderByDescending(x => x.TimestampUtc).ToList();
            }
        }
    }

    public TransferHistoryManager(string? customPath = null)
    {
        if (string.IsNullOrWhiteSpace(customPath))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appData, "SendIt");
            Directory.CreateDirectory(folder);
            _historyFilePath = Path.Combine(folder, "history.json");
        }
        else
        {
            _historyFilePath = customPath;
        }

        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_historyFilePath))
                {
                    var json = File.ReadAllText(_historyFilePath);
                    var loaded = JsonSerializer.Deserialize<List<TransferHistoryItem>>(json);
                    if (loaded != null)
                    {
                        _items = loaded;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load transfer history: {ex.Message}");
            }

            _items = [];
        }
    }

    public void Add(TransferHistoryItem item)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.Id == item.Id);
            _items.Insert(0, item);
            SaveInternal();
        }
        HistoryUpdated?.Invoke();
    }

    public void Update(TransferHistoryItem item)
    {
        lock (_lock)
        {
            var index = _items.FindIndex(x => x.Id == item.Id);
            if (index >= 0)
            {
                _items[index] = item;
            }
            else
            {
                _items.Insert(0, item);
            }
            SaveInternal();
        }
        HistoryUpdated?.Invoke();
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _items.RemoveAll(x => x.Id == id);
            SaveInternal();
        }
        HistoryUpdated?.Invoke();
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _items.Clear();
            SaveInternal();
        }
        HistoryUpdated?.Invoke();
    }

    /// <summary>
    /// Deletes history records older than the specified retention days (e.g. 7 or 30 days).
    /// If retentionDays <= 0, no items are deleted.
    /// Returns the number of pruned items.
    /// </summary>
    public int PruneOlderThan(int retentionDays)
    {
        if (retentionDays <= 0) return 0;

        int removedCount = 0;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            int initialCount = _items.Count;
            _items.RemoveAll(x => x.TimestampUtc < cutoff);
            removedCount = initialCount - _items.Count;

            if (removedCount > 0)
            {
                SaveInternal();
            }
        }

        if (removedCount > 0)
        {
            HistoryUpdated?.Invoke();
        }

        return removedCount;
    }

    private void SaveInternal()
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_items, JsonOptions);
            File.WriteAllText(_historyFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save transfer history: {ex.Message}");
        }
    }
}
