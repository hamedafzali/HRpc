using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

#if NETFRAMEWORK
using Newtonsoft.Json;
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

            T? result;
            try
            {
                // Newtonsoft.Json's default contract resolver already matches JSON property
                // names case-insensitively, so no explicit option is needed here for the
                // camelCase-JSON-into-PascalCase-type case that the System.Text.Json path below
                // needs PropertyNameCaseInsensitive for.
                result = payloadValue.ToObject<T>();
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
                throw new JsonException(
                    $"Could not convert payload to {typeof(T)}.", ex);
            }

            EnsureShapeMatches<T>(payloadValue);
            return result;
        }

        /// <summary>
        /// Neither serializer throws when a JSON object shares no property names at all with
        /// <typeparamref name="T"/> -- both silently bind every unmatched constructor
        /// parameter/property to its type's default value instead (unless T happens to use the
        /// `required` modifier, which a caller-supplied T is never guaranteed to). That breaks
        /// GetPayload&lt;T&gt;'s documented "throws on shape mismatch" contract, analogous to
        /// int.Parse: a payload that doesn't actually describe a T should fail loudly, not
        /// return a T with every member zeroed. Detect that specific case -- a JSON object none
        /// of whose property names case-insensitively match any public property of T -- and
        /// throw. A partial match (some but not all properties present) is left alone: that's a
        /// legitimate, if incomplete, T and not what this guards against.
        /// </summary>
        private static void EnsureShapeMatches<T>(JToken payloadValue)
        {
            if (payloadValue.Type != JTokenType.Object)
            {
                return;
            }

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(typeof(T)) ||
                typeof(T).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
                // A dictionary-shaped T binds every JSON object key as an entry rather than
                // matching it against a public property, so the property-name heuristic below
                // doesn't apply -- Dictionary<TKey, TValue>'s own public properties (Count, Keys,
                // Values, Comparer) never match JSON payload keys even on a perfectly legitimate
                // payload, so the check would false-positive on every dictionary target.
                return;
            }

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (properties.Length == 0)
            {
                return;
            }

            var jsonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in (JObject)payloadValue)
            {
                jsonNames.Add(prop.Key);
            }

            foreach (var prop in properties)
            {
                if (jsonNames.Contains(prop.Name))
                {
                    return;
                }
            }

            throw new JsonException(
                $"Could not convert payload to {typeof(T)}: none of its public properties matched any property in the JSON payload.");
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

        // PropertyNameCaseInsensitive: System.Text.Json is case-sensitive by default, unlike
        // Newtonsoft.Json's default contract resolver (used on net48 below). Without this, a
        // wholly ordinary camelCase JSON payload -- e.g. from EventMessage.FromJson, which
        // exists specifically for "pre-serialized JSON text from another system" -- fails to
        // bind to a PascalCase C# type, and (see EnsureShapeMatches) would otherwise do so
        // silently rather than throwing.
        private static readonly JsonSerializerOptions DeserializeOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        };

        public static T? GetPayload<T>(JsonElement payloadValue)
        {
            if (typeof(T) == typeof(string) && payloadValue.ValueKind == JsonValueKind.String)
            {
                return (T)(object)(payloadValue.GetString() ?? string.Empty);
            }

            var result = payloadValue.Deserialize<T>(DeserializeOptions);
            EnsureShapeMatches<T>(payloadValue);
            return result;
        }

        /// <summary>
        /// See the net48 overload of the same name for the full rationale: System.Text.Json
        /// silently binds every unmatched constructor parameter/property to its type's default
        /// value instead of throwing when a JSON object shares no property names at all with
        /// <typeparamref name="T"/>. Detect and reject that specific case.
        /// </summary>
        private static void EnsureShapeMatches<T>(JsonElement payloadValue)
        {
            if (payloadValue.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(typeof(T)) ||
                typeof(T).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>)))
            {
                // See the net48 overload of the same name: dictionary-shaped T binds every JSON
                // object key as an entry rather than matching it against a public property, so
                // the heuristic below would false-positive on every legitimate dictionary payload.
                return;
            }

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (properties.Length == 0)
            {
                return;
            }

            var jsonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in payloadValue.EnumerateObject())
            {
                jsonNames.Add(prop.Name);
            }

            foreach (var prop in properties)
            {
                if (jsonNames.Contains(prop.Name))
                {
                    return;
                }
            }

            throw new JsonException(
                $"Could not convert payload to {typeof(T)}: none of its public properties matched any property in the JSON payload.");
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
