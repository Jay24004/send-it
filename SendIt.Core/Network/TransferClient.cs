using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SendIt.Core.Diagnostics;
using SendIt.Core.Models;
using SendIt.Core.Storage;

namespace SendIt.Core.Network;

public class ChunkWorkItem
{
    public long ChunkIndex { get; set; }
    public int FileIndex { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public int RetryCount { get; set; }
}

public class TransferClient
{
    private readonly SettingsManager _settingsManager;
    private readonly TransferHistoryManager _historyManager;
    private readonly NetworkAdapterService _adapterService;

    public event Action<TransferProgress>? ProgressUpdated;
    public event Action<TransferHistoryItem>? TransferCompleted;

    public TransferClient(
        SettingsManager settingsManager,
        TransferHistoryManager historyManager,
        NetworkAdapterService adapterService)
    {
        _settingsManager = settingsManager;
        _historyManager = historyManager;
        _adapterService = adapterService;
    }

    public async Task<bool> SendFilesAsync(
        DiscoveredPeer targetPeer,
        IReadOnlyList<string> fileOrFolderPaths,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsManager.CurrentSettings;
        string transferId = Guid.NewGuid().ToString("N");
        var startTime = DateTime.UtcNow;

        AppLogger.Info("Client", $"Initiating transfer to '{targetPeer.DisplayName}' (TransferId: {transferId[..8]}). Staged paths: {fileOrFolderPaths.Count}");

        // 1. Build File Manifest
        var manifestFiles = BuildManifest(fileOrFolderPaths);
        if (manifestFiles.Count == 0)
        {
            AppLogger.Error("Client", "No valid accessible files found in selection to send.");
            throw new InvalidOperationException("No valid accessible files selected to send.");
        }

        long totalBytes = manifestFiles.Sum(f => f.Size);
        int chunkSize = settings.ChunkSizeMb * 1024 * 1024;
        if (chunkSize <= 0) chunkSize = TransferProtocolConstants.DefaultChunkSize;

        // Calculate chunk list
        var chunkQueue = new ConcurrentQueue<ChunkWorkItem>();
        long totalChunks = 0;

        for (int i = 0; i < manifestFiles.Count; i++)
        {
            var file = manifestFiles[i];
            long fileRemaining = file.Size;
            long fileOffset = 0;

            if (fileRemaining == 0)
            {
                // Empty file handled as 0-length chunk
                chunkQueue.Enqueue(new ChunkWorkItem
                {
                    ChunkIndex = totalChunks++,
                    FileIndex = i,
                    Offset = 0,
                    Length = 0
                });
                continue;
            }

            while (fileRemaining > 0)
            {
                int currentChunkLen = (int)Math.Min(chunkSize, fileRemaining);
                chunkQueue.Enqueue(new ChunkWorkItem
                {
                    ChunkIndex = totalChunks++,
                    FileIndex = i,
                    Offset = fileOffset,
                    Length = currentChunkLen
                });
                fileOffset += currentChunkLen;
                fileRemaining -= currentChunkLen;
            }
        }

        AppLogger.Info("Client", $"Manifest built: {manifestFiles.Count} file(s), {TransferProgress.FormatBytes(totalBytes)}, {totalChunks} chunks ({chunkSize / (1024 * 1024)} MB chunk size)");

        // 2. Establish Control Connection to target peer
        // Candidate IP addresses to attempt
        var candidateIps = new List<string>(targetPeer.IpAddresses);
        if (candidateIps.Count == 0) candidateIps.Add("127.0.0.1");
        int targetPort = targetPeer.TcpPort > 0 ? targetPeer.TcpPort : TransferProtocolConstants.DefaultControlPort;

        TcpClient? controlClient = null;
        NetworkStream? controlStream = null;
        string connectedIp = candidateIps[0];

        foreach (var ip in candidateIps)
        {
            AppLogger.Info("Client", $"Attempting control connection to '{targetPeer.DisplayName}' at {ip}:{targetPort}...");
            try
            {
                var testClient = new TcpClient { NoDelay = true };
                testClient.EnableTcpKeepAlive(3, 1, 5);
                using var ctsConnect = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await testClient.ConnectAsync(ip, targetPort, ctsConnect.Token);

                controlClient = testClient;
                controlStream = controlClient.GetStream();
                connectedIp = ip;
                AppLogger.Success("Client", $"Control connection established with {ip}:{targetPort}");
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Client", $"Failed connecting to {ip}:{targetPort} ({ex.Message}). Trying next candidate IP...");
            }
        }

        if (controlClient == null || controlStream == null)
        {
            string err = $"Could not connect to peer '{targetPeer.DisplayName}' at any known address [{string.Join(", ", candidateIps)}]:{targetPort}. Check peer firewall / network.";
            AppLogger.Error("Client", err);
            throw new IOException(err);
        }

        using (controlClient)
        using (controlStream)
        {
            // 3. Send Transfer Request
            var request = new TransferRequestMessage
            {
                TransferId = transferId,
                Sender = settings.Profile,
                Files = manifestFiles,
                TotalBytes = totalBytes,
                ChunkSize = chunkSize,
                TotalChunks = totalChunks,
                SenderEndpoints = _adapterService.GetAllLocalIPv4Addresses()
            };

            var requestJson = JsonSerializer.Serialize(request);
            var requestBytes = Encoding.UTF8.GetBytes(requestJson);
            var lenPrefix = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, requestBytes.Length);

            AppLogger.Info("Client", $"Sending transfer request message ({requestBytes.Length} bytes JSON) to '{targetPeer.DisplayName}'...");

            await controlStream.WriteAsync(lenPrefix, cancellationToken);
            await controlStream.WriteAsync(requestBytes, cancellationToken);
            await controlStream.FlushAsync(cancellationToken);

            // Notify initial progress (waiting for acceptance)
            var initialProgress = new TransferProgress
            {
                TransferId = transferId,
                PeerName = targetPeer.DisplayName,
                PeerAvatarColor = targetPeer.AvatarColor,
                PeerAvatarGlyph = targetPeer.AvatarGlyph,
                Direction = TransferDirection.Sent,
                Status = TransferStatus.Pending,
                FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                FileCount = manifestFiles.Count,
                TotalBytes = totalBytes,
                TransferredBytes = 0,
                TotalChunks = totalChunks,
                StartTimeUtc = startTime
            };
            ProgressUpdated?.Invoke(initialProgress);

            AppLogger.Info("Client", $"Transfer request sent. Awaiting approval from '{targetPeer.DisplayName}'...");

            // 4. Await Response
            var respPrefix = new byte[4];
            await ReadExactAsync(controlStream, respPrefix, 0, 4, cancellationToken);
            int respLen = BinaryPrimitives.ReadInt32LittleEndian(respPrefix);
            if (respLen <= 0 || respLen > 10 * 1024 * 1024)
            {
                AppLogger.Error("Client", $"Invalid response payload length received: {respLen} bytes.");
                throw new IOException("Invalid response length received from receiver.");
            }

            var respBuffer = new byte[respLen];
            await ReadExactAsync(controlStream, respBuffer, 0, respLen, cancellationToken);
            var respJson = Encoding.UTF8.GetString(respBuffer);
            var response = JsonSerializer.Deserialize<TransferResponseMessage>(respJson);

            if (response == null || !response.Accepted)
            {
                string reason = response?.Reason ?? "Transfer declined by peer.";
                AppLogger.Warn("Client", $"Transfer declined by '{targetPeer.DisplayName}'. Reason: {reason}");
                initialProgress.Status = TransferStatus.Rejected;
                initialProgress.ErrorMessage = reason;
                ProgressUpdated?.Invoke(initialProgress);
                return false;
            }

            AppLogger.Success("Client", $"Transfer ACCEPTED by '{targetPeer.DisplayName}'! Receiver endpoints: [{string.Join(", ", response.ReceiverEndpoints)}], Port: {response.ReceiverPort}");

            // 5. Open files for concurrent reading with FileShare.ReadWrite to avoid locking collisions
            var openHandles = new Dictionary<int, SafeFileHandle>();
            try
            {
                for (int i = 0; i < manifestFiles.Count; i++)
                {
                    openHandles[i] = File.OpenHandle(
                        manifestFiles[i].LocalFullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        FileOptions.Asynchronous);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Client", $"Failed to open local files for reading: {ex.Message}", ex);
                throw;
            }

            // Target IP endpoints for worker connections (striping across receiver interfaces)
            var allowedAdapters = _adapterService.FilterAdaptersByPreference(settings.UseAllNetworkAdapters, settings.PreferredAdapterName);
            var reachableAdapters = _adapterService.GetReachableAdaptersForPeer(targetPeer.IpAddresses);
            var effectiveAdapters = reachableAdapters.IntersectBy(allowedAdapters.Select(a => a.Id), a => a.Id).ToList();
            if (effectiveAdapters.Count == 0) effectiveAdapters = allowedAdapters.Count > 0 ? allowedAdapters : _adapterService.GetActiveAdapters();

            var candidateDestIps = response.ReceiverEndpoints.Count > 0 ? response.ReceiverEndpoints : targetPeer.IpAddresses;
            if (candidateDestIps.Count == 0) candidateDestIps = [connectedIp];

            // Filter receiver endpoints to only those reachable by our effective adapters if multi-NIC is allowed
            var destinationIps = new List<string>();
            foreach (var rIp in candidateDestIps)
            {
                if (effectiveAdapters.Any(a => a.CanReach(rIp)))
                {
                    destinationIps.Add(rIp);
                }
            }
            if (destinationIps.Count == 0) destinationIps = candidateDestIps.Take(effectiveAdapters.Count).ToList();
            if (destinationIps.Count == 0) destinationIps = [connectedIp];

            int dataPort = response.ReceiverPort > 0 ? response.ReceiverPort : targetPort;

            // Cap parallel stream count to total chunks (never open 8 sockets for 1 chunk)
            int streamSetting = Math.Max(1, Math.Min(32, settings.ParallelStreams));
            int streamCount = Math.Max(1, Math.Min(streamSetting, (int)Math.Max(1, totalChunks)));

            string nicMode = destinationIps.Count > 1 && effectiveAdapters.Count > 1 ? $"Multi-NIC ({destinationIps.Count} endpoints across {effectiveAdapters.Count} adapters)" : "Single Adapter";
            AppLogger.Info("Client", $"Initializing {streamCount} parallel data worker stream(s) using {nicMode} targeting [{string.Join(", ", destinationIps)}]:{dataPort}...");

            long transferredBytes = 0;
            long verifiedChunks = 0;
            var transferGuidBytes = Guid.Parse(transferId).ToByteArray();

            DateTime lastSpeedTime = DateTime.UtcNow;
            DateTime lastLogTime = DateTime.UtcNow;
            long lastSpeedBytes = 0;
            double currentSpeed = 0;
            double peakSpeed = 0;
            var adapterContribution = new ConcurrentDictionary<string, long>();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = linkedCts.Token;
            int activeWorkersCount = streamCount;

            try
            {
                initialProgress.Status = TransferStatus.InProgress;
                initialProgress.ActiveConnections = streamCount;
                initialProgress.ActiveNics = destinationIps.Count;

                // Spawn worker tasks
                var workerTasks = new List<Task>();

                for (int workerIndex = 0; workerIndex < streamCount; workerIndex++)
                {
                    int wIdx = workerIndex;
                    string destIp = destinationIps[wIdx % destinationIps.Count];
                    var matchingAdapter = effectiveAdapters.FirstOrDefault(a => a.CanReach(destIp)) ?? effectiveAdapters[wIdx % effectiveAdapters.Count];
                    string adapterName = matchingAdapter.InterfaceType;

                    workerTasks.Add(Task.Run(async () =>
                    {
                        TcpClient? dataClient = null;
                        ChunkWorkItem? inFlightChunk = null;
                        try
                        {
                            dataClient = new TcpClient { NoDelay = true };
                            dataClient.EnableTcpKeepAlive(3, 1, 5);
                            dataClient.SendBufferSize = 4 * 1024 * 1024;
                            dataClient.ReceiveBufferSize = 1024 * 1024;

                            bool isConnected = false;
                            string targetIp = destIp;

                            try
                            {
                                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                connectCts.CancelAfter(TimeSpan.FromSeconds(3));
                                await dataClient.ConnectAsync(destIp, dataPort, connectCts.Token);
                                isConnected = true;
                            }
                            catch (Exception ex)
                            {
                                AppLogger.Warn("Client", $"Worker #{wIdx} [{adapterName}] could not reach endpoint {destIp}:{dataPort} ({ex.Message})");
                            }

                            // If secondary endpoint failed and connectedIp is available, fallback to primary
                            if (!isConnected && !string.Equals(destIp, connectedIp, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    dataClient.Dispose();
                                    dataClient = new TcpClient { NoDelay = true, SendBufferSize = 4 * 1024 * 1024, ReceiveBufferSize = 1024 * 1024 };
                                    dataClient.EnableTcpKeepAlive(3, 1, 5);

                                    using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                                    fallbackCts.CancelAfter(TimeSpan.FromSeconds(3));
                                    await dataClient.ConnectAsync(connectedIp, dataPort, fallbackCts.Token);
                                    targetIp = connectedIp;
                                    isConnected = true;
                                    AppLogger.Info("Client", $"Worker #{wIdx} successfully connected via primary fallback {connectedIp}:{dataPort}");
                                }
                                catch (Exception ex)
                                {
                                    AppLogger.Warn("Client", $"Worker #{wIdx} fallback connect to {connectedIp}:{dataPort} failed: {ex.Message}");
                                }
                            }

                            if (!isConnected)
                            {
                                int left = Interlocked.Decrement(ref activeWorkersCount);
                                AppLogger.Warn("Client", $"Worker #{wIdx} offline. {left} active worker(s) remaining.");
                                if (left <= 0 && !chunkQueue.IsEmpty)
                                {
                                    AppLogger.Error("Client", "All data workers failed to establish connection. Aborting transfer.");
                                    try { linkedCts.Cancel(); } catch { }
                                }
                                return;
                            }

                            var dataStream = dataClient.GetStream();
                            AppLogger.Info("Client", $"Data worker #{wIdx} [{adapterName}] connected to {targetIp}:{dataPort}");

                            // Layout for chunk packet header: 4B Magic + 1B Type + 16B TransferId + 4B FileIndex + 8B ChunkIndex + 8B Offset + 4B Length + 8B Checksum = 53 bytes
                            var headerBytes = new byte[53];
                            BinaryPrimitives.WriteUInt32LittleEndian(headerBytes.AsSpan(0, 4), TransferProtocolConstants.MagicHeader);
                            headerBytes[4] = TransferProtocolConstants.PacketTypeChunk;
                            transferGuidBytes.CopyTo(headerBytes.AsSpan(5, 16));

                            var ackBuffer = new byte[30];

                            while (!token.IsCancellationRequested && chunkQueue.TryDequeue(out var chunk))
                            {
                                inFlightChunk = chunk;
                                byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                                try
                                {
                                    if (chunk.Length > 0 && openHandles.TryGetValue(chunk.FileIndex, out var handle))
                                    {
                                        int readBytes = await RandomAccess.ReadAsync(handle, chunkBuffer.AsMemory(0, chunk.Length), chunk.Offset, token);
                                        if (readBytes != chunk.Length)
                                        {
                                            throw new IOException($"Could not read expected {chunk.Length} bytes from file, got {readBytes}");
                                        }
                                    }

                                    ulong checksum = chunk.Length > 0
                                        ? ChecksumHelper.ComputeXxHash64(new ReadOnlySpan<byte>(chunkBuffer, 0, chunk.Length))
                                        : 0;

                                    // Fill header
                                    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(21, 4), chunk.FileIndex);
                                    BinaryPrimitives.WriteInt64LittleEndian(headerBytes.AsSpan(25, 8), chunk.ChunkIndex);
                                    BinaryPrimitives.WriteInt64LittleEndian(headerBytes.AsSpan(33, 8), chunk.Offset);
                                    BinaryPrimitives.WriteInt32LittleEndian(headerBytes.AsSpan(41, 4), chunk.Length);
                                    BinaryPrimitives.WriteUInt64LittleEndian(headerBytes.AsSpan(45, 8), checksum);

                                    // Write header + payload
                                    await dataStream.WriteAsync(headerBytes, token);
                                    if (chunk.Length > 0)
                                    {
                                        await dataStream.WriteAsync(chunkBuffer.AsMemory(0, chunk.Length), token);
                                    }
                                    await dataStream.FlushAsync(token);

                                    // Read ACK response: 4B Magic + 1B Type + 16B TransferId + 8B ChunkIndex + 1B Success
                                    await ReadExactAsync(dataStream, ackBuffer, 0, 30, token);
                                    bool success = ackBuffer[29] == 1;

                                    if (success)
                                    {
                                        inFlightChunk = null;
                                        Interlocked.Add(ref transferredBytes, chunk.Length);
                                        long doneChunks = Interlocked.Increment(ref verifiedChunks);
                                        adapterContribution.AddOrUpdate(adapterName, chunk.Length, (_, prev) => prev + chunk.Length);

                                        // Periodic speed update
                                        var now = DateTime.UtcNow;
                                        var elapsed = (now - lastSpeedTime).TotalSeconds;
                                        if (elapsed >= 0.25)
                                        {
                                            long currentBytes = Interlocked.Read(ref transferredBytes);
                                            currentSpeed = (currentBytes - lastSpeedBytes) / elapsed;
                                            if (currentSpeed > peakSpeed) peakSpeed = currentSpeed;
                                            lastSpeedBytes = currentBytes;
                                            lastSpeedTime = now;
                                        }

                                        double spd = currentSpeed;
                                        long curBytes = Interlocked.Read(ref transferredBytes);
                                        long remBytes = Math.Max(0, totalBytes - curBytes);
                                        var eta = spd > 0 ? TimeSpan.FromSeconds(remBytes / spd) : TimeSpan.Zero;

                                        // Periodic log update (every 10 seconds or on first chunk)
                                        if (doneChunks == 1 || (now - lastLogTime).TotalSeconds >= 10)
                                        {
                                            lastLogTime = now;
                                            double pct = totalBytes > 0 ? (curBytes / (double)totalBytes) * 100.0 : 100.0;
                                            AppLogger.Info("Client", $"Progress: {pct:F1}% ({TransferProgress.FormatBytes(curBytes)} / {TransferProgress.FormatBytes(totalBytes)}) | Speed: {TransferProgress.FormatBytes((long)spd)}/s | Chunks: {doneChunks}/{totalChunks}");
                                        }

                                        var liveProgress = new TransferProgress
                                        {
                                            TransferId = transferId,
                                            PeerName = targetPeer.DisplayName,
                                            PeerAvatarColor = targetPeer.AvatarColor,
                                            PeerAvatarGlyph = targetPeer.AvatarGlyph,
                                            Direction = TransferDirection.Sent,
                                            Status = TransferStatus.InProgress,
                                            FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                                            FileCount = manifestFiles.Count,
                                            TotalBytes = totalBytes,
                                            TransferredBytes = curBytes,
                                            TotalChunks = totalChunks,
                                            ChunksCompleted = doneChunks,
                                            ChunksVerified = doneChunks,
                                            SpeedBytesPerSecond = spd,
                                            PeakSpeedBytesPerSecond = peakSpeed,
                                            EstimatedRemaining = eta,
                                            ActiveConnections = streamCount,
                                            ActiveNics = destinationIps.Count,
                                            StartTimeUtc = startTime,
                                            AdapterBytes = new Dictionary<string, long>(adapterContribution)
                                        };

                                        foreach (var (adName, b) in adapterContribution)
                                        {
                                            liveProgress.UpdateAdapterContribution(adName, 0); // Recompute stats with current totals
                                        }

                                        ProgressUpdated?.Invoke(liveProgress);
                                    }
                                    else
                                    {
                                        // NACK: re-enqueue for retry
                                        chunk.RetryCount++;
                                        AppLogger.Warn("Client", $"Chunk #{chunk.ChunkIndex} NACKed by peer (checksum mismatch). Retrying (Attempt {chunk.RetryCount}/5)...");
                                        if (chunk.RetryCount < 5)
                                        {
                                            chunkQueue.Enqueue(chunk);
                                            inFlightChunk = null;
                                        }
                                        else
                                        {
                                            throw new IOException($"Chunk #{chunk.ChunkIndex} failed validation multiple times.");
                                        }
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(chunkBuffer);
                                }
                            }
                        }
                        catch (Exception ex) when (!token.IsCancellationRequested)
                        {
                            if (inFlightChunk != null)
                            {
                                inFlightChunk.RetryCount++;
                                if (inFlightChunk.RetryCount < 5)
                                {
                                    AppLogger.Warn("Client", $"Re-enqueuing in-flight chunk #{inFlightChunk.ChunkIndex} after worker #{wIdx} disconnect/error.");
                                    chunkQueue.Enqueue(inFlightChunk);
                                }
                            }

                            AppLogger.Warn("Client", $"Worker #{wIdx} [{adapterName}] error: {ex.Message}");
                            int left = Interlocked.Decrement(ref activeWorkersCount);
                            if (left <= 0 && !chunkQueue.IsEmpty)
                            {
                                AppLogger.Error("Client", "All active workers failed. Aborting transfer.");
                                try { linkedCts.Cancel(); } catch { }
                            }
                        }
                        finally
                        {
                            dataClient?.Dispose();
                        }
                    }, token));
                }

                await Task.WhenAll(workerTasks);

                // 6. Send Finalize Message over control stream
                AppLogger.Info("Client", "All chunks transmitted and verified. Sending finalize signal...");

                var finalizeMsg = new TransferFinalizeMessage
                {
                    TransferId = transferId,
                    Success = true
                };
                var finJson = JsonSerializer.Serialize(finalizeMsg);
                var finBytes = Encoding.UTF8.GetBytes(finJson);
                var finLenPrefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(finLenPrefix, finBytes.Length);

                await controlStream.WriteAsync(finLenPrefix, token);
                await controlStream.WriteAsync(finBytes, token);
                await controlStream.FlushAsync(token);

                var totalDuration = (DateTime.UtcNow - startTime).TotalSeconds;
                var avgSpeed = totalDuration > 0 ? totalBytes / totalDuration : 0;

                AppLogger.Success("Client", $"🎉 Transfer completed successfully to '{targetPeer.DisplayName}' in {totalDuration:F2}s! (Avg Speed: {TransferProgress.FormatBytes((long)avgSpeed)}/s)");

                var historyItem = new TransferHistoryItem
                {
                    Id = transferId,
                    PeerId = targetPeer.PeerId,
                    PeerName = targetPeer.DisplayName,
                    Direction = TransferDirection.Sent,
                    FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                    FileCount = manifestFiles.Count,
                    TotalBytes = totalBytes,
                    TransferredBytes = totalBytes,
                    Status = TransferStatus.Completed,
                    TimestampUtc = DateTime.UtcNow,
                    DurationSeconds = totalDuration,
                    AverageSpeedBps = avgSpeed
                };

                _historyManager.Add(historyItem);
                TransferCompleted?.Invoke(historyItem);

                var doneProgress = new TransferProgress
                {
                    TransferId = transferId,
                    PeerName = targetPeer.DisplayName,
                    PeerAvatarColor = targetPeer.AvatarColor,
                    PeerAvatarGlyph = targetPeer.AvatarGlyph,
                    Direction = TransferDirection.Sent,
                    Status = TransferStatus.Completed,
                    FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                    FileCount = manifestFiles.Count,
                    TotalBytes = totalBytes,
                    TransferredBytes = totalBytes,
                    TotalChunks = totalChunks,
                    ChunksCompleted = totalChunks,
                    ChunksVerified = totalChunks,
                    SpeedBytesPerSecond = avgSpeed,
                    PeakSpeedBytesPerSecond = peakSpeed,
                    StartTimeUtc = startTime,
                    EndTimeUtc = DateTime.UtcNow,
                    TargetDirectory = targetPeer.DisplayName,
                    AdapterBytes = new Dictionary<string, long>(adapterContribution)
                };

                foreach (var (adName, b) in adapterContribution)
                {
                    doneProgress.UpdateAdapterContribution(adName, 0);
                }

                ProgressUpdated?.Invoke(doneProgress);

                return true;
            }
            catch (OperationCanceledException)
            {
                AppLogger.Warn("Client", $"Transfer {transferId[..8]} cancelled by user.");
                var cancelProgress = new TransferProgress
                {
                    TransferId = transferId,
                    PeerName = targetPeer.DisplayName,
                    Direction = TransferDirection.Sent,
                    Status = TransferStatus.Cancelled,
                    FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                    FileCount = manifestFiles.Count,
                    TotalBytes = totalBytes,
                    TransferredBytes = Interlocked.Read(ref transferredBytes),
                    ErrorMessage = "Transfer cancelled by user."
                };
                ProgressUpdated?.Invoke(cancelProgress);
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Client", $"Transfer {transferId[..8]} failed: {ex.Message}", ex);
                var failedProgress = new TransferProgress
                {
                    TransferId = transferId,
                    PeerName = targetPeer.DisplayName,
                    Direction = TransferDirection.Sent,
                    Status = TransferStatus.Failed,
                    FileNames = manifestFiles.Select(f => f.FileName).ToList(),
                    FileCount = manifestFiles.Count,
                    TotalBytes = totalBytes,
                    TransferredBytes = Interlocked.Read(ref transferredBytes),
                    ErrorMessage = ex.Message
                };
                ProgressUpdated?.Invoke(failedProgress);
                throw;
            }
            finally
            {
                foreach (var handle in openHandles.Values)
                {
                    try { handle.Dispose(); } catch { }
                }
            }
        }
    }

    private static List<FileManifestItem> BuildManifest(IReadOnlyList<string> paths)
    {
        var result = new List<FileManifestItem>();
        int index = 0;

        foreach (var path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var fi = new FileInfo(path);
                    result.Add(new FileManifestItem
                    {
                        Index = index++,
                        RelativePath = fi.Name,
                        FileName = fi.Name,
                        Size = fi.Length,
                        LocalFullPath = fi.FullName
                    });
                }
                else if (Directory.Exists(path))
                {
                    var di = new DirectoryInfo(path);
                    var allFiles = di.GetFiles("*", SearchOption.AllDirectories);
                    foreach (var file in allFiles)
                    {
                        string relative = Path.GetRelativePath(di.Parent?.FullName ?? di.FullName, file.FullName);
                        result.Add(new FileManifestItem
                        {
                            Index = index++,
                            RelativePath = relative,
                            FileName = file.Name,
                            Size = file.Length,
                            LocalFullPath = file.FullName
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Client", $"Skipped inaccessible file or folder '{path}': {ex.Message}");
            }
        }

        return result;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection closed unexpectedly.");
            totalRead += read;
        }
    }
}
