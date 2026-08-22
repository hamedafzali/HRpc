using System;
using HRpc.Interfaces;
using HRpc.Utils;

#if NETFRAMEWORK
using Newtonsoft.Json.Linq;
#else
using System.Text.Json;
#endif

namespace HRpc.Models
{
    /// <summary>
    /// A message at the public pub/sub boundary — constructed by a sender to pass to
    /// <c>SendAsync</c>, and constructed by HRpc's receive loops to hand to
    /// <c>MessageReceived</c> subscribers. Stores its payload the same way
    /// <see cref="MessageEnvelope"/> does: a deferred-parse JSON value, not a flat string (see
    /// <see cref="IEventMessage"/> and PROTOCOL.md's "Payload representation" section).
    ///
    /// TWO CONSTRUCTORS, TWO DISTINCT MEANINGS — see PROTOCOL.md for the full reasoning:
    /// <list type="bullet">
    /// <item><see cref="EventMessage(string, string)"/> — the argument IS the payload; it is
    /// embedded as a JSON string value on the wire. Correct for a genuinely textual payload
    /// (e.g. <c>"Ping"</c>). This is a literal reading, never a parse attempt — a JSON-looking
    /// string passed here is embedded as a string, not as the object it happens to describe.</item>
    /// <item><see cref="EventMessage(string, object)"/> — the argument is a value to be
    /// serialized and embedded as a nested JSON value. Correct for a POCO, an anonymous object,
    /// a primitive, or <c>null</c>. This is what fixes the double-encoding B3 exists for.</item>
    /// </list>
    /// A caller already holding pre-serialized JSON text who wants it embedded as a nested value
    /// (not as an escaped string) should use <see cref="FromJson"/> instead of either
    /// constructor — passing that text to the <c>string</c> constructor embeds it as a string,
    /// which is correct only if a JSON-text string genuinely is the intended payload.
    /// </summary>
    public class EventMessage : IEventMessage
    {
        public string EventName { get; }

#if NETFRAMEWORK
        public JToken PayloadValue { get; }
#else
        public JsonElement PayloadValue { get; }
#endif

        /// <summary>
        /// The argument is embedded as a JSON STRING value, verbatim — never parsed or sniffed,
        /// even if it looks like JSON. Use this only when the payload genuinely is text. For a
        /// structured payload, use <see cref="EventMessage(string, object)"/>; for pre-serialized
        /// JSON text that should be embedded as a nested value, use <see cref="FromJson"/>.
        /// </summary>
        public EventMessage(string eventName, string payload)
            : this(eventName, (object?)payload)
        {
        }

        /// <summary>
        /// <paramref name="payload"/> is serialized by its RUNTIME type and embedded as a nested
        /// JSON value — pass a POCO, an anonymous object, a primitive, or <c>null</c> directly.
        /// </summary>
        public EventMessage(string eventName, object? payload)
        {
            EventName = eventName;
            PayloadValue = PayloadCodec.ToPayloadValue(payload);
        }

        /// <summary>
        /// Internal constructor used by the receive loops to pass an already-parsed
        /// <see cref="MessageEnvelope.PayloadValue"/> straight through without re-parsing or
        /// re-serializing it.
        /// </summary>
#if NETFRAMEWORK
        internal EventMessage(string eventName, JToken payloadValue)
        {
            EventName = eventName;
            PayloadValue = payloadValue;
        }
#else
        internal EventMessage(string eventName, JsonElement payloadValue)
        {
            EventName = eventName;
            PayloadValue = payloadValue;
        }
#endif

        /// <summary>
        /// Parses <paramref name="json"/> and embeds it as a nested JSON value — the route for a
        /// caller already holding pre-serialized JSON text who wants the structured shape rather
        /// than an escaped string. Throws (a <see cref="FormatException"/> or the active
        /// serializer's own JSON exception type) if <paramref name="json"/> is not valid JSON;
        /// this is a caller-driven, synchronous conversion, analogous to <c>int.Parse</c> — not a
        /// receive-loop path, so there is no recoverable/fatal distinction to make here.
        /// </summary>
        public static EventMessage FromJson(string eventName, string json)
        {
#if NETFRAMEWORK
            return new EventMessage(eventName, JToken.Parse(json));
#else
            using var document = JsonDocument.Parse(json);
            return new EventMessage(eventName, document.RootElement.Clone());
#endif
        }

        /// <summary>Deserializes <see cref="PayloadValue"/> as <typeparamref name="T"/>. See
        /// <see cref="MessageEnvelope.GetPayload{T}"/> for the exact contract.</summary>
        public T? GetPayload<T>() => PayloadCodec.GetPayload<T>(PayloadValue);

        /// <summary>Non-throwing form of <see cref="GetPayload{T}"/>.</summary>
        public bool TryGetPayload<T>(out T? value)
        {
            try
            {
                value = GetPayload<T>();
                return true;
            }
            catch (Exception ex) when (ReceiveLoopErrors.IsRecoverableParseFailure(ex))
            {
                value = default;
                return false;
            }
        }

        /// <summary>Never throws. Returns the payload's string content if it is a JSON string,
        /// otherwise the raw JSON text of whatever value is present.</summary>
        public string GetPayloadAsString() => PayloadCodec.GetPayloadAsString(PayloadValue);
    }
}
