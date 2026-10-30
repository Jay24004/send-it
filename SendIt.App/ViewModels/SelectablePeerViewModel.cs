using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using SendIt.Core.Models;
using SendIt.Core.Network;

namespace SendIt.App.ViewModels;

public partial class SelectablePeerViewModel : ObservableObject
{
    private readonly Action<SelectablePeerViewModel>? _onSelectionChanged;
    private readonly NetworkAdapterService? _adapterService;

    public DiscoveredPeer Peer { get; }

    public string PeerId => Peer.PeerId;
    public string DisplayName => Peer.DisplayName;
    public string AvatarColor => Peer.AvatarColor;
    public string AvatarGlyph => Peer.AvatarGlyph;
    public string DeviceName => Peer.DeviceName;
    public string FormattedAddresses => Peer.FormattedAddresses;
    public bool IsOnline => Peer.IsOnline;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsMultiNicAccessible
    {
        get
        {
            if (_adapterService == null) return false;
            var reachable = _adapterService.GetReachableAdaptersForPeer(Peer.IpAddresses);
            return reachable.Count > 1;
        }
    }

    public string ReachableBadgeText
    {
        get
        {
            if (_adapterService == null) return "LAN";
            var reachable = _adapterService.GetReachableAdaptersForPeer(Peer.IpAddresses);
            if (reachable.Count > 1)
            {
                var types = reachable.Select(a => a.InterfaceType).Distinct();
                return $"⚡ Multi-NIC ({string.Join("+", types)})";
            }
            if (reachable.Count == 1)
            {
                return reachable[0].InterfaceType;
            }
            return "LAN";
        }
    }

    public string ReachableBadgeColor => IsMultiNicAccessible ? "#00E5FF" : "#9E9E9E";

    public SelectablePeerViewModel(
        DiscoveredPeer peer,
        Action<SelectablePeerViewModel>? onSelectionChanged = null,
        NetworkAdapterService? adapterService = null)
    {
        Peer = peer;
        _onSelectionChanged = onSelectionChanged;
        _adapterService = adapterService;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        _onSelectionChanged?.Invoke(this);
    }
}
