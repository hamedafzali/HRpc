using System;
using System.Net.Sockets;

namespace HRpc.Utils
{
    /// <summary>
    /// Turns on TCP keepalive so the OS notices a silently dropped peer (cable pulled, machine
    /// crashed, NAT entry expired) and the receive loop ends. Nothing extra goes on the wire at
    /// the HRpc level, so it works with any peer, including 1.3.
    /// </summary>
    internal static class SocketKeepAlive
    {
        // Probes after the idle time, one second apart. A peer is declared dead after about
        // idle + RetryCount seconds (Windows on net48 always uses 10 probes).
        private const int IntervalSeconds = 1;
        private const int RetryCount = 3;

        public static void Validate(TimeSpan? keepAlive)
        {
            if (keepAlive.HasValue && keepAlive.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(keepAlive), "KeepAlive must be positive, or null to leave it off.");
            }
        }

        public static void Apply(Socket socket, TimeSpan keepAlive)
        {
            var idleSeconds = Math.Max(1, (int)Math.Ceiling(keepAlive.TotalSeconds));
#if NETFRAMEWORK
            // tcp_keepalive { u_long onoff; u_long keepalivetime; u_long keepaliveinterval; }, in ms.
            var values = new byte[12];
            BitConverter.GetBytes(1u).CopyTo(values, 0);
            BitConverter.GetBytes((uint)idleSeconds * 1000u).CopyTo(values, 4);
            BitConverter.GetBytes((uint)IntervalSeconds * 1000u).CopyTo(values, 8);
            socket.IOControl(IOControlCode.KeepAliveValues, values, null);
#else
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, idleSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, IntervalSeconds);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, RetryCount);
            }
            catch (SocketException)
            {
                // Older Windows builds don't support setting the probe count; the OS default applies.
            }
#endif
        }
    }
}
