using System.Threading;

namespace HRpc.Core
{
    /// <summary>
    /// Per-request information passed to an <c>OnRequest</c> handler registered with the
    /// <c>(message, context)</c> overload.
    /// </summary>
    public sealed class RequestContext
    {
        public RequestContext(string clientId, CancellationToken cancellationToken)
        {
            ClientId = clientId;
            CancellationToken = cancellationToken;
        }

        /// <summary>The id of the client that sent the request (see <c>ConnectedClients</c>).</summary>
        public string ClientId { get; }

        /// <summary>
        /// Trips when the client cancels the request (its <c>SendAndWaitAsync</c> token fired or
        /// timed out), when the client disconnects, or when the server stops. A handler that
        /// throws <see cref="System.OperationCanceledException"/> after this trips sends no reply.
        /// </summary>
        public CancellationToken CancellationToken { get; }
    }
}
