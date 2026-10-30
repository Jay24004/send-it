using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SendIt.App.Services;
using SendIt.Core.Diagnostics;
using SendIt.Core.Models;
using SendIt.Core.Network;
using SendIt.Core.Storage;

namespace SendIt.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SettingsManager _settingsManager;
    private readonly TransferHistoryManager _historyManager;
    private readonly NetworkAdapterService _adapterService;
    private readonly DiscoveryService _discoveryService;
    private readonly TransferServer _transferServer;
    private readonly TransferClient _transferClient;

    private readonly Dictionary<string, CancellationTokenSource> _transferCts = new();
    private Action<IncomingTransferDecision>? _pendingDecisionCallback;

    [ObservableProperty]
    private string _currentTab = "Workspace"; // "Workspace", "History", "Settings"

    // Profile Settings
    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _avatarColor = "#00D26A";

    [ObservableProperty]
    private string _avatarGlyph = "🚀";

    // Transfer Settings
    [ObservableProperty]
    private bool _autoApprove;

    [ObservableProperty]
    private string _defaultDownloadLocation = string.Empty;

    [ObservableProperty]
    private bool _askDownloadLocationEveryTime;

    [ObservableProperty]
    private int _autoDeleteHistoryDays = 7;

    public List<string> RetentionChoices { get; } = ["Older than 7 Days", "Older than 30 Days", "Never"];

    public string SelectedRetentionChoice
    {
        get => AutoDeleteHistoryDays switch
        {
            7 => "Older than 7 Days",
            30 => "Older than 30 Days",
            _ => "Never"
        };
        set
        {
            AutoDeleteHistoryDays = value switch
            {
                "Older than 7 Days" => 7,
                "Older than 30 Days" => 30,
                _ => 0
            };
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private int _parallelStreams = 8;

    [ObservableProperty]
    private int _chunkSizeMb = 2;

    [ObservableProperty]
    private bool _useAllNetworkAdapters = true;

    [ObservableProperty]
    private string _preferredAdapterName = "Auto";

    public ObservableCollection<string> AvailableAdapterNames { get; } = ["Auto"];

    // System IP & Pop-up
    [ObservableProperty]
    private string _primarySystemIp = "127.0.0.1";

    [ObservableProperty]
    private bool _isIpPopupOpen;

    // Direct Connect Properties
    [ObservableProperty]
    private bool _isDirectConnectOpen;

    [ObservableProperty]
    private string _directConnectIp = string.Empty;

    [ObservableProperty]
    private string _selectedProbeAdapter = "All Adapters";

    public ObservableCollection<string> AvailableProbeAdapters { get; } = ["All Adapters"];

    // Throughput & Workspace layout
    [ObservableProperty]
    private long _sessionBytesSent;

    [ObservableProperty]
    private long _sessionBytesReceived;

    [ObservableProperty]
    private bool _isWorkspaceHeaderCollapsed;

    public string SessionThroughputDisplay => $"Sent: {TransferProgress.FormatBytes(SessionBytesSent)}  •  Recv: {TransferProgress.FormatBytes(SessionBytesReceived)}";

    // Status Banner / Indicators
    [ObservableProperty]
    private string _activeAdaptersSummary = "Scanning network...";

    [ObservableProperty]
    private bool _isScanning = true;

    [ObservableProperty]
    private string? _statusToast;

    // Incoming Transfer Notification
    [ObservableProperty]
    private bool _hasIncomingRequest;

    [ObservableProperty]
    private string _incomingRequestSender = string.Empty;

    [ObservableProperty]
    private string _incomingRequestAvatarColor = "#00D26A";

    [ObservableProperty]
    private string _incomingRequestAvatarGlyph = "🚀";

    [ObservableProperty]
    private string _incomingRequestDetails = string.Empty;

    [ObservableProperty]
    private string _incomingRequestFolder = string.Empty;

    // History summary & badge
    [ObservableProperty]
    private int _activeTransfersCount;

    [ObservableProperty]
    private string _historyPruneNotice = string.Empty;

    // Peer multi-selection state
    [ObservableProperty]
    private int _selectedPeersCount;

    [ObservableProperty]
    private bool _hasSelectedPeers;

    [ObservableProperty]
    private bool _allPeersSelected;

    // Staged items state
    [ObservableProperty]
    private int _stagedCount;

    [ObservableProperty]
    private bool _hasStagedItems;

    [ObservableProperty]
    private string _stagedSummary = "0 files selected";

    // Available color and emoji presets
    public List<string> ColorPresets { get; } =
    [
        "#FFFFFF", // Pure White
        "#00E5FF", // Cyan Turbo
        "#10B981", // Emerald
        "#3B82F6", // Electric Blue
        "#9D4EDD", // Royal Purple
        "#FF6B6B", // Coral
        "#FFB703", // Gold
        "#FF007F"  // Neon Pink
    ];

    public List<string> GlyphPresets { get; } =
    [
        "⚡", "💻", "🚀", "🦊", "🪐", "🛡️", "🌊", "🦅", "💎", "🔥", "🔮", "📦"
    ];

    public ObservableCollection<SelectablePeerViewModel> DiscoveredPeers { get; } = [];
    public ObservableCollection<StagedItem> StagedItems { get; } = [];
    public ObservableCollection<TransferProgress> ActiveTransfers { get; } = [];
    public ObservableCollection<TransferHistoryItem> TransferHistory { get; } = [];
    public ObservableCollection<NetworkAdapterInfo> ActiveAdapters { get; } = [];

    public MainViewModel()
    {
        _settingsManager = new SettingsManager();
        _historyManager = new TransferHistoryManager();
        _adapterService = new NetworkAdapterService();

        var settings = _settingsManager.CurrentSettings;
        _discoveryService = new DiscoveryService(settings.Profile, _adapterService);
        _transferServer = new TransferServer(_settingsManager, _historyManager, _adapterService);
        _transferClient = new TransferClient(_settingsManager, _historyManager, _adapterService);

        LoadSettingsToVm(settings);
        RefreshAdapters();
        RefreshHistory();

        // Wire server events
        _transferServer.TransferRequested += OnTransferRequested;
        _transferServer.ProgressUpdated += OnProgressUpdated;
        _transferServer.TransferCompleted += OnTransferCompleted;

        // Wire client events
        _transferClient.ProgressUpdated += OnProgressUpdated;
        _transferClient.TransferCompleted += OnTransferCompleted;

        // Wire discovery events
        _discoveryService.PeersListChanged += OnPeersListChanged;
        _adapterService.AdaptersChanged += () => Application.Current?.Dispatcher.Invoke(RefreshAdapters);
        _historyManager.HistoryUpdated += () => Application.Current?.Dispatcher.Invoke(RefreshHistory);

        // Run auto-prune on startup
        PruneOldHistoryOnStartup();

        // Start services
        try
        {
            _transferServer.Start(settings.TcpPort);
            _discoveryService.Start();
        }
        catch (Exception ex)
        {
            ShowToast($"Startup warning: {ex.Message}");
        }
    }

    private void LoadSettingsToVm(AppSettings s)
    {
        ProfileName = s.Profile.DisplayName;
        AvatarColor = s.Profile.AvatarColor;
        AvatarGlyph = s.Profile.AvatarGlyph;
        AutoApprove = s.AutoApprove;
        DefaultDownloadLocation = s.DefaultDownloadLocation;
        AskDownloadLocationEveryTime = s.AskDownloadLocationEveryTime;
        AutoDeleteHistoryDays = s.AutoDeleteHistoryDays;
        ParallelStreams = s.ParallelStreams;
        ChunkSizeMb = s.ChunkSizeMb;
        UseAllNetworkAdapters = s.UseAllNetworkAdapters;
        PreferredAdapterName = s.PreferredAdapterName;
    }

    [RelayCommand]
    public void SaveSettings()
    {
        var s = _settingsManager.CurrentSettings;
        s.Profile.DisplayName = string.IsNullOrWhiteSpace(ProfileName) ? Environment.UserName : ProfileName.Trim();
        s.Profile.AvatarColor = AvatarColor;
        s.Profile.AvatarGlyph = AvatarGlyph;
        s.AutoApprove = AutoApprove;
        s.DefaultDownloadLocation = DefaultDownloadLocation;
        s.AskDownloadLocationEveryTime = AskDownloadLocationEveryTime;
        s.AutoDeleteHistoryDays = AutoDeleteHistoryDays;
        s.ParallelStreams = ParallelStreams;
        s.ChunkSizeMb = ChunkSizeMb;
        s.UseAllNetworkAdapters = UseAllNetworkAdapters;
        s.PreferredAdapterName = PreferredAdapterName;

        _settingsManager.Save(s);
        _discoveryService.CurrentProfile = s.Profile;
        _ = _discoveryService.BroadcastPresenceAsync();

        ShowToast("Preferences saved.");
    }

    [RelayCommand]
    public void SelectTab(string tab)
    {
        CurrentTab = tab switch
        {
            "Radar" => "Workspace",
            "Transfers" => "Workspace",
            _ => tab
        };
    }

    [RelayCommand]
    public void SelectColor(string color)
    {
        AvatarColor = color;
        SaveSettings();
    }

    [RelayCommand]
    public void SelectGlyph(string glyph)
    {
        AvatarGlyph = glyph;
        SaveSettings();
    }

    [RelayCommand]
    public void RefreshAdapters()
    {
        var adapters = _adapterService.GetActiveAdapters();
        ActiveAdapters.Clear();
        foreach (var a in adapters)
        {
            ActiveAdapters.Add(a);
        }

        // Update Direct Connect probe options
        AvailableProbeAdapters.Clear();
        AvailableProbeAdapters.Add("All Adapters");
        AvailableAdapterNames.Clear();
        AvailableAdapterNames.Add("Auto");

        foreach (var a in adapters)
        {
            if (!string.IsNullOrWhiteSpace(a.Name))
            {
                AvailableProbeAdapters.Add(a.Name);
                AvailableAdapterNames.Add(a.Name);
            }
        }

        var primary = adapters.SelectMany(a => a.IpAddresses).FirstOrDefault();
        PrimarySystemIp = primary ?? "127.0.0.1";

        if (adapters.Count == 0)
        {
            ActiveAdaptersSummary = "No active network adapters";
        }
        else if (adapters.Count == 1)
        {
            ActiveAdaptersSummary = $"{adapters[0].Name} • {adapters[0].SpeedDisplay}";
        }
        else
        {
            ActiveAdaptersSummary = $"{adapters.Count} Adapters Multi-NIC ({string.Join(" + ", adapters.Select(a => a.InterfaceType))})";
        }
    }

    private void RefreshHistory()
    {
        var items = _historyManager.Items;
        TransferHistory.Clear();
        foreach (var item in items)
        {
            TransferHistory.Add(item);
        }
    }

    private void PruneOldHistoryOnStartup()
    {
        if (AutoDeleteHistoryDays > 0)
        {
            int pruned = _historyManager.PruneOlderThan(AutoDeleteHistoryDays);
            if (pruned > 0)
            {
                HistoryPruneNotice = $"Cleaned {pruned} record{(pruned > 1 ? "s" : "")} older than {AutoDeleteHistoryDays} days.";
            }
        }
    }

    [RelayCommand]
    public void PruneHistoryNow()
    {
        if (AutoDeleteHistoryDays <= 0)
        {
            ShowToast("Auto-delete is set to 'Never'.");
            return;
        }

        int pruned = _historyManager.PruneOlderThan(AutoDeleteHistoryDays);
        ShowToast(pruned > 0 ? $"Purged {pruned} old records." : "No records older than retention period.");
    }

    [RelayCommand]
    public void ClearAllHistory()
    {
        _historyManager.ClearAll();
        ShowToast("History cleared.");
    }

    [RelayCommand]
    public void BrowseDefaultDownloadLocation()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Default Download Directory",
            InitialDirectory = Directory.Exists(DefaultDownloadLocation) ? DefaultDownloadLocation : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == true)
        {
            DefaultDownloadLocation = dialog.FolderName;
            SaveSettings();
        }
    }

    [RelayCommand]
    public void BrowseIncomingLocation()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose Destination for Incoming Transfer",
            InitialDirectory = Directory.Exists(IncomingRequestFolder) ? IncomingRequestFolder : DefaultDownloadLocation
        };

        if (dialog.ShowDialog() == true)
        {
            IncomingRequestFolder = dialog.FolderName;
        }
    }

    [RelayCommand]
    public void AcceptIncomingRequest()
    {
        if (_pendingDecisionCallback != null)
        {
            var folder = !string.IsNullOrWhiteSpace(IncomingRequestFolder) ? IncomingRequestFolder : DefaultDownloadLocation;
            AppLogger.Success("UI", $"User approved incoming transfer from '{IncomingRequestSender}' -> '{folder}'");
            _pendingDecisionCallback.Invoke(new IncomingTransferDecision
            {
                Accept = true,
                DestinationFolder = folder
            });
            _pendingDecisionCallback = null;
        }
        HasIncomingRequest = false;
        CurrentTab = "Workspace";
    }

    [RelayCommand]
    public void DeclineIncomingRequest()
    {
        if (_pendingDecisionCallback != null)
        {
            AppLogger.Warn("UI", $"User declined incoming transfer from '{IncomingRequestSender}'");
            _pendingDecisionCallback.Invoke(new IncomingTransferDecision
            {
                Accept = false
            });
            _pendingDecisionCallback = null;
        }
        HasIncomingRequest = false;
        ShowToast("Transfer declined.");
    }

    // =========================================================================
    // PEER SELECTION & MULTI-DEVICE SUPPORT
    // =========================================================================

    public void OnPeerSelectionChanged(SelectablePeerViewModel? peer = null)
    {
        SelectedPeersCount = DiscoveredPeers.Count(p => p.IsSelected);
        HasSelectedPeers = SelectedPeersCount > 0;
        AllPeersSelected = DiscoveredPeers.Count > 0 && DiscoveredPeers.All(p => p.IsSelected);
    }

    [RelayCommand]
    public void TogglePeerSelection(SelectablePeerViewModel? peer)
    {
        if (peer == null) return;
        peer.IsSelected = !peer.IsSelected;
        OnPeerSelectionChanged(peer);
    }

    [RelayCommand]
    public void SelectAllPeers()
    {
        foreach (var p in DiscoveredPeers)
        {
            p.IsSelected = true;
        }
        OnPeerSelectionChanged();
    }

    [RelayCommand]
    public void DeselectAllPeers()
    {
        foreach (var p in DiscoveredPeers)
        {
            p.IsSelected = false;
        }
        OnPeerSelectionChanged();
    }

    [RelayCommand]
    public void ToggleSelectAll()
    {
        bool targetState = !AllPeersSelected;
        foreach (var p in DiscoveredPeers)
        {
            p.IsSelected = targetState;
        }
        OnPeerSelectionChanged();
    }

    // =========================================================================
    // FILE STAGING & QUEUE
    // =========================================================================

    [RelayCommand]
    public void BrowseAndStageFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Files to Send",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            StagePaths(dialog.FileNames);
        }
    }

    [RelayCommand]
    public void BrowseAndStageFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Folder to Send"
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            StagePaths([dialog.FolderName]);
        }
    }

    public void StagePaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (!StagedItems.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    StagedItems.Add(new StagedItem
                    {
                        Path = path,
                        Name = fi.Name,
                        SizeBytes = fi.Length,
                        IsDirectory = false
                    });
                }
            }
            else if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                if (!StagedItems.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    long totalSize = 0;
                    try
                    {
                        totalSize = di.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                    }
                    catch { }

                    StagedItems.Add(new StagedItem
                    {
                        Path = path,
                        Name = di.Name,
                        SizeBytes = totalSize,
                        IsDirectory = true
                    });
                }
            }
        }

        UpdateStagedState();
    }

    [RelayCommand]
    public void RemoveStagedItem(StagedItem? item)
    {
        if (item != null)
        {
            StagedItems.Remove(item);
            UpdateStagedState();
        }
    }

    [RelayCommand]
    public void ClearStagedItems()
    {
        StagedItems.Clear();
        UpdateStagedState();
    }

    private void UpdateStagedState()
    {
        StagedCount = StagedItems.Count;
        HasStagedItems = StagedCount > 0;
        long totalBytes = StagedItems.Sum(s => s.SizeBytes);
        StagedSummary = $"{StagedCount} item{(StagedCount != 1 ? "s" : "")} • {TransferProgress.FormatBytes(totalBytes)}";
    }

    [RelayCommand]
    public async Task SendStagedFilesToSelectedPeers()
    {
        var selected = DiscoveredPeers.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowToast("Select at least one system from the sidebar.");
            return;
        }

        if (StagedItems.Count == 0)
        {
            ShowToast("Select or drop files first.");
            return;
        }

        var paths = StagedItems.Select(s => s.Path).ToList();
        ClearStagedItems();
        CurrentTab = "Workspace";

        foreach (var peerVm in selected)
        {
            _ = InitiateSendAsync(peerVm.Peer, paths);
        }
    }

    [RelayCommand]
    public async Task SendFilesToPeer(DiscoveredPeer? peer)
    {
        if (peer == null) return;

        var dialog = new OpenFileDialog
        {
            Title = $"Select Files to Send to {peer.DisplayName}",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0)
        {
            await InitiateSendAsync(peer, dialog.FileNames);
        }
    }

    [RelayCommand]
    public async Task SendFolderToPeer(DiscoveredPeer? peer)
    {
        if (peer == null) return;

        var dialog = new OpenFolderDialog
        {
            Title = $"Select Folder to Send to {peer.DisplayName}"
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            await InitiateSendAsync(peer, [dialog.FolderName]);
        }
    }

    public async Task HandleDroppedFilesAsync(DiscoveredPeer? targetPeer, IReadOnlyList<string> paths)
    {
        if (targetPeer != null)
        {
            await InitiateSendAsync(targetPeer, paths);
        }
        else
        {
            // Dropped onto main workspace drop zone
            StagePaths(paths);
            ShowToast($"Staged {paths.Count} item{(paths.Count != 1 ? "s" : "")} for sending.");
        }
    }

    private async Task InitiateSendAsync(DiscoveredPeer peer, IReadOnlyList<string> paths)
    {
        CurrentTab = "Workspace";
        var cts = new CancellationTokenSource();

        try
        {
            ShowToast($"Connecting to {peer.DisplayName}...");
            AppLogger.Info("UI", $"Initiating transfer of {paths.Count} items to '{peer.DisplayName}'");
            bool success = await Task.Run(() => _transferClient.SendFilesAsync(peer, paths, cts.Token));
            if (!success)
            {
                ShowToast($"Transfer to {peer.DisplayName} declined or cancelled.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI", $"Transfer to '{peer.DisplayName}' failed: {ex.Message}", ex);
            ShowToast($"Send error: {ex.Message}");
        }
        finally
        {
            cts.Dispose();
        }
    }

    [RelayCommand]
    public void CancelTransfer(string? transferId)
    {
        if (!string.IsNullOrWhiteSpace(transferId) && _transferCts.TryGetValue(transferId, out var cts))
        {
            AppLogger.Warn("UI", $"User cancelled transfer {transferId[..Math.Min(8, transferId.Length)]}");
            cts.Cancel();
            ShowToast("Cancellation requested.");
        }
    }

    [RelayCommand]
    public void OpenFolder(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        else if (File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
        }
    }

    [RelayCommand]
    public async Task TriggerDiscoveryBroadcast()
    {
        IsScanning = true;
        AppLogger.Info("UI", "User triggered manual discovery broadcast");
        await _discoveryService.BroadcastPresenceAsync();
        ShowToast("Discovery beacon broadcasted.");
        await Task.Delay(800);
        IsScanning = false;
    }

    private void OnTransferRequested(TransferRequestMessage request, Action<IncomingTransferDecision> respond)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                _pendingDecisionCallback = respond;
                IncomingRequestSender = request.Sender.DisplayName;
                IncomingRequestAvatarColor = request.Sender.AvatarColor;
                IncomingRequestAvatarGlyph = request.Sender.AvatarGlyph;
                IncomingRequestDetails = $"{request.Files.Count} file{(request.Files.Count > 1 ? "s" : "")} • {TransferProgress.FormatBytes(request.TotalBytes)}";
                IncomingRequestFolder = DefaultDownloadLocation;
                HasIncomingRequest = true;
                CurrentTab = "Workspace";

                AppLogger.Info("UI", $"Incoming transfer banner displayed for '{request.Sender.DisplayName}' ({request.Files.Count} files, {TransferProgress.FormatBytes(request.TotalBytes)})");
            }
            catch (Exception ex)
            {
                AppLogger.Error("UI", $"Error displaying incoming transfer request: {ex.Message}", ex);
            }
        });
    }

    [RelayCommand]
    public void ToggleIpPopup()
    {
        IsIpPopupOpen = !IsIpPopupOpen;
    }

    [RelayCommand]
    public void CopyIp(string? ip)
    {
        if (!string.IsNullOrWhiteSpace(ip))
        {
            Clipboard.SetText(ip);
            ShowToast($"Copied {ip} to clipboard");
            IsIpPopupOpen = false;
        }
    }

    [RelayCommand]
    public void ToggleDirectConnect()
    {
        IsDirectConnectOpen = !IsDirectConnectOpen;
    }

    [RelayCommand]
    public async Task DirectConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(DirectConnectIp))
        {
            ShowToast("Please enter an IP address.");
            return;
        }

        string ip = DirectConnectIp.Trim();
        ShowToast($"Probing {ip} via {SelectedProbeAdapter}...");
        bool found = await _discoveryService.ProbeDirectPeerAsync(ip, TransferProtocolConstants.DefaultControlPort, SelectedProbeAdapter);
        if (found)
        {
            ShowToast($"Found and connected to {ip}!");
            DirectConnectIp = string.Empty;
            IsDirectConnectOpen = false;
        }
        else
        {
            ShowToast($"Probe sent to {ip}. Peer will appear once beacon is returned.");
        }
    }

    [RelayCommand]
    public void ToggleWorkspaceHeader()
    {
        IsWorkspaceHeaderCollapsed = !IsWorkspaceHeaderCollapsed;
    }

    [RelayCommand]
    public void DismissTransfer(string? transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId)) return;
        var existing = ActiveTransfers.FirstOrDefault(t => t.TransferId == transferId);
        if (existing != null)
        {
            ActiveTransfers.Remove(existing);
            ActiveTransfersCount = ActiveTransfers.Count(t => t.Status == TransferStatus.InProgress || t.Status == TransferStatus.Pending);
            if (ActiveTransfersCount == 0 && ActiveTransfers.Count == 0)
            {
                IsWorkspaceHeaderCollapsed = false;
            }
        }
    }

    [RelayCommand]
    public void OpenTransferFolder(TransferProgress? progress)
    {
        if (progress == null) return;
        string? targetPath = progress.DestinationFolder;
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            targetPath = DefaultDownloadLocation;
        }

        OpenFolder(targetPath);
    }

    private void OnProgressUpdated(TransferProgress progress)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                var existing = ActiveTransfers.FirstOrDefault(t => t.TransferId == progress.TransferId);
                if (existing != null)
                {
                    int index = ActiveTransfers.IndexOf(existing);
                    ActiveTransfers[index] = progress;
                }
                else
                {
                    ActiveTransfers.Insert(0, progress);
                }

                ActiveTransfersCount = ActiveTransfers.Count(t => t.Status == TransferStatus.InProgress || t.Status == TransferStatus.Pending);
                if (ActiveTransfersCount > 0)
                {
                    IsWorkspaceHeaderCollapsed = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("UI", $"Error updating transfer progress: {ex.Message}", ex);
            }
        });
    }

    private void OnTransferCompleted(TransferHistoryItem item)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (item.Direction == TransferDirection.Sent)
            {
                SessionBytesSent += item.TotalBytes;
            }
            else
            {
                SessionBytesReceived += item.TotalBytes;
            }
            OnPropertyChanged(nameof(SessionThroughputDisplay));

            var active = ActiveTransfers.FirstOrDefault(t => t.TransferId == item.Id);
            if (active != null)
            {
                active.Status = item.Status == TransferStatus.Completed ? TransferStatus.Completed : TransferStatus.Failed;
                active.EndTimeUtc = DateTime.UtcNow;
                active.DestinationFolder = item.TargetDirectory;
                int idx = ActiveTransfers.IndexOf(active);
                if (idx >= 0)
                {
                    ActiveTransfers[idx] = active;
                }
            }

            ActiveTransfersCount = ActiveTransfers.Count(t => t.Status == TransferStatus.InProgress || t.Status == TransferStatus.Pending);
            RefreshHistory();

            string action = item.Direction == TransferDirection.Sent ? "Sent to" : "Received from";
            ShowToast($"{item.SummaryText} {action} {item.PeerName} ({item.Status})");
        });
    }

    private void OnPeersListChanged(IReadOnlyList<DiscoveredPeer> peers)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // Preserve selected state for already-discovered peers
            var previouslySelectedIds = new HashSet<string>(DiscoveredPeers.Where(p => p.IsSelected).Select(p => p.PeerId));

            DiscoveredPeers.Clear();
            foreach (var p in peers)
            {
                var vm = new SelectablePeerViewModel(p, OnPeerSelectionChanged, _adapterService)
                {
                    IsSelected = previouslySelectedIds.Contains(p.PeerId)
                };
                DiscoveredPeers.Add(vm);
            }

            OnPeerSelectionChanged();
        });
    }

    private void ShowToast(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusToast = message;
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000);
                if (StatusToast == message)
                {
                    Application.Current?.Dispatcher.Invoke(() => StatusToast = null);
                }
            });
        });
    }

    [RelayCommand]
    public void ToggleDebugConsole()
    {
        DebugConsoleService.ToggleConsole();
    }

    public void Cleanup()
    {
        // Keep independent debug console alive so logs remain visible even after app termination
        _transferServer.Dispose();
        _discoveryService.Dispose();
    }
}
