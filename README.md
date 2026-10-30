# SendIt ⚡

**Ultra-fast, zero-config local network file sharing for Windows with Multi-NIC Turbo Striping, custom profile discovery, chunk-level integrity validation, and a sleek minimalist UI.**

---

## 🌟 Key Highlights

- **Multi-NIC Turbo Striping**: Automatically detects all operational network adapters (e.g. 10GbE / 2.5GbE LAN + Wi-Fi 6/7) and stripes file chunks across multiple network interfaces in parallel to bypass single-adapter and TCP window bottlenecks.
- **Strictly Local Network Only**: Operates 100% within your local area network (LAN/WLAN). Zero third-party servers, zero cloud relays, zero external telemetry.
- **Zero-Config Peer Discovery**: Uses UDP multicast (`239.255.42.99:42425`) and subnet broadcast fallback (`255.255.255.255:42425`) to discover nearby devices within milliseconds.
- **Custom Profile & Identity**: Customize your **Display Name**, **Avatar Color**, and **Avatar Glyph**. Other peers see your personalized identity instead of raw hostname or cryptic random IDs.
- **Full Integrity Validation**:
  - **Chunk-level**: Hardware-accelerated `xxHash64` per chunk (processing at >10 GB/s) with instant retransmission on mismatch.
  - **File-level**: Complete SHA-256 verification after chunk reassembly.
- **Smart Preferences & Automation**:
  - **Auto Approve**: Automatically accept and download incoming transfers without manual confirmation.
  - **Default Download Location**: Set your favorite directory or toggle **Ask Every Time**.
  - **Auto-Delete Transfer History**: Automatically purges transfer history older than **7 Days**, **30 Days**, or **Never** (keep forever).
- **Modern Minimalist Dual-Pane UI**: Built with WPF on .NET 10 featuring a high-contrast dark and white palette, dedicated sidebar for discovered systems with multi-device selection, a main workspace for drag-and-drop file staging, a dedicated transfer approval/rejection card system, live multi-NIC speed HUD, and complete preferences/profile settings.

---

## 🏗️ Architecture & Protocol

### 1. Multi-Stream & Multi-NIC Engine (`SendIt.Core`)
```text
           [ Sender PC ]                                    [ Receiver PC ]
  (File partitioned into 2MB chunks)            (Pre-allocates files with SetLength)
                 │                                                │
       [ Work-Stealing Queue ]                                    │
    ┌────────────┼────────────┐                                   │
[Worker 1]  [Worker 2]  [Worker N]                                │
    │            │            │                                   │
(NIC 1: LAN) (NIC 2: WiFi) (NIC 1: LAN)                           │
    │            │            │                                   │
    ▼            ▼            ▼                                   ▼
 [Parallel TCP Sockets: 8-16 concurrent streams] ──> [RandomAccess.WriteAsync]
    │            │            │                                   │
    ◄────── Fast ACK/NACK ────┴───────────────────────── [xxHash64 Verified]
```

### 2. High-Throughput Binary Chunk Framing
Each chunk is streamed with a 53-byte low-overhead binary header:
- **Magic**: `0x54444E53` ("SNDT") (4 bytes)
- **Packet Type**: `0x01` ChunkData / `0x02` Ack / `0x03` Nack (1 byte)
- **Transfer ID**: 16-byte raw GUID
- **File Index**: 4-byte Int32
- **Chunk Index**: 8-byte Int64
- **File Offset**: 8-byte Int64
- **Payload Length**: 4-byte Int32
- **Payload Checksum**: 8-byte UInt64 (`xxHash64`)
- **Data Payload**: Binary chunk bytes

---

## 📁 Project Structure

```text
SendIt/
├── SendIt.slnx                # Modern XML Solution
├── SendIt.Core/               # Transfer Engine, Multi-NIC, Discovery, Protocol
│   ├── Models/
│   │   ├── AppSettings.cs          # Profile, rules, and retention settings
│   │   ├── UserProfile.cs          # Custom name, avatar color, avatar glyph
│   │   ├── DiscoveredPeer.cs       # Peer identity and multi-IP endpoints
│   │   ├── NetworkAdapterInfo.cs   # Physical adapter details and speeds
│   │   ├── TransferProtocol.cs     # Binary framing headers and control DTOs
│   │   ├── TransferProgress.cs     # Live speed, ETA, and verified chunks
│   │   └── TransferHistoryItem.cs  # Transfer records and metrics
│   ├── Network/
│   │   ├── NetworkAdapterService.cs# Adapters scanner and IP mapper
│   │   ├── DiscoveryService.cs     # Multicast & broadcast beaconing
│   │   ├── TransferServer.cs       # Parallel socket receiver & chunk validator
│   │   ├── TransferClient.cs       # Multi-NIC chunk striping dispatcher
│   │   └── ChecksumHelper.cs       # SIMD xxHash64 and SHA-256 hashing
│   └── Storage/
│       ├── SettingsManager.cs      # JSON persistence in %APPDATA%\SendIt\
│       └── TransferHistoryManager.cs# History log with auto-cleanup (7/30 days)
├── SendIt.App/                # Modern WPF Desktop Application
│   ├── Converters/                 # Value converters for UI data binding
│   ├── ViewModels/
│   │   ├── MainViewModel.cs        # MVVM view model for workspace, transfers & settings
│   │   ├── SelectablePeerViewModel.cs # Discovered peer with multi-selection state
│   │   └── StagedItem.cs           # Staged file/directory queue item model
│   ├── App.xaml                    # Minimal dark & white theme, control templates, tokens
│   ├── MainWindow.xaml            # Dual-pane sidebar & workspace layout
│   └── MainWindow.xaml.cs         # Drag-and-drop orchestration and window lifecycle
└── SendIt.Tests/              # Automated Test Suite (xUnit)
    ├── ChecksumAndTransferTests.cs# End-to-end multi-stream chunk transfer test
    ├── DiscoveryServiceTests.cs   # Profile beacon & peer discovery test
    └── SettingsAndHistoryTests.cs # Auto-delete retention and settings test
```

---

## 🚀 Getting Started

### Prerequisites
- Windows 10 / 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/) (or .NET 9+)

### Build the Solution
```powershell
dotnet build
```

### Run Automated Tests
```powershell
dotnet test
```

### Launch the Application
```powershell
dotnet run --project SendIt.App
```

---

## ⚙️ Configuration & Preferences

All preferences are stored at `%APPDATA%\SendIt\settings.json`:
- **Profile Name**: Your custom display name.
- **Avatar Customization**: Choose between 8 vibrant accent colors and 12 distinct avatar glyphs.
- **Auto Approve**: Instantly accept incoming transfers without prompt.
- **Default Download Location**: Path where received files are saved (defaults to `Downloads\SendIt`).
- **Ask Download Location Every Time**: Checkbox to prompt for download folder per transfer.
- **Auto Delete History**: Retention policy (older than 7 days, older than 30 days, or never).
- **Parallel Streams**: Number of concurrent socket connections (from 2 up to 32).
- **Multi-NIC Turbo**: Toggle striping across all detected Ethernet and Wi-Fi adapters.
