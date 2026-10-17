using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HRpc.Interfaces;
using HRpc.Models;
using HRpc.Utils;

namespace HRpc.Core
{
    /// <summary>
    /// The wire protocol for one open stream, shared by every transport: newline framing, the
    /// write lock, request/response correlation, the 1.4 capability handshake, cancellation and
    /// request dispatch. Transports only open the stream and own its lifetime.
    ///
    /// A client-role session dispatches everything inline on the read loop, exactly like 1.3. A
    /// server-role session reads into a bounded queue that a single processor drains in order, so
    /// "cancel" envelopes can be seen while a handler runs. With MaxConcurrentRequests = 1 the
    /// processor awaits each handler before touching the next message, which is 1.3's sequential
    /// semantics.
    /// </summary>
    internal sealed class MessageSession
    {
        private const int AbandonedCidCapacity = 1024;
        private const int ServerQueueCapacity = 64;

        private static readonly string[] LocalCaps = { MessageEnvelope.CapabilityCancel };

        private readonly Stream _stream;
        private readonly int _maxMessageSizeBytes;
        private readonly Action<MessageEnvelope> _onMessage;
        private readonly Action<string, Exception?> _onError;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

        // Client role.
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>> _pendingRequests =
            new ConcurrentDictionary<string, TaskCompletionSource<MessageEnvelope>>();
        private readonly HashSet<string> _abandonedCids = new HashSet<string>();
        private readonly Queue<string> _abandonedOrder = new Queue<string>();
        private int _capsSent;
        private volatile bool _peerSupportsCancel;

        // Server role.
        private readonly Func<string, Func<IEventMessage, RequestContext, Task<object?>>?>? _resolveHandler;
        private readonly int _maxConcurrentRequests;
        private readonly ConcurrentDictionary<string, RequestState> _inflight = new ConcurrentDictionary<string, RequestState>();
        private int _helloSent;

        private MessageSession(
            Stream stream,
            int maxMessageSizeBytes,
            string? clientId,
            Action<MessageEnvelope> onMessage,
            Action<string, Exception?> onError,
            Func<string, Func<IEventMessage, RequestContext, Task<object?>>?>? resolveHandler,
            int maxConcurrentRequests)
        {
            _stream = stream;
            _maxMessageSizeBytes = maxMessageSizeBytes;
            ClientId = clientId;
            _onMessage = onMessage;
            _onError = onError;
            _resolveHandler = resolveHandler;
            _maxConcurrentRequests = maxConcurrentRequests;
        }

        public static MessageSession ForClient(Stream stream, int maxMessageSizeBytes,
            Action<MessageEnvelope> onMessage, Action<string, Exception?> onError)
        {
            return new MessageSession(stream, maxMessageSizeBytes, null, onMessage, onError, null, 1);
        }

        public static MessageSession ForServer(Stream stream, int maxMessageSizeBytes, string clientId,
            Action<MessageEnvelope> onMessage, Action<string, Exception?> onError,
            Func<string, Func<IEventMessage, RequestContext, Task<object?>>?> resolveHandler, int maxConcurrentRequests)
        {
            return new MessageSession(stream, maxMessageSizeBytes, clientId, onMessage, onError, resolveHandler, maxConcurrentRequests);
        }

        public string? ClientId { get; }

        private bool IsServer => _resolveHandler != null;

