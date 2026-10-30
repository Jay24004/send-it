using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SendIt.Core.Models;
using SendIt.Core.Network;
using SendIt.Core.Storage;
using Xunit;

namespace SendIt.Tests;

public class ChecksumAndTransferTests : IDisposable
{
    private readonly string _tempDir;

    public ChecksumAndTransferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SendIt_TransferTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task ChecksumHelper_ComputesValidXxHashAndSha256()
    {
        byte[] testData = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        ulong hash1 = ChecksumHelper.ComputeXxHash64(testData);
        ulong hash2 = ChecksumHelper.ComputeXxHash64(testData);
        Assert.Equal(hash1, hash2);
        Assert.NotEqual(0UL, hash1);

        string testFilePath = Path.Combine(_tempDir, "sample.bin");
        await File.WriteAllBytesAsync(testFilePath, testData);

        string sha = await ChecksumHelper.ComputeFileSha256Async(testFilePath);
        Assert.False(string.IsNullOrWhiteSpace(sha));
        Assert.Equal(64, sha.Length);
    }

    [Fact]
    public async Task EndToEnd_MultiStreamChunkTransfer_SucceedsAndVerifiesIntegrity()
    {
        // 1. Create a 3 MB binary test file with random payload
        string sourceDir = Path.Combine(_tempDir, "Source");
        string destDir = Path.Combine(_tempDir, "Destination");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(destDir);

        string testFileName = "test_payload.dat";
        string testFilePath = Path.Combine(sourceDir, testFileName);
        byte[] originalBytes = new byte[3 * 1024 * 1024 + 123]; // 3 MB + 123 bytes
        Random.Shared.NextBytes(originalBytes);
        await File.WriteAllBytesAsync(testFilePath, originalBytes);

        string originalSha = await ChecksumHelper.ComputeFileSha256Async(testFilePath);

        // 2. Setup Server
        int serverPort = 43100 + Random.Shared.Next(500);
        string serverSettingsFile = Path.Combine(_tempDir, "server_settings.json");
        string serverHistoryFile = Path.Combine(_tempDir, "server_history.json");

        var serverSettingsMgr = new SettingsManager(serverSettingsFile);
        serverSettingsMgr.CurrentSettings.AutoApprove = true;
        serverSettingsMgr.CurrentSettings.DefaultDownloadLocation = destDir;
        serverSettingsMgr.CurrentSettings.TcpPort = serverPort;

        var serverHistoryMgr = new TransferHistoryManager(serverHistoryFile);
        var adapterService = new NetworkAdapterService();

        using var server = new TransferServer(serverSettingsMgr, serverHistoryMgr, adapterService);
        server.Start(serverPort);

        // 3. Setup Client
        string clientSettingsFile = Path.Combine(_tempDir, "client_settings.json");
        string clientHistoryFile = Path.Combine(_tempDir, "client_history.json");
        var clientSettingsMgr = new SettingsManager(clientSettingsFile);
        clientSettingsMgr.CurrentSettings.ParallelStreams = 4;
        clientSettingsMgr.CurrentSettings.ChunkSizeMb = 1; // 1 MB chunks to test multi-chunking

        var clientHistoryMgr = new TransferHistoryManager(clientHistoryFile);
        var client = new TransferClient(clientSettingsMgr, clientHistoryMgr, adapterService);

        var targetPeer = new DiscoveredPeer
        {
            PeerId = "server-peer-1",
            DisplayName = "TestReceiver",
            TcpPort = serverPort,
            IpAddresses = ["127.0.0.1"]
        };

        // 4. Send File
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        bool success = await client.SendFilesAsync(targetPeer, [testFilePath], cts.Token);

        Assert.True(success, "File transfer should report success.");

        // Wait a short moment for finalization
        await Task.Delay(300);

        // 5. Verify Transferred File at Destination
        string receivedFilePath = Path.Combine(destDir, testFileName);
        Assert.True(File.Exists(receivedFilePath), "Received file should exist at destination.");

        byte[] receivedBytes = await File.ReadAllBytesAsync(receivedFilePath);
        Assert.Equal(originalBytes.Length, receivedBytes.Length);

        string receivedSha = await ChecksumHelper.ComputeFileSha256Async(receivedFilePath);
        Assert.Equal(originalSha, receivedSha);

        // 6. Verify History records
        Assert.Single(clientHistoryMgr.Items);
        Assert.Equal(TransferStatus.Completed, clientHistoryMgr.Items[0].Status);

        Assert.Single(serverHistoryMgr.Items);
        Assert.Equal(TransferStatus.Completed, serverHistoryMgr.Items[0].Status);
    }
}
