using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

public class IncomingTransferDecision
{
    public bool Accept { get; set; }
    public string DestinationFolder { get; set; } = string.Empty;
}

public class TransferServer : IDisposable
{
    private readonly SettingsManager _settingsManager;
    private readonly TransferHistoryManager _historyManager;
    private readonly NetworkAdapterService _adapterService;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    // Active receiving sessions keyed by TransferId
    private readonly ConcurrentDictionary<string, ReceivingSession> _activeSessions = new();

    public event Action<TransferRequestMessage, Action<IncomingTransferDecision>>? TransferRequested;
    public event Action<TransferProgress>? ProgressUpdated;
    public event Action<TransferHistoryItem>? TransferCompleted;

    public bool IsRunning => _listener != null;

    public TransferServer(
        SettingsManager settingsManager,
        TransferHistoryManager historyManager,
        NetworkAdapterService adapterService)
    {
        _settingsManager = settingsManager;
        _historyManager = historyManager;
        _adapterService = adapterService;
    }

    public void Start(int port = TransferProtocolConstants.DefaultControlPort)
    {
        Stop();

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start(100);

            var localIps = _adapterService.GetAllLocalIPv4Addresses();
            AppLogger.Success("Server", $"TCP server started on port {port}. Ready for peer chunk streams. Local endpoints: [{string.Join(", ", localIps)}]");
            _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Security("Server", $"Failed to start TCP listener on port {port}: {ex.Message}. Check Windows Firewall permissions.", ex);
            throw;
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;
        }
        catch
        {
            // Ignore
        }

