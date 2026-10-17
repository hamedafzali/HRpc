using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Events;
using HRpc.Interfaces;
using HRpc.Utils;
using ErrorEventArgs = HRpc.Events.ErrorEventArgs;

namespace HRpc.Core
{
    public class PipeConnection : IPipeConnection
    {
        protected NamedPipeClientStream? _client;
        protected Stream? _stream;

        private readonly ConnectionCore _core;
        private string _pipeName = string.Empty;

        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ConnectionEventArgs>? Connected;
        public event EventHandler<ConnectionEventArgs>? Disconnected;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        public PipeConnection()
        {
            _core = new ConnectionCore(this, () => MessageReceived, () => Connected, () => Disconnected, () => ErrorOccurred,
                () => new ConnectionEventArgs(_pipeName, 0));
        }

        public bool IsConnected => _core.IsConnected;

        /// <summary>
        /// Maximum size, in UTF-8 encoded bytes, of a single incoming message. Messages that
        /// exceed this before a newline terminator is found cause <see cref="ErrorOccurred"/> to
        /// fire with a <see cref="LineTooLongException"/> and the connection to be dropped. Takes
        /// effect on the next <see cref="ConnectAsync(string, CancellationToken)"/> call.
        /// </summary>
        public int MaxMessageSizeBytes { get; set; } = MessageSizeLimits.DefaultMaxMessageSizeBytes;

        /// <summary>
        /// Only connect to a pipe server running as the current user; connecting to one owned by
        /// anyone else throws <see cref="UnauthorizedAccessException"/>. Off by default. Takes
        /// effect on the next <see cref="ConnectAsync(string, CancellationToken)"/> call.
        /// </summary>
        public bool CurrentUserOnly { get; set; }

        public async Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("Pipe name cannot be null/empty.", nameof(pipeName));
            }

            _core.ThrowIfConnected();

            try
            {
                _pipeName = pipeName;
                _client = PipeAccess.CreateClient(pipeName, CurrentUserOnly);
                await _client.ConnectAsync(cancellationToken);
                PipeAccess.VerifyServerOwner(_client, CurrentUserOnly);
                _stream = _client;

                _core.Start(_stream, MaxMessageSizeBytes, new ConnectionEventArgs(pipeName, 0), cancellationToken);
            }
            catch (Exception ex)
            {
                _core.MarkConnectFailed(ex);
                throw;
            }
        }

        Task ITcpConnection.ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            throw new NotSupportedException("PipeConnection does not support host/port ConnectAsync. Use ConnectAsync(pipeName, cancellationToken).");
        }

        public Task SendAsync(IEventMessage message)
        {
            return _core.SendAsync(message);
        }

        /// <summary>
        /// Sends <paramref name="message"/> as a request and waits for the matching response. See
        /// <see cref="TcpConnection.SendAndWaitAsync"/> for the full contract this mirrors.
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

                if (_client != null)
                {
                    _client.Dispose();
                    _client = null;
                }
            });
        }

        public void Dispose()
        {
            _core.Dispose();
            _client?.Dispose();
        }
    }
}
