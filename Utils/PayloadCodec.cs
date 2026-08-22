using System;

#if NETFRAMEWORK
using Newtonsoft.Json.Linq;
#else
using System.Text.Json;
#endif

namespace HRpc.Utils
{
    /// <summary>
    /// The typed-payload conversion logic shared by <see cref="HRpc.Models.MessageEnvelope"/>
    /// (the wire envelope) and <see cref="HRpc.Models.EventMessage"/> (the public
    /// pub/sub surface) — both store a payload as a deferred-parse JSON value
    /// (<c>JsonElement</c> on net8.0/net9.0, <c>JToken</c> on net48) and expose the identical
    /// <c>GetPayload&lt;T&gt;</c>/<c>TryGetPayload&lt;T&gt;</c>/<c>GetPayloadAsString</c> triad
    /// over it. Extracted here so that triad is defined exactly once instead of twice.
    /// </summary>
    internal static class PayloadCodec
    {
#if NETFRAMEWORK
        public static JToken NullValue => JValue.CreateNull();

        public static JToken ToPayloadValue(object? payload)
        {
            if (payload == null)
            {
                return JValue.CreateNull();
            }

            return payload is JToken token ? token : JToken.FromObject(payload);
        }

        public static T? GetPayload<T>(JToken payloadValue)
        {
            if (typeof(T) == typeof(string) && payloadValue.Type == JTokenType.String)
            {
                return (T)(object)(payloadValue.Value<string>() ?? string.Empty);
            }

            try
            {
                return payloadValue.ToObject<T>();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
            {
                // JToken.ToObject<T> falls through to Convert.ChangeType for primitive-type
                // coercion (e.g. a JSON string payload requested as int), which throws
                // FormatException/OverflowException/InvalidCastException directly instead of
                // Newtonsoft.Json.JsonException. System.Text.Json's JsonElement.Deserialize<T>
                // always throws JsonException for the equivalent shape mismatch. Normalize so
                // GetPayload<T> throws one exception type regardless of which serializer backs
                // the current target framework.
                throw new Newtonsoft.Json.JsonException(
                    $"Could not convert payload to {typeof(T)}.", ex);
            }
        }

        public static string GetPayloadAsString(JToken payloadValue)
        {
            return payloadValue.Type == JTokenType.String
                ? payloadValue.Value<string>() ?? string.Empty
                : payloadValue.ToString(Newtonsoft.Json.Formatting.None);
        }
#else
        public static JsonElement NullValue => JsonDocument.Parse("null").RootElement;

        public static JsonElement ToPayloadValue(object? payload)
        {
            if (payload == null)
            {
                return JsonDocument.Parse("null").RootElement;
            }

            if (payload is JsonElement element)
            {
                return element;
            }

            // Serialize by the value's RUNTIME type, not the "object" compile-time type — the
            // generic JsonSerializer.SerializeToElement<object>(payload) overload would use the
            // declared type and silently emit "{}" for any POCO passed in as object.
            return JsonSerializer.SerializeToElement(payload, payload.GetType());
        }

        public static T? GetPayload<T>(JsonElement payloadValue)
        {
            if (typeof(T) == typeof(string) && payloadValue.ValueKind == JsonValueKind.String)
            {
                return (T)(object)(payloadValue.GetString() ?? string.Empty);
            }

            return payloadValue.Deserialize<T>();
        }

        public static string GetPayloadAsString(JsonElement payloadValue)
        {
            return payloadValue.ValueKind == JsonValueKind.String
                ? payloadValue.GetString() ?? string.Empty
                : payloadValue.GetRawText();
        }
#endif
    }
}