        foreach (var session in _activeSessions.Values)
        {
            session.Dispose();
        }
        _activeSessions.Clear();
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(token);
                var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                _ = Task.Run(() => HandleClientAsync(client, remoteEp, token), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Server", $"Server accept loop notice: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string remoteEp, CancellationToken token)
    {
        client.NoDelay = true;
        client.EnableTcpKeepAlive(3, 1, 5);
        client.ReceiveBufferSize = 4 * 1024 * 1024;
        client.SendBufferSize = 1024 * 1024;

        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Read first 4 bytes to check if it's Magic header (Data chunk stream) or control message length
                var prefixBuffer = new byte[4];
                await ReadExactAsync(stream, prefixBuffer, 0, 4, token);

                uint firstUint = BinaryPrimitives.ReadUInt32LittleEndian(prefixBuffer);

                if (firstUint == TransferProtocolConstants.MagicHeader)
                {
                    // This is a Data Worker stream!
                    await HandleDataWorkerStreamAsync(stream, client, remoteEp, token);
                }
                else
                {
                    // This is a Control message! firstUint is the length of the JSON control payload
                    int length = BinaryPrimitives.ReadInt32LittleEndian(prefixBuffer);
                    await HandleControlMessageAsync(stream, length, remoteEp, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (EndOfStreamException)
            {
                // Normal disconnect from client
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Server", $"Client connection {remoteEp} handling error: {ex.Message}");
            }
        }
    }

    private async Task HandleControlMessageAsync(NetworkStream stream, int length, string remoteEp, CancellationToken token)
    {
        if (length <= 0 || length > 10 * 1024 * 1024)
        {
            AppLogger.Warn("Server", $"Received invalid control message length {length} from {remoteEp}");
            return;
        }

        var jsonBuffer = new byte[length];
        await ReadExactAsync(stream, jsonBuffer, 0, length, token);
        var json = Encoding.UTF8.GetString(jsonBuffer);

        var request = JsonSerializer.Deserialize<TransferRequestMessage>(json);
        if (request == null)
        {
            AppLogger.Warn("Server", $"Could not deserialize TransferRequestMessage from {remoteEp}");
            return;
        }

        AppLogger.Info("Server", $"Transfer request received from '{request.Sender.DisplayName}' ({remoteEp}). Files: {request.Files.Count}, Total: {TransferProgress.FormatBytes(request.TotalBytes)}, TransferId: {request.TransferId[..8]}");

        var settings = _settingsManager.CurrentSettings;
        var decisionTcs = new TaskCompletionSource<IncomingTransferDecision>();

        if (settings.AutoApprove && !settings.AskDownloadLocationEveryTime)
        {
            AppLogger.Info("Server", $"Auto-approve enabled: automatically accepting transfer {request.TransferId[..8]} to '{settings.DefaultDownloadLocation}'");
            decisionTcs.TrySetResult(new IncomingTransferDecision
            {
                Accept = true,
                DestinationFolder = settings.DefaultDownloadLocation
            });
        }
        else
        {
            AppLogger.Info("Server", $"Prompting user to accept/decline transfer {request.TransferId[..8]} from '{request.Sender.DisplayName}'...");
            // Trigger UI event to show prompt/banner to user
            try
            {
                TransferRequested?.Invoke(request, decision =>
                {
                    decisionTcs.TrySetResult(decision);
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Server", $"Failed to notify UI of incoming transfer: {ex.Message}", ex);
                decisionTcs.TrySetResult(new IncomingTransferDecision { Accept = false });
            }
        }

        var decisionResult = await decisionTcs.Task;
        if (!decisionResult.Accept)
        {
            AppLogger.Warn("Server", $"Transfer {request.TransferId[..8]} declined by user.");
            var rejectResponse = new TransferResponseMessage
            {
                TransferId = request.TransferId,
                Accepted = false,
                Reason = "Declined by user."
            };
            await SendControlResponseAsync(stream, rejectResponse, token);
            return;
        }

        // Accepted! Initialize receiving session
        var downloadDir = string.IsNullOrWhiteSpace(decisionResult.DestinationFolder)
            ? settings.DefaultDownloadLocation
            : decisionResult.DestinationFolder;

        AppLogger.Success("Server", $"Transfer {request.TransferId[..8]} ACCEPTED! Destination directory: '{downloadDir}'");

        try
        {
            Directory.CreateDirectory(downloadDir);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Server", $"Cannot create destination directory '{downloadDir}': {ex.Message}", ex);
            var errResponse = new TransferResponseMessage
            {
                TransferId = request.TransferId,
                Accepted = false,
                Reason = $"Receiver cannot create destination directory: {ex.Message}"
            };
            await SendControlResponseAsync(stream, errResponse, token);
            return;
        }

        var session = new ReceivingSession(request, downloadDir);
        try
        {
            session.InitializeFiles();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Server", $"Failed to pre-allocate target files in '{downloadDir}': {ex.Message}", ex);
            session.Dispose();

            var errResponse = new TransferResponseMessage
            {
                TransferId = request.TransferId,
                Accepted = false,
                Reason = $"Receiver failed to open destination files: {ex.Message}"
            };
            await SendControlResponseAsync(stream, errResponse, token);
            return;
        }

        _activeSessions[request.TransferId] = session;

        // Send accept response with our adapter endpoints
        var acceptResponse = new TransferResponseMessage
        {
            TransferId = request.TransferId,
            Accepted = true,
            ReceiverEndpoints = _adapterService.GetAllLocalIPv4Addresses(),
            ReceiverPort = settings.TcpPort
        };

        AppLogger.Info("Server", $"Sending acceptance response to {remoteEp}. Advertised endpoints: [{string.Join(", ", acceptResponse.ReceiverEndpoints)}]:{acceptResponse.ReceiverPort}");

        await SendControlResponseAsync(stream, acceptResponse, token);

        // Notify initial progress
        NotifySessionProgress(session);

        // Wait for finalization message from sender or session completion
        try
        {
            var finalPrefix = new byte[4];
            await ReadExactAsync(stream, finalPrefix, 0, 4, token);
            int finalLen = BinaryPrimitives.ReadInt32LittleEndian(finalPrefix);
            if (finalLen > 0)
            {
                var finalJsonBuf = new byte[finalLen];
                await ReadExactAsync(stream, finalJsonBuf, 0, finalLen, token);
                var finalJson = Encoding.UTF8.GetString(finalJsonBuf);
                var finalMsg = JsonSerializer.Deserialize<TransferFinalizeMessage>(finalJson);
                if (finalMsg != null && finalMsg.Success)
                {
                    AppLogger.Info("Server", $"Received transfer finalize signal for {request.TransferId[..8]}");
                    await FinalizeSessionAsync(session, true, null);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Server", $"Control finalize stream notice: {ex.Message}");
        }
    }

    private static async Task SendControlResponseAsync(NetworkStream stream, TransferResponseMessage response, CancellationToken token)
    {
        var responseJson = JsonSerializer.Serialize(response);
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

        var lenPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenPrefix, responseBytes.Length);

        await stream.WriteAsync(lenPrefix, token);
        await stream.WriteAsync(responseBytes, token);
        await stream.FlushAsync(token);
    }

    private async Task HandleDataWorkerStreamAsync(NetworkStream stream, TcpClient client, string remoteEp, CancellationToken token)
    {
        // Each data packet layout:
        // [1B PacketType] [16B TransferId] [4B FileIndex] [8B ChunkIndex] [8B Offset] [4B Length] [8B Checksum] [DataLength bytes]
        // Header after magic: 1 + 16 + 4 + 8 + 8 + 4 + 8 = 49 bytes.
        var headerBuffer = new byte[49];
        bool firstPacket = true;

        while (!token.IsCancellationRequested && client.Connected)
        {
            if (!firstPacket)
            {
                var magicCheck = new byte[4];
                int read = await stream.ReadAsync(magicCheck.AsMemory(0, 4), token);
                if (read == 0) break; // Client closed connection normally
                if (read < 4) await ReadExactAsync(stream, magicCheck, read, 4 - read, token);

                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(magicCheck);
                if (magic != TransferProtocolConstants.MagicHeader)
                {
                    break;
                }
            }
            firstPacket = false;

            await ReadExactAsync(stream, headerBuffer, 0, 49, token);

            byte packetType = headerBuffer[0];
            byte[] transferIdBytes = new byte[16];
            Array.Copy(headerBuffer, 1, transferIdBytes, 0, 16);
            var transferId = new Guid(transferIdBytes).ToString("N");
            int fileIndex = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(17, 4));
            long chunkIndex = BinaryPrimitives.ReadInt64LittleEndian(headerBuffer.AsSpan(21, 8));
            long offset = BinaryPrimitives.ReadInt64LittleEndian(headerBuffer.AsSpan(29, 8));
            int dataLength = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(37, 4));
            ulong expectedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(headerBuffer.AsSpan(41, 8));

            if (!_activeSessions.TryGetValue(transferId, out var session))
            {
                // Session not found or finished; consume data and return NACK
                await DiscardBytesAsync(stream, dataLength, token);
                SendAck(stream, transferIdBytes, chunkIndex, false);
                continue;
            }

            // Rent memory buffer for chunk payload
            byte[] payload = ArrayPool<byte>.Shared.Rent(dataLength);
            try
            {
                await ReadExactAsync(stream, payload, 0, dataLength, token);

                // Compute xxHash64 checksum
                ulong actualChecksum = ChecksumHelper.ComputeXxHash64(new ReadOnlySpan<byte>(payload, 0, dataLength));
                if (actualChecksum != expectedChecksum)
                {
                    AppLogger.Warn("Server", $"Chunk #{chunkIndex} checksum mismatch from {remoteEp}! Expected: {expectedChecksum:X16}, Actual: {actualChecksum:X16}. Requesting retransmission.");
                    SendAck(stream, transferIdBytes, chunkIndex, false);
                    continue;
                }

                // Checksum verified! Write to disk at exact offset
                await session.WriteChunkAsync(fileIndex, offset, payload, dataLength);

                // Send fast positive ACK
                SendAck(stream, transferIdBytes, chunkIndex, true);

                session.IncrementTransferred(dataLength);
                NotifySessionProgress(session);

                if (session.IsComplete)
                {
                    AppLogger.Info("Server", $"All chunks received for session {session.TransferId[..8]}. Finalizing...");
                    await FinalizeSessionAsync(session, true, null);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private static void SendAck(NetworkStream stream, byte[] transferIdBytes, long chunkIndex, bool success)
    {
        Span<byte> ack = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(ack[..4], TransferProtocolConstants.MagicHeader);
        ack[4] = success ? TransferProtocolConstants.PacketTypeAck : TransferProtocolConstants.PacketTypeNack;
        transferIdBytes.AsSpan(0, 16).CopyTo(ack.Slice(5, 16));
        BinaryPrimitives.WriteInt64LittleEndian(ack.Slice(21, 8), chunkIndex);
        ack[29] = success ? (byte)1 : (byte)0;

        stream.Write(ack);
        stream.Flush();
    }

    private async Task FinalizeSessionAsync(ReceivingSession session, bool success, string? error)
    {
        if (session.Finalized) return;
        session.Finalized = true;

        session.Dispose();
        _activeSessions.TryRemove(session.TransferId, out _);

        // Verify SHA256 hashes for each file if provided in manifest
        if (success)
        {
            foreach (var file in session.Request.Files)
            {
                if (!string.IsNullOrWhiteSpace(file.Sha256))
                {
                    string safeRel = SanitizeRelativePath(file.RelativePath);
                    string targetFile = Path.Combine(session.DownloadFolder, safeRel);
                    if (File.Exists(targetFile))
                    {
                        var actualHash = await ChecksumHelper.ComputeFileSha256Async(targetFile);
                        if (!string.Equals(actualHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            success = false;
                            error = $"SHA-256 hash mismatch for {file.FileName}";
                            AppLogger.Error("Server", error);
                            break;
                        }
                    }
                }
            }
        }

        var duration = (DateTime.UtcNow - session.StartTime).TotalSeconds;
        var avgSpeed = duration > 0 ? session.TransferredBytes / duration : 0;

        if (success)
        {
            AppLogger.Success("Server", $"🎉 Transfer completed successfully from '{session.Request.Sender.DisplayName}' in {duration:F2}s! (Avg Speed: {TransferProgress.FormatBytes((long)avgSpeed)}/s, Saved to: '{session.DownloadFolder}')");
        }
        else
        {
            AppLogger.Error("Server", $"Transfer {session.TransferId[..8]} failed: {error}");
        }

        var historyItem = new TransferHistoryItem
        {
            Id = session.TransferId,
            PeerId = session.Request.Sender.PeerId,
            PeerName = session.Request.Sender.DisplayName,
            Direction = TransferDirection.Received,
            FileNames = session.Request.Files.Select(f => f.FileName).ToList(),
            FileCount = session.Request.Files.Count,
            TotalBytes = session.Request.TotalBytes,
            TransferredBytes = session.TransferredBytes,
            Status = success ? TransferStatus.Completed : TransferStatus.Failed,
            ErrorMessage = error,
            TargetDirectory = session.DownloadFolder,
            TimestampUtc = DateTime.UtcNow,
            DurationSeconds = duration,
            AverageSpeedBps = avgSpeed
        };

        _historyManager.Add(historyItem);
        TransferCompleted?.Invoke(historyItem);

        var finalProgress = session.ToProgress();
        finalProgress.Status = success ? TransferStatus.Completed : TransferStatus.Failed;
        finalProgress.ErrorMessage = error;
        ProgressUpdated?.Invoke(finalProgress);
    }

    private void NotifySessionProgress(ReceivingSession session)
    {
        var progress = session.ToProgress();
        ProgressUpdated?.Invoke(progress);
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return "file";
        // Strip drive letters, leading slashes, and parent dir traversals
        string clean = relativePath.Replace('/', '\\').TrimStart('\\');
        if (clean.Length >= 2 && clean[1] == ':') clean = clean[2..].TrimStart('\\');
        clean = clean.Replace("..\\", "").Replace("..", "");
        return clean;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
            if (read == 0)
                throw new EndOfStreamException("Connection terminated prematurely.");
            totalRead += read;
        }
    }

    private static async Task DiscardBytesAsync(Stream stream, int count, CancellationToken ct)
    {
        byte[] temp = ArrayPool<byte>.Shared.Rent(Math.Min(count, 65536));
        try
        {
            int remaining = count;
            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, temp.Length);
                int read = await stream.ReadAsync(temp.AsMemory(0, toRead), ct);
                if (read == 0) break;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private sealed class ReceivingSession : IDisposable
    {
        public string TransferId => Request.TransferId;
        public TransferRequestMessage Request { get; }
        public string DownloadFolder { get; }
        public DateTime StartTime { get; } = DateTime.UtcNow;
        public bool Finalized { get; set; }

        private readonly Dictionary<int, SafeFileHandle> _fileHandles = new();
        private readonly object _handleLock = new();
        private long _transferredBytes;
        private long _verifiedChunks;
        private DateTime _lastSpeedSampleTime = DateTime.UtcNow;
        private long _lastSpeedSampleBytes;
        private double _currentSpeed;

        public long TransferredBytes => Volatile.Read(ref _transferredBytes);
        public long VerifiedChunks => Volatile.Read(ref _verifiedChunks);
        public bool IsComplete => TransferredBytes >= Request.TotalBytes;

        public ReceivingSession(TransferRequestMessage request, string downloadFolder)
        {
            Request = request;
            DownloadFolder = downloadFolder;
        }

        public void InitializeFiles()
        {
            lock (_handleLock)
            {
                for (int i = 0; i < Request.Files.Count; i++)
                {
                    var file = Request.Files[i];
                    string safeRel = SanitizeRelativePath(file.RelativePath);
                    string fullPath = Path.Combine(DownloadFolder, safeRel);
                    string? dir = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    var handle = File.OpenHandle(
                        fullPath,
                        FileMode.Create,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite,
                        FileOptions.Asynchronous);

                    // Preallocate length to prevent filesystem fragmentation and ensure fast multi-stream random access
                    if (file.Size > 0)
                    {
                        RandomAccess.SetLength(handle, file.Size);
                    }

                    _fileHandles[i] = handle;
                }
            }
        }

        public async ValueTask WriteChunkAsync(int fileIndex, long offset, byte[] buffer, int length)
        {
            SafeFileHandle? handle = null;
            lock (_handleLock)
            {
                _fileHandles.TryGetValue(fileIndex, out handle);
            }

            if (handle != null && !handle.IsClosed && !handle.IsInvalid)
            {
                try
                {
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, length), offset);
                }
                catch (ObjectDisposedException) { }
            }
        }

        public void IncrementTransferred(int bytes)
        {
            Interlocked.Add(ref _transferredBytes, bytes);
            Interlocked.Increment(ref _verifiedChunks);

            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSpeedSampleTime).TotalSeconds;
            if (elapsed >= 0.25)
            {
                long currentBytes = TransferredBytes;
                _currentSpeed = (currentBytes - _lastSpeedSampleBytes) / elapsed;
                _lastSpeedSampleBytes = currentBytes;
                _lastSpeedSampleTime = now;
            }
        }

        public TransferProgress ToProgress()
        {
            long currentBytes = TransferredBytes;
            long total = Request.TotalBytes;
            double speed = _currentSpeed;
            long remainingBytes = Math.Max(0, total - currentBytes);
            TimeSpan eta = speed > 0 ? TimeSpan.FromSeconds(remainingBytes / speed) : TimeSpan.Zero;

            return new TransferProgress
            {
                TransferId = TransferId,
                PeerName = Request.Sender.DisplayName,
                PeerAvatarColor = Request.Sender.AvatarColor,
                PeerAvatarGlyph = Request.Sender.AvatarGlyph,
                Direction = TransferDirection.Received,
                Status = IsComplete ? TransferStatus.Completed : TransferStatus.InProgress,
                FileNames = Request.Files.Select(f => f.FileName).ToList(),
                FileCount = Request.Files.Count,
                TotalBytes = total,
                TransferredBytes = currentBytes,
                TotalChunks = Request.TotalChunks,
                ChunksCompleted = VerifiedChunks,
                ChunksVerified = VerifiedChunks,
                SpeedBytesPerSecond = speed,
                EstimatedRemaining = eta,
                TargetDirectory = DownloadFolder,
                StartTimeUtc = StartTime
            };
        }

        public void Dispose()
        {
            lock (_handleLock)
            {
                foreach (var handle in _fileHandles.Values)
                {
                    try
                    {
                        handle.Dispose();
                    }
                    catch { }
                }
                _fileHandles.Clear();
            }
        }
    }
}
