#if NETFRAMEWORK
using Newtonsoft.Json;
#else
using System.Text.Json;
using System.Text.Json.Serialization;
#endif

namespace TcpEventFramework.Models
{
    internal class MessageEnvelope
    {
        #if NETFRAMEWORK
        [JsonProperty("eventName")]
#else
        [JsonPropertyName("eventName")]
#endif
        public string EventName { get; set; } = string.Empty;
#if NETFRAMEWORK
        [JsonProperty("payload")]
#else
        [JsonPropertyName("payload")]
#endif
        public string Payload { get; set; } = string.Empty;

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
    return JsonConvert.DeserializeObject<MessageEnvelope>(json)!;
#else
    return JsonSerializer.Deserialize<MessageEnvelope>(json)!;
#endif
}

    }
}
