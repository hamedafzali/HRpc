using System;

namespace HRpc.Utils
{
    /// <summary>
    /// Classifies which exceptions from <c>MessageEnvelope.Deserialize</c> are recoverable
    /// (the line boundary is intact, so the offending message can be skipped and reading
    /// resumed) versus everything else, which must propagate to the caller's outer handler and
    /// drop the connection. Only a genuine parse failure — malformed JSON, or the envelope
    /// deserializing to <c>null</c> — is recoverable. This deliberately excludes
    /// <c>UnsupportedProtocolVersionException</c> (the schema itself may differ) and anything
    /// not a parse failure at all (an <see cref="OperationCanceledException"/> from a cancelled
    /// read, an <see cref="OutOfMemoryException"/>, or a bug inside HRpc's own deserialization
    /// path) — those must not be silently attributed to "the peer sent a bad message."
    /// </summary>
    internal static class ReceiveLoopErrors
    {
        public static bool IsRecoverableParseFailure(Exception ex)
        {
            if (ex is FormatException)
            {
                return true;
            }

#if NETFRAMEWORK
            return ex is Newtonsoft.Json.JsonException;
#else
            return ex is System.Text.Json.JsonException;
#endif
        }
    }
}
