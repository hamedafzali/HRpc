#if NETFRAMEWORK
using Newtonsoft.Json.Linq;
#else
using System.Text.Json;
#endif

namespace HRpc.Interfaces
{
    /// <summary>
    /// A message as seen at the public pub/sub boundary (<c>SendAsync</c>,
    /// <c>MessageReceived</c>) — the API-level counterpart to
    /// <see cref="HRpc.Models.MessageEnvelope"/>, the wire-level type. As of B3-EXT
    /// (v1.2.0), the payload is a deferred-parse typed JSON value (<c>JsonElement</c> on
    /// net8.0/net9.0, <c>JToken</c> on net48), not a flat <c>string</c>. This is a breaking
    /// change for any external <see cref="IEventMessage"/> implementer: the previous
    /// <c>string Payload</c> member no longer exists on this interface. See PROTOCOL.md and
    /// CHANGELOG.md for migration guidance.
    /// </summary>
    public interface IEventMessage
    {
        string EventName { get; }

#if NETFRAMEWORK
        /// <summary>The payload as a deferred-parse JSON value. Prefer <see cref="GetPayload{T}"/>,
        /// <see cref="TryGetPayload{T}"/>, or <see cref="GetPayloadAsString"/> over touching this
        /// directly.</summary>
        JToken PayloadValue { get; }
#else
        /// <summary>The payload as a deferred-parse JSON value. Prefer <see cref="GetPayload{T}"/>,
        /// <see cref="TryGetPayload{T}"/>, or <see cref="GetPayloadAsString"/> over touching this
        /// directly.</summary>
        JsonElement PayloadValue { get; }
#endif

        /// <summary>
        /// Deserializes <see cref="PayloadValue"/> as <typeparamref name="T"/>. Throws on a genuine
        /// shape mismatch — see <see cref="HRpc.Models.MessageEnvelope.GetPayload{T}"/>
        /// for the exact contract, which this mirrors.
        /// </summary>
        T? GetPayload<T>();

        /// <summary>Non-throwing form of <see cref="GetPayload{T}"/>.</summary>
        bool TryGetPayload<T>(out T? value);

        /// <summary>
        /// Never throws. Returns the payload's string content if it is a JSON string, otherwise
        /// the raw JSON text of whatever value is present.
        /// </summary>
        string GetPayloadAsString();
    }
}