        // Serializes writes: concurrent WriteAsync calls on one stream can interleave partial
        // writes and corrupt the newline framing.
        public async Task WriteAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default)
        {
            var bytes = Encoding.UTF8.GetBytes(envelope.Serialize() + "\n");

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Reads until end of stream, a fatal error or cancellation. Reports its own errors and
        /// only returns normally (or with OperationCanceledException).
        /// </summary>
        public Task RunAsync(CancellationToken cancellationToken, string fatalErrorMessage)
        {
            return IsServer
                ? RunServerAsync(cancellationToken, fatalErrorMessage)
                : RunClientAsync(cancellationToken);
        }

        // ---- client role ----------------------------------------------------------------

        private async Task RunClientAsync(CancellationToken cancellationToken)
        {
            var reader = new BoundedLineReader(_stream, _maxMessageSizeBytes);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    MessageEnvelope msg;
                    try
                    {
                        msg = MessageEnvelope.Deserialize(line);
                    }
                    catch (Exception ex) when (ReceiveLoopErrors.IsRecoverableParseFailure(ex))
                    {
                        // Recoverable: the line boundary is intact, so resynchronization is free.
                        // Anything not a genuine parse failure falls through to the outer catch.
                        _onError(ex.Message, ex);
                        continue;
                    }

                    if (msg.Type == MessageEnvelope.MessageTypeHello)
                    {
                        _peerSupportsCancel = msg.Caps != null
                            && Array.IndexOf(msg.Caps, MessageEnvelope.CapabilityCancel) >= 0;
                        continue;
                    }

                    if ((msg.Type == MessageEnvelope.MessageTypeResponse || msg.Type == MessageEnvelope.MessageTypeErrorResponse)
                        && msg.Cid != null)
                    {
                        if (_pendingRequests.TryRemove(msg.Cid, out var pendingTcs))
                        {
                            // Routed to its SendAndWaitAsync caller instead of MessageReceived.
                            pendingTcs.TrySetResult(msg);
                            continue;
                        }

                        if (ForgetAbandoned(msg.Cid))
                        {
                            // A late reply to a request that already timed out or was cancelled.
                            continue;
                        }
                    }

                    _onMessage(msg);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when closing the connection.
            }
            catch (Exception ex)
            {
                _onError(ex.Message, ex);
            }
        }

        public async Task<IEventMessage> SendAndWaitAsync(IEventMessage message, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var cid = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[cid] = tcs;

            try
            {
                var envelope = new MessageEnvelope(message.EventName, message.PayloadValue)
                {
                    Cid = cid,
                    Type = MessageEnvelope.MessageTypeRequest,
                    // Advertised on requests only, so event-only traffic stays byte-identical to
                    // 1.3. Repeated until one write succeeds; the server answers hello once.
                    Caps = Volatile.Read(ref _capsSent) == 0 ? LocalCaps : null,
                };
                await WriteAsync(envelope).ConfigureAwait(false);
                Volatile.Write(ref _capsSent, 1);

                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                using var registration = linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token));

                MessageEnvelope response;
                try
                {
                    response = await tcs.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Abandon(cid, message.EventName);
                    if (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        throw new TimeoutException($"No response for request '{message.EventName}' (cid {cid}) within {timeout}.");
                    }

                    throw;
                }

                if (response.Type == MessageEnvelope.MessageTypeErrorResponse)
                {
                    throw new RequestFailedException(response.Error?.Code, response.Error?.Message ?? "Request failed.");
                }

