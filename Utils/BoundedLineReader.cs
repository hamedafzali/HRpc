using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HRpc.Utils
{
    /// <summary>
    /// Raised when a single newline-delimited message exceeds the configured maximum size.
    /// </summary>
    public sealed class LineTooLongException : Exception
    {
        public LineTooLongException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Default size limits for newline-delimited messages.
    /// </summary>
    public static class MessageSizeLimits
    {
        /// <summary>
        /// Default cap on a single message, in UTF-8 encoded bytes, excluding the trailing
        /// newline delimiter. 1 MB sits comfortably above typical event payloads while still
        /// bounding worst-case per-connection memory use against a misbehaving or malicious peer.
        /// </summary>
        public const int DefaultMaxMessageSizeBytes = 1024 * 1024; // 1 MB
    }

    /// <summary>
    /// Reads UTF-8, '\n'-delimited lines from a stream with a hard cap on line size in bytes.
    /// Unlike <see cref="StreamReader.ReadLineAsync()"/>, which must allocate the full line before
    /// it can be inspected, this reader enforces the limit incrementally as bytes arrive: the
    /// accumulated size is checked before each chunk is appended, so a line from a misbehaving or
    /// malicious peer is never fully buffered before rejection.
    /// </summary>
    internal sealed class BoundedLineReader
    {
        private const int DefaultChunkSize = 8192;

        private readonly Stream _stream;
        private readonly int _maxLineBytes;
        private readonly byte[] _readBuffer;
        private int _bufferStart;
        private int _bufferLength;

        public BoundedLineReader(Stream stream, int maxLineBytes, int chunkSize = DefaultChunkSize)
        {
            if (maxLineBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLineBytes), "Maximum line size must be greater than zero.");
            }

            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _maxLineBytes = maxLineBytes;
            _readBuffer = new byte[Math.Max(chunkSize, 64)];
        }

        /// <summary>
        /// Reads the next '\n'-terminated line (an optional trailing '\r' is trimmed). Returns
        /// null at end of stream. Throws <see cref="LineTooLongException"/> as soon as the bytes
        /// accumulated for the current line exceed the configured maximum, without waiting for a
        /// terminator.
        /// </summary>
        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using var line = new MemoryStream();

            while (true)
            {
                if (_bufferStart >= _bufferLength)
                {
                    _bufferLength = await _stream.ReadAsync(_readBuffer, 0, _readBuffer.Length)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false);
                    _bufferStart = 0;

                    if (_bufferLength == 0)
                    {
                        // End of stream. A non-empty partial line with no trailing newline is
                        // still returned, matching StreamReader.ReadLineAsync's behavior.
                        return line.Length == 0 ? null : DecodeAndTrim(line);
                    }
                }

                var idx = Array.IndexOf(_readBuffer, (byte)'\n', _bufferStart, _bufferLength - _bufferStart);
                var available = (idx >= 0 ? idx : _bufferLength) - _bufferStart;

                if (line.Length + available > _maxLineBytes)
                {
                    throw new LineTooLongException(
                        $"Message exceeds the maximum allowed size of {_maxLineBytes} bytes.");
                }

                line.Write(_readBuffer, _bufferStart, available);

                if (idx >= 0)
                {
                    _bufferStart = idx + 1;
                    return DecodeAndTrim(line);
                }

                _bufferStart = _bufferLength;
            }
        }

        private static string DecodeAndTrim(MemoryStream line)
        {
            var length = (int)line.Length;
            var bytes = line.GetBuffer();

            if (length > 0 && bytes[length - 1] == (byte)'\r')
            {
                length--;
            }

            return Encoding.UTF8.GetString(bytes, 0, length);
        }
    }
}
