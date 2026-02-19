using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpEventFramework.Events;
using TcpEventFramework.Interfaces;
using TcpEventFramework.Models;
using TcpEventFramework.Utils;
using ErrorEventArgs = TcpEventFramework.Events.ErrorEventArgs;

namespace TcpEventFramework.Core
{
    public class PipeServer : ITcpServer, IDisposable
    {
        private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clientTasks = new ConcurrentDictionary<NamedPipeServerStream, Task>();
        private CancellationTokenSource? _serverCts;
        private volatile bool _running;
        private string _pipeName = string.Empty;

        public event EventHandler<ConnectionEventArgs>? ClientConnected;
        public event EventHandler<ConnectionEventArgs>? ClientDisconnected;
        public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
        public event EventHandler<ErrorEventArgs>? ErrorOccurred;

        // Optional message to send upon client connection
        public IEventMessage? InitialMessage { get; set; }

        protected void OnErrorOccurred(string message, Exception? ex = null)
        {
            ErrorOccurred?.Invoke(this, new ErrorEventArgs(message, ex));
        }

        public async Task StartAsync(int port, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("PipeServer does not support port-based StartAsync. Use StartAsync(pipeName, cancellationToken).");
        }

        public async Task StartAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
            {
                throw new ArgumentException("Pipe name cannot be null/empty.", nameof(pipeName));
            }

            if (_running)
            {
                throw new InvalidOperationException("Server is already running.");
            }

            _pipeName = pipeName;
            _running = true;
            _serverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var serverToken = _serverCts.Token;

            try
            {
                while (!serverToken.IsCancellationRequested)
                {
                    var serverStream = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );

                    var task = AcceptAndHandleClientAsync(serverStream, serverToken);
                    _clientTasks[serverStream] = task;
                    _ = task.ContinueWith(_ =>
                    {
                        _clientTasks.TryRemove(serverStream, out var _);
                    }, TaskScheduler.Default);

                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in StartAsync", ex);
                throw;
            }
            finally
            {
                _running = false;
            }
        }

        private async Task AcceptAndHandleClientAsync(NamedPipeServerStream serverStream, CancellationToken cancellationToken)
        {
            try
            {
                await serverStream.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                ClientConnected?.Invoke(this, new ConnectionEventArgs(_pipeName, 0));

                await HandleClientAsync(serverStream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
                try { serverStream.Dispose(); } catch { }
            }
            catch (Exception ex)
            {
                try { serverStream.Dispose(); } catch { }
                OnErrorOccurred("Error accepting pipe client", ex);
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

            try
            {
                // Send initial message if configured
                if (InitialMessage != null)
                {
                    var envelope = new MessageEnvelope
                    {
                        EventName = InitialMessage.EventName,
                        Payload = InitialMessage.Payload
                    };
                    var bytes = Encoding.UTF8.GetBytes(envelope.Serialize() + "\n");
                    await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                }

                var line = await reader.ReadLineAsync().WithCancellation(cancellationToken).ConfigureAwait(false);
                if (line == null)
                {
                    return;
                }

                var message = MessageEnvelope.Deserialize(line);
                MessageReceived?.Invoke(this, new MessageReceivedEventArgs(
                    new EventMessage(message.EventName, message.Payload)
                ));
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                OnErrorOccurred("Error in HandleClientAsync", ex);
            }
            finally
            {
                ClientDisconnected?.Invoke(this, new ConnectionEventArgs(_pipeName, 0));

                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // ignore
                }
            }
        }

        public async Task StopAsync()
        {
            _running = false;

            var cts = _serverCts;
            cts?.Cancel();

            foreach (var kvp in _clientTasks)
            {
                try
                {
                    kvp.Key.Dispose();
                }
                catch (Exception)
                {
                    // ignore
                }
            }

            var runningTasks = _clientTasks.Values;
            await Task.WhenAll(runningTasks).ConfigureAwait(false);

            _clientTasks.Clear();
            cts?.Dispose();
            _serverCts = null;
        }

        public void Dispose()
        {
            try
            {
                _running = false;

            }
            catch (Exception)
            {
                // ignore
            }
        }
    }
}