                return new EventMessage(response.EventName, response.PayloadValue);
            }
            finally
            {
                _pendingRequests.TryRemove(cid, out _);
            }
        }

        /// <summary>
        /// Fails every still-pending <see cref="SendAndWaitAsync"/> call rather than leaving it
        /// awaiting a response that can never arrive.
        /// </summary>
        public void FailPendingRequests()
        {
            foreach (var cid in _pendingRequests.Keys)
            {
                if (_pendingRequests.TryRemove(cid, out var tcs))
                {
                    tcs.TrySetException(new InvalidOperationException("Connection closed while awaiting a response."));
                }
            }
        }

        // Remembers a timed-out or cancelled cid so its late reply is dropped instead of surfacing
        // as MessageReceived, and tells a cancel-capable server to stop working on it.
        private void Abandon(string cid, string eventName)
        {
            lock (_abandonedCids)
            {
                if (_abandonedCids.Add(cid))
                {
                    _abandonedOrder.Enqueue(cid);
                    if (_abandonedOrder.Count > AbandonedCidCapacity)
                    {
                        _abandonedCids.Remove(_abandonedOrder.Dequeue());
                    }
                }
            }

            if (_peerSupportsCancel)
            {
                _ = SendCancelAsync(cid, eventName);
            }
        }

        private bool ForgetAbandoned(string cid)
        {
            lock (_abandonedCids)
            {
                // The cid stays in _abandonedOrder until evicted; removing it from the set is
                // enough, and eviction of a cid that is no longer in the set is a no-op.
                return _abandonedCids.Remove(cid);
            }
        }

        private async Task SendCancelAsync(string cid, string eventName)
        {
            try
            {
                await WriteAsync(new MessageEnvelope(eventName, (object?)null)
                {
                    Cid = cid,
                    Type = MessageEnvelope.MessageTypeCancel,
                }).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: the connection may already be gone, which cancels the work anyway.
            }
        }

        // ---- server role ----------------------------------------------------------------

        private async Task RunServerAsync(CancellationToken shutdownToken, string fatalErrorMessage)
        {
            // Trips on end of stream, a fatal error or shutdown, so handlers see the client go away.
            using var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            var queue = new WorkQueue(ServerQueueCapacity);
            var processor = ProcessAsync(queue, lifetimeCts, shutdownToken);
            var reader = new BoundedLineReader(_stream, _maxMessageSizeBytes);
            Exception? fatal = null;

            try
            {
                while (!lifetimeCts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(lifetimeCts.Token).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    MessageEnvelope message;
                    try
                    {
                        message = MessageEnvelope.Deserialize(line);
                    }
                    catch (Exception ex) when (ReceiveLoopErrors.IsRecoverableParseFailure(ex))
                    {
                        // Queued rather than raised here, so it keeps its place relative to the
                        // messages before it.
                        await queue.AddAsync(new WorkItem(ex), lifetimeCts.Token).ConfigureAwait(false);
                        continue;
                    }

                    if (message.Caps != null && Interlocked.Exchange(ref _helloSent, 1) == 0)
                    {
                        // The client proved it is 1.4+, so it understands hello. Written before
                        // the request that carried the caps can be answered.
                        await WriteAsync(new MessageEnvelope(string.Empty, (object?)null)
                        {
                            Type = MessageEnvelope.MessageTypeHello,
                            Caps = LocalCaps,
                        }, lifetimeCts.Token).ConfigureAwait(false);
                    }

                    if (message.Type == MessageEnvelope.MessageTypeCancel)
                    {
                        CancelInflight(message.Cid);
                        continue;
                    }

                    if (message.Type == MessageEnvelope.MessageTypeHello)
                    {
                        continue;
                    }

                    RequestState? request = null;
                    if (message.Type == MessageEnvelope.MessageTypeRequest && message.Cid != null)
                    {
                        request = new RequestState(message.Cid, CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token));
                        request.Registered = _inflight.TryAdd(message.Cid, request);
                    }

                    await queue.AddAsync(new WorkItem(message, request), lifetimeCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown, or the processor stopped.
            }
            catch (Exception ex)
            {
                fatal = ex;
            }
            finally
            {
                lifetimeCts.Cancel();
                queue.Complete();
            }

            // Everything read before the end of the stream is still delivered, in order, as it
            // was in 1.3, and only then is a fatal read error reported.
            await processor.ConfigureAwait(false);

            if (fatal != null)
            {
                _onError(fatalErrorMessage, fatal);
            }
        }

        private void CancelInflight(string? cid)
        {
            if (cid == null || !_inflight.TryGetValue(cid, out var request))
            {
                return;
            }

            request.CancelledByPeer = true;

            // Off the read loop: Cancel() runs the handler's token callbacks synchronously.
            _ = Task.Run(() =>
            {
                try
                {
                    request.Cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Finished in the meantime.
                }
            });
        }

        private async Task ProcessAsync(WorkQueue queue, CancellationTokenSource lifetimeCts, CancellationToken shutdownToken)
        {
            // Yield first so the read loop starts before this one runs.
            await Task.Yield();

            var running = new List<Task>();
            using var limiter = new SemaphoreSlim(_maxConcurrentRequests, _maxConcurrentRequests);

            try
            {
                while (true)
                {
                    var item = await queue.TakeAsync(shutdownToken).ConfigureAwait(false);
                    if (item == null)
                    {
                        break;
                    }

                    if (item.ParseError != null)
                    {
                        _onError(item.ParseError.Message, item.ParseError);
                        continue;
                    }

                    var message = item.Message!;
                    var request = item.Request;
                    var handler = request != null ? _resolveHandler!(message.EventName) : null;

                    if (request == null || handler == null)
                    {
                        if (request != null)
                        {
                            Finish(request);
                        }

                        _onMessage(message);
                        continue;
                    }

                    if (_maxConcurrentRequests == 1)
                    {
                        await HandleRequestAsync(message, request, handler, shutdownToken).ConfigureAwait(false);
                        continue;
                    }

                    await limiter.WaitAsync(shutdownToken).ConfigureAwait(false);
                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await HandleRequestAsync(message, request, handler, shutdownToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            limiter.Release();
                        }
                    }));

                    if (running.Count > _maxConcurrentRequests * 2)
                    {
                        running.RemoveAll(t => t.IsCompleted);
                    }
                }
            }
            catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
            {
                // Server stopping.
            }
            catch (Exception ex)
            {
                // A bug here must not leave the reader blocked on a full queue.
                lifetimeCts.Cancel();
                _onError("Error in HandleClientAsync", ex);
            }
            finally
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
        }

        private async Task HandleRequestAsync(MessageEnvelope request, RequestState state,
            Func<IEventMessage, RequestContext, Task<object?>> handler, CancellationToken shutdownToken)
        {
            try
            {
                if (state.CancelledByPeer)
                {
                    return;
                }

                MessageEnvelope response;
                try
                {
                    var context = new RequestContext(ClientId!, state.Cts.Token);
                    var result = await handler(new EventMessage(request.EventName, request.PayloadValue), context).ConfigureAwait(false);
                    response = new MessageEnvelope(request.EventName, result)
                    {
                        Cid = request.Cid,
                        Type = MessageEnvelope.MessageTypeResponse,
                    };
                }
                catch (OperationCanceledException) when (state.Cts.IsCancellationRequested)
                {
                    // Cancelled by the client, a disconnect or shutdown: nobody wants the reply.
                    return;
                }
                catch (Exception ex)
                {
                    response = new MessageEnvelope(request.EventName, (object?)null)
                    {
                        Cid = request.Cid,
                        Type = MessageEnvelope.MessageTypeErrorResponse,
                        Error = new MessageError { Code = ex.GetType().Name, Message = ex.Message },
                    };
                }

                if (state.CancelledByPeer)
                {
                    return;
                }

                try
                {
                    await WriteAsync(response, shutdownToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // Server stopping.
                }
                catch (Exception ex)
                {
                    _onError("Error in HandleClientAsync", ex);
                }
            }
            finally
            {
                Finish(state);
            }
        }

        private void Finish(RequestState state)
        {
            if (state.Registered)
            {
                ((ICollection<KeyValuePair<string, RequestState>>)_inflight)
                    .Remove(new KeyValuePair<string, RequestState>(state.Cid, state));
            }

            state.Cts.Dispose();
        }

        private sealed class RequestState
        {
            public RequestState(string cid, CancellationTokenSource cts)
            {
                Cid = cid;
                Cts = cts;
            }

            public string Cid { get; }
            public CancellationTokenSource Cts { get; }
            public bool Registered { get; set; }
            public volatile bool CancelledByPeer;
        }

        private sealed class WorkItem
        {
            public WorkItem(MessageEnvelope message, RequestState? request)
            {
                Message = message;
                Request = request;
            }

            public WorkItem(Exception parseError)
            {
                ParseError = parseError;
            }

            public MessageEnvelope? Message { get; }
            public RequestState? Request { get; }
            public Exception? ParseError { get; }
        }

        // A bounded single-producer, single-consumer async queue. System.Threading.Channels would
        // do, but it is an extra package on net48.
        private sealed class WorkQueue
        {
            private readonly Queue<WorkItem> _items = new Queue<WorkItem>();
            private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
            private readonly SemaphoreSlim _space;

            public WorkQueue(int capacity)
            {
                _space = new SemaphoreSlim(capacity, capacity);
            }

            public async Task AddAsync(WorkItem item, CancellationToken cancellationToken)
            {
                await _space.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_items)
                {
                    _items.Enqueue(item);
                }

                _available.Release();
            }

            // Called once, by the producer, after its last AddAsync.
            public void Complete()
            {
                _available.Release();
            }

            public async Task<WorkItem?> TakeAsync(CancellationToken cancellationToken)
            {
                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                lock (_items)
                {
                    if (_items.Count == 0)
                    {
                        return null;
                    }

                    var item = _items.Dequeue();
                    _space.Release();
                    return item;
                }
            }
        }
    }
}
