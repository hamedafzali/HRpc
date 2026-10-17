using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    public class TcpConnection : ITcpConnection
    {
        protected TcpClient _client = new TcpClient();
        protected NetworkStream? _stream;

        // Captured once, right after connecting, rather than read from _client.Client at
        // disconnect time: by the time Disconnected is raised (from the receive loop's teardown,
        // racing CloseAsync's own teardown of _client), the underlying Socket may already be
        // disposed, and Socket.RemoteEndPoint throws ObjectDisposedException on a disposed
        // socket on every target framework. See CHANGELOG for why this is a real race on
        // net8.0/net9.0 too, not just a net48 quirk.
        private IPEndPoint? _remoteEndPoint;

        private readonly ConnectionCore _core;
        private TimeSpan? _keepAlive;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionEventArgs>? Connected;
        public event EventHandler<ConnectionEventArgs>? Disconnected;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public TcpConnection()
        {
            _core = new ConnectionCore(this, () => MessageReceived, () => Connected, () => Disconnected, () => ErrorOccurred,
                () => new ConnectionEventArgs(_remoteEndPoint?.Address.ToString() ?? string.Empty, _remoteEndPoint?.Port ?? 0));
        }

        public bool IsConnected => _core.IsConnected;

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. Messages that
        /// exceed this before a newline terminator is found cause <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and the connection to be dropped. Takes
        /// effect on the next <see cref="ConnectAsync"/> call.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Turns on TCP keepalive with this idle time, so a silently dropped server is detected
        /// (the connection closes and <see cref="Disconnected"/> fires) about this long plus a few
        /// seconds after traffic stops. Null (the default) leaves the OS default, usually off.
        /// Nothing changes on the wire, so it works with any server version. Takes effect on the
        /// next <see cref="ConnectAsync"/> call.
        /// </summary>
        public TimeSpan? KeepAlive
        {
            get => _keepAlive;
            set
            {
                SocketKeepAlive.Validate(value);
                _keepAlive = value;
            }
        }

        public virtual async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            _core.ThrowIfConnected();

            try
            {
                _client = new TcpClient();
#if NETFRAMEWORK
                // Task.FromCanceled + await, not ThrowIfCancellationRequested: the latter
                // throws a bare OperationCanceledException, but a canceled Task always
                // completes its awaiter with TaskCanceledException specifically — the same
                // type net8.0/net9.0 callers observe from the CancellationToken-aware
                // ConnectAsync overload below. Keeping the exception type consistent across
                // targets matters to callers that catch TaskCanceledException specifically.
                if (cancellationToken.IsCancellationRequested)
                {
                    await Task.FromCanceled(cancellationToken).ConfigureAwait(false);
                }

                await _client.ConnectAsync(host, port);
#else
                await _client.ConnectAsync(host, port, cancellationToken);
#endif
                if (_keepAlive.HasValue)
                {
                    SocketKeepAlive.Apply(_client.Client, _keepAlive.Value);
                }

                _stream = _client.GetStream();
                _remoteEndPoint = _client.Client.RemoteEndPoint as IPEndPoint;

                _core.Start(_stream, MaxMessageSizeBytes, new ConnectionEventArgs(host, port), cancellationToken);
            }
            catch (Exception ex)
            {
                _core.MarkConnectFailed(ex);
                throw;
            }
        }

        public Task SendAsync(IEventMessage message)
        {
            return _core.SendAsync(message);
        }

        /// <summary>
        /// Sends <paramref name="message"/> as a request (wire "type":"request", stamped with a
        /// freshly generated "cid") and asynchronously waits for a matching "response" (or
        /// "errorResponse") envelope from the peer, correlated by that cid — see docs/PROTOCOL.md.
        ///
        /// Throws <see cref="TimeoutException"/> if no matching response arrives within
        /// <paramref name="timeout"/>, <see cref="HRpc.Models.RequestFailedException"/> if the peer
        /// replies with an "errorResponse", <see cref="OperationCanceledException"/> if
        /// <paramref name="cancellationToken"/> fires, and <see cref="InvalidOperationException"/>
        /// if the connection is closed (or drops) while this request is still pending. A response
        /// that arrives after the timeout or cancellation is discarded. On timeout or cancellation
        /// a 1.4+ server is also told to cancel the request; older servers are not sent anything.
        /// </summary>
        public Task<IEventMessage> SendAndWaitAsync(IEventMessage message, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return _core.SendAndWaitAsync(message, timeout, cancellationToken);
        }

        public Task CloseAsync()
        {
            return _core.CloseAsync(async () =>
            {
                if (_stream != null)
                {
#if NETFRAMEWORK
                    _stream.Dispose();
                    await Task.CompletedTask.ConfigureAwait(false);
#else
                    await _stream.DisposeAsync().ConfigureAwait(false);
#endif
                    _stream = null;
                }

                _client.Close();
            });
        }

        public void Dispose()
        {
            _core.Dispose();
            _client.Dispose();
        }
    }
}
