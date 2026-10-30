using System;
using System.Net.Sockets;

namespace SendIt.Core.Network;

public static class SocketExtensions
{
    public static void EnableTcpKeepAlive(this TcpClient client, int timeSeconds = 3, int intervalSeconds = 1, int retryCount = 5)
    {
        if (client?.Client == null) return;
        EnableTcpKeepAlive(client.Client, timeSeconds, intervalSeconds, retryCount);
    }

    public static void EnableTcpKeepAlive(this Socket socket, int timeSeconds = 3, int intervalSeconds = 1, int retryCount = 5)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, timeSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, intervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, retryCount);
        }
        catch
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    uint onOff = 1;
                    uint keepAliveTimeMs = (uint)(timeSeconds * 1000);
                    uint keepAliveIntervalMs = (uint)(intervalSeconds * 1000);

                    byte[] inOptionValues = new byte[12];
                    BitConverter.GetBytes(onOff).CopyTo(inOptionValues, 0);
                    BitConverter.GetBytes(keepAliveTimeMs).CopyTo(inOptionValues, 4);
                    BitConverter.GetBytes(keepAliveIntervalMs).CopyTo(inOptionValues, 8);

                    socket.IOControl(IOControlCode.KeepAliveValues, inOptionValues, null);
                }
                else
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
            }
            catch
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                }
                catch
                {
                    // Ignored if OS blocks low-level socket options
                }
            }
        }
    }
}
