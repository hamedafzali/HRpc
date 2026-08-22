using System;
using HRpc.Utils;

#if NETFRAMEWORK
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace HRpc.Models
{
    /// <summary>
    /// Raised when a received envelope declares a protocol version outside the range this build
    /// accepts (see <see cref="MessageEnvelope.CurrentProtocolVersion"/> and
    /// <see cref="MessageEnvelope.MinimumSupportedVersion"/>). A version outside that range means
    /// the envelope schema itself may differ from what this build expects, so the receive loops
    /// treat this the same way they treat a framing violation
    /// (<see cref="HRpc.Utils.LineTooLongException"/>): the offending message is not
    /// skipped, the connection is dropped.
    /// </summary>
    public sealed class UnsupportedProtocolVersionException : Exception
    {
        public int ReceivedVersion { get; }

        public UnsupportedProtocolVersionException(int receivedVersion)
            : base(BuildMessage(receivedVersion))
        {
            ReceivedVersion = receivedVersion;
        }

        private static string BuildMessage(int receivedVersion)
        {
            return receivedVersion > MessageEnvelope.CurrentProtocolVersion
                ? $"Received message envelope with protocol version {receivedVersion}, which is newer than the highest version ({MessageEnvelope.CurrentProtocolVersion}) supported by this build."
                : $"Received message envelope with protocol version {receivedVersion}, which is older than the lowest version ({MessageEnvelope.MinimumSupportedVersion}) supported by this build.";
        }
    }

    public class MessageEnvelope
    {
        /// <summary>
        /// Highest wire protocol version this build EMITS for a brand new outgoing envelope —
        /// stamped by the <see cref="MessageEnvelope(string, object)"/> constructor, not by
        /// <see cref="Serialize"/> (which does not mutate or override <see cref="Version"/>; see
        /// its doc comment). Bump this constant — and only this constant — when the envelope
        /// schema changes in a way that isn't purely additive. See PROTOCOL.md for the full
        /// version-negotiation contract.
        ///
        /// Bumped to 2 in v1.2.0 (B3): the "payload" field's JSON shape changed from an
        /// always-a-string value (v1) to whatever <see cref="PayloadValue"/> actually holds,
        /// which may now be a nested object/array/number. That is not a purely additive change —
        /// a v1 receiver reading a v2 object payload would hand its caller a raw JSON object
        /// where a string was always guaranteed before — so it earns a real version bump rather
        /// than silently changing what an unversioned "payload" field means.
        /// </summary>
        public const int CurrentProtocolVersion = 2;

        /// <summary>
        /// The version assumed for a received envelope whose "v" field is ABSENT. This identifies
        /// one specific historical wire shape — every HRpc release before v1.2.0, none of which
        /// ever emitted a "v" field — and must NEVER change, no matter how high
        /// <see cref="CurrentProtocolVersion"/> climbs. It is not "the oldest version we still
        /// support"; see <see cref="MinimumSupportedVersion"/> for that. This is also the
        /// property initializer's default: it's what a plain <c>new MessageEnvelope()</c> (as used
        /// by the deserializer, which sets <see cref="Version"/> from JSON only when present) is
        /// left holding when "v" is absent. Code constructing a NEW outgoing envelope must use the
        /// <see cref="MessageEnvelope(string, object)"/> constructor instead, which stamps
        /// <see cref="CurrentProtocolVersion"/> explicitly. Also identifies the v1 payload shape:
        /// when <see cref="Version"/> resolves to this value, "payload" is guaranteed to be a JSON
        /// string on the wire (see <see cref="PayloadValue"/>).
        /// </summary>
        public const int LegacyProtocolVersion = 1;

        /// <summary>
        /// The oldest protocol version this build still accepts on receive. An envelope below
        /// this is rejected the same way one above <see cref="CurrentProtocolVersion"/> is: this
        /// build no longer guarantees it understands that version's framing.
        /// </summary>
        public const int MinimumSupportedVersion = 1;

#if NETFRAMEWORK
        [JsonProperty("v")]
#else
        [JsonPropertyName("v")]
#endif
        public int Version { get; set; } = LegacyProtocolVersion;

#if NETFRAMEWORK
        [JsonProperty("eventName")]
#else
        [JsonPropertyName("eventName")]
#endif
        public string EventName { get; set; } = string.Empty;

        /// <summary>
        /// The payload, stored as a deferred-parse JSON value (<c>JsonElement</c> on
        /// net8.0/net9.0, <c>JToken</c> on net48) rather than a flat string. This is what B3
        /// fixes: previously a caller holding an object had to pre-serialize it into a string,
        /// and <see cref="Serialize"/> would then JSON-escape that string a second time inside
        /// the outer envelope (<c>"payload":"{\"a\":1}"</c> instead of <c>"payload":{"a":1}</c>)
        /// — double encoding, inflated size, and no typed access on receive. Prefer
        /// <see cref="GetPayload{T}"/>, <see cref="TryGetPayload{T}"/>, or
        /// <see cref="GetPayloadAsString"/> over touching this directly.
        /// </summary>
#if NETFRAMEWORK
        [JsonProperty("payload")]
        public JToken PayloadValue { get; set; } = PayloadCodec.NullValue;
#else
        [JsonPropertyName("payload")]
        public JsonElement PayloadValue { get; set; } = PayloadCodec.NullValue;
#endif

        /// <summary>
        /// Deserializes <see cref="PayloadValue"/> as <typeparamref name="T"/>. When
        /// <typeparamref name="T"/> is <see cref="string"/> and the payload is a JSON string, this
        /// returns the string content directly rather than round-tripping through the general
        /// deserializer. Throws on a genuine mismatch (e.g. requesting a POCO when the payload is
        /// a JSON array) — the same exception types the receive loops classify as RECOVERABLE
        /// per the Item 2 taxonomy (<see cref="FormatException"/>, or the active serializer's own
        /// JSON exception type; see <see cref="ReceiveLoopErrors.IsRecoverableParseFailure"/>).
        /// Unlike a receive-loop parse failure, though, this call happens lazily, on whatever
        /// thread the caller invokes it from — outside the read loop entirely — so HRpc cannot
        /// raise <c>ErrorOccurred</c> or skip-and-continue on the caller's behalf here. Use
        /// <see cref="TryGetPayload{T}"/> for a non-throwing result instead of a try/catch.
        /// </summary>
        public T? GetPayload<T>() => PayloadCodec.GetPayload<T>(PayloadValue);

        /// <summary>
        /// Non-throwing form of <see cref="GetPayload{T}"/>. Returns <c>false</c> (with
        /// <paramref name="value"/> left at <c>default</c>) for exactly the exception shapes
        /// <see cref="GetPayload{T}"/> throws on a genuine parse/shape mismatch; any other
        /// exception (a bug, not a data problem) still propagates.
        /// </summary>
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

        /// <summary>
        /// Convenience path preserving pre-B3 raw-string behavior: returns the payload's string
        /// content when it is a JSON string, or the raw JSON text of whatever value it is
        /// otherwise (object, array, number, bool, null). Never throws. This is also what the
        /// obsolete <see cref="Payload"/> property's getter delegates to, and what the receive
        /// loops use internally to bridge a received envelope to the string-based
        /// <c>IEventMessage</c>/<c>EventMessage</c> surface without risking a lazily-thrown parse
        /// exception turning into an unwanted disconnect.
        /// </summary>
        public string GetPayloadAsString() => PayloadCodec.GetPayloadAsString(PayloadValue);

        /// <summary>
        /// Pre-B3 string-typed payload accessor, kept for source compatibility. The getter never
        /// throws (see <see cref="GetPayloadAsString"/>); the setter wraps the given string as a
        /// JSON string value, byte-identical to pre-B3 behavior for a plain string payload.
        /// Prefer <see cref="PayloadValue"/> with <see cref="GetPayload{T}"/> for typed access.
        /// </summary>
        [Obsolete("Use PayloadValue with GetPayload<T>()/TryGetPayload<T>() (or GetPayloadAsString() for the old raw-string behavior) instead. See PROTOCOL.md.")]
#if NETFRAMEWORK
        [JsonIgnore]
#else
        [JsonIgnore]
#endif
        public string Payload
        {
            get => GetPayloadAsString();
            set => PayloadValue = PayloadCodec.ToPayloadValue(value);
        }


        /// <summary>
        /// Correlation id for a future request/response layer (v1.3.0+). RESERVED: v1.2.0 does not
        /// generate, interpret, or match on this field in any way — it exists purely so the wire
        /// format doesn't need another breaking change when request/response semantics are added.
        /// Absent on ordinary fire-and-forget events, and never auto-generated. Omitted from the
        /// wire entirely when null, so a 1.2.0 fire-and-forget message stays exactly as small as a
        /// 1.1.x one.
        /// </summary>
#if NETFRAMEWORK
        [JsonProperty("cid", NullValueHandling = NullValueHandling.Ignore)]
#else
        [JsonPropertyName("cid")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#endif
        public string? Cid { get; set; }

        /// <summary>
        /// Message-type discriminator for a future request/response layer (v1.3.0+). RESERVED:
        /// v1.2.0 defines the known values below as documentation/convenience constants only — it
        /// does not validate, dispatch on, or otherwise interpret this field. An absent or
        /// unrecognized value is never fatal and is never rejected by <see cref="Deserialize"/>;
        /// what (if anything) a future version does with an unrecognized value is deferred to
        /// whichever version implements request/response. Omitted from the wire entirely when
        /// null.
        /// </summary>
#if NETFRAMEWORK
        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
#else
        [JsonPropertyName("type")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#endif
        public string? Type { get; set; }

        /// <summary>Known <see cref="Type"/> value for an ordinary fire-and-forget event.</summary>
        public const string MessageTypeEvent = "event";

        /// <summary>Known <see cref="Type"/> value for a future request (RESERVED, v1.3.0+).</summary>
        public const string MessageTypeRequest = "request";

        /// <summary>Known <see cref="Type"/> value for a future response (RESERVED, v1.3.0+).</summary>
        public const string MessageTypeResponse = "response";

        /// <summary>Known <see cref="Type"/> value for a future error response (RESERVED, v1.3.0+).</summary>
        public const string MessageTypeErrorResponse = "errorResponse";

        /// <summary>
        /// Error payload for a future error-response layer (v1.3.0+). RESERVED: fields only, no
        /// propagation or interpretation logic exists in v1.2.0. Omitted from the wire entirely
        /// when null.
        /// </summary>
#if NETFRAMEWORK
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
#else
        [JsonPropertyName("error")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
#endif
        public MessageError? Error { get; set; }

        /// <summary>
        /// Used by the JSON deserializer, and by anything that needs to build up an envelope
        /// field-by-field. <see cref="Version"/> is left at <see cref="LegacyProtocolVersion"/>
        /// until something else sets it (the deserializer, if "v" was present on the wire; or the
        /// caller, explicitly). Prefer <see cref="MessageEnvelope(string, object)"/> for a brand
        /// new outgoing envelope.
        /// </summary>
        public MessageEnvelope()
        {
        }

        /// <summary>
        /// Constructs a brand new outgoing envelope, stamped with
        /// <see cref="CurrentProtocolVersion"/>. <paramref name="payload"/> is serialized by its
        /// RUNTIME type into <see cref="PayloadValue"/> as typed JSON — pass a POCO, an anonymous
        /// object, a primitive, or <c>null</c> directly; do not pre-serialize it into a string
        /// yourself, or it will round-trip as a JSON string containing that text rather than as a
        /// nested value (which may be exactly what you want if you're intentionally sending a
        /// string payload — see <see cref="GetPayloadAsString"/> on the receiving end).
        /// </summary>
        public MessageEnvelope(string eventName, object? payload)
        {
            EventName = eventName;
            PayloadValue = PayloadCodec.ToPayloadValue(payload);
            Version = CurrentProtocolVersion;
        }

        /// <summary>
        /// Pre-B3 constructor, kept for source compatibility. Behaves identically to
        /// <see cref="MessageEnvelope(string, object)"/> called with a string payload — the
        /// string becomes the JSON string value of "payload", byte-identical to pre-B3 output.
        /// </summary>
        [Obsolete("Use MessageEnvelope(string eventName, object? payload) so the payload is written as typed JSON instead of a re-escaped string when it isn't already one. See PROTOCOL.md.")]
        public MessageEnvelope(string eventName, string payload)
            : this(eventName, (object?)payload)
        {
        }

        /// <summary>
        /// Serializes this envelope as-is. Does NOT mutate this instance, and does NOT stamp
        /// <see cref="CurrentProtocolVersion"/> — whatever <see cref="Version"/> currently holds
        /// is what gets written to the wire, and calling this twice on the same instance produces
        /// identical output both times. This matters for any future receive-then-forward path (a
        /// relay, a broadcast, re-serializing for diagnostics): forwarding a received envelope
        /// unchanged must preserve its original version, not silently relabel it as whatever this
        /// build currently emits. Construct via <see cref="MessageEnvelope(string, object)"/> to
        /// get a new outgoing envelope correctly stamped with the current version in the first
        /// place.
        /// </summary>
        public string Serialize()
        {
#if NETFRAMEWORK
            return JsonConvert.SerializeObject(this);
#else
            return JsonSerializer.Serialize(this);
#endif
        }

        public static MessageEnvelope Deserialize(string json)
        {
#if NETFRAMEWORK
            var envelope = JsonConvert.DeserializeObject<MessageEnvelope>(json);
#else
            var envelope = JsonSerializer.Deserialize<MessageEnvelope>(json);
#endif
            if (envelope == null)
            {
                throw new FormatException("Invalid message envelope payload.");
            }

            if (envelope.Version > CurrentProtocolVersion || envelope.Version < MinimumSupportedVersion)
            {
                throw new UnsupportedProtocolVersionException(envelope.Version);
            }

            return envelope;
        }
    }

    /// <summary>
    /// Error payload for a future error-response layer (v1.3.0+). RESERVED: this type exists only
    /// to give <see cref="MessageEnvelope.Error"/> a wire shape now, so v1.3.0 doesn't need another
    /// breaking format change. No HRpc code in v1.2.0 constructs, propagates, or interprets one.
    /// </summary>
    public class MessageError
    {
#if NETFRAMEWORK
        [JsonProperty("code")]
#else
        [JsonPropertyName("code")]
#endif
        public string Code { get; set; } = string.Empty;

#if NETFRAMEWORK
        [JsonProperty("message")]
#else
        [JsonPropertyName("message")]
#endif
        public string Message { get; set; } = string.Empty;
    }
}
